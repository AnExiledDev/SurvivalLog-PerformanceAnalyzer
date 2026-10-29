using GameCore.HotUpdate;
using GameCore.HotUpdate.ReduxUI;
using GameCore.Scripts;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// Sends the keyboard to the web UI while the panel's filter box has focus, the way the game's own crafting search
/// does, and gives it back as it was found. The game keeps the system's input method (for typing Chinese) off while
/// playing and turns it on only while one of its own text boxes has focus, so the filter box turns it on too, and off
/// again when it lets go.
/// </summary>
internal sealed class WebKeyboard
{
    private bool? _wasEnabled;

    public void Claim()
    {
        var layer = GameSingleton.Existing<ReduxUISystem>()?.GetWebUILayer();

        if (layer == null) return;

        _wasEnabled ??= layer.IsKeyboardEnabled;
        layer.SetKeyboardEnabled(true);
        ImeGuard.SetEnabled(true);
    }

    public void Release()
    {
        if (_wasEnabled is not { } wasEnabled) return;

        _wasEnabled = null;
        GameSingleton.Existing<ReduxUISystem>()?.GetWebUILayer()?.SetKeyboardEnabled(wasEnabled);
        ImeGuard.SetEnabled(false);
    }
}
