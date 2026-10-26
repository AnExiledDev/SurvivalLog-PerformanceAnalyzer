using System;
using PerformanceAnalyzer.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// What each frame carries besides the probes: garbage collections on both runtimes, the engine's own frame timings
/// when the game collects them, and the scene name, read once a second since it rarely changes.
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

    public FrameSource(Action<string> log)
    {
        _log = log;
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
        var context = new FrameContext((short)Math.Min(short.MaxValue, managed - _managedGcs), (short)Math.Max(0, Math.Min(short.MaxValue, game - _gameGcs)), Engine(), Scene(now));

        _managedGcs = managed;
        _gameGcs = game;

        return context;
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
