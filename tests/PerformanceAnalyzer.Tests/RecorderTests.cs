using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>
/// The recorder's arithmetic. One tick is one microsecond here, so 1000 ticks is a millisecond. Mods 0 and 1 are the
/// game and the analyzer; the tests add their own after those.
/// </summary>
public class RecorderTests
{
    private const double TicksPerMs = 1000;

    private static (Recorder Recorder, int ModA, int ModB) NewRecorder(double hitchMs = 50)
    {
        var recorder = new Recorder(TicksPerMs, 64, hitchMs);
        var a = recorder.AddMod(new ModInfo("moda", "Mod A", "1.0", "com.test.moda"));
        var b = recorder.AddMod(new ModInfo("modb", "Mod B", "1.0", "com.test.modb"));

        recorder.Start(0);
        recorder.EndFrame(0, default);

        return (recorder, a, b);
    }

    private static int Probe(Recorder recorder, string name, ProbeKind kind, params int[] owners) =>
        recorder.AddProbe(new ProbeInfo(name, kind, "", owners));

    private static long Ms(double ms) => (long)(ms * TicksPerMs);

    [Fact]
    public void Time_spent_in_a_nested_probe_is_the_inner_probes_self_time_not_the_outers()
    {
        var (recorder, a, b) = NewRecorder();
        var outer = Probe(recorder, "A.Prefix", ProbeKind.Prefix, a);
        var inner = Probe(recorder, "B.Postfix", ProbeKind.Postfix, b);

        var t1 = recorder.Begin(outer, Ms(0), 0);
        var t2 = recorder.Begin(inner, Ms(2), 0);
        recorder.End(t2, Ms(6), 0);
        recorder.End(t1, Ms(10), 0);

        Assert.Equal(Ms(6), recorder.ProbeTotals[outer].SelfTicks);
        Assert.Equal(Ms(10), recorder.ProbeTotals[outer].TotalTicks);
        Assert.Equal(Ms(4), recorder.ProbeTotals[inner].SelfTicks);
    }

    [Fact]
    public void A_probe_whose_method_threw_is_closed_when_the_probe_around_it_ends()
    {
        var (recorder, a, _) = NewRecorder();
        var outer = Probe(recorder, "Outer", ProbeKind.Ticker, a);
        var thrower = Probe(recorder, "Thrower", ProbeKind.ModMethod, a);

        var t1 = recorder.Begin(outer, Ms(0), 0);
        recorder.Begin(thrower, Ms(1), 0); // never ended: the method threw past its End
        recorder.End(t1, Ms(5), 0);

        Assert.Equal(1, recorder.ProbeTotals[thrower].Calls);
        Assert.Equal(Ms(4), recorder.ProbeTotals[thrower].SelfTicks);
        Assert.Equal(Ms(1), recorder.ProbeTotals[outer].SelfTicks);

        // The stack is empty again: a new probe nests at depth 0.
        Assert.Equal(0, recorder.Begin(outer, Ms(6), 0));
    }

    [Fact]
    public void A_recursive_probe_counts_its_total_once_and_its_self_time_in_full()
    {
        var (recorder, a, _) = NewRecorder();
        var p = Probe(recorder, "Recurse", ProbeKind.ModMethod, a);

        var t1 = recorder.Begin(p, Ms(0), 0);
        var t2 = recorder.Begin(p, Ms(1), 0);
        recorder.End(t2, Ms(3), 0);
        recorder.End(t1, Ms(4), 0);

        Assert.Equal(2, recorder.ProbeTotals[p].Calls);
        Assert.Equal(Ms(4), recorder.ProbeTotals[p].TotalTicks);
        Assert.Equal(Ms(4), recorder.ProbeTotals[p].SelfTicks);
    }

    [Fact]
    public void Allocations_inside_a_nested_probe_are_charged_to_it_alone()
    {
        var (recorder, a, b) = NewRecorder();
        var outer = Probe(recorder, "Outer", ProbeKind.Prefix, a);
        var inner = Probe(recorder, "Inner", ProbeKind.Prefix, b);

        var t1 = recorder.Begin(outer, 0, 1000);
        var t2 = recorder.Begin(inner, 1, 1100);
        recorder.End(t2, 2, 1600);
        recorder.End(t1, 3, 1700);

        Assert.Equal(500, recorder.ProbeTotals[inner].AllocBytes);
        Assert.Equal(200, recorder.ProbeTotals[outer].AllocBytes);
    }

    [Fact]
    public void Nothing_is_timed_while_not_recording()
    {
        var recorder = new Recorder(TicksPerMs, 16, 50);
        var p = recorder.AddProbe(new ProbeInfo("x", ProbeKind.Prefix, "", new[] { Recorder.AnalyzerMod }));

        Assert.Equal(Recorder.NotTimed, recorder.Begin(p, 0, 0));
    }

    [Fact]
    public void A_frame_splits_into_mods_hook_overhead_game_code_and_the_analyzer()
    {
        var (recorder, a, b) = NewRecorder();
        var hook = Probe(recorder, "Game.Update hook", ProbeKind.Hook, a, b);
        var prefix = Probe(recorder, "A.Prefix", ProbeKind.Prefix, a);
        var game = Probe(recorder, "Game.Update", ProbeKind.Game, Recorder.GameMod);
        var self = Probe(recorder, "Analyzer", ProbeKind.Analyzer, Recorder.AnalyzerMod);

        var h = recorder.Begin(hook, Ms(0), 0);
        recorder.End(recorder.Begin(prefix, Ms(1), 0), Ms(3), 0); // 2 ms of mod code
        recorder.End(recorder.Begin(game, Ms(3), 0), Ms(8), 0); // 5 ms of game code
        recorder.End(h, Ms(9), 0); // 2 ms of hook overhead around them
        recorder.End(recorder.Begin(self, Ms(9), 0), Ms(10), 0); // 1 ms of analyzer
        recorder.EndFrame(Ms(20), default);

        var sample = recorder.Ring[recorder.Ring.Count - 1];

        Assert.Equal(20, sample.Ms, 3);
        Assert.Equal(2, sample.ModsMs, 3);
        Assert.Equal(2, sample.HookMs, 3);
        Assert.Equal(5, sample.GameMs, 3);
        Assert.Equal(1, sample.AnalyzerMs, 3);
        Assert.Equal(10, sample.RestMs, 3);
    }

    [Fact]
    public void A_probe_shared_by_two_mods_charges_each_an_even_share()
    {
        var (recorder, a, b) = NewRecorder();
        var hook = Probe(recorder, "Shared hook", ProbeKind.Hook, a, b);

        recorder.End(recorder.Begin(hook, Ms(0), 0), Ms(4), 0);
        recorder.EndFrame(Ms(16), default);

        Assert.Equal(2, recorder.ModTotals[a].SelfMs, 3);
        Assert.Equal(2, recorder.ModTotals[b].SelfMs, 3);
        Assert.Equal(2, recorder.Ring.ModMs(a, recorder.Ring.Count - 1), 3);
    }

    [Fact]
    public void Probe_overhead_is_charged_to_the_analyzer_per_call()
    {
        var (recorder, a, _) = NewRecorder();
        var p = Probe(recorder, "Cheap", ProbeKind.Prefix, a);

        recorder.OverheadTicksPerCall = 100; // 0.1 ms per call

        for (var i = 0; i < 10; i++) recorder.End(recorder.Begin(p, Ms(i), 0), Ms(i), 0);

        recorder.EndFrame(Ms(16), default);

        Assert.Equal(1, recorder.Ring[recorder.Ring.Count - 1].AnalyzerMs, 3);
    }

    [Fact]
    public void A_slow_frame_is_recorded_as_a_hitch_with_the_mod_that_caused_it()
    {
        var (recorder, a, b) = NewRecorder(hitchMs: 50);
        var slow = Probe(recorder, "A.SlowUpdate", ProbeKind.Ticker, a);
        var quick = Probe(recorder, "B.Quick", ProbeKind.Ticker, b);

        recorder.End(recorder.Begin(quick, Ms(0), 0), Ms(1), 0);
        recorder.End(recorder.Begin(slow, Ms(1), 0), Ms(61), 0);
        recorder.EndFrame(Ms(70), new FrameContext(0, 0, default, "Battle"));

        var hitch = Assert.Single(recorder.Hitches);

        Assert.Equal(slow, hitch.TopProbes[0].Id);
        Assert.Equal(a, hitch.TopMods[0].Id);
        Assert.Equal("Battle", hitch.Scene);
        Assert.Equal("Mod A", Analysis.Cause(recorder, hitch));
        Assert.Equal(1, recorder.ModTotals[a].HitchFrames);
    }

    [Fact]
    public void A_slow_frame_no_mod_explains_and_a_game_collection_ran_in_is_blamed_on_the_collection()
    {
        var (recorder, _, _) = NewRecorder(hitchMs: 50);

        recorder.EndFrame(Ms(80), new FrameContext(0, 1, default, ""));

        Assert.Equal("the game's garbage collection", Analysis.Cause(recorder, Assert.Single(recorder.Hitches)));
    }

    [Fact]
    public void A_fast_frame_is_not_a_hitch()
    {
        var (recorder, _, _) = NewRecorder(hitchMs: 50);

        recorder.EndFrame(Ms(49), default);

        Assert.Empty(recorder.Hitches);
    }

    [Fact]
    public void Reset_forgets_every_number_but_keeps_the_probes()
    {
        var (recorder, a, _) = NewRecorder();
        var p = Probe(recorder, "x", ProbeKind.Prefix, a);

        recorder.End(recorder.Begin(p, Ms(0), 0), Ms(5), 0);
        recorder.EndFrame(Ms(100), default);
        recorder.Reset(Ms(100));

        Assert.Equal(0, recorder.FrameCount);
        Assert.Equal(0, recorder.ProbeTotals[p].Calls);
        Assert.Empty(recorder.Hitches);
        Assert.Equal(0, recorder.Ring.Count);
        Assert.Equal("x", recorder.Probes[p].Name);
    }

    [Fact]
    public void A_probe_needs_an_owner_the_recorder_knows()
    {
        var recorder = new Recorder(TicksPerMs, 16, 50);

        Assert.Throws<ArgumentException>(() => recorder.AddProbe(new ProbeInfo("x", ProbeKind.Prefix, "", Array.Empty<int>())));
        Assert.Throws<ArgumentOutOfRangeException>(() => recorder.AddProbe(new ProbeInfo("x", ProbeKind.Prefix, "", new[] { 99 })));
    }

    [Fact]
    public void Off_thread_calls_are_counted_per_probe()
    {
        var (recorder, a, _) = NewRecorder();
        var p = Probe(recorder, "x", ProbeKind.Prefix, a);

        Parallel.For(0, 1000, _ => recorder.CountOffThread(p));

        Assert.Equal(1000, recorder.OffThreadCalls(p));
    }
}
