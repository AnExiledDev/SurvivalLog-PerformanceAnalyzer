# Performance Analyzer

Find out which mod is slowing Survival Log down.

This is a development tool for testing mods. It is not released and has no Nexus page.

## How to use it

1. Press `Insert` in game to open the panel.
2. Press **Start** and play the part of the game that feels slow for a minute.
3. Read the **Overview** tab: the verdict at the top says whether mods are a real part of the frame time, and which one costs the most.
4. Press **Copy report** to put a text report on the clipboard, or **Save** to write it to a file.

`Esc` or the close button closes the panel. Recording keeps going while it is closed.

## What it measures

- **Frame time**: average, p50, p95, p99 and the worst frame, with a graph of the latest frames.
- **Each mod's own time**: the time spent inside every other mod's Harmony patches and Update methods, per frame and per second, with the memory each one allocates. A patch shared by two mods is split evenly between them.
- **Hook cost**: the extra time a hooked game method takes just because it is hooked, and the game's own time inside hooked methods.
- **Stutters (hitches)**: every frame over 50 ms is kept with the methods and mods that ran longest in it, the scene, and whether a garbage collection ran. Each one gets a likely cause.
- **Methods**: every timed patch and Update method, sortable and with a filter.
- **Deep profile**: the Deep button on a mod's row also times every method in that mod, to find the slow one inside it.
- **Menus**: the Web tab times each mod's scripts inside the game's web UI, and counts the scripts each mod sends, the page elements it owns, and the timers, intervals, listeners and observers it leaves running. A mod with a lot of these slows the menus even when its own code looks fast.
- **Its own cost**: the analyzer measures what one timing costs and reports its own share, so you can see how much it adds.

The frame file is a CSV with one line per frame and a column per mod, for a spreadsheet.

## Buttons

| Button | What it does |
| --- | --- |
| Start / Stop | Start or pause recording. |
| Reset | Forget everything recorded so far. |
| Unhook | Stop recording and remove every timing hook, to compare the game with and without the analyzer. |
| Copy report | Put the text report on the clipboard. |
| Save | Write `report-<date>.txt`, `latest-report.txt` and `frames-<date>.csv` to `BepInEx\performance-analyzer`. |
| Overlay | Show frame time and the slowest mod in the top-right corner, even with the panel closed. |
| Deep (Mods tab) | Time every method of that mod. One mod at a time. |

## What it can't see

- Time spent in the game's own code outside hooked methods is shown as "rest of game", not split further.
- Work a mod does on another thread is counted, but not timed.
- Timers and listeners a mod set up in the menus before recording started are not counted for that mod.

## Requirements

- BepInEx 6 (IL2CPP).
- Exiled's Toolkit is optional. With it, settings changed on the Mod Manager's Mods screen apply at once.

Settings are in [CONFIG.md](CONFIG.md).
