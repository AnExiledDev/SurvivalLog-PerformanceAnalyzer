using System;
using System.Diagnostics;
using System.Threading;
using GameCore.HotUpdate.ReduxUI;
using HarmonyLib;
using PerformanceAnalyzer.Core;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// Times the game's handler for messages from the web UI and keeps the text of the slow ones. A page message that
/// takes a hitch's worth of time shows up as game code with no name; its command says which page asked for what.
/// Static because a Harmony patch has nowhere else to look.
/// </summary>
internal static class SlowMessages
{
    private static Recorder? _recorder;
    private static Action<string> _log = _ => { };
    private static int _mainThread;
    private static int _hasReportedFailure;

    public static void Apply(Harmony harmony, Recorder recorder, Action<string> log)
    {
        _recorder = recorder;
        _log = log;
        _mainThread = Environment.CurrentManagedThreadId;

        try
        {
            var target = AccessTools.Method(typeof(WebUILayer), "OnMessageFromJS");

            // Outermost on both ends, so other mods' patches on the handler are inside the time.
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(SlowMessages), nameof(Before)) { priority = int.MaxValue },
                finalizer: new HarmonyMethod(typeof(SlowMessages), nameof(After)) { priority = int.MinValue });
            log("timing the game's web message handler");
        }
        catch (Exception ex)
        {
            log("could not hook the game's web message handler, slow web messages will not be listed: " + ex.Message);
        }
    }

    private static void Before(out long __state) => __state = _recorder is { IsRecording: true } ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Never throws into the game, and passes the handler's own exception on unchanged.</summary>
    private static Exception? After(Vuplex.WebView.EventArgs<string> eventArgs, long __state, Exception? __exception)
    {
        if (__state == 0) return __exception;

        try
        {
            var recorder = _recorder;
            var now = Stopwatch.GetTimestamp();

            if (recorder == null || Environment.CurrentManagedThreadId != _mainThread) return __exception;

            var ms = (now - __state) / recorder.TicksPerMs;

            if (ms >= recorder.HitchMs) recorder.NoteSlowMessage(now, ms, eventArgs?.Value);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _hasReportedFailure, 1) == 0) _log("timing a web message failed (reported once): " + ex.Message);
        }

        return __exception;
    }
}
