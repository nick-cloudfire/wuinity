# ELMFIRE cases

An ELMFIRE scenario (`[WildfireModule] Module=ELMFIRE`) runs the ELMFIRE fire model on a **case**: a folder of
rasters on one grid plus a namelist. This page describes how a case is built, how a run uses it, and the traps
worth knowing. The keys are listed in [the input format](input-file-format.md#elmfire--when-moduleelmfire); the
campaign that runs many fires on one case is in [Trigger campaigns](trigger-campaigns.md).

A case is built by the GUI's **Build fire case** (workflow step 5, or Data > Build fire case), by
[`PREACTcli build-case`](command-line-tools.md#build-case--build-a-scenarios-elmfire-case), or before a run when
`[ELMFIRE] BuildCase=true`. All three use the same builder with the same options, taken from the `.wui`. A build
is refused while a trigger campaign of the same scenario is running, because every realization reads the case.

## The case folder

`[ELMFIRE] CaseDirectory`, `elmfire/` beside the `.wui` by default:

| Path | What |
|---|---|
| `elmfire.data` | The namelist, written by every build. |
| `elmfire.data.kept-<yyyyMMdd_HHmmss>` | A hand-edited namelist a build set aside ([below](#the-namelist)). |
| `case_sources.txt` | What each raster was made from, the grid settings, and the hash of the namelist the build wrote. The GUI's **Read this case into the editor** reads the source fields back from it. |
| `inputs/` | The rasters, one per ELMFIRE stem, all on the grid of `dem.tif`; `fuel_models.csv` (and `building_fuel_models.csv` when given). |
| `inputs/dem_source.tif` | The DEM the grid was cut from (a download, or a copy of a local one). |
| `inputs/_previous_grid/` | The rasters of a grid a later build replaced, and originals of rasters it re-cut. |
| `climatology/<Name>_era5_hourly.csv` | The ERA5 hourly archive the weather is drawn from, downloaded once. |
| `outputs/` | The last single run's rasters, its namelist `run.data` and its `run.fingerprint`. |
| `scratch/` | ELMFIRE's intermediates. |
| `elmfire.log` | The last single run's ELMFIRE output. |

## Building a case, step by step

1. **The padded domain.** The evacuation domain (`[Simulation] LowerLeftLatLon` and `DomainSize`) grown by
   `[ELMFIRE] PaddingMetres` (2000 m) on every side. A fire is free to burn beyond the evacuation domain, and a
   fire clipped at its edge understates exactly the spread a trigger boundary measures.
2. **The grid of record: `inputs/dem.tif`.** ELMFIRE takes its domain, CRS and cell size from this file, and
   everything else — every raster, the painted areas, k-PERIL — is on its grid. An existing `dem.tif` is kept
   if it covers the padded domain (to within one cell) at `CellSizeMetres`. Otherwise the grid is cut again, and
   the old one's rasters are moved to `inputs/_previous_grid/`. A new grid is in the UTM zone of the padded
   box's centre, its extent rounded outward to whole cells so it contains the whole padded domain, at
   `CellSizeMetres` (30 m, the resolution of Copernicus GLO-30).
   The DEM it is cut from is, in order: the one given with `build-case --dem`; the cached
   `inputs/dem_source.tif` if it covers the padded domain; the scenario's `[Landscape]` DEM if it covers it; a
   fresh OpenTopography download (Copernicus 30 m by default). A source DEM smaller than the padded domain is
   used with a warning: the uncovered cells have no elevation and the fire cannot spread there.
3. **Slope and aspect** are derived from `dem.tif` (Horn's method; aspect is the compass bearing of the downhill
   direction). They are never resampled: interpolating aspect across 359°→0° gives 180°.
   **`adj` and `phi`** are written as constant 1.0.
4. **Source layers** — fuel, canopy, buildings, masks — are warped onto the grid from the files the scenario
   names ([sources below](#where-the-layers-come-from)): nearest-neighbour for class layers (fuel models, masks,
   building fuel models, barriers, pyromes — averaging models 1 and 9 would give model 5, a fuel neither cell
   has) and bilinear for continuous ones. NaN and infinities are replaced with 0 on the way in and counted:
   ELMFIRE traps on floating-point errors, and one NaN aborts the run with a traceback pointing at unrelated
   code. A layer the case already has is kept unless `RebuildExistingLayers` is on.
5. **After a re-cut grid**, every layer the old grid had and this build had no source for is warped from
   `_previous_grid/` onto the new grid (the new padding is nodata: no fuel). So is every raster a namelist in force
   names — the case's `elmfire.data`, its `elmfire.data.kept-*` and the scenario's `NamelistTemplate` — when it
   sits in `inputs/` and is not on the grid; the original is kept in `_previous_grid/`. Terrain and weather are
   derived again rather than carried.
6. **Canopy** layers nobody supplied are written as zeros: ELMFIRE refuses to start without them, and zero means
   surface fire only — no crown fire. The build says so.
7. **Painted areas** become `ignition_mask.tif`, `wui_area.tif` and an explicit ignition ([below](#painted-areas)).
8. **Ignition points** (`[IgnitionPoint]`) are measured in the case's CRS. One outside the grid is dropped and
   reported with both coordinates — never moved to the edge, which would start a fire nobody asked for.
9. **The ignition mask**: without a painted or named one, an all-ones mask (ignite anywhere). It is then
   restricted to burnable fuel — fuel codes 0 and below (NoData included), 91–99, and Anderson's 14 are not.
   On Mati 64 % of the padded domain is sea or urban; unrestricted, ELMFIRE ignited open water, exited 0 and
   reported 0 acres.
10. **Weather**: kept when all five rasters are there with one band count and cover the fire; otherwise drawn
    again ([below](#weather)).
11. **`fuel_models.csv`** is copied into `inputs/` from ELMFIRE's own `build/source/fuel_models.csv` when the
    case has none, and named in the namelist, so ELMFIRE does not rewrite a shared table on every run.
12. **The namelist** is written again ([below](#the-namelist)).
13. **`case_sources.txt`** records the sources, and the namelist's SHA-256.
14. **Validation.** Every raster is checked against `dem.tif`: size, cell size, origin (to a tenth of a cell),
    rotation, CRS, finite values, and that the five weather rasters agree on their band count. ELMFIRE itself
    reads rasters cell by cell without comparing their georeferencing, so a misregistered layer makes a
    plausible fire of the wrong ground. A case that fails is left on disk to inspect, and not run.

A build from the GUI then writes onto the scenario (save it to keep them): `[Landscape] ElevationFile`,
`SlopeFile` and `AspectFile` become the case's `dem/slp/asp.tif`, `[kPERIL] WuiAreaFile` becomes
`elmfire/inputs/wui_area.tif` when the build wrote one, and `[Weather] WeatherFile` and `WeatherAnchorDateTime`
point at the archive and the day the weather was drawn from. `build-case` prints these keys instead of writing
them.

## Where the layers come from

| Stem | Source | Notes |
|---|---|---|
| `dem` | OpenTopography (`COP30`, `COP90`, `SRTMGL1`, `SRTMGL3`), or a local DEM | Needs an OpenTopography key: environment `OPENTOPOGRAPHY_API_KEY`, then `WUInity/Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt`, or (GUI) one typed in for the session. Never stored in the scenario. |
| `slp`, `asp` | derived from `dem` | |
| `adj`, `phi` | constant 1.0 | |
| `fbfm40` / `fbfm13` | `[ELMFIRE] FuelModelFile` + `FuelModelStandard` | Required: ELMFIRE will not start without a fuel raster. LANDFIRE in the US; your own raster elsewhere. |
| `cc`, `ch`, `cbh`, `cbd` | `Canopy*File`, else `CanopyDatasetFolder` (FIRE-RES), else zeros | |
| `bldg_area_avg`, `bldg_separation_distance`, `bldg_nonburnable_frac`, `bldg_footprint_frac`, `bldg_fuel_model` | `Building*File` | The building spread model is switched on only with all five (the external pipeline's `baa`, `ssd`, `nbf_h`, `ff_h`, `bfm_h` are recognised too). |
| `ignition_mask` | the painted ignition area, else `IgnitionMaskFile`, else all ones | Restricted to burnable fuel. |
| `wui_area` | the painted WUI area | What k-PERIL protects. |
| `barriers` | `BarriersFile` | `USE_BARRIERS` is written on only when there is one. |
| `sdi`, `erc`, `pyromes`, `land_value`, `population_density`, `real_estate_value` | the matching `[ELMFIRE]` key | Nothing produces these; each switch that needs one is written on only when the case has it. |
| `ws`, `wd`, `m1`, `m10`, `m100` | the weather chain | |

**LANDFIRE (United States).** Workflow step 4's **Get LANDFIRE fuels and canopy (US)** (also Data > Advanced)
asks the LANDFIRE Product Service for the scenario's box and start year, waits for the job, and splits the result
into `downloads/landfire/<Name>_lf_fbfm40.tif` (or `_fbfm13` with the Anderson 13 option), `_cc`, `_ch`, `_cbh`
and `_cbd`. It points the scenario's fuel and canopy keys at them and switches `CH_TIMES_10`, `CBH_TIMES_10` and
`CBD_TIMES_100` on, because LANDFIRE stores heights in decimetres and bulk density ×100. Build the case after it.
It refuses a domain outside the US; a failed LFPS job is reported with the service's message, and network errors
are retried up to five times.

**FIRE-RES (Europe).** Point `[ELMFIRE] CanopyDatasetFolder` at a folder holding `panEu_canopyCover.tif`,
`panEu_canopyHeight.tif`, `panEu_cbh.tif` and `panEu_cbd.tif`; each case is clipped out of them. These are in
real units (m, kg/m³, %), so the three scaling flags are forced **off** — left on, a 44 m forest is read as 4.4 m
and crown fire never starts, with nothing reporting it. The fuel model stays yours.

**Elsewhere** there is no fuel download: name a fuel model raster of your own (any CRS; it is warped onto the
grid). Canopy is zero unless you name it.

## Painted areas

Fire > Fire areas paints three masks on the fire grid: the **WUI area** (what the trigger boundary protects), the
**ignition area** (where fires may start) and an **initial ignition**. They are saved in the scenario's `.gfi`
(`[WildfireModule] GraphicalFireInputFile`). An ELMFIRE scenario is painted on the case's `dem.tif`, so the case
has to exist before painting.

At build time the painting is placed on:

1. the case grid, when the painting has its size (and, when the file records where its grid lies, its position,
   cell size and CRS);
2. otherwise the grid this build is replacing, when it re-cut the grid;
3. otherwise the `[Landscape]` reference raster — a painting made before the case existed.

If none fits, the build fails: `The painted areas in <file> are 616x590 cells, but the fire-case grid is
704x680 and ..., so there is no telling which ground they were painted on. Repaint the ignition and WUI areas on
the fire-case grid (load the case's dem.tif as the landscape), then build again.` A painting placed on the wrong
grid would shear into different ground, and skipping it silently would leave the case with no WUI area.

**Moving a painting onto the case grid.** When step 6 finds the painting on another grid it can locate (the
`[Landscape]` raster, `inputs/_previous_grid/dem.tif`, the scenario's downloaded DEM), it offers **Move painting
onto the fire-case grid**. Every target cell takes the value of the source cell its centre falls in, reprojected
when the two grids' CRSs differ; every painted initial-ignition cell marks the target cell under it, so a small
ignition cannot vanish. It writes `<stem>_<W>x<H>.gfi` beside the original (never over it) and points the
scenario at the new file. On Mati (616 × 590 cells of 27.6 m onto 566 × 541 of 30 m) the WUI area went from 7383
to 6260 cells, 5.626 to 5.634 km², and the ignition area from 161543 to 135734 cells. A GUI build that re-cuts
the grid moves the saved painting along by itself, the same way.

What each mask becomes: the ignition area → `ignition_mask.tif` (then restricted to burnable fuel); the WUI area
→ `wui_area.tif`; the initial ignition → one explicit ignition at its centroid, which turns ELMFIRE's random
ignition off. Placed `[IgnitionPoint]`s win over a painted initial ignition. An empty mask counts as "not
painted".

## The namelist

**Every build writes `elmfire.data` again**, from the scenario's `[ElmfireNamelist]` settings and the case's
facts: the layers present, the grid, the time base (`CURRENT_YEAR`, `BAND_ONE_HOUR_OF_YEAR`, `HOUR_OF_YEAR` from
`[Simulation] StartDateTime`), the weather band count, the ignitions and `SIMULATION_TSTOP`. Settings are the
user's; facts are read off the case, because a namelist that disagrees with its rasters fails with errors that
name the wrong thing.

The builder also writes combinations ELMFIRE handles badly in a safe form:

- the ember outputs are written off when `ENABLE_SPOTTING` is off (with them on ELMFIRE dies in MPI with
  `Fatal error in internal_Reduce: Invalid buffer pointer`);
- `USE_BLDG_SPREAD_MODEL` is written off unless all five building layers are there;
- a switch that needs a raster (`USE_SDI`, `USE_ERC`, `USE_PYROMES`, `USE_LAND_VALUE`, `USE_POPULATION_DENSITY`,
  `USE_REAL_ESTATE_VALUE`) is written off, with a comment, for a case without it; a `*_BY_PYROME` switch only when
  its CSV is in `inputs/` (otherwise `severe (29): file not found, unit 100`);
- per-fuel-model spotting settings are written for all 304 fuel models (`KEY(0:303) = 304*value`); unsubscripted,
  Fortran would set fuel model 0 only;
- `RANDOMIZE_RANDOM_SEED` is refused: it makes `SEED` inert.

**A hand-edited `elmfire.data`.** The build compares the file's SHA-256 with the `GeneratedNamelistSha256` it
recorded in `case_sources.txt`. When they differ — or nothing was recorded, which is every case built before
v1 — the file is moved to `elmfire.data.kept-<time>` before the new one is written. The log lists every key the
two disagree on and says whether every raster the kept file names is on the case grid, i.e. whether it would
still run. The GUI warns before a build (step 5, with **Keep running it**) and names the kept file afterwards
(with **Run elmfire.data.kept-...**).

**To run a hand-tuned namelist**, name it in `[ELMFIRE] NamelistTemplate`. It is used as it is; only
`PATH_TO_GDAL`, `SIMULATION_TSTOP`, the five required outputs and the fuel table are written into it at run time.
When the template is the case's own `elmfire.data` (step 5's **Keep running it**), builds leave that file alone.
Rasters the template names in `inputs/` are re-cut with the case when the grid changes; a hand-made raster that
lives elsewhere, or one the scenario itself reads, is not — name hand-made layers (Mati's `fbfm40_roads101`) as
source layers under Fuels, canopy and buildings instead, so they survive any re-cut.

**Which namelist a run uses**: `NamelistTemplate` if named; else the case's `elmfire.data`; else the only `*.data`
in the case folder. Before ELMFIRE starts, every raster that namelist names is checked against the grid of the
DEM it names; a raster on another grid stops the run with the file and both grids named (ELMFIRE itself would
segfault or report `Fuel Model raster dimensions mismatch (680 vs 541)`).

## Weather

The five weather rasters are one hourly series split into five files: 10 m wind speed (`ws`, mi/h), wind
direction (`wd`, degrees the wind blows from), and 1-, 10- and 100-hour dead fuel moisture (`m1`, `m10`, `m100`,
percent). They must agree on their band count, because ELMFIRE reads them all against one
`NUM_METEOROLOGY_TIMES`.

A case's weather is a **historical peak fire-weather day**:

1. **ERA5.** Hourly ERA5 reanalysis for the domain centre from Open-Meteo, 2000 to the last complete year,
   downloaded once into `climatology/` and reused. Fire-weather codes are derived for every hour: the Canadian
   FWI system's daily codes at 12:00 local standard time (from the longitude: 10:00 UTC at Mati, 17:00–20:00 UTC
   across the continental US), from km/h wind and the rain of the preceding 24 hours. Days from October to
   January get an FWI of 0, a northern-hemisphere fire season; see the [changelog's known
   issues](../CHANGELOG.md#known-issues) for what that means south of the equator.
2. **The day.** Each year's highest-FWI noon; one is drawn (`--weather-seed`, default 0) or named
   (`--weather-date`).
3. **Bands.** One per `DT_METEOROLOGY` (3600 s) for the fire's `SimulationTstopHours`, walking consecutive
   archive hours from the drawn day at the scenario's start hour — so hour 30 of a three-day fire is the second
   night of a real sequence, not the first day replayed. The archive is on UTC and the start hour is the
   scenario's local time; v1 does not convert between them (a [known issue](../CHANGELOG.md#known-issues)), so in
   the western US a 13:00 start reads the drawn day's early-morning hours.
4. **Wind.** A WindNinja solve per band from that hour's archive wind (`fine` mesh, `grass` vegetation), giving a
   terrain-resolved speed and direction on the grid; the edge cells outside WindNinja's mesh take the domain
   value. A failed solve carries the previous band forward. Without WindNinja: one wind value over the whole
   domain, which leaves k-PERIL no terrain variation — the build says which it did.
5. **Dead fuel moisture.** Nelson's model marched hourly over the 20 conditioning days before the day and
   through the fire, one stick per terrain class (elevation, slope, aspect, canopy cover), so moisture varies with
   the ground. Without it (it needs NFDRS4, which did not load on the Linux bench): uniform values.

Each stage falls back to uniform values on its own (`--wind`, `--wind-dir`, `--m1/--m10/--m100`) and says so;
`--no-climatology` writes uniform weather deliberately.

The weather is **kept** by later builds when it covers the fire. A run whose fire is longer than the case's
weather extends it by rebuilding the weather (one WindNinja solve per hour of fire); with a `NamelistTemplate`
it stops instead, since the template's weather is the user's. A one-band series is legal at any duration —
ELMFIRE holds it for the whole fire — but two or more bands must cover `SIMULATION_TSTOP`.

The run's reported weather (`[Weather]`) is read from the same archive at `WeatherAnchorDateTime`, the moment
band 1 was written from, so the temperature and humidity shown during the run are the day the fire burned.

A campaign draws its weather differently, per realization — see
[Trigger campaigns](trigger-campaigns.md#weather-per-realization).

## Running the case

A run of an ELMFIRE scenario, before the evacuation starts:

1. checks `SimulationTstopHours` is 1 to 240;
2. builds the case when `BuildCase=true`;
3. resolves the namelist, and extends the case's weather if it is too short;
4. resolves the executable and the GDAL tools;
5. writes the run's namelist — `PATH_TO_GDAL`, `SIMULATION_TSTOP`, the required outputs, the fuel table patched
   in — to `outputs/run.data` (the case's `elmfire.data` is never overwritten by a run);
6. checks every raster that namelist names is on the grid;
7. **reuses** the outputs of an identical earlier run, or runs ELMFIRE from the case folder, as one process on the
   namelist, with the GDAL tools and their own `PROJ_DATA` first on its `PATH`;
8. reads the outputs back through the same reader `[AscImport]` uses.

**Reuse.** A successful run writes `outputs/run.fingerprint`: a hash of the run namelist's text, the executable
file, and every file (by content) in the inputs, weather and miscellaneous folders the namelist reads —
ignoring GDAL's and ELMFIRE's own intermediates (`.aux.xml`, `.bsq`, `.hdr`, `.bil`, `.ovr`, `.xml`). The next
run reuses the outputs only when the fingerprint matches (`reusing ELMFIRE outputs computed from the same
namelist and inputs`), so a rebuild that changes nothing costs about a second, and any change — the ignition, the
stop time, one fuel cell — runs ELMFIRE again. `ReuseExistingOutput=false` always runs.

**Outputs.** ELMFIRE dumps `<stem>_<7-digit case>_<7-digit seconds>.tif` every `DTDUMP`; the run takes the
latest time-of-arrival dump and the rate of spread, spread direction, fireline intensity and midflame wind with
the same suffix. The run fails when ELMFIRE says so in its log (its own error lines come first in the message),
exits non-zero, or writes no arrival raster. A fire of **0 acres** is reported as `elmfire burned 0 acres (the
ignition most likely landed on non-burnable fuel)`. A fire stopped by `MAX_RUNTIME` is reported as truncated.

**Stopping.** The GUI's Stop and every way of quitting kill the ELMFIRE and WindNinja process trees at once. A
case build whose WindNinja was killed can still finish, on uniform wind; the GUI reports that step as stopped —
build the case again.

## The midflame wind

Every run sets `DUMP_MIDFLAME_WINDSPEED = .TRUE.` (ELMFIRE-WUINITY a7fb9d6 and later). ELMFIRE then writes
`outputs/mfws_<case>_<seconds>.tif` on the final dump: the **midflame wind speed in ft/min** for every cell the
fire reached (the 20 ft wind times the wind adjustment factor), nodata (-9999) elsewhere.

k-PERIL's length-to-breadth ratio (Anderson 1983) is defined for midflame wind in mi/h, so k-PERIL is given
`mfws / 88`. It used to be given the 10 m wind: on Mati realization 13, 14.4 mi/h at 10 m against 2.75 mi/h
midflame. The direction comes from the run's own `wd.tif`, each cell taking the band covering the time the fire
reached it; cells the fire never reached take the last band.

What this changes: the ellipse's length-to-breadth ratio. What it does not change much: the boundary. kPERILcore
breaks the ellipse down along its parametric angle, so above about 3 mi/h the flank rate is about half the head
rate whatever the ratio; on two Mati realizations midflame against 10 m wind moved the boundary by 1–2 % (Jaccard
0.98–0.99). Nothing caps the ratio.

For an imported fire, name its midflame raster as `[AscImport] MidflameWindSpeedFile`. Without one, k-PERIL
takes `[kPERIL] WindSpeedFile` *as* midflame wind and warns loudly.

## Units ELMFIRE does not declare

| Raster | Unit | Note |
|---|---|---|
| `time_of_arrival` | **seconds** from the simulation start | FARSITE/FlamMap/Prometheus use minutes: an `[AscImport]` of theirs needs `TimeOfArrivalUnits=Minutes`. |
| `vs` (rate of spread) | m/min | Only because `SPREAD_RATE_IN_M` is forced on; ELMFIRE's default is ft/min, and a reader expecting m/min then spreads the fire 3.28 times too fast. |
| `spread_dir` | degrees clockwise from north | |
| `flin` | kW/m | |
| `mfws` | **ft/min** | ÷ 88 for mi/h. |
| `ws` | **mi/h**, whatever `WS_AT_10M` says | `WS_AT_10M` only says the height (10 m, on). `WS_IN_KPH` is off. |
| `wd` | degrees, the direction the wind blows from | |
| `m1`, `m10`, `m100` | percent | `DEAD_MC_IN_PERCENT`. |
| `LH/LW_MOISTURE_CONTENT` | percent | `LIVE_MC_IN_PERCENT`. |
| `cc` | percent | `CC_IN_PERCENT`. |
| `ch`, `cbh` | metres, or decimetres with `CH_TIMES_10` / `CBH_TIMES_10` | LANDFIRE: decimetres. FIRE-RES: metres. |
| `cbd` | kg/m³, or ×100 with `CBD_TIMES_100` | LANDFIRE: ×100. FIRE-RES: kg/m³. |
| `dem`, `slp`, `asp` | metres; degrees; degrees clockwise from north | |

## ELMFIRE errors that name the wrong thing

| ELMFIRE says | What is wrong |
|---|---|
| `DEM CRS does not appear to use metre linear units` | GDAL's tools were not reachable; the DEM is fine. Install QGIS or OSGeo4W, or set `[ELMFIRE] PathToGdal`. |
| "complete, fire area N acres" but no GeoTIFFs in `outputs/`, exit 0 | A PROJ database that does not match the GDAL tools was first on `PATH` (SUMO ships an older `proj.db`), so the CRS lookup failed and every `gdal_translate` refused. The runner now puts the GDAL tools and their own `share/proj` first; if it still happens, set `PathToGdal` to a QGIS or OSGeo4W `bin`. |
| `Fatal error in internal_Reduce: Invalid buffer pointer` | An ember output on with `ENABLE_SPOTTING` off, in a hand-edited namelist. |
| `forrtl: error (65): floating invalid` | NaN in a raster that did not come through the builder. |
| `severe (29): file not found, unit 100` | A calibration table is named but not in `inputs/`. |
| `[ERROR] Not enough weather bands for given SIMULATION TSTOP` | The weather series is shorter than the fire; a generated case extends it itself, a template's must be extended by hand. |
| `Fuel Model raster dimensions mismatch (680 vs 541)`, or a segmentation fault | A raster on another grid. v1 checks this before ELMFIRE starts. |
| `Too many command options` (from `gdal_translate`) | A space in a path ELMFIRE passes to GDAL unquoted. A campaign refuses such paths up front. |
| `... is not specified and is a required input` | Usually canopy or fuel missing from a hand-written namelist. |

## Time and memory

- A 72 h fire on Mati takes about six minutes on one core (measured on the Linux bench); an 8 h fire, seconds.
- WindNinja takes about 5 s per band on a 15 km domain at the fine mesh, so a 72-band case spends about six minutes
  in it. The coarse mesh is ten times faster and smooths away most of the terrain effect.
- The five weather rasters are held at columns × rows × `WX_BANDS_KEPT_IN_MEM` bands (30 by default): 72 bands of a
  566 × 541 grid peaked at 814 MB held whole against 218 MB at 8 bands kept, with identical fires. Lower it for
  long fires on large grids.
- The ERA5 archive (26 years hourly) is downloaded once per case and is the same for every run and realization.
