using System.Globalization;
using System.Text.Json;
using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>Parsing the page's messages, naming script senders, the report and the panel's JSON.</summary>
public class TextTests
{
    [Theory]
    [InlineData("window.__slcheatmenuCss=\"...\";", "cheatmenu")]
    [InlineData("window.__slcheatmenu&&window.__slcheatmenu.update({})", "cheatmenu")]
    [InlineData("window.__slmodmenu.open()", "modmenu")]
    [InlineData("window.__slmod.x; window.__sltraphub.y()", "traphub")]
    [InlineData("window.__slfarmhubLogic.x()", "farmhub")]
    [InlineData("document.title='x'", "game")]
    [InlineData("", "game")]
    public void A_script_belongs_to_the_mod_whose_window_object_it_names(string script, string owner)
    {
        Assert.Equal(owner, ScriptTraffic.OwnerOf(script));
    }

    [Fact]
    public void Script_traffic_is_counted_per_sender_largest_first()
    {
        var traffic = new ScriptTraffic();

        traffic.Record("game", 10);
        traffic.Record("cheatmenu", 500);
        traffic.Record("cheatmenu", 100);

        var rows = traffic.Rows();

        Assert.Equal("cheatmenu", rows[0].Owner);
        Assert.Equal(2, rows[0].Calls);
        Assert.Equal(600, rows[0].Characters);
        Assert.Equal(500, rows[0].Largest);
    }

    [Theory]
    [InlineData("slmod|perfanalyzer|unhook", typeof(PageCommand.Unhook))]
    [InlineData("slmod|perfanalyzer|start", typeof(PageCommand.Start))]
    [InlineData("slmod|perfanalyzer|copy", typeof(PageCommand.CopyReport))]
    [InlineData("slmod|perfanalyzer|start|now", typeof(PageCommand.Unknown))]
    [InlineData("slmod|perfanalyzer|tab|nope", typeof(PageCommand.Unknown))]
    [InlineData("slmod|perfanalyzer|deep|Bad Key!", typeof(PageCommand.Unknown))]
    [InlineData("slmod|perfanalyzer|overlay|2", typeof(PageCommand.Unknown))]
    public void Page_messages_parse_to_one_command_and_anything_else_is_unknown(string message, Type expected)
    {
        Assert.IsType(expected, PageCommand.Parse(message));
    }

    [Fact]
    public void Page_message_arguments_are_kept()
    {
        Assert.Equal(new PageCommand.ShowTab(PanelTab.Methods), PageCommand.Parse("slmod|perfanalyzer|tab|methods"));
        Assert.Equal(new PageCommand.Deep("cheatmenu"), PageCommand.Parse("slmod|perfanalyzer|deep|cheatmenu"));
        Assert.Equal(new PageCommand.Deep(""), PageCommand.Parse("slmod|perfanalyzer|deep|"));
        Assert.Equal(new PageCommand.Overlay(true), PageCommand.Parse("slmod|perfanalyzer|overlay|1"));
        Assert.Equal(new PageCommand.Web("{\"a\":\"x|y\"}"), PageCommand.Parse("slmod|perfanalyzer|web|{\"a\":\"x|y\"}"));
    }

    [Theory]
    [InlineData("com.dev.survivallog.cheatmenu", "Cheat Menu", "cheatmenu")]
    [InlineData("", "Some Library", "somelibrary")]
    [InlineData("com.x.!!!", "Fancy Mod 2", "fancymod2")]
    [InlineData("", "???", "mod")]
    public void Mod_keys_come_from_the_plugin_id_else_the_name(string guid, string name, string key)
    {
        Assert.Equal(key, ModKeys.For(guid, name));
    }

    private static Recorder Recorded()
    {
        var recorder = new Recorder(1000, 256, 50);
        var mod = recorder.AddMod(new ModInfo("moda", "Mod A", "1.0", "com.test.moda"));
        var p = recorder.AddProbe(new ProbeInfo("ModA.Ticker.Update", ProbeKind.Ticker, "", new[] { mod }));

        recorder.Start(0);
        recorder.EndFrame(0, default);

        long now = 0;

        for (var i = 0; i < 120; i++)
        {
            recorder.End(recorder.Begin(p, now, 0), now + 1500, 0);
            now += i == 60 ? 90_000 : 16_000;
            recorder.EndFrame(now, new FrameContext(0, 0, default, "Main"));
        }

        return recorder;
    }

    private static InstrumentStatus Status => InstrumentStatus.None with { Hooks = 3, Tickers = 1, Notes = new[] { "a note" } };

    private static string ReportText(Recorder recorder, string webJson = "") => Report.Text(recorder, new ReportInput(2_000_000, new DateTime(2026, 10, 25, 12, 0, 0), "0.1.0", "1.1.19611", "Windows; CPU", Status, new[] { new ScriptRow("cheatmenu", 4, 4000, 2000) }, webJson, new[] { new ModInfo("moda", "Mod A", "1.0", "com.test.moda") }));

    [Fact]
    public void The_report_names_the_slow_mod_and_the_hitch_in_plain_ascii()
    {
        var text = ReportText(Recorded(), "{\"fps\":60,\"seconds\":10,\"owners\":[{\"key\":\"cheatmenu\",\"ms\":12.5,\"calls\":3}]}");

        foreach (var heading in new[] { "Verdict", "Frame time (ms)", "Mods (time in the mod's own code)", "Scripts sent to the web UI", "Web UI (inside the page)", "What was timed" }) Assert.Contains(heading, text);

        Assert.Contains("Mod A", text);
        Assert.Contains("ModA.Ticker.Update", text);
        Assert.Contains("1 frames took 50 ms or more", text);
        Assert.Contains("cheatmenu", text);
        Assert.All(text, c => Assert.True(c < 128, "non-ASCII character in the report: " + (int)c));
    }

    [Fact]
    public void A_broken_web_summary_is_reported_not_thrown()
    {
        Assert.Contains("could not be read", ReportText(Recorded(), "{not json"));
    }

    [Fact]
    public void Reports_and_snapshots_use_dots_for_decimals_whatever_the_machines_language()
    {
        var before = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");

            var recorder = Recorded();

            Assert.Contains("p50 16.00", ReportText(recorder));
            JsonDocument.Parse(Snapshot.Json(recorder, Input(PanelTab.Overview))).Dispose();
            Assert.DoesNotContain(";", Report.FramesCsv(recorder));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    private static SnapshotInput Input(PanelTab tab) => new(2_000_000, tab, false, Status, new[] { new ScriptRow("game", 1, 10, 10) }, false, "0.1.0");

    [Theory]
    [InlineData(PanelTab.Overview, "graph")]
    [InlineData(PanelTab.Mods, "mods")]
    [InlineData(PanelTab.Methods, "methods")]
    [InlineData(PanelTab.Hitches, "hitches")]
    [InlineData(PanelTab.Web, "scripts")]
    [InlineData(PanelTab.Game, "scenes")]
    public void Each_tab_gets_its_own_list_and_only_that_tab(PanelTab tab, string list)
    {
        using var doc = JsonDocument.Parse(Snapshot.Json(Recorded(), Input(tab)));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty(list, out _));
        Assert.Equal(tab != PanelTab.Methods, !root.TryGetProperty("methods", out _));
        Assert.Equal(tab != PanelTab.Hitches, !root.TryGetProperty("hitches", out _));

        // The overlay draws from these whatever the tab.
        Assert.True(root.TryGetProperty("lastMs", out _));
        Assert.Equal("Mod A", root.GetProperty("topMod").GetProperty("name").GetString());
    }

    [Fact]
    public void The_hitch_list_names_the_mod_behind_each_hitch()
    {
        using var doc = JsonDocument.Parse(Snapshot.Json(Recorded(), Input(PanelTab.Hitches)));
        var hitch = doc.RootElement.GetProperty("hitches")[0];

        Assert.Equal(90, hitch.GetProperty("ms").GetDouble(), 1);
        Assert.Equal("Main", hitch.GetProperty("scene").GetString());
        Assert.Equal("ModA.Ticker.Update", hitch.GetProperty("top")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void The_verdict_asks_for_more_frames_before_judging()
    {
        var recorder = new Recorder(1000, 64, 50);

        Assert.StartsWith("Not recording", Assert.Single(Analysis.Verdict(recorder, 0)));
        recorder.Start(0);
        Assert.StartsWith("Recording", Assert.Single(Analysis.Verdict(recorder, 0)));
    }

    [Fact]
    public void The_frames_file_has_a_column_per_mod_and_a_line_per_frame()
    {
        var lines = Report.FramesCsv(Recorded()).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("moda_ms", lines[0]);
        Assert.Equal(121, lines.Length);
    }
}
