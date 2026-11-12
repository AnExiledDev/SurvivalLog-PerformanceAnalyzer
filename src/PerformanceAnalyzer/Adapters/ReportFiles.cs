using System;
using System.IO;

namespace PerformanceAnalyzer.Adapters;

/// <summary>A report's text and its two spreadsheets, made on the main thread so they can be written on another.</summary>
internal sealed record ReportSet(string Text, string FramesCsv, string SecondsCsv);

/// <summary>Where a save went, and the first thing that went wrong ("" when every file was written).</summary>
internal sealed record SaveOutcome(string Report, string Failure)
{
    public bool IsSaved => Failure.Length == 0;
}

/// <summary>Writes a report and its spreadsheets into the report folder.</summary>
internal static class ReportFiles
{
    public const string Latest = "latest-report.txt";

    /// <summary>
    /// Writes report-&lt;stamp&gt;.txt, latest-report.txt, frames-&lt;stamp&gt;.csv and seconds-&lt;stamp&gt;.csv. Each file
    /// goes to a temporary name first and is then moved over the old one, so a game that crashes partway through
    /// leaves the last whole file, not half of a new one. A file that can't be written (a spreadsheet open in Excel,
    /// say) doesn't stop the others.
    /// </summary>
    public static SaveOutcome Write(string dir, string stamp, ReportSet set)
    {
        var report = Path.Combine(dir, $"report-{stamp}.txt");

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            return new SaveOutcome(report, ex.Message);
        }

        var failure = Replace(report, set.Text);

        failure = First(failure, Replace(Path.Combine(dir, Latest), set.Text));
        failure = First(failure, Replace(Path.Combine(dir, $"frames-{stamp}.csv"), set.FramesCsv));
        failure = First(failure, Replace(Path.Combine(dir, $"seconds-{stamp}.csv"), set.SecondsCsv));

        return new SaveOutcome(report, failure);
    }

    private static string First(string earlier, string later) => earlier.Length > 0 ? earlier : later;

    /// <summary>"" when the file was written, else why not.</summary>
    private static string Replace(string path, string text)
    {
        // Its own name, so the Save button and a save on another thread never write the same temporary file.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";

        try
        {
            File.WriteAllText(temp, text);
            File.Move(temp, path, true);

            return "";
        }
        catch (Exception ex)
        {
            DeleteQuietly(temp);

            return ex.Message;
        }
    }

    /// <summary>A left-over temporary file is only clutter; the failure that left it is what gets reported.</summary>
    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
