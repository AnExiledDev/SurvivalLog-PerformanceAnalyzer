using Jint;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>
/// The Methods tab's filter box and the game's keyboard. The game sends typed keys to the web UI only while it is told
/// a text box has focus, so the panel tells the plugin when the box takes the keyboard and when it lets go, the panel
/// closing included.
/// </summary>
public class PanelTypingTests
{
    private const string Focus = "slmod|perfanalyzer|focus|";

    // The shadow root keeps its listeners and which element has focus; timers wait until flush() runs them, the way the
    // browser runs them after the focus has moved.
    private const string FocusPage = """
        var posted = [];
        window.vuplex = { postMessage: function (m) { posted.push(m); } };
        var timers = [];
        setTimeout = function (callback) { timers.push(callback); return timers.length; };
        function flush() { var due = timers; timers = []; due.forEach(function (callback) { callback(); }); }
        var shadow;
        Node.prototype.attachShadow = function () {
          var found = {};
          shadow = {
            listeners: {},
            activeElement: null,
            addEventListener: function (type, callback) { (this.listeners[type] = this.listeners[type] || []).push(callback); },
            querySelector: function (selector) { return found[selector] = found[selector] || new Node(selector); },
            querySelectorAll: function () { return []; }
          };
          return shadow;
        };
        var box = { id: 'filter', tagName: 'INPUT' };
        function fire(type, target) { (shadow.listeners[type] || []).forEach(function (callback) { callback({ target: target }); }); }
        function focusFilter() { shadow.activeElement = box; fire('focusin', box); flush(); }
        function blurFilter() { shadow.activeElement = null; fire('focusout', box); flush(); }
        function focuses() { return posted.filter(function (m) { return m.indexOf('slmod|perfanalyzer|focus|') === 0; }).join(','); }
        """;

    private readonly Engine _js = new();

    public PanelTypingTests()
    {
        _js.Execute(PanelUiSizeTests.FakePage);
        _js.Execute(FocusPage);
        _js.Execute(PageTests.Web("format.js"));
        _js.Execute(PageTests.Web("panel.js"));
        _js.Execute("window.__slperfanalyzer.open({ tab: 'methods' }); posted = [];");
    }

    private string Focuses() => _js.Evaluate("focuses()").AsString();

    [Fact]
    public void Clicking_into_the_filter_asks_the_game_for_the_keyboard_and_clicking_out_gives_it_back()
    {
        _js.Execute("focusFilter();");

        Assert.Equal(Focus + "1", Focuses());

        _js.Execute("blurFilter();");

        Assert.Equal(Focus + "1," + Focus + "0", Focuses());
    }

    [Fact]
    public void Updates_while_typing_do_not_ask_again()
    {
        _js.Execute("focusFilter(); window.__slperfanalyzer.update({}); window.__slperfanalyzer.update({});");

        Assert.Equal(Focus + "1", Focuses());
    }

    [Fact]
    public void Closing_the_panel_while_typing_gives_the_keyboard_back()
    {
        // Hiding the panel fires no focusout: the box stays the active element.
        _js.Execute("focusFilter(); window.__slperfanalyzer.close();");

        Assert.Equal(Focus + "1," + Focus + "0", Focuses());
    }

    [Fact]
    public void Focus_on_something_else_in_the_panel_does_not_ask_for_the_keyboard()
    {
        _js.Execute("shadow.activeElement = { id: 'sort', tagName: 'BUTTON' }; fire('focusin', shadow.activeElement); flush();");

        Assert.Equal("", Focuses());
    }
}
