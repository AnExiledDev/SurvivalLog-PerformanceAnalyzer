# Performance Analyzer settings

Settings live in `BepInEx\config\com.dev.survivallog.perfanalyzer.cfg`, created the first time the game starts with the mod. Change them in game on the Mod Manager's Mods screen, or edit the file while the game is closed. Delete the file to get the defaults back.

## Controls

| Setting | Default | What it does |
| --- | --- | --- |
| `PanelKey` | `Insert` | Key that opens and closes the panel. `None` turns the key off. |

## Recording

| Setting | Default | What it does |
| --- | --- | --- |
| `AutoStartSeconds` | `0` | Start recording on its own this many seconds after the game starts, to catch slow loading. `0` waits for the Start button. |
| `HitchMs` | `50` | A frame longer than this many milliseconds is kept as a hitch. |
| `WindowFrames` | `7200` | How many of the latest frames the graph, percentiles and frame file keep. Totals cover the whole recording. |
| `SaveOnQuit` | `true` | Save a report when the game closes, if anything was recorded. |

## Probes

| Setting | Default | What it does |
| --- | --- | --- |
| `Patches` | `true` | Time other mods' Harmony prefixes, postfixes and finalizers, and the hooked methods they sit on. |
| `Tickers` | `true` | Time other mods' Update, LateUpdate, FixedUpdate and OnGUI methods. |
| `GameTime` | `true` | Inside a hooked game method, time the game's own code apart from the patches. |
| `Exclude` | empty | Mods to leave untimed, by plugin id or short key, separated by commas. |

## Web

| Setting | Default | What it does |
| --- | --- | --- |
| `Probe` | `true` | Watch the menus from inside the page while recording. |
| `ScriptTraffic` | `true` | Count the scripts each mod sends to the menus, and their size. |
| `TagScripts` | `true` | Mark each mod's scripts so the page can time them. Turn it off if a menu mod misbehaves while recording. |
| `RemoteDebugging` | `true` | Let Chrome's developer tools attach to the menus. While recording, the analyzer also reads the menus' style, layout and script time each second for the report. Needs a game restart. |
| `RemoteDebuggingPort` | `9222` | Port for Chrome's developer tools. |

With `RemoteDebugging` on, open `chrome://inspect` in Chrome on the same PC, add `localhost:9222` under Configure, and the game's page shows up to inspect, with Chrome's own Performance profiler.

## Overlay

| Setting | Default | What it does |
| --- | --- | --- |
| `Show` | `false` | Show frame time and the slowest mod in the top-right corner, even with the panel closed. |
