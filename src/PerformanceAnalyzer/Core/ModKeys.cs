using System.Linq;

namespace PerformanceAnalyzer.Core;

public static class ModKeys
{
    /// <summary>
    /// The short key the panel and the deep profile use: the last part of the plugin id ("com.dev.survivallog.cheatmenu"
    /// gives "cheatmenu"), else the name, lowercased with only letters and digits.
    /// </summary>
    public static string For(string guid, string name)
    {
        var source = guid.Length > 0 ? guid.Substring(guid.LastIndexOf('.') + 1) : name;
        var key = Clean(source);

        if (key.Length == 0) key = Clean(name);

        return key.Length == 0 ? "mod" : key.Length > 40 ? key.Substring(0, 40) : key;
    }

    private static string Clean(string text) => new(text.ToLowerInvariant().Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray());
}
