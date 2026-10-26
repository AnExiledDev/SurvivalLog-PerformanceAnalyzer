using System;
using System.Globalization;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// Reports and the panel use "1.5", never "1,5", whatever the player's Windows region is. Interpolated strings read
/// the thread's culture, so the writers run under the invariant one.
/// </summary>
public static class Culture
{
    public static T Invariant<T>(Func<T> write)
    {
        var previous = CultureInfo.CurrentCulture;

        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        try
        {
            return write();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
