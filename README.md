# Performance Analyzer

Find out which mod is slowing Survival Log down.

This is a development tool for testing mods. It is not released and has no Nexus page.

## How to use it

1. Press `Insert` in game to open the panel.
2. Press **Start** and play the part of the game that feels slow for a minute.
3. Read the **Overview** tab: the verdict at the top says whether mods are a real part of the frame time, and which one costs the most. The **Game** tab shows the game's own frame rate, world updates, speed and memory.
4. Press **Copy report** to put a text report on the clipboard, or **Save** to write it to a file.

`Esc` or the close button closes the panel. Recording keeps going while it is closed.

## What it measures

- **Frame time**: average, p50, p95, p99 and the worst frame, with a graph of the latest frames.
- **Each mod's own time**: the time spent inside every other mod's Harmony patches and Update methods, per frame and per second, with the memory each one allocates. A patch shared by two mods is split evenly between them.
- **Hook cost**: the extra time a hooked game method takes just because it is hooked, and the game's own time inside hooked methods.
- **Stutters (hitches)**: every frame over 50 ms is kept with the methods and mods that ran longest in it, the scene, and whether a garbage collection ran. Each one gets a likely cause.
- **Methods**: every timed patch and Update method, sortable and with a filter.
- **Deep profile**: the Deep button on a mod's row also times every method in that mod, to find the slow one inside it.
- **Menus**: the Web tab times each mod's scripts inside the game's web UI, and counts the scripts each mod sends, the page elements it owns, and the timers, intervals, listeners and observers it leaves running. A mod with a lot of these slows the menus even when its own code looks fast. This covers the main page and every game page inside it. Long tasks show how much of their time no mod's script explains, slow messages from a page to the game are listed with what they asked for, and each game page gets its element and listener count. Every click and key press is timed until the page draws again and until the fades it started have finished. The Trace 10 s button, or the End key with the panel closed, records the browser for 10 seconds and saves what ran, which mod sent it, the frames drawn and the garbage collections, and follows the frames from the page to the game screen to show where any are lost.
- **The game itself**: frames per second, world updates per second, physics steps per second, the game speed and how many in-game seconds pass per real second. The game has no fixed tick: it updates its world once per frame, so world updates follow the frame rate. When a frame runs longer than the engine allows, the game itself slows down; the analyzer shows how much game time was lost that way.
- **Memory**: the game's script memory, the mods' memory, what the engine has allocated and reserved, textures and the whole process, read once a second, with the peak and the growth per minute. A reading the game build doesn't give shows as not available.
- **Scenes**: frame rate and frame time spread for each scene since the last reset.
- **Its own cost**: the analyzer measures what one timing costs and reports its own share, so you can see how much it adds.

The frame file is a CSV with one line per frame and a column per mod, for a spreadsheet. The seconds file has one line per second of the last hour: frame rate, world updates, game speed, the scene, the game page that was open, and memory. With `RemoteDebugging` on it also has the browser's style, layout and script time for each second.

## Buttons

| Button | What it does |
| --- | --- |
| Start / Stop | Start or pause recording. |
| Reset | Forget everything recorded so far. |
| Unhook | Stop recording and remove every timing hook, to compare the game with and without the analyzer. |
| Copy report | Put the text report on the clipboard. |
| Save | Write `report-<date>.txt`, `latest-report.txt`, `frames-<date>.csv` and `seconds-<date>.csv` to `BepInEx\performance-analyzer`. |
| Overlay | Show frame time, the slowest mod, world updates, game speed and memory in the top-right corner, even with the panel closed. |
| Deep (Mods tab) | Time every method of that mod. One mod at a time. |

## For modders: markers

The analyzer times your patches and Update methods on its own. To see which part of a slow method is the slow part, mark sections of your code:

1. Copy [markers/PerfMarkers.cs](markers/PerfMarkers.cs) into your mod. It needs no reference to the analyzer.
2. Make one marker per section, in a static field, and wrap the code:

```csharp
private static readonly PerfMarker CropScan = new("Crop scan");

using (CropScan.Time())
{
    // the code to time
}
```

Each marker shows as its own row, kind `marker`, in the Methods tab, the report and the list of what ran in each stutter. Its time is taken out of the method around it. Without the analyzer installed a marker does nothing.

## What it can't see

- Time spent in the game's own code outside hooked methods is shown as "rest of game", not split further.
- Work a mod does on another thread is counted, but not timed.
- Timers and listeners a mod set up in the menus before recording started are not counted for that mod.

## Requirements

- BepInEx 6 (IL2CPP).
- Exiled's Toolkit is optional. With it, settings changed on the Mod Manager's Mods screen apply at once. With 0.10.0 or newer, its UI size setting also sizes the panel, the corner overlay and the notices. With 0.11.0 or newer, Performance Analyzer's own `UiScale` under Interface sets a size for this mod alone; at 0, the default, it follows the Toolkit's.

Settings are in [CONFIG.md](CONFIG.md).
