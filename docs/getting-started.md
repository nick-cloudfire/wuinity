# Getting started

This guide takes you from a fresh clone to a completed simulation, using the
bundled **Roxborough** example (Colorado, USA). It is the most complete,
up-to-date example and matches the current engine.

## 1. Prerequisites

- **Windows** (the `dev` branch is Windows-only — see [Building](building.md)).
- **.NET 8 SDK** — <https://dotnet.microsoft.com/en-us/download/dotnet/8.0>.
- **SUMO 1.22**, installed with all extras — <https://eclipse.dev/sumo/>. Needed
  for traffic and for the bundled GDAL. See [Troubleshooting](troubleshooting.md)
  if GeoTIFF/traffic fails to load.
- **Unity** — only if you want the visualizer. Add the project in Unity Hub and
  it will tell you the exact editor version.

You do **not** need a Mapbox token to run a simulation; it is only used for the
map background in the visualizer.

## 2. Build the engine and the command-line tools

```sh
powershell -ExecutionPolicy Bypass -File build.ps1   # Windows
./build.sh                                           # Linux
```

This builds `PREACTcore` (the engine), `PREACTexecute` (the head-less runner,
`PREACT/PREACTexecute/bin/Release/net8.0/PREACT.exe`) and `PREACTcli`
(`PREACT/PREACTcli/bin/Release/net8.0/PREACTcli.exe`), all in Release, and says
where they are.

> The engine DLLs the Unity project uses are not committed; the script builds
> them into `WUInity/Assets/PREACT/Release/`. Run it before opening WUI-NITY in
> Unity, and again after pulling engine changes.

## 3. Run a simulation (command line)

```sh
PREACT/PREACTexecute/bin/Release/net8.0/PREACT.exe \
    Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui
```

The run logs progress to the console. When it finishes, results appear in
`Examples/NFDRS4_Behave/Roxborough/_output/`. See [Output files](output-files.md)
for what each file contains.

To run a Monte-Carlo batch (the run stops early once results converge):

```sh
# PREACT.exe <file.wui> <numberOfRuns> <batchSize> <offset>
PREACT.exe Roxborough_no_smoke.wui 20 4 0
```

See [Command-line tools](command-line-tools.md) for the full argument reference.

## 4. Run in the visualizer (Unity)

1. Run the build script (step 2) so the engine DLLs are in
   `WUInity/Assets/PREACT/Release/` and up to date.
2. Open the `WUInity/` project in Unity (the version Unity Hub reports).
3. Open the scene `WUInity/Assets/WUInity/Scenes/WUInityMain.unity`.
4. (Optional) add a Mapbox token — see [Troubleshooting](troubleshooting.md).
5. Press **Play**, then load a `.wui` project from the in-app menu.

### Finding things in the visualizer

The **Scenario workflow** panel, docked on the left, lists every step from an empty folder to a trigger
campaign, in the order the work is done, each with a status (Done, Needs attention, Blocked, To do), what is
wrong and a button that does the step:

1. Place and time · 2. Roads · 3. Population · 4. Fuels, canopy and buildings · 5. Fire case (ELMFIRE) ·
6. Fire areas and ignition · 7. Destinations · 8. Response curves and demographics · 9. Evacuation groups ·
10. Trigger boundary (k-PERIL) · 11. Run one simulation · 12. Results · 13. Probabilistic campaign

The menus hold the same actions, grouped the same way:

| Menu | Holds |
|---|---|
| **File** | New scenario, Open, Open recent, Save, Save as, Copy scenario to, Close, Quit. Unsaved changes are asked about before anything discards them. |
| **Scenario** | The workflow panel, Place and time, Terrain, Weather, Check scenario, All settings. |
| **Data** | Roads (OpenStreetMap to a SUMO network), Population (WorldPop to households), Fuels, canopy and buildings (LANDFIRE for the US), Build fire case. Each runs only what is missing. |
| **Fire** | Fire areas (painting), Ignition points, Fire model settings, Fire behaviour (the namelist) and its preview, Trigger boundary, Smoke. |
| **Evacuation** | Modules, Destinations, Response curves, Demographics, Evacuation groups, Paint group areas. |
| **Run** | Run simulation (F5), Pause, Stop, Probabilistic trigger campaign. |
| **Results** | Live output, Show on map (fire arrival, trigger boundary and probability, burn probability, arrival percentiles), the results of the last run and campaign. |
| **View** / **Help** | Map layers, console, theme, window layout / getting started, troubleshooting, external tools and keys. |

Two distinctions worth knowing:

- **Fire model settings** is how ELMFIRE is *run* — the case, the executables, the fire duration, what to
  build. **Fire behaviour** is what it is *told*, and becomes the generated `elmfire.data`. Setting a
  `NamelistTemplate` replaces the whole of Fire behaviour, and the page says so.
- For an ELMFIRE scenario the case's `dem.tif` is the grid everything is painted and computed on. Building
  the case points `[Landscape]` at the case terrain; fuel and canopy rasters are named under Fuels, canopy
  and buildings, not under Terrain.

## 5. Prepare your own scenario

`File > New scenario` asks for a folder, a name, the area (picked on the map) and the dates, and writes the
`.wui` at once. Then work down the workflow panel: each row's button fetches or builds what that step needs —
roads, population, fuels, the fire case — and sets the paths on the scenario, which `File > Save` (Ctrl+S)
keeps. To understand or hand-edit the file, read [The `.wui` input file format](input-file-format.md).

## Where to go next

- [The `.wui` input file format](input-file-format.md)
- [Examples](examples.md) — what else ships in `Examples/`
- [Module status](modules.md) — what is production-ready vs experimental
- [Troubleshooting](troubleshooting.md)
