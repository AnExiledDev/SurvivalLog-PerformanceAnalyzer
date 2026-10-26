# Changelog

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
