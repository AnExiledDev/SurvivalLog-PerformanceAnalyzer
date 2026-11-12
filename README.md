# Performance Analyzer

Find out which mod is slowing Survival Log down.

This is a development tool for testing mods. It is not released and has no Nexus page.

## How to use it

1. Press `Insert` (the default key) in game to open the panel.
2. Press **Start** and play the part of the game that feels slow for a minute.
3. Read the **Overview** tab. The verdict at the top says how much of each frame the mods take, and which mod costs the most. The **Game** tab shows the game's frame rate, world updates, speed and memory.
4. Press **Copy report** to put a text report on the clipboard, or **Save report** to write it to a file.

While it records, the analyzer also saves the report every so often, and once more when you press Stop. `SaveEverySeconds` sets how often (60 seconds by default), and `0` turns it off. Each of these saves replaces the last one: `report-<date>.txt` (the date recording started), `latest-report.txt` and the two CSVs. If the game crashes or is closed hard, the last save is still there.

`Esc` or the close button closes the panel. Recording keeps going while it is closed.

## What it measures

- **Frame time**: average, p50, p95, p99 and the worst frame, with a graph of the latest frames.
- **Time per mod**: the time spent inside every other mod's Harmony patches and Update methods, per frame and per second, with the memory each one allocates. A patch shared by two mods is split evenly between them.
- **Hook cost**: the extra time a hooked game method takes just because it is hooked, and the game's time inside hooked methods.
- **Stutters (hitches)**: every frame slower than `HitchMs` (50 ms by default) is kept with the methods and mods that ran longest in it, the scene, and whether a garbage collection ran. Each one gets a likely cause.
- **Methods**: every timed patch and Update method, sortable and with a filter.
- **Deep profile**: the Deep button on a mod's row also times every method in that mod, to find the slow one inside it.
- **Menus**: the Web tab times each mod's scripts inside the game's web UI, and counts the scripts each mod sends, the page elements it owns, and the timers, intervals, listeners and observers it leaves running. A mod with a lot of these slows the menus even when its scripts look fast. This covers the main page and every game page inside it. Long tasks show how much of their time no mod's script explains. Slow messages from a page to the game are listed with what they asked for, and each game page gets its element and listener count. Every click and key press is timed until the page draws again and until the fades it started have finished.
- **Browser trace**: the Trace 10 s button, or the trace key (End by default) with the panel closed, records the browser for 10 seconds. It saves what ran, which mod sent it, the frames drawn and the garbage collections, and follows the frames from the page to the game screen to show where any are lost. During the trace the analyzer also times the game's side: messages to the page and back, how late the page's frames reach the game screen, and how long a mouse move takes to reach the page. A small dot flashes in the top-left corner while it runs.
- **The game itself**: frames per second, world updates per second, physics steps per second, the game speed and how many in-game seconds pass per real second. The game has no fixed tick: it updates its world once per frame, so world updates follow the frame rate. When a frame takes longer than the engine allows, the game slows down, and the analyzer shows how much game time was lost that way.
- **Memory**: the game's script memory, the mods' memory, what the engine has allocated and reserved, textures and the whole process, read once a second, with the peak and the growth per minute. A reading the game build doesn't give shows as not available.
- **Scenes**: frame rate and frame time spread for each scene since the last reset.
- **The analyzer's cost**: the analyzer measures what one timing costs and shows how much of each frame it takes itself.

The frame file is a CSV with one line per frame and a column per mod, for a spreadsheet. The seconds file has one line per second of the last hour: frame rate, world updates, game speed, the scene, the game page that was open, and memory. With `RemoteDebugging` on it also has the browser's style, layout and script time for each second.

## Buttons

| Button | What it does |
| --- | --- |
| Start / Stop | Start or pause recording. |
| Reset | Forget everything recorded so far. |
| Unhook | Stop recording and remove every timing hook, to compare the game with and without the analyzer. |
| Copy report | Put the text report on the clipboard. |
| Save report | Write `report-<date>.txt`, `latest-report.txt`, `frames-<date>.csv` and `seconds-<date>.csv` to `BepInEx\performance-analyzer`. |
| Overlay | Show frame time, the slowest mod, world updates, game speed and memory in the top-right corner, even with the panel closed. |
| Deep (Mods tab) | Time every method of that mod. One mod at a time. |

## For modders: markers

The analyzer already times your patches and Update methods. To see which part of a slow method is the slow part, mark sections of your code:

1. Copy [markers/PerfMarkers.cs](markers/PerfMarkers.cs) into your mod. It needs no reference to the analyzer.
2. Make one marker per section, in a static field, and wrap the code:

```csharp
private static readonly PerfMarker CropScan = new("Crop scan");

using (CropScan.Time())
{
    // the code to time
}
```

Each marker shows as its own row, kind `marker`, in the Methods tab, the report and the list of what ran in each stutter. Its time is left out of the self time of the method it runs in. Without the analyzer installed a marker does nothing.

## What it can't see

- Time spent in the game's code outside hooked methods is shown as "rest of game", not split further.
- Work a mod does on another thread is counted, but not timed.
- Timers and listeners a mod set up in the menus before the analyzer reached the page, right at the start of the game, are not counted for any mod.

## Requirements

- BepInEx 6 (IL2CPP).
- [Exiled's Toolkit](https://github.com/AnExiledDev/SurvivalLog-ExiledToolkit) 0.20.0 or newer. Performance Analyzer does not load without it. It stops the panel key and the trace key from firing while you type, passes the panel's button presses to the mod, applies settings changed on the Mod Manager's Mods screen without a restart, and its UI size setting sizes the panel, the corner overlay and the notices. Performance Analyzer's `UiScale` setting under Interface sets a size for this mod alone. At 0, the default, it uses the Toolkit's size.

Settings are in [CONFIG.md](CONFIG.md).
