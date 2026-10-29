using System;
using System.IO;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// The mod's own UI size (Interface &gt; UiScale), handed to Exiled's Toolkit 0.11.0 or newer, which then draws the
/// panels the page script hands it with <c>mod: "PerformanceAnalyzer"</c> at that size instead of the Toolkit's UiScale. 0 has them
/// follow the Toolkit's. Without the Toolkit, or with an older one, the setting does nothing. Only <see cref="SetWithToolkit"/> names a Toolkit type, so the mod loads without it.
/// </summary>
internal static class OptionalUiSize
{
    /// <summary>The name the page script hands its panels over with.</summary>
    public const string Mod = "PerformanceAnalyzer";

    private static readonly Lazy<ManualLogSource> Log = new(() => Logger.CreateLogSource(typeof(OptionalUiSize).Assembly.GetName().Name ?? "UiSize"));
    private static bool _isUnavailable;
    private static bool _hasSent;

    public static void Bind(ConfigFile config)
    {
        var entry = config.Bind("Interface", "UiScale", 0, new ConfigDescription(
            "How big Performance Analyzer's own windows, panels and messages are drawn, in percent, from 75 to 200. At 0 they follow the UI size set in Exiled's Toolkit (its UiScale). Needs Exiled's Toolkit 0.11.0 or newer.\n" +
            "Performance Analyzer 自己的窗口、面板和提示的显示大小（百分比），范围 75 到 200。设为 0 时跟随 Exiled's Toolkit 的界面大小设置（它的 UiScale）。需要 Exiled's Toolkit 0.11.0 或更新版本。",
            new AcceptableValueRange<int>(0, 200)));

        OptionalLive.Watch(entry, Set);
    }

    // 0 at start is what the Toolkit has anyway, so nothing is sent until there is a size to send.
    private static void Set(int percent)
    {
        if (_isUnavailable || !OptionalLive.IsToolkitInstalled || (percent <= 0 && !_hasSent)) return;

        try
        {
            SetWithToolkit(percent);
            _hasSent = true;
        }
        catch (Exception ex) when (ex is TypeLoadException or MissingMethodException or FileNotFoundException or FileLoadException or InvalidOperationException)
        {
            _isUnavailable = true;
            Log.Value.LogWarning("UiScale under Interface needs Exiled's Toolkit 0.11.0 or newer, so it does nothing: " + ex.Message);
        }
    }

    // Kept in its own method so ModUiSize is only looked up when there is a size to send.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SetWithToolkit(int percent) => ExiledToolkit.ModUiSize.Set(Mod, percent);
}
