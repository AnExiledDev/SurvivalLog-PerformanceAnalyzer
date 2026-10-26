namespace PerformanceAnalyzer.Core;

/// <summary>
/// Marks a script with its sender for the web probe. The tag is a call before and after the script, never a block
/// around it: a block would turn the script's top-level let and const into locals. The closing call is on a line of
/// its own so a trailing line comment in the script cannot swallow it. If the script throws, the probe clears the
/// tag itself once the script's task ends.
/// </summary>
public static class ScriptTag
{
    public static string Wrap(string owner, string script) =>
        "window.__slperfprobe&&window.__slperfprobe.ctx(" + JsText.Literal(owner) + ");\n" + script + "\n;window.__slperfprobe&&window.__slperfprobe.ctx(null);";
}
