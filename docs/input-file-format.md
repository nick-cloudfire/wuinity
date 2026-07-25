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
- **All spaces are stripped from every line before parsing.** This means
  `Key = Value` and `a, b` are fine, but a **value may not contain spaces** —
  file paths and names with spaces will break.
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
| `Module` | `AscImport`\|`ElmClone` | – | Required when enabled. Use `AscImport`; `ElmClone` is the in-development ELMFIRE scaffold. |

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
| `InitialFuelMoistureFile` | path | | Required when `CalculateROSFromBehave=true`. |
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
