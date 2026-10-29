using System;
using System.IO;
using GameCore.HotUpdate.ReduxUI;
using PerformanceAnalyzer.Core;
using Vuplex.WebView;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// The mod's two scripts in the game's web UI: the probe, which watches the page from the inside, and the panel. The
/// web view can reload at any time and lose both, so each is checked for every couple of seconds and sent again when
/// the page says it is missing. The probe also rides in the web view's page-load scripts, which the web view runs as
/// each page finishes loading, so it is back after a reload before most mods resend their own scripts and the
/// listeners and timers those scripts make still get an owner. The panel is only sent once it is first wanted.
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
    private IntPtr _probeOn;
    private bool _hasReportedRunFailure;

    public WebPage(Action<string> log)
    {
        _log = log;
    }

    public bool WantsProbe { get; set; }

    public bool WantsPanel { get; set; }

    /// <summary>True while a script is being sent by this class, so the script counter can tell our own traffic apart.</summary>
    public static bool IsSending { get; private set; }

    /// <param name="probeState">What the probe's state() should say: see <see cref="ProbeState"/>. Any other answer
    /// gets "noprobe" back, and the session sends the probe and its settings again.</param>
    public void KeepInstalled(float now, string probeState)
    {
        if (WantsProbe) FirstProbe();

        if (now < _nextCheckAt) return;

        _nextCheckAt = now + CheckSeconds;

        if (WantsProbe) Run($"window.__slperfprobe&&window.__slperfprobe.state()==={JsText.Literal(probeState)}||{NoProbeReply}");
        if (WantsPanel) Run("window.__slperfanalyzer||" + NoScriptReply);
    }

    /// <summary>"n" once the probe has the mod names, then "r" while it records.</summary>
    public static string ProbeState(bool isRecording) => isRecording ? "nr" : "n";

    public void InstallProbe()
    {
        if (Run(ProbeSource)) _log("web probe sent to the web UI");
    }

    /// <summary>The mod names the probe needs to give each mod one row; see <see cref="WebOwners.ProbeJson"/>.</summary>
    public void ProbeNames(string json) => Run($"window.__slperfprobe&&window.__slperfprobe.names({json})");

    private string ProbeSource => _probe ??= Resource(PageBundle.Probe);

    /// <summary>
    /// Once per web view: the probe goes in at once and into the page-load scripts, so it is in the page before the
    /// mods send their scripts, from the start of the game, not only once recording starts.
    /// </summary>
    private void FirstProbe()
    {
        var webView = WebView();

        if (webView == null || webView.Pointer == _probeOn) return;

        _probeOn = webView.Pointer;
        InstallProbe();

        try
        {
            webView.PageLoadScripts.Add(ProbeSource);
        }
        catch (Exception ex)
        {
            _log("could not add the web probe to the page-load scripts, so after a reload scripts go without an owner until the next check: " + ex.Message);
        }
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

    /// <summary>Asks the probe to answer at once with its clock; see <see cref="HopProbe"/>.</summary>
    public bool Ping(int id) => Run($"window.__slperfprobe&&window.__slperfprobe.ping({id})");

    /// <summary>Shows or removes the probe's frame counter in the page's top-left corner.</summary>
    public bool Hop(bool isOn) => Run($"window.__slperfprobe&&window.__slperfprobe.hop({(isOn ? "true" : "false")})");

    /// <summary>The game's copy of the page, the texture the game draws the menus from.</summary>
    public static UnityEngine.Texture2D? Texture() => WebView()?.Texture;

    private bool Call(string expression) => Run($"window.__slperfanalyzer?window.__slperfanalyzer.{expression}:{NoScriptReply}");

    private static IWebView? WebView() => GameSingleton.Existing<ReduxUISystem>()?.GetWebUILayer()?.canvasWebViewPrefab?.WebView;

    private bool Run(string javaScript)
    {
        var webView = WebView();

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
