using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public const int GraphSeconds = 300;

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
            WriteGame(json, recorder.Game);
            WriteMemory(json, recorder.Game);

            if (input.Tab == PanelTab.Overview)
            {
                WriteGraph(json, recorder.Ring);
                WriteStrings(json, "verdict", Analysis.Verdict(recorder, seconds));
            }

            if (input.Tab == PanelTab.Game)
            {
                WriteTimeline(json, recorder.Game);
                WriteScenes(json, recorder.Game, recorder.HitchMs);
                WriteSpeeds(json, recorder.Game);
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

    /// <summary>Logic updates, physics, game speed and clock: overall and for the last whole second.</summary>
    private static void WriteGame(Utf8JsonWriter json, GameStats game)
    {
        var world = game.World;
        var last = game.LastSecond;

        json.WriteStartObject("game");
        json.WriteBoolean("inGame", world.InGame);
        json.WriteBoolean("seenGame", game.HasWorld);
        json.WriteBoolean("paused", world.Paused);
        Number(json, "fpsNow", last?.Fps ?? 0, 1);
        Number(json, "logic", game.LogicPerSecond, 1);
        Number(json, "logicNow", last?.LogicPerSecond ?? 0, 1);
        Number(json, "fixed", game.FixedPerSecond, 1);
        Number(json, "fixedNow", last?.FixedPerSecond ?? 0, 1);
        Number(json, "physicsRate", world.FixedDeltaSeconds > 0 ? 1 / world.FixedDeltaSeconds : 0, 1);
        Number(json, "timeScale", world.TimeScale, 3);
        Number(json, "speed", world.GameSpeed, 2);
        Number(json, "clockRate", game.ClockRate, 1);
        Number(json, "clockNow", last?.ClockRate ?? 0, 1);
        Number(json, "cappedPct", game.CappedPercent);
        json.WriteNumber("cappedFrames", game.CappedFrames);
        Number(json, "cappedMs", game.CappedMs, 0);
        Number(json, "maxDeltaMs", world.MaxDeltaSeconds * 1000, 0);
        Number(json, "inGameSeconds", game.InGameMs / 1000, 0);
        Number(json, "pausedSeconds", game.PausedMs / 1000, 0);
        json.WriteEndObject();
    }

    /// <summary>Each memory reading in MB: the newest, the peak and the growth per minute; 0 where it is not available.</summary>
    private static void WriteMemory(Utf8JsonWriter json, GameStats game)
    {
        json.WriteStartObject("memory");

        foreach (var (key, _, pick) in MemoryFields.All)
        {
            json.WriteStartObject(key);
            Number(json, "now", pick(game.LastMemory) / Analysis.MB, 1);
            Number(json, "peak", pick(game.PeakMemory) / Analysis.MB, 1);
            Number(json, "perMin", game.GrowthPerMinute(pick) / Analysis.MB, 2);
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    /// <summary>The newest seconds as parallel arrays, for the Game tab's graphs.</summary>
    private static void WriteTimeline(Utf8JsonWriter json, GameStats game)
    {
        var timeline = game.Timeline;
        var from = Math.Max(0, timeline.Count - GraphSeconds);

        json.WriteStartObject("timeline");
        SecondSeries(json, "fps", timeline, from, s => s.Fps);
        SecondSeries(json, "logic", timeline, from, s => s.LogicPerSecond);
        SecondSeries(json, "maxMs", timeline, from, s => s.MaxMs);
        SecondSeries(json, "speed", timeline, from, s => s.GameSpeed);
        SecondSeries(json, "gameHeap", timeline, from, s => s.Memory.GameHeap / Analysis.MB);
        SecondSeries(json, "unity", timeline, from, s => s.Memory.UnityAllocated / Analysis.MB);
        SecondSeries(json, "process", timeline, from, s => s.Memory.WorkingSet / Analysis.MB);
        json.WriteEndObject();
    }

    private static void SecondSeries(Utf8JsonWriter json, string name, IReadOnlyList<SecondSample> timeline, int from, Func<SecondSample, double> pick)
    {
        json.WriteStartArray(name);

        for (var i = from; i < timeline.Count; i++)
        {
            var value = pick(timeline[i]);

            json.WriteNumberValue(double.IsFinite(value) ? Math.Round(value, 1) : 0);
        }

        json.WriteEndArray();
    }

    private static void WriteScenes(Utf8JsonWriter json, GameStats game, double hitchMs)
    {
        json.WriteStartArray("scenes");

        foreach (var scene in game.Scenes)
        {
            json.WriteStartObject();
            json.WriteString("name", scene.Name);
            json.WriteNumber("frames", scene.Frames);
            Number(json, "seconds", scene.Ms / 1000, 0);
            Number(json, "fps", scene.Fps, 1);
            Number(json, "mean", scene.MeanMs);
            Number(json, "p50", scene.Percentile(0.50));
            Number(json, "p95", scene.Percentile(0.95));
            Number(json, "p99", scene.Percentile(0.99));
            Number(json, "max", scene.MaxMs);
            json.WriteNumber("hitches", scene.Hitches);
            Number(json, "over16", scene.Frames > 0 ? scene.Over16 * 100.0 / scene.Frames : 0, 1);
            Number(json, "over33", scene.Frames > 0 ? scene.Over33 * 100.0 / scene.Frames : 0, 1);
            Number(json, "logic", scene.LogicPerSecond, 1);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    /// <summary>Share of in-game time at each game speed, fastest last.</summary>
    private static void WriteSpeeds(Utf8JsonWriter json, GameStats game)
    {
        json.WriteStartArray("speeds");

        foreach (var (speed, ms) in game.SpeedMs.OrderBy(pair => pair.Key))
        {
            json.WriteStartObject();
            Number(json, "speed", speed);
            Number(json, "pct", game.InGameMs > 0 ? ms * 100 / game.InGameMs : 0, 1);
            json.WriteEndObject();
        }

        json.WriteEndArray();
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
            json.WriteNumber("hitches", row.CausedHitches);
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
