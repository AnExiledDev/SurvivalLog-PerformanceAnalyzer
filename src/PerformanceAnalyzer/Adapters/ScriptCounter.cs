using System;
using ExiledToolkit.Game;
using PerformanceAnalyzer.Core;
using Vuplex.WebView.Internal;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// Counts every script sent into the web UI while recording, by sender, and tags each one so the web probe can tell
/// which mod's script is running and which mod a timer or listener came from. Tagging runs from the start of the game,
/// not only while recording, since mods set up most of their listeners and timers when they first send their scripts.
/// Tagging only touches scripts sent without a callback: a callback receives the script's last value, which the tag
/// would change. Static because a Harmony prefix has nowhere else to look.
/// </summary>
internal static class ScriptCounter
{
    private static ScriptTraffic? _traffic;
    private static PatchKit? _hooks;

    private const string Self = "perfanalyzer";

    /// <summary>Recording, with [Web] ScriptTraffic on.</summary>
    public static volatile bool IsCounting;

    /// <summary>[Web] Probe and TagScripts on, recording or not.</summary>
    public static volatile bool IsTagging;

    public static void Apply(PatchKit hooks, ScriptTraffic traffic)
    {
        _traffic = traffic;
        _hooks = hooks;
        hooks.Patch(typeof(ScriptCounter), typeof(BaseWebView), nameof(BaseWebView.ExecuteJavaScript), new[] { typeof(string), typeof(Il2CppSystem.Action<string>) },
            prefix: nameof(Count), done: "counting scripts sent to the web UI", consequence: "the Web UI tab will show no senders");
    }

    /// <summary>Never throws into the web view: on any failure the script goes through as it was.</summary>
    private static void Count(ref string javaScript, Il2CppSystem.Action<string> callback)
    {
        if (javaScript == null || !(IsCounting || IsTagging)) return;

        try
        {
            var owner = WebPage.IsSending ? Self : ScriptTraffic.OwnerOf(javaScript, _traffic?.Known);

            if (IsCounting) _traffic?.Record(owner, javaScript.Length);

            if (IsTagging && callback == null && owner != Self) javaScript = ScriptTag.Wrap(owner, javaScript);
        }
        catch (Exception ex)
        {
            _hooks?.Fail("counting a web UI script, it was sent unchanged", ex);
        }
    }
}
