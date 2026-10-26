using System;
using GameCore.HotUpdate.Battle.Logic;
using PerformanceAnalyzer.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// What each frame carries besides the probes: garbage collections on both runtimes, the engine's own frame timings
/// when the game collects them, the scene name (read once a second since it rarely changes), the engine clock and the
/// game world's state, and a memory reading once a second. Each game or engine reading that throws is turned off and
/// logged once, since a stripped engine call throws every time.
/// </summary>
internal sealed class FrameSource
{
    private readonly Action<string> _log;
    private readonly bool _hasEngineTimings;
    private readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming>? _timings;
    private int _managedGcs;
    private int _gameGcs;
    private bool _canCountGameGcs = true;
    private string _scene = "";
    private float _nextSceneAt;
    private bool _canReadWorld = true;
    private bool _canReadClock = true;
    private readonly MemorySampler _memory;
    private MemoryReading _pendingMemory;

    public FrameSource(Action<string> log)
    {
        _log = log;
        _memory = new MemorySampler(log);
        _managedGcs = GC.CollectionCount(0);
        _gameGcs = GameGcCount();

        try
        {
            _hasEngineTimings = FrameTimingManager.IsFeatureEnabled();

            if (_hasEngineTimings) _timings = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<FrameTiming>(1);
        }
        catch (Exception ex)
        {
            log("could not ask the engine for its frame timings: " + ex.Message);
        }

        log(_hasEngineTimings ? "the engine's own frame timings are on" : "the engine's own frame timings are off in this game build; frames are timed by the mod alone");
    }

    public bool HasEngineTimings => _hasEngineTimings;

    public FrameContext Next(float now)
    {
        var managed = GC.CollectionCount(0);
        var game = GameGcCount();
        var context = new FrameContext((short)Math.Min(short.MaxValue, managed - _managedGcs), (short)Math.Max(0, Math.Min(short.MaxValue, game - _gameGcs)), Engine(), Scene(now), World(), _pendingMemory);

        _managedGcs = managed;
        _gameGcs = game;
        _pendingMemory = default;

        return context;
    }

    /// <summary>Takes a memory reading when one is due; the next frame carries it. Run inside the analyzer's own probe.</summary>
    public void SampleMemory(float now)
    {
        if (_memory.TryRead(now, out var reading)) _pendingMemory = reading;
    }

    private WorldReading World()
    {
        var fixedSteps = (short)Math.Min(short.MaxValue, AnalyzerTicker.TakeFixedSteps());
        var reading = new WorldReading(Time.timeScale, 0, Time.deltaTime, Time.unscaledDeltaTime, Time.maximumDeltaTime, Time.fixedDeltaTime, fixedSteps, false, false, false, -1);

        if (!_canReadWorld) return reading;

        try
        {
            var world = GameSingleton.Existing<BattleLogicWorld>();

            if (world == null) return reading;

            var time = world._GameTimeManager;
            var paused = world.IsPause || (time != null && time.IsPausedScale());

            reading = reading with { InGame = true, LogicRan = world.IsUpdate && !world.IsPause, Paused = paused, GameSpeed = time?.BattleTimeScale ?? 0 };

            return time != null && _canReadClock ? reading with { ClockSeconds = Clock(time) } : reading;
        }
        catch (Exception ex)
        {
            _canReadWorld = false;
            _log("could not read the game world's state; logic updates and game speed are off: " + ex.Message);

            return reading;
        }
    }

    private int Clock(GameTimeManager time)
    {
        try
        {
            return time.GetTotalSeconds();
        }
        catch (Exception ex)
        {
            _canReadClock = false;
            _log("could not read the in-game clock: " + ex.Message);

            return -1;
        }
    }

    private int GameGcCount()
    {
        if (!_canCountGameGcs) return 0;

        try
        {
            return Il2CppSystem.GC.CollectionCount(0);
        }
        catch (Exception ex)
        {
            _canCountGameGcs = false;
            _log("could not count the game's garbage collections: " + ex.Message);

            return 0;
        }
    }

    private EngineTimes Engine()
    {
        if (_timings == null) return default;

        FrameTimingManager.CaptureFrameTimings();

        if (FrameTimingManager.GetLatestTimings(1, _timings) == 0) return default;

        var timing = _timings[0];

        return new EngineTimes((float)timing.cpuMainThreadFrameTime, (float)timing.cpuRenderThreadFrameTime, (float)timing.gpuFrameTime);
    }

    private string Scene(float now)
    {
        if (now < _nextSceneAt) return _scene;

        _nextSceneAt = now + 1f;
        _scene = SceneManager.GetActiveScene().name ?? "";

        return _scene;
    }
}
