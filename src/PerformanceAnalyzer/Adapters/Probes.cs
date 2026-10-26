using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using PerformanceAnalyzer.Core;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// The calls the patched methods make into the recorder. Static because patched code has nowhere else to look. Off
/// the main thread a call is only counted: the recorder's stack belongs to the main thread.
/// </summary>
internal static class Probes
{
    private static Recorder? _recorder;
    private static int _mainThread;

    public static void Attach(Recorder recorder)
    {
        _recorder = recorder;
        _mainThread = Environment.CurrentManagedThreadId;
    }

    public static Recorder? Current => _recorder;

    public static int Begin(int probe)
    {
        var recorder = _recorder;

        if (recorder == null || !recorder.IsRecording) return Recorder.NotTimed;

        if (Environment.CurrentManagedThreadId != _mainThread)
        {
            recorder.CountOffThread(probe);

            return Recorder.NotTimed;
        }

        return recorder.Begin(probe, Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
    }

    public static void End(int token)
    {
        if (token < 0) return;

        // A token is only handed out on the main thread, so this End is on it too.
        _recorder?.End(token, Stopwatch.GetTimestamp(), GC.GetAllocatedBytesForCurrentThread());
    }

    public static readonly MethodInfo BeginMethod = AccessTools.Method(typeof(Probes), nameof(Begin));
    public static readonly MethodInfo EndMethod = AccessTools.Method(typeof(Probes), nameof(End));
}

/// <summary>
/// Wraps a whole managed method: Begin at the top, End before every return. A method that throws never reaches its
/// End; the recorder closes it when the next probe out ends. The probe id is looked up once, when the method is patched.
/// </summary>
internal static class MethodProbe
{
    /// <summary>Probe id per method, filled in before the method is patched.</summary>
    public static readonly ConcurrentDictionary<MethodBase, int> Ids = new();

    public static readonly HarmonyMethod Transpiler = new(AccessTools.Method(typeof(MethodProbe), nameof(Wrap))) { priority = Priority.Last };

    /// <summary>Whether the method has IL of its own to wrap.</summary>
    public static bool HasBody(MethodBase method) => method.GetMethodBody() != null;

    private static IEnumerable<CodeInstruction> Wrap(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        var body = new List<CodeInstruction>(instructions);

        // A jmp hands the frame to another method and would skip the End, so such a method is left as it is.
        if (!Ids.TryGetValue(original, out var id) || body.Exists(i => i.opcode == OpCodes.Jmp))
        {
            foreach (var instruction in body) yield return instruction;

            yield break;
        }

        var token = generator.DeclareLocal(typeof(int));

        // Labels stay on the method's first instruction, so a loop back to the top does not begin the probe again.
        yield return new CodeInstruction(OpCodes.Ldc_I4, id);
        yield return new CodeInstruction(OpCodes.Call, Probes.BeginMethod);
        yield return new CodeInstruction(OpCodes.Stloc, token);

        foreach (var instruction in body)
        {
            // "tail." must be followed by ret; the End goes in between, so the tail call becomes a normal call.
            if (instruction.opcode == OpCodes.Tailcall)
            {
                if (instruction.labels.Count > 0 || instruction.blocks.Count > 0) yield return new CodeInstruction(OpCodes.Nop) { labels = instruction.labels, blocks = instruction.blocks };

                continue;
            }

            if (instruction.opcode == OpCodes.Ret)
            {
                yield return new CodeInstruction(OpCodes.Ldloc, token).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                yield return new CodeInstruction(OpCodes.Call, Probes.EndMethod);
            }

            yield return instruction;
        }
    }
}

/// <summary>
/// Brackets a hooked method: the first prefix to run and the last finalizer, so everything Harmony runs for it is
/// inside. For a game method, also times the call into the game's own code inside the wrapper.
/// </summary>
internal static class HookProbe
{
    /// <summary>Hook probe id per hooked method.</summary>
    public static readonly ConcurrentDictionary<MethodBase, int> Ids = new();

    /// <summary>Game-code probe id per hooked game method.</summary>
    public static readonly ConcurrentDictionary<MethodBase, int> GameIds = new();

    public static readonly HarmonyMethod Prefix = new(AccessTools.Method(typeof(HookProbe), nameof(Enter))) { priority = int.MaxValue };
    public static readonly HarmonyMethod Finalizer = new(AccessTools.Method(typeof(HookProbe), nameof(Leave))) { priority = int.MinValue };
    public static readonly HarmonyMethod Transpiler = new(AccessTools.Method(typeof(HookProbe), nameof(TimeGameCode))) { priority = Priority.Last };

    private static readonly MethodInfo? RuntimeInvoke = AccessTools.Method(typeof(Il2CppInterop.Runtime.IL2CPP), "il2cpp_runtime_invoke");

    public static bool CanTimeGameCode => RuntimeInvoke != null;

    private static void Enter(MethodBase __originalMethod, out int __state)
    {
        __state = Ids.TryGetValue(__originalMethod, out var id) ? Probes.Begin(id) : Recorder.NotTimed;
    }

    private static void Leave(int __state) => Probes.End(__state);

    /// <summary>Wraps each call into the game's native code. The call's arguments are already on the stack and stay there.</summary>
    private static IEnumerable<CodeInstruction> TimeGameCode(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
    {
        if (RuntimeInvoke == null || !GameIds.TryGetValue(original, out var id))
        {
            foreach (var instruction in instructions) yield return instruction;

            yield break;
        }

        LocalBuilder? token = null;

        foreach (var instruction in instructions)
        {
            if (!instruction.Calls(RuntimeInvoke))
            {
                yield return instruction;

                continue;
            }

            token ??= generator.DeclareLocal(typeof(int));

            yield return new CodeInstruction(OpCodes.Ldc_I4, id).MoveLabelsFrom(instruction);
            yield return new CodeInstruction(OpCodes.Call, Probes.BeginMethod);
            yield return new CodeInstruction(OpCodes.Stloc, token);
            yield return instruction;
            yield return new CodeInstruction(OpCodes.Ldloc, token);
            yield return new CodeInstruction(OpCodes.Call, Probes.EndMethod);
        }
    }
}

/// <summary>A method that does nothing, timed to learn what one probe costs.</summary>
internal static class Calibration
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Timed(int value) => value + 1;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Untimed(int value) => value + 1;
}
