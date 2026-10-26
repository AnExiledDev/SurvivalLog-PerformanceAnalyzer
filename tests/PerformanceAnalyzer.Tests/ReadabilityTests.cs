using System;
using System.Linq;
using System.Text.Json;
using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>
/// What makes the numbers readable: stutter makers with their method, hitches grouped by cause, ratings, mods with
/// nothing to show folded away, mod and game methods apart. One tick is a microsecond.
/// </summary>
public class ReadabilityTests
{
    private const double TicksPerMs = 1000;

    private sealed class Run
    {
        public readonly Recorder Recorder = new(TicksPerMs, 256, 50);
        public readonly int ModA, ModB, ModC, Slow, Light, Quiet, GameCode;
        private long _now;

        public Run()
        {
            ModA = Recorder.AddMod(new ModInfo("moda", "Mod A", "1.0", "com.test.moda"));
            ModB = Recorder.AddMod(new ModInfo("modb", "Mod B", "1.0", "com.test.modb"));
            ModC = Recorder.AddMod(new ModInfo("modc", "Mod C", "1.0", "com.test.modc"));
            Slow = Recorder.AddProbe(new ProbeInfo("A.PageTick", ProbeKind.Postfix, "Game.Tick", new[] { ModA }));
            Light = Recorder.AddProbe(new ProbeInfo("B.Update", ProbeKind.Ticker, "", new[] { ModB }));
            Quiet = Recorder.AddProbe(new ProbeInfo("C.Update", ProbeKind.Ticker, "", new[] { ModC }));
            GameCode = Recorder.AddProbe(new ProbeInfo("Game.Tick", ProbeKind.Game, "", new[] { Recorder.GameMod }));
            Recorder.Start(0);
            Recorder.EndFrame(0, default);
        }

        /// <summary>One frame of <paramref name="frameMs"/>, with Mod A's postfix taking <paramref name="slowMs"/> and Mod B a little.</summary>
        public void Frame(double frameMs, double slowMs = 0, double gameMs = 0)
        {
            var at = _now;

            Time(Light, ref at, 0.1);

            if (slowMs > 0) Time(Slow, ref at, slowMs);
            if (gameMs > 0) Time(GameCode, ref at, gameMs);

            _now += (long)(frameMs * TicksPerMs);
            Recorder.EndFrame(_now, new FrameContext(0, 0, default, "Main"));
        }

        private void Time(int probe, ref long at, double ms)
        {
            var token = Recorder.Begin(probe, at, 0);

            at += (long)(ms * TicksPerMs);
            Recorder.End(token, at, 0);
        }

        public double Seconds => Recorder.RecordedSeconds(_now);
    }

    /// <summary>60 normal frames, Mod A stutters at 60 and 90 ms, one game hitch of 70 ms with no mod busy.</summary>
    private static Run Stuttering()
    {
        var run = new Run();

        for (var i = 0; i < 60; i++) run.Frame(16);

        run.Frame(60, slowMs: 40);
        run.Frame(16);
        run.Frame(90, slowMs: 70);
        run.Frame(70);
        run.Frame(16);

        return run;
    }

    [Theory]
    [InlineData(0.01, 0.1, 1, 0, Rating.Fine)]
    [InlineData(0.3, 0.5, 2, 0, Rating.Watch)]
    [InlineData(0.01, 2.5, 3, 0, Rating.Watch)]
    [InlineData(0.01, 0.1, 30, 0, Rating.Watch)]
    [InlineData(0.01, 0.1, 10, 1, Rating.Watch)]
    [InlineData(1.2, 2, 3, 0, Rating.Heavy)]
    [InlineData(0.01, 0.1, 10, 3, Rating.Heavy)]
    [InlineData(0.01, 0.1, 55, 0, Rating.Heavy)]
    public void A_mod_is_rated_by_its_cost_per_frame_and_its_stutters(double avg, double p99, double worst, long caused, Rating expected)
    {
        Assert.Equal(expected, Analysis.Rate(avg, p99, worst, caused, 50));
    }

    [Fact]
    public void Stutter_makers_name_the_mod_its_count_and_the_method_that_did_it()
    {
        var run = Stuttering();
        var rows = Analysis.StutterMakers(run.Recorder, run.Seconds);

        var row = Assert.Single(rows);
        Assert.Equal(run.ModA, row.Id);
        Assert.Equal(2, row.CausedHitches);
        Assert.Equal(70, row.WorstMs, 3);
        Assert.Equal(55, row.TypicalMs, 3);
        Assert.Equal("A.PageTick postfix", row.Method);
        Assert.Equal(Rating.Heavy, row.Rating);
    }

    [Fact]
    public void The_verdict_lists_the_stutter_makers_with_their_method()
    {
        var run = Stuttering();
        var verdict = Analysis.Verdict(run.Recorder, run.Seconds);

        Assert.Contains(verdict, line => line.Contains("Stutters come from Mod A (2 stutters, worst 70 ms, in A.PageTick postfix)"));
    }

    [Fact]
    public void Hitches_with_the_same_cause_and_method_are_counted_together()
    {
        var run = Stuttering();
        var groups = Analysis.HitchGroups(run.Recorder);

        Assert.Equal(2, groups.Count);
        Assert.Equal("Mod A", groups[0].Cause);
        Assert.Equal("A.PageTick postfix (Mod A)", groups[0].Method);
        Assert.Equal(2, groups[0].Count);
        Assert.Equal(75, groups[0].TypicalMs, 3);
        Assert.Equal(90, groups[0].WorstMs, 3);
        Assert.Equal(2, groups[0].WorstNumber);
        Assert.Equal("", groups[1].Method);
        Assert.Equal(1, groups[1].Count);
    }

    [Fact]
    public void The_report_lists_the_worst_hitches_first_and_their_parts_include_the_analyzer()
    {
        var text = Report.Text(Stuttering().Recorder, Input());
        var worst = text.Substring(text.IndexOf("== Worst hitches", StringComparison.Ordinal));
        var lines = worst.Split('\n').Where(l => l.StartsWith("#", StringComparison.Ordinal)).ToList();

        Assert.StartsWith("#2 ", lines[0]);
        Assert.StartsWith("#3 ", lines[1]);
        Assert.StartsWith("#1 ", lines[2]);
        Assert.Contains("analyzer ", lines[0]);
        Assert.Contains("== Stutter makers", text);
        Assert.Contains("== Hitches by cause", text);
    }

    [Fact]
    public void Mods_with_no_measurable_time_are_folded_into_one_line_and_the_rest_are_rated()
    {
        var text = Report.Text(Stuttering().Recorder, Input());
        var mods = text.Substring(text.IndexOf("== Mods (", StringComparison.Ordinal));

        mods = mods.Substring(0, mods.IndexOf("\n== ", StringComparison.Ordinal));

        Assert.Contains("1 more with no measurable time: Mod C", mods);
        Assert.DoesNotContain("Mod C 1.0", mods);
        Assert.Contains("Mod A 1.0", mods);
        Assert.Matches(@"Mod A 1\.0\s+heavy", mods);
        Assert.Matches(@"Mod B 1\.0\s+fine", mods);
    }

    [Fact]
    public void Game_code_is_listed_apart_from_mod_methods()
    {
        var run = new Run();

        for (var i = 0; i < 40; i++) run.Frame(16, slowMs: 1, gameMs: 3);

        Assert.DoesNotContain(Analysis.MethodRows(run.Recorder, 10, side: MethodSide.Mods), r => r.Probe.Kind == ProbeKind.Game);
        Assert.All(Analysis.MethodRows(run.Recorder, 10, side: MethodSide.Game), r => Assert.Equal(ProbeKind.Game, r.Probe.Kind));
        Assert.Single(Analysis.MethodRows(run.Recorder, 10, side: MethodSide.Game));

        var text = Report.Text(run.Recorder, Input());
        var game = text.Substring(text.IndexOf("== Game code inside hooked methods", StringComparison.Ordinal));

        Assert.Contains("Game.Tick", game.Substring(0, game.IndexOf("\n== ", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(0, 10, "0")]
    [InlineData(25, 10, "2.5 KB/s")]
    [InlineData(250, 10, "25 KB/s")]
    [InlineData(20480, 10, "2.0 MB/s")]
    public void Allocations_read_as_a_rate(double kb, double seconds, string expected)
    {
        Assert.Equal(expected, Report.PerSecond(kb, seconds));
    }

    [Fact]
    public void The_panel_gets_stutter_makers_ratings_hitch_groups_and_the_analyzers_part()
    {
        var run = Stuttering();

        using (var mods = JsonDocument.Parse(Snapshot.Json(run.Recorder, Snap(PanelTab.Mods))))
        {
            var root = mods.RootElement;
            var stutter = root.GetProperty("stutters")[0];

            Assert.Equal("Mod A", stutter.GetProperty("name").GetString());
            Assert.Equal(2, stutter.GetProperty("caused").GetInt32());
            Assert.Equal("A.PageTick postfix", stutter.GetProperty("method").GetString());

            var c = root.GetProperty("mods").EnumerateArray().Single(m => m.GetProperty("name").GetString() == "Mod C");

            Assert.True(c.GetProperty("idle").GetBoolean());
            Assert.Equal("fine", c.GetProperty("rating").GetString());
        }

        using (var hitches = JsonDocument.Parse(Snapshot.Json(run.Recorder, Snap(PanelTab.Hitches))))
        {
            var root = hitches.RootElement;

            Assert.Equal(2, root.GetProperty("hitchGroups")[0].GetProperty("count").GetInt32());
            Assert.Equal(3, root.GetProperty("hitchesKept").GetInt32());
            Assert.True(root.GetProperty("hitches")[0].TryGetProperty("analyzer", out _));
        }
    }

    private static SnapshotInput Snap(PanelTab tab) => new(3_000_000, tab, false, InstrumentStatus.None, Array.Empty<ScriptRow>(), false, "0.4.0");

    private static ReportInput Input() => new(3_000_000, new DateTime(2026, 10, 26), "0.4.0", "1.1.19611", "", InstrumentStatus.None, Array.Empty<ScriptRow>(), "", Array.Empty<ModInfo>());
}
