using GameCore.HotUpdate;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// The game's singletons build themselves on first read. A world read before the game makes it is built before the
/// configs load, throws halfway through, and leaves the managers it already built answering the game's events for the
/// rest of the session, so one trade or one order is carried out several times. So a mod only reads what the game
/// already made.
/// </summary>
internal static class GameSingleton
{
    public static T? Existing<T>() where T : BaseSingleton<T>, new() =>
        BaseSingleton<T>.IsInstanceCreated ? BaseSingleton<T>.Instance : null;
}
