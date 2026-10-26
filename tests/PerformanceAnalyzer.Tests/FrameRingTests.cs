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
