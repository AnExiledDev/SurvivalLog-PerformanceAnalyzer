using System;
using System.Collections.Generic;

namespace PerformanceAnalyzer.Core;

/// <summary>The last N frames, oldest dropped first, with each mod's milliseconds per frame kept alongside.</summary>
public sealed class FrameRing
{
    private readonly FrameSample[] _frames;
    private readonly List<float[]> _modMs = new();
    private int _next;

    public FrameRing(int capacity)
    {
        Capacity = Math.Max(16, capacity);
        _frames = new FrameSample[Capacity];
    }

    public int Capacity { get; }

    public int Count { get; private set; }

    /// <summary>The i-th frame kept, 0 being the oldest.</summary>
    public FrameSample this[int i] => _frames[Slot(i)];

    public float ModMs(int mod, int i) => _modMs[mod][Slot(i)];

    public void AddSeries() => _modMs.Add(new float[Capacity]);

    public void Push(FrameSample sample, IReadOnlyList<double> modMs)
    {
        _frames[_next] = sample;

        for (var mod = 0; mod < _modMs.Count; mod++) _modMs[mod][_next] = mod < modMs.Count ? (float)modMs[mod] : 0;

        _next = (_next + 1) % Capacity;
        Count = Math.Min(Count + 1, Capacity);
    }

    public void Clear()
    {
        _next = 0;
        Count = 0;
    }

    public float[] Select(Func<FrameSample, float> pick)
    {
        var values = new float[Count];

        for (var i = 0; i < Count; i++) values[i] = pick(this[i]);

        return values;
    }

    public float[] ModSeries(int mod)
    {
        var values = new float[Count];

        for (var i = 0; i < Count; i++) values[i] = ModMs(mod, i);

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
        double sum = 0;

        Array.Sort(sorted);

        foreach (var value in sorted) sum += value;

        return new Spread(sorted.Length, sum / sorted.Length, Percentile(sorted, 0.50), Percentile(sorted, 0.95), Percentile(sorted, 0.99), sorted[^1]);
    }

    /// <summary>Nearest-rank percentile of already sorted values.</summary>
    public static double Percentile(float[] sorted, double fraction)
    {
        if (sorted.Length == 0) return 0;

        var rank = (int)Math.Ceiling(fraction * sorted.Length);

        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
