using System.Reflection;
using PerformanceAnalyzer.Core;
using PerformanceMarkers;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>Markers: sections of their own code other mods ask to have timed.</summary>
public class MarkerTests
{
    private const double TicksPerMs = 1000;

    private static readonly Assembly ModAssembly = typeof(MarkerTests).Assembly;
    private static readonly Assembly OtherAssembly = typeof(object).Assembly;

    private static long Ms(double ms) => (long)(ms * TicksPerMs);

    private static (Recorder Recorder, int ModA, int ModB) NewRecorder()
    {
        var recorder = new Recorder(TicksPerMs, 64, 50);
        var a = recorder.AddMod(new ModInfo("moda", "Mod A", "1.0", "com.test.moda"));
        var b = recorder.AddMod(new ModInfo("modb", "Mod B", "1.0", "com.test.modb"));

        recorder.Start(0);
        recorder.EndFrame(0, default);

        return (recorder, a, b);
    }

    [Fact]
    public void The_same_name_from_the_same_mod_is_one_marker_and_from_another_mod_is_another()
    {
        var registry = new MarkerRegistry();
        var first = registry.Register(ModAssembly, "Crop scan");

        Assert.Equal(first, registry.Register(ModAssembly, "  Crop scan "));
        Assert.NotEqual(first, registry.Register(OtherAssembly, "Crop scan"));
        Assert.NotEqual(first, registry.Register(ModAssembly, "Crop growth"));
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void A_marker_is_untimed_until_drained_then_charged_to_the_mod_that_owns_it()
    {
        var (recorder, a, b) = NewRecorder();
        var registry = new MarkerRegistry();
        var mine = registry.Register(ModAssembly, "Crop scan");
        var theirs = registry.Register(OtherAssembly, "Path cache");

        Assert.Equal(Recorder.NotTimed, registry.ProbeOf(mine));
        Assert.Equal(2, registry.Drain(recorder, assembly => assembly == ModAssembly ? a : b));
        Assert.Equal(0, registry.Drain(recorder, _ => a));

        var probe = recorder.Probes[registry.ProbeOf(mine)];

        Assert.Equal("Crop scan", probe.Name);
        Assert.Equal(ProbeKind.Marker, probe.Kind);
        Assert.Equal(new[] { a }, probe.Owners);
        Assert.Equal(new[] { b }, recorder.Probes[registry.ProbeOf(theirs)].Owners);
        Assert.Equal("marker", ProbeKinds.Label(probe.Kind));
    }

    [Fact]
    public void Names_are_plain_ascii_cut_to_length_and_never_empty()
    {
        Assert.Equal("unnamed section", MarkerRegistry.Clean("   "));
        Assert.Equal("unnamed section", MarkerRegistry.Clean(null));
        Assert.Equal("Crop ?? scan", MarkerRegistry.Clean("Crop 作物 scan"));
        Assert.Equal(MarkerRegistry.MaxNameLength, MarkerRegistry.Clean(new string('x', 500)).Length);
    }

    [Fact]
    public void A_marker_in_a_mods_own_code_is_timed_through_the_copied_file_and_taken_out_of_the_method_around_it()
    {
        var (recorder, a, _) = NewRecorder();
        var ticker = recorder.AddProbe(new ProbeInfo("A.Update", ProbeKind.Ticker, "", new[] { a }));
        long clock = 0;
        var marker = new PerfMarker("Shim crop scan");

        Markers.Attach(probe => recorder.Begin(probe, clock, 0), token => recorder.End(token, clock, 0));

        // The first use registers it; it is timed once the analyzer has drained it on the main thread.
        using (marker.Time()) clock = Ms(1);

        Markers.Registry.Drain(recorder, assembly => assembly == ModAssembly ? a : Recorder.GameMod);

        clock = Ms(10);
        var outer = recorder.Begin(ticker, clock, 0);

        clock = Ms(11);

        using (marker.Time()) clock = Ms(14);

        clock = Ms(15);
        recorder.End(outer, clock, 0);
        recorder.EndFrame(Ms(20), default);

        var rows = Analysis.MethodRows(recorder, 10);
        var section = Assert.Single(rows, r => r.Probe.Kind == ProbeKind.Marker);

        Assert.Equal("Shim crop scan", section.Probe.Name);
        Assert.Equal(1, section.Calls);
        Assert.Equal(3, section.SelfMs, 6);
        Assert.Equal(2, Assert.Single(rows, r => r.Id == ticker).SelfMs, 6);
        Assert.Equal(5, recorder.ModTotals[a].SelfMs, 6);
    }
}
