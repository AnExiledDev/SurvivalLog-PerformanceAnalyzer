using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PerformanceAnalyzer.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace PerformanceAnalyzer.Adapters;

/// <summary>
/// Runs beside each trace for the same few seconds and times what the browser's trace cannot see: the game's own
/// side of the web UI. Every 100 ms it pings the page; every frame it notes the mouse and asks the graphics card for
/// one column of the game's copy of the page, where the probe paints a frame counter in the corner. When it ends the
/// page sends the times of the frames it drew and the moves it got, and the summary goes to latency-{stamp}.txt.
/// </summary>
internal sealed class HopProbe
{
    private const float PingSeconds = 0.1f;

    /// <summary>How long to wait for the page's lists and the last reads once the check ends.</summary>
    private const float WaitSeconds = 3f;

    private const int MaxPending = 16;

    /// <summary>The counter is 4 px at the page's top-left corner; this column and these rows sit inside it at any zoom.</summary>
    private const int Column = 1;
    private const int Edge = 1;

    private readonly WebPage _page;
    private readonly string _dir;
    private readonly double _ticksPerMs;
    private readonly Action<string> _log;
    private readonly HopRecord _record = new();
    private readonly List<(AsyncGPUReadbackRequest Request, long At, int Height)> _pending = new();
    private readonly byte[] _top = new byte[4];
    private readonly byte[] _bottom = new byte[4];
    private State _state;
    private float _endAt;
    private float _nextPingAt;
    private int _nextPing;
    private Vector3 _lastMouse;
    private bool _hasMouse;
    private bool _canRead = true;
    private string _stamp = "";

    public HopProbe(WebPage page, string dir, double ticksPerMs, Action<string> log)
    {
        _page = page;
        _dir = dir;
        _ticksPerMs = ticksPerMs;
        _log = log;
    }

    private enum State
    {
        Idle,
        Running,
        Waiting,
    }

    /// <summary>The summary of the last finished check, for the report; empty until one finishes.</summary>
    public string LastText { get; private set; } = "";

    public bool Start(float now, float seconds)
    {
        if (_state != State.Idle) return false;

        _record.Clear();
        _pending.Clear();
        _hasMouse = false;
        _nextPing = 0;
        _nextPingAt = now;
        _endAt = now + seconds;
        _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);

        if (!_page.Hop(true))
        {
            _log("latency check skipped: the web UI is not there");

            return false;
        }

        _state = State.Running;
        _log($"timing the game's side of the web UI for {seconds:0} s");

        return true;
    }

    /// <summary>Once a frame, inside the analyzer's own probe.</summary>
    public void Tick(float now)
    {
        if (_state == State.Idle) return;

        var at = Stopwatch.GetTimestamp();

        Collect(at);

        if (_state == State.Running)
        {
            _record.GameFrame(at);
            NoteMouse(at);
            Read(at);

            if (now >= _nextPingAt)
            {
                _nextPingAt = now + PingSeconds;
                _record.Sent(_nextPing, Stopwatch.GetTimestamp());
                _page.Ping(_nextPing++);
            }

            if (now >= _endAt)
            {
                _page.Hop(false);
                _state = State.Waiting;
                _endAt = now + WaitSeconds;
            }

            return;
        }

        if ((_record.HasPageFrames && _record.HasPageMoves && _pending.Count == 0) || now >= _endAt) Finish();
    }

    public void Take(PageCommand command)
    {
        if (_state == State.Idle) return;

        switch (command)
        {
            case PageCommand.Pong pong:
                _record.Answered(pong.Id, pong.PageMs, pong.At);
                break;
            case PageCommand.HopFrames frames:
                _record.PageFrames(frames.Text);
                break;
            case PageCommand.HopMoves moves:
                _record.PageMoves(moves.Text);
                break;
        }
    }

    private void NoteMouse(long at)
    {
        var mouse = Input.mousePosition;

        if (_hasMouse && mouse != _lastMouse) _record.GameMove(at);

        _lastMouse = mouse;
        _hasMouse = true;
    }

    /// <summary>One 1 px column of the whole copy, so the counter is found whether the copy is upright or flipped.</summary>
    private void Read(long at)
    {
        if (!_canRead) return;

        if (_pending.Count >= MaxPending)
        {
            _record.ReadSkipped();

            return;
        }

        try
        {
            var texture = WebPage.Texture();

            if (texture == null || texture.width <= Column || texture.height <= 2 * Edge)
            {
                _record.ReadSkipped();

                return;
            }

            var height = texture.height;

            _pending.Add((AsyncGPUReadback.Request(texture, 0, Column, 1, 0, height, 0, 1, null), at, height));
        }
        catch (Exception ex)
        {
            _canRead = false;
            _log("could not read the web UI's texture back, so the check times messages and the mouse only: " + ex.Message);
        }
    }

    private void Collect(long now)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var (request, at, height) = _pending[i];

            try
            {
                if (!request.done) continue;

                _pending.RemoveAt(i);

                if (request.hasError)
                {
                    _record.ReadFailed();

                    continue;
                }

                var size = request.GetLayerDataSize();

                if (size != height * 4)
                {
                    _record.ReadFailed();

                    if (_record.ReadFailures == 1) _log($"the web UI's texture read back as {size} bytes for {height} rows, not 4 a pixel");

                    continue;
                }

                var data = request.GetDataRaw(0);

                Marshal.Copy(data + Edge * 4, _top, 0, 4);
                Marshal.Copy(data + (height - 1 - Edge) * 4, _bottom, 0, 4);
                _record.Read(at, _top, _bottom);
            }
            catch (Exception ex)
            {
                _pending.RemoveAt(i);
                _record.ReadFailed();

                if (_record.ReadFailures == 1) _log("reading the web UI's texture failed: " + ex.Message);
            }
        }
    }

    private void Finish()
    {
        _state = State.Idle;
        _pending.Clear();
        _page.Hop(false);

        try
        {
            LastText = HopLatency.Text(_record, _ticksPerMs, _stamp);
            Directory.CreateDirectory(_dir);

            var path = Path.Combine(_dir, $"latency-{_stamp}.txt");

            File.WriteAllText(path, LastText);
            _log("latency check saved to " + path);
        }
        catch (Exception ex)
        {
            _log("could not sum up the latency check: " + ex.Message);
        }
    }
}
