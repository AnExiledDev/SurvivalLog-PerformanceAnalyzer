using System;
using System.Collections.Generic;
using PerformanceAnalyzer.Core;
using UnityEngine;
using UnityEngine.Profiling;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// Memory once a second. Some of the engine's memory calls can be missing or return nothing in a released game, so
/// each reading is tried on its own and turned off, with one log line, the first time it throws.
/// </summary>
internal sealed class MemorySampler
{
    private const float EverySeconds = 1f;

    private readonly Action<string> _log;
    private readonly HashSet<string> _off = new(StringComparer.Ordinal);
    private float _nextAt;

    public MemorySampler(Action<string> log) => _log = log;

    public bool TryRead(float now, out MemoryReading reading)
    {
        reading = default;

        if (now < _nextAt) return false;

        _nextAt = now + EverySeconds;
        reading = new MemoryReading(
            Read("the mods' heap", () => GC.GetTotalMemory(false)),
            Read("the game's script heap", () => Il2CppSystem.GC.GetTotalMemory(false)),
            Read("the engine's allocated memory", Profiler.GetTotalAllocatedMemoryLong),
            Read("the engine's reserved memory", Profiler.GetTotalReservedMemoryLong),
            Read("texture memory", () => (long)Texture.currentTextureMemory),
            Read("the process's memory", () => Environment.WorkingSet));

        return true;
    }

    private long Read(string name, Func<long> read)
    {
        if (_off.Contains(name)) return 0;

        try
        {
            return Math.Max(0, read());
        }
        catch (Exception ex)
        {
            _off.Add(name);
            _log($"could not read {name}; it is left out: {ex.Message}");

            return 0;
        }
    }
}
