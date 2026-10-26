using System.Linq;
using System.Text.Json;
using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>The game side of a recording: logic updates, game clock, game time lost to slow frames, memory, scenes.</summary>
public class GameStatsTests
{
    private const long MB = 1024 * 1024;

    /// <summary>A running, unpaused game at 1x whose engine handed out the whole frame as game time.</summary>
    private static WorldReading Running(double frameMs, int clock = -1) =>
        new(1, 1, (float)(frameMs / 1000), (float)(frameMs / 1000), 0.333f, 0.02f, 1, true, true, false, clock);

    private static readonly WorldReading Menu = new(1, 0, 0.016f, 0.016f, 0.333f, 0.02f, 1, false, false, false, -1);

    /// <summary>Feeds frames of <paramref name="frameMs"/> back to back, starting at <paramref name="at"/> seconds.</summary>
    private static double Feed(GameStats game, double at, int frames, double frameMs, string scene, System.Func<int, WorldReading> world, double hitchMs = 50)
    {
        for (var i = 0; i < frames; i++)
        {
            at += frameMs / 1000;
            game.Add(at, frameMs, frameMs >= hitchMs, scene, world(i), default);
        }

        return at;
    }

    [Fact]
    public void A_frame_past_the_engines_cap_loses_the_game_time_the_cap_cut_off()
    {
        // Half a second of real time at 2x should move the game a whole second; the engine let it move 0.666 s.
        var capped = new WorldReading(2, 2, 0.666f, 0.5f, 0.333f, 0.02f, 0, true, true, false, -1);

        Assert.Equal(334, capped.CappedMs, 1);
        Assert.Equal(0, Running(16).CappedMs, 3);
    }

    [Fact]
    public void Logic_updates_per_second_count_only_time_in_a_game_so_menus_do_not_dilute_them()
    {
        var game = new GameStats();
        var at = Feed(game, 0, 60, 1000.0 / 60, "Menu", _ => Menu);

        // A second in a game where the world updated on every other frame.
        Feed(game, at, 60, 1000.0 / 60, "Main", i => Running(1000.0 / 60) with { LogicRan = i % 2 == 0 });

        Assert.Equal(30, game.LogicPerSecond, 1);
        Assert.Equal(1000, game.InGameMs, 1);
        Assert.Equal(60, game.FixedPerSecond, 1);
    }

    [Fact]
    public void The_clock_rate_is_game_seconds_per_real_second_while_running_and_a_load_jump_is_not_time_passing()
    {
        var game = new GameStats();
        var clock = 1000;

        // 100 frames of 10 ms, the clock moving 2 game seconds a frame: 200 game seconds per real second.
        var at = Feed(game, 0, 100, 10, "Main", _ => Running(10, clock += 2));

        // A save loads: the clock jumps a day ahead in one frame.
        game.Add(at + 0.01, 10, false, "Main", Running(10, clock + 86400), default);

        Assert.Equal(198, game.ClockAdvance);
        // 101 running frames of 10 ms.
        Assert.Equal(198 * 1000.0 / 1010, game.ClockRate, 1);
    }

    [Fact]
    public void A_paused_game_still_counts_logic_updates_but_adds_no_running_time_and_no_lost_game_time()
    {
        var game = new GameStats();

        // Paused with the engine still at full time scale: each frame looks like 20 ms of dropped game time.
        Feed(game, 0, 50, 20, "Main", _ => Running(20) with { Paused = true, DeltaSeconds = 0.000002f });

        Assert.Equal(50, game.LogicFrames);
        Assert.Equal(1000, game.PausedMs, 1);
        Assert.Equal(0, game.LogicMs);
        Assert.Equal(0, game.CappedMs);
        Assert.Equal(0, game.ClockRate);
    }

    [Fact]
    public void Game_time_lost_is_a_share_of_all_the_game_time_the_frames_should_have_had()
    {
        var game = new GameStats();

        // Three normal 100 ms frames, then one 1000 ms frame cut to 333 ms of game time.
        var at = Feed(game, 0, 3, 100, "Main", _ => Running(100));

        game.Add(at + 1, 1000, true, "Main", Running(1000) with { DeltaSeconds = 0.333f }, default);

        Assert.Equal(1, game.CappedFrames);
        Assert.Equal(667, game.CappedMs, 0);
        Assert.Equal(667.0 * 100 / (300 + 333 + 667), game.CappedPercent, 1);
    }

    [Fact]
    public void The_timeline_keeps_one_sample_per_whole_second_with_the_latest_memory_reading()
    {
        var game = new GameStats();
        var memory = new MemoryReading(10 * MB, 200 * MB, 300 * MB, 400 * MB, 50 * MB, 900 * MB);

        // Frames end just before each 20 ms mark, so 50 land in second 0 and the rest in the unfinished second 1.
        for (var i = 1; i <= 75; i++) game.Add(i * 0.02 - 0.0001, 20, false, "Main", Running(20), i == 10 ? memory : default);

        var first = Assert.Single(game.Timeline);

        Assert.Equal(0, first.Second);
        Assert.Equal(50, first.Frames);
        Assert.Equal(50, first.Fps, 1);
        Assert.Equal(50, first.LogicPerSecond, 1);
        Assert.Equal(200 * MB, first.Memory.GameHeap);
        Assert.Equal("Main", first.Scene);
    }

    [Fact]
    public void The_timeline_drops_its_oldest_second_past_an_hour()
    {
        var game = new GameStats();

        for (var s = 0; s <= GameStats.TimelineSeconds + 5; s++) game.Add(s + 0.5, 16, false, "Main", Running(16), default);

        Assert.Equal(GameStats.TimelineSeconds, game.Timeline.Count);
        Assert.Equal(5, game.Timeline[0].Second);
    }

    [Fact]
    public void Scene_percentiles_come_from_the_histogram_within_one_bucket()
    {
        var scene = new SceneStats("Main");

        for (var i = 0; i < 97; i++) scene.Add(10.1, false, true);

        scene.Add(40, false, true);
        scene.Add(100, true, true);
        scene.Add(400, true, false);

        Assert.InRange(scene.Percentile(0.50), 10.1, 10.1 + SceneStats.BucketMs);
        Assert.InRange(scene.Percentile(0.98), 40, 40 + SceneStats.BucketMs);
        Assert.InRange(scene.Percentile(0.99), 100, 100 + SceneStats.BucketMs);
        // Past the histogram the slowest frame is the answer.
        Assert.Equal(400, scene.Percentile(1.0));
        Assert.Equal(400, scene.MaxMs);
        Assert.Equal(3, scene.Over16);
        Assert.Equal(3, scene.Over33);
        Assert.Equal(2, scene.Hitches);
        Assert.Equal(99, scene.LogicFrames);

        // The top of a bucket is never reported past the slowest frame.
        var one = new SceneStats("One");

        one.Add(10.1, false, false);
        Assert.Equal(10.1, one.Percentile(0.5), 6);
    }

    [Fact]
    public void Scenes_are_kept_apart_in_the_order_they_were_first_seen()
    {
        var game = new GameStats();
        var at = Feed(game, 0, 10, 10, "Menu", _ => Menu);

        at = Feed(game, at, 20, 40, "Main", _ => Running(40));
        Feed(game, at, 5, 10, "Menu", _ => Menu);

        Assert.Equal(new[] { "Menu", "Main" }, game.Scenes.Select(s => s.Name));
        Assert.Equal(15, game.Scenes[0].Frames);
        Assert.Equal(20, game.Scenes[1].Over33);
        Assert.Equal(25, game.Scenes[1].Fps, 1);
    }

    [Fact]
    public void Memory_growth_per_minute_waits_for_a_minute_of_readings()
    {
        var game = new GameStats();

        game.Add(0.5, 16, false, "Main", Running(16), new MemoryReading(0, 100 * MB, 0, 0, 0, 0));
        game.Add(30.5, 16, false, "Main", Running(16), new MemoryReading(0, 105 * MB, 0, 0, 0, 0));

        Assert.Equal(0, game.GrowthPerMinute(m => m.GameHeap));

        game.Add(120.5, 16, false, "Main", Running(16), new MemoryReading(0, 120 * MB, 0, 0, 0, 0));

        Assert.Equal(10 * MB, game.GrowthPerMinute(m => m.GameHeap), 0);
        Assert.Equal(120 * MB, game.PeakMemory.GameHeap);
        // A reading the game build never gave stays at no growth, not a drop to zero.
        Assert.Equal(0, game.GrowthPerMinute(m => m.Textures));
    }

    [Fact]
    public void The_recorder_feeds_every_frame_to_the_game_side_and_a_reset_clears_it()
    {
        var recorder = new Recorder(1000, 64, 50);

        recorder.Start(0);
        recorder.EndFrame(0, default);

        for (var i = 1; i <= 120; i++) recorder.EndFrame(i * 20_000, new FrameContext(0, 0, default, "Main", Running(20)));

        Assert.Equal(120, recorder.Game.Frames);
        Assert.Equal(1, recorder.Ring[0].World.FixedSteps);
        Assert.NotEmpty(recorder.Game.Timeline);
        Assert.Single(recorder.Game.Scenes);

        recorder.Reset(3_000_000);

        Assert.Equal(0, recorder.Game.Frames);
        Assert.Empty(recorder.Game.Timeline);
        Assert.Empty(recorder.Game.Scenes);
        Assert.Empty(recorder.Game.SpeedMs);
    }

    private static Recorder RecordedGame()
    {
        var recorder = new Recorder(1000, 256, 50);
        var clock = 0;

        recorder.Start(0);
        recorder.EndFrame(0, default);

        // Three seconds at 50 frames per second, every tenth frame slow enough to be cut by the cap.
        for (var i = 1; i <= 150; i++)
        {
            var slow = i % 10 == 0;
            var world = Running(20, clock += 1) with { DeltaSeconds = slow ? 0.01f : 0.02f, GameSpeed = i > 100 ? 2 : 1 };
            var memory = i % 50 == 1 ? new MemoryReading(10 * MB, (200 + i) * MB, 300 * MB, 400 * MB, 0, 900 * MB) : default;

            recorder.EndFrame(i * 20_000, new FrameContext(0, 0, default, i > 75 ? "Map" : "Main", world, memory));
        }

        return recorder;
    }

    private static SnapshotInput Input(PanelTab tab) => new(3_000_000, tab, false, InstrumentStatus.None, System.Array.Empty<ScriptRow>(), false, "0.2.0");

    [Fact]
    public void Every_snapshot_carries_the_game_and_memory_readings_for_the_overlay()
    {
        using var doc = JsonDocument.Parse(Snapshot.Json(RecordedGame(), Input(PanelTab.Mods)));
        var root = doc.RootElement;
        var game = root.GetProperty("game");

        Assert.True(game.GetProperty("inGame").GetBoolean());
        Assert.Equal(50, game.GetProperty("logicNow").GetDouble(), 0);
        Assert.Equal(50, game.GetProperty("physicsRate").GetDouble(), 0);
        Assert.Equal(2, game.GetProperty("speed").GetDouble());
        Assert.True(game.GetProperty("cappedPct").GetDouble() > 1);
        Assert.Equal(900, root.GetProperty("memory").GetProperty("process").GetProperty("now").GetDouble(), 0);
        Assert.Equal(0, root.GetProperty("memory").GetProperty("textures").GetProperty("now").GetDouble());
        Assert.False(root.TryGetProperty("scenes", out _));
        Assert.False(root.TryGetProperty("timeline", out _));
    }

    [Fact]
    public void The_game_tab_gets_the_timeline_the_scenes_and_the_time_at_each_speed()
    {
        using var doc = JsonDocument.Parse(Snapshot.Json(RecordedGame(), Input(PanelTab.Game)));
        var root = doc.RootElement;
        var scenes = root.GetProperty("scenes");

        // Seconds 0 to 2 are complete; frame 150 opened second 3.
        Assert.Equal(3, root.GetProperty("timeline").GetProperty("fps").GetArrayLength());
        Assert.Equal(50, root.GetProperty("timeline").GetProperty("fps")[0].GetDouble(), 0);
        Assert.Equal("Main", scenes[0].GetProperty("name").GetString());
        Assert.Equal("Map", scenes[1].GetProperty("name").GetString());
        Assert.Equal(2, root.GetProperty("speeds").GetArrayLength());
        Assert.True(Tabs.TryParse("game", out var tab) && tab == PanelTab.Game);
    }

    [Fact]
    public void The_report_has_the_game_clock_memory_and_scenes_in_plain_ascii()
    {
        var text = Report.Text(RecordedGame(), new ReportInput(3_000_000, new System.DateTime(2026, 10, 25), "0.2.0", "1.1.19611", "", InstrumentStatus.None, System.Array.Empty<ScriptRow>(), "", System.Array.Empty<ModInfo>()));

        foreach (var heading in new[] { "== Game clock and logic", "== Memory (MB)", "== Scenes (frame time in ms)" }) Assert.Contains(heading, text);

        Assert.Contains($"{"textures",-20} {"n/a",9}", text);
        Assert.Contains("1x 67%, 2x 33%", text);
        Assert.Matches(@"Slow frames cost \d+\.\d s of game time over \d+ frames", text);
        Assert.Contains("Map", text);
        Assert.Contains("the game itself slowed down", text);
        Assert.All(text, c => Assert.True(c < 128, "non-ASCII character in the report: " + (int)c));
    }

    [Fact]
    public void The_seconds_file_has_a_line_per_second_and_the_frames_file_has_the_game_columns()
    {
        var recorder = RecordedGame();
        var seconds = Report.SecondsCsv(recorder).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        var frames = Report.FramesCsv(recorder).Split('\n', System.StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("second,frames,fps", seconds[0]);
        Assert.Equal(4, seconds.Length);
        Assert.Contains(",Main,", seconds[1]);
        Assert.EndsWith(",paused,capped_ms,page", frames[0]);
        Assert.EndsWith(",1,1,0,10,", frames[10]);
    }
}
