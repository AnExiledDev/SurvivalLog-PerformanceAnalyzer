using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// Reads a browser trace (Chrome's trace event format) and a CPU profile of the web UI and says where the page's main
/// thread spent its time: script, style, layout, paint, garbage collection, which events and which functions, and the
/// long tasks. Self time is used throughout, so nested work is counted once, under the innermost event.
/// </summary>
public static class TraceSummary
{
    private const double LongTaskMs = 50;

    /// <summary>What a trace event's self time counts as. Unknown names are "other" and still listed by name.</summary>
    public static string Kind(string name)
    {
        if (name.Contains("GC", StringComparison.Ordinal) || name.StartsWith("ThreadState::", StringComparison.Ordinal)) return "gc";

        switch (name)
        {
            case "UpdateLayoutTree":
            case "RecalculateStyles":
            case "ParseAuthorStyleSheet":
                return "style";
            case "Layout":
                return "layout";
            case "Paint":
            case "PrePaint":
            case "Layerize":
            case "UpdateLayer":
            case "UpdateLayerTree":
            case "CompositeLayers":
            case "PaintImage":
            case "Decode Image":
            case "Commit":
                return "paint";
            case "ParseHTML":
                return "parse html";
            case "RunTask":
            case "ThreadControllerImpl::RunTask":
                return "task";
            case "FunctionCall":
            case "EvaluateScript":
            case "RunMicrotasks":
            case "TimerFire":
            case "FireAnimationFrame":
            case "FireIdleCallback":
            case "EventDispatch":
            case "XHRReadyStateChange":
            case "XHRLoad":
                return "script";
        }

        return name.StartsWith("v8.", StringComparison.Ordinal) || name.StartsWith("V8.", StringComparison.Ordinal) ? "script" : "other";
    }

    /// <summary>The scripts with the most self time in the profile, most first, so their sources can name a mod.</summary>
    public static IReadOnlyList<string> HotScripts(string profileJson, int max)
    {
        try
        {
            using var doc = JsonDocument.Parse(profileJson);
            var nodes = ProfileNodes(doc.RootElement);

            return nodes.Where(n => n.ScriptId.Length > 0 && n.ScriptId != "0").GroupBy(n => n.ScriptId)
                .Select(g => (Id: g.Key, Ms: g.Sum(n => n.SelfMs))).OrderByDescending(g => g.Ms).Take(max).Select(g => g.Id).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The mod a script belongs to, from its source: a script the plugin tagged starts with the probe's ctx call
    /// naming its sender; any other is named the way <see cref="ScriptTraffic.OwnerOf"/> names a sent script.
    /// </summary>
    public static string OwnerOfSource(string source, IReadOnlySet<string>? known)
    {
        const string tag = "window.__slperfprobe&&window.__slperfprobe.ctx(\"";

        if (source.StartsWith(tag, StringComparison.Ordinal))
        {
            var end = source.IndexOf('"', tag.Length);

            if (end > tag.Length) return source.Substring(tag.Length, end - tag.Length);
        }

        return ScriptTraffic.OwnerOf(source, known);
    }

    /// <param name="trace">The trace file's text: an object with "traceEvents" or a bare array of events.</param>
    /// <param name="profileJson">The CPU profile from Profiler.stop, or empty.</param>
    /// <param name="owners">Script id to mod, for the profile's functions.</param>
    public static string Text(string trace, string profileJson, IReadOnlyDictionary<string, string> owners) =>
        Culture.Invariant(() => Write(() => JsonDocument.Parse(trace), profileJson, owners));

    /// <summary>The same from the saved file, read as bytes: a trace can run to tens of megabytes.</summary>
    public static string Text(Stream trace, string profileJson, IReadOnlyDictionary<string, string> owners) =>
        Culture.Invariant(() => Write(() => JsonDocument.Parse(trace), profileJson, owners));

    private static string Write(Func<JsonDocument> parse, string profileJson, IReadOnlyDictionary<string, string> owners)
    {
        var text = new StringBuilder();

        try
        {
            using var doc = parse();
            var root = doc.RootElement;
            var events = root.ValueKind == JsonValueKind.Array ? root : root.TryGetProperty("traceEvents", out var list) ? list : default;

            if (events.ValueKind != JsonValueKind.Array) Line(text, "the trace holds no events");
            else WriteTrace(text, events);
        }
        catch (JsonException ex)
        {
            Line(text, "the trace could not be read: " + ex.Message);
        }

        if (profileJson.Length > 0) WriteProfile(text, profileJson, owners);

        return text.ToString();
    }

    private readonly record struct Slice(long Ts, long Dur, string Name, int Tid, int Pid, string Detail);

    private static void WriteTrace(StringBuilder text, JsonElement events)
    {
        var mains = new HashSet<(int, int)>();
        var slices = new List<Slice>();
        var frames = new List<long>();
        long first = long.MaxValue, last = long.MinValue;

        foreach (var e in events.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;

            var ph = Str(e, "ph");
            var name = Str(e, "name");
            var pid = (int)Num(e, "pid");
            var tid = (int)Num(e, "tid");

            if (ph == "M")
            {
                if (name == "thread_name" && e.TryGetProperty("args", out var args) && Str(args, "name") == "CrRendererMain") mains.Add((pid, tid));

                continue;
            }

            var ts = (long)Num(e, "ts");

            if (ts > 0)
            {
                first = Math.Min(first, ts);
                last = Math.Max(last, ts);
            }

            if (name == "DrawFrame" && ph != "e" && ph != "E") frames.Add(ts);

            if (ph == "X") slices.Add(new Slice(ts, (long)Num(e, "dur"), name, tid, pid, Detail(e, name)));
        }

        var seconds = last > first ? (last - first) / 1e6 : 0;

        Line(text, $"trace of {seconds:0.0} s");

        // The page's main thread: of the threads named CrRendererMain, the busiest one.
        var main = slices.Where(s => mains.Contains((s.Pid, s.Tid))).GroupBy(s => (s.Pid, s.Tid)).OrderByDescending(g => g.Sum(s => s.Dur)).FirstOrDefault();

        if (main == null)
        {
            Line(text, "the trace has no page main thread (CrRendererMain)");

            return;
        }

        var self = SelfTimes(main.OrderBy(s => s.Ts).ThenByDescending(s => s.Dur).ToList(), out var tasks);
        var busy = self.Sum(s => s.Ms);

        Line(text, $"main thread busy {busy:0} ms of {seconds * 1000:0} ms ({(seconds > 0 ? busy / (seconds * 10) : 0):0}%)");
        Line(text);
        Line(text, $"{"time on the main thread by kind",-34} {"ms",8} {"ms/s",7}");

        foreach (var kind in self.GroupBy(s => Kind(s.Name)).Select(g => (Kind: g.Key, Ms: g.Sum(s => s.Ms))).OrderByDescending(k => k.Ms))
        {
            Line(text, $"{kind.Kind,-34} {kind.Ms,8:0} {PerSecond(kind.Ms, seconds),7:0.0}");
        }

        Line(text);
        Line(text, $"{"busiest events (self time)",-34} {"kind",-10} {"count",6} {"ms",8} {"worst ms",9}");

        foreach (var name in self.GroupBy(s => s.Name).Select(g => (Name: g.Key, Count: g.Count(), Ms: g.Sum(s => s.Ms), Worst: g.Max(s => s.Ms))).OrderByDescending(n => n.Ms).Take(15))
        {
            Line(text, $"{Fit(name.Name, 34),-34} {Kind(name.Name),-10} {name.Count,6} {name.Ms,8:0} {name.Worst,9:0.0}");
        }

        var dispatch = main.Where(s => s.Name == "EventDispatch").GroupBy(s => s.Detail).Select(g => (Type: g.Key, Count: g.Count(), Ms: g.Sum(s => s.Dur) / 1000.0, Worst: g.Max(s => s.Dur) / 1000.0)).OrderByDescending(d => d.Ms).Take(12).ToList();

        if (dispatch.Count > 0)
        {
            Line(text);
            Line(text, $"{"page events handled",-34} {"count",6} {"ms",8} {"worst ms",9}");

            foreach (var d in dispatch) Line(text, $"{Fit(d.Type.Length > 0 ? d.Type : "(no type)", 34),-34} {d.Count,6} {d.Ms,8:0.0} {d.Worst,9:0.0}");
        }

        var scripts = main.Where(s => s.Name is "FunctionCall" or "EvaluateScript").GroupBy(s => s.Detail).Select(g => (Where: g.Key, Count: g.Count(), Ms: g.Sum(s => s.Dur) / 1000.0, Worst: g.Max(s => s.Dur) / 1000.0)).OrderByDescending(d => d.Ms).Take(12).ToList();

        if (scripts.Count > 0)
        {
            Line(text);
            Line(text, $"{"scripts run (with what they called)",-60} {"count",6} {"ms",8} {"worst ms",9}");

            foreach (var d in scripts) Line(text, $"{Fit(d.Where, 60),-60} {d.Count,6} {d.Ms,8:0.0} {d.Worst,9:0.0}");
        }

        var animationFrames = main.Count(s => s.Name == "FireAnimationFrame");
        var timers = main.Count(s => s.Name == "TimerFire");

        Line(text);
        Line(text, $"animation frame callbacks {animationFrames} ({PerSecond(animationFrames, seconds):0.0}/s), timers fired {timers} ({PerSecond(timers, seconds):0.0}/s)");

        if (frames.Count > 1)
        {
            frames.Sort();

            var gap = 0L;

            for (var i = 1; i < frames.Count; i++) gap = Math.Max(gap, frames[i] - frames[i - 1]);

            Line(text, $"frames drawn {frames.Count} ({PerSecond(frames.Count, seconds):0.0}/s), longest gap between frames {gap / 1000.0:0} ms");
        }
        else
        {
            Line(text, "the trace has no frame events");
        }

        var gcs = main.Where(s => s.Name is "MinorGC" or "MajorGC").ToList();

        if (gcs.Count > 0) Line(text, $"garbage collections {gcs.Count} ({gcs.Count(s => s.Name == "MajorGC")} major), {gcs.Sum(s => s.Dur) / 1000.0:0.0} ms, worst {gcs.Max(s => s.Dur) / 1000.0:0.0} ms");

        var longTasks = tasks.Where(t => t.Ms >= LongTaskMs).OrderByDescending(t => t.Ms).ToList();

        Line(text);
        Line(text, $"long tasks (50 ms or more): {longTasks.Count}, {longTasks.Sum(t => t.Ms):0} ms");

        foreach (var task in longTasks.Take(8))
        {
            var parts = string.Join(", ", task.Inside.OrderByDescending(p => p.Value).Take(4).Select(p => $"{p.Key} {p.Value:0}"));

            Line(text, $"  {task.Ms,6:0} ms at {(task.Ts - main.Min(s => s.Ts)) / 1e6,5:0.00} s: {parts}");
        }
    }

    private readonly record struct SelfTime(string Name, double Ms);

    private sealed record TopTask(long Ts, double Ms, Dictionary<string, double> Inside);

    /// <summary>Self time of each slice (its time minus its children's), and each top-level task with what ran in it.</summary>
    private static List<SelfTime> SelfTimes(List<Slice> slices, out List<TopTask> tasks)
    {
        var self = new List<SelfTime>(slices.Count);
        var stack = new List<(Slice Slice, long Children)>();
        tasks = new List<TopTask>();
        TopTask? task = null;

        void Pop()
        {
            var top = stack[^1];

            stack.RemoveAt(stack.Count - 1);

            var ms = Math.Max(0, top.Slice.Dur - top.Children) / 1000.0;

            self.Add(new SelfTime(top.Slice.Name, ms));

            if (task != null)
            {
                var label = top.Slice.Name is "FunctionCall" or "EventDispatch" && top.Slice.Detail.Length > 0 ? top.Slice.Name + " " + Fit(top.Slice.Detail, 40) : top.Slice.Name;

                task.Inside[label] = task.Inside.GetValueOrDefault(label) + ms;
            }

            if (stack.Count > 0) stack[^1] = (stack[^1].Slice, stack[^1].Children + top.Slice.Dur);
        }

        foreach (var slice in slices)
        {
            while (stack.Count > 0 && slice.Ts >= stack[^1].Slice.Ts + stack[^1].Slice.Dur) Pop();

            if (stack.Count == 0)
            {
                task = new TopTask(slice.Ts, slice.Dur / 1000.0, new Dictionary<string, double>());
                tasks.Add(task);
            }

            // A slice that runs past its parent's end is cut to fit, so the parent's self time never goes negative.
            var dur = stack.Count > 0 ? Math.Min(slice.Dur, stack[^1].Slice.Ts + stack[^1].Slice.Dur - slice.Ts) : slice.Dur;

            stack.Add((slice with { Dur = dur }, 0));
        }

        while (stack.Count > 0) Pop();

        return self;
    }

    private static string Detail(JsonElement e, string name)
    {
        if (!e.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Object) return "";
        if (!args.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return "";

        if (name == "EventDispatch") return Str(data, "type");

        if (name is "FunctionCall" or "EvaluateScript")
        {
            var url = Str(data, "url");
            var function = Str(data, "functionName");
            var at = (url.Length > 0 ? ShortUrl(url) : "sent script") + ":" + ((long)Num(data, "lineNumber") + (name == "FunctionCall" ? 1 : 0));

            return function.Length > 0 ? function + " " + at : at;
        }

        return "";
    }

    private sealed record Node(string Function, string Url, long Line, string ScriptId, double SelfMs);

    private static List<Node> ProfileNodes(JsonElement profile)
    {
        var nodes = new Dictionary<long, (string Function, string Url, long Line, string ScriptId)>();

        if (profile.TryGetProperty("nodes", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var n in list.EnumerateArray())
            {
                var frame = n.TryGetProperty("callFrame", out var f) ? f : default;
                var scriptId = frame.ValueKind == JsonValueKind.Object && frame.TryGetProperty("scriptId", out var id) ? id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() ?? "" : "";

                nodes[(long)Num(n, "id")] = frame.ValueKind == JsonValueKind.Object ? (Str(frame, "functionName"), Str(frame, "url"), (long)Num(frame, "lineNumber"), scriptId) : ("", "", 0, "");
            }
        }

        // Each sample is charged the time until the next one.
        var hits = new Dictionary<long, double>();

        if (profile.TryGetProperty("samples", out var samples) && samples.ValueKind == JsonValueKind.Array && profile.TryGetProperty("timeDeltas", out var deltas) && deltas.ValueKind == JsonValueKind.Array)
        {
            var ids = samples.EnumerateArray().Select(s => s.GetInt64()).ToList();
            var gaps = deltas.EnumerateArray().Select(d => d.GetDouble()).ToList();

            for (var i = 0; i < ids.Count; i++)
            {
                var us = i + 1 < gaps.Count ? gaps[i + 1] : 0;

                hits[ids[i]] = hits.GetValueOrDefault(ids[i]) + Math.Max(0, us) / 1000.0;
            }
        }

        return nodes.Select(n => new Node(n.Value.Function, n.Value.Url, n.Value.Line, n.Value.ScriptId, hits.GetValueOrDefault(n.Key))).ToList();
    }

    private static void WriteProfile(StringBuilder text, string profileJson, IReadOnlyDictionary<string, string> owners)
    {
        List<Node> nodes;

        try
        {
            using var doc = JsonDocument.Parse(profileJson);

            nodes = ProfileNodes(doc.RootElement);
        }
        catch (JsonException ex)
        {
            Line(text);
            Line(text, "the CPU profile could not be read: " + ex.Message);

            return;
        }

        string Owner(Node n) => n.Function is "(idle)" or "(program)" or "(garbage collector)" or "(root)" ? n.Function : owners.TryGetValue(n.ScriptId, out var o) ? o : n.Url.Length > 0 ? "game" : "unknown";

        var total = nodes.Sum(n => n.SelfMs);

        Line(text);
        Line(text, $"CPU profile of the page's scripts, {total:0} ms sampled. By owner (\"unknown\" is a sent script whose source was not read):");

        foreach (var owner in nodes.GroupBy(Owner).Select(g => (Owner: g.Key, Ms: g.Sum(n => n.SelfMs))).Where(o => o.Ms >= 0.5).OrderByDescending(o => o.Ms))
        {
            Line(text, $"  {Fit(owner.Owner, 30),-30} {owner.Ms,8:0} ms");
        }

        Line(text, $"{"busiest functions (self time)",-60} {"owner",-16} {"ms",8}");

        var functions = nodes.Where(n => n.ScriptId.Length > 0 && n.ScriptId != "0")
            .GroupBy(n => (n.Function, n.Url, n.Line, n.ScriptId))
            .Select(g => (Node: g.First(), Ms: g.Sum(n => n.SelfMs)))
            .OrderByDescending(f => f.Ms).Take(20);

        foreach (var (node, ms) in functions)
        {
            var name = (node.Function.Length > 0 ? node.Function : "(anonymous)") + " " + (node.Url.Length > 0 ? ShortUrl(node.Url) : "script " + node.ScriptId) + ":" + (node.Line + 1);

            Line(text, $"{Fit(name, 60),-60} {Fit(Owner(node), 16),-16} {ms,8:0.0}");
        }
    }

    private static string ShortUrl(string url)
    {
        var cut = url.Split('?', '#')[0];
        var slash = cut.LastIndexOf('/');

        return slash >= 0 && slash < cut.Length - 1 ? cut.Substring(slash + 1) : cut;
    }

    private static double PerSecond(double value, double seconds) => seconds > 0 ? value / seconds : 0;

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static double Num(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static string Fit(string text, int width) => text.Length <= width ? text : text.Substring(0, width - 1) + "~";

    private static void Line(StringBuilder text, string line = "")
    {
        foreach (var c in line) text.Append(c is >= ' ' and <= '~' ? c : '?');

        text.Append('\n');
    }
}
