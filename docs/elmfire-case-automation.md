# Automating ELMFIRE case preparation in WUInity

Goal: a `template.data` that WUInity patches, plus a "Prepare ELMFIRE case" step that writes
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

**The generated Mati case runs.** `build-case` from the scenario's own `.wui`, then `elmfire.exe` on the
namelist it wrote: exit 0, no errors, `time_of_arrival` / `vs` / `spread_dir` / `flin` all written. Two
separate faults were behind the run that would not start, and both are fixed:

1. **GDAL never reached the child process.** `ElmfireRunner` put the GDAL bin directory on the child's
   `PATH`, but Windows spells the variable `Path` and whether `psi.Environment["PATH"] = …` replaces that
   entry or adds a second one beside it depends on the runtime — .NET Core keys that dictionary
   `OrdinalIgnoreCase`, Mono keys it ordinally. Under Unity's Mono the child inherited both, the original
   won, and ELMFIRE's `where gdal_translate` found nothing. **This is why the same code worked from
   PREACTcli and not from the GUI**, and it is worth remembering for any other process this codebase
   starts. `ElmfireRunner.SetEnvironmentVariable` now drops case-differing duplicates first, and
   `PATH_TO_GDAL` is written into the namelist as well, so the run no longer depends on environment
   inheritance alone.
2. **The fuel model stem was hardcoded.** `BuildNamelist` asked for `fbfm13` and the case carries
   `fbfm40.tif`, so `FBFM_FILENAME` was emitted *as a comment* and ELMFIRE stopped at its own
   `CHECK_FILEPATH_IS_SET` before MPI came up. `ElmfireCaseBuilder.ResolveStem` now resolves the stem
   against the case, preferring `fbfm40`, and `Result.FuelStem` carries the answer to everything that
   needs it. The five building layers had the same mismatch — the builder's descriptive names against the
   `baa`/`ssd`/`nbf_h`/`ff_h`/`bfm_h` the external pipeline writes — and resolve the same way, which is
   what switches `USE_BLDG_SPREAD_MODEL` on for a case that has them.

Two consequences worth knowing. `RestrictIgnitionToBurnableFuel` had been reading the same missing
`fbfm13.tif` and so had been silently doing nothing; it now restricts (98816 of 135776 cells on the padded
Mati domain, 73 %). And **a namelist the case already has is kept, not rebuilt** — deliberately, since that
is where the physics is tuned — so a case built by the older builder keeps its broken `elmfire.data`.
`WarnIfNamelistLacksFuelModel` now says so and names the raster to point at; deleting the namelist, or
turning `RebuildExistingLayers` on, gets a correct one written.

**P1 is now done** - the namelist is generated in full from the scenario's `[ElmfireNamelist]` section,
including `&SPOTTING`, `&WUI`, the ember and crown outputs, the scaling flags, and a meteorology band count
counted from the case's own weather rasters rather than hardcoded. See P1 under Plan.

**All 323 namelist keys are now covered**, with their dependencies enforced rather than documented, and the
result runs. See P1c under Plan for what that turned up — including four spotting settings that are arrays
over the fuel models and had been silently applying to fuel model 0 alone, and three switches that were
writing runs ELMFIRE refuses to start.

**A fixed ignition point is now a single-run thing only.** `[IgnitionPoint]` is what one named fire through
WUInity means, and it still is; a campaign overrides it per realization and draws the ignition out of the
ignition mask instead. That was the decision the previous version of this section was waiting for, and it is
made: a campaign whose every realization starts in the same cell produces that one fire's boundary at
probability 1 — on the Mati domain, a cell whose fire misses the WUI area by 2.21 km — however many
realizations are averaged. Both behaviours now come from one template, so nothing has to be kept in step by
hand. `ElmfireNamelist.ForceRandomIgnition` is the whole of it; the four keys it has to set together, and
what each one silently does when it disagrees, are in
`docs/probabilistic-trigger-convergence.md` under "Ignition is overridden per realization".

Doing that turned up a second break of the same kind: `MISCELLANEOUS_INPUTS_DIRECTORY` is written `'./inputs'`
relative to the case root, and a realization runs in its own directory — so every generated realization died
with "Problem opening fuel model table file ./inputs\fuel_models.csv". It is now resolved absolute.

### Units ELMFIRE does not declare

**`time_of_arrival` is in SECONDS** — `elmfire_level_set.f90` assigns the simulation clock straight into it,
and nothing scales it on the way out. The `AscImport` reader that WUInity reads an ELMFIRE fire back through
was written for FARSITE/FlamMap/Prometheus `.asc`, which use **minutes**, and multiplied by 60
unconditionally. So every ELMFIRE fire arrived 60× too late and barely moved across an evacuation, with
nothing anywhere saying so. `[AscImport] TimeOfArrivalUnits` now states it, and **seconds is the default** —
ELMFIRE is what produces the fires this platform runs, and seconds is what the simulation clock and
everything downstream work in. The four shipped `.asc` examples pin `Minutes` explicitly rather than rely on
a default that has now moved once. Verified against the raster: a 600 s run writes arrival times up to
370.8, which as minutes would be 6.2 hours of fire in a ten-minute run.

Two other units in the same class, both already handled but worth keeping together:
`SPREAD_RATE_IN_M` (m/min if set, ELMFIRE's default ft/min if not — the reader wants m/min), and
`WS_FILENAME`, which is mph regardless of `WS_AT_10M`.

### ELMFIRE errors that name the wrong thing

Four now, all found by running it:

1. **"DEM CRS does not appear to use metre linear units"** - GDAL was unreachable, and the DEM is fine.
2. **A run reporting "complete, fire area N acres" that wrote no raster at all** - the wrong PROJ database
   was ahead of GDAL on PATH. Exit code 0.
3. **"Fatal error in internal_Reduce: Invalid buffer pointer"**, with a count that happens to equal the
   cell count - an ember output key (`ACCUMULATE_EMBER_FLUX` and friends) was left on with
   `ENABLE_SPOTTING` off, so the array it dumps was never allocated. An error naming MPI, about a raster,
   caused by an output flag. The namelist builder now gates the four ember keys on spotting.
4. **`forrtl: severe (157): Program Exception - access violation` in `LEVEL_SET_PROPAGATION`**, always a
   few hours after `UPDATED WEATHER SLICE TO [...]` - the rolling weather window, and nothing to do with
   the level set. See "The rolling weather window" below.

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
locates them — PATH, then the newest QGIS under Program Files, then OSGeo4W, then `SUMO_HOME` — and both
puts them on the child's PATH *and* writes the directory into `PATH_TO_GDAL`. Two mechanisms rather than
one, because the environment half is runtime-dependent (see "Where to pick up") and when it silently failed
the error blamed the DEM. Searching PATH first means the answer agrees with what `where` would have found,
so writing the key does not override ELMFIRE's judgement, only its need to exercise it. `[ELMFIRE]
PathToGdal` forces a particular install, and `build-case` / `converge-trigger` default `--gdal` the same way.

**No trailing separator.** ELMFIRE appends `PATH_SEPARATOR` itself once it has a directory
(`elmfire_namelists.f90:71`), so a path written with one gives it `bin/\gdalinfo`.

Getting this wrong is unusually hard to diagnose, in two different ways:

- With **no GDAL reachable at all**, ELMFIRE fails its startup CRS check and reports **"DEM CRS does not
  appear to use metre linear units"** — an error about the DEM, when the DEM is fine.
- With **GDAL reachable but the wrong PROJ database ahead of it on PATH**, it is worse. SUMO ships a PROJ 8
  database and is on PATH on any machine set up for WUInity's traffic half, so `gdalsrsinfo` fails with
  `proj.db contains DATABASE.LAYOUT.VERSION.MINOR = 4 whereas a number >= 5 is expected`, ELMFIRE falls back
  to `-a_srs UNKNOWN`, every `gdal_translate` refuses it, and the run **still exits 0** having deleted its
  own `.bil` intermediates. Reproduced deliberately: a case that reported "complete, fire area 2.0 acres"
  wrote three CSVs and not one GeoTIFF. This is what `PROJ_DATA`/`PROJ_LIB` in
  `ElmfireRunner.ApplyGdalEnvironment` are for, and why they are not optional tidying.

## Which namelist a run uses

`ElmfireCoupling.ResolveNamelist` picks, in order: `[ELMFIRE] NamelistTemplate` if the scenario names one;
then `elmfire.data`; then the only `*.data` in the case folder, since a hand-prepared case is usually named
after the town. The last step needs there to be exactly one.

That order has a trap worth knowing. A case that has been built *and* hand-prepared holds both
`elmfire.data` and, say, `mati.data`, and the generated one wins silently — the hand-corrected namelist is
never read and the run fails for reasons that were fixed in the file sitting beside it.

There is a second, sharper edge: **`elmfire.data` is also where `ElmfireRunner` writes the patched
namelist** (`ElmfireRunner.cs`), so in the GUI path — where the run directory *is* the case directory — it
is overwritten on every run. That contradicts `ElmfireCaseBuilder`, which deliberately keeps an existing
`elmfire.data` on the grounds that it is where the physics is tuned. Hand-edits to `elmfire.data` do not
survive a run; hand-edits to a file named by `NamelistTemplate` do. The ensemble path is unaffected, since
each realization has its own directory. Giving the runner its own filename would settle it.

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

- **`FirePaintWindow`** (Scenario > Edit > Hazards > Ignitions and areas): the three modes, add/erase brush, and a
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

### P1 - the namelist from the scenario — **done**

`ElmfireNamelistInput` holds the modelling choices as ~100 fields **named after ELMFIRE's keys verbatim**,
in its own `[ElmfireNamelist]` section of the `.wui`; `ElmfireNamelistBuilder` turns them plus the case's
own contents into a complete `elmfire.data`. The Hazards > Fire behaviour tab edits them in nine collapsing sections
mirroring ELMFIRE's namelist groups, with a preview of the file they produce.

The naming is a deliberate break from this codebase's PascalCase. A setting called `NEMBERS_MIN` is worth
nothing unless it is obvious which key it becomes, and a rename between the two is a defect nobody would
spot in review.

The split that matters is **choice versus fact**. The settings are choices. Filenames, which optional
layers exist, the grid, the time base, the weather band count and the ignition coordinates are facts about
the case, read off it at build time and not offered as settings — offering them would be offering the
chance to disagree with the rasters, and a namelist that disagrees with its case fails in ways that name
the wrong thing. Four output keys are forced for the same reason: the reader needs
`DUMP_TIME_OF_ARRIVAL`, `DUMP_SPREAD_RATE`, `DUMP_SPREAD_DIRECTION` and `SPREAD_RATE_IN_M`, and the last is
the one that is not an error anywhere when wrong — without it the fire simply spreads 3.28 times too fast.

Three combinations are written conditionally because ELMFIRE handles them badly; the ember one was found
by running it, and is listed under "ELMFIRE errors that name the wrong thing" below. They are documented
with the section in `docs/input-file-format.md`.

Verified: all 102 fields round-trip through the writer and parser; the section is written for an ELMFIRE
scenario and not for another module; `build-case` reads it and the values reach the namelist; and the
generated namelist runs ELMFIRE to completion.

#### P1c - every namelist key — **done**

The eleven groups are now covered in full: **323 keys**, checked against `elmfire_namelists.f90`'s own
`NAMELIST` statements rather than against the user guide, which documents 143 of them. Everything ELMFIRE
reads is either a field in `ElmfireNamelistInput` with a widget in the GUI, or a fact the builder writes off
the case — the filenames, the grid, the time base, the ignitions and the band count.

The GUI sections follow the groups in the order the work is actually done in: inputs, outputs, simulator,
time control, spotting, suppression, smoke, monte carlo, misc, calibration.

**Dependencies are enforced rather than documented.** A setting ELMFIRE would not read is disabled in the
GUI and left out of the file with a comment saying why, so the file records what the run does and nothing
more. The cases that matter:

- `STOCHASTIC_SPOTTING` decides which half of `&SPOTTING` exists at all. On, eight parameters are sampled
  per case from their LO/HI bounds and the single values are inert; off, the bounds are. Both were being
  written before, so half of what the file said was untrue whichever way the switch went.
- The four ember models each select their own settings — `GENERATION_MODEL` picks between `NEMBERS_*`,
  `EMBER_GR` and the two `EMBER_GR_PER_MW_*`; `SPOTTING_DISTANCE_MODEL = EMPIRICAL` replaces
  `MEAN_SPOTTING_DIST` with Sardoy's and Himoto's fits unless `USE_CUSTOMIZED_PDF` overrides them; and
  `IGNITION_MODEL = DIRECT` means the two ignition delays are never read. `USE_SUPERSEDED_SPOTTING`
  discards all four and reads `SPOTTING_DISTRIBUTION_TYPE` instead.
- Six switches need a raster ELMFIRE checks for and shuts down without: `USE_SDI`, `USE_ERC`,
  `USE_PYROMES`, `USE_LAND_VALUE`, `USE_POPULATION_DENSITY`, `USE_REAL_ESTATE_VALUE`. The first three were
  writing runs that could not start, because the switch was written and its filename never was. All six
  now name a source layer in `[ELMFIRE]`, are warped into the case like any other, and are written off with
  a comment for a case that has none. Their filenames live in `&INPUTS` while three of the switches do not,
  so the pair cannot be written together — Fortran refuses a key outside its own group.
- `RANDOMIZE_RANDOM_SEED` is refused outright. It makes ELMFIRE seed from `SYSTEM_CLOCK` and ignore `SEED`,
  and every ensemble here varies `SEED` to produce its realizations, so it would silently make the campaign
  unreproducible and its realizations identical in distribution.
- `WIND_SPEED_FLUCTUATION_INTENSITY_MIN`/`_MAX` and the direction pair have no switch: ELMFIRE decides to
  sample by finding both ends above zero. One end set is not half a setting, it is none, and is said so.

Two traps found while doing this, both silent:

- **Four spotting settings are arrays over the 303 fuel models**, not scalars —
  `CRITICAL_SPOTTING_FIRELINE_INTENSITY`, `SURFACE_FIRE_SPOTTING_PERCENT`, its `_MULT`, and
  `SOURCE_FUEL_IGN_MULT`. A Fortran namelist assigns an unsubscripted value to the **first element only**,
  and these are dimensioned `(0:303)`, so raising the intensity threshold used to change fuel model 0 —
  which nothing burns — and leave every real fuel model at ELMFIRE's default. They are written
  `KEY(0:303) = 304*value` now.
- **A named calibration table that is not in the case kills the run with a bare
  `severe (29): file not found, unit 100`**, naming neither the table nor the switch, because ELMFIRE checks
  only that the filename is *set*. The three `*_BY_PYROME` switches are written on only when their CSV is
  actually in `inputs/`.

Verified by running it: a namelist with every group populated and every compatible feature on — empirical
Eulerian physical spotting with a customized PDF and crosswind spread, stochastic spotting, three raster
perturbations across all three distributions, initial and extended attack, smoke outputs, the diurnal
adjustment and a randomized stop time — parses all eleven groups and runs to
`End of simulation reached successfully`. `INITIAL ATTACK CONTAINMENT SUCCESSFUL` in that log is the
suppression group being read and acted on, not merely accepted.

### P1b - `template.data` as a patched template

The FBFM40 stem is done, and with it the building layers: stems are resolved against the case by
`ElmfireCaseBuilder.ResolveStem` rather than hardcoded, since fuel and building rasters are external
products (P3) that arrive under whichever name the pipeline that made them uses.

**The template itself exists**, at `PREACT/PREACTcore/Templates/elmfire/template.data`, copied beside the
assembly at build time so both PREACTcli and Unity can reach it. It is the corrected `mati.data` with the
Mati-specific values generalised, the keys WUInity patches marked `[WUInity]`, and the optional blocks
(building layers, `&WUI`, explicit ignitions, `PATH_TO_GDAL`) commented out rather than removed — a case
missing any of those would otherwise fail on a raster nobody wrote. Verified by running it unmodified
against the Mati case: exit 0, 539.1 acres over the 8 h default, random ignition inside the mask, all seven
output rasters written.

**Driving the template through `SetKeyInGroup` is superseded and will not be done.** It was the plan while
the namelist was a string list in `BuildNamelist()`; P1 replaced that with settings the scenario owns, which
covers everything patching the template was meant to reach — spotting, `&WUI`, the ember keys, the scaling
flags, and a band count taken from the case. Building the same file a second way would leave two namelist
paths that can disagree, and the interesting question of which is in force.

`template.data` keeps a narrower job: **the hand-editable starting point**, named through
`[ELMFIRE] NamelistTemplate` when a case wants physics the settings do not expose. That is the escape hatch,
and the coupling patches only what it must into it.

A second case, for a city other than Mati, is what will show which of the template's remaining values are
really generic and which are Attica in disguise.

### P2 - grid discipline and a validator — **mostly done**

**`MasterGrid.FromRasterFile` no longer accepts a grid it cannot represent.** It took `gt[1]` as *the* cell
size and ignored the rest of the geotransform, so a rectangular, rotated or south-up raster was accepted and
then measured as if it were square and north-up — with `CellSize` flowing from there into slope, aspect, the
distance transform and k-PERIL. All three are now refused with a message naming which it was.

**`ElmfireCaseValidator` checks a finished case against its own master grid.** Run as the last step of
`ElmfireCaseBuilder`, reported through `Result.Validation`; `ElmfireCoupling` refuses to run an invalid case
and `build-case` exits non-zero, while the rasters are left on disk to inspect either way. It checks, per
raster: size, cell size, origin (to a tenth of a cell), rotation, CRS against the grid's EPSG, non-finite
values, and that all five weather stems agree on band count. Missing required stems are listed; optional
layers (canopy, buildings, ignition mask) are validated when present and not reported when absent.

This exists because **ELMFIRE reads rasters by filename and never compares their geotransforms.** Two layers
on different grids are read cell-for-cell as if they lined up, and the result is a complete, plausible fire
built from layers describing different ground — nothing downstream can tell.

**Verified** against the Mati case (21 rasters, 8-band weather, passes clean) and against five deliberately
broken synthetic cases: a layer shifted 5 cells, mismatched weather band counts, a deleted required stem, a
planted NaN, and a 30x50 m raster handed to `MasterGrid`. All five are caught with the offending file named.

One trap worth recording: the NaN check was first written to ask GDAL for the band's statistics and test those
for finiteness. **That silently never works** — `ComputeStatistics` excludes non-finite cells from its own
min/max/mean, so a raster with a NaN in it reports perfectly finite statistics. It passed a raster with a NaN
deliberately planted in it. The check now reads every cell of every band.

**Both remaining P2 items are now done too.**

`RasterHarmonizer` stamps **`AREA_OR_POINT=Area`** on every warp output. A raster tagged `Point` declares that
its geotransform names cell *centres* rather than corners — half a cell, 15 m on a 30 m grid — and gdalwarp
honours the tag on read but *copies it to the output*, so the shift returns the next time anything reads the
harmonized file. This was one of the hand-repair steps in P0 that had to be repeated after every re-warp.
Verified: a deliberately `Point`-tagged raster warps out as `Area`, on grid.

**Non-finite values are scrubbed on ingest** (`RasterScrubber`), replacing NaN and infinity with 0 for every
layer warped through the user-raster path — 0 being right for a canopy void, a building layer and a mask alike.
ELMFIRE compiles with FP traps, so one NaN aborts the run with `forrtl: error (65): floating invalid` and a
traceback pointing at code the run never enters; the canopy products for the reference case carried NaN in
about 23 % of cells. The count is logged and recorded as a fallback, so a heavily-patched layer is visible
rather than silently repaired. Verified: 137 planted NaN and 2 planted infinities all replaced, other cells
untouched, and idempotent on a second pass.

The validator still checks for non-finite data, and should: it is the safety net for layers that arrive by
other routes, where this is the repair on the one path the builder controls.

### P3 - ingest the external layers — **done**

The warping machinery already existed and worked; what was missing was any way to reach it except
`PREACTcli --fbfm40 <path>`. So **a case built from the scenario editor could not have these layers at all**:
canopy silently defaulted to zero (surface fire only, no crown fire), the building spread model stayed off, and
the scenario recorded none of it. Which layers a case had depended on whoever remembered the right command
line.

`[ELMFIRE]` now names all twelve — `FuelModelFile` (+ `FuelModelStandard` choosing `fbfm40` vs `fbfm13`), the
four canopy layers, the five building layers, `IgnitionMaskFile` and `BarriersFile` — with a "Source layers"
section under Hazards > Fire. `ElmfireInput.GetSourceRasters()` holds the field-to-stem mapping in one place, so
adding a layer is one edit. Paths stay scenario-relative, so a `.wui` remains portable; a path that is set but
does not resolve is reported by name at parse time rather than showing up later as a layer that quietly is not
there.

`build-case` reads the same keys, with an explicit flag overriding the scenario rather than the reverse — so
one layer can still be tried against a scenario without editing it. Verified: a scenario naming
`layers/fuel.tif` and `layers/canopy_cover.tif` reports `fbfm40: from the scenario`, `cc: from the scenario`,
and names the third layer as absent.

The two silences that made this worth doing are now stated on screen: canopy absent means surface fire only,
and the building model needs **all five** layers — four is the state that looks like progress and behaves like
nothing.

### P4 - weather, and WindNinja

**WindNinja now runs. The first half of P4 is done.** A k-PERIL run on the Mati case had reported
`WindSpeedFile is constant at 18.567; the spread ellipse will be circular` — the pipeline's uniform fallback,
one value for the whole domain, so every cell got the same length-to-breadth ratio and the trigger boundary
came out isotropic instead of wind-driven. **Two separate causes, both fixed:**

1. **Only the CLI could find WindNinja.** `FindWindNinja` was a private helper in `PREACTcli`, so a case
   built from the scenario editor reported "no WindNinja executable configured" *on a machine with WindNinja
   installed* and wrote a uniform field. Terrain wind therefore depended on which front end prepared the
   case. The probe is now `WindNinjaRunner.FindExecutable` in core, `ElmfireCaseBuilder` calls it when no
   executable was named, and the CLI's method delegates to it. It checks `WINDNINJA_CLI`, then `PATH`, then
   `C:\WindNinja` and the `Program Files` roots read from the environment. `[Elmfire] WindNinjaExe` overrides
   it for a non-standard install; the Hazards > Fire tab reports which install was resolved rather than asking for a path.
2. **The case kept its uniform wind.** Weather layers are kept by default, so installing WindNinja and
   rebuilding changed nothing — the build reported success and `ws.tif` stayed flat, with the only symptom
   appearing much later as a circular ellipse in k-PERIL. `WarnIfKeptWindIsUniform` now says so at build
   time, where `RebuildExistingLayers` is the answer.

**Verified** against the Mati DEM through `WindNinjaRunner.Run` itself, not just the executable: detection
finds 3.12.1, the run completes, and the u/v warp puts 5.48-18.02 mph and 284-347° on the 566x541 master
grid from an 11.2 mph / 315° domain average. Directions stay inside 0-360, which is the check that the
bearing field was recomposed from components rather than interpolated as angles. 1646 edge cells (0.5 %) fall
outside WindNinja's mesh and take the domain average.

**The mesh default changed from `coarse` to `fine`**, measured rather than assumed. On this domain coarse
meshes at 263 m and takes 0.4 s; fine meshes at 117 m and takes 5 s, and spans 5.3-18.2 mph against coarse's
7.8-15.4 from the same input. The coarse mesh smooths away the variation WindNinja exists to produce, and 5 s
is nothing against a case build — though the 11x is worth revisiting per realization in an ensemble. Finer
still is not simply better: solver cost climbs roughly with the cube of the mesh count, so meshing at the
grid's own 30 m would be two orders of magnitude slower to resolve terrain a mass-conserving model does not
claim to.

This matters more than it did before: k-PERIL now samples the wind per cell at each cell's arrival time, so a
varying field is used properly rather than collapsed to one hour. A uniform field wasted that entirely.

#### Second half: the N-band series — **done**

Everything the pipeline wrote was single-band: one WindNinja run at the sampled day's peak and one Nelson
sample, so `NUM_METEOROLOGY_TIMES` came out 1 and the weather was constant in time for the whole burn. Wrong
for an 8-hour fire — the wind driving hour 8 was the wind of hour 1 — and it left the per-cell arrival-time
sampling in k-PERIL with one band to choose from, so that change could not yet do anything.

**The five weather rasters are now a real series.** One band per `DT_METEOROLOGY` step of the simulation, from
`SimulationTstopSeconds`, capped by `MaxBands` (72) with the cap reported. Bands walk **real consecutive
archive hours** from the sampled day at the simulation's own hour of day — so hour 30 of a three-day ensemble
realization is the second night of an actual historical sequence, with its actual overnight recovery, rather
than one day replayed. `GeoTiffRasterWriter.WriteBands` writes them a band at a time, since 72 bands of the
Mati domain is 88 MB.

- **Wind:** one WindNinja solve per band from that band's own hour. A solve that fails carries the previous
  band forward rather than abandoning terrain wind for the whole run, and says how many did.
- **Moisture:** the Nelson march already visited every hour, so the series was nearly free — it now records
  each band's hour as it passes instead of keeping only a minimum. The conditioning window was extended to
  reach the last band, which the burning period does not necessarily contain: a fire can start before 10:00,
  run past 18:00, or leave that window entirely on a multi-day run.

**Band count is identical across all five stems, including on every fallback path.** ELMFIRE reads them all
against one `NUM_METEOROLOGY_TIMES` and the builder counts it from `ws.tif` alone, so a wind raster with fewer
bands than the moisture rasters would have it read past the end of the others. The uniform fallbacks
therefore write `WriteConstantTimeSeries` at the full band count rather than a single band.
`SecondsPerBand` is passed from the namelist's own `DT_METEOROLOGY`, so a series cannot be written at one
interval and read at another.

**This changed a deliberate decision, and the change is deliberate too.** Nelson used to give one diurnal
minimum over the burning period: the driest hour of peak burning, which was both more conservative than a
daily mean and robust to ERA5 reporting a trace of drizzle (an area-average over ~9 km rather than rain that
necessarily fell on the fuel) that Nelson correctly answers by soaking the 1-hour stick. Both concerns were
about choosing *one* hour to stand for a whole day; a series does not choose. The trade is real — the run is
no longer uniformly at the diurnal minimum, so it is less conservative — so **the old minimum is still
computed and logged beside the series mean**, and a case whose series looks wet can be compared against the
figure it used to be given instead of the change being invisible.

**Verified** on the Mati domain against the cached 2000-2024 ERA5 archive (219168 rows), drawing 2001-08-09
(FWI 51.4) for a 4-hour run from 13:00. All five rasters come out with 4 bands; wind falls 15.54 -> 12.37 mph
through the afternoon with 24 mph of spatial spread within a band, direction shifts 18.3 -> 13.2 deg, and
moisture varies in both time (m1 5.30/5.15/5.35/5.86 %) and space. The old single figure would have been
5.2/6.7/9.6 %. A separate round-trip test confirms `WriteBands` preserves band order and the lower-left row
orientation, which is the failure that would misplace weather north-for-south without erroring.

### P5 - Unity wiring — **done**

All three parts, though not in the shape originally planned:

- **"Build ELMFIRE case" sits beside the existing data steps** in Scenario > Prepare data, calling
  `ElmfireCoupling.BuildCaseOnly`. Completion is judged by the case's `elmfire.data` existing, since that is
  written last and only once every layer is in place.
- **The runner is the module itself.** `Module=ELMFIRE` runs `elmfire.exe` when the simulation starts rather
  than needing a separate Unity-side runner, and reuses existing output for an unchanged case.
- **The import needs no switching.** The ELMFIRE module reads its own output back through the `AscImport`
  reader internally, so there is no step where the scenario has to be re-pointed at rasters by hand.

The loop is closed: fire -> evacuation -> k-PERIL, from the GUI.

### P6 - ensemble - **done, not yet run at scale**

`PREACTcli converge-trigger` drives it: per-realization `outputs/`/`scratch/` under one shared, read-only
`inputs/`, resumable and parallel across OS processes, with `NUM_ENSEMBLE_MEMBERS` forced to 1. ELMFIRE's own
internal ensemble is deliberately not used — it does not write a time-of-arrival raster per member, and
k-PERIL needs one per realization.

**Ignitions come from ELMFIRE itself**, by varying `SEED` per realization against the case's
`ignition_mask.tif` — which `RestrictIgnitionToBurnableFuel` has already intersected with burnable fuel at
build time. This is why the campaign needs no ignition sampler of its own, and the `IgnitionSampler` and
`ElmfireRealizationWriter` classes written for that job have been deleted: nothing ever called either, since
the driver patches the namelist directly. (Worth a note for anyone doing the same tidying: the dead writer was
also housing the live `ElmfireNamelistKeys` constants the driver uses, so removing the file broke the build
until they were moved beside `ElmfireNamelist.SetKeyInGroup`, which is where they belonged.)

Weather comes from `ClimatologySampler` when `--realization-weather` is on; see the decision below.

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

**The ensemble aggregates the fire, not only the boundary.** It used to fold in each realization's trigger
boundary and discard the arrival raster it was derived from — so the campaign could say when an area must
leave and nothing about which ground is threatened, how often, or how soon. `EnsembleFireStatistics` now folds
the arrival rasters in the same pass and writes, beside `trigger_probability.asc`:

- `ensemble_burn_probability.asc` — fraction of realizations in which each cell burned.
- `ensemble_arrival_earliest.asc` — earliest arrival seen anywhere in the ensemble.
- `ensemble_arrival_mean.asc` — mean arrival, **conditional on burning**.
- `ensemble_arrival_p10/p50/p90.asc` — percentiles, also conditional on burning.

The mean and percentiles are conditional on purpose. Averaging a non-arrival in as zero would make
rarely-burned cells look like the first to go; averaging it in as `tstop` would make them look safest. Both are
statements about *how often* a cell burns, which is what the burn probability is for — read the two together.
The low percentiles are the conservative ones: the 10th is "when the fire arrives in the fastest tenth of the
cases it arrives at all", which is the figure an evacuation has to survive, where the median describes a typical
fire nobody is planning for. Percentiles come from a per-cell histogram over the run's own duration, hourly by
default, and are reported at the bin's **lower** edge so the answer is never later than the truth. That costs
44 MB for a 566x541 grid over three days, against 245 MB to hold 200 realizations outright, and one hour is far
finer than the question needs.

Verified against hand-computed values on a 3-cell ensemble of 10 realizations: burn probability 1.00 / 0.20 /
0.00, conditional means 5.5 h and 5.0 h, nodata where nothing burned, p10/p50/p90/p100 exact, six rasters
written and read back.

**Both open decisions are settled.**

**Ignition only, by default; weather optionally.** `--realization-weather` draws a fresh historical peak
fire-weather day per realization and gives it its own five weather rasters; without it every realization shares
the case's. Ignition-only is the default for three reasons: where a fire starts is the dominant uncertainty for
a trigger boundary, since it decides whether the fire reaches the community at all and from which direction;
the sampled day is *already* an annual peak fire-weather day, so holding it fixed is the conservative choice
rather than the lazy one; and varying weather now costs a WindNinja solve per band per realization — about six
minutes for a 72-band three-day run, which is twenty hours of WindNinja across a 200-realization campaign.

**So `RASTER_TO_PERTURB` blocks do not belong in the template.** Weather variation is done by redrawing the
whole day and rewriting the five rasters, which keeps a realization's weather a physically consistent day out
of the record. Perturbing rasters inside ELMFIRE would instead jitter each field independently, which can
combine into a day that never happened — the same reason `ClimatologySampler` resamples whole days rather than
sampling variables separately.

**Master cell size stays 30 m**, the builder default and what the reference case already uses. It matches
Copernicus GLO-30, the DEM source; going to 25 m would resample the DEM finer than the data behind it, which
invents detail rather than preserving it. `MasterGrid` now refuses non-square cells outright, so there is no
longer a route by which a case drifts off it.

**Not yet run at scale.** Every piece is built and unit-verified, but no full campaign has been run end to end
since these changes, so the wall-clock cost of a converging campaign and the shape of the aggregate rasters on
a real domain are both unmeasured.

## Data questions for the external pipeline

- `ff_h` (building footprint fraction) maxes at **2**, not 1.
- `bfm_h` (building fuel model) holds codes up to **256**, while `building_fuel_models.csv` defines 15
  rows (0-14).
- The case has only ~250-490 m of margin beyond the evacuation domain (`EDGEBUFFER = 60`);
  `ElmfireCaseBuilder` defaults to 2000 m because a fire is free to burn outside the domain.

## The scenario's weather and the fire's weather

They were two unrelated things, and now they are one.

`[Weather] WeatherFile` feeds `WeatherManager`, which computes temperature, humidity, the hourly FFMC, the FWI
and the KBDI. `WeatherRasterPipeline` separately draws a historical peak fire-weather day out of ERA5 and writes
the five rasters ELMFIRE actually spreads with. Nothing connected them: the fire burned an August 2001 day while
the platform reported the scenario's own calendar date, and both downloaded their own weather.

Made worse by the removal of the in-process spread models, which had been the only consumers of the scenario
weather — after that it drove nothing at all, and the six `[Weather]` index seeds seeded indices nobody read.
That was missed when BEHAVE went: the fuel and canopy bands were checked for orphaning and weather was not.

**`[Weather] WeatherAnchorDateTime`** is the fix: the moment in the record the simulation's start time reads
from. The case build sets it to the same anchor `BuildBandSchedule` positions band 1 at, and points
`WeatherFile` at the case's own ERA5 archive — which `WeatherStream` can already read, both being Open-Meteo
CSV. Every lookup, the download span and the shared cache key all go through the offset.

An **offset** rather than moving the scenario's dates, because response curves with absolute times, evacuation
orders and timed ignitions are all stated on the scenario's calendar, and shifting that would move all of them.
And an offset rather than re-stamping the sampled day's values onto the scenario's dates, because the FWI's
day-length factor is seasonal — that would index an August day as July.

`WeatherManager.Rebase` exists because the weather manager is constructed **before** the ELMFIRE module, and
building a case is what draws the day. Without it, a scenario that builds its case as part of the run would
report its own calendar date on that run and the sampled day's only on the next — the same scenario giving two
different answers depending on whether the case already existed.

**Verified** against the cached Mati archive: a scenario dated 2020-07-01 13:00 anchored to 2001-08-09 13:00
reads 33.7 C / RH 22 %, which is the day the pipeline drew (`33.7 C, RH 23 %`), and tracks it hour by hour.
Unanchored, the same scenario read 32.5 C / RH 34 % — a different, wetter day.

The indices are now also displayed, which they never were: only temperature and RH ever reached the output
window, so FWI and KBDI were computed every timestep and shown nowhere.

**Two limits, stated rather than papered over.** `DMC` and `DC` still start from their `[Weather]` seeds, since
no antecedent marching exists (`WeatherManager.Initialize` has been commented out since before this work) — so
the drought codes describe the seeds, not the weeks before the sampled day. And fire danger remains *reported,
not used*: ELMFIRE spreads from the moisture rasters, and nothing reads the FWI.

## The rolling weather window

`WX_BANDS_KEPT_IN_MEM` is how many weather bands ELMFIRE holds at once, rolling the window forward as the fire
burns past it. It is the only thing standing between a run of hundreds of hours and its memory: the five
weather rasters are held at `ncols x nrows x bands kept`, so on the 566x541 Mati domain a 72-band series costs
814 MB held whole against 218 MB at 8 bands kept.

It used to crash. Every case here was therefore written with the window *disabled* — `WX_BANDS_KEPT_IN_MEM`
above the band count, so the series is loaded once — after a 72-band run propagated correctly for 30 hours and
then died with `forrtl: severe (157): Program Exception - access violation` in `LEVEL_SET_PROPAGATION` the
moment it advanced past band 30. That is an error naming the level set, about the weather, and the line number
it gives is a blank line: the release build inlines, so the frame belongs to the caller of the routine that
actually faulted.

**The cause was in the vendored Fortran, three bugs of one kind** — an index into the window used as though it
were an index into the file. Found by rebuilding with `/check:bounds`, which names it outright:
`Subscript #3 of the array R4 has value 0`, in `EMBER_TRAJECTORY_EULERIAN`.

1. The four ember sites in `elmfire_spotting.f90` computed a band's number in the file, clamped *that* at 1,
   and only then subtracted the number of the first band in memory. An ember still flying from a band the
   window has rolled past therefore got 0 or less, and indexed off the front of the shared-memory window.
2. The same sites' interpolation weight counted from the rebased index as though it were absolute, so it grew
   by one per band as the window advanced — extrapolating wind speeds far outside the two bands being
   interpolated between, rather than blending them.
3. `LEVEL_SET_PROPAGATION` clamped its band index against `BAND_H`, the last band's *number in the file*,
   instead of against the number of bands held. Those are equal only while the first slice is loaded.

All three are identity when the whole series is held, which is why cases built before this never saw them, and
why the crash arrived only once campaigns grew past 30 hours.

**Why it "works on Linux".** Index 0 is one element before the array. On Windows the MPI shared-memory window
begins on a page boundary, so that read is on an unmapped page and faults immediately. On Linux it lands on
mapped memory and returns whatever is there — no crash, and embers driven by whatever value happened to sit
below the window. The Linux build was not working; it was failing quietly.

**Measured after the fix**, same ignition, same 72-band series, 72 h, only the window changing:

| bands kept | fire area | peak RAM | slice loads | wall clock |
|---|---|---|---|---|
| 80 (whole series) | 14205.4 ac | 814 MB | 0 | 38 s |
| 30 (ELMFIRE default) | 14205.4 ac | 421 MB | 2 | 37 s |
| 8 | 14205.4 ac | 218 MB | 10 | 37 s |
| 4 | 14205.4 ac | 180 MB | 23 | 37 s |

Identical fires. The window is now a setting (`ElmfireNamelistInput.WX_BANDS_KEPT_IN_MEM`, default 30, floor
of 2 since a band is interpolated against the next one) rather than a fact the builder forces, and
`converge-trigger` leaves the template's value alone instead of overriding it. **Cases built before
2026-08-05 still carry the disabling value** and will keep holding the whole series until the line is edited
or the case is rebuilt.

## Letting a run end itself

`memopt` gained a check that ends a case once the phi field stops moving, so a run can be given an absurd
`SIMULATION_TSTOP` and stop when the fire front has nowhere left to go rather than at a duration guessed in
advance. Pulled here on 2026-08-05, with two things it needed to work:

- **The check flagged the case but nothing ended it.** `rank_finished` is only read where the weather window
  rolls forward, and a single-band run never reaches that branch — so a stalled fire under a 9999999 h stop
  time marched the clock all the way there in empty steps and then took its final dump at T of order 1e10 s,
  where `NINT(T)` overflows a 32-bit integer and the run died with `floating invalid` in `MAIN_DUMP_ROUTINE`
  *after* the fire had finished. It now sets `TSTOP = T`, which is per case.
- **`totalDuration` was a default INTEGER** taken straight from `SIMULATION_TSTOP` for a single-band run, so
  it overflowed above 2^31 s: 300000 h ran, 1000000 h failed on the assignment itself. Now REAL, like the T
  it is compared against.

**Two limits worth knowing before setting a stop time of nine million hours.** A *multi-band* run is bounded
by its weather series regardless — `totalDuration = WS%NBANDS * DT_METEOROLOGY` — so an absurd stop time only
means anything with a single band held, or with a series long enough to cover the fire. And the check requires
every ignition to have gone and no embers to be in flight, so a run with spotting enabled ends only once the
last ember has landed.

## Running a campaign: what the first real attempt taught

The tool now works mechanically end to end, verified by running it. Two things about it are operational
constraints rather than bugs, and both cost a run to discover.

**The case's weather must cover the campaign's duration, or ELMFIRE refuses every realization.** It validates
this itself — `[ERROR] Not enough weather bands for given SIMULATION TSTOP` — so a 72-hour campaign against a
case built for 8 hours fails on realization 1 and every one after it. The driver now sets
`NUM_METEOROLOGY_TIMES` from the bands the rasters actually carry (the template's value describes whatever the
case was built for) and warns up front with the band count needed and three ways out.

Rebuilding just the weather is safe and does **not** need `RebuildExistingLayers`: delete
`inputs/{ws,wd,m1,m10,m100}.tif` and build the case again with `--tstop` set to the campaign's duration, and
the builder produces only what is missing. **Do not use `RebuildExistingLayers` for this** — with no source
layers named it refills canopy with zeros, silently turning a crown-fire case into a surface-fire one.

**A short `--tstop` produces a campaign of fires that never arrive.** An 8-hour Mati fire burns ~1750 cells
and does not reach the community, so k-PERIL correctly declines to compute a boundary and the campaign
aggregates nothing. This is the documented reason the default is three days, and it is easy to defeat by
shortening `--tstop` to match a case's existing weather — which is exactly the wrong trade.

### What the first run confirmed working

- The realization `.wui` loads, the fire is read rather than recomputed, and a full SUMO evacuation runs
  (10912 s average evacuation time on Mati) yielding a real WRSET of 181.9 minutes.
- **The per-cell arrival-time wind sampling works**, reporting its own distribution:
  `Cells per band - 1:20, 2:83, 3:142, 4:238, 5:285, 6:247, 7:312, 8:304879; 304453 cell(s) the fire never
  reached took the last band.` That is the first demonstration of it on a real run.
- The case's own `ws`/`wd`/`wui_area` are handed to k-PERIL automatically, which a realization cannot get any
  other way: it runs as `AscImport`, so the ELMFIRE coupling that used to supply them never executes.

### Ignition varies per realization; two paths were pointing at the case root

A campaign no longer inherits the template's ignition. Each realization has its fixed points commented out
and draws its ignition from `ignition_mask.tif` instead — see the table in
`docs/probabilistic-trigger-convergence.md`, which lists the four keys that have to agree and what each does
when it does not. Verified against real `elmfire.exe`: two seeds ignite in different cells, one case per
realization, both fires burn. A missing or all-zero mask is now fatal at startup rather than on realization
`--max`.

The same test found that `MISCELLANEOUS_INPUTS_DIRECTORY` had never been repointed for a realization. It is
`'./inputs'` — correct relative to the case root, where the namelist sits, and nowhere at all relative to
`_elmfire/<idx>/`, where a realization runs. So a generating campaign failed every realization with "Problem
opening fuel model table file ./inputs\fuel_models.csv", which reads like a missing file rather than a wrong
working directory. It is resolved against the template's own directory and written absolute, and the driver
says so up front when it resolves to somewhere that does not exist.
