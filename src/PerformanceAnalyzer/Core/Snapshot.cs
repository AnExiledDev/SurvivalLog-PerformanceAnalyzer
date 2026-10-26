using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PerformanceAnalyzer.Core;

/// <summary>Everything one panel update needs that the recorder does not hold.</summary>
/// <param name="Now">Clock reading in recorder ticks.</param>
/// <param name="Tab">The tab the panel shows; the heavy lists are only sent for their own tab.</param>
/// <param name="Overlay">Whether the small top-right readout is on.</param>
/// <param name="Scripts">Scripts sent to the web UI by sender since the last reset.</param>
/// <param name="EngineTimings">Whether the engine reports its own frame timings.</param>
public sealed record SnapshotInput(long Now, PanelTab Tab, bool Overlay, InstrumentStatus Status, IReadOnlyList<ScriptRow> Scripts, bool EngineTimings, string Version);

/// <summary>The JSON the panel draws from, written by hand so it carries only what the current tab shows.</summary>
public static class Snapshot
{
    public const int GraphFrames = 300;
    public const int MethodLimit = 300;
    public const int HitchLimit = 60;

    public static string Json(Recorder recorder, SnapshotInput input) => Culture.Invariant(() => Write(recorder, input));

    private static string Write(Recorder recorder, SnapshotInput input)
    {
        using var stream = new MemoryStream();

        using (var json = new Utf8JsonWriter(stream))
        {
            var seconds = recorder.RecordedSeconds(input.Now);
            var spread = Analysis.FrameSpread(recorder);
            var split = Analysis.Split(recorder);

            json.WriteStartObject();
            json.WriteString("version", input.Version);
            json.WriteBoolean("recording", recorder.IsRecording);
            json.WriteBoolean("overlay", input.Overlay);
            json.WriteString("tab", Tabs.Key(input.Tab));
            Number(json, "seconds", seconds);
            json.WriteNumber("frames", recorder.FrameCount);
            json.WriteNumber("hitchCount", recorder.HitchCount);
            Number(json, "hitchMs", recorder.HitchMs);
            Number(json, "overheadUs", recorder.OverheadTicksPerCall / recorder.TicksPerMs * 1000);
            json.WriteBoolean("engineTimings", input.EngineTimings);
            WriteStatus(json, input.Status, recorder.Overflow);
            WriteSpread(json, "frame", spread);
            WriteSplit(json, split);
            WriteLatest(json, recorder, seconds);

            if (input.Tab == PanelTab.Overview)
            {
                WriteGraph(json, recorder.Ring);
                WriteStrings(json, "verdict", Analysis.Verdict(recorder, seconds));
            }

            if (input.Tab is PanelTab.Overview or PanelTab.Mods) WriteMods(json, Analysis.ModRows(recorder, seconds), input.Tab == PanelTab.Overview ? 6 : int.MaxValue);
            if (input.Tab == PanelTab.Methods) WriteMethods(json, Analysis.MethodRows(recorder, MethodLimit));
            if (input.Tab == PanelTab.Hitches) WriteHitches(json, recorder);
            if (input.Tab == PanelTab.Web) WriteScripts(json, input.Scripts, seconds);

            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The newest frame and the mod costing the most, for the overlay, which draws with the panel closed.</summary>
    private static void WriteLatest(Utf8JsonWriter json, Recorder recorder, double seconds)
    {
        var ring = recorder.Ring;

        Number(json, "lastMs", ring.Count > 0 ? ring[ring.Count - 1].Ms : 0);

        var top = Analysis.ModRows(recorder, seconds).Find(row => row.Id != Recorder.AnalyzerMod);

        json.WriteStartObject("topMod");
        json.WriteString("name", top?.Mod.Name ?? "");
        Number(json, "avg", top?.AvgMs ?? 0, 3);
        json.WriteEndObject();
    }

    private static void WriteStatus(Utf8JsonWriter json, InstrumentStatus status, int overflow)
    {
        json.WriteStartObject("status");
        json.WriteNumber("hooks", status.Hooks);
        json.WriteNumber("patches", status.Patches);
        json.WriteNumber("tickers", status.Tickers);
        json.WriteNumber("deepMethods", status.DeepMethods);
        json.WriteNumber("pending", status.Pending);
        json.WriteNumber("failed", status.Failed);
        json.WriteNumber("untimeable", status.Untimeable);
        json.WriteNumber("overflow", overflow);
        json.WriteString("deepMod", status.DeepMod);
        WriteStrings(json, "notes", status.Notes);
        json.WriteEndObject();
    }

    private static void WriteSpread(Utf8JsonWriter json, string name, Spread spread)
    {
        json.WriteStartObject(name);
        Number(json, "mean", spread.Mean);
        Number(json, "p50", spread.P50);
        Number(json, "p95", spread.P95);
        Number(json, "p99", spread.P99);
        Number(json, "max", spread.Max);
        json.WriteNumber("count", spread.Count);
        json.WriteEndObject();
    }

    private static void WriteSplit(Utf8JsonWriter json, SplitAverages split)
    {
        json.WriteStartObject("split");
        Number(json, "frame", split.FrameMs);
        Number(json, "mods", split.ModsMs);
        Number(json, "hook", split.HookMs);
        Number(json, "game", split.GameMs);
        Number(json, "analyzer", split.AnalyzerMs);
        Number(json, "rest", split.RestMs);
        json.WriteEndObject();
    }

    /// <summary>The newest frames as parallel arrays: total, mods, hooks, game in hooks.</summary>
    private static void WriteGraph(Utf8JsonWriter json, FrameRing ring)
    {
        var from = Math.Max(0, ring.Count - GraphFrames);

        json.WriteStartObject("graph");
        Series(json, "ms", ring, from, f => f.Ms);
        Series(json, "mods", ring, from, f => f.ModsMs);
        Series(json, "hook", ring, from, f => f.HookMs);
        Series(json, "game", ring, from, f => f.GameMs);
        Series(json, "gc", ring, from, f => f.ManagedGcs + f.GameGcs);
        json.WriteEndObject();
    }

    private static void Series(Utf8JsonWriter json, string name, FrameRing ring, int from, Func<FrameSample, float> pick)
    {
        json.WriteStartArray(name);

        for (var i = from; i < ring.Count; i++) json.WriteNumberValue(Math.Round(pick(ring[i]), 2));

        json.WriteEndArray();
    }

    private static void WriteMods(Utf8JsonWriter json, List<ModRow> rows, int limit)
    {
        json.WriteStartArray("mods");

        foreach (var row in rows)
        {
            if (limit-- <= 0) break;

            json.WriteStartObject();
            json.WriteString("key", row.Mod.Key);
            json.WriteString("name", row.Mod.Name);
            json.WriteString("version", row.Mod.Version);
            Number(json, "avg", row.AvgMs, 3);
            Number(json, "p99", row.P99Ms, 3);
            Number(json, "worst", row.WorstMs);
            Number(json, "share", row.SharePercent);
            Number(json, "allocKbS", row.AllocKbPerSecond);
            json.WriteNumber("hitches", row.HitchFrames);
            json.WriteNumber("probes", row.Probes);
            json.WriteNumber("calls", row.Calls);
            json.WriteBoolean("self", row.Id == Recorder.AnalyzerMod);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteMethods(Utf8JsonWriter json, List<MethodRow> rows)
    {
        json.WriteStartArray("methods");

        foreach (var row in rows)
        {
            json.WriteStartObject();
            json.WriteString("name", row.Probe.Name);
            json.WriteString("kind", ProbeKinds.Label(row.Probe.Kind));
            json.WriteString("target", row.Probe.Target);
            json.WriteString("owners", row.OwnerNames);
            json.WriteNumber("calls", row.Calls);
            Number(json, "perFrame", row.CallsPerFrame);
            Number(json, "selfPerFrame", row.SelfMsPerFrame, 4);
            Number(json, "self", row.SelfMs);
            Number(json, "total", row.TotalMs);
            Number(json, "maxCall", row.MaxCallMs, 3);
            Number(json, "worstFrame", row.WorstFrameMs, 3);
            Number(json, "allocKb", row.AllocKb);
            json.WriteNumber("offThread", row.OffThreadCalls);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteHitches(Utf8JsonWriter json, Recorder recorder)
    {
        var hitches = recorder.Hitches;

        json.WriteStartArray("hitches");

        for (var i = hitches.Count - 1; i >= 0 && i >= hitches.Count - HitchLimit; i--)
        {
            var hitch = hitches[i];
            var sample = hitch.Sample;

            json.WriteStartObject();
            json.WriteNumber("n", hitch.Number);
            Number(json, "at", hitch.AtSeconds);
            Number(json, "ms", sample.Ms);
            Number(json, "mods", sample.ModsMs);
            Number(json, "hook", sample.HookMs);
            Number(json, "game", sample.GameMs);
            Number(json, "rest", sample.RestMs);
            json.WriteNumber("gcManaged", sample.ManagedGcs);
            json.WriteNumber("gcGame", sample.GameGcs);
            WriteEngine(json, sample.Engine);
            json.WriteString("scene", hitch.Scene);
            json.WriteString("cause", Analysis.Cause(recorder, hitch));
            json.WriteStartArray("top");

            foreach (var entry in hitch.TopProbes)
            {
                var probe = recorder.Probes[entry.Id];

                json.WriteStartObject();
                json.WriteString("name", probe.Name);
                json.WriteString("kind", ProbeKinds.Label(probe.Kind));
                json.WriteString("owner", recorder.Mods[probe.Owners[0]].Name);
                Number(json, "ms", entry.Ms, 3);
                json.WriteNumber("calls", entry.Calls);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("mods");

            foreach (var entry in hitch.TopMods)
            {
                json.WriteStartObject();
                json.WriteString("name", recorder.Mods[entry.Id].Name);
                Number(json, "ms", entry.Ms, 3);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteEngine(Utf8JsonWriter json, EngineTimes engine)
    {
        if (engine.MainThreadMs <= 0 && engine.RenderThreadMs <= 0 && engine.GpuMs <= 0) return;

        json.WriteStartObject("engine");
        Number(json, "main", engine.MainThreadMs);
        Number(json, "render", engine.RenderThreadMs);
        Number(json, "gpu", engine.GpuMs);
        json.WriteEndObject();
    }

    private static void WriteScripts(Utf8JsonWriter json, IReadOnlyList<ScriptRow> rows, double seconds)
    {
        json.WriteStartArray("scripts");

        foreach (var row in rows)
        {
            json.WriteStartObject();
            json.WriteString("owner", row.Owner);
            json.WriteNumber("calls", row.Calls);
            json.WriteNumber("chars", row.Characters);
            json.WriteNumber("largest", row.Largest);
            Number(json, "perSecond", seconds > 0 ? row.Calls / seconds : 0);
            Number(json, "kbPerSecond", seconds > 0 ? row.Characters / 1024.0 / seconds : 0);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);

        foreach (var value in values) json.WriteStringValue(value);

        json.WriteEndArray();
    }

    /// <summary>Rounded so the page gets short numbers; NaN and infinity become 0 since JSON has neither.</summary>
    private static void Number(Utf8JsonWriter json, string name, double value, int digits = 2) =>
        json.WriteNumber(name, double.IsFinite(value) ? Math.Round(value, digits) : 0);
}
