using System.Collections.Generic;
using System.Linq;
using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>The browser trace summary, the clicks in the report, and naming a traced script's mod.</summary>
public class TraceTests
{
    // One page main thread (pid 1, tid 10) and one other thread. On the main thread a 100 ms task runs a 60 ms
    // function call, which runs a 20 ms layout; then a 30 ms click handler; then a 5 ms garbage collection.
    private const string Trace = """
        {"traceEvents":[
          {"ph":"M","name":"thread_name","pid":1,"tid":10,"args":{"name":"CrRendererMain"}},
          {"ph":"M","name":"thread_name","pid":1,"tid":11,"args":{"name":"Compositor"}},
          {"ph":"X","name":"RunTask","pid":1,"tid":10,"ts":1000000,"dur":100000},
          {"ph":"X","name":"FunctionCall","pid":1,"tid":10,"ts":1010000,"dur":60000,"args":{"data":{"functionName":"openMap","url":"","lineNumber":4}}},
          {"ph":"X","name":"Layout","pid":1,"tid":10,"ts":1020000,"dur":20000},
          {"ph":"X","name":"RunTask","pid":1,"tid":10,"ts":1200000,"dur":30000},
          {"ph":"X","name":"EventDispatch","pid":1,"tid":10,"ts":1200000,"dur":30000,"args":{"data":{"type":"click"}}},
          {"ph":"X","name":"MinorGC","pid":1,"tid":10,"ts":1300000,"dur":5000},
          {"ph":"X","name":"RunTask","pid":1,"tid":11,"ts":1000000,"dur":900000},
          {"ph":"I","name":"DrawFrame","pid":1,"tid":11,"ts":1000000},
          {"ph":"I","name":"DrawFrame","pid":1,"tid":11,"ts":1016000},
          {"ph":"I","name":"DrawFrame","pid":1,"tid":11,"ts":1216000},
          {"ph":"I","name":"Other","pid":1,"tid":11,"ts":2000000}
        ]}
        """;

    // Root, then a cheat menu function (script 7) and a game function (script 9). Four samples: three in the cheat
    // menu, one in the game, each charged the 2 ms until the next.
    private const string Profile = """
        {"nodes":[
          {"id":1,"callFrame":{"functionName":"(root)","scriptId":"0","url":"","lineNumber":-1}},
          {"id":2,"callFrame":{"functionName":"drawList","scriptId":"7","url":"","lineNumber":41}},
          {"id":3,"callFrame":{"functionName":"tick","scriptId":"9","url":"https://game/CoreUI1.js","lineNumber":9}}
        ],"startTime":0,"endTime":10000,"samples":[2,2,2,3,1],"timeDeltas":[0,2000,2000,2000,2000]}
        """;

    private static string Summary() => TraceSummary.Text(Trace, Profile, new Dictionary<string, string> { ["7"] = "cheatmenu" });

    [Fact]
    public void Main_thread_time_is_self_time_by_kind_so_nested_work_counts_once()
    {
        var text = Summary();

        Assert.Contains("main thread busy 135 ms", text);
        Assert.Matches(@"\nscript +70 ", text);
        Assert.Matches(@"\nlayout +20 ", text);
        Assert.Matches(@"\ntask +40 ", text);
        Assert.Matches(@"\ngc +5 ", text);
        Assert.DoesNotContain("900", text);
    }

    [Fact]
    public void A_long_task_lists_what_ran_inside_it()
    {
        var text = Summary();

        Assert.Contains("long tasks (50 ms or more): 1, 100 ms", text);
        Assert.Contains("FunctionCall openMap sent script:5 40", text);
        Assert.Contains("Layout 20", text);
    }

    [Fact]
    public void Page_events_frames_and_garbage_collection_are_counted()
    {
        var text = Summary();

        Assert.Matches(@"\nclick +1 +30\.0 ", text);
        Assert.Contains("frames drawn 3", text);
        Assert.Contains("longest gap between frames 200 ms", text);
        Assert.Contains("garbage collections 1 (0 major)", text);
    }

    [Fact]
    public void Profile_functions_are_charged_to_the_mod_whose_script_they_are_in()
    {
        var text = Summary();

        Assert.Matches(@"\n  cheatmenu +6 ms", text);
        Assert.Matches(@"\n  game +2 ms", text);
        Assert.Matches(@"drawList script 7:42 +cheatmenu +6\.0", text);
        Assert.Equal(new[] { "7", "9" }, TraceSummary.HotScripts(Profile, 5).ToArray());
    }

    // Two seconds of the frame path. Second 0: the page (renderer sink 6:3) sends 4 frames, the compositor draws all
    // 4, the capture takes 1 and declines 3, and 1 reaches the game. Second 1: the page sends 2 more that are drawn,
    // taken and delivered. The browser's own sink (0:1) sends 1 frame.
    private static string FramePath()
    {
        var events = new List<string> { """{"ph":"M","name":"thread_name","pid":1,"tid":10,"args":{"name":"CrRendererMain"}}""" };

        void Frame(long ts, int client, int id) => events.Add("""{"ph":"X","name":"Graphics.Pipeline","cat":"viz,benchmark,graphics.pipeline","pid":2,"tid":20,"ts":""" + ts + ""","dur":5,"args":{"chrome_graphics_pipeline":{"step":"STEP_RECEIVE_COMPOSITOR_FRAME","frame_sink_id":{"frame_sink_client_id":""" + client + ""","frame_sink_id":""" + id + """}}}}""");
        void Draw(long ts) => events.Add($$$"""{"ph":"X","name":"Display::DrawAndSwap","cat":"viz","pid":2,"tid":20,"ts":{{{ts}}},"dur":5}""");
        void Take(long ts) => events.Add($$$"""{"ph":"b","name":"Capture","cat":"gpu.capture","pid":2,"tid":20,"ts":{{{ts}}},"id":"0x1","args":{"trigger":"compositor"}}""");
        void Decline(long ts) => events.Add($$$"""{"ph":"I","name":"FpsRateLimited","cat":"gpu.capture","pid":2,"tid":20,"ts":{{{ts}}},"s":"t","args":{"trigger":"compositor"}}""");
        void Deliver(long ts) => events.Add("""{"ph":"X","name":"Receive mojo message","cat":"toplevel,mojom","pid":3,"tid":30,"ts":""" + ts + ""","dur":1000,"args":{"chrome_mojo_event_info":{"mojo_interface_tag":"viz.mojom.FrameSinkVideoConsumer"}}}""");

        foreach (var ts in new long[] { 1_000_000, 1_100_000, 1_200_000, 1_300_000 })
        {
            Frame(ts, 6, 3);
            Draw(ts + 10);
        }

        Take(1_000_020);
        Deliver(1_001_000);
        Decline(1_100_020);
        Decline(1_200_020);
        Decline(1_300_020);

        foreach (var ts in new long[] { 2_000_000, 2_500_000 })
        {
            Frame(ts, 6, 3);
            Draw(ts + 10);
            Take(ts + 20);
            Deliver(ts + 1000);
        }

        Frame(1_050_000, 0, 1);
        events.Add("""{"ph":"I","name":"Other","pid":1,"tid":10,"ts":3000000}""");

        return TraceSummary.Text("[" + string.Join(",", events) + "]", "", new Dictionary<string, string>());
    }

    [Fact]
    public void The_frame_path_counts_each_stage_and_names_where_frames_are_lost()
    {
        var text = FramePath();

        Assert.Matches(@"page frames at the compositor +6 ", text);
        Assert.Contains("by frame sink: 6:3 6, 0:1 1", text);
        Assert.Matches(@"compositor draws +6 ", text);
        Assert.Matches(@"captures taken +3 ", text);
        Assert.Matches(@"frames delivered to the game +3 ", text);
        Assert.Contains("the capture declined a frame: FpsRateLimited (compositor) 3", text);
        Assert.Contains("most frames are lost between the compositor and the capture: 6 -> 3", text);
        Assert.Matches(@"\n +0 +4 +4 +1 +1 +3\n", text);
        Assert.Matches(@"\n +1 +2 +2 +2 +2 +0\n", text);
    }

    [Fact]
    public void The_longest_gap_counts_to_the_end_of_the_trace()
    {
        // Deliveries at 1.001, 2.001 and 2.501 s; the trace runs from 1.0 to 3.0 s. The gap after the last one (0.499 s)
        // is shorter than the 1.0 s between the first two, so 1000 ms is the longest. A freeze at the end must count
        // too, so with the last two removed the gap from 1.001 s to the end, 1999 ms, is the longest.
        Assert.Matches(@"frames delivered to the game +3 +[\d.]+/s  longest gap +1000 ms", FramePath());

        var trace = """
            [{"ph":"X","name":"Receive mojo message","cat":"toplevel,mojom","pid":3,"tid":30,"ts":1001000,"dur":1000,"args":{"chrome_mojo_event_info":{"mojo_interface_tag":"viz.mojom.FrameSinkVideoConsumer"}}},
             {"ph":"I","name":"Start","pid":3,"tid":30,"ts":1000000},
             {"ph":"I","name":"Other","pid":3,"tid":30,"ts":3000000}]
            """;

        Assert.Matches(@"frames delivered to the game +1 +[\d.]+/s  longest gap +1999 ms", TraceSummary.Text(trace, "", new Dictionary<string, string>()));
    }

    [Fact]
    public void An_older_trace_shows_the_deliveries_and_says_the_other_stages_were_not_recorded()
    {
        var trace = """
            [{"ph":"X","name":"Receive mojo message","cat":"toplevel,mojom","pid":3,"tid":30,"ts":1000000,"dur":1000,"args":{"chrome_mojo_event_info":{"mojo_interface_tag":"viz.mojom.FrameSinkVideoConsumer"}}},
             {"ph":"I","name":"Other","pid":3,"tid":30,"ts":3000000}]
            """;
        var text = TraceSummary.Text(trace, "", new Dictionary<string, string>());

        Assert.Matches(@"frames delivered to the game +1 ", text);
        Assert.Contains("page frames and compositor draws: not in this trace", text);
        Assert.Contains("captures taken: not in this trace", text);
        Assert.DoesNotContain("most frames are lost", text);
    }

    [Fact]
    public void A_trace_without_the_frame_path_has_no_frame_path_section()
    {
        Assert.DoesNotContain("frames to the game", Summary());
    }

    [Fact]
    public void A_bad_trace_says_so_instead_of_throwing()
    {
        Assert.Contains("could not be read", TraceSummary.Text("{oops", "", new Dictionary<string, string>()));
        Assert.Contains("no page main thread", TraceSummary.Text("[]", "", new Dictionary<string, string>()));
    }

    [Fact]
    public void A_traced_script_is_named_by_its_tag_first_then_by_what_it_names()
    {
        Assert.Equal("farmhub", TraceSummary.OwnerOfSource(ScriptTag.Wrap("farmhub", "window.__slcheatmenu.x()"), null));
        Assert.Equal("cheatmenu", TraceSummary.OwnerOfSource("window.__slcheatmenu.x()", null));
        Assert.Equal("perfanalyzer", TraceSummary.OwnerOfSource("const PREFIX = 'slmod|perfanalyzer|';", null));
    }
}
