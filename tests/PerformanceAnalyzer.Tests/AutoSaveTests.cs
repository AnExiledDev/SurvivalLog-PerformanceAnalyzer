using PerformanceAnalyzer.Adapters;
using PerformanceAnalyzer.Core;
using Xunit;

namespace PerformanceAnalyzer.Tests;

/// <summary>
/// The report saves itself while recording, so a crash or a hard close still leaves one. The timer decides when, and
/// the files are swapped in whole so a crash in the middle of a save never leaves half a report.
/// </summary>
public class AutoSaveTests
{
    private const string Stamp = "20261112-010203";

    private static readonly ReportSet Set = new("report text", "frame,ms\n1,16.6\n", "second,fps\n1,60\n");

    [Fact]
    public void Saves_once_per_interval_while_recording()
    {
        var timer = new SaveTimer();

        Assert.False(timer.Due(0, true, 60));
        Assert.False(timer.Due(59.9, true, 60));
        Assert.True(timer.Due(60, true, 60));
        Assert.False(timer.Due(61, true, 60));
        Assert.True(timer.Due(120, true, 60));
    }

    [Fact]
    public void An_interval_of_zero_never_saves()
    {
        var timer = new SaveTimer();

        Assert.False(timer.Due(0, true, 0));
        Assert.False(timer.Due(1000, true, 0));
        Assert.False(timer.Due(5000, true, 0));
    }

    [Fact]
    public void Stopping_saves_nothing_more_and_the_count_starts_again_at_the_next_start()
    {
        var timer = new SaveTimer();

        timer.Due(0, true, 60);

        Assert.False(timer.Due(70, false, 60));
        Assert.False(timer.Due(100, true, 60));
        Assert.False(timer.Due(159, true, 60));
        Assert.True(timer.Due(160, true, 60));
    }

    [Fact]
    public void A_changed_interval_counts_from_when_it_was_changed()
    {
        var timer = new SaveTimer();

        timer.Due(0, true, 60);

        // Still counting from the start, 120 s would have saved at 60; counted from 50, it saves at 170.
        Assert.False(timer.Due(50, true, 120));
        Assert.False(timer.Due(60, true, 120));
        Assert.True(timer.Due(170, true, 120));
    }

    [Fact]
    public void A_save_writes_the_report_its_latest_copy_and_both_spreadsheets()
    {
        var dir = NewFolder();

        var outcome = ReportFiles.Write(dir, Stamp, Set);

        Assert.True(outcome.IsSaved, outcome.Failure);
        Assert.Equal(Path.Combine(dir, $"report-{Stamp}.txt"), outcome.Report);
        Assert.Equal(Set.Text, File.ReadAllText(Path.Combine(dir, $"report-{Stamp}.txt")));
        Assert.Equal(Set.Text, File.ReadAllText(Path.Combine(dir, ReportFiles.Latest)));
        Assert.Equal(Set.FramesCsv, File.ReadAllText(Path.Combine(dir, $"frames-{Stamp}.csv")));
        Assert.Equal(Set.SecondsCsv, File.ReadAllText(Path.Combine(dir, $"seconds-{Stamp}.csv")));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void A_later_save_of_the_same_recording_replaces_its_files()
    {
        var dir = NewFolder();
        var later = new ReportSet("later text", "frame,ms\n", "second,fps\n");

        ReportFiles.Write(dir, Stamp, Set);
        var outcome = ReportFiles.Write(dir, Stamp, later);

        Assert.True(outcome.IsSaved, outcome.Failure);
        Assert.Equal(later.Text, File.ReadAllText(Path.Combine(dir, $"report-{Stamp}.txt")));
        Assert.Equal(later.FramesCsv, File.ReadAllText(Path.Combine(dir, $"frames-{Stamp}.csv")));
        Assert.Equal(4, Directory.GetFiles(dir).Length);
    }

    [Fact]
    public void A_file_that_cannot_be_written_does_not_stop_the_others()
    {
        var dir = NewFolder();

        // A folder where the frames file goes stands in for a spreadsheet held open by another program.
        Directory.CreateDirectory(Path.Combine(dir, $"frames-{Stamp}.csv"));

        var outcome = ReportFiles.Write(dir, Stamp, Set);

        Assert.False(outcome.IsSaved);
        Assert.NotEqual("", outcome.Failure);
        Assert.Equal(Set.Text, File.ReadAllText(Path.Combine(dir, $"report-{Stamp}.txt")));
        Assert.Equal(Set.Text, File.ReadAllText(Path.Combine(dir, ReportFiles.Latest)));
        Assert.Equal(Set.SecondsCsv, File.ReadAllText(Path.Combine(dir, $"seconds-{Stamp}.csv")));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void A_report_folder_that_cannot_be_made_is_a_failure_not_a_crash()
    {
        var blocked = Path.Combine(NewFolder(), "performance-analyzer");

        File.WriteAllText(blocked, "a file where the folder should be");

        var outcome = ReportFiles.Write(blocked, Stamp, Set);

        Assert.False(outcome.IsSaved);
        Assert.NotEqual("", outcome.Failure);
    }

    private static string NewFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pa-autosave-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(dir);

        return dir;
    }
}
