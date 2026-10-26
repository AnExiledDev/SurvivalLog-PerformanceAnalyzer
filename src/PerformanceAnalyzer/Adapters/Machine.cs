using System;
using UnityEngine;

namespace PerformanceAnalyzer.Adapters;

/// <summary>The machine and clipboard, through the engine.</summary>
internal static class Machine
{
    public static string Describe()
    {
        try
        {
            return $"{SystemInfo.operatingSystem}; {SystemInfo.processorType} ({SystemInfo.processorCount} threads); {SystemInfo.systemMemorySize} MB RAM; {SystemInfo.graphicsDeviceName} ({SystemInfo.graphicsMemorySize} MB); {Screen.width}x{Screen.height}";
        }
        catch (Exception)
        {
            return "";
        }
    }

    public static bool Copy(string text, Action<string> log)
    {
        try
        {
            GUIUtility.systemCopyBuffer = text;

            return true;
        }
        catch (Exception ex)
        {
            log("could not copy to the clipboard: " + ex.Message);

            return false;
        }
    }
}
