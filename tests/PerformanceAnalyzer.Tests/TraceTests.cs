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
