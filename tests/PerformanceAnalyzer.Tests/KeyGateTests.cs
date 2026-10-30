using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>
/// The panel and trace keys: a key set to None never fires, and a key pressed while a text box has the keyboard is
/// text, so it must not open the panel or start a trace.
/// </summary>
public class KeyGateTests
{
    private enum Key { None, Insert }

    private int _downAsks;
    private int _typingAsks;

    [Fact]
    public void A_set_key_fires_when_pressed_and_nobody_is_typing() => Assert.True(Fires(Key.Insert, isDown: true, isTyping: false));

    [Fact]
    public void A_set_key_does_not_fire_while_the_player_types_in_a_text_box() => Assert.False(Fires(Key.Insert, isDown: true, isTyping: true));

    [Fact]
    public void A_key_that_is_not_pressed_does_not_fire() => Assert.False(Fires(Key.Insert, isDown: false, isTyping: false));

    [Fact]
    public void A_key_set_to_None_never_fires_and_reads_nothing()
    {
        Assert.False(Fires(Key.None, isDown: true, isTyping: false));
        Assert.Equal(0, _downAsks);
        Assert.Equal(0, _typingAsks);
    }

    [Fact]
    public void Typing_is_only_asked_once_the_key_went_down()
    {
        Fires(Key.Insert, isDown: false, isTyping: true);

        Assert.Equal(1, _downAsks);
        Assert.Equal(0, _typingAsks);
    }

    private bool Fires(Key key, bool isDown, bool isTyping) =>
        KeyGate.Fires(key, Key.None, _ => { _downAsks++; return isDown; }, () => { _typingAsks++; return isTyping; });
}
