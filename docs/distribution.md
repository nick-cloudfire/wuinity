# Distribution: WUInity as a standalone program

`build-player.ps1` builds WUInity as a Windows program that runs without the Unity editor:
`dist\WUInity\WUInity.exe`, with `PREACT.exe`, `PREACTcli.exe` and ELMFIRE beside it. This page covers how to
build it, what the folder holds, what else a machine needs, and where the program looks for its tools and keys.
Building the engine and the editor set-up are in [Building](building.md).

## Building it

You need what the editor needs (see [Building](building.md)): the .NET 8 SDK, Unity 6000.3.15f1 through Unity
Hub, and the submodules. To ship ELMFIRE, build it first with `make_windows.bat`
([Building ELMFIRE](building.md#building-elmfire)); without it the folder has no `elmfire\`.

**Close the Unity editor first.** Unity cannot open the project in batch mode while an editor has it open; the
script checks this and stops if it is. Then:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-player.ps1
```

It works in Windows PowerShell 5.1 and in PowerShell 7, and runs three steps:

1. **`build.ps1`**: the engine DLLs into the Unity project, then `PREACT.exe` and `PREACTcli.exe`, all in
   Release.
2. **Unity in batch mode**. The editor script `WUInity/Assets/WUInity/Editor/PlayerBuild.cs` builds the scenes
   enabled in the Build Settings into `dist\WUInity\WUInity.exe` and `dist\WUInity\WUInity_Data\`. Unity's log goes
   to `dist\WUInity-build.log`. The first build imports the whole project and can take a while; the script prints
   the elapsed minutes.
3. **The copies beside the player**: `PREACT\`, `elmfire\`, `docs\`, `README.txt`, and the engine's native
   runtimes into `WUInity_Data\Managed\Runtimes\` (see [What ships](#what-ships)).

It ends with `Build succeeded.` and any warnings, or `BUILD FAILED: <reason>` and exit code 1. When Unity fails,
the compiler errors and Unity's own failure lines from the log are printed.

| Option | Meaning |
|---|---|
| `-Unity <Unity.exe>` | The Unity to build with. Default: `UNITY_EXE`, else Unity Hub's install of the project's version (`WUInity\ProjectSettings\ProjectVersion.txt`) under Program Files or the Hub's own install folder. |
| `-Output <folder>` | Where to build. Default `dist\WUInity`. An earlier build there is replaced. A folder that holds anything else is refused, as are the repository itself and anything under `WUInity\Assets`. |
| `-ElmfireExe <exe>` | The ELMFIRE to ship. Default: `WUInity\Assets\ThirdParty\elmfire\build\windows\bin\elmfire.exe`. It is shipped as `elmfire\elmfire.exe` whatever its name. |
| `-SkipEngineBuild` | Use what `build.ps1` built last instead of running it. |
| `-SkipPlayer` | Keep the player in `-Output` and only replace `PREACT\`, `elmfire\` and `docs\`, for example after rebuilding ELMFIRE. **Not after an engine change**: the player carries its own copy of the engine. The script warns when that copy differs from the one `PREACT\` gets. |
| `-Development` | A development player: a console window, and stack traces with line numbers. |

**From the editor**, **WUInity > Build standalone player...** builds the player alone into the same place. It does
not make the copies, so campaigns and fires do not work from that folder until the script has run.

Run the script again after every pull. `dist\` is ignored by git.

## What ships

```
dist\WUInity\
  WUInity.exe, UnityPlayer.dll, UnityCrashHandler64.exe, MonoBleedingEdge\
  WUInity_Data\                 the visualizer; Managed\ holds the engine (PREACTcore.dll) and Managed\Runtimes\
                                its native runtimes (GDAL wrappers, NFDRS4, FOFEM); StreamingAssets\Fonts\ the GUI font
  PREACT\                       PREACT.exe and PREACTcli.exe with their DLLs and Runtimes\
  elmfire\                      elmfire.exe, the DLLs beside it (impi.dll), fuel_models.csv,
                                building_fuel_models.csv and ELMFIRE's other fuel tables
  docs\                         this documentation, with README.md, CHANGELOG.md and the licences
  README.txt                    what the folder holds and what else to install
```

The folder can be copied or zipped as it is; nothing in it refers back to the repository. The program finds its
parts relative to itself:

- **ELMFIRE**: in `elmfire\` beside the program (from `WUInity_Data\Managed` and from `PREACT\`). This comes before
  the vendored build in a checkout, so a build that is still inside the repository uses the same ELMFIRE as one
  copied elsewhere.
- **ELMFIRE's fuel model tables**: beside that `elmfire.exe`. For an executable in an ELMFIRE tree, `build\source` is
  used.
- **PREACTcli**: the campaign window takes `PREACT\PREACTcli.exe` beside the program. `PREACTcli` finds
  `PREACT.exe` beside itself.
- **The docs**: Help > Getting started and Help > Troubleshooting open `docs\`.
- **The GUI font**: from `WUInity_Data\StreamingAssets\Fonts`. Without it the GUI uses Dear ImGui's own font.

## What else the machine needs

These are not shipped. Help > External tools and keys shows which of them the program found.

| Install | Why | Notes |
|---|---|---|
| **.NET 8 Runtime (x64)** | `PREACT.exe` and `PREACTcli.exe` are .NET 8 programs. A campaign runs both, and so does a head-less run. The visualizer itself does not need it. | <https://dotnet.microsoft.com/download/dotnet/8.0>. `dotnet --list-runtimes` should list `Microsoft.NETCore.App 8.x`. A machine with the .NET 8 SDK has it. |
| **SUMO 1.22**, with extras | Traffic. On Windows the engine also loads GDAL's library (`gdal.dll`) from SUMO's `bin`. **Without SUMO no raster can be read or written**, and the program says so when it starts. | Set `SUMO_HOME`, put its `bin` on `PATH`, or name it in Help > External tools and keys. |
| **QGIS or OSGeo4W** | GDAL's command-line tools (`gdal_translate`, `gdalinfo`, `gdalsrsinfo`), which ELMFIRE runs; also a PROJ database (`share\proj`). | The newest `C:\Program Files\QGIS*\bin` is found by itself. |
| **WindNinja** | Terrain-resolved wind. Without it a case gets one wind value for the whole domain, and a campaign refuses to start unless told to accept that. | The installer's location is found by itself. |
| **Microsoft Visual C++ 2015-2022 Redistributable (x64)** | The engine's GDAL wrappers import `MSVCP140.dll` and `VCRUNTIME140.dll`. | Usually already installed: SUMO, QGIS and most other programs install it. |
| **Intel MPI runtime** | Only if `elmfire\` has no `impi.dll`. | `make_windows.bat` copies `impi.dll` beside `elmfire.exe`, and the script copies it from there. ELMFIRE's Fortran and C runtimes are linked statically. |

## Where the tools are found

The same rules hold in the editor, in the standalone program, in `PREACT.exe` and in `PREACTcli`, so a campaign uses
the tools the GUI shows. For each tool, the first that applies wins:

1. **The scenario or the command line**: `[ELMFIRE] ElmfireExe`, `PathToGdal` and `WindNinjaExe`, and PREACTcli's
   `--elmfire`, `--gdal` and `--windninja`. A key that names something that is not there is reported; the next
   source is not tried instead.
2. **Your setting**, from **Help > External tools and keys** (below).
3. **The automatic search**:

| Tool | Automatic search |
|---|---|
| ELMFIRE | `elmfire\` beside the program, then `WUInity/Assets/ThirdParty/elmfire/build/windows/bin/elmfire.exe` above the program or the working folder |
| GDAL tools | `PATH`, the newest `C:\Program Files\QGIS*\bin`, `C:\OSGeo4W\bin`, `C:\OSGeo4W64\bin`, `%SUMO_HOME%\bin` |
| WindNinja | `WINDNINJA_CLI`, `PATH`, `C:\WindNinja`, `%ProgramFiles%\WindNinja` (searched below) |
| SUMO | `%SUMO_HOME%\bin`, the `PATH` folder holding `sumo.exe`, then a `PATH` folder named like SUMO's `bin` |
| PROJ data | `PROJ_DATA`, then `PROJ_LIB`. Otherwise GDAL's own search is left alone. |

### Help > External tools and keys

The window has a path field for each tool, with **Browse...** and **Clear**. An empty field means "find it
automatically". What you type is checked as you type: the path must be a full path, and must hold the program. You
can pick the program itself, its folder, or an install above it. For example, the QGIS folder works for the GDAL
tools (its `bin`), `SUMO_HOME` or SUMO's `bin` both work for SUMO, and a QGIS folder works for PROJ (its
`share\proj`). **Save** is refused while a field is wrong.

Under each field, **In use** gives the path in use and where it came from: the open scenario, your setting, or the
automatic search and where it looked. A scenario key that names nothing is shown, and so is a saved path that
holds nothing; a saved path that holds nothing is skipped, so the search still runs.

When a saved path takes effect:

| Tool | After Save |
|---|---|
| ELMFIRE, GDAL tools, WindNinja | At once. They are looked up each time they are used, so the next case build, run or campaign uses the new path. |
| PROJ | At once. It is handed to the program's GDAL, which applies it to its next lookup. Clearing it waits for a restart. |
| SUMO | **After restarting WUInity.** SUMO's `bin` goes on the library path when the program starts, before GDAL's `gdal.dll` is loaded from it; SUMO's own library is loaded by the first traffic run. The window says `Saved: ... Applies after restarting WUInity.` |

### The settings file

The paths are saved per user, outside every scenario, because a scenario is meant to be shared and where a program
is installed belongs to the machine:

- Windows: `%APPDATA%\PREACT\tools.ini`;
- Linux: `$XDG_CONFIG_HOME/PREACT/tools.ini`, or `~/.config/PREACT/tools.ini`;
- or the file named by the environment variable `PREACT_TOOLS_FILE`, if set. Tests use this, and so can a second
  set of tools.

You can also edit the file by hand. A program reads the change at its next lookup. A program that saves it takes
`tools.ini.lock` beside it for the moment it reads and writes, so two programs saving at once (the GUI and a
campaign's CLI) do not undo each other's change:

```ini
[Tools]
ElmfireExe = D:\elmfire\build\windows\bin\elmfire.exe
GdalBin = C:\Program Files\QGIS 3.40.4\bin
WindNinjaExe =
Sumo = C:\Program Files (x86)\Eclipse\Sumo
ProjData = C:\Program Files\QGIS 3.40.4\share\proj
[User]
LandfireEmail = you@example.org
```

Lines starting with `;` or `#` are comments. Quotes around a value are removed. Keys this version does not know
are kept when the window saves. The same file keeps the **LANDFIRE contact e-mail** (`[User] LandfireEmail`),
entered in the fuels step (Fuels, canopy and buildings > Get them); `LANDFIRE_EMAIL` and `PREACTcli landfire --email`
come before it. An e-mail an earlier v1.1 build kept in `%APPDATA%\WUInity\user-settings.txt` is moved into this
file the first time it is needed.

## Keys

The standalone program cannot read `WUInity\Assets\Resources`, because there is none beside it. **The keys are
built into the player** from the files that exist when it is built:

- **Mapbox token**: from `WUInity/Assets/Resources/Mapbox/MapboxConfiguration.txt`. To change it, build the player
  again.
- **OpenTopography key**: from `WUInity/Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt`. The
  `OPENTOPOGRAPHY_API_KEY` environment variable wins over it. With neither, Help > External tools and keys takes a
  key for the current session. The program hands its key to the engine, so the fire case build uses it.

`PlayerBuild` says which key files it builds in, as a warning in the log and in the menu's confirmation.
**Whoever gets a copy of the folder gets those keys.** To share a build, move the two files out of
`Assets/Resources` before building, and put them back afterwards. Each user then sets `OPENTOPOGRAPHY_API_KEY`;
the map stays blank without a Mapbox token, and everything else works.

`PREACTcli build-case`, run by hand from `PREACT\`, cannot read the key built into the player. Set
`OPENTOPOGRAPHY_API_KEY` or pass `--api-key`.

## What the program writes, and where

| File | Where |
|---|---|
| Tool paths and the LANDFIRE e-mail | `%APPDATA%\PREACT\tools.ini` |
| Window layout (`imgui.ini`) | The working folder: the program's folder when it is started from Explorer. Put the folder somewhere you can write to (not `C:\Program Files`), or the layout is not kept. |
| Unity's log (`Player.log`) | `%USERPROFILE%\AppData\LocalLow\Lund University\WUInity (PREACT)\` |
| Scenarios, cases, campaigns | Their own folders, as in the editor. |

## Checking a build

On the build machine, and once on a machine that has never had the repository:

1. Start `WUInity.exe`. The GUI font is the editor's, and the map loads if the token was built in.
2. **Help > External tools and keys**. ELMFIRE is in use from `...\WUInity\elmfire\elmfire.exe`, found by the
   automatic search. GDAL, WindNinja, SUMO and PROJ are found, or have a path set.
3. Open a scenario, such as Mati, and build its fire case (row 5). The log says the fuel table was copied from
   `...\elmfire\fuel_models.csv` when the case had none.
4. Run it, and open the campaign window. **PREACTcli** is `...\WUInity\PREACT\PREACTcli.exe`. Start a campaign with
   a few realizations.
5. **Help > Getting started** opens `docs\getting-started.md`.

## Limits

- Windows x64 only. The player build script sets `StandaloneWindows64`; Linux runs the engine and the CLI
  head-less ([Building](building.md#linux)).
- The GUI font is Adobe Clean (`Assets/StreamingAssets/Fonts/AdobeClean-Regular.otf`), Adobe's own typeface. Check
  that its licence allows you to redistribute it before you give the folder to others. To ship without it, delete
  `WUInity_Data\StreamingAssets\Fonts\` from the build; the GUI falls back to Dear ImGui's font.
- `PREACT.exe` and `PREACTcli.exe` are framework-dependent, so they need the .NET 8 Runtime.
