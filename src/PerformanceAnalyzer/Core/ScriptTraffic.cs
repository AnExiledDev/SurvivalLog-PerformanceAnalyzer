using System;
using System.Collections.Generic;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// Counts the scripts the game and the mods send into the web UI, by who sent them. A mod's script names its own
/// global ("window.__slcheatmenu"), so the first such name is the sender; a script with none is the game's.
/// Sending a script is cheap for the sender but the page has to parse and run every one, so a mod that sends a big
/// script many times a second slows the menus down even when its own C# side looks fast.
/// </summary>
public sealed class ScriptTraffic
{
    public const string GameOwner = "game";

    private const string Marker = "__sl";

    private readonly Dictionary<string, ScriptCounts> _byOwner = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Who sent a script: the lowercase run after the first "__sl", or <see cref="GameOwner"/>. Mods name extra globals
    /// with a capitalized suffix ("__slcheatmenuCss"), so the suffix is left off.
    /// </summary>
    public static string OwnerOf(string? script)
    {
        if (string.IsNullOrEmpty(script)) return GameOwner;

        var from = 0;

        while (true)
        {
            var at = script.IndexOf(Marker, from, StringComparison.Ordinal);

            if (at < 0) return GameOwner;

            var start = at + Marker.Length;
            var end = start;

            while (end < script.Length && end - start < 40 && IsKeyChar(script[end])) end++;

            // "__slmod" is the shared message prefix some scripts carry, not a mod's own global.
            if (end > start && !IsSharedName(script, start, end)) return script.Substring(start, end - start);

            from = Math.Max(end, at + 1);
        }
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

    private static bool IsSharedName(string script, int start, int end) =>
        end - start == 3 && string.CompareOrdinal(script, start, "mod", 0, 3) == 0;

    private sealed class ScriptCounts
    {
        public long Calls;
        public long Characters;
        public int Largest;
    }
}

public readonly record struct ScriptRow(string Owner, long Calls, long Characters, int Largest);
