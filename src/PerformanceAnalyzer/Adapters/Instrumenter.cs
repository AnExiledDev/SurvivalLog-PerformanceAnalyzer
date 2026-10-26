using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PerformanceAnalyzer.Core;
using UnityEngine;

namespace PerformanceAnalyzer.Adapters;

/// <summary>What the instrumenter is allowed to time, from the config.</summary>
internal sealed record ProbeOptions(bool Patches, bool Tickers, bool GameTime, IReadOnlyCollection<string> Excluded);

/// <summary>
/// Adds the probes, a few per frame so the game does not freeze while hundreds of methods are patched.
/// <para>
/// Order matters for each hooked method. Harmony builds one replacement method that calls every patch; a small patch
/// may have been inlined into it, and a probe on that patch would never run. So the patches get their probes first,
/// and only then does the hooked method get its own bracket, which makes Harmony build the replacement again, now
/// calling the patches as real calls.
/// </para>
/// </summary>
internal sealed class Instrumenter
{
    private const double BudgetMsPerFrame = 6;
    private const float RescanSeconds = 10f;
    private const int DeepLimit = 4000;
    private const int MaxNotes = 12;

    private static readonly string[] TickerNames = { "Update", "LateUpdate", "FixedUpdate", "OnGUI" };

    private readonly Recorder _recorder;
    private readonly PluginIndex _plugins;
    private readonly string _ourId;
    private readonly Harmony _probes;
    private readonly Harmony _deep;
    private readonly Action<string> _log;
    private readonly Queue<Job> _queue = new();
    private readonly HashSet<MethodBase> _wrapped = new();
    private readonly HashSet<MethodBase> _hooked = new();
    private readonly Dictionary<MethodBase, int> _hookPatchCount = new();
    private readonly HashSet<MethodInfo> _untimeable = new();
    private readonly List<MethodBase> _deepMethods = new();
    private readonly List<string> _notes = new();
    private readonly Dictionary<MethodBase, int> _probeIds = new();
    private readonly Dictionary<MethodBase, int> _gameProbeIds = new();
    private ProbeOptions _options = new(true, true, true, Array.Empty<string>());
    private float _nextScanAt;
    private int _patches;
    private int _tickers;
    private int _failed;
    private string _deepMod = "";

    public Instrumenter(Recorder recorder, PluginIndex plugins, string ourId, Action<string> log)
    {
        _recorder = recorder;
        _plugins = plugins;
        _ourId = ourId;
        _probes = new Harmony(ourId + ".probes");
        _deep = new Harmony(ourId + ".deep");
        _log = log;
    }

    public bool IsActive { get; private set; }

    public InstrumentStatus Status => new(_hooked.Count, _patches, _tickers, _deepMethods.Count, _queue.Count, _failed, _untimeable.Count, _deepMod, _notes);

    /// <summary>Starts adding probes; the queue is worked through by <see cref="Work"/>.</summary>
    public void Activate(ProbeOptions options, float now)
    {
        _options = options;

        if (IsActive) return;

        IsActive = true;
        _nextScanAt = now;
        _log("adding probes to other mods' hooks and update methods");
    }

    /// <summary>Removes every probe this mod added. The recorded numbers stay.</summary>
    public void Deactivate()
    {
        if (!IsActive) return;

        IsActive = false;
        _queue.Clear();
        SetDeep("");
        TryUnpatch(_probes);
        _wrapped.Clear();
        _hooked.Clear();
        _hookPatchCount.Clear();
        _patches = 0;
        _tickers = 0;
        _log("all probes removed");
    }

    /// <summary>Profiles every method of one mod; "" stops it. Patching hundreds of methods takes a few seconds.</summary>
    public void SetDeep(string modKey)
    {
        if (modKey == _deepMod) return;

        if (_deepMod.Length > 0)
        {
            TryUnpatch(_deep);

            foreach (var method in _deepMethods) _wrapped.Remove(method);

            _deepMethods.Clear();
            _log("deep profile of " + _deepMod + " stopped");
        }

        _deepMod = "";

        if (modKey.Length == 0) return;

        var assembly = _plugins.AssemblyOf(modKey);

        if (assembly == null || _plugins.IsOurs(assembly))
        {
            _log("no mod with the key " + modKey + " to profile deeply");

            return;
        }

        _deepMod = modKey;

        var mod = _plugins.ModOf(assembly);
        var count = 0;

        foreach (var method in DeepCandidates(assembly))
        {
            if (count++ >= DeepLimit)
            {
                Note($"deep profile of {modKey} stopped at {DeepLimit} methods");

                break;
            }

            _queue.Enqueue(new Job(TaskKind.Deep, method, null, mod));
        }

        _log($"deep profile of {modKey}: {Math.Min(count, DeepLimit)} methods queued");
    }

    /// <summary>Works the queue for a few milliseconds and rescans for new hooks now and then.</summary>
    public void Work(float now)
    {
        if (!IsActive) return;

        if (now >= _nextScanAt)
        {
            _nextScanAt = now + RescanSeconds;
            Scan();
        }

        var clock = Stopwatch.StartNew();

        while (_queue.Count > 0 && clock.Elapsed.TotalMilliseconds < BudgetMsPerFrame) Run(_queue.Dequeue());
    }

    /// <summary>Learns what one Begin and End pair costs, by timing a method that does nothing with and without a probe.</summary>
    public double Calibrate()
    {
        const int Calls = 20000;

        var target = AccessTools.Method(typeof(Calibration), nameof(Calibration.Timed));
        var scratch = new Recorder(Stopwatch.Frequency / 1000.0, 16, double.MaxValue);
        var id = scratch.AddProbe(new ProbeInfo("calibration", ProbeKind.Analyzer, "", new[] { Recorder.AnalyzerMod }));
        var live = Probes.Current;

        try
        {
            MethodProbe.Ids[target] = id;
            _probes.Patch(target, transpiler: MethodProbe.Transpiler);
            Probes.Attach(scratch);
            scratch.Start(Stopwatch.GetTimestamp());

            var timed = TimeCalls(Calibration.Timed, Calls);
            var untimed = TimeCalls(Calibration.Untimed, Calls);

            return Math.Max(0, (double)(timed - untimed) / Calls);
        }
        catch (Exception ex)
        {
            _log("could not measure what a probe costs, the analyzer's own time will read low: " + ex.Message);

            return 0;
        }
        finally
        {
            if (live != null) Probes.Attach(live);

            TryUnpatchOne(_probes, target, MethodProbe.Transpiler.method);
            MethodProbe.Ids.TryRemove(target, out _);
        }
    }

    private static long TimeCalls(Func<int, int> call, int calls)
    {
        var sum = 0;

        for (var i = 0; i < 1000; i++) sum += call(i);

        var start = Stopwatch.GetTimestamp();

        for (var i = 0; i < calls; i++) sum += call(i);

        var elapsed = Stopwatch.GetTimestamp() - start;

        return sum == int.MinValue ? 0 : elapsed;
    }

    private void Scan()
    {
        if (_options.Patches) ScanHooks();
        if (_options.Tickers) ScanTickers();
    }

    private void ScanHooks()
    {
        foreach (var original in Harmony.GetAllPatchedMethods().ToList())
        {
            if (original.DeclaringType != null && _plugins.IsOurs(original.DeclaringType.Assembly)) continue;

            var info = Harmony.GetPatchInfo(original);

            if (info == null) continue;

            var theirs = TheirPatches(info).ToList();
            var known = _hookPatchCount.TryGetValue(original, out var count) ? count : -1;

            NoteUntimeable(info);

            if (theirs.Count == 0 || theirs.Count == known) continue;

            foreach (var (patch, kind) in theirs)
            {
                if (_wrapped.Contains(patch.PatchMethod)) continue;

                _queue.Enqueue(new Job(kind, patch.PatchMethod, original, _plugins.ModOf(patch.PatchMethod.DeclaringType?.Assembly ?? typeof(object).Assembly)));
            }

            _hookPatchCount[original] = theirs.Count;
            _queue.Enqueue(new Job(TaskKind.Hook, original, null, -1));
        }
    }

    private void ScanTickers()
    {
        foreach (var (mod, assembly) in _plugins.PluginAssemblies())
        {
            if (IsExcluded(mod)) continue;

            foreach (var type in SafeTypes(assembly))
            {
                if (type.IsGenericTypeDefinition || !typeof(MonoBehaviour).IsAssignableFrom(type)) continue;

                foreach (var name in TickerNames)
                {
                    var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);

                    if (method == null || _wrapped.Contains(method) || !MethodProbe.HasBody(method)) continue;

                    _wrapped.Add(method);
                    _queue.Enqueue(new Job(TaskKind.Ticker, method, null, mod));
                }
            }
        }
    }

    private IEnumerable<(Patch Patch, ProbeKind Kind)> TheirPatches(Patches info)
    {
        foreach (var patch in info.Prefixes) if (IsTheirs(patch)) yield return (patch, ProbeKind.Prefix);
        foreach (var patch in info.Postfixes) if (IsTheirs(patch)) yield return (patch, ProbeKind.Postfix);
        foreach (var patch in info.Finalizers) if (IsTheirs(patch)) yield return (patch, ProbeKind.Finalizer);
    }

    private bool IsTheirs(Patch patch)
    {
        var assembly = patch.PatchMethod?.DeclaringType?.Assembly;

        if (assembly == null || patch.owner.StartsWith(_ourId, StringComparison.Ordinal) || _plugins.IsOurs(assembly) || PluginIndex.IsFramework(assembly)) return false;

        return !IsExcluded(_plugins.ModOf(assembly));
    }

    private void NoteUntimeable(Patches info)
    {
        foreach (var patch in info.Transpilers.Concat(info.ILManipulators))
        {
            if (patch.PatchMethod != null && !patch.owner.StartsWith(_ourId, StringComparison.Ordinal)) _untimeable.Add(patch.PatchMethod);
        }
    }

    private bool IsExcluded(int mod)
    {
        var info = _recorder.Mods[mod];

        return _options.Excluded.Contains(info.Key) || _options.Excluded.Contains(info.Guid);
    }

    private void Run(Job task)
    {
        try
        {
            switch (task.Kind)
            {
                case TaskKind.Hook:
                    AddHook(task.Method);
                    break;
                case TaskKind.Deep:
                    Wrap(task.Method, ProbeKind.Deep, "", task.Mod, _deep);
                    if (_wrapped.Contains(task.Method)) _deepMethods.Add(task.Method);
                    break;
                case TaskKind.Ticker:
                    _wrapped.Remove(task.Method);
                    Wrap(task.Method, ProbeKind.Ticker, "", task.Mod, _probes);
                    if (_wrapped.Contains(task.Method)) _tickers++;
                    break;
                default:
                    Wrap(task.Method, (ProbeKind)task.Kind, Name(task.Target), task.Mod, _probes);
                    if (_wrapped.Contains(task.Method)) _patches++;
                    break;
            }
        }
        catch (Exception ex)
        {
            _failed++;
            Note($"{Name(task.Method)}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Wrap(MethodBase method, ProbeKind kind, string target, int mod, Harmony harmony)
    {
        if (_wrapped.Contains(method)) return;

        if (method.IsGenericMethodDefinition || method.ContainsGenericParameters || !MethodProbe.HasBody(method)) return;

        MethodProbe.Ids[method] = ProbeId(method, kind, target, new[] { mod });
        harmony.Patch(method, transpiler: MethodProbe.Transpiler);
        _wrapped.Add(method);
    }

    /// <summary>
    /// Brackets a hooked method. Patching it again (or for the first time) makes Harmony rebuild its replacement, so
    /// patches that just got their probes are called for real rather than inlined.
    /// </summary>
    private void AddHook(MethodBase original)
    {
        var info = Harmony.GetPatchInfo(original);

        if (info == null) return;

        var owners = TheirPatches(info).Select(p => _plugins.ModOf(p.Patch.PatchMethod.DeclaringType?.Assembly ?? typeof(object).Assembly)).Distinct().ToArray();

        if (owners.Length == 0) return;

        var isGame = PluginIndex.IsGameType(original.DeclaringType);
        var name = Name(original);

        HookProbe.Ids[original] = ProbeId(original, isGame ? ProbeKind.Hook : ProbeKind.ModMethod, isGame ? "" : name, isGame ? owners : new[] { _plugins.ModOf(original.DeclaringType?.Assembly ?? typeof(object).Assembly) });

        var timeGame = isGame && _options.GameTime && HookProbe.CanTimeGameCode;

        if (timeGame) HookProbe.GameIds[original] = GameProbeId(original);

        if (_hooked.Contains(original)) TryUnpatchOne(_probes, original, HookProbe.Prefix.method);

        _probes.Patch(original, prefix: HookProbe.Prefix, finalizer: _hooked.Contains(original) ? null : HookProbe.Finalizer, transpiler: timeGame && !_hooked.Contains(original) ? HookProbe.Transpiler : null);
        _hooked.Add(original);
    }

    private int ProbeId(MethodBase method, ProbeKind kind, string target, int[] owners)
    {
        if (_probeIds.TryGetValue(method, out var id)) return id;

        id = _recorder.AddProbe(new ProbeInfo(Name(method), kind, target, owners));
        _probeIds[method] = id;

        return id;
    }

    private int GameProbeId(MethodBase original)
    {
        if (_gameProbeIds.TryGetValue(original, out var id)) return id;

        id = _recorder.AddProbe(new ProbeInfo(Name(original), ProbeKind.Game, "", new[] { Recorder.GameMod }));
        _gameProbeIds[original] = id;

        return id;
    }

    private IEnumerable<MethodBase> DeepCandidates(Assembly assembly)
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var type in SafeTypes(assembly))
        {
            if (type.IsGenericTypeDefinition || type.IsInterface) continue;

            foreach (var method in type.GetMethods(All))
            {
                if (method.IsAbstract || method.IsGenericMethodDefinition || _wrapped.Contains(method) || !MethodProbe.HasBody(method)) continue;
                if ((method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) != 0) continue;

                yield return method;
            }
        }
    }

    private static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
    }

    private void TryUnpatch(Harmony harmony)
    {
        try
        {
            harmony.UnpatchSelf();
        }
        catch (Exception ex)
        {
            _log("could not remove some probes: " + ex.Message);
        }
    }

    private void TryUnpatchOne(Harmony harmony, MethodBase method, MethodInfo patch)
    {
        try
        {
            harmony.Unpatch(method, patch);
        }
        catch (Exception ex)
        {
            _log($"could not remove a probe from {Name(method)}: {ex.Message}");
        }
    }

    private void Note(string note)
    {
        if (_notes.Count < MaxNotes) _notes.Add(note);

        _log("probe not added: " + note);
    }

    public static string Name(MethodBase? method) => method == null ? "" : (method.DeclaringType?.Name ?? "?") + "." + method.Name;

    private enum TaskKind
    {
        Prefix = ProbeKind.Prefix,
        Postfix = ProbeKind.Postfix,
        Finalizer = ProbeKind.Finalizer,
        Hook = 100,
        Ticker,
        Deep,
    }

    private sealed record Job(TaskKind Kind, MethodBase Method, MethodBase? Target, int Mod)
    {
        public Job(ProbeKind kind, MethodBase method, MethodBase? target, int mod) : this((TaskKind)kind, method, target, mod)
        {
        }
    }
}
