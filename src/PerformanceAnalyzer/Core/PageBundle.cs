using System;
using System.Text;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// The scripts sent to the web UI. The probe goes on its own and as early as possible, so it sees the other mods'
/// timers and listeners from the start; the panel follows with its style as a string.
/// </summary>
public static class PageBundle
{
    public const string Probe = "probe.js";

    public static readonly string[] PanelScripts = { "format.js", "panel.js" };

    public const string Style = "panel.css";

    /// <param name="read">Returns one embedded file's text by name.</param>
    public static string Panel(Func<string, string> read)
    {
        var script = new StringBuilder("window.__slperfanalyzerCss=").Append(JsText.Literal(read(Style))).Append(";\n");

        foreach (var name in PanelScripts) script.Append(read(name)).Append('\n');

        return script.ToString();
    }
}
