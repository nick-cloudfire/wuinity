# The `.wui` input file format

A PREACT simulation is described by a single plain-text project file with the
`.wui` extension. This page is the authoritative reference for its syntax and
every section it can contain.

> This document is generated from the actual parser
> (`PREACT/PREACTcore/Source/Input/`). It supersedes the older
> `Documentation/new_input_format.wui`, which uses key names the current engine
> no longer reads.

A complete, working example is
[`Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui`](../Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui).

---

## Syntax rules

- **INI-style.** Sections are introduced by a header in square brackets, e.g.
  `[Simulation]`. A section runs until the next header line.
- **Key/value** pairs are written `Key=Value`.
- `#` starts a comment; everything after it on a line is ignored.
- **Keys are stripped of spaces; values are only trimmed.** `Key = Value` is
  fine, and a value **may** contain spaces — `C:/Program Files/GDAL/bin` works.
  (It did not until spaces stopped being stripped from the whole line, which
  silently turned that path into `C:/ProgramFiles/GDAL/bin`.)
- Comma-separated lists are trimmed element by element, so `a, b` is fine.
- Blank lines and lines beginning with `#` are ignored.
- Some sections (`[ResponseCurve]`) contain bare data lines with no `Key=`.
- These sections may appear **multiple times** and are collected as a list:
  `[Destination]`, `[ResponseCurve]`, `[EvacuationGroup]`, `[Demographics]`.
  All other sections should appear at most once.
- **Relative file paths are resolved against the folder that contains the
  `.wui` file.**

**Required** below means the simulation aborts if the key/section is missing or
cannot be parsed.

---

## `[Simulation]` — required

| Key | Type | Required | Default | Notes |
|-----|------|----------|---------|-------|
| `Name` | string | ✔ | – | Run name; used to name output files. |
| `LowerLeftLatLon` | lat,lon | ✔ | – | South-west corner of the domain. |
| `DomainSize` | x,y (metres) | ✔ | – | Domain width and height. |
| `StartDateTime` | ISO 8601 | ✔ | – | e.g. `2001-06-05T11:00:00`. |
| `EndDateTime` | ISO 8601 | ✔ | – | Interpreted in the simulation location's timezone. |
| `DeltaTime` | float (s) | | `1.0` | Simulation time step. |
| `StopWhenEvacuated` | bool | | `false` | End the run early once everyone has evacuated. |
| `RandomSeed` | int | | `0` | Seeds everything stochastic — departure times, walking speeds, household sizes, destination choice — so a run can be repeated. Combined with the simulation index, so run *n* of a batch is reproducible on its own rather than every run of a batch being identical (which would make a convergence check meaningless). `0` draws from the clock, as before this key existed. |

## `[Map]` — optional

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `MapProvider` | `Mapbox`\|`Bing`\|`OSM` | `Mapbox` | Visualizer background only. |
| `ZoomLevel` | int 0–20 | `13` | |

## `[Weather]` — optional

| Key | Type | Notes |
|-----|------|-------|
| `WeatherFile` | csv path | Hourly record in Open-Meteo format. Existence is checked. See [Companion files](#companion-file-formats). Set by the ELMFIRE case build to the case's own ERA5 archive. |
| `WeatherAnchorDateTime` | ISO 8601 | **Which moment in the record the simulation's start time reads from.** Unset means read at the scenario's own dates, as before this key. Set by the case build to the historical peak fire-weather day it drew, so the weather reported during a run is the weather the fire was computed against — a scenario dated 2020 may correctly report an August 2001 day. An offset rather than moving the scenario's dates, because response curves, evacuation orders and timed ignitions are all stated on the scenario's calendar. |
| `DesiredLatLon` | lat,lon | Optional. |
| `StartFFMC` | float | Fine fuel moisture code at the start. Default `85`. |
| `StartDMC` | float | Duff moisture code. Default `6`. |
| `StartDC` | float | Drought code. Default `15`. |
| `StartHourlyFFMC` | float | Hourly FFMC. Default `85`. |
| `StartKBDI` | float | Keetch-Byram drought index. Default `100`. |
| `MeanAnnualPrcp` | float (mm) | For the KBDI. Default `1000`. |

The six index seeds are here because they are weather, carried forward from the weather series whatever
fire module is running. They used to be read off the cell-based fire model's own section, so they were only
ever applied to a scenario using that module and silently defaulted everywhere else — and
`MeanAnnualPrcp` was not read at all, being hardcoded to 1500.

**What this section drives, and what it does not.** With ELMFIRE the fire's own behaviour comes entirely from
the case's raster series (`ws`/`wd`/`m1`/`m10`/`m100`) — this section does not affect spread. What it produces
is the temperature, humidity, hourly FFMC, FWI and KBDI reported in the output window. That used to be worth
almost nothing, because the in-process spread models that consumed it were removed and the values were read at
the scenario's own calendar date while the fire was computed against a historical day drawn out of ERA5 — two
unrelated days. `WeatherAnchorDateTime` is what ties them together.

Two limits worth knowing:

- **`DMC` and `DC` still start at their seeds.** They are meant to accumulate over weeks of antecedent
  weather, and nothing marches them over the record before the run begins — the code for it exists but is
  commented out. So the drought codes describe the seeds, not the month leading up to the sampled day.
- **Fire danger is reported, not used.** Nothing in the fire, the smoke or the trigger boundary reads it.

## `[Population]` — required if the pedestrian module is enabled

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `PopulationFile` | csv path | – | See [Companion files](#companion-file-formats). |
| `CullOutsideGroups` | bool | `false` | Drop households outside every evacuation group. |

## `[Demographics]` — repeatable

The first one defined is treated as the default. Referenced by name from
`[EvacuationGroup]`.

| Key | Type | Required | Default |
|-----|------|----------|---------|
| `Name` | string | ✔ | – |
| `AllowMoreThanOneCar` | bool | | `true` |
| `MaxCars` | int | | `2` |
| `MaxCarsProbability` | float | | `0.3` |
| `Default` | bool | | – |

## `[Evacuation]` — required if pedestrian or traffic is enabled

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `UseTriggerBufferEvacuation` | bool | `false` | |
| `TriggerBufferFile` | asc path | – | Required only when `UseTriggerBufferEvacuation=true`. |

## `[Destination]` — repeatable

| Key | Type | Required | Default | Notes |
|-----|------|----------|---------|-------|
| `Name` | string | ✔ | – | |
| `LatLon` | lat,lon | ✔ | – | |
| `Type` | `Exit`\|`Shelter` | ✔ | – | |
| `MaxFlow` | float (cars/hr) | | `-1` | `-1` = unlimited. |
| `MaxVehicles` | int | | `-1` | `-1` = unlimited. |
| `MaxPeople` | int | | `-1` | `-1` = unlimited. |
| `Blocked` | bool | | `false` | |
| `Color` | r,g,b (0–1) | | random | Visualizer only. |

## `[ResponseCurve]` — repeatable

Defines a cumulative departure-time distribution. After the keys, add **at least
two** bare `time,probability` data lines.

| Key | Type | Required | Notes |
|-----|------|----------|-------|
| `Name` | string | ✔ | |
| `TimeInput` | `Relative`\|`Absolute` | ✔ | `Relative` = seconds from start; `Absolute` = ISO datetime. |

```
[ResponseCurve]
Name=observed_average
TimeInput=Relative
0, 0
300, 0.037
1080, 0.67
6300, 1.0
```

Probability is cumulative and runs from 0 to 1.

## `[EvacuationGroup]` — repeatable

The first one defined is treated as the default.

| Key | Type | Required | Notes |
|-----|------|----------|-------|
| `Name` | string | ✔ | |
| `DestinationChoice` | enum | ✔ | `Random`, `ClosestEuclidean`, `EvacGroupCDF`, `EvacGroupClosestEuclidean`. |
| `Demographics` | name | | Must match a `[Demographics] Name`. |
| `Destinations` | name list | | Comma-separated destination names. |
| `DestinationsCDF` | float list | | Must have the same count as `Destinations`. |
| `ResponseCurves` | name list | ✔ | Comma-separated response-curve names. |
| `ResponseCurvesCDF` | float list | | Same count as `ResponseCurves`. |
| `ShapeFile` | .shp path | | Required when more than one group is defined. |
| `EvacuationOrderDateTime` | ISO 8601 | | Defaults to the simulation start. |
| `Default` | bool | | |
| `Color` | r,g,b | | Visualizer only. |

> An unrecognised `DestinationChoice` value causes the run to abort. Use exactly
> one of the four enum values above (case-sensitive).

## `[PedestrianModule]`

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `Enabled` | bool | `false` | |
| `Module` | `MacroHouseholdSim` | `MacroHouseholdSim` | The only pedestrian module. |

### `[MacroHouseholdSim]`

| Key | Type | Default |
|-----|------|---------|
| `WalkingSpeedMinMax` | min,max | `0.7,1.0` |
| `WalkingSpeedModifier` | float | `1.0` |
| `WalkingDistanceModifier` | float | `1.0` |

## `[TrafficModule]`

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `Enabled` | bool | `false` | |
| `Module` | `SUMO` | `SUMO` | The only traffic module. |
| `VisibilityAffectsSpeed` | bool | `false` | Smoke reduces vehicle speed. |

### `[SUMO]`

| Key | Type | Required | Default |
|-----|------|----------|---------|
| `ConfigurationFile` | .sumocfg path | ✔ | – |
| `OutputRasterSize` | double | | `25.0` |
| `SmokeAlpha` | float | | `0` |
| `SmokeBeta` | float | | `0` |

## `[WildfireModule]`

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `Enabled` | bool | `false` | |
| `Module` | `AscImport`\|`ELMFIRE` | – | Required when enabled. `ELMFIRE` runs ELMFIRE; `AscImport` reads a fire computed elsewhere. `ElmClone` — the removed cell-based model — is reported on load rather than silently mapped to either, since the two produce different fires. |
| `GraphicalFireInputFile` | path | – | Painted WUI area / ignition area / initial ignition, written by the fire paint window. Optional. |

### `[AscImport]` — when `Module=AscImport`

Imports pre-computed fire behaviour rasters (from FARSITE, FlamMap, Prometheus,
WISE, …). This is the recommended, validated fire module.

| Key | Type | Required | Notes |
|-----|------|----------|-------|
| `StartDateTime` | ISO 8601 | ✔ | Fire clock start. |
| `TimeOfArrivalFile` | asc path | ✔ | `TOA.asc`. |
| `RateOfSpreadFile` | asc path | ✔ | `ROS.asc`. |
| `SpreadDirectionFile` | asc path | ✔ | `SD.asc`. |
| `FirelineIntensityFile` | asc path | | `FI.asc`. |
| `FuelModelFile` | raster path | | **Display only.** What the output window's fuel model mode draws; nothing about the fire depends on it, since an imported fire arrives with its behaviour computed. Must be on the fire grid. Set automatically for an ELMFIRE fire from the case's own `fbfm40`/`fbfm13`. |
| `TimeOfArrivalUnits` | `Minutes`\|`Seconds` | | What the arrival times are measured in. Default `Seconds`. |

**`TimeOfArrivalUnits` cannot be inferred from the raster, and getting it wrong is silent** — the fire
arrives 60× early or late, which reads as a fire that barely moves or one that has already swept the
domain before the evacuation starts. So the reader logs which unit it used, whichever way it goes.

**Seconds is the default**: ELMFIRE writes seconds (`time_of_arrival` holds the simulation clock directly —
a 600 s run produces values up to 370.8), and seconds is what the simulation clock and everything
downstream work in. **FARSITE, FlamMap and Prometheus write minutes**, so a scenario importing one of those
must say `TimeOfArrivalUnits=Minutes`. The four examples that ship do, explicitly, rather than relying on a
default that could move under them.

### `[ELMFIRE]` — when `Module=ELMFIRE`

Runs ELMFIRE itself. Every key is optional — a scenario that says nothing but `Module=ELMFIRE` runs the
vendored build against an `elmfire/` case beside it.

ELMFIRE is a batch program: it computes a whole fire and writes rasters, so it cannot be stepped alongside
the evacuation. The module runs it once when the simulation starts and then reads its output through the
same reader `[AscImport]` uses, so an ELMFIRE fire and an imported one are the same thing from that point.
Output already in the case folder is reused, so the wait falls on the first run.

The namelist is **generated from the scenario** — see `[ElmfireNamelist]` below. A case that already has an
`elmfire.data` keeps it, since that is where physics gets tuned by hand; `RebuildExistingLayers`, or
deleting the file, is what lets regenerated settings reach it.

When a `NamelistTemplate` is named instead, the coupling only patches the few keys it must: `PATH_TO_GDAL`,
`SIMULATION_TSTOP`, and the dumps the reader needs (`DUMP_TIME_OF_ARRIVAL`, `DUMP_SPREAD_RATE`,
`DUMP_SPREAD_DIRECTION`, `SPREAD_RATE_IN_M`). Everything else stays the template's, and `[ElmfireNamelist]`
is not used at all.

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `CaseDirectory` | path | `elmfire` | Holds `inputs/`, `outputs/`, `scratch/` and the namelist. |
| `ElmfireExe` | path | vendored build | `ThirdParty/elmfire/build/windows/bin/elmfire.exe` when empty. **Override only — not offered in the GUI**, which reports which build was resolved instead. |
| `NamelistTemplate` | path | the case's `elmfire.data` | Resolved against the case directory first, then the scenario folder — the file picker writes scenario-relative paths, so `elmfire/mati.data` works. The console says which was used. |
| `SimulationTstopHours` | float | `8` | Hours of fire, converted to seconds for ELMFIRE's `SIMULATION_TSTOP`. Independent of the evacuation's end time. **The case needs at least this many hourly weather bands** or ELMFIRE refuses the run. Supersedes `SimulationTstopSeconds`, which is still read (and divided by 3600) so existing scenarios keep their duration. |
| `ReuseExistingOutput` | bool | `true` | Off means ELMFIRE runs again every simulation. |
| `BuildCase` | bool | `false` | Produce only the layers the case is **missing** — a DEM if it has none, then slope, aspect, adj/phi, weather, namelist. Safe on a prepared case. |
| `RebuildExistingLayers` | bool | `false` | Replace layers the case already has. Only needed when the domain or cell size changed; otherwise destructive — canopy with no source is refilled with zeros, and the namelist is rewritten. |
| `CellSizeMetres` | float | `30` | Master grid resolution, when building. |
| `PaddingMetres` | float | `2000` | Margin beyond the domain, so the fire is not clipped at its edge. |
| `PathToGdal` | path | – | GDAL bin directory. Located automatically from `PATH`, then a QGIS or OSGeo4W install, and ELMFIRE's own `PATH_TO_GDAL='auto'` resolves it from there. **Override only — not offered in the GUI**, which reports which install was resolved. Set it to force a particular one. |
| `WindNinjaExe` | path | – | **Override only — not offered in the GUI**, which reports what was found. `WindNinja_cli.exe`, for an install the probe does not find (`WINDNINJA_CLI`, then `PATH`, then `C:\WindNinja` and the `Program Files` roots). Used only while building. Without WindNinja the weather stage writes **one wind value across the whole domain**, which costs k-PERIL the terrain variation its spread ellipse is built from, so the trigger boundary comes out circular. The build reports which of the two it did. |

#### Source layers — read only while building

Fuel, canopy and buildings have no global source the builder can download, so they are named here. Any CRS and
any resolution: each is warped onto the case's master grid, **nearest-neighbour** for the categorical layers
(fuel models, masks — averaging model 1 and model 9 would give model 5, a fuel neither cell contains) and
bilinear for the continuous ones. Paths are relative to the scenario folder.

A layer the case already carries is **kept** rather than re-warped unless `RebuildExistingLayers` is on, so
naming a source is safe on a prepared case. A path that is set but does not resolve is reported by name and
that layer skipped.

| Key | Becomes | Notes |
|---|---|---|
| `FuelModelFile` | `fbfm40.tif` / `fbfm13.tif` | Which, per `FuelModelStandard`. Categorical. |
| `FuelModelStandard` | – | `FBFM40` (default, what LANDFIRE and the global products ship) or `FBFM13`. |
| `CanopyCoverFile` | `cc.tif` | Percent. Also shades Nelson's dead fuel sticks. |
| `CanopyHeightFile` | `ch.tif` | See `CH_TIMES_10` for the LANDFIRE scaled-integer trap. |
| `CanopyBaseHeightFile` | `cbh.tif` | See `CBH_TIMES_10`. |
| `CanopyBulkDensityFile` | `cbd.tif` | |
| `BuildingAreaFile` | `bldg_area_avg.tif` | |
| `BuildingSeparationFile` | `bldg_separation_distance.tif` | |
| `BuildingNonBurnableFractionFile` | `bldg_nonburnable_frac.tif` | |
| `BuildingFootprintFractionFile` | `bldg_footprint_frac.tif` | |
| `BuildingFuelModelFile` | `bldg_fuel_model.tif` | Categorical; pairs with `building_fuel_models.csv`. |
| `IgnitionMaskFile` | `ignition_mask.tif` | Categorical. A painted ignition area becomes this automatically. |
| `BarriersFile` | `barriers.tif` | Fuel breaks and other barriers to spread. |

Two silences worth knowing, both of which the scenario editor now states on screen:

- **Canopy absent is filled with zeros**, which means surface fire only and no crown fire. A case with no
  canopy builds and runs perfectly happily, so nothing else says so.
- **The building spread model needs all five** `Building*` layers. Four is the state worth watching: the
  layers are ingested and then not used, so `USE_BLDG_SPREAD_MODEL` stays off.

`PREACTcli build-case` reads these same keys, and an explicit `--cc`/`--fbfm40`/… flag overrides the scenario
rather than the other way round, so one layer can be tried against a scenario without editing it.

### `[ElmfireNamelist]` — when `Module=ELMFIRE`

The modelling choices the generated `elmfire.data` carries: canopy scaling, moisture, time stepping,
ignition mode, crown fire, spotting, building spread, and which output rasters to dump. Around a hundred
keys, all optional — a scenario without the section gets defaults that are ELMFIRE's own, except where the
case builder's rasters make another value the only correct one.

**Key names are ELMFIRE's, verbatim**, so a value can be checked against ELMFIRE's documentation without a
translation table, and the section reads like the namelist it produces. Edit them in the scenario editor's
Hazards > Fire behaviour tab, which groups them the way ELMFIRE's namelist groups them and can preview the resulting file.

Two keys are WUInity's rather than ELMFIRE's:

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `MeteorologyBands` | int | `0` | How many hourly weather bands to read. `0` counts them from the case's own `ws.tif`, which is what you want: too many fails the run, too few silently reuses hour one for the whole fire. Becomes `METEOROLOGY_BAND_STOP` / `NUM_METEOROLOGY_TIMES`. |
| `RANDOM_IGNITIONS` / `USE_IGNITION_MASK` | bool | `false` | Both are forced off when the scenario has `[IgnitionPoint]` sections: explicit coordinates win, and the namelist says so in a comment. |

Not in this section, deliberately: filenames, the grid, the time base, and the ignition coordinates. Those
are facts about the case rather than choices, and are read off the case at build time. Offering them here
would be offering the chance to disagree with the rasters, and a namelist that disagrees with its case
fails in ways that name the wrong thing.

Some keys are also written conditionally, because ELMFIRE's failure modes for the combinations are poor:

- The **ember outputs** (`DUMP_SPOTTING_OUTPUTS`, `ACCUMULATE_EMBER_FLUX`, `DUMP_EMBER_FLUX`,
  `DUMP_EMBER_IGNITION`) are written as `.FALSE.` whenever `ENABLE_SPOTTING` is off. With spotting off
  their arrays are never allocated and ELMFIRE reduces a null pointer across MPI, dying with
  `Fatal error in internal_Reduce: Invalid buffer pointer` — an error naming MPI, about a raster, caused
  by an output flag.
- **`USE_BLDG_SPREAD_MODEL`** is written as `.FALSE.` unless the case carries the complete set of five
  building rasters or `USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS` is on. A partial set makes ELMFIRE read a
  raster nobody produced.
- The four **ember model** keys (`GENERATION_MODEL`, `SPOTTING_DISTANCE_MODEL`, `ACCUMULATION_MODEL`,
  `IGNITION_MODEL`) are only written with `USE_SUPERSEDED_SPOTTING` off, which is the only state in which
  ELMFIRE reads them.

### `[IgnitionPoint]` — repeated, one per point

Where a fire starts. Placed in the ignition point editor and read by the ELMFIRE case builder, which
measures it in the case's own CRS. Independent of which fire module is selected.

| Key | Type | Required | Notes |
|-----|------|----------|-------|
| `LatLon` | lat,lon | ✔ | WGS84. Deliberately not projected coordinates - see `docs/elmfire-case-automation.md`. |
| `AbsoluteTime` | bool | | `true` to give a date and time rather than seconds. |
| `IgnitionTime` | float | | Seconds from the simulation start, when `AbsoluteTime=false`. |
| `IgnitionDateTime` | ISO 8601 | | When `AbsoluteTime=true`. The seconds are derived from it, so moving the simulation's start moves the ignition with it. |

## `[TriggerBufferModule]`

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `Enabled` | bool | `false` | |
| `Module` | `kPERIL` | – | k-PERIL is integrated but not fully tested. |

### `[kPERIL]`

| Key | Type | Required | Default |
|-----|------|----------|---------|
| `MidflameWindspeed` | float | ✔ | – |
| `OutputName` | string | ✔ | – |
| `WuiAreaFile` | raster path | | Mask of the protected WUI area (cell value `1` = WUI). Without it k-PERIL has no community to protect and the trigger boundary is empty. |

The rate of spread always comes from the fire module. `CalculateROSFromBehave`, `InitialFuelMoistureFile`
and `FuelModelsFile` are **gone**, with BEHAVE itself — see [Modules](modules.md). A scenario that still
carries them loads fine; the keys are ignored.

`WindSpeedFile`/`WindDirectionFile` are **filled in from the ELMFIRE case** (`inputs/ws.tif`, `inputs/wd.tif`)
when `Module=ELMFIRE` and they are not set here — the same wind the fire was computed with. Set them only
to override that.

An ELMFIRE case's wind rasters hold **one band per hour**. k-PERIL computes on a single wind field: its
solver has no time axis, and the wind enters it once, as the length-to-breadth ratio of the Huygens ellipse
at each cell. So the field is composed **per cell, from the band covering the hour the fire actually reached
that cell** — a property of the fire rather than a setting, which is why there is no key for it. Cells the
fire never reached take the last band; they lie ahead of the front, and k-PERIL evaluates the whole grid
rather than just the burned footprint. The run logs how many cells came from each band.

The band interval comes from `[ElmfireNamelist] DT_METEOROLOGY` for an ELMFIRE fire — the same value the fire
was computed with. For an imported fire nothing in the scenario states it, so hourly is assumed.

Building a case produces that series: one band per `DT_METEOROLOGY` step of `SimulationTstopSeconds`, each
from its own hour of the sampled historical day, with a WindNinja solve per band. All five weather rasters
(`ws`, `wd`, `m1`, `m10`, `m100`) always come out with the **same** band count, including when a stage falls
back to uniform values — ELMFIRE reads them all against one `NUM_METEOROLOGY_TIMES`.

This replaces `WindBand`, which chose one hour for the whole domain: an eight-hour burn was evaluated
entirely against its first hour, so a fire that swung 90° mid-run had its later half analysed against wind it
never saw. A `WindBand` key in an older scenario is ignored.

k-PERIL takes the required safe egress time (RSET / WRSET) from the run itself:
the **last-arrival evacuation time** (when the final vehicle reaches safety),
converted to minutes. It is not a `.wui` key.

Raster inputs (fire `.asc`/GeoTIFF, the WUI mask, topography) may be supplied as
either ESRI ASCII grids (`.asc`) or GeoTIFF (`.tif`/`.tiff`) — the format is
detected from the file extension.

## `[SmokeModule]`

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `Enabled` | bool | `false` | |
| `Module` | `GlobalSmoke` | `GlobalSmoke` | The only smoke module. |

### `[GlobalSmoke]`

| Key | Type | Required | Notes |
|-----|------|----------|-------|
| `ExtinctionFile` | .exc path | ✔ | Global extinction coefficient over time. |

## `[Events]` — optional

| Key | Type | Notes |
|-----|------|-------|
| `BlockGoalEventFiles` | file list | Comma-separated list of event files. |

---

## Legacy keys

These keys appear in some shipped examples but are **not read** by the current
parser. They are harmless (silently ignored) but do nothing — do not rely on
them:

| Key | Section | Replacement |
|-----|---------|-------------|
| `EvacuationOrderStart` | `[Evacuation]` | Per-group `EvacuationOrderDateTime`. |
| `MaxSimTime` | `[Simulation]` | `EndDateTime`. |
| `UTMoffset` | `[SUMO]` | Derived internally. |
| `RootFolder`, `WeatherStreamFile` | `[AscImport]` | Give paths relative to the `.wui` folder. |
| `GraphicalFireInputFile` | `[WildfireModule]` | (commented out in the parser). |

---

## Companion file formats

### Population CSV

Header row followed by one row per household:

```
OriginLat,OriginLon,AccessLat,AccessLon,People
```

- `OriginLat,OriginLon` – household (home) location.
- `AccessLat,AccessLon` – the vehicle's road-access point. This must lie on the
  road network, otherwise the vehicle is teleported to the nearest valid edge.
- `People` – number of people in the household (integer).

Generate this file with the [QGIS plugin](qgis-plugin.md) or
[`PREACTcli global-gpw-to-pop`](command-line-tools.md).

### Fire rasters (`[AscImport]`)

ESRI ASCII grids (`.asc`): `TOA.asc` (time of arrival), `ROS.asc` (rate of
spread), `SD.asc` (spread direction) and, optionally, `FI.asc` (fireline
intensity). WUI-NITY also accepts LCP and GeoTIFF landscape formats.

### Weather CSV

Three metadata rows (latitude, longitude, elevation), then a header row, then
hourly data: time, temperature, relative humidity, precipitation, wind speed,
wind direction, cloud cover, radiation, boundary-layer height and the derived
fire-weather indices (FFMC, DMC, DC, ISI, BUI, FWI).
