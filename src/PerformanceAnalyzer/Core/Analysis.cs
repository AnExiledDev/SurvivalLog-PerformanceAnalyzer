using System;
using System.Collections.Generic;
using System.Linq;

namespace PerformanceAnalyzer.Core;

/// <summary>How far the instrumenter got: what it timed, what it could not, and why.</summary>
/// <param name="Hooks">Game or mod methods other mods hooked, now timed.</param>
/// <param name="Patches">Other mods' prefixes, postfixes and finalizers, now timed.</param>
/// <param name="Tickers">Mods' own Update-style methods, now timed.</param>
/// <param name="DeepMethods">Methods timed by the deep profile.</param>
/// <param name="Pending">Methods still waiting in the queue.</param>
/// <param name="Failed">Methods Harmony refused to patch.</param>
/// <param name="Untimeable">Transpilers and IL edits: they change the game's own code, so their cost is part of the hook or game time.</param>
/// <param name="Notes">The first few failures, for the report.</param>
public sealed record InstrumentStatus(int Hooks, int Patches, int Tickers, int DeepMethods, int Pending, int Failed, int Untimeable, string DeepMod, IReadOnlyList<string> Notes)
{
    public static readonly InstrumentStatus None = new(0, 0, 0, 0, 0, 0, 0, "", Array.Empty<string>());
}

/// <summary>One mod's line in the Mods table.</summary>
/// <param name="CausedHitches">Hitches where this mod took 40% of the frame or more on its own.</param>
/// <param name="Rating">How much a player would notice the mod: fine, watch or heavy.</param>
public sealed record ModRow(int Id, ModInfo Mod, double AvgMs, double P99Ms, double WorstMs, double SharePercent, double AllocKbPerSecond, long CausedHitches, int Probes, long Calls, Rating Rating)
{
    /// <summary>Hooked or timed, but it never took a measurable amount of time.</summary>
    public bool Idle => AvgMs < 0.0005 && WorstMs < 0.01 && CausedHitches == 0;
}

/// <summary>How much a player would notice a mod.</summary>
public enum Rating
{
    /// <summary>Cheap and steady.</summary>
    Fine,

    /// <summary>A noticeable cost per frame, or a slow frame now and then.</summary>
    Watch,

    /// <summary>A large cost every frame, or stutters of its own.</summary>
    Heavy,
}

/// <summary>A mod that made frames slow on its own, with the method that did it.</summary>
/// <param name="TypicalMs">The mod's median time in the stutters it caused; its worst frame when it caused none.</param>
/// <param name="Method">The mod's method that was slowest most often in those frames, or its slowest frame's method.</param>
public sealed record StutterRow(int Id, ModInfo Mod, long CausedHitches, double TypicalMs, double WorstMs, string Method, Rating Rating);

/// <summary>Kept hitches with the same cause and the same slowest method.</summary>
/// <param name="Method">The method that took a fifth of the frame or more, with its mod; "" when none did.</param>
public sealed record HitchGroup(string Cause, string Method, int Count, double TypicalMs, double WorstMs, long WorstNumber);

/// <summary>Which methods a Methods list holds.</summary>
public enum MethodSide
{
    All,

    /// <summary>Everything but the game's own code inside hooked methods.</summary>
    Mods,

    /// <summary>Only the game's own code inside hooked methods.</summary>
    Game,
}

/// <summary>One probe's line in the Methods table.</summary>
/// <param name="FirstCallMs">The first call's length, which often pays for loading and setup.</param>
/// <param name="MaxCall">Where the slowest call happened, with that frame's breakdown.</param>
public sealed record MethodRow(int Id, ProbeInfo Probe, string OwnerNames, long Calls, double CallsPerFrame, double SelfMsPerFrame, double TotalMs, double SelfMs, double MaxCallMs, double WorstFrameMs, double AllocKb, long OffThreadCalls, double FirstCallMs = 0, WorstCall? MaxCall = null);

/// <summary>Averages per frame over the frames in the window.</summary>
public readonly record struct SplitAverages(double FrameMs, double ModsMs, double HookMs, double GameMs, double AnalyzerMs, double RestMs);

/// <summary>Turns what the recorder holds into the rows and numbers the panel and the report show.</summary>
public static class Analysis
{
    public static Spread FrameSpread(Recorder recorder) => recorder.Ring.FrameSpread();

    public static SplitAverages Split(Recorder recorder)
    {
        var ring = recorder.Ring;

        if (ring.Count == 0) return default;

        double frame = 0, mods = 0, hook = 0, game = 0, analyzer = 0, rest = 0;

        for (var i = 0; i < ring.Count; i++)
        {
            var f = ring[i];

            frame += f.Ms;
            mods += f.ModsMs;
            hook += f.HookMs;
            game += f.GameMs;
            analyzer += f.AnalyzerMs;
            rest += f.RestMs;
        }

        var n = ring.Count;

        return new SplitAverages(frame / n, mods / n, hook / n, game / n, analyzer / n, rest / n);
    }

    /// <summary>Every mod with any recorded time, heaviest first. The game's row is left out; its time is in the split.</summary>
    public static List<ModRow> ModRows(Recorder recorder, double recordedSeconds)
    {
        var frames = Math.Max(1, recorder.FrameCount);
        var meanFrame = FrameSpread(recorder).Mean;
        var probeCounts = new int[recorder.Mods.Count];
        var calls = new long[recorder.Mods.Count];
        var rows = new List<ModRow>();

        for (var p = 0; p < recorder.Probes.Count; p++)
        {
            foreach (var owner in recorder.Probes[p].Owners)
            {
                probeCounts[owner]++;
                calls[owner] += recorder.ProbeTotals[p].Calls;
            }
        }

        for (var mod = 0; mod < recorder.Mods.Count; mod++)
        {
            if (mod == Recorder.GameMod) continue;

            var totals = recorder.ModTotals[mod];

            if (probeCounts[mod] == 0 && totals.SelfMs <= 0) continue;

            var avg = totals.SelfMs / frames;
            var p99 = recorder.Ring.ModP99(mod);
            var share = meanFrame > 0 ? 100 * avg / meanFrame : 0;
            var allocPerSecond = recordedSeconds > 0 ? totals.AllocBytes / 1024.0 / recordedSeconds : 0;

            rows.Add(new ModRow(mod, recorder.Mods[mod], avg, p99, totals.WorstFrameMs, share, allocPerSecond, totals.CausedHitches, probeCounts[mod], calls[mod], Rate(avg, p99, totals.WorstFrameMs, totals.CausedHitches, recorder.HitchMs)));
        }

        rows.Sort((a, b) => b.AvgMs.CompareTo(a.AvgMs));

        return rows;
    }

    /// <summary>
    /// Heavy: a millisecond or more every frame, three stutters of its own, or one frame as slow as a hitch. Watch: a
    /// quarter of a millisecond a frame, any stutter, half a hitch in one frame, or 2 ms in its slowest 1% of frames.
    /// </summary>
    public static Rating Rate(double avgMs, double p99Ms, double worstMs, long caused, double hitchMs)
    {
        if (avgMs >= 1 || caused >= 3 || worstMs >= hitchMs) return Rating.Heavy;
        if (avgMs >= 0.25 || caused > 0 || worstMs >= hitchMs / 2 || p99Ms >= 2) return Rating.Watch;

        return Rating.Fine;
    }

    public static string Label(Rating rating) => rating switch
    {
        Rating.Heavy => "heavy",
        Rating.Watch => "watch",
        _ => "fine",
    };

    /// <summary>
    /// Mods (not the analyzer) that caused a stutter or took half a hitch in one frame, most stutters first. The
    /// method and typical time come from the hitches still kept.
    /// </summary>
    public static List<StutterRow> StutterMakers(Recorder recorder, double recordedSeconds)
    {
        var rows = new List<StutterRow>();

        foreach (var mod in ModRows(recorder, recordedSeconds))
        {
            if (mod.Id == Recorder.AnalyzerMod) continue;
            if (mod.CausedHitches == 0 && mod.WorstMs < recorder.HitchMs / 2) continue;

            var times = new List<double>();
            var methods = new Dictionary<int, int>();

            foreach (var hitch in recorder.Hitches)
            {
                if (CauseMod(hitch) != mod.Id) continue;

                times.Add(hitch.TopMods[0].Ms);

                foreach (var entry in hitch.TopProbes)
                {
                    if (Array.IndexOf(recorder.Probes[entry.Id].Owners, mod.Id) < 0) continue;

                    methods.TryGetValue(entry.Id, out var n);
                    methods[entry.Id] = n + 1;

                    break;
                }
            }

            var method = methods.Count > 0 ? methods.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key : SlowestProbe(recorder, mod.Id);
            var typical = times.Count > 0 ? Median(times) : mod.WorstMs;

            rows.Add(new StutterRow(mod.Id, mod.Mod, mod.CausedHitches, typical, mod.WorstMs, method < 0 ? "" : MethodName(recorder.Probes[method]), mod.Rating));
        }

        rows.Sort((a, b) => b.CausedHitches != a.CausedHitches ? b.CausedHitches.CompareTo(a.CausedHitches) : b.WorstMs.CompareTo(a.WorstMs));

        return rows;
    }

    /// <summary>The kept hitches grouped by cause and slowest method, most frequent first.</summary>
    public static List<HitchGroup> HitchGroups(Recorder recorder)
    {
        var groups = new Dictionary<(string Cause, string Method), List<Hitch>>();

        foreach (var hitch in recorder.Hitches)
        {
            var key = (hitch.CauseText.Length > 0 ? hitch.CauseText : Cause(recorder, hitch), hitch.CauseText.Length > 0 ? hitch.MethodText : TopMethod(recorder, hitch));

            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<Hitch>();

            list.Add(hitch);
        }

        var rows = groups.Select(g =>
        {
            var worst = g.Value.OrderByDescending(h => h.Sample.Ms).First();

            return new HitchGroup(g.Key.Cause, g.Key.Method, g.Value.Count, Median(g.Value.Select(h => (double)h.Sample.Ms).ToList()), worst.Sample.Ms, worst.Number);
        }).ToList();

        rows.Sort((a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : b.WorstMs.CompareTo(a.WorstMs));

        return rows;
    }

    /// <summary>The kept hitches, slowest first.</summary>
    public static List<Hitch> WorstHitches(Recorder recorder, int limit) => recorder.Hitches.OrderByDescending(h => h.Sample.Ms).ThenBy(h => h.Number).Take(limit).ToList();

    /// <summary>"Type.Method", with the kind when it is a patch: "StatsUI.PageTick postfix".</summary>
    public static string MethodName(ProbeInfo probe) => probe.Kind is ProbeKind.Prefix or ProbeKind.Postfix or ProbeKind.Finalizer ? probe.Name + " " + ProbeKinds.Label(probe.Kind) : probe.Name;

    /// <summary>The frame's slowest method with its mod, when it took a fifth of the frame or more; "" otherwise.</summary>
    public static string TopMethod(Recorder recorder, Hitch hitch)
    {
        if (hitch.TopProbes.Count == 0 || hitch.TopProbes[0].Ms < hitch.Sample.Ms * 0.2) return "";

        var probe = recorder.Probes[hitch.TopProbes[0].Id];

        return MethodName(probe) + " (" + recorder.Mods[probe.Owners[0]].Name + ")";
    }

    private static int SlowestProbe(Recorder recorder, int mod)
    {
        var best = -1;

        for (var p = 0; p < recorder.Probes.Count; p++)
        {
            if (Array.IndexOf(recorder.Probes[p].Owners, mod) < 0) continue;
            if (best < 0 || recorder.ProbeTotals[p].WorstFrameMs > recorder.ProbeTotals[best].WorstFrameMs) best = p;
        }

        return best;
    }

    private static double Median(List<double> values)
    {
        values.Sort();

        var mid = values.Count / 2;

        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    /// <summary>Probes that ran at least once, by self time, filtered to one mod when <paramref name="modKey"/> is set.</summary>
    public static List<MethodRow> MethodRows(Recorder recorder, int limit, string modKey = "", MethodSide side = MethodSide.All)
    {
        var frames = Math.Max(1, recorder.FrameCount);
        var tpm = recorder.TicksPerMs;
        var picked = new List<(int Id, long Self, long OffThread)>();

        // Pick and sort on the numbers alone, then build rows (and their owner strings) only for the ones kept: the
        // panel asks for this twice a second over every probe, and most of them never make the list.
        for (var p = 0; p < recorder.Probes.Count; p++)
        {
            var probe = recorder.Probes[p];
            var totals = recorder.ProbeTotals[p];
            var offThread = recorder.OffThreadCalls(p);

            if (totals.Calls == 0 && offThread == 0) continue;
            if (modKey.Length > 0 && !OwnedBy(recorder, probe, modKey)) continue;
            if (side == MethodSide.Mods && probe.Kind == ProbeKind.Game) continue;
            if (side == MethodSide.Game && probe.Kind != ProbeKind.Game) continue;

            picked.Add((p, totals.SelfTicks, offThread));
        }

        picked.Sort((a, b) => b.Self != a.Self ? b.Self.CompareTo(a.Self) : a.Id.CompareTo(b.Id));

        var rows = new List<MethodRow>(Math.Min(limit, picked.Count));

        for (var i = 0; i < picked.Count && i < limit; i++) rows.Add(Row(recorder, picked[i].Id, picked[i].OffThread, frames, tpm));

        return rows;
    }

    /// <summary>Methods by their single slowest call, slowest first; the calls stay listed after their frames leave the window.</summary>
    public static List<MethodRow> SlowestCalls(Recorder recorder, int limit)
    {
        var frames = Math.Max(1, recorder.FrameCount);
        var picked = new List<(int Id, long Max)>();

        for (var p = 0; p < recorder.Probes.Count; p++)
        {
            var totals = recorder.ProbeTotals[p];

            if (totals.Calls > 0 && recorder.Probes[p].Kind != ProbeKind.Analyzer) picked.Add((p, totals.MaxCallTicks));
        }

        picked.Sort((a, b) => b.Max != a.Max ? b.Max.CompareTo(a.Max) : a.Id.CompareTo(b.Id));

        var rows = new List<MethodRow>(Math.Min(limit, picked.Count));

        for (var i = 0; i < picked.Count && i < limit; i++) rows.Add(Row(recorder, picked[i].Id, recorder.OffThreadCalls(picked[i].Id), frames, recorder.TicksPerMs));

        return rows;
    }

    private static MethodRow Row(Recorder recorder, int p, long offThread, long frames, double tpm)
    {
        var probe = recorder.Probes[p];
        var totals = recorder.ProbeTotals[p];
        var owners = probe.Owners.Length == 1 ? recorder.Mods[probe.Owners[0]].Name : string.Join(", ", probe.Owners.Select(o => recorder.Mods[o].Name));

        return new MethodRow(p, probe, owners, totals.Calls, (double)totals.Calls / frames, totals.SelfTicks / tpm / frames, totals.TotalTicks / tpm, totals.SelfTicks / tpm, totals.MaxCallTicks / tpm, totals.WorstFrameMs, totals.AllocBytes / 1024.0, offThread, totals.FirstCallTicks / tpm, totals.MaxCall.IsNone ? null : totals.MaxCall);
    }

    private static bool OwnedBy(Recorder recorder, ProbeInfo probe, string modKey)
    {
        foreach (var owner in probe.Owners)
        {
            if (recorder.Mods[owner].Key == modKey) return true;
        }

        return false;
    }

    /// <summary>The slow web messages handled in one frame.</summary>
    public static IEnumerable<SlowMessage> MessagesIn(Recorder recorder, long frame) => recorder.SlowMessages.Where(m => m.Frame == frame);

    /// <summary>Plain sentences on what the numbers say, most important first.</summary>
    public static List<string> Verdict(Recorder recorder, double recordedSeconds)
    {
        var lines = new List<string>();
        var spread = FrameSpread(recorder);

        if (spread.Count < 30)
        {
            lines.Add(recorder.IsRecording ? "Recording. Give it a few seconds of normal play." : "Not recording. Press Start, then play normally for a minute.");

            return lines;
        }

        var split = Split(recorder);
        var fps = spread.Mean > 0 ? 1000 / spread.Mean : 0;
        var modShare = split.FrameMs > 0 ? (split.ModsMs + split.HookMs) / split.FrameMs : 0;
        var mods = ModRows(recorder, recordedSeconds).Where(r => r.Id != Recorder.AnalyzerMod).ToList();

        lines.Add($"About {fps:0} frames per second on average; the slowest 1% of frames take {spread.P99:0.0} ms or more.");

        if (modShare >= 0.15) lines.Add($"Mods are a large part of each frame: {Percent(modShare)} of frame time is mod code or the cost of their hooks.");
        else if (modShare >= 0.05) lines.Add($"Mods take a noticeable part of each frame: {Percent(modShare)} of frame time.");
        else lines.Add($"Mods are a small part of each frame: {Percent(modShare)} of frame time. Most of the time is the game itself.");

        if (mods.Count > 0 && mods[0].AvgMs >= 0.25) lines.Add($"The heaviest mod is {mods[0].Mod.Name}, at {mods[0].AvgMs:0.00} ms per frame on average.");

        var stutters = StutterMakers(recorder, recordedSeconds);

        if (stutters.Count > 0) lines.Add("Stutters come from " + string.Join("; ", stutters.Take(3).Select(StutterText)) + ".");

        lines.AddRange(GameVerdict(recorder));
        lines.AddRange(HitchVerdict(recorder));

        if (split.HookMs >= 0.5) lines.Add($"Hooking game methods costs {split.HookMs:0.00} ms per frame by itself. The Methods tab sorted by self time shows which hooks run most often.");
        if (split.AnalyzerMs >= 1) lines.Add($"The analyzer itself adds about {split.AnalyzerMs:0.0} ms per frame. A deep profile adds the most; turn it off when done.");

        return lines;
    }

    public const double MB = 1024 * 1024;

    /// <summary>What the game side shows: game time lost to slow frames, memory that keeps growing, the slowest place.</summary>
    public static List<string> GameVerdict(Recorder recorder)
    {
        var lines = new List<string>();
        var game = recorder.Game;

        if (game.CappedPercent >= 1) lines.Add($"Some frames were so slow that the game itself slowed down: {game.CappedPercent:0.0}% of game time was lost over {game.CappedFrames} frames.");

        var minutes = (game.LastMemoryAt - game.FirstMemoryAt) / 60;
        var heap = game.GrowthPerMinute(m => m.GameHeap) / MB;

        if (minutes >= 5 && heap >= 5) lines.Add($"The game's script memory grew by {heap:0} MB a minute over {minutes:0} minutes. If it keeps climbing in a long session, something is holding on to memory.");

        var scenes = game.Scenes.Where(s => s.Frames >= 300 && s.Name.Length > 0).ToList();

        if (scenes.Count >= 2)
        {
            var slowest = scenes.OrderByDescending(s => s.MeanMs).First();

            lines.Add($"The slowest place was {slowest.Name}, at about {slowest.Fps:0} frames per second.");
        }

        return lines;
    }

    /// <summary>What most hitches had in common.</summary>
    public static List<string> HitchVerdict(Recorder recorder)
    {
        var lines = new List<string>();
        var hitches = recorder.Hitches;

        if (hitches.Count == 0)
        {
            lines.Add($"No frame took {recorder.HitchMs:0} ms or more.");

            return lines;
        }

        var analyzer = recorder.Mods[Recorder.AnalyzerMod].Name;
        var causes = recorder.HitchCauses.Where(c => c.Key != analyzer).OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        var summary = string.Join("; ", causes.Take(3).Select(c => $"{c.Key} ({c.Value})"));

        lines.Add($"{recorder.HitchCount} frames took {recorder.HitchMs:0} ms or more." + (summary.Length > 0 ? $" Most likely causes: {summary}." : ""));

        if (recorder.HitchCauses.TryGetValue(analyzer, out var own)) lines.Add($"The analyzer itself caused {own} of them; those are not a mod's fault.");

        return lines;
    }

    /// <summary>The mod that took 40% of a slow frame or more on its own, or <see cref="Recorder.NotTimed"/> when none did.</summary>
    public static int CauseMod(Hitch hitch)
    {
        if (hitch.TopMods.Count == 0) return Recorder.NotTimed;

        var top = hitch.TopMods[0];

        return top.Id != Recorder.GameMod && top.Ms >= hitch.Sample.Ms * 0.4 ? top.Id : Recorder.NotTimed;
    }

    /// <summary>The single most likely reason one frame was slow.</summary>
    public static string Cause(Recorder recorder, Hitch hitch)
    {
        var sample = hitch.Sample;
        var mod = CauseMod(hitch);

        if (mod != Recorder.NotTimed) return recorder.Mods[mod].Name;

        if (sample.GameGcs > 0) return "the game's garbage collection";
        if (sample.ManagedGcs > 0) return "garbage collection in mod code";
        if (sample.GameMs >= sample.Ms * 0.4) return "game code inside a hooked method";
        if (sample.ModsMs + sample.HookMs >= sample.Ms * 0.4) return "several mods together";

        return "the game itself (no mod was busy)";
    }

    private static string StutterText(StutterRow row)
    {
        var count = row.CausedHitches > 0 ? $"{row.CausedHitches} stutter{(row.CausedHitches == 1 ? "" : "s")}, worst {row.WorstMs:0} ms" : $"one frame of {row.WorstMs:0} ms";

        return row.Method.Length > 0 ? $"{row.Mod.Name} ({count}, in {row.Method})" : $"{row.Mod.Name} ({count})";
    }

    private static string Percent(double fraction) => $"{fraction * 100:0}%";
}
