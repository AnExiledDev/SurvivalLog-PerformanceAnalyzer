using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// The game's side of the web UI, timed from inside the game while a trace runs. The browser's own trace stops where
/// the browser hands a frame over; this picks up from there. It measures three hops:
/// a script sent to the page and the page's answer back (the message round trip, which also lines the two clocks up),
/// each frame the page draws to the first game frame that shows it (read back from the game's copy of the page), and
/// a mouse move the game sees to the page getting it. Collects on the main thread; <see cref="Text"/> sums it up.
/// </summary>
public sealed class HopRecord
{
    /// <summary>A mouse move after this long a pause starts a new move, so moves on both sides can be paired.</summary>
    public const double MoveGapMs = 200;

    /// <summary>The page's counter wraps here; a trace never draws this many frames.</summary>
    public const int CounterLimit = 2048;

    private readonly Dictionary<int, long> _pings = new();
    private readonly List<(int Id, double PageMs, long At)> _pongs = new();
    private readonly List<long> _gameFrames = new();
    private readonly List<long> _gameMoves = new();
    private readonly List<(long At, int Counter)> _sightings = new();
    private readonly List<double> _pageFrames = new();
    private readonly List<double> _pageMoves = new();

    public int ReadFailures { get; private set; }

    public int Unreadable { get; private set; }

    public int Skipped { get; private set; }

    public bool HasPageFrames { get; private set; }

    public bool HasPageMoves { get; private set; }

    /// <summary>Which row held the counter: "top", "bottom", or empty until one is read.</summary>
    public string Row { get; private set; } = "";

    /// <summary>The first pixel that could not be read, as four bytes, so a wrong colour format shows in the report.</summary>
    public string FirstUnreadable { get; private set; } = "";

    public void Clear()
    {
        _pings.Clear();
        _pongs.Clear();
        _gameFrames.Clear();
        _gameMoves.Clear();
        _sightings.Clear();
        _pageFrames.Clear();
        _pageMoves.Clear();
        ReadFailures = 0;
        Unreadable = 0;
        Skipped = 0;
        HasPageFrames = false;
        HasPageMoves = false;
        Row = "";
        FirstUnreadable = "";
    }

    public void Sent(int id, long at) => _pings[id] = at;

    public void Answered(int id, double pageMs, long at) => _pongs.Add((id, pageMs, at));

    public void GameFrame(long at) => _gameFrames.Add(at);

    public void GameMove(long at) => _gameMoves.Add(at);

    public void ReadFailed() => ReadFailures++;

    public void ReadSkipped() => Skipped++;

    /// <summary>One game frame's copy of the page, read at its top row and at its bottom row (the copy may be upside down).</summary>
    public void Read(long at, ReadOnlySpan<byte> top, ReadOnlySpan<byte> bottom)
    {
        var counter = Decode(top);

        if (counter >= 0)
        {
            Row = "top";
        }
        else
        {
            counter = Decode(bottom);

            if (counter >= 0) Row = "bottom";
        }

        if (counter < 0)
        {
            Unreadable++;

            if (FirstUnreadable.Length == 0) FirstUnreadable = Bytes(top) + " / " + Bytes(bottom);

            return;
        }

        _sightings.Add((at, counter));
    }

    /// <summary>"c,ms;c,ms;..." from the page: each frame it drew and when, in the page's clock. The counter is the index.</summary>
    public void PageFrames(string text)
    {
        HasPageFrames = true;
        _pageFrames.Clear();

        foreach (var item in Items(text))
        {
            var comma = item.IndexOf(',');

            if (comma < 0 || !int.TryParse(item.Substring(0, comma), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c)) continue;
            if (!double.TryParse(item.Substring(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) continue;
            if (c != _pageFrames.Count) continue;

            _pageFrames.Add(ms);
        }
    }

    /// <summary>"ms;ms;..." from the page: each mouse move it got, in the page's clock.</summary>
    public void PageMoves(string text)
    {
        HasPageMoves = true;
        _pageMoves.Clear();

        foreach (var item in Items(text))
        {
            if (double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) _pageMoves.Add(ms);
        }
    }

    /// <summary>
    /// The page paints its counter as red 4*(c/64)+2, green 4*(c%64)+2, blue 255, opaque. The copy can be in BGRA or
    /// RGBA order, so the 255 says which end is blue; the +2 leaves room for a colour off by one. -1 when it is not ours.
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> pixel)
    {
        if (pixel.Length < 4 || pixel[3] < 250) return -1;

        int red;

        if (pixel[2] >= 250 && pixel[0] < 250) red = pixel[0];
        else if (pixel[0] >= 250 && pixel[2] < 250) red = pixel[2];
        else return -1;

        var high = red >> 2;

        return high > 31 ? -1 : high * 64 + (pixel[1] >> 2);
    }

    /// <summary>The colour the page paints for a counter, as red, green, blue.</summary>
    public static (int R, int G, int B) Colour(int counter)
    {
        var c = ((counter % CounterLimit) + CounterLimit) % CounterLimit;

        return (4 * (c / 64) + 2, 4 * (c % 64) + 2, 255);
    }

    public IReadOnlyDictionary<int, long> Pings => _pings;
    public IReadOnlyList<(int Id, double PageMs, long At)> Pongs => _pongs;
    public IReadOnlyList<long> GameFrames => _gameFrames;
    public IReadOnlyList<long> GameMoves => _gameMoves;
    public IReadOnlyList<(long At, int Counter)> Sightings => _sightings;
    public IReadOnlyList<double> PageFrameTimes => _pageFrames;
    public IReadOnlyList<double> PageMoveTimes => _pageMoves;

    private static IEnumerable<string> Items(string text) => text.Split(';').Where(item => item.Length > 0).Take(CounterLimit);

    private static string Bytes(ReadOnlySpan<byte> pixel)
    {
        var parts = new List<string>();

        foreach (var b in pixel) parts.Add(b.ToString(CultureInfo.InvariantCulture));

        return string.Join(",", parts);
    }
}

public static class HopLatency
{
    public static string Text(HopRecord record, double ticksPerMs, string stamp)
    {
        var text = new StringBuilder();
        double Ms(long ticks) => ticks / ticksPerMs;
        void Line(string line) => text.Append(line).Append('\n');

        Line($"Game side of the web UI {stamp}, timed inside the game during the trace.");

        var frames = record.GameFrames;
        var spanS = frames.Count > 1 ? (Ms(frames[frames.Count - 1]) - Ms(frames[0])) / 1000 : 0;

        Line(spanS > 0 ? $"game frames {frames.Count} ({Rate(frames.Count - 1, spanS)} per s) over {F(spanS)} s" : "game frames: too few to time");

        // The message round trip, and the page's clock against the game's from the quickest answer.
        var trips = new List<(double Rtt, double PageMs, double SentMs)>();

        foreach (var pong in record.Pongs)
        {
            if (record.Pings.TryGetValue(pong.Id, out var sent) && pong.At >= sent) trips.Add((Ms(pong.At) - Ms(sent), pong.PageMs, Ms(sent)));
        }

        double? offset = null;

        if (trips.Count == 0)
        {
            Line($"messages to the page and back: none of {record.Pings.Count} answered");
        }
        else
        {
            var quickest = trips.OrderBy(t => t.Rtt).First();

            offset = quickest.PageMs - (quickest.SentMs + quickest.Rtt / 2);
            Line($"messages to the page and back: {trips.Count} of {record.Pings.Count} answered, round trip {Spread(trips.Select(t => t.Rtt))}");
        }

        var pageFrames = record.PageFrameTimes;

        if (!record.HasPageFrames)
        {
            Line("page frames: the page never sent its list (it may have reloaded during the trace)");
        }
        else if (pageFrames.Count > 1)
        {
            var pageSpanS = (pageFrames[pageFrames.Count - 1] - pageFrames[0]) / 1000;
            var gaps = pageFrames.Zip(pageFrames.Skip(1), (a, b) => b - a).ToList();

            Line($"page frames drawn {pageFrames.Count} ({Rate(pageFrames.Count - 1, pageSpanS)} per s), longest gap between two {F(gaps.Max())} ms");
        }
        else
        {
            Line($"page frames drawn {pageFrames.Count}");
        }

        WriteShown(record, Ms, offset, Line);
        WriteMoves(record, Ms, offset, Line);

        Line($"reads of the game's copy of the page: {record.Sightings.Count} read, {record.ReadFailures} failed, {record.Unreadable} without the counter, {record.Skipped} skipped" +
            (record.Row.Length > 0 ? $", counter found in the {record.Row} row" : ""));

        if (record.FirstUnreadable.Length > 0) Line("first pixel without the counter (top / bottom, as bytes): " + record.FirstUnreadable);

        return text.ToString();
    }

    /// <summary>Which page frames the game showed, how late, and the longest the game held one.</summary>
    private static void WriteShown(HopRecord record, Func<long, double> ms, double? offset, Action<string> line)
    {
        var sightings = record.Sightings;

        if (sightings.Count == 0)
        {
            line("page frames on the game's screen: none read");

            return;
        }

        // The first game frame showing each new page frame. A counter going back is a misread and is left out.
        var firsts = new List<(long At, int Counter)>();

        foreach (var s in sightings)
        {
            if (firsts.Count > 0 && s.Counter <= firsts[firsts.Count - 1].Counter) continue;

            firsts.Add(s);
        }

        var spanS = firsts.Count > 1 ? (ms(firsts[firsts.Count - 1].At) - ms(firsts[0].At)) / 1000 : 0;
        var between = firsts[firsts.Count - 1].Counter - firsts[0].Counter + 1;
        var holds = firsts.Zip(firsts.Skip(1), (a, b) => ms(b.At) - ms(a.At)).ToList();

        line($"page frames on the game's screen {firsts.Count}" + (spanS > 0 ? $" ({Rate(firsts.Count - 1, spanS)} per s)" : "") +
            $", {between - firsts.Count} of the {between} drawn in that time never shown" +
            (holds.Count > 0 ? $", longest the game kept one {F(holds.Max())} ms" : ""));

        var pageFrames = record.PageFrameTimes;

        if (offset is not double o || pageFrames.Count == 0) return;

        var late = firsts.Where(f => f.Counter < pageFrames.Count).Select(f => ms(f.At) - (pageFrames[f.Counter] - o)).ToList();

        if (late.Count > 0) line($"page drawing a frame to the game showing it: {Spread(late)} ({late.Count} frames)");
    }

    /// <summary>Pairs each mouse move the game saw after a pause with the page's first move after it.</summary>
    private static void WriteMoves(HopRecord record, Func<long, double> ms, double? offset, Action<string> line)
    {
        var game = Starts(record.GameMoves.Select(ms));

        if (game.Count == 0)
        {
            line("mouse: no moves during the trace");

            return;
        }

        if (offset is not double o || !record.HasPageMoves)
        {
            line($"mouse: {game.Count} moves, the page's side is missing");

            return;
        }

        var page = Starts(record.PageMoveTimes.Select(p => p - o));
        var delays = new List<double>();

        foreach (var g in game)
        {
            foreach (var p in page)
            {
                if (p < g - 5) continue;
                if (p < g + 1000) delays.Add(Math.Max(0, p - g));

                break;
            }
        }

        line(delays.Count > 0
            ? $"mouse: the game seeing a move to the page getting it: {Spread(delays)} ({delays.Count} of {game.Count} moves after a pause)"
            : $"mouse: {game.Count} moves after a pause, none reached the page within 1 s");
    }

    private static List<double> Starts(IEnumerable<double> times)
    {
        var starts = new List<double>();
        var last = double.NegativeInfinity;

        foreach (var t in times.OrderBy(t => t))
        {
            if (t - last >= HopRecord.MoveGapMs) starts.Add(t);

            last = t;
        }

        return starts;
    }

    private static string Spread(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        double At(double q) => v[Math.Min(v.Count - 1, (int)Math.Floor(q * v.Count))];

        return $"p50 {F(At(0.5))} ms, p90 {F(At(0.9))}, max {F(v[v.Count - 1])}";
    }

    private static string Rate(int intervals, double seconds) => seconds > 0 ? F(intervals / seconds) : "-";

    private static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
}
