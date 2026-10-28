using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// One name per mod on the web side. A mod's page globals, its messages to the game and its plugin id do not always
/// use the same word: Defense Manager's globals are "__sldefense" but it posts as "defensemanager", and Exiled's
/// Toolkit shows up as "toolkit", "toolkitmenu", "modmenu" and "exiledtoolkit". Every such word maps to the key the
/// plugin id gives, so each mod is one row, except the Toolkit, which keeps the "toolkit" the reports have always used.
/// </summary>
public static class WebOwners
{
    private static readonly Dictionary<string, string> AliasMap = new(StringComparer.Ordinal)
    {
        ["defense"] = "defensemanager",
        ["notify"] = "notifications",
        ["pkgbank"] = "packagebank",
        ["nlp"] = "newloopplus",
        ["st"] = "statustimers",
        ["machineoverview"] = "machinespeed",
        ["fitness"] = "fitnessbar",
        ["modmenu"] = ScriptTraffic.ToolkitOwner,
        ["toolkitmenu"] = ScriptTraffic.ToolkitOwner,
        ["exiledtoolkit"] = ScriptTraffic.ToolkitOwner,
    };

    public static IReadOnlyDictionary<string, string> Aliases => AliasMap;

    public static string Canonical(string key) => AliasMap.TryGetValue(key, out var to) ? to : key;

    /// <summary>The loaded mods' keys as the web side names them.</summary>
    public static HashSet<string> Known(IEnumerable<string> modKeys) => new(modKeys.Select(Canonical), StringComparer.Ordinal);

    /// <summary>The table the web probe needs to name owners the same way: {"aliases":{...},"known":[...]}.</summary>
    public static string ProbeJson(IEnumerable<string> known)
    {
        var json = new StringBuilder("{\"aliases\":{");
        var first = true;

        foreach (var pair in AliasMap.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!first) json.Append(',');

            first = false;
            json.Append(JsText.Literal(pair.Key)).Append(':').Append(JsText.Literal(pair.Value));
        }

        json.Append("},\"known\":[");
        json.Append(string.Join(",", known.OrderBy(k => k, StringComparer.Ordinal).Select(JsText.Literal)));

        return json.Append("]}").ToString();
    }
}
