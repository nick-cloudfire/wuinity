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

## `[Map]` — optional

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `MapProvider` | `Mapbox`\|`Bing`\|`OSM` | `Mapbox` | Visualizer background only. |
| `ZoomLevel` | int 0–20 | `13` | |

## `[Weather]` — optional

| Key | Type | Notes |
|-----|------|-------|
| `WeatherFile` | csv path | Existence is checked. See [Companion files](#companion-file-formats). |
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

### `[ELMFIRE]` — when `Module=ELMFIRE`

Runs ELMFIRE itself. Every key is optional — a scenario that says nothing but `Module=ELMFIRE` runs the
vendored build against an `elmfire/` case beside it.

ELMFIRE is a batch program: it computes a whole fire and writes rasters, so it cannot be stepped alongside
the evacuation. The module runs it once when the simulation starts and then reads its output through the
same reader `[AscImport]` uses, so an ELMFIRE fire and an imported one are the same thing from that point.
Output already in the case folder is reused, so the wait falls on the first run.

The coupling patches a few keys into the namelist before running: `PATH_TO_GDAL`, `SIMULATION_TSTOP`, and
the dumps the reader needs (`DUMP_TIME_OF_ARRIVAL`, `DUMP_SPREAD_RATE`, `DUMP_SPREAD_DIRECTION`,
`SPREAD_RATE_IN_M`). The physics stays the template's.

| Key | Type | Default | Notes |
|-----|------|---------|-------|
| `CaseDirectory` | path | `elmfire` | Holds `inputs/`, `outputs/`, `scratch/` and the namelist. |
| `ElmfireExe` | path | vendored build | `ThirdParty/elmfire/build/windows/bin/elmfire.exe` when empty. |
| `NamelistTemplate` | path | the case's `elmfire.data` | Relative to the case directory. |
| `SimulationTstopSeconds` | float | `28800` | 8 h of fire. Independent of the evacuation's end time. |
| `ReuseExistingOutput` | bool | `true` | Off means ELMFIRE runs again every simulation. |
| `BuildCase` | bool | `false` | Produce only the layers the case is **missing** — a DEM if it has none, then slope, aspect, adj/phi, weather, namelist. Safe on a prepared case. |
| `RebuildExistingLayers` | bool | `false` | Replace layers the case already has. Only needed when the domain or cell size changed; otherwise destructive — canopy with no source is refilled with zeros, and the namelist is rewritten. |
| `CellSizeMetres` | float | `30` | Master grid resolution, when building. |
| `PaddingMetres` | float | `2000` | Margin beyond the domain, so the fire is not clipped at its edge. |
| `PathToGdal` | path | – | GDAL bin directory. **Normally leave empty** — the tools are located automatically and ELMFIRE's own `PATH_TO_GDAL='auto'` then resolves them. Set it only to force a particular install. |

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
| `CalculateROSFromBehave` | bool | | `true` |
| `InitialFuelMoistureFile` | `.fmc` path | | Required when `CalculateROSFromBehave=true`. |
| `FuelModelsFile` | `.fuel` path | | BEHAVE's fuel model table. Required when `CalculateROSFromBehave=true`. |
| `WindBand` | int | | Which hour of a multi-band wind raster to use, 1-based. Default `1`. |

`WindSpeedFile`/`WindDirectionFile` are **filled in from the ELMFIRE case** (`inputs/ws.tif`, `inputs/wd.tif`)
when `Module=ELMFIRE` and they are not set here — the same wind the fire was computed with. Set them only
to override that.

An ELMFIRE case's wind rasters hold **one band per hour**. k-PERIL computes on a single wind field: its
solver has no time axis, and the wind enters it once, as the length-to-breadth ratio of the Huygens ellipse
at each cell. So one hour is used and the rest are not — `WindBand` chooses which, and the run says out loud
how many bands the file had and which it took. It used to take band 1 silently.

Both are k-PERIL's own. They used to be taken from the fire module's section — so a BEHAVE-derived trigger
boundary depended on a fire module that has nothing to do with it, and the `InitialFuelMoistureFile`
declared here was never actually read. With `CalculateROSFromBehave=false` (the ELMFIRE-driven path) neither
is used.
| `WuiAreaFile` | raster path | | Mask of the protected WUI area (cell value `1` = WUI). Without it k-PERIL has no community to protect and the trigger boundary is empty. |

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
