using System;
using System.Collections.Generic;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// Counts the scripts the game and the mods send into the web UI, by who sent them. A mod's script names its own
/// globals ("window.__slcheatmenu") or posts as itself ("slmod|cheatmenu|"), so those name the sender; a script with
/// neither is the game's.
/// Sending a script is cheap for the sender but the page has to parse and run every one, so a mod that sends a big
/// script many times a second slows the menus down even when its own C# side looks fast.
/// </summary>
public sealed class ScriptTraffic
{
    public const string GameOwner = "game";

    /// <summary>Exiled's Toolkit names its globals "__exiledtoolkit..." rather than "__sl...", and posts as "toolkit".</summary>
    public const string ToolkitOwner = "toolkit";

    private const string PostMarker = "slmod|";
    private const string ToolkitMarker = "exiledtoolkit";

    private readonly Dictionary<string, ScriptCounts> _byOwner = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Who sent a script, best evidence first: the mod a "slmod|key|" message in it posts as, then the lowercase run
    /// after the first "__sl" ("__slcheatmenuCss" gives "cheatmenu"; the shared "__slmod" is skipped), then a
    /// "__key" global of a loaded mod in <paramref name="known"/> ("__storagehubWords"). Exiled's Toolkit is only the
    /// sender when nothing else matches, since mods drive its globals from their own scripts. Every key goes through
    /// <see cref="WebOwners.Canonical"/>, so a mod is one row whichever name it used. Anything else is the game's.
    /// </summary>
    public static string OwnerOf(string? script, IReadOnlySet<string>? known = null)
    {
        if (string.IsNullOrEmpty(script)) return GameOwner;

        var toolkit = false;
        var posted = FirstPost(script, ref toolkit);

        if (posted != null) return posted;

        var global = FirstGlobal(script, known, ref toolkit);

        if (global != null) return global;

        return toolkit ? ToolkitOwner : GameOwner;
    }

    public IReadOnlySet<string> Known { get; private set; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The loaded mods' keys, so a "__key" global names its mod. Set once the plugins are read.</summary>
    public void SetKnown(IEnumerable<string> modKeys) => Known = WebOwners.Known(modKeys);

    private static string? FirstPost(string script, ref bool toolkit)
    {
        var from = 0;

        while (true)
        {
            var at = script.IndexOf(PostMarker, from, StringComparison.Ordinal);

            if (at < 0) return null;

            var start = at + PostMarker.Length;
            var end = KeyEnd(script, start);

            from = Math.Max(end, at + 1);

            if (end == start || end >= script.Length || script[end] != '|') continue;

            var key = WebOwners.Canonical(script.Substring(start, end - start));

            if (key == ToolkitOwner) toolkit = true;
            else return key;
        }
    }

    /// <summary>The first "__sl" owner wins over the first known "__key" one, wherever each sits in the script.</summary>
    private static string? FirstGlobal(string script, IReadOnlySet<string>? known, ref bool toolkit)
    {
        string? plain = null;
        var from = 0;

        while (true)
        {
            var at = script.IndexOf("__", from, StringComparison.Ordinal);

            if (at < 0) return plain;

            var start = at + 2;
            var end = KeyEnd(script, start);

            from = Math.Max(end, at + 1);

            if (end == start) continue;

            var run = script.Substring(start, end - start);

            if (run.StartsWith(ToolkitMarker, StringComparison.Ordinal))
            {
                toolkit = true;

                continue;
            }

            if (run.Length > 2 && run.StartsWith("sl", StringComparison.Ordinal))
            {
                // "__slmod" is the shared message prefix some scripts carry, not a mod's own global.
                if (run == "slmod") continue;

                var key = WebOwners.Canonical(run.Substring(2));

                if (key == ToolkitOwner) toolkit = true;
                else return key;

                continue;
            }

            if (plain == null && known != null)
            {
                var key = WebOwners.Canonical(run);

                if (key != ToolkitOwner && known.Contains(key)) plain = key;
            }
        }
    }

    private static int KeyEnd(string script, int start)
    {
        var end = start;

        while (end < script.Length && end - start < 40 && IsKeyChar(script[end])) end++;

        return end;
    }

    /// <summary>Safe from any thread: the web view may be driven off the main thread.</summary>
    public void Record(string owner, int characters)
    {
        lock (_gate)
        {
            if (!_byOwner.TryGetValue(owner, out var counts)) _byOwner[owner] = counts = new ScriptCounts();

            counts.Calls++;
            counts.Characters += characters;

            if (characters > counts.Largest) counts.Largest = characters;
        }
    }

    public void Clear()
    {
        lock (_gate) _byOwner.Clear();
    }

    /// <summary>A copy, largest sender first.</summary>
    public List<ScriptRow> Rows()
    {
        var rows = new List<ScriptRow>();

        lock (_gate)
        {
            foreach (var pair in _byOwner) rows.Add(new ScriptRow(pair.Key, pair.Value.Calls, pair.Value.Characters, pair.Value.Largest));
        }

        rows.Sort((a, b) => b.Characters.CompareTo(a.Characters));

        return rows;
    }

    private static bool IsKeyChar(char c) => c is >= 'a' and <= 'z' or >= '0' and <= '9';

    private sealed class ScriptCounts
    {
        public long Calls;
        public long Characters;
        public int Largest;
    }
}

public readonly record struct ScriptRow(string Owner, long Calls, long Characters, int Largest);
