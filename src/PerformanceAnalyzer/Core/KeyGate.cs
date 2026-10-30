using System;
using System.Collections.Generic;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// When one of the mod's keys counts as pressed: it is set, it went down this frame, and no text box has the keyboard,
/// so a letter typed into a search box is not also taken as a key. Generic over the key type so the tests need no Unity.
/// </summary>
public static class KeyGate
{
    /// <summary>
    /// Asks <paramref name="isDown"/> only for a key that is set and <paramref name="isTyping"/> only once the key went
    /// down, so an unset key costs nothing and the typing check runs on a press, not every frame.
    /// </summary>
    public static bool Fires<TKey>(TKey key, TKey none, Func<TKey, bool> isDown, Func<bool> isTyping) where TKey : struct =>
        !EqualityComparer<TKey>.Default.Equals(key, none) && isDown(key) && !isTyping();
}
