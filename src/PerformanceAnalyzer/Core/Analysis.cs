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
public sealed record ModRow(int Id, ModInfo Mod, double AvgMs, double P99Ms, double WorstMs, double SharePercent, double AllocKbPerSecond, long HitchFrames, int Probes, long Calls);

/// <summary>One probe's line in the Methods table.</summary>
public sealed record MethodRow(int Id, ProbeInfo Probe, string OwnerNames, long Calls, double CallsPerFrame, double SelfMsPerFrame, double TotalMs, double SelfMs, double MaxCallMs, double WorstFrameMs, double AllocKb, long OffThreadCalls);

/// <summary>Averages per frame over the frames in the window.</summary>
public readonly record struct SplitAverages(double FrameMs, double ModsMs, double HookMs, double GameMs, double AnalyzerMs, double RestMs);

/// <summary>Turns what the recorder holds into the rows and numbers the panel and the report show.</summary>
public static class Analysis
{
    public static Spread FrameSpread(Recorder recorder) => Spread.Of(recorder.Ring.Select(f => f.Ms));

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
            var p99 = Spread.Of(recorder.Ring.ModSeries(mod)).P99;
            var share = meanFrame > 0 ? 100 * avg / meanFrame : 0;
            var allocPerSecond = recordedSeconds > 0 ? totals.AllocBytes / 1024.0 / recordedSeconds : 0;

            rows.Add(new ModRow(mod, recorder.Mods[mod], avg, p99, totals.WorstFrameMs, share, allocPerSecond, totals.HitchFrames, probeCounts[mod], calls[mod]));
        }

        rows.Sort((a, b) => b.AvgMs.CompareTo(a.AvgMs));

        return rows;
    }

    /// <summary>Probes that ran at least once, by self time, filtered to one mod when <paramref name="modKey"/> is set.</summary>
    public static List<MethodRow> MethodRows(Recorder recorder, int limit, string modKey = "")
    {
        var frames = Math.Max(1, recorder.FrameCount);
        var tpm = recorder.TicksPerMs;
        var rows = new List<MethodRow>();

        for (var p = 0; p < recorder.Probes.Count; p++)
        {
            var probe = recorder.Probes[p];
            var totals = recorder.ProbeTotals[p];
            var offThread = recorder.OffThreadCalls(p);

            if (totals.Calls == 0 && offThread == 0) continue;
            if (modKey.Length > 0 && !probe.Owners.Any(o => recorder.Mods[o].Key == modKey)) continue;

            var owners = string.Join(", ", probe.Owners.Select(o => recorder.Mods[o].Name));

            rows.Add(new MethodRow(p, probe, owners, totals.Calls, (double)totals.Calls / frames, totals.SelfTicks / tpm / frames, totals.TotalTicks / tpm, totals.SelfTicks / tpm, totals.MaxCallTicks / tpm, totals.WorstFrameMs, totals.AllocBytes / 1024.0, offThread));
        }

        rows.Sort((a, b) => b.SelfMs.CompareTo(a.SelfMs));

        if (rows.Count > limit) rows.RemoveRange(limit, rows.Count - limit);

        return rows;
    }

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

        var spiky = mods.Where(m => m.WorstMs >= recorder.HitchMs / 2).OrderByDescending(m => m.WorstMs).FirstOrDefault();

        if (spiky != null) lines.Add($"{spiky.Mod.Name} had a single frame where it took {spiky.WorstMs:0} ms on its own, which is felt as a stutter.");

        lines.AddRange(HitchVerdict(recorder));

        if (split.HookMs >= 0.5) lines.Add($"Hooking game methods costs {split.HookMs:0.00} ms per frame by itself. The Methods tab sorted by self time shows which hooks run most often.");
        if (split.AnalyzerMs >= 1) lines.Add($"The analyzer itself adds about {split.AnalyzerMs:0.0} ms per frame. A deep profile adds the most; turn it off when done.");

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

        var causes = hitches.GroupBy(h => Cause(recorder, h)).OrderByDescending(g => g.Count()).ToList();
        var summary = string.Join("; ", causes.Take(3).Select(g => $"{g.Key} ({g.Count()})"));

        lines.Add($"{recorder.HitchCount} frames took {recorder.HitchMs:0} ms or more. Most likely causes: {summary}.");

        return lines;
    }

    /// <summary>The single most likely reason one frame was slow.</summary>
    public static string Cause(Recorder recorder, Hitch hitch)
    {
        var sample = hitch.Sample;

        if (hitch.TopMods.Count > 0)
        {
            var top = hitch.TopMods[0];

            if (top.Id != Recorder.GameMod && top.Ms >= sample.Ms * 0.4) return recorder.Mods[top.Id].Name;
        }

        if (sample.GameGcs > 0) return "the game's garbage collection";
        if (sample.ManagedGcs > 0) return "garbage collection in mod code";
        if (sample.GameMs >= sample.Ms * 0.4) return "game code inside a hooked method";
        if (sample.ModsMs + sample.HookMs >= sample.Ms * 0.4) return "several mods together";

        return "the game itself (no mod was busy)";
    }

    private static string Percent(double fraction) => $"{fraction * 100:0}%";
}
