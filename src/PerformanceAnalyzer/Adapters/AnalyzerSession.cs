using System;
using System.Diagnostics;
using System.IO;
using PerformanceAnalyzer.Core;
using UnityEngine;

namespace PerformanceAnalyzer.Adapters;

/// <summary>Settings the session reads every frame; the plugin replaces them when the config changes.</summary>
internal sealed record SessionSettings(
    KeyCode Key,
    float AutoStartSeconds,
    bool SaveOnQuit,
    ProbeOptions Probes,
    bool WebProbe,
    bool ScriptTraffic,
    bool TagScripts);

/// <summary>
/// One frame of the analyzer: closes the recorder's frame, reads the key and the panel's messages, adds probes a few
/// at a time, and pushes the panel a fresh snapshot twice a second while it or the overlay shows.
/// </summary>
internal sealed class AnalyzerSession
{
    private const float PushSeconds = 0.5f;

    private readonly Recorder _recorder;
    private readonly PluginIndex _plugins;
    private readonly Instrumenter _instrumenter;
    private readonly FrameSource _frames;
    private readonly WebPage _page;
    private readonly ScriptTraffic _traffic;
    private readonly string _reportDir;
    private readonly string _version;
    private readonly Action<bool> _saveOverlay;
    private readonly Action<string> _log;
    private readonly int _selfProbe;
    private bool _hasReadPlugins;
    private bool _hasCalibrated;
    private bool _hasAutoStarted;
    private bool _isOpen;
    private PanelTab _tab = PanelTab.Overview;
    private float _nextPushAt;
    private string _webJson = "";

    public AnalyzerSession(Recorder recorder, PluginIndex plugins, Instrumenter instrumenter, FrameSource frames, WebPage page, ScriptTraffic traffic, string reportDir, string version, Action<bool> saveOverlay, Action<string> log)
    {
        _recorder = recorder;
        _plugins = plugins;
        _instrumenter = instrumenter;
        _frames = frames;
        _page = page;
        _traffic = traffic;
        _reportDir = reportDir;
        _version = version;
        _saveOverlay = saveOverlay;
        _log = log;
        _selfProbe = recorder.AddProbe(new ProbeInfo("Performance Analyzer (panel, probes, reports)", ProbeKind.Analyzer, "", new[] { Recorder.AnalyzerMod }));
    }

    public SessionSettings Settings { get; set; } = new(KeyCode.Insert, 0, true, new ProbeOptions(true, true, true, Array.Empty<string>()), true, true, true);

    public bool Overlay { get; set; }

    public void Update()
    {
        var now = Stopwatch.GetTimestamp();

        // The frame boundary: everything since the last call is one frame.
        if (_recorder.IsRecording) _recorder.EndFrame(now, _frames.Next(Time.unscaledTime));

        var token = Probes.Begin(_selfProbe);

        try
        {
            Tick();
        }
        finally
        {
            Probes.End(token);
        }
    }

    /// <summary>Saves a report on the way out, so a session that ended in a crash-free quit is not lost.</summary>
    public void Quit()
    {
        if (Settings.SaveOnQuit && _recorder.FrameCount > 0) Save(false);
    }

    private void Tick()
    {
        var time = Time.unscaledTime;

        if (!_hasReadPlugins)
        {
            // By the first frame every plugin has loaded.
            _hasReadPlugins = true;
            _plugins.ReadPlugins();
            _log($"{_plugins.Loaded.Count} plugins loaded");
        }

        // After the plugins are read, so a marker is charged to its plugin by name and not to its DLL's file name.
        var markers = Markers.Registry.Drain(_recorder, _plugins.ModOf);

        if (markers > 0) _log($"{markers} new markers, {Markers.Registry.Count} in all");

        _page.WantsPanel = _isOpen || Overlay;
        _page.KeepInstalled(time);

        if (Settings.Key != KeyCode.None && Input.GetKeyDown(Settings.Key)) Toggle();

        while (PageMessages.TryTake(out var command)) Handle(command);

        if (!_hasAutoStarted && Settings.AutoStartSeconds > 0 && time >= Settings.AutoStartSeconds)
        {
            _hasAutoStarted = true;
            _log($"recording started on its own after {Settings.AutoStartSeconds:0} seconds");
            Start();
        }

        _instrumenter.Work(time);

        if (_recorder.IsRecording) _frames.SampleMemory(time);

        if ((_isOpen || Overlay) && time >= _nextPushAt)
        {
            _nextPushAt = time + PushSeconds;
            Push();
        }
    }

    private void Toggle()
    {
        if (_isOpen)
        {
            Close();

            return;
        }

        if (!_page.Open(State())) return;

        _isOpen = true;
        InputGate.PanelOpened();
        Push();
    }

    private void Close()
    {
        _isOpen = false;
        _page.Close();
        InputGate.PanelClosed();
    }

    private string State() =>
        "{\"key\":" + JsText.Literal(Settings.Key == KeyCode.None ? "" : Settings.Key.ToString()) +
        ",\"version\":" + JsText.Literal(_version) +
        ",\"tab\":" + JsText.Literal(Tabs.Key(_tab)) +
        ",\"overlay\":" + (Overlay ? "true" : "false") + "}";

    /// <summary>With the panel closed only the overlay draws, and the Report tab's snapshot is the lightest one.</summary>
    private void Push() => _page.Update(Snapshot.Json(_recorder, new SnapshotInput(Stopwatch.GetTimestamp(), _isOpen ? _tab : PanelTab.Report, Overlay, _instrumenter.Status, _traffic.Rows(), _frames.HasEngineTimings, _version)));

    private void Handle(PageCommand command)
    {
        switch (command)
        {
            case PageCommand.Close:
                if (_isOpen) Close();
                break;
            case PageCommand.Ready:
                _log("panel script ready");
                // A reload loses the panel; open it again where it was.
                if (_isOpen && _page.Open(State())) Push();
                break;
            case PageCommand.NoScript:
                _page.InstallPanel();
                break;
            case PageCommand.NoProbe:
                _page.InstallProbe();
                _page.ProbeRecording(_recorder.IsRecording, Settings.TagScripts);
                break;
            case PageCommand.Start:
                Start();
                break;
            case PageCommand.Stop:
                Stop();
                break;
            case PageCommand.Reset:
                Reset();
                break;
            case PageCommand.Unhook:
                Stop();
                _instrumenter.Deactivate();
                _page.Toast("Probes removed. Start adds them again.");
                break;
            case PageCommand.CopyReport:
                _page.Toast(Machine.Copy(ReportText(), _log) ? "Report copied to the clipboard." : "Could not copy. Use Save report instead.");
                break;
            case PageCommand.SaveReport:
                Save(true);
                break;
            case PageCommand.ShowTab tab:
                _tab = tab.Tab;
                if (_tab == PanelTab.Report) _page.ShowReport(ReportText());
                Push();
                break;
            case PageCommand.Deep deep:
                if (!_instrumenter.IsActive) _instrumenter.Activate(Settings.Probes, Time.unscaledTime);
                _instrumenter.SetDeep(deep.ModKey);
                Push();
                break;
            case PageCommand.Overlay overlay:
                Overlay = overlay.IsOn;
                _saveOverlay(overlay.IsOn);
                break;
            case PageCommand.Web web:
                _webJson = web.Json;
                break;
            case PageCommand.Unknown unknown:
                _log("panel: " + unknown.Text);
                break;
        }
    }

    private void Start()
    {
        if (_recorder.IsRecording) return;

        if (!_hasCalibrated)
        {
            _hasCalibrated = true;
            _recorder.OverheadTicksPerCall = _instrumenter.Calibrate();
            _log($"one probe costs about {_recorder.OverheadTicksPerCall / _recorder.TicksPerMs * 1_000_000:0} ns");
        }

        _instrumenter.Activate(Settings.Probes, Time.unscaledTime);
        _recorder.Start(Stopwatch.GetTimestamp());
        ScriptCounter.IsCounting = Settings.ScriptTraffic;
        ScriptCounter.IsTagging = Settings.ScriptTraffic && Settings.TagScripts;

        if (Settings.WebProbe)
        {
            _page.WantsProbe = true;
            _page.InstallProbe();
            _page.ProbeRecording(true, Settings.TagScripts);
        }

        _log("recording started");
    }

    private void Stop()
    {
        if (!_recorder.IsRecording) return;

        _recorder.Stop(Stopwatch.GetTimestamp());
        ScriptCounter.IsCounting = false;
        ScriptCounter.IsTagging = false;
        _page.ProbeRecording(false, false);
        _log($"recording stopped after {_recorder.FrameCount} frames, {_recorder.HitchCount} hitches");
    }

    private void Reset()
    {
        _recorder.Reset(Stopwatch.GetTimestamp());
        _traffic.Clear();
        _webJson = "";
        _page.ProbeReset();
        _log("numbers cleared");
    }

    private string ReportText() => Report.Text(_recorder, new ReportInput(Stopwatch.GetTimestamp(), DateTime.Now, _version, Application.version, Machine.Describe(), _instrumenter.Status, _traffic.Rows(), _webJson, _plugins.Loaded));

    private void Save(bool tellPanel)
    {
        try
        {
            Directory.CreateDirectory(_reportDir);

            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var text = ReportText();
            var report = Path.Combine(_reportDir, $"report-{stamp}.txt");

            File.WriteAllText(report, text);
            File.WriteAllText(Path.Combine(_reportDir, "latest-report.txt"), text);
            File.WriteAllText(Path.Combine(_reportDir, $"frames-{stamp}.csv"), Report.FramesCsv(_recorder));
            File.WriteAllText(Path.Combine(_reportDir, $"seconds-{stamp}.csv"), Report.SecondsCsv(_recorder));
            _log("report saved to " + report);

            if (tellPanel) _page.Toast("Saved to BepInEx/performance-analyzer/report-" + stamp + ".txt");
        }
        catch (Exception ex)
        {
            _log("could not save the report: " + ex.Message);

            if (tellPanel) _page.Toast("Could not save the report: " + ex.Message);
        }
    }
}
