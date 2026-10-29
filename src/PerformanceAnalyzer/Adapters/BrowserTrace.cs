using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PerformanceAnalyzer.Core;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// Records the web UI for a few seconds the way Chrome's Performance tab does, over the DevTools socket: a browser
/// trace (script, style, layout, paint, garbage collection, every event handled) and a CPU profile of the page's
/// scripts. Saves both, so they open in Chrome's developer tools, and a short text summary that names the mod behind
/// each busy script. Runs on a background task; the main thread takes the messages with <see cref="TryTakeMessage"/>.
/// </summary>
internal sealed class BrowserTrace
{
    public const int Seconds = 10;
    private const int HotScripts = 30;
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SlowAskTimeout = TimeSpan.FromSeconds(60);

    // What Chrome's Performance tab records, without screenshots, plus the browser's compositor (viz) and its frame
    // capture (gpu.capture): the game gets the menus as captured frames, so those show where frames are lost.
    private const string TraceConfig = "{\"transferMode\":\"ReturnAsStream\",\"traceConfig\":{\"recordMode\":\"recordAsMuchAsPossible\",\"includedCategories\":[" +
        "\"toplevel\",\"devtools.timeline\",\"disabled-by-default-devtools.timeline\",\"disabled-by-default-devtools.timeline.frame\",\"v8.execute\",\"blink.user_timing\",\"latencyInfo\",\"viz\",\"gpu.capture\"]}}";

    private readonly int _port;
    private readonly string _dir;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<string> _messages = new();
    private int _isRunning;
    private int _nextId;

    public BrowserTrace(int port, string dir, Action<string> log)
    {
        _port = port;
        _dir = dir;
        _log = log;
    }

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    /// <summary>The summary of the last finished trace, for the report; empty until one finishes.</summary>
    public string LastText { get; private set; } = "";

    public bool TryTakeMessage(out string message) => _messages.TryDequeue(out message!);

    /// <summary>Starts a trace unless one is running. The loaded mods' keys name the scripts in the summary.</summary>
    public bool Start(IReadOnlySet<string> known)
    {
        if (Interlocked.Exchange(ref _isRunning, 1) == 1) return false;

        Task.Run(async () =>
        {
            try
            {
                await Record(known).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Tell("The trace failed: " + ex.Message);
            }
            finally
            {
                Volatile.Write(ref _isRunning, 0);
            }
        });

        return true;
    }

    private async Task Record(IReadOnlySet<string> known)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var list = await http.GetStringAsync($"http://127.0.0.1:{_port}/json").ConfigureAwait(false);
        var target = WebMetrics.PickTarget(list) ?? throw new InvalidOperationException("the browser lists no page to trace");

        using var socket = new ClientWebSocket();
        using var connecting = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await socket.ConnectAsync(new Uri(target), connecting.Token).ConfigureAwait(false);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var stream = "";
        void Watch(string message)
        {
            if (stream.Length == 0 && message.Contains("\"Tracing.tracingComplete\"", StringComparison.Ordinal)) stream = Field(message, "params", "stream");
        }

        await Ask(socket, "Profiler.enable", "", Watch, AskTimeout).ConfigureAwait(false);
        await Ask(socket, "Profiler.setSamplingInterval", "{\"interval\":500}", Watch, AskTimeout).ConfigureAwait(false);
        await Ask(socket, "Profiler.start", "", Watch, AskTimeout).ConfigureAwait(false);
        await Ask(socket, "Tracing.start", TraceConfig, Watch, AskTimeout).ConfigureAwait(false);
        _log($"tracing the web UI for {Seconds} s");

        await Task.Delay(TimeSpan.FromSeconds(Seconds)).ConfigureAwait(false);

        await Ask(socket, "Tracing.end", "", Watch, AskTimeout).ConfigureAwait(false);

        var stopped = await Ask(socket, "Profiler.stop", "", Watch, SlowAskTimeout).ConfigureAwait(false);
        var profile = Raw(stopped, "result", "profile");

        await Ask(socket, "Profiler.disable", "", Watch, AskTimeout).ConfigureAwait(false);

        using (var waiting = new CancellationTokenSource(SlowAskTimeout))
        {
            while (stream.Length == 0) Watch(await Receive(socket, waiting.Token).ConfigureAwait(false));
        }

        Directory.CreateDirectory(_dir);

        var tracePath = Path.Combine(_dir, $"trace-{stamp}.json");
        await ReadStream(socket, stream, tracePath, Watch).ConfigureAwait(false);

        if (profile.Length > 0) File.WriteAllText(Path.Combine(_dir, $"trace-{stamp}.cpuprofile"), profile);

        var owners = profile.Length > 0 ? await ScriptOwners(socket, profile, known, Watch).ConfigureAwait(false) : new Dictionary<string, string>();

        if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);

        var text = $"Browser trace {stamp}, {Seconds} s. Open trace-{stamp}.json in Chrome's developer tools (Performance tab, Load profile) to see it on a timeline.\n" +
            Summarize(tracePath, profile, owners);
        var textPath = Path.Combine(_dir, $"trace-{stamp}.txt");

        File.WriteAllText(textPath, text);
        LastText = text;
        _log("trace saved to " + textPath);
        Tell($"Trace saved to BepInEx/performance-analyzer/trace-{stamp}.txt. The report shows it too.");
    }

    /// <summary>The trace comes back as a stream to read in pieces; each piece goes to the file as it arrives.</summary>
    private async Task ReadStream(ClientWebSocket socket, string handle, string path, Action<string> watch)
    {
        using (var file = new StreamWriter(path, false, new UTF8Encoding(false)))
        {
            while (true)
            {
                var answer = await Ask(socket, "IO.read", "{\"handle\":" + JsText.Literal(handle) + ",\"size\":1048576}", watch, SlowAskTimeout).ConfigureAwait(false);

                using var doc = JsonDocument.Parse(answer);
                var result = doc.RootElement.GetProperty("result");
                var data = result.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";

                if (result.TryGetProperty("base64Encoded", out var b) && b.ValueKind == JsonValueKind.True) data = Encoding.UTF8.GetString(Convert.FromBase64String(data));

                file.Write(data);

                if (!result.TryGetProperty("eof", out var eof) || eof.ValueKind == JsonValueKind.True || data.Length == 0) break;
            }
        }

        await Ask(socket, "IO.close", "{\"handle\":" + JsText.Literal(handle) + "}", watch, AskTimeout).ConfigureAwait(false);
    }

    private static string Summarize(string tracePath, string profile, IReadOnlyDictionary<string, string> owners)
    {
        using var file = File.OpenRead(tracePath);

        return TraceSummary.Text(file, profile, owners);
    }

    /// <summary>
    /// Reads the source of the busiest scripts to name their mod. The debugger is on only for this and told never to
    /// pause, so a stray debugger statement in a mod cannot stop the page.
    /// </summary>
    private async Task<Dictionary<string, string>> ScriptOwners(ClientWebSocket socket, string profile, IReadOnlySet<string> known, Action<string> watch)
    {
        var owners = new Dictionary<string, string>();

        try
        {
            await Ask(socket, "Debugger.enable", "", watch, SlowAskTimeout).ConfigureAwait(false);
            await Ask(socket, "Debugger.setSkipAllPauses", "{\"skip\":true}", watch, AskTimeout).ConfigureAwait(false);

            var missed = 0;

            foreach (var id in TraceSummary.HotScripts(profile, HotScripts))
            {
                // A script from a page that has since closed or reloaded is gone; the others can still be read.
                try
                {
                    var answer = await Ask(socket, "Debugger.getScriptSource", "{\"scriptId\":" + JsText.Literal(id) + "}", watch, AskTimeout).ConfigureAwait(false);
                    var source = Field(answer, "result", "scriptSource");

                    if (source.Length > 0) owners[id] = TraceSummary.OwnerOfSource(source, known);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && socket.State == WebSocketState.Open)
                {
                    missed++;
                }
            }

            if (missed > 0) _log($"{missed} traced scripts were gone before they could be read, so the profile counts them as unknown");
        }
        catch (Exception ex)
        {
            _log("could not read the traced scripts, so the profile names fewer mods: " + ex.Message);
        }
        finally
        {
            try { await Ask(socket, "Debugger.disable", "", watch, AskTimeout).ConfigureAwait(false); } catch (Exception) { /* the socket is gone; the debugger goes with it */ }
        }

        return owners;
    }

    /// <summary>Sends one command and returns its answer; every event that arrives first goes to watch.</summary>
    private async Task<string> Ask(ClientWebSocket socket, string method, string parameters, Action<string> watch, TimeSpan wait)
    {
        using var timeout = new CancellationTokenSource(wait);
        var id = Interlocked.Increment(ref _nextId);
        var request = "{\"id\":" + id + ",\"method\":\"" + method + "\"" + (parameters.Length > 0 ? ",\"params\":" + parameters : "") + "}";

        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(request)), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);

        while (true)
        {
            var message = await Receive(socket, timeout.Token).ConfigureAwait(false);

            if (!IsAnswer(message, id))
            {
                watch(message);

                continue;
            }

            if (message.Contains("\"error\"", StringComparison.Ordinal) && Field(message, "error", "message") is { Length: > 0 } error) throw new InvalidOperationException(method + ": " + error);

            return message;
        }
    }

    private static bool IsAnswer(string message, int id)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);

            return doc.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string> Receive(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var bytes = new MemoryStream();
        WebSocketReceiveResult result;

        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);

            if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("the browser closed the connection");

            bytes.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
    }

    private static string Field(string json, string outer, string inner)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.TryGetProperty(outer, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(inner, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string Raw(string json, string outer, string inner)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            return doc.RootElement.TryGetProperty(outer, out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(inner, out var v) ? v.GetRawText() : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private void Tell(string message)
    {
        _log(message);
        _messages.Enqueue(message);
    }
}
