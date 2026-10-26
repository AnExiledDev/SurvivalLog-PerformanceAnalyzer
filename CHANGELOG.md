# Changelog

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
