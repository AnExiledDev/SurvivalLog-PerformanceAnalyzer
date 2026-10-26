namespace PerformanceAnalyzer.Core;

/// <summary>What a probe times.</summary>
public enum ProbeKind
{
    /// <summary>A mod's Harmony prefix on some method.</summary>
    Prefix,

    /// <summary>A mod's Harmony postfix.</summary>
    Postfix,

    /// <summary>A mod's Harmony finalizer.</summary>
    Finalizer,

    /// <summary>Update, LateUpdate, FixedUpdate or OnGUI of a mod's own Unity component.</summary>
    Ticker,

    /// <summary>
    /// The managed wrapper Harmony builds around a hooked game method, minus what runs inside it that has its own probe
    /// (the patches and the game's own code). What is left is the cost of hooking that method at all.
    /// </summary>
    Hook,

    /// <summary>The game's own code inside a hooked game method.</summary>
    Game,

    /// <summary>A mod method that another mod patched: its own body.</summary>
    ModMethod,

    /// <summary>Any method of a mod picked for a deep profile.</summary>
    Deep,

    /// <summary>The analyzer's own work.</summary>
    Analyzer,

    /// <summary>A section of its own code a mod asked to have timed, through a marker.</summary>
    Marker,
}

/// <param name="Name">Readable method name, "Type.Method".</param>
/// <param name="Target">For a patch or hook, the method it is on; "" otherwise.</param>
/// <param name="Owners">Mod ids the probe's time is charged to, split evenly. Never empty.</param>
public sealed record ProbeInfo(string Name, ProbeKind Kind, string Target, int[] Owners);

/// <param name="Key">Short lowercase key, also used to match a mod's web UI script ("cheatmenu").</param>
public sealed record ModInfo(string Key, string Name, string Version, string Guid);

public static class ProbeKinds
{
    public static string Label(ProbeKind kind) => kind switch
    {
        ProbeKind.Prefix => "prefix",
        ProbeKind.Postfix => "postfix",
        ProbeKind.Finalizer => "finalizer",
        ProbeKind.Ticker => "update",
        ProbeKind.Hook => "hook",
        ProbeKind.Game => "game",
        ProbeKind.ModMethod => "patched",
        ProbeKind.Deep => "deep",
        ProbeKind.Analyzer => "analyzer",
        ProbeKind.Marker => "marker",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
