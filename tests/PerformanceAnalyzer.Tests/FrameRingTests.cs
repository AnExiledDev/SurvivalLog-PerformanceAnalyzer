using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

public class FrameRingTests
{
    private static FrameSample Frame(float ms) => new(ms, 0, 0, 0, 0, 0, 0, default);

    [Fact]
    public void A_full_ring_keeps_the_newest_frames_oldest_first()
    {
        var ring = new FrameRing(16);

        ring.AddSeries();

        for (var i = 0; i < 20; i++) ring.Push(Frame(i), new double[] { i * 10 });

        Assert.Equal(16, ring.Count);
        Assert.Equal(4, ring[0].Ms);
        Assert.Equal(19, ring[15].Ms);
        Assert.Equal(190, ring.ModMs(0, 15));
    }

    [Fact]
    public void A_mods_99th_percentile_is_the_slow_frame_once_in_a_hundred_and_rounds_down_to_within_6_percent()
    {
        var ring = new FrameRing(100);

        ring.AddSeries();

        for (var i = 0; i < 98; i++) ring.Push(Frame(16), new double[] { 0.5 });

        ring.Push(Frame(16), new double[] { 30 });
        ring.Push(Frame(16), new double[] { 40 });

        // Nearest rank 99 of 100 is the 30 ms frame, not the 0.5 ms ones.
        Assert.InRange(ring.ModP99(0), 30 * 0.94, 30);
    }

    [Fact]
    public void A_mods_99th_percentile_forgets_frames_that_left_the_window()
    {
        var ring = new FrameRing(100);

        ring.AddSeries();

        for (var i = 0; i < 100; i++) ring.Push(Frame(16), new double[] { 40 });
        for (var i = 0; i < 100; i++) ring.Push(Frame(16), new double[] { i < 99 ? 0 : 2 });

        Assert.Equal(0, ring.ModP99(0));

        ring.Push(Frame(16), new double[] { 2 });

        Assert.InRange(ring.ModP99(0), 2 * 0.94, 2);

        ring.Clear();

        Assert.Equal(0, ring.ModP99(0));
        ring.Push(Frame(16), new double[] { 8 });
        Assert.Equal(8, ring.ModP99(0));
    }

    [Fact]
    public void A_mod_added_after_frames_were_kept_counts_those_frames_as_zero()
    {
        var ring = new FrameRing(100);

        for (var i = 0; i < 99; i++) ring.Push(Frame(16), Array.Empty<double>());

        ring.AddSeries();
        ring.Push(Frame(16), new double[] { 50 });

        Assert.Equal(0, ring.ModP99(0));
        Assert.Equal(100, ring.Count);
    }

    [Fact]
    public void The_frame_spread_follows_new_frames_and_matches_a_full_sort()
    {
        var ring = new FrameRing(16);

        for (var i = 1; i <= 10; i++) ring.Push(Frame(i), Array.Empty<double>());

        Assert.Equal(Spread.Of(Enumerable.Range(1, 10).Select(i => (float)i).ToArray()), ring.FrameSpread());

        ring.Push(Frame(100), Array.Empty<double>());

        Assert.Equal(100, ring.FrameSpread().Max);
        Assert.Equal(11, ring.FrameSpread().Count);

        ring.Clear();

        Assert.Equal(0, ring.FrameSpread().Count);
    }

    [Fact]
    public void Percentiles_are_nearest_rank()
    {
        var values = Enumerable.Range(1, 100).Select(i => (float)i).Reverse().ToArray();
        var spread = Spread.Of(values);

        Assert.Equal(50, spread.P50);
        Assert.Equal(95, spread.P95);
        Assert.Equal(99, spread.P99);
        Assert.Equal(100, spread.Max);
        Assert.Equal(50.5, spread.Mean, 6);
    }

    [Fact]
    public void One_slow_frame_in_a_hundred_shows_at_the_99th_percentile_and_not_the_median()
    {
        var values = Enumerable.Repeat(10f, 99).Append(200f).ToArray();
        var spread = Spread.Of(values);

        Assert.Equal(10, spread.P50);
        Assert.Equal(10, spread.P99);
        Assert.Equal(200, spread.Max);

        var two = Enumerable.Repeat(10f, 98).Append(200f).Append(200f).ToArray();

        Assert.Equal(200, Spread.Of(two).P99);
    }

    [Fact]
    public void No_frames_give_zeros()
    {
        Assert.Equal(Spread.Empty, Spread.Of(Array.Empty<float>()));
    }
}
