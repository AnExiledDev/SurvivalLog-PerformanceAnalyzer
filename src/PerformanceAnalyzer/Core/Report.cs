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
    private const int GameMethodLines = 15;
    private const int HitchLines = 10;
    private const int GroupLines = 15;
    private const int SlowCallLines = 15;
    private const int SlowMessageLines = 15;

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

        WriteStutters(text, Analysis.StutterMakers(recorder, seconds));
        WriteHitchGroups(text, recorder);

        WriteGame(text, recorder.Game);
        WriteMemory(text, recorder.Game);
        WriteScenes(text, recorder.Game);

        WriteMods(text, Analysis.ModRows(recorder, seconds));
        WriteMethods(text, $"Slowest mod methods (top {MethodLines} by self time)", Analysis.MethodRows(recorder, MethodLines, side: MethodSide.Mods), seconds);
        WriteMethods(text, $"Game code inside hooked methods (top {GameMethodLines})", Analysis.MethodRows(recorder, GameMethodLines, side: MethodSide.Game), seconds);
        WriteSlowestCalls(text, recorder);
        WriteHitches(text, recorder);
        WriteScripts(text, input.Scripts, seconds);
        WriteSlowMessages(text, recorder);
        WriteBrowser(text, recorder.Game);
        WriteWeb(text, input.WebJson);
        WriteCoverage(text, input.Status, recorder);
        WriteLoaded(text, input.Loaded);

        return text.ToString();
    }

    private static string WriteCsv(Recorder recorder)
    {
        var ring = recorder.Ring;
        var mods = Enumerable.Range(0, recorder.Mods.Count).Where(m => m != Recorder.GameMod && ring.Count > 0 && Enumerable.Range(0, ring.Count).Any(i => ring.ModMs(m, i) > 0)).ToList();
        var csv = new StringBuilder("frame,ms,mods_ms,hooks_ms,game_in_hooks_ms,analyzer_ms,rest_ms,gc_mods,gc_game,engine_main_ms,engine_render_ms,engine_gpu_ms,time_scale,game_speed,game_dt_ms,fixed_steps,logic_ran,paused,capped_ms,page");

        foreach (var mod in mods) csv.Append(',').Append(CsvName(recorder.Mods[mod].Key)).Append("_ms");

        csv.Append('\n');

        for (var i = 0; i < ring.Count; i++)
        {
            var f = ring[i];

            csv.Append(string.Join(",", new[] { (recorder.FrameCount - ring.Count + i + 1).ToString(Invariant), F(f.Ms), F(f.ModsMs), F(f.HookMs), F(f.GameMs), F(f.AnalyzerMs), F(f.RestMs), f.ManagedGcs.ToString(Invariant), f.GameGcs.ToString(Invariant), F(f.Engine.MainThreadMs), F(f.Engine.RenderThreadMs), F(f.Engine.GpuMs), F(f.World.TimeScale), F(f.World.GameSpeed), F(f.World.DeltaSeconds * 1000), f.World.FixedSteps.ToString(Invariant), f.World.LogicRan ? "1" : "0", f.World.Paused ? "1" : "0", F(f.World.LogicRan && !f.World.Paused ? f.World.CappedMs : 0), CsvPage(recorder.PageName(f.Page)) }));

            foreach (var mod in mods) csv.Append(',').Append(F(ring.ModMs(mod, i)));

            csv.Append('\n');
        }

        return csv.ToString();
    }

    private static string WriteSecondsCsv(GameStats game)
    {
        var csv = new StringBuilder("second,frames,fps,max_ms,hitches,logic_per_s,fixed_per_s,game_speed,time_scale,paused,clock_per_s,capped_ms,scene,game_heap_mb,mods_heap_mb,engine_allocated_mb,engine_reserved_mb,textures_mb,process_mb,page,web_styles,web_style_ms,web_layouts,web_layout_ms,web_script_ms,web_task_ms\n");

        foreach (var s in game.Timeline)
        {
            var m = s.Memory;

            csv.Append(string.Join(",", new[] { s.Second.ToString(Invariant), s.Frames.ToString(Invariant), D(s.Fps), F(s.MaxMs), s.Hitches.ToString(Invariant), D(s.LogicPerSecond), D(s.FixedPerSecond), F(s.GameSpeed), F(s.TimeScale), s.Paused ? "1" : "0", D(s.ClockRate), F(s.CappedMs), CsvName(s.Scene), Mb(m.GameHeap), Mb(m.ModsHeap), Mb(m.UnityAllocated), Mb(m.UnityReserved), Mb(m.Textures), Mb(m.WorkingSet), CsvPage(s.Page), Web(s.Web, s.Web.StyleCount), Web(s.Web, s.Web.StyleMs), Web(s.Web, s.Web.LayoutCount), Web(s.Web, s.Web.LayoutMs), Web(s.Web, s.Web.ScriptMs), Web(s.Web, s.Web.TaskMs) }));
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

        if (game.HasWorld)
        {
            Line(text, $"In a game for {game.InGameMs / 1000:0} s, paused for {game.PausedMs / 1000:0} s of it.");
            Line(text, $"The world updated {game.LogicPerSecond:0.0} times a second. It updates once per frame, so this follows the frame rate.");
            Line(text, $"The in-game clock ran {game.ClockRate:0.0} game seconds per real second while not paused.");
            Line(text, "Time at each game speed: " + string.Join(", ", game.SpeedMs.OrderBy(p => p.Key).Select(p => $"{p.Key:0.##}x {(game.InGameMs > 0 ? p.Value * 100 / game.InGameMs : 0):0}%")) + ".");
        }
        else
        {
            Line(text, "No game was loaded while recording (menus only).");
        }

        Line(text, $"Physics ran {game.FixedPerSecond:0.0} steps a second (set to {(world.FixedDeltaSeconds > 0 ? 1 / world.FixedDeltaSeconds : 0):0}). At the end: game speed {world.GameSpeed:0.##}x, engine time scale {world.TimeScale:0.###}.");
        Line(text, game.CappedFrames > 0
            ? $"Slow frames cost {game.CappedMs / 1000:0.0} s of game time over {game.CappedFrames} frames ({game.CappedPercent:0.0}%). One frame moves the game at most {world.MaxDeltaSeconds * 1000:0} ms, so a slower frame makes the game itself run slow."
            : $"No game time was lost to slow frames (one frame moves the game at most {world.MaxDeltaSeconds * 1000:0} ms).");
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

        Line(text, $"{"mod",-30} {"rating",6} {"ms/frame",9} {"p99 ms",8} {"worst ms",9} {"share",6} {"KB/s",8} {"caused",8} {"probes",7}");

        foreach (var row in rows.Where(r => !r.Idle)) Line(text, $"{Fit(row.Mod.Name + " " + row.Mod.Version, 30),-30} {Analysis.Label(row.Rating),6} {row.AvgMs,9:0.000} {row.P99Ms,8:0.00} {row.WorstMs,9:0.00} {row.SharePercent,5:0.0}% {row.AllocKbPerSecond,8:0.0} {row.CausedHitches,8} {row.Probes,7}");

        var idle = rows.Where(r => r.Idle).ToList();

        if (idle.Count > 0) Line(text, $"{idle.Count} more with no measurable time: " + string.Join(", ", idle.Select(r => r.Mod.Name)));

        Line(text, "rating: heavy is 1 ms a frame or more, 3 stutters of its own, or one frame as slow as a hitch; watch is 0.25 ms a frame, any stutter, a frame of half a hitch, or p99 of 2 ms; fine is less.");
        Line(text, "KB/s is memory the mod's own code allocated on the mods' .NET runtime; the game's script heap is not counted here.");
        Line(text, "caused: hitches where the mod took 40% of the frame or more on its own. p99 is rounded down, by up to 6%.");

        Line(text);
    }

    private static void WriteStutters(StringBuilder text, List<StutterRow> rows)
    {
        Heading(text, "Stutter makers (mods that made frames slow on their own)");

        if (rows.Count == 0)
        {
            Line(text, "none");
            Line(text);

            return;
        }

        Line(text, $"{"mod",-30} {"rating",6} {"stutters",8} {"typical",8} {"worst ms",9}  method");

        foreach (var row in rows) Line(text, $"{Fit(row.Mod.Name + " " + row.Mod.Version, 30),-30} {Analysis.Label(row.Rating),6} {row.CausedHitches,8} {row.TypicalMs,8:0.0} {row.WorstMs,9:0.0}  {row.Method}");

        Line(text, "stutters: hitches where the mod took 40% of the frame or more. typical: the mod's median ms in those frames (its worst frame when it caused none). method: its slowest method most often.");
        Line(text);
    }

    private static void WriteHitchGroups(StringBuilder text, Recorder recorder)
    {
        var groups = Analysis.HitchGroups(recorder);
        var kept = recorder.Hitches.Count;

        Heading(text, kept < recorder.HitchCount ? $"Hitches by cause (the last {kept} of {recorder.HitchCount})" : "Hitches by cause");

        if (groups.Count == 0)
        {
            Line(text, "none");
            Line(text);

            return;
        }

        Line(text, $"{"count",6} {"typical",8} {"worst ms",9} {"worst",7}  cause, slowest method");

        foreach (var g in groups.Take(GroupLines)) Line(text, $"{g.Count,6} {g.TypicalMs,8:0.0} {g.WorstMs,9:0.0} {"#" + g.WorstNumber,7}  {g.Cause}{(g.Method.Length > 0 ? ", " + g.Method : "")}");

        if (groups.Count > GroupLines) Line(text, $"{groups.Count - GroupLines} more groups with fewer hitches");

        Line(text, "typical: median frame time of the group. The slowest method is named when it took a fifth of the frame or more.");
        Line(text);
    }

    private static void WriteMethods(StringBuilder text, string heading, List<MethodRow> rows, double seconds)
    {
        Heading(text, heading);

        if (rows.Count == 0)
        {
            Line(text, "none timed yet");
            Line(text);

            return;
        }

        Line(text, $"{"self ms",9} {"ms/frame",9} {"calls/frame",11} {"max call",9} {"first call",10} {"alloc/s",9}  kind       method (owner)");

        foreach (var row in rows)
        {
            var target = row.Probe.Target.Length > 0 ? " on " + row.Probe.Target : "";

            Line(text, $"{row.SelfMs,9:0.0} {row.SelfMsPerFrame,9:0.0000} {row.CallsPerFrame,11:0.0} {row.MaxCallMs,9:0.000} {row.FirstCallMs,10:0.000} {PerSecond(row.AllocKb, seconds),9}  {ProbeKinds.Label(row.Probe.Kind),-10} {row.Probe.Name}{target} ({row.OwnerNames}){(row.OffThreadCalls > 0 ? $" [{row.OffThreadCalls} calls off the main thread, not timed]" : "")}");
        }

        Line(text);
    }

    private static void WriteSlowestCalls(StringBuilder text, Recorder recorder)
    {
        var rows = Analysis.SlowestCalls(recorder, SlowCallLines);

        Heading(text, $"Slowest single calls (top {SlowCallLines}, kept for the whole recording)");

        if (rows.Count == 0)
        {
            Line(text, "none timed yet");
            Line(text);

            return;
        }

        Line(text, $"{"max call",9} {"first call",10} {"at s",7} {"frame ms",9}  where, frame parts, method (owner)");

        foreach (var row in rows)
        {
            var call = row.MaxCall;
            var s = call?.Sample ?? default;
            var where = call == null ? "" : Snapshot.Where(call.Scene, call.Page);
            var parts = call == null ? "" : $"mods {s.ModsMs:0.0}, hooks {s.HookMs:0.0}, game in hooks {s.GameMs:0.0}, rest {s.RestMs:0.0}";

            Line(text, $"{row.MaxCallMs,9:0.00} {row.FirstCallMs,10:0.00} {call?.AtSeconds ?? 0,7:0.0} {s.Ms,9:0.0}  {(where.Length > 0 ? where + "; " : "")}{(parts.Length > 0 ? parts + "; " : "")}{Analysis.MethodName(row.Probe)} ({row.OwnerNames})");
        }

        Line(text, "max call: the method's slowest call, counting the calls inside it. first call: its first call, which often pays for loading and setup. frame ms: the whole frame that call was in.");
        Line(text);
    }

    private static void WriteSlowMessages(StringBuilder text, Recorder recorder)
    {
        var messages = recorder.SlowMessages;

        Heading(text, $"Slow web messages (the game handling a message from the web UI in {recorder.HitchMs:0} ms or more)");

        if (messages.Count == 0)
        {
            Line(text, "none");
            Line(text);

            return;
        }

        if (recorder.SlowMessageCount > messages.Count) Line(text, $"{recorder.SlowMessageCount} in all; the last {messages.Count} are kept");

        Line(text, $"{"ms",7} {"at s",7} {"page",-14} {"command",-30}  first 80 characters");

        foreach (var m in messages.OrderByDescending(m => m.Ms).Take(SlowMessageLines)) Line(text, $"{m.Ms,7:0.0} {m.AtSeconds,7:0.0} {Fit(m.Page, 14),-14} {Fit(m.Command, 30),-30}  {m.Head}");

        Line(text, "command: the message's type, page and parameter. Type 3 is a page talking to the game; the page decides what it asks for.");
        Line(text);
    }

    private static void WriteBrowser(StringBuilder text, GameStats game)
    {
        Heading(text, "Browser work (from remote debugging)");

        if (game.WebSeconds == 0)
        {
            Line(text, "not read: turn on [Web] RemoteDebugging and restart the game to get these");
            Line(text);

            return;
        }

        var n = (double)game.WebSeconds;
        var t = game.WebTotals;
        var w = game.WebWorst;

        Line(text, $"per second over {game.WebSeconds} s: {t.StyleCount / n:0.0} style recalcs ({t.StyleMs / n:0.0} ms), {t.LayoutCount / n:0.0} layouts ({t.LayoutMs / n:0.0} ms), scripts {t.ScriptMs / n:0.0} ms, all tasks {t.TaskMs / n:0.0} ms");

        if (game.WebWorstSecond >= 0) Line(text, $"busiest second: {game.WebWorstSecond} s{(game.WebWorstPage.Length > 0 ? " on " + game.WebWorstPage : "")}, styles {w.StyleMs:0.0} ms ({w.StyleCount:0}), layout {w.LayoutMs:0.0} ms ({w.LayoutCount:0}), scripts {w.ScriptMs:0.0} ms");

        Line(text, "The seconds CSV has these per second. Many style recalcs or layouts a second usually means a script keeps changing the page.");
        Line(text);
    }

    /// <summary>Kilobytes over the recording as a rate: "12 KB/s", "1.5 MB/s", "0" when nothing was allocated.</summary>
    public static string PerSecond(double kb, double seconds)
    {
        var rate = seconds > 0 ? kb / seconds : 0;

        if (rate <= 0) return "0";
        if (rate >= 1024) return (rate / 1024).ToString("0.0", Invariant) + " MB/s";

        return rate.ToString(rate < 10 ? "0.0" : "0", Invariant) + " KB/s";
    }

    private static void WriteHitches(StringBuilder text, Recorder recorder)
    {
        var hitches = recorder.Hitches;

        Heading(text, $"Worst hitches ({Math.Min(HitchLines, hitches.Count)} slowest of the {hitches.Count} kept)");

        if (hitches.Count == 0)
        {
            Line(text, "none");
            Line(text);

            return;
        }

        Line(text, "The parts add up to the frame. gc mods: collections by the mods' .NET runtime in that frame; gc game: collections by the game's own runtime.");

        foreach (var hitch in Analysis.WorstHitches(recorder, HitchLines))
        {
            var s = hitch.Sample;
            var gc = s.ManagedGcs + s.GameGcs > 0 ? $", gc mods {s.ManagedGcs} game {s.GameGcs}" : "";
            var where = Snapshot.Where(hitch.Scene, hitch.Page);
            var scene = where.Length > 0 ? $" in {where}" : "";

            Line(text, $"#{hitch.Number} at {hitch.AtSeconds:0.0} s{scene}: {s.Ms:0.0} ms (mods {s.ModsMs:0.0}, hooks {s.HookMs:0.0}, game in hooks {s.GameMs:0.0}, analyzer {s.AnalyzerMs:0.0}, rest {s.RestMs:0.0}{gc}) -> {(hitch.CauseText.Length > 0 ? hitch.CauseText : Analysis.Cause(recorder, hitch))}");

            foreach (var m in Analysis.MessagesIn(recorder, hitch.Frame)) Line(text, $"    {m.Ms,8:0.00} ms  web message {m.Command}: {m.Head}");

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

            if (Get(root, "longTasks") > 0) Line(text, $"long task time no timed script explains: {Get(root, "longTaskUnownedMs"):0} ms of {Get(root, "longTaskMs"):0} ms. That is the page's own work: style, layout, painting, or the game's scripts.");

            if (root.TryGetProperty("longTaskPages", out var taskPages) && taskPages.ValueKind == JsonValueKind.Array && taskPages.GetArrayLength() > 0)
            {
                Line(text, $"{"long tasks on page",-20} {"count",6} {"ms",8} {"unexplained",12}");

                foreach (var page in taskPages.EnumerateArray()) Line(text, $"{Fit(Ascii(Str(page, "page")), 20),-20} {Get(page, "count"),6:0} {Get(page, "ms"),8:0} {Get(page, "unownedMs"),12:0}");
            }

            if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array && pages.GetArrayLength() > 0)
            {
                Line(text, $"{"page frame",-20} {"elements",9} {"listeners",10}");

                foreach (var page in pages.EnumerateArray()) Line(text, $"{Fit(Ascii(Str(page, "name")), 20),-20} {Get(page, "elements"),9:0} {Get(page, "listeners"),10:0}");
            }

            if (root.TryGetProperty("owners", out var owners) && owners.ValueKind == JsonValueKind.Array)
            {
                Line(text, $"{"script",-20} {"ms",8} {"calls",7} {"worst ms",9} {"elements",9} {"listeners",10} {"timers",7} {"messages",9}");

                foreach (var owner in owners.EnumerateArray())
                {
                    var key = Str(owner, "key");

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

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double Get(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static void Heading(StringBuilder text, string heading) => Line(text, "== " + heading);

    private static void Line(StringBuilder text, string line = "") => text.Append(Ascii(line)).Append('\n');

    private static string Fit(string text, int width) => text.Length <= width ? text : text.Substring(0, width - 1) + "~";

    private static string F(float value) => value.ToString("0.###", Invariant);

    private static string D(double value) => double.IsFinite(value) ? value.ToString("0.##", Invariant) : "0";

    private static string Mb(long bytes) => bytes > 0 ? (bytes / Analysis.MB).ToString("0.0", Invariant) : "";

    private static string CsvName(string key) => new(key.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>A page name for a CSV cell: "PhoneSMS+Map" keeps its '+', anything else odd becomes '_'.</summary>
    private static string CsvPage(string page) => new(page.Select(c => c < 128 && (char.IsLetterOrDigit(c) || c is '+' or '-' or '.') ? c : '_').ToArray());

    /// <summary>A browser counter for the seconds CSV: blank in a second with no reading (a reading always has some task time).</summary>
    private static string Web(WebReading reading, float value) => reading.TaskMs > 0 ? value.ToString("0.##", Invariant) : "";

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
