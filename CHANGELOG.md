# Changelog

## 0.7.2

- The 10 second trace now also times the game's side of the menus, from inside the game: how long a message takes to reach the page and come back, which of the frames the page draws reach the game screen and how late, the longest the screen kept one frame, and how long a mouse move takes to reach the page. It saves these to latency-<time>.txt and adds them to the report. A small coloured dot shows in the top-left corner while it runs. This works without RemoteDebugging too.
- Made for Survival Log 1.1.19611.

## 0.7.1

- The 10 second trace now follows the menus' frames from the page to the game screen: frames the page made, frames the browser drew, frames it copied for the game, and frames that reached the game, each per second, with the stage where the most are lost and why the copy skipped any. This shows lag that comes after the page has drawn.
- Made for Survival Log 1.1.19611.

## 0.7.0

- New: `UiScale` under Interface sets the size of Performance Analyzer's own windows on their own, from 75% to 200%, whatever Exiled's Toolkit's UI size is. At 0, the default, they follow the Toolkit's. Needs Exiled's Toolkit 0.11.0 or newer.
- Made for Survival Log 1.1.19611.

## 0.6.2

- Fixed: the trace notices did not show with the panel closed.
- Fixed: one script the browser had already thrown away stopped the trace from naming the mods of all the others.

## 0.6.1

- New TraceKey setting, End by default: it starts the 10 second trace without opening the panel. The notices now show with the panel closed too.

## 0.6.0

- The Web tab and the report now time every click and key press in the menus: how long until the page drew again, and how long until the fades and transitions the click started had finished. The slowest clicks are listed with what was clicked, and the fades that ran longest are listed with the element they ran on.
- New Trace 10 s button in the panel (needs RemoteDebugging). It records the browser for 10 seconds and saves the trace, a CPU profile and a short summary in BepInEx/performance-analyzer: main thread time by kind, the busiest scripts and which mod sent them, frames drawn, garbage collections and long tasks. The report shows the last summary.
- The page probe now starts with the game, so timers, listeners and observers a mod leaves running are named even when they were made before recording started. Time is still counted only while recording.
- Exiled's Toolkit and its Mod Menu scripts now count under one Toolkit row, and a mod that names itself in more than one way on the page gets one row.
- Mods whose page code uses a plain name like __storagehub are now named, when that mod is loaded.

## 0.5.3

- Fixed: with RemoteDebugging on, nothing listened on the port, so the report said the browser's style, layout and script time were not read. The port now stays in the browser's launch arguments when the game or WebUI Doctor sets them.

## 0.5.2

- RemoteDebugging is now on by default, so a recording also has the menus' style, layout and script time. A config file that already has it off keeps it off.

## 0.5.1

- The panel, the corner overlay and the notices can be drawn bigger or smaller with the UI size setting of Exiled's Toolkit 0.10.0 or newer. A change shows at once.

## 0.5.0

- The Web tab now counts the timers, listeners and observers mods leave inside the game's own pages, not just the main page.
- Exiled's Toolkit gets its own row in the Web tab. Its scripts used to count as the game's.
- Long tasks in the menus now show how much of their time no mod's script explains, and which page was open.
- Every frame and every second in the CSV files names the game page that was open, and so does each hitch.
- With RemoteDebugging on, the analyzer reads the browser's own style, layout and script time each second. It shows in the Web tab, the report and the seconds file.
- A slow message from a page to the game is listed with what it asked for, so a hitch with no mod to blame can still point at a page.
- Each method shows its single slowest call with the frame, scene and page it was in, and how long its first call took. The report lists the 15 slowest calls of the recording.
- The Web tab lists the elements and listeners of each game page.
- The analyzer allocates less memory with the panel open. The Methods and Hitches tabs refresh once a second instead of twice.

## 0.4.0

- Stutter makers: a short list of the mods that made frames slow on their own, with how often, their typical and worst time, and the method that did it. On the Overview, the Mods tab and in the report.
- Hitches are grouped by cause and slowest method ("Mod A, PageTick postfix: 57 hitches, typical 40 ms, worst 114"). The report lists the 10 worst hitches instead of the latest 15.
- Each hitch now shows the analyzer's own time, so its parts add up to the frame.
- Every mod gets a rating: fine, watch or heavy. Mods that never took measurable time are folded into one line.
- The verdict names up to three stutter makers instead of one.
- Mod methods and the game's own code inside hooks are listed apart (Methods tab buttons, two report tables). Allocations read as KB/s or MB/s.
- The game clock section of the report is written as plain sentences.

## 0.3.0

- Markers: a mod can time a section of its own code. Copy `markers/PerfMarkers.cs` into the mod and wrap the code in `using (marker.Time())`. Each section shows as its own row in the Methods tab, the report and the stutter list, and does nothing for players without the analyzer.

## 0.2.1

- The panel no longer stutters the game. With the panel or overlay open it took up to 110 ms every half second; it now takes about 1 ms.
- The stutter verdict counts every stutter of the session, not just the latest 200, and the analyzer's own stutters are listed apart from the mods.
- The Mods table counts the stutters each mod caused (40% of the frame or more on its own), not every stutter it happened to run in.
- The report says that KB/s and the mods' garbage collections are the mods' own runtime, not the game's.

## 0.2.0

- New Game tab: frames per second, how often the game world updates, physics steps, the game speed and how fast the in-game clock runs.
- Shows game time lost to slow frames. When one frame takes too long, the game itself slows down, not just the picture.
- Memory: the game's script memory, the mods' memory, the engine's memory, textures and the whole process, with the peak and how fast each one grows. Readings the game build doesn't give show as not available.
- A table per scene: frame rate, average, median, 95th and 99th frame time, the worst frame and stutters.
- The report gains game clock, memory and scene sections. Save also writes `seconds-<date>.csv`, one line per second of the last hour. The frames file gains the game speed and physics columns.
- The overlay shows world updates, game speed and memory.

## 0.1.0

- First version, for testing mods. Not released.
- Press `Insert` to open the panel, then Start to record. It shows how long each mod takes every frame, which methods are slowest, and what ran in each stutter.
- Copy report puts a plain text report on the clipboard. Save writes it, plus every recorded frame as a spreadsheet file, to `BepInEx\performance-analyzer`.
- The Web tab shows how much each mod slows the menus: script time, scripts sent, page elements, timers and listeners.
