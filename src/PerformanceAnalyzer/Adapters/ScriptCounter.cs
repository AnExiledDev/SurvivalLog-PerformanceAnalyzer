using System;
using System.Threading;
using HarmonyLib;
using PerformanceAnalyzer.Core;
using Vuplex.WebView.Internal;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// Counts every script sent into the web UI, by sender, and optionally tags each one so the web probe can tell which
/// mod's script is running and which mod a timer or listener came from. Tagging only touches scripts sent without a
/// callback: a callback receives the script's last value, which the tag would change. Static because a Harmony prefix
/// has nowhere else to look.
/// </summary>
internal static class ScriptCounter
{
    private static ScriptTraffic? _traffic;
    private static Action<string> _log = _ => { };
    private static int _hasReportedFailure;

    public static volatile bool IsCounting;
    public static volatile bool IsTagging;

    public static void Apply(Harmony harmony, ScriptTraffic traffic, Action<string> log)
    {
        _traffic = traffic;
        _log = log;

        try
        {
            harmony.Patch(AccessTools.Method(typeof(BaseWebView), nameof(BaseWebView.ExecuteJavaScript), new[] { typeof(string), typeof(Il2CppSystem.Action<string>) }), prefix: new HarmonyMethod(typeof(ScriptCounter), nameof(Count)));
            log("counting scripts sent to the web UI");
        }
        catch (Exception ex)
        {
            log("could not hook the web UI's script runner, the Web UI tab will show no senders: " + ex.Message);
        }
    }

    /// <summary>Never throws into the web view: on any failure the script goes through as it was.</summary>
    private static void Count(ref string javaScript, Il2CppSystem.Action<string> callback)
    {
        if (!IsCounting || javaScript == null) return;

        try
        {
            var owner = WebPage.IsSending ? "perfanalyzer" : ScriptTraffic.OwnerOf(javaScript);

            _traffic?.Record(owner, javaScript.Length);

            if (IsTagging && callback == null && owner != "perfanalyzer") javaScript = ScriptTag.Wrap(owner, javaScript);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _hasReportedFailure, 1) == 0) _log("counting a web UI script failed, it was sent unchanged (reported once): " + ex);
        }
    }
}
