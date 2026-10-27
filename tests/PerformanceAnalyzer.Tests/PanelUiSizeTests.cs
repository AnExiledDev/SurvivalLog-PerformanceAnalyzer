using Jint;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>The panel, its overlay and its notices are drawn at the player's UI size from Exiled's Toolkit.</summary>
public class PanelUiSizeTests
{
    // Just enough of Root.html for panel.js to load, open and update: elements that keep their children, and a shadow
    // root whose every element is a plain stand-in.
    private const string FakePage = """
        var window = this;
        window.vuplex = { postMessage: function () {} };
        function setTimeout() { return 1; }
        function clearTimeout() {}
        function Node(tagName) {
          this.tagName = tagName;
          this.style = {};
          this.children = [];
          this.parentNode = null;
          this.classList = { toggle: function () {} };
        }
        Node.prototype.appendChild = function (child) {
          if (child.parentNode) child.parentNode.removeChild(child);
          child.parentNode = this;
          this.children.push(child);
          return child;
        };
        Node.prototype.removeChild = function (child) {
          this.children = this.children.filter(function (c) { return c !== child; });
          child.parentNode = null;
          return child;
        };
        Node.prototype.contains = function (other) {
          for (var node = other; node; node = node.parentNode) if (node === this) return true;
          return false;
        };
        Node.prototype.attachShadow = function () {
          var found = {};
          return {
            addEventListener: function () {},
            querySelector: function (selector) { return found[selector] = found[selector] || new Node(selector); },
            querySelectorAll: function () { return []; }
          };
        };
        var document = {
          body: new Node('BODY'),
          documentElement: new Node('HTML'),
          createElement: function (tagName) { return new Node(tagName); },
          addEventListener: function () {}
        };
        function host() { return document.body.children[0]; }
        """;

    // Stands in for Exiled's Toolkit's window.ExiledUi and keeps what each call handed it.
    private const string FakeUi = "var handed = []; window.ExiledUi = { size: function (el, options) { handed.push({ el: el, options: options }); } }";

    private static Engine Page(bool hasToolkit)
    {
        var js = new Engine();

        js.Execute(FakePage);
        if (hasToolkit) js.Execute(FakeUi);
        js.Execute(PageTests.Web("format.js"));
        js.Execute(PageTests.Web("panel.js"));

        return js;
    }

    private static bool Is(Engine js, string expression) => js.Evaluate($"!!({expression})").AsBoolean();

    [Fact]
    public void The_host_is_handed_to_the_toolkit_once_and_never_shrunk()
    {
        var js = Page(hasToolkit: true);

        js.Execute("window.__slperfanalyzer.open({ key: 'F10' }); window.__slperfanalyzer.close(); window.__slperfanalyzer.update({})");

        // Opening and updating find the host still in the page, so the Toolkit is not handed it again.
        Assert.Equal(1, js.Evaluate("handed.length").AsNumber());
        Assert.Equal("slperfanalyzer-host", js.Evaluate("handed[0].el.id").AsString());
        // The panel is pinned inside the window's edges, so it always fits.
        Assert.True(Is(js, "handed[0].options.fit === false"));
    }

    [Fact]
    public void A_host_the_page_lost_is_handed_again_when_it_goes_back_in()
    {
        var js = Page(hasToolkit: true);
        js.Execute("var lost = host(); document.body.removeChild(lost)");

        js.Execute("window.__slperfanalyzer.open({}); window.__slperfanalyzer.close()");
        js.Execute("document.body.removeChild(lost); window.__slperfanalyzer.update({})");

        Assert.Equal(3, js.Evaluate("handed.length").AsNumber());
        Assert.True(Is(js, "host() === lost && handed[1].el === lost && handed[2].el === lost"));
        Assert.True(Is(js, "handed[1].options.fit === false && handed[2].options.fit === false"));
    }

    [Fact]
    public void Before_the_toolkit_has_loaded_the_host_waits_in_the_list_it_reads()
    {
        var js = Page(hasToolkit: false);

        Assert.Equal(1, js.Evaluate("window.__exiledUiWait.length").AsNumber());
        Assert.True(Is(js, "window.__exiledUiWait[0][0] === host()"));
        Assert.True(Is(js, "window.__exiledUiWait[0][1].fit === false"));
    }
}
