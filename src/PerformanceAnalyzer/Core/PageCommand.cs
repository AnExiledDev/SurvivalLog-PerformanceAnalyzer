using System;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// What the page asks for. The panel and the web probe send "slmod|perfanalyzer|" followed by a verb and its
/// arguments; anything else parses to Unknown so it can be logged rather than acted on.
/// </summary>
public abstract record PageCommand
{
    public const string Prefix = "slmod|perfanalyzer|";

    private PageCommand()
    {
    }

    public sealed record Close : PageCommand;

    public sealed record Ready : PageCommand;

    /// <summary>The panel script is missing from the page and must be sent again.</summary>
    public sealed record NoScript : PageCommand;

    /// <summary>The web probe is missing from the page and must be sent again.</summary>
    public sealed record NoProbe : PageCommand;

    public sealed record Start : PageCommand;

    public sealed record Stop : PageCommand;

    public sealed record Reset : PageCommand;

    /// <summary>Stop and remove every probe, so the game runs as if the analyzer were idle.</summary>
    public sealed record Unhook : PageCommand;

    public sealed record CopyReport : PageCommand;

    public sealed record SaveReport : PageCommand;

    /// <summary>Which tab the panel shows, so each update carries only what that tab draws.</summary>
    public sealed record ShowTab(PanelTab Tab) : PageCommand;

    /// <summary>Profile every method of one mod; an empty key turns the deep profile off.</summary>
    public sealed record Deep(string ModKey) : PageCommand;

    public sealed record Overlay(bool IsOn) : PageCommand;

    /// <summary>The web probe's summary of the last second, as JSON.</summary>
    public sealed record Web(string Json) : PageCommand;

    public sealed record Unknown(string Text) : PageCommand;

    public static bool IsOurs(string? message) => message is not null && message.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Parses a whole message, prefix included. JSON goes last, so it may hold the separator itself.</summary>
    public static PageCommand Parse(string message)
    {
        if (!IsOurs(message)) return new Unknown(message);

        var rest = message.Substring(Prefix.Length);
        var verb = Head(ref rest);
        var hasNoArgs = rest.Length == 0;

        return verb switch
        {
            "close" when hasNoArgs => new Close(),
            "ready" when hasNoArgs => new Ready(),
            "noscript" when hasNoArgs => new NoScript(),
            "noprobe" when hasNoArgs => new NoProbe(),
            "start" when hasNoArgs => new Start(),
            "stop" when hasNoArgs => new Stop(),
            "reset" when hasNoArgs => new Reset(),
            "unhook" when hasNoArgs => new Unhook(),
            "copy" when hasNoArgs => new CopyReport(),
            "save" when hasNoArgs => new SaveReport(),
            "tab" when Tabs.TryParse(rest, out var tab) => new ShowTab(tab),
            "deep" when IsModKey(rest) => new Deep(rest),
            "overlay" when rest is "0" or "1" => new Overlay(rest == "1"),
            "web" when rest.StartsWith("{", StringComparison.Ordinal) => new Web(rest),
            _ => new Unknown(message),
        };
    }

    /// <summary>A mod key is short lowercase letters and digits, or empty.</summary>
    public static bool IsModKey(string key)
    {
        if (key.Length > 64) return false;

        foreach (var c in key)
        {
            if (c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.')) return false;
        }

        return true;
    }

    /// <summary>Takes the text up to the next separator off the front of <paramref name="rest"/>.</summary>
    private static string Head(ref string rest)
    {
        var at = rest.IndexOf('|');

        if (at < 0)
        {
            var all = rest;
            rest = "";

            return all;
        }

        var head = rest.Substring(0, at);
        rest = rest.Substring(at + 1);

        return head;
    }
}

public enum PanelTab
{
    Overview,
    Game,
    Mods,
    Methods,
    Hitches,
    Web,
    Report,
}

public static class Tabs
{
    public static bool TryParse(string text, out PanelTab tab)
    {
        foreach (PanelTab candidate in Enum.GetValues(typeof(PanelTab)))
        {
            if (!string.Equals(Key(candidate), text, StringComparison.Ordinal)) continue;

            tab = candidate;

            return true;
        }

        tab = PanelTab.Overview;

        return false;
    }

    public static string Key(PanelTab tab) => tab.ToString().ToLowerInvariant();
}
