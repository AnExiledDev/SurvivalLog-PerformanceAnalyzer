using System.Text.RegularExpressions;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// The browser's launch arguments with the remote debugging port in them. The game and WebUI Doctor each set the
/// launch arguments before the browser starts, and that drops the port the analyzer asked for, so the port goes into
/// every set of arguments instead.
/// </summary>
public static class DebugPort
{
    private static readonly Regex PortFlag = new(@"--remote-debugging-port=\S*", RegexOptions.CultureInvariant);

    public static string With(string? arguments, int port)
    {
        var flag = "--remote-debugging-port=" + port;
        var text = (arguments ?? "").Trim();

        if (PortFlag.IsMatch(text)) return PortFlag.Replace(text, flag);

        return text.Length == 0 ? flag : text + " " + flag;
    }
}
