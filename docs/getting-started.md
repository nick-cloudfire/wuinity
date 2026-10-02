# Getting started

This page takes you from a fresh clone to a trigger campaign in the visualizer: build, set up the external
tools, run a shipped example, then make a scenario of your own by working down the **Scenario workflow** panel.
Help > Getting started (docs) opens this page from inside the visualizer.

## 1. What you need

| For | You need |
|---|---|
| Everything | Windows (x64) and the .NET 8 SDK. |
| The visualizer | Unity 6000.3.15f1 (through Unity Hub). |
| Traffic, and the GDAL library the engine loads | SUMO 1.22, installed with its extras. |
| ELMFIRE fires | `elmfire.exe` built from the `ELMFIRE-WUINITY` submodule (commit a7fb9d6 or later), the GDAL command-line tools (QGIS or OSGeo4W), WindNinja, and an OpenTopography API key. |
| The map background | A Mapbox access token (optional: without it the map is blank, and everything else works). |

The engine and the command-line tools also run on Linux; the visualizer does not. Details, and the Linux
setup, are in [Building](building.md).

## 2. Build

Clone with the submodules, then build the engine, `PREACT.exe` and `PREACTcli.exe`:

```powershell
git clone --recurse-submodules <repository url>
cd <the cloned folder>
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

**Run the script before opening Unity**, and again after every pull that changes `PREACT/`: the engine DLLs the
Unity project uses are not committed, and Unity opened without them reports compile errors. Then build ELMFIRE
once with `make_windows.bat` — see [Building ELMFIRE](building.md#building-elmfire).

## 3. Keys and external tools

| What | Where it goes |
|---|---|
| OpenTopography API key (free from portal.opentopography.org) | Copy `WUInity/Assets/Resources/OpenTopography/OpenTopographyConfigurationTemplate.txt` to `OpenTopographyConfiguration.txt` beside it and paste the key in, or set `OPENTOPOGRAPHY_API_KEY`. Help > External tools and keys can also take one for the current session only. |
| Mapbox token | `WUInity/Assets/Resources/Mapbox/MapboxConfiguration.txt`, the way the Mapbox SDK expects. |
| SUMO | `SUMO_HOME`, or its `bin` folder on `PATH`. |
| GDAL command-line tools | `PATH`, or a QGIS / OSGeo4W install, which is found by itself. |
| WindNinja | The installer's location, `PATH`, or `WINDNINJA_CLI`. |

**Help > External tools and keys** lists what was found — ELMFIRE, the GDAL tools, WindNinja, SUMO, `PROJ_LIB`,
`PROJ_DATA`, the Mapbox token and the OpenTopography key — where each was looked for, and what does not work
without it. **Look again** searches again after you install something.

## 4. Open the visualizer

1. In Unity Hub, add the `WUInity/` folder and open it with Unity 6000.3.15f1.
2. Open the scene `Assets/WUInity/Scenes/WUInityMain.unity` and press **Play**.
3. The scenario you had open last time reopens. Otherwise the workflow panel offers **New scenario**, **Open
   scenario** and your recent scenarios.

On the map, drag with the left or middle mouse button to pan, use the arrow keys, and scroll to zoom.
**View > Reset window layout** puts the workflow panel back on the left and the console along the bottom.

## 5. Run a shipped example

File > Open scenario, and pick `Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui`. It imports a
FlamMap fire from rasters, so it needs no ELMFIRE and no OpenTopography key — only SUMO. Its workflow panel shows
step 4 as *Imported fire rasters* and step 5 as *Terrain (DEM)*, since there is no ELMFIRE case to build.

Press **F5** (Run > Run simulation), then **Run**. The fire, the households and the cars are drawn as the run
goes; Results > Live output shows the counts. When it finishes, Results > Show on map > Fire arrival (last run)
draws the fire, and Results > Open output folder opens `_output/`. What each file holds is in
[Output files](output-files.md).

The same run from the command line:

```powershell
PREACT\PREACTexecute\bin\Release\net8.0\PREACT.exe Examples\NFDRS4_Behave\Roxborough\Roxborough_no_smoke.wui
```

More on both examples: [Examples](examples.md).

## 6. Make a new scenario

**File > New scenario** (Ctrl+N) writes a `.wui` straight away; everything else is prepared afterwards from the
workflow panel.

| Field | Notes |
|---|---|
| Folder, and *Make a folder for it, named after it* | Where the scenario goes. Every path in the scenario is relative to this folder. |
| Name | Every prepared file is named after it, so it cannot contain spaces or `\ / : * ? " < > \|`. Rename early, if at all. |
| Area of interest | **Pick on map**: the dialog hides while you click two opposite corners on the world map (Escape brings it back). Or type the corners under *Type the corners in*. The dialog shows the size, warns above 50 km across, and says whether LANDFIRE covers the area (the US only). |
| Starts, Ends | The simulated window. The fire case's weather is fetched for it, so give it the whole event. |
| What it simulates | Wildfire (ELMFIRE, or imported rasters), Trigger boundary (k-PERIL), Smoke, People on foot, Vehicles (SUMO). All but smoke are on by default. |
| Start with a standard response curve and demographics | On by default: a curve named `standard` (10 % of households gone 60 min after the order, 60 % by 80 min, all by 100 min) and demographics `standard` (up to 2 cars, 30 % chance of the second). A starting point to replace with the town's own, not a finding. |

An ELMFIRE scenario's fire runs for the length of the window, at least 24 h and at most 240 h
(`[ELMFIRE] SimulationTstopHours`; Fire > Fire model settings changes it).

## 7. Work down the workflow

The **Scenario workflow** panel lists the thirteen steps from an empty folder to a trigger campaign, in the
order the work is done. Each row has a status — **Done**, **Needs attention**, **Blocked**, **To do** — the
problems it found, each with a button that fixes it, and a main button that does the step. A step that cannot
start yet says which step blocks it. Anything a step fetches or builds is recorded on the scenario; **File >
Save** (Ctrl+S) keeps it, and the workflow reminds you while there are unsaved changes.

A running data step shows its progress in its row and can be stopped there. While a step, a simulation or a
campaign runs, the steps that would change what it reads are disabled, and say why.

### 1. Place and time

The name, the area of interest and the start and end. **Edit place and time** changes them. Problems it reports:
no name, a name that cannot be a file name, no area, an end before the start, a domain larger than 50 km across,
a scenario never saved.

### 2. Roads

**Prepare missing** downloads the OpenStreetMap roads for the domain (`downloads/<Name>.osm.xml`), builds the
RouterDb that households are placed on (`<Name>.routerdb`), and, with traffic on, converts the roads to a SUMO
network with SUMO's `netconvert` (`sumo/osm.sumocfg`), skipping whatever already exists. **Rebuild everything**
downloads again. **Show on map** draws the lanes the traffic is routed on. It needs SUMO to be found.

### 3. Population

**Prepare missing** downloads WorldPop's population raster for the area, builds the RouterDb if there is none,
and places the people as households on the roads (`<Name>_population.csv`). **Regenerate** starts again from
WorldPop. WorldPop covers the whole world; where it has no data for the scenario's year, the nearest year is
used and the log says so.

### 4. Fuels, canopy and buildings

The rasters the fire case is cut from: a fuel model raster, and canopy cover, height, base height and bulk
density.

- **In the US**: **Get LANDFIRE fuels and canopy (US)** downloads them for the domain (into `downloads/landfire/`)
  and names them as the source layers.
- **Elsewhere**: **Source layers** takes a fuel model raster of your own, in any CRS — coded as Scott & Burgan 40,
  or Anderson 13 with `FuelModelStandard` set to match — and optional canopy rasters.

Without canopy the case is filled with zeros, so the fire is surface fire only; the step warns about it. Where
the layers can come from, and what the build does to them: [ELMFIRE cases](elmfire-cases.md#where-the-layers-come-from).

### 5. Fire case (ELMFIRE)

**Build fire case** makes the case folder (`elmfire/` beside the `.wui`): it downloads the terrain from
OpenTopography, cuts every source layer onto the terrain's grid, fetches the weather (ERA5 through Open-Meteo)
and turns it into wind and fuel moisture rasters through WindNinja and the Nelson model, and writes the
namelist. Once built, the button reads **Update the case**, which builds only what is missing and keeps the
rest; **Rebuild everything** replaces every layer, the weather and the namelist — for a changed domain, cell
size or source layer. **Rebuild weather only** makes the wind and fuel moisture again for the scenario's current
start time and fire duration and touches nothing else (the namelist's time and weather band keys follow); the step
offers it when the case's weather is shorter than the fire or was made for another start hour.

The build checks first that the case will have a fuel model, and refuses before downloading anything without one.

The step says before you build what will not work: no OpenTopography key (it blocks the build), no ELMFIRE
executable, no GDAL tools, no WindNinja (the case then has one wind value for the whole domain, and a trigger
boundary comes out circular). Fire > Fire model settings holds how the case is built and run (case folder,
duration, cell size, padding, executables); Fire > Fire behaviour holds what ELMFIRE is told. See
[two distinctions](#two-distinctions-worth-knowing) below and [ELMFIRE cases](elmfire-cases.md).

### 6. Fire areas and ignition

**Fire areas** opens the painting window, on the case grid:

| Area | What it is for |
|---|---|
| WUI area | What the trigger boundary protects. The case build writes it as `wui_area.tif`. |
| Ignition area | Where a campaign's fires may start (`ignition_mask.tif`). Unpainted means anywhere burnable. |
| Initial ignition | Where the single run's fire starts: the middle of what is painted. |

**Start painting**, then left-drag to paint, right-click to flood-fill, keypad + and − to change the brush;
**Erase** paints the area out. **Save painted areas** writes the `.gfi` file and records it on the scenario.
**Apply to case** builds the case again so the painted areas become its `ignition_mask.tif` and `wui_area.tif`;
the step warns while the painting is newer than the case.

For an exact start, **New ignition point** places a point (Set on map, snapped to the centre of a fire cell by
default) with its own start time. A painting made on another grid — an older landscape raster, or a grid a
rebuild replaced — is moved onto the case grid with **Move painting onto the fire-case grid**, into a new file.

### 7. Destinations

**Destinations**: add the places people evacuate to — an exit from the domain, or a shelter. **Set on map**
places one, snapped to the nearest road lane when that box is ticked (it is by default). Any destination can
limit its arrival flow (cars per hour); a shelter can also limit the vehicles and people it takes, and closes for
good when either is reached. `-1` means no limit.

### 8. Response curves and demographics

**Response curves**: when households set off. A curve is a list of (time, cumulative probability) points,
relative to their group's evacuation order unless it is `Absolute`; it must rise, and a curve ending below 1
leaves some households at home. **Demographics**: how many cars a household takes. One demographics must be
marked Default. The keys are in [the input format](input-file-format.md#responsecurve--repeatable).

### 9. Evacuation groups

**Evacuation groups**: who is ordered to leave, when (`EvacuationOrderDateTime`, shown against the simulated
window), to which destinations, by which response curves, with which demographics. A group's area is painted
(**Paint group areas**, on the fire grid — for an ELMFIRE scenario the case's `dem.tif`, so after step 5) or
taken from a shapefile. A cell belongs to
one group only, so painting covers every group at once; **Save group areas** writes one mask per group.

### 10. Trigger boundary (k-PERIL)

**Trigger boundary**: which area k-PERIL protects (`WuiAreaSource`) — a WUI raster (`WuiAreaFile`; left empty,
the painted WUI area), or the evacuation groups' areas — and, for a fire without a wind of its own, which wind
rasters it reads. The step warns when the fire stops before it reaches
the area (no boundary is computed then). How the boundary is computed, and what to know before relying on its
shape: [Modules](modules.md#trigger-boundary).

### 11. Run one simulation

**Run simulation...** (F5) lists what still blocks the run, with a button to the first problem. Its **Run**
(**Save and run** when there are unsaved changes) saves first, because what runs is what is on disk. More than
one run makes a Monte-Carlo batch, one run after another in this process, which can stop once the evacuation
time has converged. For an ELMFIRE scenario the fire is computed before the evacuation starts (or an identical
earlier fire reused), so Pause becomes available only after that.

A single run is the quickest check that the evacuation works before a campaign.

### 12. Results

**Results** lists what the last run and campaign wrote — the rasters, not the `.prj`, `.aux.xml` or `.ovr` files
beside them. A boundary without a `.prj` is marked *(earlier version)* (a run that could not write one says so in
its log), and a campaign made before v1's k-PERIL fix
gets a line saying so. Results > Show on map draws a result raster over the map: the fire's arrival, the trigger
boundary, and a campaign's trigger probability, burn probability and arrival percentiles. [Output
files](output-files.md) says what each file holds.

### 13. Trigger campaign

**Campaign...** sets up and starts a campaign: many fires, each from a drawn ignition under drawn weather, each
with its evacuation and its trigger boundary, aggregated until the probability raster stops changing. It needs
the case built, the painted areas applied, and the trigger boundary on. A campaign folder made before v1 (its
realizations predate v1's k-PERIL orientation fix and per-realization evacuation seeds) is not reused: the step
warns, and Run starts a new campaign beside it. The window, its fields and what happens on Run: [Trigger
campaigns](trigger-campaigns.md#in-the-gui).

## Finding things

The menus hold the same actions as the workflow, grouped the same way:

| Menu | Holds |
|---|---|
| **File** | New scenario (Ctrl+N), Open scenario (Ctrl+O), Open recent, Save (Ctrl+S), Save as (same folder), Copy scenario to, Reveal scenario folder, Close scenario, Quit. |
| **Scenario** | Workflow panel, Place and time, Terrain, Weather, Check scenario, All settings (every setting in one tabbed window). |
| **Data** | Roads: OpenStreetMap to SUMO network; Population: WorldPop to households; Fuels, canopy and buildings; Build fire case (ELMFIRE). Each runs what is missing and shows its workflow row. Advanced: LANDFIRE fuels and canopy (US), and — for a scenario without ELMFIRE — Download DEM only and Download Open-Meteo weather. |
| **Fire** | Fire areas (WUI, ignition area), Ignition points, Fire model settings, Fire behaviour (namelist), Preview namelist, Trigger boundary (k-PERIL), Smoke (when smoke is on). |
| **Evacuation** | Modules (pedestrian, traffic), Destinations, Response curves, Demographics, Evacuation groups, Paint group areas. |
| **Run** | Run simulation (F5), Pause / Resume, Stop, Trigger campaign (opens at any time, to set one up or to watch and cancel the one running). |
| **Results** | Live output; Show on map (fire arrival, trigger boundary, trigger and burn probability, arrival p10/p50/p90, evacuation groups, the case's WUI area, Hide result); Results of the last run and campaign; Open output folder. |
| **View** | Map layers (population density, road network, fire grid outline, markers, result overlay), Console, Clear console, Theme, Reset window layout. |
| **Help** | Getting started, Troubleshooting, External tools and keys, About. |

Scenario > Check scenario parses the scenario as it stands, unsaved edits included, and files what it finds
under the workflow's steps — the same check a run makes when it loads the file.

## Two distinctions worth knowing

- **Fire model settings** is how ELMFIRE is *run* — the case, the executables, the fire duration, what to
  build. **Fire behaviour** is what it is *told*, and becomes the generated `elmfire.data`. Every build writes
  `elmfire.data` again from these settings; one edited by hand is first set aside as
  `elmfire.data.kept-<time>`, and step 5 says so before and after. Setting a `NamelistTemplate` replaces the
  whole of Fire behaviour, and the page says so.
- For an ELMFIRE scenario the case's `dem.tif` is the grid everything is painted and computed on. Building
  the case points `[Landscape]` at the case terrain; fuel and canopy rasters are named under Fuels, canopy
  and buildings, not under Terrain. A painting made on another grid is moved onto the case grid with step 6's
  **Move painting onto the fire-case grid**, into a new file beside the old one; a build that re-cuts the grid
  moves the painting along by itself.

## Saving and quitting

The scenario is the `.wui` file: the GUI writes it back from what it read, with paths relative to its folder
(see [what a save writes](input-file-format.md#what-a-save-writes)). A campaign, `PREACT.exe` and a run from the
GUI all read the file on disk, which is why Run and Campaign ask to save first.

**File > Quit** stops a running simulation, data step or campaign, waits for it, then asks about unsaved
changes — settings and painted areas. Stopping Play in the editor does the same, in Unity's own dialog.

To check a release by hand, [Manual test of the v1 GUI](manual-test-v1.md) walks through every part of it.

## Where to go next

- [The `.wui` input file format](input-file-format.md) — every key.
- [ELMFIRE cases](elmfire-cases.md) — what a case build does, and the namelist.
- [Trigger campaigns](trigger-campaigns.md) — how a campaign works and how to read its results.
- [Modules](modules.md) — what each part of the simulation does, and its status.
- [Command-line tools](command-line-tools.md) — `PREACT.exe` and `PREACTcli`.
- [Troubleshooting](troubleshooting.md) — messages you may meet, and what to do.
