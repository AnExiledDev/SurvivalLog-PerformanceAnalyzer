using System;
using System.Collections.Generic;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// The last N frames, oldest dropped first, with each mod's milliseconds per frame kept alongside. Each mod's series also
/// keeps a running histogram, so its 99th percentile costs no sort and no allocation when the panel asks for it.
/// </summary>
public sealed class FrameRing
{
    // Histogram buckets cut a float's bits after the exponent and the top 4 mantissa bits: each bucket is 1/16 of a
    // power of two wide, so a percentile read from it is at most about 6% under the true value.
    private const int BucketShift = 19;
    private static readonly int LowBits = BitConverter.SingleToInt32Bits(0.001f) >> BucketShift;
    private static readonly int Buckets = (BitConverter.SingleToInt32Bits(100_000f) >> BucketShift) - LowBits + 2;

    private readonly FrameSample[] _frames;
    private readonly List<float[]> _modMs = new();
    private readonly List<int[]> _modHistograms = new();
    private readonly float[] _sorted;
    private int _next;
    private long _version;
    private long _spreadVersion = -1;
    private Spread _spread;

    public FrameRing(int capacity)
    {
        Capacity = Math.Max(16, capacity);
        _frames = new FrameSample[Capacity];
        _sorted = new float[Capacity];
    }

    public int Capacity { get; }

    public int Count { get; private set; }

    /// <summary>The i-th frame kept, 0 being the oldest.</summary>
    public FrameSample this[int i] => _frames[Slot(i)];

    public float ModMs(int mod, int i) => _modMs[mod][Slot(i)];

    public void AddSeries()
    {
        var histogram = new int[Buckets];

        // The frames already kept count as zero for the new mod.
        histogram[0] = Count;
        _modMs.Add(new float[Capacity]);
        _modHistograms.Add(histogram);
    }

    public void Push(FrameSample sample, IReadOnlyList<double> modMs)
    {
        var full = Count == Capacity;

        _frames[_next] = sample;

        for (var mod = 0; mod < _modMs.Count; mod++)
        {
            var series = _modMs[mod];
            var histogram = _modHistograms[mod];
            var value = mod < modMs.Count ? (float)modMs[mod] : 0;

            if (full) histogram[Bucket(series[_next])]--;

            series[_next] = value;
            histogram[Bucket(value)]++;
        }

        _next = (_next + 1) % Capacity;
        Count = Math.Min(Count + 1, Capacity);
        _version++;
    }

    public void Clear()
    {
        _next = 0;
        Count = 0;
        _version++;

        foreach (var histogram in _modHistograms) Array.Clear(histogram);
    }

    /// <summary>Frame times over the window, worked out once per new frame and kept until the next one.</summary>
    public Spread FrameSpread()
    {
        if (_spreadVersion == _version) return _spread;

        for (var i = 0; i < Count; i++) _sorted[i] = this[i].Ms;

        Array.Sort(_sorted, 0, Count);
        _spread = Spread.OfSorted(_sorted, Count);
        _spreadVersion = _version;

        return _spread;
    }

    /// <summary>A mod's 99th percentile milliseconds per frame over the window, rounded down to its histogram bucket.</summary>
    public double ModP99(int mod)
    {
        if (Count == 0) return 0;

        var histogram = _modHistograms[mod];
        var rank = (int)Math.Ceiling(0.99 * Count);
        var seen = 0;

        for (var bucket = 0; bucket < histogram.Length; bucket++)
        {
            seen += histogram[bucket];

            if (seen >= rank) return BucketFloor(bucket);
        }

        return BucketFloor(histogram.Length - 1);
    }

    private static int Bucket(float ms)
    {
        if (!(ms > 0)) return 0;

        return Math.Clamp((BitConverter.SingleToInt32Bits(ms) >> BucketShift) - LowBits + 1, 1, Buckets - 1);
    }

    private static double BucketFloor(int bucket) => bucket == 0 ? 0 : BitConverter.Int32BitsToSingle((bucket - 1 + LowBits) << BucketShift);

    public float[] Select(Func<FrameSample, float> pick)
    {
        var values = new float[Count];

        for (var i = 0; i < Count; i++) values[i] = pick(this[i]);

        return values;
    }

    private int Slot(int i)
    {
        if ((uint)i >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(i));

        return (_next - Count + i + Capacity) % Capacity;
    }
}

/// <summary>Summary numbers over a set of frame times.</summary>
public readonly record struct Spread(int Count, double Mean, double P50, double P95, double P99, double Max)
{
    public static readonly Spread Empty = new(0, 0, 0, 0, 0, 0);

    public static Spread Of(float[] values)
    {
        if (values.Length == 0) return Empty;

        var sorted = (float[])values.Clone();

        Array.Sort(sorted);

        return OfSorted(sorted, sorted.Length);
    }

    /// <summary>The spread of the first <paramref name="count"/> values of an already sorted array.</summary>
    public static Spread OfSorted(float[] sorted, int count)
    {
        if (count == 0) return Empty;

        double sum = 0;

        for (var i = 0; i < count; i++) sum += sorted[i];

        return new Spread(count, sum / count, Percentile(sorted, count, 0.50), Percentile(sorted, count, 0.95), Percentile(sorted, count, 0.99), sorted[count - 1]);
    }

    /// <summary>Nearest-rank percentile of already sorted values.</summary>
    public static double Percentile(float[] sorted, double fraction) => Percentile(sorted, sorted.Length, fraction);

    private static double Percentile(float[] sorted, int count, double fraction)
    {
        if (count == 0) return 0;

        var rank = (int)Math.Ceiling(fraction * count);

        return sorted[Math.Clamp(rank - 1, 0, count - 1)];
    }
}
