using System;
using System.IO;
using GameCore.HotUpdate.ReduxUI;
using PerformanceAnalyzer.Core;
using Vuplex.WebView;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// The mod's two scripts in the game's web UI: the probe, which watches the page from the inside, and the panel. The
/// web view can reload at any time and lose both, so each is checked for every couple of seconds and sent again when
/// the page says it is missing. The panel is only sent once it is first wanted.
/// </summary>
internal sealed class WebPage
{
    private const float CheckSeconds = 2f;
    private const string NoScriptReply = "window.vuplex&&window.vuplex.postMessage('" + PageCommand.Prefix + "noscript')";
    private const string NoProbeReply = "window.vuplex&&window.vuplex.postMessage('" + PageCommand.Prefix + "noprobe')";

    private readonly Action<string> _log;
    private string? _panel;
    private string? _probe;
    private float _nextCheckAt;
    private bool _hasReportedRunFailure;

    public WebPage(Action<string> log)
    {
        _log = log;
    }

    public bool WantsProbe { get; set; }

    public bool WantsPanel { get; set; }

    /// <summary>True while a script is being sent by this class, so the script counter can tell our own traffic apart.</summary>
    public static bool IsSending { get; private set; }

    public void KeepInstalled(float now)
    {
        if (now < _nextCheckAt) return;

        _nextCheckAt = now + CheckSeconds;

        if (WantsProbe) Run("window.__slperfprobe||" + NoProbeReply);
        if (WantsPanel) Run("window.__slperfanalyzer||" + NoScriptReply);
    }

    public void InstallProbe()
    {
        if (Run(_probe ??= Resource(PageBundle.Probe))) _log("web probe sent to the web UI");
    }

    public void InstallPanel()
    {
        if (Run(_panel ??= PageBundle.Panel(Resource))) _log("panel script sent to the web UI");
    }

    public bool Open(string stateJson) => Call($"open({stateJson})");

    public void Close() => Call("close()");

    public void Update(string snapshotJson) => Call($"update({snapshotJson})");

    /// <summary>The report text for the Report tab.</summary>
    public void ShowReport(string text) => Call($"report({JsText.Literal(text)})");

    public void Toast(string text) => Call($"toast({JsText.Literal(text)})");

    /// <summary>Tells the probe whether to send its summary every second.</summary>
    public void ProbeRecording(bool isRecording, bool tagScripts) => Run($"window.__slperfprobe&&window.__slperfprobe.record({(isRecording ? "true" : "false")},{(tagScripts ? "true" : "false")})");

    public void ProbeReset() => Run("window.__slperfprobe&&window.__slperfprobe.reset()");

    private bool Call(string expression) => Run($"window.__slperfanalyzer?window.__slperfanalyzer.{expression}:{NoScriptReply}");

    private bool Run(string javaScript)
    {
        var webView = GameSingleton.Existing<ReduxUISystem>()?.GetWebUILayer()?.canvasWebViewPrefab?.WebView;

        if (webView == null) return false;

        try
        {
            IsSending = true;
            webView.ExecuteJavaScript(javaScript, null);

            return true;
        }
        catch (Exception ex)
        {
            if (!_hasReportedRunFailure) _log("could not run script in the web UI (reported once): " + ex.Message);

            _hasReportedRunFailure = true;

            return false;
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Has the game's web view listen for Chrome's remote debugger. Only works before the web view starts.</summary>
    public static void EnableRemoteDebugging(int port, Action<string> log)
    {
        try
        {
            StandaloneWebView.EnableRemoteDebugging(port);
            log($"web UI remote debugging on: open chrome://inspect on this PC and add localhost:{port}");
        }
        catch (Exception ex)
        {
            log("could not turn on web UI remote debugging: " + ex.Message);
        }
    }

    private static string Resource(string name)
    {
        var resource = "PerformanceAnalyzer.web." + name;

        using var stream = typeof(WebPage).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException(resource + " is not embedded");
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }
}
