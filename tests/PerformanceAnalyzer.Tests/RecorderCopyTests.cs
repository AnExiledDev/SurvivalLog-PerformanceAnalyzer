using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PerformanceAnalyzer.Adapters;
using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>
/// The report saved while recording is built on another thread from a copy of the recorder, so the main thread only
/// pays for the copy. The copy has to say everything the recorder would, and keep saying it while the recorder goes on
/// recording, or the saved report is wrong or the worker reads numbers the main thread is changing.
/// </summary>
public class RecorderCopyTests
{
    private const double TicksPerMs = 1000;
    private const long MB = 1024 * 1024;

    private static long Ms(double ms) => (long)(ms * TicksPerMs);

    private sealed class Run
    {
        public Run(Recorder recorder, int ticker, int hook, int gameMethod)
        {
            Recorder = recorder;
            Ticker = ticker;
            Hook = hook;
            GameMethod = gameMethod;
        }

        public Recorder Recorder { get; }
        public int Ticker { get; }
        public int Hook { get; }
        public int GameMethod { get; }
        public long Now;
        public int Clock;
        public int Frame;
    }

    /// <summary>
    /// About four seconds of a game with some of everything the report shows: two mods, a hook around a game method,
    /// hitches, slow web messages, two scenes and two pages, memory and web readings, two game speeds and a probe hit
    /// off the main thread. The window is shorter than the recording, so the frame ring has wrapped.
    /// </summary>
    private static Run Recorded()
    {
        var recorder = new Recorder(TicksPerMs, 64, 50);
        var a = recorder.AddMod(new ModInfo("moda", "Mod A", "1.0", "com.test.moda"));
        var b = recorder.AddMod(new ModInfo("modb", "Mod B", "1.0", "com.test.modb"));
        var run = new Run(
            recorder,
            recorder.AddProbe(new ProbeInfo("ModA.Ticker.Update", ProbeKind.Ticker, "", new[] { a })),
            recorder.AddProbe(new ProbeInfo("ModB.Patch.Prefix", ProbeKind.Prefix, "Game.Save", new[] { b })),
            recorder.AddProbe(new ProbeInfo("Game.Save", ProbeKind.Game, "", new[] { Recorder.GameMod })));

        recorder.Start(0);
        recorder.EndFrame(0, default);
        recorder.CountOffThread(run.Ticker);
        Frames(run, 200, "Main", "Map");

        return run;
    }

    /// <summary>Records <paramref name="count"/> frames, the second half in <paramref name="laterScene"/>; every 70th is a hitch.</summary>
    private static void Frames(Run run, int count, string scene, string laterScene)
    {
        var recorder = run.Recorder;

        for (var i = 1; i <= count; i++)
        {
            var frame = ++run.Frame;
            var now = run.Now;
            var isSlow = frame % 70 == 0;

            recorder.SetPage(i > count / 2 ? "PhoneSMS" : "hud");
            recorder.End(recorder.Begin(run.Ticker, now, 0), now + Ms(1.5), 4096);

            if (isSlow)
            {
                var hook = recorder.Begin(run.Hook, now + Ms(2), 0);

                recorder.End(recorder.Begin(run.GameMethod, now + Ms(3), 0), now + Ms(50), MB);
                recorder.End(hook, now + Ms(55), MB);
                recorder.NoteSlowMessage(now + Ms(60), 60, string.Join(SlowMessage.Separator, "3", "PhoneSMS", "open", "data"));
            }

            if (frame % 50 == 0) recorder.Game.NoteWeb(new WebReading(10, 2, 5, 3, 4, 12));

            run.Now += isSlow ? Ms(80) : Ms(20);

            var world = new WorldReading(1, frame > 120 ? 2 : 1, 0.02f, isSlow ? 0.08f : 0.02f, 0.1f, 0.02f, 1, true, true, false, run.Clock += 1);
            var memory = frame % 50 == 1 ? new MemoryReading(10 * MB, (200 + frame) * MB, 300 * MB, 400 * MB, 50 * MB, 900 * MB) : default;

            recorder.EndFrame(run.Now, new FrameContext(0, 1, new EngineTimes(5, 4, 6), i > count / 2 ? laterScene : scene, world, memory));
        }
    }

    private static ReportInput Input() => new(Ms(4200), new DateTime(2026, 11, 12, 6, 0, 0), "0.7.9", "1.1.20719", "Windows; CPU", InstrumentStatus.None with { Hooks = 2, Notes = new[] { "a note" } }, new[] { new ScriptRow("moda", 3, 900, 400) }, "", new[] { new ModInfo("moda", "Mod A", "1.0", "com.test.moda") });

    /// <summary>The report, both spreadsheets and the panel's snapshot for every tab: everything the analyzer reads from a recorder.</summary>
    private static List<string> Everything(Recorder recorder)
    {
        var all = new List<string> { Report.Text(recorder, Input()), Report.FramesCsv(recorder), Report.SecondsCsv(recorder) };

        foreach (var tab in Enum.GetValues<PanelTab>()) all.Add(Snapshot.Json(recorder, new SnapshotInput(Ms(4200), tab, true, InstrumentStatus.None, Array.Empty<ScriptRow>(), true, "0.7.9")));

        return all;
    }

    [Fact]
    public void A_copy_says_everything_the_recorder_it_was_taken_from_would()
    {
        var recorder = Recorded().Recorder;
        var copy = recorder.Copy();
        var text = Report.Text(copy, Input());

        Assert.Equal(Everything(recorder), Everything(copy));

        // The recording has what the comparison is meant to cover.
        Assert.Equal(2, copy.HitchCount);
        Assert.Contains("Game.Save", text);
        Assert.Contains("PhoneSMS", text);
        Assert.Contains("Map", text);
        Assert.Equal(64, copy.Ring.Count);
        Assert.Equal(4, copy.Game.Timeline.Count);
    }

    [Fact]
    public void A_report_built_from_a_copy_on_another_thread_keeps_the_numbers_from_when_it_was_copied()
    {
        var run = Recorded();
        var recorder = run.Recorder;
        var before = Everything(recorder);
        var copy = recorder.Copy();
        var building = Task.Run(() => ReportSet.Of(copy, Input()));

        // The main thread goes on recording while the worker builds: new frames, hitches, a scene and a page.
        recorder.CountOffThread(run.Hook);
        Frames(run, 300, "Home", "Main");

        var set = building.Result;

        Assert.Equal(new ReportSet(before[0], before[1], before[2]), set);
        Assert.Equal(before, Everything(copy));
        Assert.NotEqual(before, Everything(recorder));

        recorder.Reset(run.Now);

        Assert.Equal(before, Everything(copy));
    }
}
