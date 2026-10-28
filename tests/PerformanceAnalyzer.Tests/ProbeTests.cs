using Jint;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>
/// The web probe, run in Jint over the shipped file inside a small fake page: a clock the test moves, timers the test
/// fires, an event target, and animations the test starts and finishes.
/// </summary>
public class ProbeTests
{
    private const string FakePage = """
        var window = this;
        var clock = 0;
        var timers = [];
        var animations = [];
        var performance = { now: function () { return clock; } };
        function setTimeout(fn, ms) { timers.push({ fn: fn, at: clock + (ms || 0) }); return timers.length; }
        function setInterval() { return 0; }
        function clearInterval() {}
        function requestAnimationFrame() { return 0; }
        function queueMicrotask() {}
        function EventTarget() { this.l = {}; }
        EventTarget.prototype.addEventListener = function (type, fn) { (this.l[type] || (this.l[type] = [])).push(fn); };
        EventTarget.prototype.removeEventListener = function (type, fn) { var a = this.l[type] || []; var i = a.indexOf(fn); if (i >= 0) a.splice(i, 1); };
        function fire(target, type, e) { (target.l[type] || []).slice().forEach(function (fn) { fn.call(target, e); }); }
        function MutationObserver(cb) { this.cb = cb; }
        MutationObserver.prototype.observe = function () {};
        MutationObserver.prototype.disconnect = function () {};
        window.l = {};
        var document = new EventTarget();
        document.getElementsByTagName = function () { return []; };
        document.querySelectorAll = function () { return []; };
        document.getAnimations = function () { return animations.slice(); };
        // Runs every timer due by clock + ms, in time order, moving the clock to each.
        function advance(ms) {
          var end = clock + ms;
          for (;;) {
            timers.sort(function (a, b) { return a.at - b.at; });
            if (!timers.length || timers[0].at > end) break;
            var t = timers.shift();
            clock = t.at;
            t.fn();
          }
          clock = end;
        }
        function fade(property, ms, iterations) {
          var a = { transitionProperty: property, finished: { then: function (ok) { a.done = ok; } },
            effect: { target: { tagName: 'DIV', className: 'panel', textContent: 'Trade' }, getComputedTiming: function () { return { endTime: iterations === Infinity ? Infinity : ms }; } } };
          animations.push(a);
          return a;
        }
        function owner(key) { return window.__slperfprobe.summary().owners.filter(function (o) { return o.key === key; })[0]; }
        """;

    private static Engine Page()
    {
        var js = new Engine();

        js.Execute(FakePage);
        js.Execute(PageTests.Web("probe.js"));
        js.Execute("var P = window.__slperfprobe;");

        return js;
    }

    [Fact]
    public void Listeners_a_mod_adds_before_recording_still_have_that_mod_as_owner()
    {
        var js = Page();

        js.Execute("P.names({ aliases: { pkgbank: 'packagebank' }, known: [] }); P.ctx('pkgbank'); document.addEventListener('click', function () {}); P.ctx(null);");

        Assert.Equal(1, js.Evaluate("owner('packagebank').listeners").AsNumber());
        Assert.Equal(0, js.Evaluate("owner('packagebank').ms").AsNumber());
    }

    [Fact]
    public void Time_is_counted_only_while_recording_and_a_reset_keeps_the_live_listeners()
    {
        var js = Page();

        js.Execute("P.ctx('farmhub'); document.addEventListener('x', function () {}); clock += 4; P.ctx(null);");
        js.Execute("P.record(true); P.ctx('farmhub'); clock += 5; P.ctx(null);");

        Assert.Equal(5, js.Evaluate("owner('farmhub').ms").AsNumber());

        js.Execute("P.reset();");

        Assert.Equal(0, js.Evaluate("owner('farmhub').ms").AsNumber());
        Assert.Equal(1, js.Evaluate("owner('farmhub').listeners").AsNumber());
    }

    [Fact]
    public void The_state_says_whether_the_probe_has_the_names_and_is_recording()
    {
        var js = Page();

        Assert.Equal("", js.Evaluate("P.state()").AsString());

        js.Execute("P.names({ aliases: {}, known: [] });");
        Assert.Equal("n", js.Evaluate("P.state()").AsString());

        js.Execute("P.record(true);");
        Assert.Equal("nr", js.Evaluate("P.state()").AsString());
    }

    [Theory]
    [InlineData("__storagehubWords", "storagehub")]
    [InlineData("__slmodmenu", "toolkit")]
    [InlineData("__exiledtoolkitUi", "toolkit")]
    [InlineData("__sldefenseWords", "defensemanager")]
    [InlineData("__slmod", "")]
    [InlineData("__craftinghub", "")]
    public void A_window_global_names_its_mod_by_the_one_key_the_plugin_uses(string global, string owner)
    {
        var js = Page();

        js.Execute("P.names({ aliases: { modmenu: 'toolkit', defense: 'defensemanager' }, known: ['storagehub', 'defensemanager'] });");

        Assert.Equal(owner, js.Evaluate($"P.ownerOf('{global}')").AsString());
    }

    [Fact]
    public void A_click_is_timed_until_the_fade_it_started_has_finished()
    {
        var js = Page();

        js.Execute("""
            P.record(true);
            fade('transform', 200);
            clock = 1000;
            var button = { tagName: 'BUTTON', id: '', className: 'go big', textContent: '  Open   map ' };
            fire(window, 'click', { type: 'click', timeStamp: 998, composedPath: function () { return [button]; } });
            var opacity = fade('opacity', 300);
            fade('opacity', 100, Infinity);
            advance(320);
            opacity.done();
            advance(2000);
            var c = P.summary().interactions;
            var r = c.slowest[0];
            """);

        Assert.Equal(1, js.Evaluate("c.count").AsNumber());
        Assert.Equal("main", js.Evaluate("r.page").AsString());
        Assert.Equal("button.go \"Open map\"", js.Evaluate("r.target").AsString());
        Assert.Equal(2, js.Evaluate("r.delayMs").AsNumber());
        Assert.Equal(322, js.Evaluate("r.settleMs").AsNumber());
        Assert.Equal(1, js.Evaluate("r.animations").AsNumber());
        Assert.Equal("transition opacity on div.panel", js.Evaluate("r.fade").AsString());
        Assert.Equal("transition opacity on div.panel", js.Evaluate("c.fades[0].label").AsString());
        Assert.Equal(1, js.Evaluate("c.fades.length").AsNumber());
        Assert.Equal(322, js.Evaluate("c.pages[0].settle.max").AsNumber());
    }

    [Fact]
    public void Clicks_are_not_watched_while_not_recording()
    {
        var js = Page();

        js.Execute("fire(window, 'click', { type: 'click', timeStamp: 0 }); advance(5000);");

        Assert.Equal(0, js.Evaluate("P.summary().interactions.count").AsNumber());
    }
}
