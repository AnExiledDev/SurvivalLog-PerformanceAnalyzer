using System;
using System.Diagnostics;
using ExiledToolkit.Game;
using GameCore.HotUpdate.ReduxUI;
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
    private static int _mainThread;
    private static PatchKit? _hooks;

    public static void Apply(PatchKit hooks, Recorder recorder)
    {
        const string Consequence = "slow web messages will not be listed";

        _recorder = recorder;
        _hooks = hooks;
        _mainThread = Environment.CurrentManagedThreadId;

        // Outermost on both ends, so other mods' patches on the handler are inside the time. Each call takes one
        // priority, so the two ends go in one at a time, and the finalizer only once the prefix is in.
        if (hooks.Patch(typeof(SlowMessages), typeof(WebUILayer), "OnMessageFromJS", null, int.MaxValue, prefix: nameof(Before), consequence: Consequence))
        {
            hooks.Patch(typeof(SlowMessages), typeof(WebUILayer), "OnMessageFromJS", null, int.MinValue, finalizer: nameof(After),
                done: "timing the game's web message handler", consequence: Consequence);
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
            _hooks?.Fail("timing a web message", ex);
        }

        return __exception;
    }
}
