# Changelog

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
