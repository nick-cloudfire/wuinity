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

**P0.5 and P0.6 are done.** Painted fire areas are now saved, referenced by the scenario and loaded back;
ignition points are a first-class part of the `.wui` with a map-picking editor, and the case builder
measures them in the case's own CRS. Verified end to end: a scenario carrying
`LatLon=38.0441296693475,23.9465295359622` builds to `X_IGN(1) = 758569.98`, `Y_IGN(1) = 4214810.74` in
EPSG:32634 — the value the hand-repaired case needed — and ELMFIRE 1.1 ran that case to completion
(exit 0, 46.7 ac in 1 h), with the earliest-burning cell 41 m from the placed point. A second point at
10 N 10 E in the same scenario was reported as 526 km outside the domain and dropped rather than clamped.

**`Module=ELMFIRE` runs ELMFIRE.** It is a batch program, so the module runs it once when the simulation
starts and then reads the rasters back through the `AscImport` reader; output for an unchanged case is
reused. This supersedes P5's "Unity-side runner". The cell-based model that held this slot (`ElmClone`) has
been removed — see `docs/modules.md`.

### Where to pick up

**Open thread: an ELMFIRE run of the Mati case is failing and the cause is not yet known.** The symptom was
`Attempting to use an MPI routine (internal_barrier) before initializing or after finalizing MPICH`, which is
not the fault — every ELMFIRE error path is a bare Fortran `STOP` that skips `MPI_FINALIZE`, so Intel MPI
always says that on the way out. `ElmfireRunner` now mines `elmfire.log` for ELMFIRE's own diagnostic and
filters the MPI epilogue, so re-running should name the real problem. The two likely causes, neither ruled
out:

1. A raster the namelist references is missing from `inputs/`. Reproduced deliberately by deleting
   `fbfm40.tif`, which now reports `ERROR 4: ./inputs\fbfm40.tif: No such file or directory`.
2. **The fuel model stem mismatch, which is P1 and still outstanding.** `ElmfireCaseBuilder.BuildNamelist`
   hardcodes `FBFM_FILENAME = 'fbfm13'` and `RestrictIgnitionToBurnableFuel` reads `inputs/fbfm13.tif`, while
   the Mati case's fuel raster is `fbfm40.tif`. A hand-written namelist referencing `fbfm40` works; anything
   generated does not.

Also unfinished, and known: the painted masks in the Mati case are on the 616×590 @ 27.6 m landscape grid
while ELMFIRE's fire grid is 566×541 @ 30 m, so k-PERIL refuses the painted WUI area (correctly, with a
message). Repaint with `Module=ELMFIRE` selected and the painter will resolve the grid from ELMFIRE's arrival
time raster.

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
`ignition_mask` is `fbfm40 > 100` rather than painted — the painting path exists now, that particular
case's mask simply predates it.

## GDAL for ELMFIRE

ELMFIRE shells out to `gdal_translate`, `gdalinfo` and `gdalsrsinfo` **as programs**, which is a different
dependency from the GDAL PREACT itself uses: `Runtimes/Native/GDAL` holds only the SWIG bindings
(`gdal_wrap.dll` and friends), and **no GDAL executables ship with WUInity** — so there is nothing local to
point `PATH_TO_GDAL` at.

This ELMFIRE build resolves them itself. `build/source/elmfire_namelists.f90` defaults `PATH_TO_GDAL` to
`'auto'`, runs `where gdal_translate`, and prints `Auto-detected PATH_TO_GDAL: ...`. That only works if the
tools are on the PATH of the process running it, which on Windows they usually are not. So `GdalTools`
locates them — PATH, then the newest QGIS under Program Files, then OSGeo4W, then `SUMO_HOME` — and puts
them on the child's PATH, leaving ELMFIRE's own detection to succeed rather than overriding it with an
explicit key. `[ELMFIRE] PathToGdal` exists only to force a particular install, and `build-case` /
`converge-trigger` default `--gdal` the same way.

Getting this wrong is unusually hard to diagnose: with no GDAL reachable, ELMFIRE fails its startup CRS
check and reports **"DEM CRS does not appear to use metre linear units"** — an error about the DEM, when the
DEM is fine.

## Scenario folder layout

The data steps write into folders rather than dropping everything beside the `.wui`:

| Folder | Holds | Written by |
|---|---|---|
| *root* | the `.wui`, the population CSV, the weather CSV, the RouterDb, painted masks | steps, paint windows |
| `downloads/` | raw downloads before clipping or warping: the WGS84 DEM, WorldPop and its `_UTM` reprojection, the OSM extract | steps |
| `elmfire/inputs/` | the terrain the scenario runs on (`<name>_dem/_slope/_aspect.tif`), plus everything `ElmfireCaseBuilder` generates | steps, case builder |
| `canopy/` | reprojected canopy layers | external |
| `sumo/` | the SUMO network and configuration | `SumoNetworkBuilder` |

Recorded paths use forward slashes, since they are resolved on disk *and* stored in the `.wui`, and a
backslash written on Windows is not a separator anywhere else.

`ScenarioFileLocator` covers files that have since been moved: when the recorded path misses, the same
filename is looked for one level down in each folder above, and in the root. It warns which copy it used,
names any other candidate rather than choosing silently, and corrects the field - so saving the scenario
records the new path and the search stops being needed. The root is in that list for the reverse case: a
scenario written by the current steps, opened on a folder still laid out the old way. It is deliberately
not recursive; `_output`, `scratch` and `cache` hold results and intermediates, and "first file with this
name anywhere underneath" is as likely to find the wrong copy as the right one.

## Existing code, and what it needs

Most of the machinery exists in PREACTcore; almost none of it is reachable from Unity (zero
references from `WUInity/Assets`).

| Step | Code | Needs |
|---|---|---|
| Grid + warping | `RasterHarmonizer`, `MasterGrid` | square cells, force `AREA_OR_POINT=Area`, scrub non-finite |
| Slope/aspect | `SlopeAspect`, `GeoTiffRasterWriter` | non-square cell bug; aspect must not be bilinear |
| adj/phi | `GeoTiffRasterWriter.WriteConstant` | already correct |
| Weather | `WeatherRasterPipeline` (ERA5 -> WindNinja -> Nelson) | single-band -> N-band time series |
| Painted masks | `PaintedMaskExporter`, `GraphicalFireInput`, `FirePaintWindow` | **done** (P0.5) |
| Ignition point | `[IgnitionPoint]` sections, `IgnitionPointEditWindow`, `CrsTransform` | **done** (P0.6) |
| Namelist | `ElmfireNamelist.SetKeyInGroup`, `ElmfireCaseBuilder.BuildNamelist` | FBFM40, spotting, `&WUI`, ember keys, N bands |
| Case build | `ElmfireCaseBuilder` | GUI entry point |
| Run | `ElmfireRunner` (PREACTcli) | Unity-side runner |
| Ensemble | `ElmfireRealizationWriter`, `IgnitionSampler`, `converge-trigger` | GUI entry, aggregation |

## Plan

### P0.5 - persist painted fire masks (done)

The problem was that `GraphicalFireInput.SaveGraphicalFireInput()` had no callers and
`WildfireModuleInput.GraphicalFireInputFile` was commented out, so a WUI area, a random-ignition area
and an initial ignition could all be painted into `WildfireModule.Data.*` and none of it ever reached
disk. There was also nothing in the GUI that opened those three paint modes at all. Which is why
`ElmfireCaseBuilder.ApplyPaintedMasks()` had never run: it reads a file nothing produced.

What now exists:

- **`FirePaintWindow`** (Run/edit > Hazards > Paint fire areas): the three modes, add/erase brush, and a
  Save button. Not disabled with the wildfire module, because the WUI area matters to a scenario whose
  fire is imported.
- **`GraphicalFireInputFile`** is a live scenario field again - written by the reflection-based writer,
  parsed, and loaded by `WildfireData.LoadAll` whatever the module is and whether or not a fire is
  modelled. That last part also makes `EvacuationManager`'s existing fallback to `Data.WuiArea` for
  k-PERIL's protected area reachable for the first time.
- **The grid is written into the file** rather than taken from the landscape. Masks are painted on
  whichever raster the painter resolved, so a scenario with an imported fire - which has no
  `LandscapeData` at all - used to throw on save. `WildfireData.PaintedCellCount` carries the grid the
  loaded masks are on, and the painter says so plainly when it no longer matches.
- A **checklist entry** (`PREACTInput.OptionalInputMissing`, non-critical) for a scenario with nothing
  painted, because otherwise the case builder's ignite-anywhere fallback and k-PERIL's no-WUI-area both
  look like deliberate choices.
- A missing `.gfi` **keeps its reference** instead of clearing it. Every other file field in these
  parsers clears; here clearing is silent data loss, since the writer omits empty values and the masks
  were painted by hand.

**The ignition mask is the user's to paint.** `RestrictIgnitionToBurnableFuel` then *intersects* the
painted mask with burnable fuel; it is not the mask.

### P0.6 - one ignition point from the GUI (done)

Ignition points existed as a type and were drawn as markers, but could only arrive from a CSV named by
the legacy `[ElmClone] IgnitionPointsFile`, only for that module, with no editor - and
`ElmfireCaseBuilder` ignored them entirely. So an ELMFIRE ignition had to be written into the namelist
by hand, which is how the Mati case acquired zone-35 coordinates in a zone-34 case.

What now exists:

- **`[IgnitionPoint]` sections** in the `.wui`, repeated like `[Destination]` and `[EvacuationGroup]`,
  parsed on the module rather than in the `ElmClone` sub-section - an ignition is not that module's
  property. They replace what the legacy CSV loaded, and say so, so which is in effect is never in
  question. Round-trip verified through writer and parser, including the absolute/relative time split
  (`IgnitionTime` is derived from `IgnitionDateTime`, so moving the scenario's start moves the ignition
  with it).
- **`IgnitionPointEditWindow`**: map picking, lat/lon at full precision, relative seconds or an absolute
  date, and marker refresh via `WUInityManager.RefreshWildfireIgnitionMarkers`. The snap is to the
  **fire grid cell centre**, not to a road: ELMFIRE resolves `X_IGN`/`Y_IGN` to a cell and ignites the
  whole of it, so which cell it landed in is the question worth answering - and a point one cell into
  the sea becomes visible before the run.
- **`CrsTransform.TryWgs84To`** and `ElmfireCaseBuilder.ApplyIgnitionPoints`: the transform happens once,
  in code, against the CRS of the case grid that actually exists - not the scenario's, not a raster's,
  not the zone the domain corner falls in. A point outside the domain is dropped with both its
  coordinates and the domain's stated, never clamped: clamping would ignite a fire nobody asked for and
  the run would look successful.
- `NUM_IGNITIONS` = however many were placed, with `X_IGN(i)`/`Y_IGN(i)`/`T_IGN(i)`, and
  `RANDOM_IGNITIONS`/`USE_IGNITION_MASK` off. Explicit points supersede a painted initial ignition, with
  a line saying which was used.
- `PREACTcli build-case` reads the sections out of the `.wui` with the same local parse it uses for the
  domain, so the CLI honours them without a full scenario load.

### P1 - `sample.data` as a patched template — **next**

Promote the corrected `mati.data` to a repo `sample.data` and drive it through
`ElmfireNamelist.SetKeyInGroup` instead of `BuildNamelist()`'s string list, so the template owns the
physics and WUInity only patches keys. Extend `ElmfireNamelistKeys` with the spotting, `&WUI`, ember,
`CBD_TIMES_100` / `*_TIMES_10` keys.

**Start with the FBFM40 stem**, which is the most likely cause of the ELMFIRE failure noted under Status:
`CategoricalStems` knows `fbfm40`, but `BuildNamelist` hardcodes `FBFM_FILENAME = 'fbfm13'` and
`RestrictIgnitionToBurnableFuel` reads `inputs/fbfm13.tif`. The Mati case's raster is `fbfm40.tif`, so a
generated namelist points at a file that is not there while the hand-written one works. The builder already
patches the keys it owns into an existing namelist (`ElmfireCoupling.PatchNamelist`), so the shape to follow
exists.

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

**Each realization simulates three days** (`converge-trigger --tstop`, default 259200 s). Much longer than a
single case needs, and deliberately so: a realization only contributes to the boundary if its fire reaches
the community, ignitions are drawn from across the whole domain, and the ones started furthest away are
exactly the ones that decide how far out the boundary must sit. A run cut short does not merely lose those
realizations — it counts them as fires that did not threaten the town, and the boundary comes out too tight.

**No boundary is produced for an area the fire never reached.** Checked per WUI area against the arrival
times (`WildfireModule.CellHasBurned`), so with per-group areas one group can get a boundary and another
correctly not. A boundary computed for an unreached area would look like every other boundary, making a
case whose fire went the other way — or whose run stopped too early — indistinguishable from one that was
genuinely threatened. For single cases this is the expected outcome rather than an error, which is why the
message names both possibilities.

Open decisions: whether the ensemble varies ignition only or ignition **and** weather (that decides
whether `RASTER_TO_PERTURB` blocks belong in the template), and the master cell size (30 m matches
Copernicus GLO-30 and the builder default; 25 m preserves the case's original ~27.6 m detail).

## Data questions for the external pipeline

- `ff_h` (building footprint fraction) maxes at **2**, not 1.
- `bfm_h` (building fuel model) holds codes up to **256**, while `building_fuel_models.csv` defines 15
  rows (0-14).
- The case has only ~250-490 m of margin beyond the evacuation domain (`EDGEBUFFER = 60`);
  `ElmfireCaseBuilder` defaults to 2000 m because a fire is free to burn outside the domain.
