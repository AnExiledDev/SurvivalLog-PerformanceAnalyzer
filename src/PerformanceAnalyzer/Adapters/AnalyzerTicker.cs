using System;
using System.Collections.Generic;
using UnityEngine;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// The Unity object that hands frames to the session. Each distinct failure is logged once, never rethrown into the
/// game; a failure that repeats every frame would otherwise flood the log.
/// </summary>
public sealed class AnalyzerTicker : MonoBehaviour
{
    private const int MaxReportedFailures = 20;

    internal static AnalyzerSession? Session;
    internal static Action<string> Log = _ => { };
    private static readonly HashSet<string> ReportedFailures = new();

    public AnalyzerTicker(IntPtr pointer) : base(pointer)
    {
    }

    private void Update() => Guard(() => Session?.Update());

    private void OnApplicationQuit() => Guard(() => Session?.Quit());

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (ReportedFailures.Count < MaxReportedFailures && ReportedFailures.Add(ex.GetType().FullName + ": " + ex.Message)) Log("update failed (each distinct failure reported once): " + ex);
        }
    }
}
