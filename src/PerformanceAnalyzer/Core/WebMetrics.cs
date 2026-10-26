using System;
using System.Collections.Generic;
using System.Text.Json;

namespace PerformanceAnalyzer.Core;

/// <summary>
/// One stretch of the page's own work from the browser's counters (Chrome DevTools Performance.getMetrics): how many
/// times it worked out styles and layout, and how long that, scripts and all tasks took.
/// </summary>
public readonly record struct WebReading(float StyleCount, float StyleMs, float LayoutCount, float LayoutMs, float ScriptMs, float TaskMs)
{
    /// <summary>Style, layout and script time: the page's work a mod can cause.</summary>
    public float BusyMs => StyleMs + LayoutMs + ScriptMs;

    public WebReading Plus(WebReading other) => new(StyleCount + other.StyleCount, StyleMs + other.StyleMs, LayoutCount + other.LayoutCount, LayoutMs + other.LayoutMs, ScriptMs + other.ScriptMs, TaskMs + other.TaskMs);
}

/// <summary>
/// Reads the browser's DevTools answers. The counters only ever go up, so a reading is the change since the last one;
/// a counter that went down (the page reloaded) starts over from that answer.
/// </summary>
public sealed class WebMetrics
{
    private Counters _last;
    private bool _hasLast;

    /// <summary>The page to watch from the browser's /json list: the game's Root.html, else its first page.</summary>
    public static string? PickTarget(string listJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(listJson);

            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            string? first = null;

            foreach (var target in doc.RootElement.EnumerateArray())
            {
                if (target.ValueKind != JsonValueKind.Object) continue;
                if (Text(target, "type") != "page") continue;

                var socket = Text(target, "webSocketDebuggerUrl");

                if (socket.Length == 0) continue;
                if (Text(target, "url").IndexOf("Root.html", StringComparison.OrdinalIgnoreCase) >= 0) return socket;

                first ??= socket;
            }

            return first;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The change since the last answer, or null for the first answer, a broken one, or one with no metrics.</summary>
    public WebReading? Read(string answerJson)
    {
        if (!TryParse(answerJson, out var now)) return null;

        var last = _last;
        var hadLast = _hasLast;

        _last = now;
        _hasLast = true;

        if (!hadLast || now.StyleCount < last.StyleCount || now.TaskSeconds < last.TaskSeconds) return null;

        return new WebReading(
            (float)(now.StyleCount - last.StyleCount),
            (float)((now.StyleSeconds - last.StyleSeconds) * 1000),
            (float)(now.LayoutCount - last.LayoutCount),
            (float)((now.LayoutSeconds - last.LayoutSeconds) * 1000),
            (float)((now.ScriptSeconds - last.ScriptSeconds) * 1000),
            (float)((now.TaskSeconds - last.TaskSeconds) * 1000));
    }

    public void Forget() => _hasLast = false;

    private static bool TryParse(string answerJson, out Counters counters)
    {
        counters = default;

        try
        {
            using var doc = JsonDocument.Parse(answerJson);

            if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("metrics", out var metrics) || metrics.ValueKind != JsonValueKind.Array) return false;

            var values = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (var metric in metrics.EnumerateArray())
            {
                if (metric.ValueKind != JsonValueKind.Object) continue;
                if (!metric.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Number) continue;

                values[Text(metric, "name")] = value.GetDouble();
            }

            if (!values.ContainsKey("TaskDuration")) return false;

            counters = new Counters(Value(values, "RecalcStyleCount"), Value(values, "RecalcStyleDuration"), Value(values, "LayoutCount"), Value(values, "LayoutDuration"), Value(values, "ScriptDuration"), Value(values, "TaskDuration"));

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static double Value(Dictionary<string, double> values, string name) => values.TryGetValue(name, out var value) ? value : 0;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private readonly record struct Counters(double StyleCount, double StyleSeconds, double LayoutCount, double LayoutSeconds, double ScriptSeconds, double TaskSeconds);
}
