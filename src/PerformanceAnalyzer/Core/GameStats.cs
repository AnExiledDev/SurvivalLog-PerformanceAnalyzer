using System;
using System.Collections.Generic;

namespace PerformanceAnalyzer.Core;

/// <summary>What the engine clock and the game world did in one frame; all zero when nothing could be read.</summary>
/// <param name="TimeScale">The engine's time scale (Time.timeScale).</param>
/// <param name="GameSpeed">The game's own speed setting, 0 outside a game.</param>
/// <param name="DeltaSeconds">Game time the engine handed out this frame (Time.deltaTime).</param>
/// <param name="UnscaledSeconds">Real time of the frame as the engine saw it (Time.unscaledDeltaTime).</param>
/// <param name="MaxDeltaSeconds">The engine's cap on one frame's game time (Time.maximumDeltaTime).</param>
/// <param name="FixedDeltaSeconds">The physics step (Time.fixedDeltaTime).</param>
/// <param name="FixedSteps">Physics steps the engine ran since the last frame.</param>
/// <param name="InGame">A game world exists (a save is loaded).</param>
/// <param name="LogicRan">The game world updated this frame: loaded, running and not paused.</param>
/// <param name="Paused">The game is paused, by the player or by a menu.</param>
/// <param name="ClockSeconds">The in-game clock in game seconds, or -1 outside a game.</param>
public readonly record struct WorldReading(
    float TimeScale,
    float GameSpeed,
    float DeltaSeconds,
    float UnscaledSeconds,
    float MaxDeltaSeconds,
    float FixedDeltaSeconds,
    short FixedSteps,
    bool InGame,
    bool LogicRan,
    bool Paused,
    int ClockSeconds)
{
    /// <summary>
    /// Game time the engine dropped because the frame ran past the cap: what the clock would have moved at this time
    /// scale, minus what it did. A slow frame then slows the game itself, not just the picture.
    /// </summary>
    public float CappedMs => Math.Max(0, (UnscaledSeconds * TimeScale - DeltaSeconds) * 1000);
}

/// <summary>Memory in bytes, read about once a second; 0 means that reading is not available in this game build.</summary>
/// <param name="ModsHeap">The mods' .NET heap.</param>
/// <param name="GameHeap">The game's own script heap.</param>
/// <param name="UnityAllocated">Memory the engine has handed out.</param>
/// <param name="UnityReserved">Memory the engine has taken from the system.</param>
/// <param name="Textures">Memory the loaded textures use.</param>
/// <param name="WorkingSet">The game process's memory in RAM.</param>
public readonly record struct MemoryReading(long ModsHeap, long GameHeap, long UnityAllocated, long UnityReserved, long Textures, long WorkingSet)
{
    public bool IsEmpty => ModsHeap == 0 && GameHeap == 0 && UnityAllocated == 0 && UnityReserved == 0 && Textures == 0 && WorkingSet == 0;

    public static MemoryReading Max(MemoryReading a, MemoryReading b) => new(
        Math.Max(a.ModsHeap, b.ModsHeap),
        Math.Max(a.GameHeap, b.GameHeap),
        Math.Max(a.UnityAllocated, b.UnityAllocated),
        Math.Max(a.UnityReserved, b.UnityReserved),
        Math.Max(a.Textures, b.Textures),
        Math.Max(a.WorkingSet, b.WorkingSet));
}

/// <summary>One second of the recording.</summary>
/// <param name="Second">Whole seconds into the recording.</param>
/// <param name="Ms">The frames' summed length; frames per second is Frames over this.</param>
/// <param name="LogicFrames">Frames where the game world updated.</param>
/// <param name="ClockAdvance">In-game seconds the clock moved.</param>
/// <param name="Memory">The latest memory reading in the second.</param>
/// <param name="Page">The game page showing at the end of the second.</param>
/// <param name="Web">The page's style, layout and script work in the second, when remote debugging is on.</param>
public readonly record struct SecondSample(
    long Second,
    int Frames,
    float Ms,
    float MaxMs,
    int LogicFrames,
    int FixedSteps,
    int Hitches,
    float ClockAdvance,
    float CappedMs,
    float TimeScale,
    float GameSpeed,
    bool Paused,
    MemoryReading Memory,
    string Scene,
    string Page = "",
    WebReading Web = default)
{
    public double Fps => Ms > 0 ? Frames * 1000.0 / Ms : 0;

    public double LogicPerSecond => Ms > 0 ? LogicFrames * 1000.0 / Ms : 0;

    public double FixedPerSecond => Ms > 0 ? FixedSteps * 1000.0 / Ms : 0;

    public double ClockRate => Ms > 0 ? ClockAdvance * 1000.0 / Ms : 0;
}

/// <summary>Frame times in one scene, kept as a histogram so a long recording costs no more memory than a short one.</summary>
public sealed class SceneStats
{
    public const double BucketMs = 0.25;
    public const int Buckets = 1000;

    private readonly int[] _histogram = new int[Buckets + 1];

    public SceneStats(string name) => Name = name;

    public string Name { get; }

    public long Frames { get; private set; }

    public double Ms { get; private set; }

    public double MaxMs { get; private set; }

    public long Hitches { get; private set; }

    /// <summary>Frames slower than 60 frames per second.</summary>
    public long Over16 { get; private set; }

    /// <summary>Frames slower than 30 frames per second.</summary>
    public long Over33 { get; private set; }

    public long LogicFrames { get; private set; }

    public double MeanMs => Frames > 0 ? Ms / Frames : 0;

    public double Fps => Ms > 0 ? Frames * 1000.0 / Ms : 0;

    public double LogicPerSecond => Ms > 0 ? LogicFrames * 1000.0 / Ms : 0;

    public void Add(double frameMs, bool isHitch, bool logicRan)
    {
        Frames++;
        Ms += frameMs;

        if (frameMs > MaxMs) MaxMs = frameMs;
        if (isHitch) Hitches++;
        if (frameMs > 1000.0 / 60) Over16++;
        if (frameMs > 1000.0 / 30) Over33++;
        if (logicRan) LogicFrames++;

        _histogram[Math.Clamp((int)(frameMs / BucketMs), 0, Buckets)]++;
    }

    /// <summary>Nearest-rank percentile, read as the top of its histogram bucket and never past the slowest frame.</summary>
    public double Percentile(double fraction)
    {
        if (Frames == 0) return 0;

        var rank = Math.Max(1, (long)Math.Ceiling(fraction * Frames));
        long seen = 0;

        for (var i = 0; i < Buckets; i++)
        {
            seen += _histogram[i];

            if (seen >= rank) return Math.Min((i + 1) * BucketMs, MaxMs);
        }

        return MaxMs;
    }
}

/// <summary>
/// The game side of a recording: logic updates, physics steps, game speed and clock, game time lost to the engine's
/// frame cap, memory over time and frame times per scene. Fed one frame at a time by the recorder.
/// </summary>
public sealed class GameStats
{
    /// <summary>An hour of seconds; older ones are dropped.</summary>
    public const int TimelineSeconds = 3600;

    /// <summary>A clock jump bigger than this in one frame is a load or a skip, not time passing.</summary>
    private const int MaxClockStep = 3600;

    /// <summary>Below this a frame's dropped game time is float noise.</summary>
    private const float CappedNoiseMs = 0.05f;

    private readonly List<SecondSample> _timeline = new();
    private readonly List<SceneStats> _scenes = new();
    private readonly Dictionary<string, SceneStats> _sceneByName = new(StringComparer.Ordinal);
    private readonly Dictionary<float, double> _speedMs = new();

    private long _second = -1;
    private int _frames;
    private double _ms;
    private double _maxMs;
    private int _logic;
    private int _fixed;
    private int _hitches;
    private double _clock;
    private double _capped;
    private int _lastClock = -1;
    private WorldReading _world;
    private MemoryReading _memory;
    private string _scene = "";
    private string _page = "";
    private WebReading _web;

    public IReadOnlyList<SecondSample> Timeline => _timeline;

    /// <summary>Every web reading added up, and how many seconds had one.</summary>
    public WebReading WebTotals { get; private set; }

    public int WebSeconds { get; private set; }

    /// <summary>The slowest second for the page's own work (style, layout and script), when any reading came in.</summary>
    public WebReading WebWorst { get; private set; }

    public long WebWorstSecond { get; private set; } = -1;

    public string WebWorstPage { get; private set; } = "";

    public IReadOnlyList<SceneStats> Scenes => _scenes;

    /// <summary>Real milliseconds spent at each game speed while in a game, speeds rounded to two places.</summary>
    public IReadOnlyDictionary<float, double> SpeedMs => _speedMs;

    public long Frames { get; private set; }

    public double Ms { get; private set; }

    /// <summary>Real time with a game world loaded.</summary>
    public double InGameMs { get; private set; }

    public double PausedMs { get; private set; }

    public long LogicFrames { get; private set; }

    /// <summary>Real time of the frames where the game world updated and was not paused.</summary>
    public double LogicMs { get; private set; }

    public long FixedSteps { get; private set; }

    public double ClockAdvance { get; private set; }

    /// <summary>Game time the engine handed out while the game ran unpaused.</summary>
    public double GameTimeMs { get; private set; }

    public double CappedMs { get; private set; }

    public long CappedFrames { get; private set; }

    /// <summary>The newest frame's reading.</summary>
    public WorldReading World => _world;

    public MemoryReading FirstMemory { get; private set; }

    public double FirstMemoryAt { get; private set; }

    public MemoryReading LastMemory { get; private set; }

    public double LastMemoryAt { get; private set; }

    public MemoryReading PeakMemory { get; private set; }

    public bool HasWorld { get; private set; }

    /// <summary>Logic updates per second of real time in a game.</summary>
    public double LogicPerSecond => InGameMs > 0 ? LogicFrames * 1000.0 / InGameMs : 0;

    public double FixedPerSecond => Ms > 0 ? FixedSteps * 1000.0 / Ms : 0;

    /// <summary>In-game seconds per real second while the game ran unpaused.</summary>
    public double ClockRate => LogicMs > 0 ? ClockAdvance * 1000.0 / LogicMs : 0;

    /// <summary>Share of game time lost to the frame cap, 0 to 100.</summary>
    public double CappedPercent => GameTimeMs + CappedMs > 0 ? CappedMs * 100 / (GameTimeMs + CappedMs) : 0;

    /// <summary>The last complete second, or null before the first one ends.</summary>
    public SecondSample? LastSecond => _timeline.Count > 0 ? _timeline[^1] : null;

    /// <summary>Bytes per minute between the first and the newest memory reading; 0 until they are a minute apart.</summary>
    public double GrowthPerMinute(Func<MemoryReading, long> pick)
    {
        var minutes = (LastMemoryAt - FirstMemoryAt) / 60;

        if (minutes < 1) return 0;

        var first = pick(FirstMemory);
        var last = pick(LastMemory);

        return first > 0 && last > 0 ? (last - first) / minutes : 0;
    }

    /// <param name="atSeconds">Seconds into the recording at the frame's end.</param>
    public void Add(double atSeconds, double frameMs, bool isHitch, string scene, WorldReading world, MemoryReading memory, string page = "")
    {
        scene ??= "";

        var second = (long)Math.Floor(atSeconds);

        if (second != _second)
        {
            CloseSecond();
            _second = second;
        }

        var advance = ClockStep(world);
        var capped = world.CappedMs;

        Frames++;
        Ms += frameMs;
        FixedSteps += world.FixedSteps;
        ClockAdvance += advance;

        if (world.InGame)
        {
            HasWorld = true;
            InGameMs += frameMs;

            var speed = (float)Math.Round(world.GameSpeed, 2);

            _speedMs.TryGetValue(speed, out var atSpeed);
            _speedMs[speed] = atSpeed + frameMs;
        }

        if (world.Paused) PausedMs += frameMs;

        if (world.LogicRan) LogicFrames++;

        // A game paused by a near-zero time scale still updates its world; only the running frames tell the clock rate.
        var running = world.LogicRan && !world.Paused;

        if (running)
        {
            LogicMs += frameMs;
            GameTimeMs += world.DeltaSeconds * 1000.0;

            if (capped > CappedNoiseMs)
            {
                CappedMs += capped;
                CappedFrames++;
            }
        }

        _frames++;
        _ms += frameMs;
        _maxMs = Math.Max(_maxMs, frameMs);
        _fixed += world.FixedSteps;
        _clock += advance;

        if (world.LogicRan) _logic++;
        if (running && capped > CappedNoiseMs) _capped += capped;

        if (isHitch) _hitches++;

        _world = world;
        _scene = scene;
        _page = page ?? "";

        if (!memory.IsEmpty) NoteMemory(atSeconds, memory);

        Scene(scene).Add(frameMs, isHitch, world.LogicRan);
    }

    /// <summary>One second of the page's own work from the browser's counters; it lands in the second being recorded.</summary>
    public void NoteWeb(WebReading reading)
    {
        _web = _web.Plus(reading);
        WebTotals = WebTotals.Plus(reading);
        WebSeconds++;

        if (reading.BusyMs <= WebWorst.BusyMs) return;

        WebWorst = reading;
        WebWorstSecond = _second;
        WebWorstPage = _page;
    }

    public void Clear()
    {
        _timeline.Clear();
        _scenes.Clear();
        _sceneByName.Clear();
        _speedMs.Clear();
        _second = -1;
        ClearSecond();
        _lastClock = -1;
        _world = default;
        _memory = default;
        _scene = "";
        _page = "";
        _web = default;
        WebTotals = WebWorst = default;
        WebSeconds = 0;
        WebWorstSecond = -1;
        WebWorstPage = "";
        Frames = LogicFrames = FixedSteps = CappedFrames = 0;
        Ms = InGameMs = PausedMs = LogicMs = ClockAdvance = GameTimeMs = CappedMs = 0;
        FirstMemory = LastMemory = PeakMemory = default;
        FirstMemoryAt = LastMemoryAt = 0;
        HasWorld = false;
    }

    /// <summary>In-game seconds since the last frame's clock reading; a load or a jump counts as none.</summary>
    private double ClockStep(WorldReading world)
    {
        var last = _lastClock;

        _lastClock = world.ClockSeconds;

        if (last < 0 || world.ClockSeconds < 0) return 0;

        var step = world.ClockSeconds - last;

        return step is >= 0 and <= MaxClockStep ? step : 0;
    }

    private void NoteMemory(double atSeconds, MemoryReading memory)
    {
        if (FirstMemory.IsEmpty)
        {
            FirstMemory = memory;
            FirstMemoryAt = atSeconds;
        }

        LastMemory = memory;
        LastMemoryAt = atSeconds;
        PeakMemory = MemoryReading.Max(PeakMemory, memory);
        _memory = memory;
    }

    private SceneStats Scene(string name)
    {
        if (_sceneByName.TryGetValue(name, out var stats)) return stats;

        stats = new SceneStats(name);
        _sceneByName[name] = stats;
        _scenes.Add(stats);

        return stats;
    }

    private void CloseSecond()
    {
        if (_frames > 0)
        {
            if (_timeline.Count == TimelineSeconds) _timeline.RemoveAt(0);

            _timeline.Add(new SecondSample(_second, _frames, (float)_ms, (float)_maxMs, _logic, _fixed, _hitches, (float)_clock, (float)_capped, _world.TimeScale, _world.GameSpeed, _world.Paused, _memory, _scene, _page, _web));
        }

        ClearSecond();
    }

    private void ClearSecond()
    {
        _web = default;
        _frames = _logic = _fixed = _hitches = 0;
        _ms = _maxMs = _clock = _capped = 0;
    }
}

/// <summary>The memory readings by name, in the order the panel and the report show them.</summary>
public static class MemoryFields
{
    public static readonly (string Key, string Label, Func<MemoryReading, long> Pick)[] All =
    {
        ("game", "game script heap", m => m.GameHeap),
        ("mods", "mods' heap", m => m.ModsHeap),
        ("unity", "engine allocated", m => m.UnityAllocated),
        ("reserved", "engine reserved", m => m.UnityReserved),
        ("textures", "textures", m => m.Textures),
        ("process", "process in RAM", m => m.WorkingSet),
    };
}
