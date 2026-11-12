namespace PerformanceAnalyzer.Core;

/// <summary>
/// When the report is next saved on its own while recording, so a crash or a hard close still leaves one. The count
/// starts when recording starts, and a new interval counts from the moment it is set.
/// </summary>
public sealed class SaveTimer
{
    private double _dueAt = double.NaN;
    private double _every;

    /// <summary>True once per interval while recording. An interval of 0 or less never saves.</summary>
    public bool Due(double time, bool isRecording, double everySeconds)
    {
        if (!isRecording || everySeconds <= 0)
        {
            _dueAt = double.NaN;

            return false;
        }

        if (double.IsNaN(_dueAt) || everySeconds != _every)
        {
            _every = everySeconds;
            _dueAt = time + everySeconds;

            return false;
        }

        if (time < _dueAt) return false;

        _dueAt = time + everySeconds;

        return true;
    }
}
