using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PerformanceAnalyzer.Core;

/// <summary>Everything a report names that the recorder does not hold.</summary>
/// <param name="WebJson">The web probe's latest summary, or "" when it never sent one.</param>
/// <param name="System">One line on the machine: OS, processor, memory, graphics card.</param>
public sealed record ReportInput(long Now, DateTime At, string Version, string GameVersion, string System, InstrumentStatus Status, IReadOnlyList<ScriptRow> Scripts, string WebJson, IReadOnlyList<ModInfo> Loaded);

/// <summary>
/// The plain-text report a player pastes into a bug report or a modder reads first. ASCII only, fixed-width columns,
/// the verdict before the tables.
/// </summary>
public static class Report
{
    private const int MethodLines = 40;
    private const int HitchLines = 15;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Text(Recorder recorder, ReportInput input) => Culture.Invariant(() => Write(recorder, input));

    /// <summary>One line per frame in the window, for a spreadsheet.</summary>
    public static string FramesCsv(Recorder recorder) => Culture.Invariant(() => WriteCsv(recorder));

    /// <summary>One line per second of the recording (up to the last hour): frame rate, logic, game speed and memory.</summary>
    public static string SecondsCsv(Recorder recorder) => Culture.Invariant(() => WriteSecondsCsv(recorder.Game));

    private static string Write(Recorder recorder, ReportInput input)
    {
        var text = new StringBuilder();
        var seconds = recorder.RecordedSeconds(input.Now);
        var spread = Analysis.FrameSpread(recorder);
        var split = Analysis.Split(recorder);

        Line(text, "Survival Log performance report");
        Line(text, $"Performance Analyzer {input.Version}, game {input.GameVersion}, {input.At.ToString("yyyy-MM-dd HH:mm", Invariant)}");
        if (input.System.Length > 0) Line(text, input.System);
        Line(text, $"Recorded {seconds:0} s, {recorder.FrameCount} frames, last {spread.Count} frames below");
        Line(text);

        Heading(text, "Verdict");
        foreach (var line in Analysis.Verdict(recorder, seconds)) Line(text, "- " + line);
        Line(text);

        Heading(text, "Frame time (ms)");
        Line(text, $"mean {spread.Mean:0.00}   p50 {spread.P50:0.00}   p95 {spread.P95:0.00}   p99 {spread.P99:0.00}   max {spread.Max:0.00}");
        Line(text, $"per frame: mods {split.ModsMs:0.000}   hooks {split.HookMs:0.000}   game in hooks {split.GameMs:0.000}   analyzer {split.AnalyzerMs:0.000}   rest of game {split.RestMs:0.00}");
        Line(text, $"hitches (>= {recorder.HitchMs:0} ms): {recorder.HitchCount}");
        Line(text);

        WriteGame(text, recorder.Game);
        WriteMemory(text, recorder.Game);
        WriteScenes(text, recorder.Game);

        WriteMods(text, Analysis.ModRows(recorder, seconds));
        WriteMethods(text, Analysis.MethodRows(recorder, MethodLines));
        WriteHitches(text, recorder);
        WriteScripts(text, input.Scripts, seconds);
        WriteWeb(text, input.WebJson);
        WriteCoverage(text, input.Status, recorder);
        WriteLoaded(text, input.Loaded);

        return text.ToString();
    }

    private static string WriteCsv(Recorder recorder)
    {
        var ring = recorder.Ring;
        var mods = Enumerable.Range(0, recorder.Mods.Count).Where(m => m != Recorder.GameMod && ring.Count > 0 && Enumerable.Range(0, ring.Count).Any(i => ring.ModMs(m, i) > 0)).ToList();
        var csv = new StringBuilder("frame,ms,mods_ms,hooks_ms,game_in_hooks_ms,analyzer_ms,rest_ms,gc_mods,gc_game,engine_main_ms,engine_render_ms,engine_gpu_ms,time_scale,game_speed,game_dt_ms,fixed_steps,logic_ran,paused,capped_ms");

        foreach (var mod in mods) csv.Append(',').Append(CsvName(recorder.Mods[mod].Key)).Append("_ms");

        csv.Append('\n');

        for (var i = 0; i < ring.Count; i++)
        {
            var f = ring[i];

            csv.Append(string.Join(",", new[] { (recorder.FrameCount - ring.Count + i + 1).ToString(Invariant), F(f.Ms), F(f.ModsMs), F(f.HookMs), F(f.GameMs), F(f.AnalyzerMs), F(f.RestMs), f.ManagedGcs.ToString(Invariant), f.GameGcs.ToString(Invariant), F(f.Engine.MainThreadMs), F(f.Engine.RenderThreadMs), F(f.Engine.GpuMs), F(f.World.TimeScale), F(f.World.GameSpeed), F(f.World.DeltaSeconds * 1000), f.World.FixedSteps.ToString(Invariant), f.World.LogicRan ? "1" : "0", f.World.Paused ? "1" : "0", F(f.World.LogicRan && !f.World.Paused ? f.World.CappedMs : 0) }));

            foreach (var mod in mods) csv.Append(',').Append(F(ring.ModMs(mod, i)));

            csv.Append('\n');
        }

        return csv.ToString();
    }

    private static string WriteSecondsCsv(GameStats game)
    {
        var csv = new StringBuilder("second,frames,fps,max_ms,hitches,logic_per_s,fixed_per_s,game_speed,time_scale,paused,clock_per_s,capped_ms,scene,game_heap_mb,mods_heap_mb,engine_allocated_mb,engine_reserved_mb,textures_mb,process_mb\n");

        foreach (var s in game.Timeline)
        {
            var m = s.Memory;

            csv.Append(string.Join(",", new[] { s.Second.ToString(Invariant), s.Frames.ToString(Invariant), D(s.Fps), F(s.MaxMs), s.Hitches.ToString(Invariant), D(s.LogicPerSecond), D(s.FixedPerSecond), F(s.GameSpeed), F(s.TimeScale), s.Paused ? "1" : "0", D(s.ClockRate), F(s.CappedMs), CsvName(s.Scene), Mb(m.GameHeap), Mb(m.ModsHeap), Mb(m.UnityAllocated), Mb(m.UnityReserved), Mb(m.Textures), Mb(m.WorkingSet) }));
            csv.Append('\n');
        }

        return csv.ToString();
    }

    private static void WriteGame(StringBuilder text, GameStats game)
    {
        var world = game.World;

        Heading(text, "Game clock and logic");

        if (game.Frames == 0)
        {
            Line(text, "nothing recorded yet");
            Line(text);

            return;
        }

        Line(text, "The game has no fixed tick: its world updates once per frame, so logic updates per second follow the frame rate.");

        if (game.HasWorld)
        {
            Line(text, $"in a game {game.InGameMs / 1000:0} s, paused {game.PausedMs / 1000:0} s; logic updates {game.LogicPerSecond:0.0}/s while in a game");
            Line(text, $"in-game clock: {game.ClockRate:0.0} game seconds per real second while running");
            Line(text, "time at each game speed: " + string.Join(", ", game.SpeedMs.OrderBy(p => p.Key).Select(p => $"{p.Key:0.##}x {(game.InGameMs > 0 ? p.Value * 100 / game.InGameMs : 0):0}%")));
        }
        else
        {
            Line(text, "no game was loaded while recording (menus only)");
        }

        Line(text, $"physics steps {game.FixedPerSecond:0.0}/s (set to {(world.FixedDeltaSeconds > 0 ? 1 / world.FixedDeltaSeconds : 0):0}/s); engine time scale {world.TimeScale:0.###}, game speed {world.GameSpeed:0.##} at the end");
        Line(text, $"game time lost to slow frames: {game.CappedMs / 1000:0.0} s over {game.CappedFrames} frames ({game.CappedPercent:0.0}% of game time); one frame may move the game at most {world.MaxDeltaSeconds * 1000:0} ms");
        Line(text);
    }

    private static void WriteMemory(StringBuilder text, GameStats game)
    {
        Heading(text, "Memory (MB)");

        if (game.LastMemory.IsEmpty)
        {
            Line(text, "no memory reading yet");
            Line(text);

            return;
        }

        Line(text, $"{"",-20} {"now",9} {"peak",9} {"per min",9}");

        foreach (var (_, label, pick) in MemoryFields.All)
        {
            if (pick(game.PeakMemory) == 0)
            {
                Line(text, $"{label,-20} {"n/a",9}");

                continue;
            }

            Line(text, $"{label,-20} {pick(game.LastMemory) / Analysis.MB,9:0.0} {pick(game.PeakMemory) / Analysis.MB,9:0.0} {game.GrowthPerMinute(pick) / Analysis.MB,9:+0.00;-0.00;0.00}");
        }

        Line(text, $"over {(game.LastMemoryAt - game.FirstMemoryAt) / 60:0.0} minutes; per min is the change from the first reading to the last");
        Line(text);
    }

    private static void WriteScenes(StringBuilder text, GameStats game)
    {
        Heading(text, "Scenes (frame time in ms)");

        if (game.Scenes.Count == 0)
        {
            Line(text, "none yet");
            Line(text);

            return;
        }

        Line(text, $"{"scene",-20} {"seconds",8} {"fps",6} {"mean",7} {"p50",7} {"p95",7} {"p99",7} {"max",8} {">16.7",6} {">33.3",6} {"hitches",8} {"logic/s",8}");

        foreach (var s in game.Scenes)
        {
            var over16 = s.Frames > 0 ? s.Over16 * 100.0 / s.Frames : 0;
            var over33 = s.Frames > 0 ? s.Over33 * 100.0 / s.Frames : 0;

            Line(text, $"{Fit(s.Name.Length > 0 ? s.Name : "(none)", 20),-20} {s.Ms / 1000,8:0} {s.Fps,6:0.0} {s.MeanMs,7:0.00} {s.Percentile(0.50),7:0.00} {s.Percentile(0.95),7:0.00} {s.Percentile(0.99),7:0.00} {s.MaxMs,8:0.0} {over16,5:0}% {over33,5:0}% {s.Hitches,8} {s.LogicPerSecond,8:0.0}");
        }

        Line(text);
    }

    private static void WriteMods(StringBuilder text, List<ModRow> rows)
    {
        Heading(text, "Mods (time in the mod's own code)");

        if (rows.Count == 0)
        {
            Line(text, "none timed yet");
            Line(text);

            return;
        }

        Line(text, $"{"mod",-30} {"ms/frame",9} {"p99 ms",8} {"worst ms",9} {"share",6} {"KB/s",8} {"caused",8} {"probes",7}");

        foreach (var row in rows) Line(text, $"{Fit(row.Mod.Name + " " + row.Mod.Version, 30),-30} {row.AvgMs,9:0.000} {row.P99Ms,8:0.00} {row.WorstMs,9:0.00} {row.SharePercent,5:0.0}% {row.AllocKbPerSecond,8:0.0} {row.CausedHitches,8} {row.Probes,7}");

        Line(text, "KB/s is memory the mod's own code allocated on the mods' .NET runtime; the game's script heap is not counted here.");
        Line(text, "caused: hitches where the mod took 40% of the frame or more on its own. p99 is rounded down, by up to 6%.");

        Line(text);
    }

    private static void WriteMethods(StringBuilder text, List<MethodRow> rows)
    {
        Heading(text, $"Slowest methods (top {MethodLines} by self time)");

        if (rows.Count == 0)
        {
            Line(text, "none timed yet");
            Line(text);

            return;
        }

        Line(text, $"{"self ms",9} {"ms/frame",9} {"calls/frame",11} {"max call",9} {"KB",8}  kind       method (owner)");

        foreach (var row in rows)
        {
            var target = row.Probe.Target.Length > 0 ? " on " + row.Probe.Target : "";

            Line(text, $"{row.SelfMs,9:0.0} {row.SelfMsPerFrame,9:0.0000} {row.CallsPerFrame,11:0.0} {row.MaxCallMs,9:0.000} {row.AllocKb,8:0.0}  {ProbeKinds.Label(row.Probe.Kind),-10} {row.Probe.Name}{target} ({row.OwnerNames}){(row.OffThreadCalls > 0 ? $" [{row.OffThreadCalls} calls off the main thread, not timed]" : "")}");
        }

        Line(text);
    }

    private static void WriteHitches(StringBuilder text, Recorder recorder)
    {
        var hitches = recorder.Hitches;

        Heading(text, $"Latest hitches (of {recorder.HitchCount})");

        if (hitches.Count == 0)
        {
            Line(text, "none");
            Line(text);

            return;
        }

        Line(text, "gc mods: collections by the mods' .NET runtime in that frame; gc game: collections by the game's own runtime.");

        for (var i = hitches.Count - 1; i >= 0 && i >= hitches.Count - HitchLines; i--)
        {
            var hitch = hitches[i];
            var s = hitch.Sample;
            var gc = s.ManagedGcs + s.GameGcs > 0 ? $", gc mods {s.ManagedGcs} game {s.GameGcs}" : "";
            var scene = hitch.Scene.Length > 0 ? $" in {hitch.Scene}" : "";

            Line(text, $"#{hitch.Number} at {hitch.AtSeconds:0.0} s{scene}: {s.Ms:0.0} ms (mods {s.ModsMs:0.0}, hooks {s.HookMs:0.0}, game in hooks {s.GameMs:0.0}, rest {s.RestMs:0.0}{gc}) -> {Analysis.Cause(recorder, hitch)}");

            foreach (var entry in hitch.TopProbes.Take(4))
            {
                var probe = recorder.Probes[entry.Id];

                Line(text, $"    {entry.Ms,8:0.00} ms  {entry.Calls,5}x  {probe.Name} ({recorder.Mods[probe.Owners[0]].Name}, {ProbeKinds.Label(probe.Kind)})");
            }
        }

        Line(text);
    }

    private static void WriteScripts(StringBuilder text, IReadOnlyList<ScriptRow> rows, double seconds)
    {
        Heading(text, "Scripts sent to the web UI");

        if (rows.Count == 0)
        {
            Line(text, "none counted");
            Line(text);

            return;
        }

        Line(text, $"{"sender",-20} {"per s",8} {"KB/s",8} {"largest KB",11}");

        foreach (var row in rows) Line(text, $"{Fit(row.Owner, 20),-20} {(seconds > 0 ? row.Calls / seconds : 0),8:0.0} {(seconds > 0 ? row.Characters / 1024.0 / seconds : 0),8:0.0} {row.Largest / 1024.0,11:0.0}");

        Line(text);
    }

    /// <summary>The web probe's summary; a broken one is reported, not thrown.</summary>
    private static void WriteWeb(StringBuilder text, string webJson)
    {
        Heading(text, "Web UI (inside the page)");

        if (webJson.Length == 0)
        {
            Line(text, "no data from the web probe");
            Line(text);

            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(webJson);
            var root = doc.RootElement;

            Line(text, $"page frames/s {Get(root, "fps"):0}, longest gap {Get(root, "worstGapMs"):0} ms, long tasks {Get(root, "longTasks"):0} ({Get(root, "longTaskMs"):0} ms), script heap {Get(root, "heapMB"):0.0} MB, page elements {Get(root, "domNodes"):0}, over the last {Get(root, "seconds"):0} s");

            if (root.TryGetProperty("owners", out var owners) && owners.ValueKind == JsonValueKind.Array)
            {
                Line(text, $"{"script",-20} {"ms",8} {"calls",7} {"worst ms",9} {"elements",9} {"listeners",10} {"timers",7} {"messages",9}");

                foreach (var owner in owners.EnumerateArray())
                {
                    var key = owner.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() ?? "" : "";

                    Line(text, $"{Fit(Ascii(key), 20),-20} {Get(owner, "ms"),8:0.0} {Get(owner, "calls"),7:0} {Get(owner, "worstMs"),9:0.0} {Get(owner, "dom"),9:0} {Get(owner, "listeners"),10:0} {Get(owner, "timers"),7:0} {Get(owner, "posts"),9:0}");
                }
            }
        }
        catch (JsonException ex)
        {
            Line(text, "the web probe sent a summary that could not be read: " + Ascii(ex.Message));
        }

        Line(text);
    }

    private static void WriteCoverage(StringBuilder text, InstrumentStatus status, Recorder recorder)
    {
        Heading(text, "What was timed");
        Line(text, $"hooked methods {status.Hooks}, patches {status.Patches}, update methods {status.Tickers}, deep profile {(status.DeepMod.Length > 0 ? status.DeepMod + " (" + status.DeepMethods + " methods)" : "off")}");
        Line(text, $"not timeable (transpilers, IL edits) {status.Untimeable}, failed {status.Failed}, still queued {status.Pending}, too deep {recorder.Overflow}");
        Line(text, $"each timed call costs about {recorder.OverheadTicksPerCall / recorder.TicksPerMs * 1000:0.00} us, shown as analyzer time");

        foreach (var note in status.Notes) Line(text, "  " + Ascii(note));

        Line(text);
    }

    private static void WriteLoaded(StringBuilder text, IReadOnlyList<ModInfo> loaded)
    {
        Heading(text, $"Mods loaded ({loaded.Count})");

        foreach (var mod in loaded) Line(text, $"{mod.Name} {mod.Version} ({mod.Guid})");
    }

    private static double Get(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static void Heading(StringBuilder text, string heading) => Line(text, "== " + heading);

    private static void Line(StringBuilder text, string line = "") => text.Append(Ascii(line)).Append('\n');

    private static string Fit(string text, int width) => text.Length <= width ? text : text.Substring(0, width - 1) + "~";

    private static string F(float value) => value.ToString("0.###", Invariant);

    private static string D(double value) => double.IsFinite(value) ? value.ToString("0.##", Invariant) : "0";

    private static string Mb(long bytes) => bytes > 0 ? (bytes / Analysis.MB).ToString("0.0", Invariant) : "";

    private static string CsvName(string key) => new(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Anything outside printable ASCII becomes '?', so the report pastes cleanly anywhere.</summary>
    public static string Ascii(string text)
    {
        var chars = text.ToCharArray();

        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is < ' ' or > '~') chars[i] = '?';
        }

        return new string(chars);
    }
}
