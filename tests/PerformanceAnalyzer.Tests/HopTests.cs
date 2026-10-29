using System.Globalization;
using System.Linq;
using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>
/// The game's side of the web UI: the message round trip, which page frames reach the game's screen and how late, and
/// how long a mouse move takes to reach the page. These are the hops the browser's trace cannot see, so a wrong
/// number here sends the lag hunt to the wrong place.
/// </summary>
public class HopTests
{
    private const double TicksPerMs = 1000;

    // The page's clock runs 1000 ms behind the game's.
    private const double PageBehind = 1000;

    private static long Game(double ms) => (long)(ms * TicksPerMs);

    private static string Page(double gameMs) => (gameMs - PageBehind).ToString("0.00", CultureInfo.InvariantCulture);

    private static byte[] Bgra(int counter)
    {
        var (r, g, b) = HopRecord.Colour(counter);

        return new[] { (byte)b, (byte)g, (byte)r, (byte)255 };
    }

    private static readonly byte[] Black = { 0, 0, 0, 255 };

    private static void Show(HopRecord record, double gameMs, int counter) => record.Read(Game(gameMs), Bgra(counter), Black);

    [Fact]
    public void Every_counter_reads_back_in_either_byte_order_and_with_colours_off_by_one()
    {
        for (var c = 0; c < HopRecord.CounterLimit; c++)
        {
            var (r, g, b) = HopRecord.Colour(c);

            foreach (var off in new[] { -1, 0, 1 })
            {
                Assert.Equal(c, HopRecord.Decode(new[] { (byte)b, (byte)(g + off), (byte)(r + off), (byte)255 }));
                Assert.Equal(c, HopRecord.Decode(new[] { (byte)(r - off), (byte)(g - off), (byte)b, (byte)255 }));
            }
        }
    }

    [Theory]
    [InlineData(255, 255, 255, 255)] // white page
    [InlineData(10, 20, 30, 255)] // no blue marker
    [InlineData(255, 10, 2, 120)] // see-through
    [InlineData(255, 10, 200, 255)] // red too high to be a counter
    public void A_pixel_that_is_not_the_counter_reads_as_none(int b0, int b1, int b2, int b3)
    {
        Assert.Equal(-1, HopRecord.Decode(new[] { (byte)b0, (byte)b1, (byte)b2, (byte)b3 }));
    }

    [Fact]
    public void The_page_s_answers_parse_and_bad_ones_are_refused()
    {
        Assert.Equal(new PageCommand.Pong(7, 1234.5), PageCommand.Parse("slmod|perfanalyzer|pong|7|1234.50"));
        Assert.IsType<PageCommand.Unknown>(PageCommand.Parse("slmod|perfanalyzer|pong|7"));
        Assert.IsType<PageCommand.Unknown>(PageCommand.Parse("slmod|perfanalyzer|pong|x|1.0"));
        Assert.Equal(new PageCommand.HopFrames("0,1.00;1,2.00"), PageCommand.Parse("slmod|perfanalyzer|hopframes|0,1.00;1,2.00"));
        Assert.Equal(new PageCommand.HopMoves(""), PageCommand.Parse("slmod|perfanalyzer|hopmoves|"));
    }

    [Fact]
    public void The_round_trip_counts_only_answered_pings_and_lines_up_the_clocks()
    {
        var record = new HopRecord();

        record.Sent(0, Game(0));
        record.Answered(0, double.Parse(Page(10), CultureInfo.InvariantCulture), Game(20));
        record.Sent(1, Game(100));
        record.Answered(1, double.Parse(Page(115), CultureInfo.InvariantCulture), Game(140));
        record.Sent(2, Game(200));

        // One page frame drawn at game 50 ms and first shown at game 120 ms: 70 ms late only if the clocks line up.
        record.PageFrames("0," + Page(50));
        Show(record, 120, 0);

        var text = HopLatency.Text(record, TicksPerMs, "s");

        Assert.Contains("messages to the page and back: 2 of 3 answered, round trip p50 40.0 ms, p90 40.0, max 40.0", text);
        Assert.Contains("page drawing a frame to the game showing it: p50 70.0 ms, p90 70.0, max 70.0 (1 frames)", text);
    }

    [Fact]
    public void Frames_the_game_never_showed_and_the_longest_it_kept_one_are_counted()
    {
        var record = new HopRecord();

        record.Sent(0, Game(0));
        record.Answered(0, double.Parse(Page(10), CultureInfo.InvariantCulture), Game(20));
        record.PageFrames(string.Join(";", Enumerable.Range(0, 10).Select(c => c + "," + Page(50 + 40 * c))));

        // Frames 0-3 shown 70 ms after drawing; 3 stays up until 6 arrives, so 4 and 5 are lost; then a misread.
        Show(record, 120, 0);
        Show(record, 160, 1);
        Show(record, 200, 2);
        Show(record, 240, 3);
        Show(record, 260, 3);
        Show(record, 400, 6);
        Show(record, 420, 7);
        Show(record, 440, 2);
        record.Read(Game(460), Black, Black);

        var text = HopLatency.Text(record, TicksPerMs, "s");

        Assert.Contains("page frames drawn 10 (25.0 per s), longest gap between two 40.0 ms", text);
        Assert.Contains("page frames on the game's screen 6 (16.7 per s), 2 of the 8 drawn in that time never shown, longest the game kept one 160.0 ms", text);
        Assert.Contains("page drawing a frame to the game showing it: p50 70.0 ms, p90 110.0, max 110.0 (6 frames)", text);
        Assert.Contains("8 read, 0 failed, 1 without the counter, 0 skipped, counter found in the top row", text);
        Assert.Contains("first pixel without the counter (top / bottom, as bytes): 0,0,0,255 / 0,0,0,255", text);
    }

    [Fact]
    public void A_flipped_copy_is_read_at_the_bottom_row()
    {
        var record = new HopRecord();

        record.Read(Game(0), Black, Bgra(5));

        Assert.Equal("bottom", record.Row);
        Assert.Contains("counter found in the bottom row", HopLatency.Text(record, TicksPerMs, "s"));
    }

    [Fact]
    public void Mouse_moves_after_a_pause_are_paired_with_the_page_s_first_move_after_them()
    {
        var record = new HopRecord();

        record.Sent(0, Game(0));
        record.Answered(0, double.Parse(Page(10), CultureInfo.InvariantCulture), Game(20));

        // Two moves: one starting at 1000 ms (three frames), one at 1500 ms after a pause.
        foreach (var ms in new[] { 1000.0, 1010, 1020, 1500 }) record.GameMove(Game(ms));

        record.PageMoves(string.Join(";", new[] { 1030.0, 1040, 1545 }.Select(Page)));

        Assert.Contains("mouse: the game seeing a move to the page getting it: p50 45.0 ms, p90 45.0, max 45.0 (2 of 2 moves after a pause)", HopLatency.Text(record, TicksPerMs, "s"));
    }

    [Fact]
    public void A_page_that_never_sent_its_lists_says_so_rather_than_showing_zeros()
    {
        var record = new HopRecord();

        record.Sent(0, Game(0));
        record.GameMove(Game(100));

        var text = HopLatency.Text(record, TicksPerMs, "s");

        Assert.Contains("messages to the page and back: none of 1 answered", text);
        Assert.Contains("page frames: the page never sent its list", text);
        Assert.Contains("mouse: 1 moves, the page's side is missing", text);
    }

    [Fact]
    public void Page_frames_out_of_order_are_dropped_so_the_counter_stays_the_index()
    {
        var record = new HopRecord();

        record.PageFrames("0,1.00;5,2.00;1,3.00;bad;2,x;2,4.00");

        Assert.Equal(new[] { 1.0, 3.0, 4.0 }, record.PageFrameTimes);
    }
}
