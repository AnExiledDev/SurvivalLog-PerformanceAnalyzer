using System.Linq;
using System.Text.Json;
using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>
/// What the analyzer learns about the web UI: the page that was showing, slow page messages, the browser's own
/// counters, Exiled's Toolkit as a script sender, and each method's worst call.
/// </summary>
public class WebSideTests
{
    private const double TicksPerMs = 1000;

    private static long Ms(double ms) => (long)(ms * TicksPerMs);

    private static (Recorder Recorder, int Mod) NewRecorder()
    {
        var recorder = new Recorder(TicksPerMs, 64, 50);
        var mod = recorder.AddMod(new ModInfo("moda", "Mod A", "1.0", "com.test.moda"));

        recorder.Start(0);
        recorder.EndFrame(0, default);

        return (recorder, mod);
    }

    private static void Frame(Recorder recorder, double endMs, string scene = "Main") =>
        recorder.EndFrame(Ms(endMs), new FrameContext(0, 0, default, scene));

    private static string Message(params string[] parts) => string.Join(SlowMessage.Separator, parts);

    [Theory]
    [InlineData("window.__exiledtoolkitToasts.show('x')", "toolkit")]
    [InlineData("window.__exiledtoolkit&&window.__exiledtoolkit.place('hud')", "toolkit")]
    [InlineData("window.__slcheatmenu.open(); window.__exiledtoolkit.x()", "cheatmenu")]
    public void Exiled_s_Toolkit_scripts_count_as_the_toolkit_not_the_game(string script, string owner)
    {
        Assert.Equal(owner, ScriptTraffic.OwnerOf(script));
    }

    [Fact]
    public void The_probe_names_the_showing_page_and_bad_names_are_refused()
    {
        Assert.Equal(new PageCommand.Showing("PhoneSMS+Store"), PageCommand.Parse("slmod|perfanalyzer|page|PhoneSMS+Store"));
        Assert.Equal(new PageCommand.Showing("hud"), PageCommand.Parse("slmod|perfanalyzer|page|hud"));
        Assert.IsType<PageCommand.Unknown>(PageCommand.Parse("slmod|perfanalyzer|page|"));
        Assert.IsType<PageCommand.Unknown>(PageCommand.Parse("slmod|perfanalyzer|page|bad,name"));
        Assert.IsType<PageCommand.Unknown>(PageCommand.Parse("slmod|perfanalyzer|page|" + new string('a', 121)));
    }

    [Fact]
    public void Frames_seconds_and_hitches_carry_the_page_that_was_showing()
    {
        var (recorder, _) = NewRecorder();

        recorder.SetPage("hud");
        Frame(recorder, 16);
        recorder.SetPage("PhoneSMS");
        Frame(recorder, 1100); // a hitch, and the first frame of second 1
        Frame(recorder, 1116);
        Frame(recorder, 2100);

        var ring = recorder.Ring;

        Assert.Equal("hud", recorder.PageName(ring[0].Page));
        Assert.Equal("PhoneSMS", recorder.PageName(ring[1].Page));
        Assert.Equal("PhoneSMS", recorder.Hitches.Last().Page);

        var seconds = Report.SecondsCsv(recorder).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        var header = seconds[0].Split(',').ToList();
        var page = header.IndexOf("page");

        Assert.True(page > 0);
        Assert.Equal("hud", seconds[1].Split(',')[page]);
        Assert.Equal("PhoneSMS", seconds[2].Split(',')[page]);

        var frames = Report.FramesCsv(recorder).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

        Assert.EndsWith(",page", frames[0]);
        Assert.EndsWith(",hud", frames[1]);
        Assert.EndsWith(",PhoneSMS", frames[2]);
    }

    [Fact]
    public void Past_two_hundred_page_names_new_ones_are_marked_other()
    {
        var (recorder, _) = NewRecorder();

        for (var i = 0; i < 250; i++) recorder.SetPage("page" + i);

        Assert.Equal("other", recorder.Page);

        recorder.SetPage("page3");

        Assert.Equal("page3", recorder.Page);
    }

    [Fact]
    public void A_slow_message_keeps_its_command_and_its_start_and_names_its_frame_and_page()
    {
        var (recorder, _) = NewRecorder();
        var message = Message("3", "PhoneSMS", "open", "{\"list\":[" + new string('1', 200) + "]}");

        recorder.SetPage("PhoneSMS");
        recorder.NoteSlowMessage(Ms(900), 80, message);
        Frame(recorder, 1000);

        var slow = Assert.Single(recorder.SlowMessages);

        Assert.Equal("3 PhoneSMS open", slow.Command);
        Assert.Equal(80, slow.Head.Length);
        Assert.StartsWith("3|PhoneSMS|open|{\"list\":[", slow.Head);
        Assert.DoesNotContain(SlowMessage.Separator, slow.Head);
        Assert.Equal("PhoneSMS", slow.Page);
        Assert.Equal(recorder.Hitches.Last().Frame, slow.Frame);

        using var doc = JsonDocument.Parse(Snapshot.Json(recorder, new SnapshotInput(Ms(1000), PanelTab.Hitches, true, InstrumentStatus.None, System.Array.Empty<ScriptRow>(), false, "0.5.0")));
        var hitch = doc.RootElement.GetProperty("hitches")[0];

        Assert.Equal("PhoneSMS", hitch.GetProperty("page").GetString());
        Assert.Equal("3 PhoneSMS open", hitch.GetProperty("messages")[0].GetProperty("command").GetString());

        var report = Report.Text(recorder, new ReportInput(Ms(1000), new System.DateTime(2026, 10, 26), "0.5.0", "1.1.19611", "", InstrumentStatus.None, System.Array.Empty<ScriptRow>(), "", System.Array.Empty<ModInfo>()));

        Assert.Matches(@"80\.0 +0\.9 PhoneSMS +3 PhoneSMS open", report);
    }

    [Fact]
    public void A_message_without_the_four_parts_shows_its_start_as_the_command()
    {
        Assert.Equal("slmod|toolkit|ping", SlowMessage.CommandOf("slmod|toolkit|ping"));
        Assert.Equal("1|a", SlowMessage.CommandOf(Message("1", "a")));
        Assert.Equal("", SlowMessage.CommandOf(null));
        Assert.Equal(40, SlowMessage.CommandOf(new string('x', 100)).Length);
    }

    [Fact]
    public void Slow_messages_are_only_kept_while_recording_and_only_the_last_fifty()
    {
        var recorder = new Recorder(TicksPerMs, 64, 50);

        recorder.NoteSlowMessage(0, 90, "x");
        Assert.Empty(recorder.SlowMessages);

        recorder.Start(0);

        for (var i = 0; i < 60; i++) recorder.NoteSlowMessage(Ms(i), 60, "m" + i);

        Assert.Equal(50, recorder.SlowMessages.Count);
        Assert.Equal(60, recorder.SlowMessageCount);
        Assert.Equal("m59", recorder.SlowMessages.Last().Head);
        Assert.Equal("m10", recorder.SlowMessages[0].Head);
    }

    [Fact]
    public void A_method_keeps_its_first_call_and_its_worst_call_with_the_frame_it_was_in()
    {
        var (recorder, mod) = NewRecorder();
        var probe = recorder.AddProbe(new ProbeInfo("ModA.Load", ProbeKind.ModMethod, "", new[] { mod }));

        recorder.End(recorder.Begin(probe, Ms(1), 0), Ms(4), 0); // first call: 3 ms
        Frame(recorder, 16);
        recorder.SetPage("Store");
        recorder.End(recorder.Begin(probe, Ms(20), 0), Ms(70), 0); // worst call: 50 ms
        Frame(recorder, 80, "Map");
        recorder.SetPage("hud");

        for (var i = 0; i < 5; i++)
        {
            recorder.End(recorder.Begin(probe, Ms(100 + i * 16), 0), Ms(110 + i * 16), 0);
            Frame(recorder, 116 + i * 16);
        }

        var totals = recorder.ProbeTotals[probe];

        Assert.Equal(Ms(3), totals.FirstCallTicks);
        Assert.False(totals.MaxCall.IsNone);
        Assert.Equal("Map", totals.MaxCall.Scene);
        Assert.Equal("Store", totals.MaxCall.Page);
        Assert.Equal(recorder.Hitches[0].Frame, totals.MaxCall.Frame);
        Assert.Equal(64, totals.MaxCall.Sample.Ms, 1);

        var slowest = Analysis.SlowestCalls(recorder, 10);

        Assert.Equal(probe, slowest[0].Id);
        Assert.Equal(50, slowest[0].MaxCallMs, 1);

        using var doc = JsonDocument.Parse(Snapshot.Json(recorder, new SnapshotInput(Ms(200), PanelTab.Methods, true, InstrumentStatus.None, System.Array.Empty<ScriptRow>(), false, "0.5.0")));
        var row = doc.RootElement.GetProperty("methods").EnumerateArray().First(m => m.GetProperty("name").GetString() == "ModA.Load");

        Assert.Equal(3, row.GetProperty("firstCall").GetDouble(), 1);
        Assert.Equal(Snapshot.Where("Map", "Store"), row.GetProperty("maxCallWhere").GetString());
    }

    [Fact]
    public void Slowest_calls_put_the_worst_single_call_first_and_leave_the_analyzer_out()
    {
        var (recorder, mod) = NewRecorder();
        var many = recorder.AddProbe(new ProbeInfo("ModA.Tick", ProbeKind.Ticker, "", new[] { mod }));
        var once = recorder.AddProbe(new ProbeInfo("ModA.Open", ProbeKind.ModMethod, "", new[] { mod }));
        var self = recorder.AddProbe(new ProbeInfo("Analyzer", ProbeKind.Analyzer, "", new[] { Recorder.AnalyzerMod }));

        for (var i = 0; i < 20; i++) recorder.End(recorder.Begin(many, Ms(i * 5), 0), Ms(i * 5 + 4), 0);
        recorder.End(recorder.Begin(once, Ms(200), 0), Ms(230), 0);
        recorder.End(recorder.Begin(self, Ms(240), 0), Ms(300), 0);
        Frame(recorder, 310);

        var ids = Analysis.SlowestCalls(recorder, 10).Select(r => r.Id).ToList();

        Assert.Equal(new[] { once, many }, ids);
        Assert.Equal(many, Analysis.MethodRows(recorder, 10).First(r => r.Probe.Kind != ProbeKind.Analyzer).Id);
    }

    private static string Metrics(double styles, double styleSec, double layouts, double layoutSec, double scriptSec, double taskSec) =>
        "{\"id\":3,\"result\":{\"metrics\":[" +
        $"{{\"name\":\"RecalcStyleCount\",\"value\":{styles}}},{{\"name\":\"RecalcStyleDuration\",\"value\":{styleSec}}}," +
        $"{{\"name\":\"LayoutCount\",\"value\":{layouts}}},{{\"name\":\"LayoutDuration\",\"value\":{layoutSec}}}," +
        $"{{\"name\":\"ScriptDuration\",\"value\":{scriptSec}}},{{\"name\":\"TaskDuration\",\"value\":{taskSec}}}]}}}}";

    [Fact]
    public void Browser_counters_become_the_change_since_the_last_answer_in_milliseconds()
    {
        var metrics = new WebMetrics();

        Assert.Null(metrics.Read(Metrics(100, 1.0, 50, 0.5, 2.0, 4.0)));

        var reading = metrics.Read(Metrics(130, 1.02, 60, 0.51, 2.1, 4.3));

        Assert.NotNull(reading);
        Assert.Equal(30, reading!.Value.StyleCount);
        Assert.Equal(20, reading.Value.StyleMs, 2);
        Assert.Equal(10, reading.Value.LayoutCount);
        Assert.Equal(10, reading.Value.LayoutMs, 2);
        Assert.Equal(100, reading.Value.ScriptMs, 2);
        Assert.Equal(300, reading.Value.TaskMs, 2);
        Assert.Equal(130, reading.Value.BusyMs, 2);

        // The page reloaded: the counters start over, so that answer only sets the new base.
        Assert.Null(metrics.Read(Metrics(5, 0.01, 2, 0.01, 0.1, 0.2)));
        Assert.Equal(5, metrics.Read(Metrics(10, 0.02, 2, 0.01, 0.1, 0.3))!.Value.StyleCount);

        Assert.Null(metrics.Read("{\"id\":4,\"error\":{}}"));
        Assert.Null(metrics.Read("not json"));
    }

    [Fact]
    public void The_watched_browser_page_is_the_game_s_root_page()
    {
        const string list = "[{\"type\":\"service_worker\",\"url\":\"Root.html\",\"webSocketDebuggerUrl\":\"ws://a\"}," +
            "{\"type\":\"page\",\"url\":\"http://x/CoreUI1.html\",\"webSocketDebuggerUrl\":\"ws://b\"}," +
            "{\"type\":\"page\",\"url\":\"http://x/Root.html\",\"webSocketDebuggerUrl\":\"ws://c\"}]";

        Assert.Equal("ws://c", WebMetrics.PickTarget(list));
        Assert.Equal("ws://b", WebMetrics.PickTarget("[{\"type\":\"page\",\"url\":\"http://x/CoreUI1.html\",\"webSocketDebuggerUrl\":\"ws://b\"}]"));
        Assert.Null(WebMetrics.PickTarget("[{\"type\":\"page\",\"url\":\"http://x/Root.html\"}]"));
        Assert.Null(WebMetrics.PickTarget("{}"));
    }

    [Fact]
    public void Browser_counters_land_in_their_second_and_seconds_without_one_stay_blank()
    {
        var game = new GameStats();
        var world = new WorldReading(1, 1, 0.02f, 0.02f, 0.333f, 0.02f, 1, true, true, false, -1);

        game.Add(0.5, 20, false, "Main", world, default, "hud");
        game.NoteWeb(new WebReading(30, 20, 10, 10, 100, 300));
        game.Add(1.5, 20, false, "Main", world, default, "Store");
        game.NoteWeb(new WebReading(3, 2, 1, 1, 5, 400));
        game.Add(2.5, 20, false, "Main", world, default, "Store");
        game.Add(3.5, 20, false, "Main", world, default, "Store");

        Assert.Equal(2, game.WebSeconds);
        Assert.Equal(700, game.WebTotals.TaskMs, 1);
        Assert.Equal(130, game.WebWorst.BusyMs, 1);
        Assert.Equal(0, game.WebWorstSecond);
        Assert.Equal("hud", game.WebWorstPage);

        Assert.Equal(300, game.Timeline[0].Web.TaskMs, 1);
        Assert.Equal(400, game.Timeline[1].Web.TaskMs, 1);
        Assert.Equal(0, game.Timeline[2].Web.TaskMs);
    }

    [Fact]
    public void The_seconds_file_has_the_browser_columns_blank_where_no_reading_came_in()
    {
        var (recorder, _) = NewRecorder();

        Frame(recorder, 500);
        recorder.Game.NoteWeb(new WebReading(30, 20, 10, 10, 100, 300));
        Frame(recorder, 1500);
        Frame(recorder, 2500);

        var lines = Report.SecondsCsv(recorder).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

        Assert.EndsWith(",page,web_styles,web_style_ms,web_layouts,web_layout_ms,web_script_ms,web_task_ms", lines[0]);
        Assert.EndsWith(",30,20,10,10,100,300", lines[1]);
        Assert.EndsWith(",,,,,,", lines[2]);

        var report = Report.Text(recorder, new ReportInput(Ms(2500), new System.DateTime(2026, 10, 26), "0.5.0", "1.1.19611", "", InstrumentStatus.None, System.Array.Empty<ScriptRow>(), "", System.Array.Empty<ModInfo>()));

        Assert.Contains("per second over 1 s: 30.0 style recalcs (20.0 ms), 10.0 layouts (10.0 ms), scripts 100.0 ms, all tasks 300.0 ms", report);
        Assert.Contains("busiest second: 0 s,", report);
    }
}
