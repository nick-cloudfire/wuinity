# The `.wui` input file format

A scenario is one plain-text file with the `.wui` extension, plus the files it names. This page lists every
section and key the parser (`PREACT/PREACTcore/Source/Input/`) reads, with its type, default and whether a run
can start without it. The GUI writes these files for you; this page is for reading them, editing them by hand
and scripting them.

A complete example is [`Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui`](../Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui).

## Syntax

- **Sections** start with a header in square brackets, `[Simulation]`, and run to the next header. Their order in
  the file does not matter.
- **Keys** are `Key=Value`, split on the first `=`, so a value may contain `=`. Spaces are removed from the key;
  the value is only trimmed, so `C:/Program Files/QGIS 3.44.2/bin` survives.
- **Comments**: a line whose first non-blank character is `#`, and anything after a `#` that follows whitespace.
  A `#` inside a word (a URL, a file name) is kept.
- **Numbers and dates** are read with the invariant culture: `.` for decimals whatever the machine's locale.
  Dates are ISO 8601, `2021-06-29T12:00:00`.
- **Booleans** are `true`/`false`. **Lists** are comma-separated and each element is trimmed (`a, b`).
  **Coordinates** are `lat,lon` in WGS84 degrees; **sizes** are `x,y`.
- **Repeatable sections** — `[Destination]`, `[ResponseCurve]`, `[EvacuationGroup]`, `[Demographics]`,
  `[IgnitionPoint]` — may appear any number of times. Any other section given twice is reported and only the
  first is read (and saved).
- **A key given twice** in one section is reported; the first value is used and the second is dropped on save.
- **Paths** are relative to the folder holding the `.wui` and are written with `/`. A `\` is accepted on every
  platform. A file that has moved is looked for, by name, one folder down from each folder above where it
  was expected and in the scenario folder; the scenario check says which copy it used, and saving records the
  new path.
- Each section is read on its own: a problem in one is reported against that section and the rest of the file
  is still read.
- **A key or section the parser does not know**, a retired one, and a line that is not `Key=Value` are reported
  once when the file is read — with the line, how many times it occurs, and the closest known name when there is
  one (`[Simulation] Deltatime (line 5) is not a key of [Simulation] - did you mean DeltaTime?`). They are
  ignored, never critical, and not written when the scenario is saved. Keys are case-sensitive.

## Required, optional and critical

The scenario check (Scenario > Check scenario, or the list printed when a `.wui` loads) reports everything
missing or wrong. An item is **critical** when the run cannot start without it; `PREACT.exe`, the GUI's Run and a
campaign all refuse a scenario with a critical item. Everything else is a note: a default applies, or the thing
is optional.

In the tables, **Critical** means critical when the section's module is enabled. Sections of a module that is
switched off are still read in full and kept when the scenario is saved, but nothing in them is critical.

## What a save writes

The GUI writes the file back from what it read (`PREACTInputWriter`):

- a section that only restates its defaults is left out, unless a module sub-section follows it;
- the sub-section of the **selected** module is always written — `[AscImport]` or `[ELMFIRE]` for the fire,
  `[GlobalSmoke]` for smoke, `[kPERIL]` for the trigger boundary — and the section of a module option that is
  not selected is written too when it holds anything but defaults, so switching modules loses no setting;
- `[ElmfireNamelist]` is written when the fire module is `ELMFIRE`, or when it holds anything but defaults;
- paths are written with `/`; retired keys and sections are not written.

A module's sections are read whether or not it is enabled, whichever option it selects, and even when its own
header (`[WildfireModule]`, `[TrafficModule]`, ...) is missing: without the header the module is off.

---

## `[Simulation]` — always required

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `Name` | text | – | yes | Names every output file, and the campaign folder. |
| `LowerLeftLatLon` | lat,lon | – | yes | South-west corner of the evacuation domain. |
| `DomainSize` | x,y metres | – | yes | Width (east) and height (north); both > 0. |
| `StartDateTime` | date | – | yes | Local civil time at the domain (its time zone is looked up from the coordinates). |
| `EndDateTime` | date | – | yes | Local time. Not after the start: a warning, and the run ends at once. |
| `DeltaTime` | seconds | `1.0` | – | Time step; must be > 0. SUMO's step length is set to it. |
| `StopWhenEvacuated` | bool | `false` | – | End the run once everyone has arrived. |
| `RandomSeed` | int | `0` | – | Seeds departure times, walking speeds, household sizes and destination choice. Combined with the run index, so run *n* of a multi-run is reproducible on its own. `0` seeds from the clock. A campaign sets its own per realization. |

The simulation is measured in the UTM zone of its south-west corner, unless a georeferenced raster says
otherwise: the imported fire's arrival-time raster (`[AscImport] TimeOfArrivalFile`) first, else the
`[Landscape]` reference raster. For an ELMFIRE scenario that is the case's `dem.tif` once the case is built.

## `[Map]` — optional

The visualizer's background; nothing in a run depends on it.

| Key | Type | Default | Notes |
|---|---|---|---|
| `MapProvider` | `Mapbox` \| `Bing` \| `OSM` | `Mapbox` | |
| `ZoomLevel` | int 0–20 | `13` | |

## `[Landscape]` — optional

The terrain (and, for a FARSITE-style landscape, fuels and canopy). Every key is optional; a named file that is
missing is a note, and its path is kept so it is not lost on save. For an ELMFIRE scenario, building the case
points the three terrain keys at the case's `dem.tif`, `slp.tif` and `asp.tif`, which are then the grid
everything is painted and computed on. Fuel and canopy for ELMFIRE are named under `[ELMFIRE]`, not here.

| Key | Type | Notes |
|---|---|---|
| `LandscapeFile` | path | A multiband GeoTIFF in LANDFIRE band order (elevation, slope, aspect, fuel model, canopy cover, canopy height, canopy base height, canopy bulk density), or a FARSITE `.lcp`. When set, the band keys below are ignored. An `.lcp` carries no CRS. |
| `ElevationFile` | raster | Metres. |
| `SlopeFile` | raster | Degrees. Computed from the elevation when not given. |
| `AspectFile` | raster | Degrees clockwise from north, downhill direction. Computed from the elevation when not given. |
| `FuelModelFile` | raster | Fuel model number per cell. |
| `CanopyCoverFile` | raster | Percent. |
| `CanopyHeightFile`, `CanopyBaseHeightFile`, `CanopyBulkDensityFile` | raster | All three or none: with only one or two given, none is used (a warning). |

k-PERIL reads its slope and aspect from here; a missing band is treated as flat ground and said.

## `[Weather]` — optional

The hourly weather reported during a run: temperature, humidity, wind and the fire-danger indices. **It does not
drive the fire** — an ELMFIRE fire spreads with the case's own weather rasters — nor the evacuation or the
trigger boundary.

| Key | Type | Default | Notes |
|---|---|---|---|
| `WeatherFile` | path | – | Hourly CSV in the Open-Meteo layout ([below](#weather-csv)). Not set: no weather is reported and nothing is downloaded. Set but missing, or not covering the run: a run downloads what it needs from Open-Meteo and caches it. Building an ELMFIRE case points it at the case's ERA5 archive. |
| `WeatherAnchorDateTime` | date | unset | The moment in the record (UTC) that the simulation's start reads from, so a scenario dated today reports the historical day its fire was computed against. Set by the case build. Unset: the record is read at the scenario's own dates, at the UTC hour its local clock stands for (13:00 at Mati in June reads the record's 10:00). |
| `StartFFMC` | number | `85` | Fine fuel moisture code at the first local noon. |
| `StartDMC` | number | `6` | Duff moisture code. |
| `StartDC` | number | `15` | Drought code. |
| `StartHourlyFFMC` | number | `85` | Hourly FFMC. |
| `StartKBDI` | number | `100` | Keetch–Byram drought index. |
| `MeanAnnualPrcp` | mm | `1000` | For the KBDI. |

The daily codes advance at 12:00 local standard time (from the longitude, not the civil time zone) with the rain
of the preceding 24 hours and the wind in km/h — the same derivation the ERA5 archive uses. DMC and DC start
from the seeds above; nothing marches them over the weeks before the run.

## `[Population]` — required when the pedestrian module is enabled

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `PopulationFile` | path | – | yes | Households CSV ([below](#population-csv)). |
| `CullOutsideGroups` | bool | `false` | – | Remove households that lie in no evacuation group's area. |

## `[Demographics]` — repeatable, optional

Referenced by name from `[EvacuationGroup] Demographics`. The first one is the default unless another says
`Default=true`. Nothing here is critical: a group whose demographics are missing uses the default ones. A
section without `Name` is ignored with a warning.

| Key | Type | Default | Notes |
|---|---|---|---|
| `Name` | text | – | |
| `AllowMoreThanOneCar` | bool | `true` | |
| `MaxCars` | int ≥ 1 | `2` | |
| `MaxCarsProbability` | 0–1 | `0.3` | |
| `Default` | bool | first section | |

## `[Evacuation]` — optional, holds no current keys

The destinations, response curves and groups are sections of their own. The retired keys
`UseTriggerBufferEvacuation`, `TriggerBufferFile` and `EvacuationOrderStart` are ignored with a notice and not
written. With the traffic module on, at least one `[Destination]` is critical; with the pedestrian module on, at
least one `[ResponseCurve]` and one `[EvacuationGroup]` are.

## `[Destination]` — repeatable

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `Name` | text | – | yes | A section without one is ignored (and reported). |
| `LatLon` | lat,lon | – | yes | |
| `Type` | `Exit` \| `Shelter` | `Exit` | – | |
| `MaxFlow` | cars/h | `-1` | – | `-1` is unlimited. Applied as a rate of `MaxFlow`/3600 cars per second with up to one minute's worth in reserve; a car arriving over it is sent round again. |
| `MaxVehicles` | int | `-1` | – | `-1` is unlimited. |
| `MaxPeople` | int | `-1` | – | `-1` is unlimited. |
| `Blocked` | bool | `false` | – | |
| `Color` | r,g,b 0–1 | random | – | Display only. |

## `[ResponseCurve]` — repeatable

A cumulative distribution of when households start to leave. `Name` and `TimeInput`, then at least two bare
`time,probability` rows (lines without `=`), in any order among the keys, with comments allowed.

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `Name` | text | – | yes | |
| `TimeInput` | `Relative` \| `Absolute` | `Relative` | – | An unreadable value is critical. |

- **Relative** rows are seconds after the group's `EvacuationOrderDateTime`.
- **Absolute** rows are dates (`2021-06-30T17:30:00,0.0`) on the scenario's own calendar; the order time is not
  added to them.
- Rows must rise in both time and probability; probabilities lie in 0–1. The first row is the curve's start
  (probability 0 in effect). A curve that ends below 1 is allowed and warned about: the rest never leave.

```
[ResponseCurve]
Name=observed_average
TimeInput=Relative
0, 0
300, 0.037
1080, 0.67
6300, 1.0
```

## `[EvacuationGroup]` — repeatable

Every household belongs to one group, by its home's position in the group's area. The first group is the default
unless another says `Default=true`; households in no group's area join the default group, unless
`[Population] CullOutsideGroups=true` removes them.

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `Name` | text | – | yes | |
| `EvacuationOrderDateTime` | date | the simulation start | – | When the group is ordered out. |
| `DestinationChoice` | `Random` \| `ClosestEuclidean` \| `EvacGroupCDF` \| `EvacGroupClosestEuclidean` | `EvacGroupCDF` | – | |
| `Destinations` | name list | – | traffic on, and a choice that draws from the group's list (`EvacGroupCDF`, `EvacGroupClosestEuclidean`) | Names of `[Destination]`s. A name that does not exist is kept and reported. |
| `DestinationsCDF` | number list | even split | traffic on and `EvacGroupCDF`, with more than one destination | Cumulative, one per destination, rising from 0 to 1. A step of 0 (e.g. `1,1`) means that destination is never chosen — warned about. A CDF ending below 1 sends the rest to the last destination. |
| `ResponseCurves` | name list | – | pedestrian on | Names of `[ResponseCurve]`s. |
| `ResponseCurvesCDF` | number list | even split | pedestrian on, with more than one curve | As `DestinationsCDF`. |
| `Demographics` | name | the default demographics | – | |
| `MaskFile` | raster | – | pedestrian on, when named | The group's area, as painted in the GUI (any positive value is inside). Wins over `ShapeFile`. |
| `ShapeFile` | `.shp` | – | pedestrian on, when named and there is no `MaskFile` | The group's area as a polygon. With more than one group, each needs a `MaskFile` or `ShapeFile`. |
| `Default` | bool | first section | – | |
| `Color` | r,g,b | random | – | Display only. |

## `[PedestrianModule]`

| Key | Type | Default | Notes |
|---|---|---|---|
| `Enabled` | bool | `false` | |
| `Module` | `MacroHouseholdSim` | `MacroHouseholdSim` | The only one. |

### `[MacroHouseholdSim]` — optional

Every key is optional; an unreadable value keeps the default and says so.

| Key | Type | Default | Notes |
|---|---|---|---|
| `WalkingSpeedMinMax` | min,max m/s | `0.7,1.0` | Each household's walking speed is drawn between them. |
| `WalkingSpeedModifier` | > 0 | `1.0` | Multiplies walking speed. |
| `WalkingDistanceModifier` | > 0 | `1.0` | Multiplies the walk from home to the car. |
| `ReactToFire` | bool | `true` | A household that has not left yet leaves as soon as the fire front comes within `FireReactionDistance` of its home, instead of waiting for its drawn response time. Households whose drawn response is "never" stay. |
| `FireReactionDistance` | metres ≥ 0 | `500` | Distance from home to the nearest burning cell (arrival time ≤ now). |
| `FireReactionUpdateInterval` | seconds > 0 | `300` | How often the distance to the front is recomputed. |

## `[TrafficModule]`

| Key | Type | Default | Notes |
|---|---|---|---|
| `Enabled` | bool | `false` | |
| `Module` | `SUMO` | `SUMO` | The only one. With traffic on, a `[SUMO]` section is critical. |

`VisibilityAffectsSpeed` is no longer read; smoke acts through `[SUMO] SmokeAlpha/SmokeBeta`.

### `[SUMO]`

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `ConfigurationFile` | `.sumocfg` path | – | yes | The GUI's Roads step writes `sumo/osm.sumocfg`. The network must be in the simulation's UTM zone; one in another zone is reported as a warning at load. |
| `OutputRasterSize` | metres > 0 | `25` | – | Cell size of the traffic raster output. |
| `SmokeAlpha` | 0–1 | `0` | – | Speed factor in smoke: `1 − SmokeAlpha·exp(SmokeBeta / K)`, K the extinction coefficient (1/m), clamped to 0.05–1. `0` switches it off. |
| `SmokeBeta` | number (< 0) | `0` | – | |

## `[WildfireModule]`

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `Enabled` | bool | `false` | – | Missing or unreadable means off. A missing one is a notice the workflow panel shows as a warning ("Not set, so the fire module is off and the scenario runs without a fire ...", and the same for the other modules), since a section without it may well have meant the module to run; a save writes `Enabled=false`. The rest of the section, the `[IgnitionPoint]`s and the module's sections are read (and kept) either way. |
| `Module` | `ELMFIRE` \| `AscImport` | – | yes, when enabled | `ELMFIRE` runs ELMFIRE on the scenario's case; `AscImport` reads a fire computed elsewhere. `None` with the module enabled is critical. `ElmClone`/`CellSpread` (the removed cell-based model) are reported as removed. |
| `GraphicalFireInputFile` | `.gfi` path | – | – | The painted WUI area, ignition area and initial ignition ([below](#painted-areas-gfi)). Kept even when the file is missing, so a save does not lose the reference. |

### `[IgnitionPoint]` — repeatable

Fixed ignitions for a single run, placed with Fire > Ignition points. The case build measures them in the case's
own CRS; a point outside the case grid is dropped and reported, never moved to the edge. They win over a painted
initial ignition, and switch ELMFIRE's random ignition off. A campaign ignores them: each realization draws its
own ignition.

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `LatLon` | lat,lon | – | yes | WGS84, so the point means the same ground whatever grid the case ends up on. |
| `AbsoluteTime` | bool | `false` | – | |
| `IgnitionTime` | seconds | `0` | – | After the simulation start, when `AbsoluteTime=false`. |
| `IgnitionDateTime` | date | – | yes, when `AbsoluteTime=true` | The seconds are derived from it, so moving the scenario's start moves the ignition with it. |

### `[AscImport]` — when `Module=AscImport`

A fire computed elsewhere — FARSITE, FlamMap, Prometheus, an ELMFIRE run by hand. ESRI ASCII (`.asc`) or GeoTIFF;
all on one grid. A campaign writes this section into every realization's scenario.

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `StartDateTime` | date | – | yes | The moment arrival time 0 corresponds to. |
| `TimeOfArrivalFile` | raster | – | yes | Also decides the simulation's UTM zone when it carries a CRS (a GeoTIFF, or an `.asc` with a `.prj`). |
| `TimeOfArrivalUnits` | `Seconds` \| `Minutes` | `Seconds` | – | Cannot be read from the raster. ELMFIRE writes seconds; FARSITE, FlamMap and Prometheus write minutes. Wrong is silent: the fire arrives 60 times early or late. |
| `RateOfSpreadFile` | raster | – | yes | m/min. |
| `SpreadDirectionFile` | raster | – | yes | Degrees clockwise from north, the direction of spread. |
| `FirelineIntensityFile` | raster | – | – | kW/m; reads as 0 without it. |
| `MidflameWindSpeedFile` | raster | – | – | Midflame wind in **ft/min** (as ELMFIRE's `mfws_*.tif`). k-PERIL uses it, converted to mi/h, when set; without it, it falls back to `[kPERIL] WindSpeedFile` and warns. |
| `FuelModelFile` | raster | – | – | Read and kept; nothing uses it. |

### `[ELMFIRE]` — when `Module=ELMFIRE`

How ELMFIRE is run. Every key is optional: a scenario that only says `Module=ELMFIRE` runs the ELMFIRE build in
the repository on a case in an `elmfire/` folder beside the `.wui`. What has to exist (the executable, the case,
its rasters) is checked when the run starts. [ELMFIRE cases](elmfire-cases.md) describes the case this builds.
The section is read whatever the fire module is, because it also describes the case that Data > Build fire case,
`PREACTcli build-case` and a campaign build. A value that cannot be read (`SimulationTstopHours=24h`) is reported
and keeps its default.

| Key | Type | Default | Notes |
|---|---|---|---|
| `CaseDirectory` | folder | `elmfire` | Holds `inputs/`, `outputs/`, `scratch/`, `climatology/` and the namelist. |
| `ElmfireExe` | path | the repository's build | `WUInity/Assets/ThirdParty/elmfire/build/windows/bin/elmfire.exe` (`build/linux/bin/elmfire` on Linux). Must be built from a7fb9d6 or later. |
| `NamelistTemplate` | path | – | A namelist to run as it is, instead of the one generated from `[ElmfireNamelist]`. Looked for in the case folder, then beside the `.wui`. Only the stop time, `PATH_TO_GDAL`, the weather band keys (fitted to the case's `ws.tif`), the required outputs and the fuel tables are written into it. |
| `SimulationTstopHours` | hours | `8` | How long the fire burns, 1 to 240; independent of the evacuation's end. The GUI gives a new scenario its time window (at least 24 h). A legacy `SimulationTstopSeconds` is read (÷ 3600) when this key is absent. |
| `CellSizeMetres` | metres | `30` | Case grid resolution. |
| `PaddingMetres` | metres | `2000` | Margin around the evacuation domain, so the fire is not clipped at its edge. |
| `ReuseExistingOutput` | bool | `true` | Reuse the case's `outputs/` when the namelist, the executable and every input are the same by content as the run that wrote them. |
| `BuildCase` | bool | `false` | Build the case before each run. The GUI's Build fire case does it on demand instead. |
| `RebuildExistingLayers` | bool | `false` | Replace every layer the case already has (re-warp sources, redraw the weather). Canopy with no source named is refilled with zeros. |
| `PathToGdal` | folder | found | GDAL tools for ELMFIRE (`gdal_translate`, `gdalinfo`, `gdalsrsinfo`). Found on `PATH`, in QGIS, OSGeo4W or `SUMO_HOME`. |
| `WindNinjaExe` | path | found | `WINDNINJA_CLI`, `PATH`, `C:\WindNinja`, Program Files (on Linux `WindNinja_cli` on `PATH`, `/opt/WindNinja`, `/usr/local/WindNinja`, `~/WindNinja`). |
| `LandfireVersion` | text | `closest` | The LANDFIRE release the fuels step downloads: `closest` to the start year, or `LF2016`, `LF2022`, `LF2023`, `LF2024`, `LF2025`. Only the download reads it. An unknown value keeps `closest` and says so. |
| `CanopyDatasetFolder` | folder | – | The FIRE-RES pan-European canopy rasters (`panEu_canopyCover.tif`, `panEu_canopyHeight.tif`, `panEu_cbh.tif`, `panEu_cbd.tif`), for any canopy layer not named below. Real units: the LANDFIRE scaling flags are forced off. |

**Source layers.** Fuel, canopy and buildings have no global download, so they are named here. Any CRS and
resolution: each is warped onto the case grid when the case is built, nearest-neighbour for class layers and
bilinear for continuous ones. A layer the case already has is kept unless `RebuildExistingLayers` is on. A path
that does not resolve is warned about at load and skipped at build. The GUI's LANDFIRE download (US) fills the
fuel and canopy keys and the four canopy unit flags of `[ElmfireNamelist]`.

| Key | Case stem | Notes |
|---|---|---|
| `FuelModelFile` | `fbfm40` or `fbfm13` | Class layer. |
| `FuelModelStandard` | – | `FBFM40` (Scott & Burgan 40, default) or `FBFM13` (Anderson 13). |
| `CanopyCoverFile` | `cc` | Percent. |
| `CanopyHeightFile` | `ch` | See `CH_TIMES_10`. |
| `CanopyBaseHeightFile` | `cbh` | See `CBH_TIMES_10`. |
| `CanopyBulkDensityFile` | `cbd` | See `CBD_TIMES_100`. |
| `BuildingAreaFile` | `bldg_area_avg` | The building spread model needs all five building layers. |
| `BuildingSeparationFile` | `bldg_separation_distance` | |
| `BuildingNonBurnableFractionFile` | `bldg_nonburnable_frac` | |
| `BuildingFootprintFractionFile` | `bldg_footprint_frac` | |
| `BuildingFuelModelFile` | `bldg_fuel_model` | Class layer; pairs with `building_fuel_models.csv`. |
| `IgnitionMaskFile` | `ignition_mask` | Class layer. A painted ignition area replaces it. |
| `BarriersFile` | `barriers` | Fuel breaks. |
| `SuppressionDifficultyFile` | `sdi` | For `USE_SDI`. |
| `LandValueFile` | `land_value` | For `USE_LAND_VALUE`. |
| `PopulationDensityFile` | `population_density` | For `USE_POPULATION_DENSITY`. |
| `RealEstateValueFile` | `real_estate_value` | For `USE_REAL_ESTATE_VALUE`. |
| `EnergyReleaseComponentFile` | `erc` | For `USE_ERC`. |
| `PyromesFile` | `pyromes` | For `USE_PYROMES` and the per-pyrome tables. |

Without canopy the case gets zero canopy — surface fire only, no crown fire — and the build says so.

### `[ElmfireNamelist]` — when `Module=ELMFIRE`

The modelling choices the generated `elmfire.data` carries: 255 of ELMFIRE's own keys, **named exactly as in
ELMFIRE's namelist** (so a value can be checked against ELMFIRE's documentation), plus one of WUInity's. All are
optional; without the section, ELMFIRE's defaults apply except where the case makes another value the only
correct one. Edit them on the Fire > Fire behaviour (namelist) page, which groups them as ELMFIRE does and
disables what the current choices make inert; Fire > Preview namelist shows the file they produce. The Fire model
settings page's **Read this case into the editor** fills them from a case's existing namelist. A value that
cannot be read is reported on the checklist and keeps its default; it does not stop the run.

Groups covered: `&INPUTS`, `&OUTPUTS`, `&SIMULATOR`, `&TIME_CONTROL`, `&MONTE_CARLO`, `&SPOTTING`,
`&SUPPRESSION`, `&SMOKE`, `&CALIBRATION`, `&WUI`, `&MISCELLANEOUS`. Frequently changed:

| Key | Default | Notes |
|---|---|---|
| `DT_METEOROLOGY` | `3600` | Seconds per weather band. The case's weather is written at this interval. |
| `MeteorologyBands` | `0` | WUInity's. Weather bands the generated `elmfire.data` says to read; `0` counts them from the case's `ws.tif`, which is what you want. A run always reads every band the case's weather has: it sets `NUM_METEOROLOGY_TIMES` from `ws.tif`, as a campaign does per realization. |
| `WX_BANDS_KEPT_IN_MEM` | `30` | Weather bands ELMFIRE holds at once; lower saves memory on long fires (at least 2). |
| `SIMULATION_DT`, `SIMULATION_DTMAX`, `TARGET_CFL` | `5`, `300`, `0.4` | Time stepping. |
| `DTDUMP` | `3600` | Seconds between raster dumps. |
| `LH_MOISTURE_CONTENT`, `LW_MOISTURE_CONTENT` | `60`, `90` | Live herbaceous and woody moisture, percent (a campaign draws its own). |
| `FOLIAR_MOISTURE_CONTENT` | `90` | |
| `CC_IN_PERCENT`, `CH_TIMES_10`, `CBH_TIMES_10`, `CBD_TIMES_100` | `true`, `false`, `false`, `false` | Canopy units. LANDFIRE stores height ×10 and bulk density ×100: the LANDFIRE download switches the three scaling flags on; FIRE-RES canopy forces them off. |
| `ENABLE_SPOTTING` | `true` | With it off, the ember outputs are written off too (ELMFIRE otherwise crashes in MPI). |
| `CROWN_FIRE_MODEL` | `1` | |
| `USE_BLDG_SPREAD_MODEL` | – | Written on only when the case has all five building layers (or constant building parameters are on). |
| `RANDOM_IGNITIONS`, `USE_IGNITION_MASK` | `false` | Forced off when the scenario has `[IgnitionPoint]`s. |
| `SEED` | `2024` | A campaign sets it per realization. `RANDOMIZE_RANDOM_SEED` is refused (it would make every realization's seed meaningless). |
| `MAX_RUNTIME` | `999999` | Wall-clock seconds; a campaign sets its own limit. |

Not settings, and not in this section, because they are facts about the case: the filenames, the grid, the time
base (`CURRENT_YEAR`, `HOUR_OF_YEAR`), the band count, the ignition coordinates and `SIMULATION_TSTOP` (from
`SimulationTstopHours`). Every run forces `DUMP_TIME_OF_ARRIVAL`, `DUMP_SPREAD_RATE`, `DUMP_SPREAD_DIRECTION`,
`SPREAD_RATE_IN_M` and `DUMP_MIDFLAME_WINDSPEED` on, whatever this section or a template says.

## `[SmokeModule]`

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `Enabled` | bool | `false` | – | |
| `Module` | `GlobalSmoke` | `None` | yes, when enabled | |

### `[GlobalSmoke]`

| Key | Type | Critical | Notes |
|---|---|---|---|
| `ExtinctionFile` | `.exc` path | yes | The extinction coefficient over the whole domain over time ([below](#extinction-ramp-exc)). |

## `[TriggerBufferModule]`

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `Enabled` | bool | `false` | – | |
| `Module` | `kPERIL` | `None` | yes, when enabled | With the boundary on, a `[kPERIL]` section is critical. |

### `[kPERIL]`

k-PERIL computes the trigger boundary once the evacuation has run, from the fire's rate of spread and direction,
the wind, the case's slope and aspect, and the required safe egress time — the run's own last-arrival time, in
minutes. No boundary is computed for an area the fire never reached. See [Modules](modules.md#trigger-boundary).

| Key | Type | Default | Critical | Notes |
|---|---|---|---|---|
| `WuiAreaSource` | `Raster` \| `EvacuationGroupsCombined` \| `EvacuationGroupsSeparate` | `Raster` | – | What k-PERIL protects: the WUI raster, the union of the evacuation groups' areas, or one boundary per group. A campaign refuses `EvacuationGroupsSeparate`. |
| `WuiAreaFile` | raster | – | yes, when named and missing | The WUI area (1 = protected). Building the ELMFIRE case writes `elmfire/inputs/wui_area.tif` from the painted WUI area and points this at it. Without it, an ELMFIRE run uses its case's `wui_area.tif`, and failing that the painted WUI area if it is on the fire grid. |
| `OutputName` | text | `trigger_boundary` | – | Names the output: `_output/<run index>_<OutputName>`, with `.asc` added when it has no extension, and the group's name before the extension when there is one boundary per group. |
| `WindSpeedFile` | raster, **mi/h** | – | yes, when named and missing | Fallback only. With an ELMFIRE fire, or `[AscImport] MidflameWindSpeedFile`, the fire's own midflame wind is used and this is ignored. Otherwise it is used *as* midflame wind, with a warning. |
| `WindDirectionFile` | raster, degrees (from) | – | yes, when named and missing | For an imported fire. An ELMFIRE fire's own weather direction is used instead and this is ignored with a warning. Multi-band: each cell takes the band covering the time the fire reached it. |
| `WindBandSeconds` | seconds | `3600` | – | Seconds per band of the two wind rasters, for an imported fire. An ELMFIRE fire supplies its own. |

`WindBand` (one hour for the whole domain) is no longer read.

---

## Retired keys and sections

Tolerated when read, reported once, and not written again:

| Where | What | Now |
|---|---|---|
| `[Events]` | the whole section | Never implemented; removed. |
| `[WUIShow]` | the whole section | The streaming output was removed. |
| `[Evacuation]` | `UseTriggerBufferEvacuation`, `TriggerBufferFile`, `EvacuationOrderStart` | Never read by the engine. Use each group's `EvacuationOrderDateTime`. |
| `[Weather]` | `DesiredLatLon` | Read but never used; ignored. |
| `[Weather]` | `HasWeatherAnchor` | Only restated whether `WeatherAnchorDateTime` is set. |
| `[TrafficModule]` | `VisibilityAffectsSpeed` | Ignored; smoke acts through `[SUMO] SmokeAlpha/SmokeBeta`. |
| `[SUMO]` | `UTMoffset` | Ignored; the SUMO network carries its own offset. |
| `[kPERIL]` | `WindBand` | Ignored; the band is chosen per cell from the arrival time. |
| `[kPERIL]` | `MidflameWindspeed`, `CalculateROSFromBehave`, `InitialFuelMoistureFile`, `FuelModelsFile` | Ignored; the rate of spread always comes from the fire module. |
| `[ELMFIRE]` | `SimulationTstopSeconds` | Read (÷ 3600) when `SimulationTstopHours` is absent; the next save writes hours. |
| `[ELMFIRE]` | `IgnitionPointsFile` | The CSV is not read; ignition points are `[IgnitionPoint]` sections (Fire > Ignition points). |
| `[SimpleWildfireCA]`, `[ElmClone]`, `[CellParticleHybrid]`, `[FireCell]` | the whole section | The cell-based fire models were removed; use `ELMFIRE`, or `AscImport` for a fire computed elsewhere. |
| `[Behave]`, `[Rothermel]` | the whole section | The rate of spread always comes from the fire module. |
| `[AdvectDiffuse3D]`, `[AdvectDiffuseMixingLayer]` | the whole section | Removed; `GlobalSmoke` is the smoke module. |
| `[CityFlow]`, `[MacroTrafficSim]` | the whole section | Removed; `SUMO` is the traffic module. |
| `[WildfireModule]` | `Module=ElmClone` / `CellSpread` | The cell-based model is gone; choose `ELMFIRE` or `AscImport`. |

---

## Companion files

### Population CSV

A header row, then one household per row:

```
OriginLat,OriginLon,AccessLat,AccessLon,People
39.3829238878821,-105.042008820747,39.38234,-105.0416,5
```

`Origin` is the home; `Access` is where the household's car joins the road network (a point off the network is
moved to the nearest edge); `People` is an integer. Blank lines are skipped; a malformed line is reported by
number. The GUI's Population step (WorldPop to households) and
[`PREACTcli global-gpw-to-pop`](command-line-tools.md#global-gpw-to-pop--population-csv-from-gpw) write it.

### Weather CSV

The layout Open-Meteo downloads are written in: three metadata rows, a header row, then one row per hour.

```
Latitide,50.22847
Longitude,-121.579
Elevation,254
Time,Temperature_2m [°C],Relativehumidity_2m [%],Precipitation [mm],Windspeed_10m [m/s],Winddirection_10m [°],Cloudcover [%],Direct_radiation [W/m²],Boundary_layer_height [m],FFMC hourly [-],FFMC [-],DMC [-],DC [-],ISI [-],BUI [-],FWI [-]
2021-01-01T00:00,6.2,71,0,1.34,207,97,3,150,84.96,0,0,0,0,0,0
```

Times are UTC. The case's ERA5 archive (`<case>/climatology/<Name>_era5_hourly.csv`) is the same layout with
`archive_format=3` appended to the header line, marking codes derived at local noon from km/h wind and 24 h rain,
all year, with the drought codes' day lengths for the archive's latitude; an older archive is re-derived once, from
its own hourly columns, when it is next read.

### Extinction ramp (`.exc`)

A header line, then `time,extinction coefficient` rows — seconds from the start, and 1/m — linearly
interpolated:

```
time, ext. coeff. [1/m]
0.0, 0.2
360000, 0.2
```

### Fire rasters

ESRI ASCII grids (`.asc`, header keywords in any order, `xllcorner`/`xllcenter`, optional `NODATA_value`) or
GeoTIFFs; the format is taken from the extension. An `.asc` has no CRS unless a `.prj` sits beside it; without
one it is assumed to be in the simulation's zone, with a warning. ELMFIRE's arrival times are seconds and its
rates of spread m/min (with `SPREAD_RATE_IN_M`); FARSITE/FlamMap arrival times are minutes.

### Painted areas (`.gfi`)

Written by Fire > Fire areas. A binary file: the grid's column and row counts, then one byte per cell for each of
four masks (WUI area, random-ignition area, initial ignition, and a trigger-buffer mask nothing uses), rows
running north. It may end with a trailer recording the grid's south-west corner, cell size and EPSG code; without
it a painting is matched to a grid by its size alone. An ELMFIRE scenario is painted on the case's `dem.tif`; a
painting made on another grid is moved onto it with step 6's **Move painting onto the fire-case grid**, which
writes `<name>_<W>x<H>.gfi` beside the original — see [ELMFIRE cases](elmfire-cases.md#painted-areas).

### Evacuation group masks

Written by Evacuation > Paint group areas as `evac_group_<name>.asc`, one per group, and named by that group's
`MaskFile`. They are placed on the fire grid by cell, in simulation coordinates; they carry no georeference of
their own.
