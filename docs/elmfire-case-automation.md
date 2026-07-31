# Automating ELMFIRE case preparation in WUInity

Goal: a `sample.data` template that WUInity patches, plus a "Prepare ELMFIRE case" step that writes
every raster the template references, so one run and then an ensemble can be launched from the GUI.
Fuel, canopy and building layers stay external and are only ingested.

Reference case: `D:/WUINITY/cases/mati_generated` (Mati, Attica, EPSG:32634).

## Status

**P0 (make one case run) is done, but it was done by hand.** The scripts lived in a scratchpad, not in
the repo: gdalwarp/gdaldem/gdal_create for the rasters, a numpy pass for NaN, and a hand-written
`elmfire/mati.data`. ELMFIRE 1.1 (`WUInity/Assets/ThirdParty/elmfire/build/windows/bin/elmfire.exe`)
then ran it to completion: exit 0, 724.6 ac / 293 ha burned in 8 h, outputs verified in QGIS.

P0 proves the input contract and the template. It does **not** mean WUInity can reproduce any of it.

## What P0 established

The input contract every layer must satisfy, and which the automation has to guarantee:

- **One grid.** 566 x 541 cells of **30 m square**, EPSG:32634, origin (750300, 4222320),
  `AREA_OR_POINT=Area`. ELMFIRE takes domain, CRS and cell size from `DEM_FILENAME`, so the DEM is the
  grid of record and nothing may disagree with it.
- The case originally had **three** grids: slope/aspect were square-celled and 14 m south of
  everything else, and the DEM was tagged `RasterPixelIsPoint` (a half-cell shift). `gdalwarp`
  compensates for the Point tag on read but *carries it through to the output*, so it has to be
  cleared explicitly or the shift returns on the next read.
- **Aspect cannot be resampled bilinearly.** Interpolating across the 359->0 wrap gives 180 - a
  south-facing slope where the ground faces north. Recompute slope/aspect from the harmonized DEM.
- **Non-finite values abort the run.** The canopy rasters carried NaN in ~23% of cells (inherited
  from source products with no nodata declared). ELMFIRE compiles with FP traps, so one NaN gives
  `forrtl: error (65): floating invalid` with a traceback pointing at an unrelated line
  (`elmfire.f90:505`, Mode-2 code the run never enters). Ingestion must scrub non-finite values;
  0 is correct for canopy voids.
- **Ignition coordinates must be in the case CRS.** The case's were `(232043.4, 4215113.9)` - zone
  **35** coordinates in a zone **34** case. Mati sits on the 24 E boundary, so the same point is
  232 km east in zone 35 and 758 km east in zone 34; as written the ignition was half a zone outside
  the domain. Correct value: `(758570.0, 4214810.7)`.
- **`WS_AT_10M` must match the wind source.** It was `.FALSE.` against Open-Meteo 10 m wind, so
  ELMFIRE read the numbers as 20 ft wind. `WS_FILENAME` is mph regardless of height.
- **Time base must agree with the weather rasters.** `CURRENT_YEAR` / `BAND_ONE_HOUR_OF_YEAR` said
  2018 / hour 4888 (the real fire) against 2026 rasters; now 2026 / 4284, matching the scenario's
  `StartDateTime` and the eight hours the rasters hold.

Canonical stems written to `elmfire/inputs`: `dem slp asp fbfm40 cc ch cbh cbd adj phi ws wd m1 m10
m100 ignition_mask baa ssd nbf_h ff_h bfm_h`. Weather is 8 hourly bands.

Placeholders still in the case: `m10`/`m100` are `m1` + 1.5 / 3.0 (P4 replaces them with Nelson), and
`ignition_mask` is `fbfm40 > 100` rather than painted (P0.5).

## Existing code, and what it needs

Most of the machinery exists in PREACTcore; almost none of it is reachable from Unity (zero
references from `WUInity/Assets`).

| Step | Code | Needs |
|---|---|---|
| Grid + warping | `RasterHarmonizer`, `MasterGrid` | square cells, force `AREA_OR_POINT=Area`, scrub non-finite |
| Slope/aspect | `SlopeAspect`, `GeoTiffRasterWriter` | non-square cell bug; aspect must not be bilinear |
| adj/phi | `GeoTiffRasterWriter.WriteConstant` | already correct |
| Weather | `WeatherRasterPipeline` (ERA5 -> WindNinja -> Nelson) | single-band -> N-band time series |
| Painted masks | `PaintedMaskExporter`, `GraphicalFireInput` | nothing saves the file (see P0.5) |
| Namelist | `ElmfireNamelist.SetKeyInGroup`, `ElmfireCaseBuilder.BuildNamelist` | FBFM40, spotting, `&WUI`, ember keys, N bands |
| Case build | `ElmfireCaseBuilder` | GUI entry point |
| Run | `ElmfireRunner` (PREACTcli) | Unity-side runner |
| Ensemble | `ElmfireRealizationWriter`, `IgnitionSampler`, `converge-trigger` | GUI entry, aggregation |

## Plan

### P0.5 - persist painted fire masks (blocker for everything ignition-related)

`GraphicalFireInput.SaveGraphicalFireInput()` has **no callers**, and
`WildfireModuleInput.GraphicalFireInputFile` is **commented out** (lines 26, 106-113). A user can
paint a WUI area, a random-ignition area and an initial ignition - they land in
`WildfireModule.Data.*` - but nothing writes them to disk, the scenario has no field to reference
them, and `WildfireData.LoadGraphicalFireInput` is never called. Evacuation groups have their own
`SaveMasks()`; the fire masks never got one.

Consequence: `ElmfireCaseBuilder --painted` expects a file nothing produces, so
`ApplyPaintedMasks()` - the code that turns the painted random-ignition area into `ignition_mask.tif`,
the painted WUI area into `wui_area.tif`, and a painted initial ignition into `X_IGN`/`Y_IGN` - is
currently unreachable.

- Save button in the fire paint windows calling `SaveGraphicalFireInput`.
- Re-enable `GraphicalFireInputFile`: write it, parse it, load it when the scenario opens.
- Checklist entry so an unpainted case says so.

**The ignition mask is the user's to paint.** `RestrictIgnitionToBurnableFuel` then *intersects* the
painted mask with burnable fuel; it is not the mask.

### P0.6 - one ignition point from the GUI

Today there is no supported way to place a single ignition for ELMFIRE:

- `Painter.PaintMode.InitialIgnition` exists and `PaintedMaskExporter.TryGetIgnitionPoint` turns it
  into `X_IGN`/`Y_IGN`, but it cannot be saved (P0.5).
- `WildfireModule.Data.IgnitionPoints` (`IgnitionPointInput`: LatLon, absolute/relative time) is read
  only from a CSV named by the legacy `[FireCell] IgnitionPointsFile`, and only when the module is
  `ElmClone`. There is no editor for it - the GUI only draws markers for it
  (`SpawnWildfireIgnitionMarkers`).
- `ElmfireCaseBuilder` ignores `Data.IgnitionPoints` entirely.

Work: an ignition-point editor with map picking (like the destination editor, including snapping and
marker refresh), persisted in the scenario, and read by `ElmfireCaseBuilder` so a single-scenario run
gets `NUM_IGNITIONS=1` + `X_IGN`/`Y_IGN`/`T_IGN` in the case CRS - the transform being exactly what
the case got wrong by hand.

### P1 - `sample.data` as a patched template

Promote the corrected `mati.data` to a repo `sample.data` and drive it through
`ElmfireNamelist.SetKeyInGroup` instead of `BuildNamelist()`'s string list, so the template owns the
physics and WUInity only patches keys. Extend `ElmfireNamelistKeys` with the spotting, `&WUI`, ember,
`CBD_TIMES_100` / `*_TIMES_10` keys; promote FBFM40 to a first-class stem (`CategoricalStems` knows
it, but `BuildNamelist` hardcodes `fbfm13` and `RestrictIgnitionToBurnableFuel` reads
`inputs/fbfm13.tif`).

### P2 - grid discipline and a validator

Square cells and `AREA_OR_POINT=Area` in `RasterHarmonizer`; `MasterGrid` to carry x and y cell size
or refuse non-square; NaN scrub on ingest. A `ValidateCase()` that compares every referenced
raster's geotransform, CRS, size and finiteness and lists missing stems - turning silent
misregistration into an error message.

### P3 - ingest the external layers

Scenario fields (an `[Elmfire]` section or `[Landscape]` extensions) for
`fbfm40 / cc / ch / cbh / cbd / bldg_*`, warped onto the master grid at prep time: nearest for
categorical, bilinear for continuous. The `canopy/` and `buildings_*` products become named inputs
instead of files placed by hand.

### P4 - weather

N-band series from the hourly weather CSV; WindNinja terrain wind and Nelson dead fuel moisture where
available, with a clear report when skipped. Removes the `m10`/`m100` placeholder.

### P5 - Unity wiring

"Prepare ELMFIRE case" beside the existing data steps, calling `ElmfireCaseBuilder`; a Unity-side
runner (the `elmfire-windows-runtime` branch); then import the result by switching the scenario to
`AscImport` with `TimeOfArrivalFile` set automatically. Closes the loop: fire -> evacuation ->
k-PERIL.

### P6 - ensemble

Drive it with `ElmfireRealizationWriter` + per-realization `outputs/`/`scratch/` (resumable,
parallel-safe, already what `converge-trigger` does) rather than ELMFIRE's internal
`NUM_ENSEMBLE_MEMBERS`, because k-PERIL needs per-realization time-of-arrival rasters. Ignitions drawn
by `IgnitionSampler` inside the painted mask intersected with burnable fuel; weather drawn from
`ClimatologySampler`. Aggregate to burn probability and mean/percentile arrival time.

Open decisions: whether the ensemble varies ignition only or ignition **and** weather (that decides
whether `RASTER_TO_PERTURB` blocks belong in the template), and the master cell size (30 m matches
Copernicus GLO-30 and the builder default; 25 m preserves the case's original ~27.6 m detail).

## Data questions for the external pipeline

- `ff_h` (building footprint fraction) maxes at **2**, not 1.
- `bfm_h` (building fuel model) holds codes up to **256**, while `building_fuel_models.csv` defines 15
  rows (0-14).
- The case has only ~250-490 m of margin beyond the evacuation domain (`EDGEBUFFER = 60`);
  `ElmfireCaseBuilder` defaults to 2000 m because a fire is free to burn outside the domain.
