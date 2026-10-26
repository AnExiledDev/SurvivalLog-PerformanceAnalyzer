using System.Reflection;
using Jint;
using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>The page side, run in Jint over the shipped files: the script tag and the panel's number formatting.</summary>
public class PageTests
{
    internal static string Web(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PerformanceAnalyzer.web." + name) ?? throw new InvalidOperationException(name);
        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    [Fact]
    public void A_tagged_script_opens_and_closes_its_owner_around_the_original_code()
    {
        var js = new Engine();

        js.Execute("var window = this; var calls = []; window.__slperfprobe = { ctx: function (o) { calls.push(o === null ? 'null' : o); } }; var ran = 0;");
        js.Execute(ScriptTag.Wrap("cheatmenu", "ran = ran + 1 // a trailing comment"));

        Assert.Equal("cheatmenu,null", js.Evaluate("calls.join(',')").AsString());
        Assert.Equal(1, js.Evaluate("ran").AsNumber());
    }

    [Fact]
    public void A_tagged_script_runs_unchanged_when_the_probe_is_not_in_the_page()
    {
        var js = new Engine();

        js.Execute("var window = this; var ran = 0;");
        js.Execute(ScriptTag.Wrap("cheatmenu", "ran = 2"));

        Assert.Equal(2, js.Evaluate("ran").AsNumber());
    }

    private static Engine Format()
    {
        var js = new Engine();

        js.Execute("var window = this;");
        js.Execute(Web("format.js"));
        js.Execute("var F = window.__slperfanalyzerFormat;");

        return js;
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(0.00412, "0.0041")]
    [InlineData(0.42, "0.420")]
    [InlineData(3.14159, "3.14")]
    [InlineData(48.25, "48.3")]
    [InlineData(1234.4, "1234")]
    public void Milliseconds_show_fewer_decimals_as_they_grow(double value, string text)
    {
        Assert.Equal(text, Format().Evaluate($"F.ms({value.ToString(System.Globalization.CultureInfo.InvariantCulture)})").AsString());
    }

    [Fact]
    public void Frame_times_past_one_60_fps_frame_warn_and_past_one_30_fps_frame_are_bad()
    {
        var js = Format();

        Assert.Equal("good", js.Evaluate("F.frameClass(16.6)").AsString());
        Assert.Equal("warn", js.Evaluate("F.frameClass(20)").AsString());
        Assert.Equal("bad", js.Evaluate("F.frameClass(40)").AsString());
    }

    [Fact]
    public void Rows_sort_numbers_high_to_low_and_text_a_to_z_and_filter_on_every_word()
    {
        var js = Format();

        Assert.Equal("3,2,1", js.Evaluate("F.sortRows([{a:1},{a:3},{a:2}], 'a').map(function (r) { return r.a; }).join(',')").AsString());
        Assert.Equal("1,2,3", js.Evaluate("F.sortRows([{a:1},{a:3},{a:2}], 'a', true).map(function (r) { return r.a; }).join(',')").AsString());
        Assert.Equal("a,b", js.Evaluate("F.sortRows([{n:'b'},{n:'a'}], 'n').map(function (r) { return r.n; }).join(',')").AsString());
        Assert.Equal("Farm.Update", js.Evaluate("F.filterRows([{name:'Farm.Update',kind:'Ticker'},{name:'Trap.Update',kind:'Prefix'}], 'update ticker', ['name','kind']).map(function (r) { return r.name; }).join(',')").AsString());
    }

    [Fact]
    public void Graph_points_scale_to_the_box_with_the_newest_frame_on_the_right()
    {
        Assert.Equal("0.0,50.0 50.0,25.0 100.0,0.0", Format().Evaluate("F.points([0, 10, 99], 100, 50, 20)").AsString());
    }

    [Fact]
    public void The_graph_top_rounds_up_to_a_readable_step()
    {
        var js = Format();

        Assert.Equal(16.7, js.Evaluate("F.graphMax([3, 5])").AsNumber());
        Assert.Equal(50, js.Evaluate("F.graphMax([40])").AsNumber());
        Assert.Equal(6000, js.Evaluate("F.graphMax([5500])").AsNumber());
    }

    [Fact]
    public void Memory_shows_a_dash_when_the_game_build_gives_no_reading_and_switches_to_gb_past_1024_mb()
    {
        var js = Format();

        Assert.Equal("-", js.Evaluate("F.mb(0)").AsString());
        Assert.Equal("-", js.Evaluate("F.mb(undefined)").AsString());
        Assert.Equal("42.5 MB", js.Evaluate("F.mb(42.5)").AsString());
        Assert.Equal("512 MB", js.Evaluate("F.mb(512.4)").AsString());
        Assert.Equal("2.00 GB", js.Evaluate("F.mb(2048)").AsString());
    }

    [Fact]
    public void A_seconds_graph_tops_out_at_one_two_or_five_times_a_power_of_ten_over_every_series()
    {
        var js = Format();

        Assert.Equal(50, js.Evaluate("F.niceMax([[10, 20]], 50)").AsNumber());
        Assert.Equal(100, js.Evaluate("F.niceMax([[10, 20], [75]], 60)").AsNumber());
        Assert.Equal(2000, js.Evaluate("F.niceMax([[1100], null], 100)").AsNumber());
        Assert.Equal(5000, js.Evaluate("F.niceMax([[2001]], 100)").AsNumber());
    }

    [Fact]
    public void Text_from_mods_is_escaped_before_it_reaches_the_panel()
    {
        Assert.Equal("&lt;img src=x onerror=&quot;a&quot;&gt;", Format().Evaluate("F.esc('<img src=x onerror=\"a\">')").AsString());
    }

    [Fact]
    public void The_panel_bundle_starts_with_the_style_and_carries_every_panel_script()
    {
        var bundle = PageBundle.Panel(Web);

        Assert.StartsWith("window.__slperfanalyzerCss=", bundle);
        Assert.Contains("window.__slperfanalyzerFormat =", bundle);
        Assert.Contains("window.__slperfanalyzer = {", bundle);
    }

    [Fact]
    public void Every_shipped_page_file_is_ascii()
    {
        foreach (var name in new[] { "probe.js", "format.js", "panel.js", "panel.css" }) Assert.All(Web(name), c => Assert.True(c < 128, name + " has a non-ASCII character"));
    }
}
