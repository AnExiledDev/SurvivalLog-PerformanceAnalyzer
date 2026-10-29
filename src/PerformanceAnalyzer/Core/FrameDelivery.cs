using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PerformanceAnalyzer.Core;

public static partial class TraceSummary
{
    /// <summary>
    /// Follows the menus' frames from the page to the game: the page's frames reaching the browser's compositor, the
    /// compositor drawing them, the capture taking a copy, and the copy reaching the game. The game only shows what
    /// arrives at the end, so a page that draws smoothly can still look frozen when a later stage drops frames; the
    /// stage where the counts fall is where the lag is.
    /// </summary>
    private sealed class FrameDelivery
    {
        // The capture's reasons for not taking a frame, as the browser names them.
        private static readonly HashSet<string> Declines = new(StringComparer.Ordinal)
        {
            "FpsRateLimited", "NoConsumer", "NoRegionProperties", "DroppingFrameWithUncontainedRegion", "PipelineLimited", "NearlyPipelineLimited",
        };

        private readonly Dictionary<string, List<long>> _sinks = new();
        private readonly Dictionary<string, int> _skipped = new();
        private readonly List<long> _draws = new();
        private readonly List<long> _captures = new();
        private readonly List<long> _delivered = new();
        private readonly List<(long Ts, string Why)> _declined = new();
        private bool _sawCompositor;
        private bool _sawCapture;

        public void Add(JsonElement e, string ph, string name, long ts)
        {
            var category = Str(e, "cat");
            var args = e.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;

            if (category.Contains("gpu.capture", StringComparison.Ordinal))
            {
                _sawCapture = true;

                if (name == "Capture" && ph is "b" or "B") _captures.Add(ts);
                else if (Declines.Contains(name)) _declined.Add((ts, Str(args, "trigger") is { Length: > 0 } trigger ? name + " (" + trigger + ")" : name));

                return;
            }

            if (name == "Display::DrawAndSwap" && ph is "X" or "B")
            {
                _sawCompositor = true;
                _draws.Add(ts);

                return;
            }

            if (name == "Graphics.Pipeline" && ph == "X" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("chrome_graphics_pipeline", out var step) && step.ValueKind == JsonValueKind.Object)
            {
                _sawCompositor = true;

                switch (Str(step, "step"))
                {
                    case "STEP_RECEIVE_COMPOSITOR_FRAME":
                        var sink = step.TryGetProperty("frame_sink_id", out var id) ? $"{(long)Num(id, "frame_sink_client_id")}:{(long)Num(id, "frame_sink_id")}" : "?";

                        if (!_sinks.TryGetValue(sink, out var list)) _sinks[sink] = list = new List<long>();

                        list.Add(ts);
                        break;
                    case "STEP_DID_NOT_PRODUCE_COMPOSITOR_FRAME":
                        var why = Str(step, "frame_skipped_reason");

                        why = why.StartsWith("SKIPPED_REASON_", StringComparison.Ordinal) ? why.Substring("SKIPPED_REASON_".Length).ToLowerInvariant() : why.Length > 0 ? why : "no reason given";
                        _skipped[why] = _skipped.GetValueOrDefault(why) + 1;
                        break;
                }

                return;
            }

            if (name == "Receive mojo message" && ph == "X" && args.ValueKind == JsonValueKind.Object && args.TryGetProperty("chrome_mojo_event_info", out var mojo) &&
                Str(mojo, "mojo_interface_tag") == "viz.mojom.FrameSinkVideoConsumer")
            {
                _delivered.Add(ts);
            }
        }

        public void Write(StringBuilder text, long first, double seconds)
        {
            if (!_sawCompositor && !_sawCapture && _delivered.Count == 0) return;

            var end = first + (long)(seconds * 1e6);
            var page = _sinks.OrderByDescending(s => s.Value.Count).Select(s => s.Value).FirstOrDefault() ?? new List<long>();

            Line(text);
            Line(text, "frames to the game, stage by stage (a stage can only pass on what the one before it made):");

            if (_sawCompositor)
            {
                var sinks = string.Join(", ", _sinks.OrderByDescending(s => s.Value.Count).Take(4).Select(s => $"{s.Key} {s.Value.Count}"));

                Line(text, $"  {"page frames at the compositor",-32} {page.Count,6} {PerSecond(page.Count, seconds),6:0.0}/s  longest gap {Gap(page, first, end),5:0} ms  (by frame sink: {sinks})");
                Line(text, $"  {"compositor draws",-32} {_draws.Count,6} {PerSecond(_draws.Count, seconds),6:0.0}/s  longest gap {Gap(_draws, first, end),5:0} ms");
            }
            else
            {
                Line(text, "  page frames and compositor draws: not in this trace (made before version 0.7.1)");
            }

            if (_sawCapture) Line(text, $"  {"captures taken",-32} {_captures.Count,6} {PerSecond(_captures.Count, seconds),6:0.0}/s  longest gap {Gap(_captures, first, end),5:0} ms");
            else Line(text, "  captures taken: not in this trace (made before version 0.7.1)");

            Line(text, $"  {"frames delivered to the game",-32} {_delivered.Count,6} {PerSecond(_delivered.Count, seconds),6:0.0}/s  longest gap {Gap(_delivered, first, end),5:0} ms");

            if (_skipped.Count > 0) Line(text, "  the page made no frame: " + string.Join(", ", _skipped.OrderByDescending(s => s.Value).Select(s => $"{s.Key} {s.Value}")));

            if (_declined.Count > 0) Line(text, "  the capture declined a frame: " + string.Join(", ", _declined.GroupBy(d => d.Why).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));

            var stages = new List<(string Name, int Count)>();

            if (_sawCompositor)
            {
                stages.Add(("the page", page.Count));
                stages.Add(("the compositor", _draws.Count));
            }

            if (_sawCapture) stages.Add(("the capture", _captures.Count));

            stages.Add(("the game", _delivered.Count));

            var worst = stages.Zip(stages.Skip(1), (a, b) => (From: a, To: b)).Where(p => p.From.Count > 0).OrderByDescending(p => (p.From.Count - p.To.Count) / (double)p.From.Count).FirstOrDefault();

            if (worst.From.Name != null && worst.From.Count - worst.To.Count >= Math.Max(2, worst.From.Count / 5))
            {
                Line(text, $"  most frames are lost between {worst.From.Name} and {worst.To.Name}: {worst.From.Count} -> {worst.To.Count}");
            }

            var rows = Math.Min(60, (int)Math.Ceiling(seconds));

            if (rows < 1) return;

            Line(text, $"  {"second",6} {"page",6} {"draws",6} {"taken",6} {"to game",8} {"declined",9}");

            int In(IEnumerable<long> list, int s) => list.Count(t => t >= first + s * 1_000_000L && t < first + (s + 1) * 1_000_000L);

            for (var s = 0; s < rows; s++)
            {
                Line(text, $"  {s,6} {In(page, s),6} {In(_draws, s),6} {In(_captures, s),6} {In(_delivered, s),8} {In(_declined.Select(d => d.Ts), s),9}");
            }
        }

        /// <summary>The longest time with none of these events, counting from the trace's start and to its end, in ms.</summary>
        private static double Gap(List<long> times, long first, long end)
        {
            var sorted = times.OrderBy(t => t).ToList();
            var gap = 0L;
            var previous = first;

            foreach (var t in sorted)
            {
                gap = Math.Max(gap, t - previous);
                previous = t;
            }

            return Math.Max(gap, end - previous) / 1000.0;
        }
    }
}
