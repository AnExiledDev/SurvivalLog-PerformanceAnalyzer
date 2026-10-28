using System;
using HarmonyLib;
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
    private static Action<string> _log = _ => { };
    private static bool _hasLogged;

    public static void Apply(Harmony harmony, int port, Action<string> log)
    {
        _port = port;
        _log = log;

        try
        {
            harmony.Patch(
                AccessTools.Method(typeof(StandaloneWebView), nameof(StandaloneWebView.SetCommandLineArguments), new[] { typeof(string) }),
                prefix: new HarmonyMethod(typeof(DebugPortHook), nameof(AddPort)));
            log("the browser's launch arguments keep the remote debugging port");
        }
        catch (Exception ex)
        {
            log("could not hook the browser's launch arguments, remote debugging may not start: " + ex.Message);
        }
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
            if (!_hasLogged) _log("could not add the remote debugging port to the launch arguments: " + ex.Message);

            _hasLogged = true;
        }
    }
}
