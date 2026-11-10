using System;
using ExiledToolkit.Game;
using PerformanceAnalyzer.Core;
using Vuplex.WebView;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// Adds the remote debugging port to the browser's launch arguments whenever the game or another mod sets them.
/// Without it the port asked for at startup is gone by the time the browser starts, and nothing listens on it.
/// Static because a Harmony patch has nowhere else to look.
/// </summary>
internal static class DebugPortHook
{
    private static int _port;
    private static PatchKit? _hooks;
    private static Action<string> _log = _ => { };
    private static bool _hasLogged;

    public static void Apply(PatchKit hooks, int port, Action<string> log)
    {
        _port = port;
        _hooks = hooks;
        _log = log;
        hooks.Patch(typeof(DebugPortHook), typeof(StandaloneWebView), nameof(StandaloneWebView.SetCommandLineArguments), new[] { typeof(string) },
            prefix: nameof(AddPort), done: "the browser's launch arguments keep the remote debugging port", consequence: "remote debugging may not start");
    }

    private static void AddPort(ref string __0)
    {
        try
        {
            __0 = DebugPort.With(__0, _port);

            if (!_hasLogged) _log("browser launch arguments: " + __0);

            _hasLogged = true;
        }
        catch (Exception ex)
        {
            _hooks?.Fail("adding the remote debugging port to the launch arguments", ex);
        }
    }
}
