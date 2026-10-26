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
    private const int MaxSlowMessages = 50;
    private const int MaxPages = 200;

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
    private readonly Dictionary<string, long> _hitchCauses = new(StringComparer.Ordinal);
    private readonly List<SlowMessage> _slowMessages = new();
    private readonly List<string> _pageNames = new() { "" };
    private readonly Dictionary<string, short> _pageIds = new(StringComparer.Ordinal) { [""] = 0 };
    private short _page;

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

    /// <summary>Every hitch since the last reset by its most likely cause, including the ones no longer kept in <see cref="Hitches"/>.</summary>
    public IReadOnlyDictionary<string, long> HitchCauses => _hitchCauses;

    public FrameRing Ring => _ring;

    /// <summary>The game page that was showing ("hud", "PhoneSMS"), as the web probe last reported it; "" before it did.</summary>
    public string Page => _pageNames[_page];

    /// <summary>Slow calls of the game's web message handler, the last <see cref="MaxSlowMessages"/>.</summary>
    public IReadOnlyList<SlowMessage> SlowMessages => _slowMessages;

    public long SlowMessageCount { get; private set; }

    /// <summary>The game side: logic updates, game speed and clock, memory over time, frame times per scene.</summary>
    public GameStats Game { get; } = new();

    public long FrameCount { get; private set; }

    public long HitchCount { get; private set; }

    /// <summary>Probes whose depth went past the stack limit; their time lands on the deepest probe that fit.</summary>
    public int Overflow => _overflow;

    public double RecordedSeconds(long now) => IsRecording ? (now - _recordStart) / _ticksPerMs / 1000 : _stoppedSeconds;

    /// <summary>The page frames are marked with from now on. Past <see cref="MaxPages"/> names, new ones mark as "other".</summary>
    public void SetPage(string? page)
    {
        page ??= "";

        if (!_pageIds.TryGetValue(page, out var id))
        {
            if (_pageNames.Count >= MaxPages) page = "other";

            if (!_pageIds.TryGetValue(page, out id))
            {
                id = (short)_pageNames.Count;
                _pageNames.Add(page);
                _pageIds[page] = id;
            }
        }

        _page = id;
    }

    /// <summary>The name a frame's <see cref="FrameSample.Page"/> stands for.</summary>
    public string PageName(short id) => (uint)id < (uint)_pageNames.Count ? _pageNames[id] : "";

    /// <summary>
    /// One call of the game's web message handler that took <paramref name="ms"/>, in the frame being recorded. Only
    /// slow calls are passed in, so the text is kept short: the message's command and its first 80 characters.
    /// </summary>
    public void NoteSlowMessage(long now, double ms, string? message)
    {
        if (!IsRecording) return;

        SlowMessageCount++;

        if (_slowMessages.Count == MaxSlowMessages) _slowMessages.RemoveAt(0);

        _slowMessages.Add(new SlowMessage((now - _recordStart) / _ticksPerMs / 1000, FrameCount + 1, ms, SlowMessage.CommandOf(message), SlowMessage.HeadOf(message), Page));
    }

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
        _hitchCauses.Clear();
        _slowMessages.Clear();
        SlowMessageCount = 0;
        _ring.Clear();
        Game.Clear();
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
        var sample = new FrameSample((float)frameMs, (float)split.ModsMs, (float)split.HookMs, (float)split.GameMs, (float)split.AnalyzerMs, context.ManagedGcs, context.GameGcs, context.Engine, context.World, _page);
        var atSeconds = (now - _recordStart) / _ticksPerMs / 1000;

        _lastFrameEnd = now;
        FrameCount++;
        _ring.Push(sample, _modFrameMs);
        Game.Add(atSeconds, frameMs, frameMs >= HitchMs, context.Scene, context.World, context.Memory, Page);
        NoteWorstCalls(atSeconds, sample, context.Scene);

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
        if (totals.Calls == 0) totals.FirstCallTicks = total;

        _active[probe]--;
        _frameCalls[probe]++;
        _frameSelf[probe] += self;
        _frameAlloc[probe] += selfAlloc;
        totals.Calls++;
        totals.SelfTicks += self;
        totals.AllocBytes += selfAlloc;

        // A probe inside itself (recursion) adds its total once, at the outermost call.
        if (_active[probe] == 0) totals.TotalTicks += total;

        if (total > totals.MaxCallTicks)
        {
            totals.MaxCallTicks = total;
            totals.MaxCallThisFrame = true;
        }

        if (_depth == 0) return;

        _stack[_depth - 1].ChildTicks += total;
        _stack[_depth - 1].ChildAlloc += alloc;
    }

    /// <summary>Where and in what frame each probe's slowest call so far happened, for the probes whose record this frame broke.</summary>
    private void NoteWorstCalls(double atSeconds, FrameSample sample, string? scene)
    {
        foreach (var probe in _touched)
        {
            var totals = _probeTotals[probe];

            if (!totals.MaxCallThisFrame) continue;

            totals.MaxCallThisFrame = false;
            totals.MaxCall = new WorstCall(atSeconds, FrameCount, scene ?? "", Page, sample);
        }
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

        var hitch = new Hitch(HitchCount, (now - _recordStart) / _ticksPerMs / 1000, FrameCount, sample, probes, mods, context.Scene, Page);
        var cause = Analysis.Cause(this, hitch);
        var causeMod = Analysis.CauseMod(hitch);

        // Worked out once here, so the panel's half-second refresh does not rebuild the strings for every hitch.
        hitch.CauseText = cause;
        hitch.MethodText = Analysis.TopMethod(this, hitch);
        _hitches.Add(hitch);
        _hitchCauses.TryGetValue(cause, out var count);
        _hitchCauses[cause] = count + 1;

        if (causeMod != NotTimed) _modTotals[causeMod].CausedHitches++;
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
            _probeTotals[probe].MaxCallThisFrame = false;
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

    /// <summary>The first call's length; often the slowest, since it pays for loading and first-time setup.</summary>
    public long FirstCallTicks;

    /// <summary>Where the slowest call happened. Kept after its frame leaves the window.</summary>
    public WorstCall MaxCall = WorstCall.None;

    /// <summary>Set when a call in the frame being recorded beat <see cref="MaxCallTicks"/>; cleared at the frame's end.</summary>
    internal bool MaxCallThisFrame;

    public void Clear()
    {
        Calls = SelfTicks = TotalTicks = AllocBytes = MaxCallTicks = Frames = FirstCallTicks = 0;
        WorstFrameMs = 0;
        MaxCall = WorstCall.None;
        MaxCallThisFrame = false;
    }
}

public sealed class ModTotals
{
    public double SelfMs;
    public long AllocBytes;
    public long Frames;
    /// <summary>Hitches where this mod took 40% of the frame or more on its own.</summary>
    public long CausedHitches;
    public double WorstFrameMs;

    public void Clear()
    {
        SelfMs = WorstFrameMs = 0;
        AllocBytes = Frames = CausedHitches = 0;
    }
}

/// <summary>What the adapter knows about a frame that the probes do not: garbage collections and the engine's own timings.</summary>
/// <param name="ManagedGcs">Collections by the mods' .NET runtime during the frame.</param>
/// <param name="GameGcs">Collections by the game's own runtime during the frame.</param>
/// <param name="Engine">The engine's frame timings, when the game has them turned on.</param>
/// <param name="Scene">Where the player was, for a hitch ("home", "map", "menu").</param>
/// <param name="World">The engine clock and the game world this frame.</param>
/// <param name="Memory">The latest memory reading, empty when none was taken.</param>
public readonly record struct FrameContext(short ManagedGcs, short GameGcs, EngineTimes Engine, string Scene, WorldReading World = default, MemoryReading Memory = default);

/// <summary>Milliseconds from the engine's frame timing, all zero when the game does not collect them.</summary>
public readonly record struct EngineTimes(float MainThreadMs, float RenderThreadMs, float GpuMs);

/// <param name="Page">The page showing, as an id for <see cref="Recorder.PageName"/>; 0 when unknown.</param>
public readonly record struct FrameSample(float Ms, float ModsMs, float HookMs, float GameMs, float AnalyzerMs, short ManagedGcs, short GameGcs, EngineTimes Engine, WorldReading World = default, short Page = 0)
{
    /// <summary>Time no probe saw: the game's own work outside hooked methods, rendering and waiting.</summary>
    public float RestMs => Math.Max(0, Ms - ModsMs - HookMs - GameMs - AnalyzerMs);
}

/// <param name="Id">Probe id for a probe entry, mod id for a mod entry.</param>
public readonly record struct HitchEntry(int Id, double Ms, int Calls);

/// <param name="Number">Counts from 1 since the last reset, and keeps counting when old hitches are dropped.</param>
/// <param name="AtSeconds">Seconds into the recording.</param>
/// <param name="Page">The game page that was showing, "" when the web probe had not said.</param>
public sealed record Hitch(long Number, double AtSeconds, long Frame, FrameSample Sample, List<HitchEntry> TopProbes, List<HitchEntry> TopMods, string Scene, string Page = "")
{
    /// <summary>The most likely cause, set when the hitch is recorded; "" on a hitch made outside the recorder.</summary>
    public string CauseText { get; internal set; } = "";

    /// <summary>The slowest method with its mod when it took a fifth of the frame, set when the hitch is recorded.</summary>
    public string MethodText { get; internal set; } = "";
}

/// <summary>Where a probe's slowest call happened.</summary>
/// <param name="AtSeconds">Seconds into the recording.</param>
/// <param name="Frame">The frame number, counted like <see cref="Hitch.Frame"/>.</param>
/// <param name="Sample">That whole frame's breakdown.</param>
public sealed record WorstCall(double AtSeconds, long Frame, string Scene, string Page, FrameSample Sample)
{
    public static readonly WorstCall None = new(0, 0, "", "", default);

    public bool IsNone => Frame == 0;
}

/// <summary>One slow call of the game's web message handler.</summary>
/// <param name="Frame">The frame it ran in, counted like <see cref="Hitch.Frame"/>.</param>
/// <param name="Command">The message's type, page and parameter, the parts before its data: "3 PhoneSMS open".</param>
/// <param name="Head">The message's first 80 characters, with its separators shown as '|'.</param>
public sealed record SlowMessage(double AtSeconds, long Frame, double Ms, string Command, string Head, string Page)
{
    /// <summary>The game splits a web message into 4 parts on this character: type id, page, parameter, data.</summary>
    public const char Separator = (char)30;

    public const int HeadLength = 80;

    public static string CommandOf(string? message)
    {
        if (string.IsNullOrEmpty(message)) return "";

        var parts = message.Split(Separator, 4);

        if (parts.Length < 4) return Shorten(message, 40).Replace(Separator, '|');

        return Shorten(string.Join(" ", parts, 0, 3).Trim(), 60);
    }

    public static string HeadOf(string? message) => string.IsNullOrEmpty(message) ? "" : Shorten(message, HeadLength).Replace(Separator, '|');

    private static string Shorten(string text, int length) => text.Length <= length ? text : text.Substring(0, length);
}
