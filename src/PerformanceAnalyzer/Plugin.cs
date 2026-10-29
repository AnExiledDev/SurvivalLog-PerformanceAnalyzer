using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using PerformanceAnalyzer.Adapters;
using PerformanceAnalyzer.Core;
using UnityEngine;

namespace PerformanceAnalyzer;

[BepInPlugin(Guid, "Performance Analyzer", Version)]
[BepInDependency(OptionalLive.ToolkitGuid, BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BasePlugin
{
    private const string Guid = "com.dev.survivallog.perfanalyzer";
    private const string Version = "0.7.3";

    public override void Load()
    {
        if (!OptionalLive.IsToolkitInstalled) Log.LogInfo(OptionalLive.MissingNote);

        OptionalUiSize.Bind(Config);

        var panelKey = Config.Bind("Controls", "PanelKey", KeyCode.Insert,
            "Key that opens and closes the Performance Analyzer panel, Insert by default. None turns the key off.\n" +
            "打开和关闭性能分析器面板的按键，默认 Insert。设为 None 则停用该按键。");
        var traceKey = Config.Bind("Controls", "TraceKey", KeyCode.End,
            "Key that records the menus for 10 seconds with the browser's own tracing, the same as the Trace 10 s button, without opening the panel. Needs [Web] RemoteDebugging. End by default. None turns the key off.\n" +
            "无需打开面板即可用浏览器自带的追踪录制菜单 10 秒的按键，与「Trace 10 s」按钮相同。需要开启 [Web] RemoteDebugging。默认 End。设为 None 则停用该按键。");
        var autoStart = Config.Bind("Recording", "AutoStartSeconds", 0f,
            "Start recording on its own this many seconds after the game starts, to catch slow loading. 0 waits for the Start button.\n" +
            "游戏启动多少秒后自动开始记录，用于捕捉加载时的卡顿。0 表示等待点击「开始」。");
        var hitchMs = Config.Bind("Recording", "HitchMs", 50f,
            "A frame that takes longer than this many milliseconds is recorded as a hitch, with what ran in it. 50 ms is a stutter most players notice.\n" +
            "耗时超过这么多毫秒的帧会被记为卡顿，并记下其中运行的内容。50 毫秒是大多数玩家能察觉的卡顿。");
        var windowFrames = Config.Bind("Recording", "WindowFrames", 7200,
            "How many of the latest frames the graphs, percentiles and frame file keep. 7200 is about a minute at 120 FPS. Totals per mod and method cover the whole recording.\n" +
            "图表、百分位与帧数据文件保留最近多少帧。7200 帧在 120 FPS 下约为一分钟。各模组与方法的总计覆盖整个记录过程。");
        var saveOnQuit = Config.Bind("Recording", "SaveOnQuit", true,
            "Save a report to BepInEx/performance-analyzer when the game closes, if anything was recorded.\n" +
            "游戏关闭时，如有记录内容，则把报告保存到 BepInEx/performance-analyzer。");
        var patches = Config.Bind("Probes", "Patches", true,
            "Time every other mod's Harmony prefixes, postfixes and finalizers, and the hooked methods they sit on.\n" +
            "统计其他每个模组的 Harmony 前缀、后缀与终结补丁，以及它们所钩住的方法的耗时。");
        var tickers = Config.Bind("Probes", "Tickers", true,
            "Time the Update, LateUpdate, FixedUpdate and OnGUI methods of every other mod.\n" +
            "统计其他每个模组的 Update、LateUpdate、FixedUpdate 与 OnGUI 方法的耗时。");
        var gameTime = Config.Bind("Probes", "GameTime", true,
            "Inside a hooked game method, time the game's own code apart from the mods' patches.\n" +
            "在被钩住的游戏方法内，把游戏自身代码的耗时与模组补丁分开统计。");
        var exclude = Config.Bind("Probes", "Exclude", "",
            "Mods to leave untimed, by plugin id or short key, separated by commas. Use it when a mod breaks while timed.\n" +
            "不统计的模组，填写插件 ID 或简称，用逗号分隔。某个模组在被统计时出错时使用。");
        var webProbe = Config.Bind("Web", "Probe", true,
            "Watch the menus from inside the page while recording: page frame rate, long tasks, memory, page elements and timers per mod, and how long each click takes to show its answer and finish its fades.\n" +
            "记录时从页面内部监测菜单：页面帧率、长任务、内存，各模组的页面元素与计时器，以及每次点击多久显示结果、多久完成淡入淡出。");
        var scriptTraffic = Config.Bind("Web", "ScriptTraffic", true,
            "Count the scripts each mod sends to the menus while recording, and their size.\n" +
            "记录时统计各模组发送到菜单的脚本数量与大小。");
        var tagScripts = Config.Bind("Web", "TagScripts", true,
            "Mark each mod's scripts from the start of the game, so the page can tell which mod made each timer and listener and time each mod's scripts while recording. Turn it off if a menu mod misbehaves.\n" +
            "从游戏开始就为各模组的脚本加上标记，以便页面分辨每个计时器与监听器来自哪个模组，并在记录时统计每个模组脚本的运行时间。如果某个菜单模组出现异常，请关闭此项。");
        var remoteDebugging = Config.Bind("Web", "RemoteDebugging", true,
            "Let Chrome's developer tools attach to the game's menus: open chrome://inspect in Chrome and add localhost and the port below. While recording, the analyzer also reads the menus' style, layout and script time each second for the report, and the Trace button records the menus for 10 seconds. Needs a game restart.\n" +
            "允许 Chrome 开发者工具连接到游戏菜单：在 Chrome 中打开 chrome://inspect，并添加 localhost 与下面的端口。记录时，分析器还会每秒读取菜单的样式、布局与脚本耗时并写入报告；「Trace 10 s」按钮可录制菜单 10 秒。需要重启游戏。");
        var remotePort = Config.Bind("Web", "RemoteDebuggingPort", 9222,
            "Port for Chrome's developer tools when RemoteDebugging is on. Needs a game restart.\n" +
            "开启 RemoteDebugging 时 Chrome 开发者工具使用的端口。需要重启游戏。");
        var overlay = Config.Bind("Overlay", "Show", false,
            "Show a small readout of frame time and the slowest mod in the top-right corner, even with the panel closed.\n" +
            "即使面板关闭，也在右上角显示帧耗时与最慢模组的小型读数。");

        var log = new AnalyzerLog(Log, Path.Combine(Paths.BepInExRootPath, "performance-analyzer.log"));
        var harmony = new Harmony(Guid);
        var recorder = new Recorder(Stopwatch.Frequency / 1000.0, Math.Max(16, windowFrames.Value), hitchMs.Value);
        var plugins = new PluginIndex(recorder);
        var traffic = new ScriptTraffic();
        var browser = remoteDebugging.Value ? new BrowserMetrics(remotePort.Value, log.Write) : null;
        var reportDir = Path.Combine(Paths.BepInExRootPath, "performance-analyzer");
        var trace = remoteDebugging.Value ? new BrowserTrace(remotePort.Value, reportDir, log.Write) : null;
        var session = new AnalyzerSession(recorder, plugins, new Instrumenter(recorder, plugins, Guid, log.Write), new FrameSource(log.Write), new WebPage(log.Write), traffic, browser, trace,
            reportDir, Version, isOn => overlay.Value = isOn, log.Write);

        log.Write($"===== Performance Analyzer {Version} started");
        Probes.Attach(recorder);
        Markers.Attach(Probes.Begin, Probes.End);
        InputGate.Apply(harmony, log.Write);
        PageMessages.Apply(harmony, log.Write);
        ScriptCounter.Apply(harmony, traffic, log.Write);
        SlowMessages.Apply(harmony, recorder, log.Write);

        if (remoteDebugging.Value)
        {
            WebPage.EnableRemoteDebugging(remotePort.Value, log.Write);
            DebugPortHook.Apply(harmony, remotePort.Value, log.Write);
        }

        OptionalLive.Watch(() => session.Settings = new SessionSettings(
            panelKey.Value,
            traceKey.Value,
            autoStart.Value,
            saveOnQuit.Value,
            new ProbeOptions(patches.Value, tickers.Value, gameTime.Value, exclude.Value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray()),
            webProbe.Value,
            scriptTraffic.Value,
            tagScripts.Value), panelKey, traceKey, autoStart, saveOnQuit, patches, tickers, gameTime, exclude, webProbe, scriptTraffic, tagScripts);
        OptionalLive.Watch(hitchMs, ms => recorder.HitchMs = Math.Max(1, ms));
        OptionalLive.Watch(windowFrames, frames => recorder.SetWindow(Math.Max(16, frames)));
        OptionalLive.Watch(overlay, isOn => session.Overlay = isOn);

        AnalyzerTicker.Session = session;
        AnalyzerTicker.Log = log.Write;
        AddComponent<AnalyzerTicker>();
    }
}
