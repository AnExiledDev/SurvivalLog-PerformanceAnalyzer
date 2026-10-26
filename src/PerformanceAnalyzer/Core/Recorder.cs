using System;
using System.Collections.Generic;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// Times nested probes on the main thread and folds them into frames, totals and hitches. Every clock and allocation
/// reading is passed in, so this class reads no clock of its own.
/// <para>
/// A probe's total time runs from its Begin to its End. Its self time is its total minus the total of the probes that
/// ran inside it, so a mod's prefix that calls into the game is not charged for a hooked game method that has its own
/// probe. A probe that never ends (its method threw) is closed when an outer probe ends, at that time.
/// </para>
/// </summary>
public sealed class Recorder
{
    /// <summary>Mod id 0: the game itself, charged with the game's own code inside hooked methods.</summary>
    public const int GameMod = 0;

    /// <summary>Mod id 1: this mod, charged with its own work.</summary>
    public const int AnalyzerMod = 1;

    public const int NotTimed = -1;

    private const int MaxDepth = 256;
    private const int MaxHitches = 200;
    private const int HitchTopCount = 8;

    private readonly double _ticksPerMs;
    private readonly List<ProbeInfo> _probes = new();
    private readonly List<ModInfo> _mods = new();
    private readonly List<ProbeTotals> _probeTotals = new();
    private readonly List<ModTotals> _modTotals = new();

    // Per-frame accumulators, indexed by probe or mod id, cleared through the touched lists at the end of a frame.
    private readonly List<long> _frameSelf = new();
    private readonly List<long> _frameAlloc = new();
    private readonly List<int> _frameCalls = new();
    private readonly List<int> _active = new();
    private readonly List<int> _touched = new();
    private readonly List<double> _modFrameMs = new();
    private readonly List<long> _modFrameAlloc = new();

    private readonly Frame[] _stack = new Frame[MaxDepth];
    private int _depth;
    private int _overflow;

    private readonly Dictionary<int, long> _offThreadCalls = new();
    private readonly List<Hitch> _hitches = new();

    private FrameRing _ring;
    private long _lastFrameEnd;
    private bool _hasFrameStart;
    private long _recordStart;
    private double _stoppedSeconds;
    private int _frameCallCount;

    public Recorder(double ticksPerMs, int windowFrames, double hitchMs)
    {
        _ticksPerMs = ticksPerMs;
        _ring = new FrameRing(windowFrames);
        HitchMs = hitchMs;
        AddMod(new ModInfo("game", "Game", "", ""));
        AddMod(new ModInfo("perfanalyzer", "Performance Analyzer", "", ""));
    }

    public bool IsRecording { get; private set; }

    public double HitchMs { get; set; }

    /// <summary>Measured cost of one Begin/End pair in ticks; charged to the analyzer rather than to the probe's frame.</summary>
    public double OverheadTicksPerCall { get; set; }

    public double TicksPerMs => _ticksPerMs;

    public IReadOnlyList<ProbeInfo> Probes => _probes;

    public IReadOnlyList<ModInfo> Mods => _mods;

    public IReadOnlyList<ProbeTotals> ProbeTotals => _probeTotals;

    public IReadOnlyList<ModTotals> ModTotals => _modTotals;

    public IReadOnlyList<Hitch> Hitches => _hitches;

    public FrameRing Ring => _ring;

    public long FrameCount { get; private set; }

    public long HitchCount { get; private set; }

    /// <summary>Probes whose depth went past the stack limit; their time lands on the deepest probe that fit.</summary>
    public int Overflow => _overflow;

    public double RecordedSeconds(long now) => IsRecording ? (now - _recordStart) / _ticksPerMs / 1000 : _stoppedSeconds;

    public int AddMod(ModInfo mod)
    {
        _mods.Add(mod);
        _modTotals.Add(new ModTotals());
        _modFrameMs.Add(0);
        _modFrameAlloc.Add(0);
        _ring.AddSeries();

        return _mods.Count - 1;
    }

    public int AddProbe(ProbeInfo probe)
    {
        if (probe.Owners.Length == 0) throw new ArgumentException("a probe needs at least one owner", nameof(probe));

        foreach (var owner in probe.Owners)
        {
            if (owner < 0 || owner >= _mods.Count) throw new ArgumentOutOfRangeException(nameof(probe), "unknown owner mod " + owner);
        }

        _probes.Add(probe);
        _probeTotals.Add(new ProbeTotals());
        _frameSelf.Add(0);
        _frameAlloc.Add(0);
        _frameCalls.Add(0);
        _active.Add(0);

        return _probes.Count - 1;
    }

    public void Start(long now)
    {
        if (IsRecording) return;

        IsRecording = true;
        _recordStart = now - (long)(_stoppedSeconds * 1000 * _ticksPerMs);
        _hasFrameStart = false;
        DropStack();
    }

    public void Stop(long now)
    {
        if (!IsRecording) return;

        _stoppedSeconds = RecordedSeconds(now);
        IsRecording = false;
        DropStack();
        ClearFrame();
    }

    /// <summary>Forgets everything recorded; probes and mods stay registered.</summary>
    public void Reset(long now)
    {
        foreach (var totals in _probeTotals) totals.Clear();
        foreach (var totals in _modTotals) totals.Clear();

        _hitches.Clear();
        _ring.Clear();
        _offThreadCalls.Clear();
        DropStack();
        _overflow = 0;
        _stoppedSeconds = 0;
        _recordStart = now;
        _hasFrameStart = false;
        FrameCount = 0;
        HitchCount = 0;
        ClearFrame();
    }

    /// <summary>Resizes the frame window; the frames kept so far are dropped.</summary>
    public void SetWindow(int windowFrames)
    {
        if (windowFrames == _ring.Capacity) return;

        _ring = new FrameRing(windowFrames);

        for (var i = 0; i < _mods.Count; i++) _ring.AddSeries();
    }

    /// <returns>A token for <see cref="End"/>, or <see cref="NotTimed"/> when not recording.</returns>
    public int Begin(int probe, long now, long allocated)
    {
        if (!IsRecording || (uint)probe >= (uint)_probes.Count) return NotTimed;

        if (_depth == MaxDepth)
        {
            _overflow++;

            return NotTimed;
        }

        _stack[_depth] = new Frame(probe, now, allocated);
        _active[probe]++;
        _frameCallCount++;

        return _depth++;
    }

    public void End(int token, long now, long allocated)
    {
        if (token < 0 || token >= _depth) return;

        while (_depth > token + 1) Close(now, allocated);

        Close(now, allocated);
    }

    /// <summary>A probe hit off the main thread: counted, not timed.</summary>
    public void CountOffThread(int probe)
    {
        lock (_offThreadCalls)
        {
            _offThreadCalls.TryGetValue(probe, out var calls);
            _offThreadCalls[probe] = calls + 1;
        }
    }

    public long OffThreadCalls(int probe)
    {
        lock (_offThreadCalls) return _offThreadCalls.TryGetValue(probe, out var calls) ? calls : 0;
    }

    /// <summary>Ends a frame at <paramref name="now"/>: the time since the previous call is the frame's length.</summary>
    public void EndFrame(long now, FrameContext context)
    {
        if (!IsRecording) return;

        if (!_hasFrameStart)
        {
            _hasFrameStart = true;
            _lastFrameEnd = now;
            ClearFrame();

            return;
        }

        var frameMs = (now - _lastFrameEnd) / _ticksPerMs;
        var split = FoldFrame(frameMs);
        var sample = new FrameSample((float)frameMs, (float)split.ModsMs, (float)split.HookMs, (float)split.GameMs, (float)split.AnalyzerMs, context.ManagedGcs, context.GameGcs, context.Engine);

        _lastFrameEnd = now;
        FrameCount++;
        _ring.Push(sample, _modFrameMs);

        if (frameMs >= HitchMs) RecordHitch(now, sample, context);

        ClearFrame();
    }

    private void Close(long now, long allocated)
    {
        var frame = _stack[--_depth];
        var total = now - frame.Start;
        var self = total - frame.ChildTicks;
        var alloc = allocated - frame.StartAlloc;
        var selfAlloc = alloc - frame.ChildAlloc;
        var probe = frame.Probe;
        var totals = _probeTotals[probe];

        if (_frameCalls[probe] == 0) _touched.Add(probe);

        _active[probe]--;
        _frameCalls[probe]++;
        _frameSelf[probe] += self;
        _frameAlloc[probe] += selfAlloc;
        totals.Calls++;
        totals.SelfTicks += self;
        totals.AllocBytes += selfAlloc;

        // A probe inside itself (recursion) adds its total once, at the outermost call.
        if (_active[probe] == 0) totals.TotalTicks += total;

        if (total > totals.MaxCallTicks) totals.MaxCallTicks = total;

        if (_depth == 0) return;

        _stack[_depth - 1].ChildTicks += total;
        _stack[_depth - 1].ChildAlloc += alloc;
    }

    private FrameSplit FoldFrame(double frameMs)
    {
        var split = new FrameSplit { AnalyzerMs = _frameCallCount * OverheadTicksPerCall / _ticksPerMs };

        foreach (var probe in _touched)
        {
            var info = _probes[probe];
            var selfMs = _frameSelf[probe] / _ticksPerMs;
            var totals = _probeTotals[probe];
            var share = selfMs / info.Owners.Length;
            var shareAlloc = _frameAlloc[probe] / info.Owners.Length;

            totals.Frames++;

            if (selfMs > totals.WorstFrameMs) totals.WorstFrameMs = selfMs;

            switch (info.Kind)
            {
                case ProbeKind.Hook:
                    split.HookMs += selfMs;
                    break;
                case ProbeKind.Game:
                    split.GameMs += selfMs;
                    break;
                case ProbeKind.Analyzer:
                    split.AnalyzerMs += selfMs;
                    break;
                default:
                    split.ModsMs += selfMs;
                    break;
            }

            foreach (var owner in info.Owners)
            {
                _modFrameMs[owner] += share;
                _modFrameAlloc[owner] += shareAlloc;
            }
        }

        for (var mod = 0; mod < _mods.Count; mod++)
        {
            var ms = _modFrameMs[mod];

            if (ms <= 0 && _modFrameAlloc[mod] == 0) continue;

            var totals = _modTotals[mod];

            totals.SelfMs += ms;
            totals.AllocBytes += _modFrameAlloc[mod];
            totals.Frames++;

            if (ms > totals.WorstFrameMs) totals.WorstFrameMs = ms;
            if (frameMs >= HitchMs) totals.HitchFrames++;
        }

        return split;
    }

    private void RecordHitch(long now, FrameSample sample, FrameContext context)
    {
        var probes = new List<HitchEntry>();
        var mods = new List<HitchEntry>();

        foreach (var probe in _touched) probes.Add(new HitchEntry(probe, _frameSelf[probe] / _ticksPerMs, _frameCalls[probe]));

        for (var mod = 0; mod < _mods.Count; mod++)
        {
            if (_modFrameMs[mod] > 0) mods.Add(new HitchEntry(mod, _modFrameMs[mod], 0));
        }

        probes.Sort(ByMsDescending);
        mods.Sort(ByMsDescending);

        if (probes.Count > HitchTopCount) probes.RemoveRange(HitchTopCount, probes.Count - HitchTopCount);
        if (mods.Count > HitchTopCount) mods.RemoveRange(HitchTopCount, mods.Count - HitchTopCount);

        HitchCount++;

        if (_hitches.Count == MaxHitches) _hitches.RemoveAt(0);

        _hitches.Add(new Hitch(HitchCount, (now - _recordStart) / _ticksPerMs / 1000, FrameCount, sample, probes, mods, context.Scene));
    }

    /// <summary>Forgets probes still open; an End for one of them later is ignored, since its token is past the new depth or reused.</summary>
    private void DropStack()
    {
        for (var i = 0; i < _depth; i++) _active[_stack[i].Probe]--;

        _depth = 0;
    }

    private static int ByMsDescending(HitchEntry a, HitchEntry b) => b.Ms.CompareTo(a.Ms);

    private void ClearFrame()
    {
        foreach (var probe in _touched)
        {
            _frameSelf[probe] = 0;
            _frameAlloc[probe] = 0;
            _frameCalls[probe] = 0;
        }

        _touched.Clear();

        for (var mod = 0; mod < _modFrameMs.Count; mod++)
        {
            _modFrameMs[mod] = 0;
            _modFrameAlloc[mod] = 0;
        }

        _frameCallCount = 0;
    }

    private struct Frame
    {
        public readonly int Probe;
        public readonly long Start;
        public readonly long StartAlloc;
        public long ChildTicks;
        public long ChildAlloc;

        public Frame(int probe, long start, long startAlloc)
        {
            Probe = probe;
            Start = start;
            StartAlloc = startAlloc;
            ChildTicks = 0;
            ChildAlloc = 0;
        }
    }

    private struct FrameSplit
    {
        public double ModsMs;
        public double HookMs;
        public double GameMs;
        public double AnalyzerMs;
    }
}

public sealed class ProbeTotals
{
    public long Calls;
    public long SelfTicks;
    public long TotalTicks;
    public long AllocBytes;
    public long MaxCallTicks;
    public long Frames;
    public double WorstFrameMs;

    public void Clear()
    {
        Calls = SelfTicks = TotalTicks = AllocBytes = MaxCallTicks = Frames = 0;
        WorstFrameMs = 0;
    }
}

public sealed class ModTotals
{
    public double SelfMs;
    public long AllocBytes;
    public long Frames;
    public long HitchFrames;
    public double WorstFrameMs;

    public void Clear()
    {
        SelfMs = WorstFrameMs = 0;
        AllocBytes = Frames = HitchFrames = 0;
    }
}

/// <summary>What the adapter knows about a frame that the probes do not: garbage collections and the engine's own timings.</summary>
/// <param name="ManagedGcs">Collections by the mods' .NET runtime during the frame.</param>
/// <param name="GameGcs">Collections by the game's own runtime during the frame.</param>
/// <param name="Engine">The engine's frame timings, when the game has them turned on.</param>
/// <param name="Scene">Where the player was, for a hitch ("home", "map", "menu").</param>
public readonly record struct FrameContext(short ManagedGcs, short GameGcs, EngineTimes Engine, string Scene);

/// <summary>Milliseconds from the engine's frame timing, all zero when the game does not collect them.</summary>
public readonly record struct EngineTimes(float MainThreadMs, float RenderThreadMs, float GpuMs);

public readonly record struct FrameSample(float Ms, float ModsMs, float HookMs, float GameMs, float AnalyzerMs, short ManagedGcs, short GameGcs, EngineTimes Engine)
{
    /// <summary>Time no probe saw: the game's own work outside hooked methods, rendering and waiting.</summary>
    public float RestMs => Math.Max(0, Ms - ModsMs - HookMs - GameMs - AnalyzerMs);
}

/// <param name="Id">Probe id for a probe entry, mod id for a mod entry.</param>
public readonly record struct HitchEntry(int Id, double Ms, int Calls);

/// <param name="Number">Counts from 1 since the last reset, and keeps counting when old hitches are dropped.</param>
/// <param name="AtSeconds">Seconds into the recording.</param>
public sealed record Hitch(long Number, double AtSeconds, long Frame, FrameSample Sample, List<HitchEntry> TopProbes, List<HitchEntry> TopMods, string Scene);
