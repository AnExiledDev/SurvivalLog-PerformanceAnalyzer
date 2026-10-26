using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PerformanceAnalyzer.Core;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// With remote debugging on, asks the browser for its own counters once a second over the DevTools socket: style
/// recalcs, layouts, script and task time. Runs on a background task and only while recording; the main thread takes
/// the readings with <see cref="TryTake"/>.
/// </summary>
internal sealed class BrowserMetrics
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);

    private readonly int _port;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<WebReading> _readings = new();
    private readonly WebMetrics _metrics = new();
    private string _lastProblem = "";
    private bool _hasConnected;
    private volatile bool _isWanted;
    private int _isRunning;

    public BrowserMetrics(int port, Action<string> log)
    {
        _port = port;
        _log = log;
    }

    /// <summary>Starts the reader the first time it is wanted; it idles while not.</summary>
    public bool IsWanted
    {
        get => _isWanted;
        set
        {
            _isWanted = value;

            if (value && Interlocked.Exchange(ref _isRunning, 1) == 0) Task.Run(Run);
        }
    }

    public bool TryTake(out WebReading reading) => _readings.TryDequeue(out reading);

    private async Task Run()
    {
        while (true)
        {
            if (!_isWanted)
            {
                await Task.Delay(Every).ConfigureAwait(false);

                continue;
            }

            try
            {
                await ReadWhileWanted().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Problem("could not read the browser's counters, trying again in 10 seconds: " + ex.Message);
                await Task.Delay(RetryAfter).ConfigureAwait(false);
            }

            _metrics.Forget();
        }
    }

    private async Task ReadWhileWanted()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var list = await http.GetStringAsync($"http://127.0.0.1:{_port}/json").ConfigureAwait(false);
        var target = WebMetrics.PickTarget(list) ?? throw new InvalidOperationException("the browser lists no page to watch");

        using var socket = new ClientWebSocket();
        using var connecting = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await socket.ConnectAsync(new Uri(target), connecting.Token).ConfigureAwait(false);
        await Ask(socket, 1, "Performance.enable").ConfigureAwait(false);

        if (!_hasConnected || _lastProblem.Length > 0)
        {
            _log("reading the browser's style, layout and script counters each second");
            _hasConnected = true;
            _lastProblem = "";
        }

        for (var id = 2; _isWanted && socket.State == WebSocketState.Open; id++)
        {
            var answer = await Ask(socket, id, "Performance.getMetrics").ConfigureAwait(false);
            var reading = _metrics.Read(answer);

            if (reading.HasValue) _readings.Enqueue(reading.Value);

            await Task.Delay(Every).ConfigureAwait(false);
        }

        if (socket.State == WebSocketState.Open) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Sends one command and returns its answer, skipping any events the browser sends in between.</summary>
    private static async Task<string> Ask(ClientWebSocket socket, int id, string method)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = Encoding.UTF8.GetBytes("{\"id\":" + id + ",\"method\":\"" + method + "\"}");

        await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);

        var marker = "\"id\":" + id + ",";
        var buffer = new byte[16 * 1024];

        while (true)
        {
            var text = new StringBuilder();
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("the browser closed the connection");

                text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            var message = text.ToString();

            if (message.Contains(marker, StringComparison.Ordinal) || message.Contains("\"id\":" + id + "}", StringComparison.Ordinal)) return message;
        }
    }

    /// <summary>Logs a problem once until it changes, so a closed port does not fill the log every 10 seconds.</summary>
    private void Problem(string text)
    {
        if (text == _lastProblem) return;

        _lastProblem = text;
        _log(text);
    }
}
