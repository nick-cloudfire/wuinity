# Convergence-driven probabilistic trigger boundaries

Design for the closed-loop Monte Carlo that computes a probabilistic k-PERIL
trigger boundary, running ELMFIRE realizations on demand and stopping on a
convergence criterion instead of a fixed count.

## The loop

For each realization until converged:

1. **Sample fire drivers**
   - Ignition location — from a user ignition mask (raster or drawn in Unity).
   - Wind speed + direction — from climatology (below).
   - Weather (temperature, RH, precipitation, solar) — from climatology.
2. **Fuel moisture**
   - Dead (m1/m10/m100) — Nelson model from the sampled weather (spin-up window).
   - Live (forest/shrub LFMC) — from the ECMWF derived fire-fuel dataset,
     read for the same historical day the weather sample was drawn from.
3. **Run ELMFIRE** (native binary, per WildfireAV) for that single realization → `TOA/ROS/SD/FI` rasters.
4. **Run WUInity** — AscImport plays the fire back, evacuation runs, and the
   run's own last-arrival evacuation time gives **WRSET** (a full evacuation is
   run every realization).
5. **k-PERIL** → this realization's trigger boundary.
6. **Aggregate** — recompute the per-cell probability map.
7. **Convergence check** (below). Stop when satisfied.

## Convergence criterion

- After realization *i*, per-cell probability `P_i(cell)` = fraction of the *i*
  realizations in which the cell lies inside the trigger boundary.
- Decile thresholds `τ ∈ {0.1, 0.2, …, 1.0}`. For each, the decile area is
  `area_τ(i) = (#cells with P_i ≥ τ) × cellArea`.
- Relative change `Δ_τ(i) = |area_τ(i) − area_τ(i−1)| / area_τ(i−1)`.
- **Converged** when, for **20 consecutive realizations**, `Δ_τ(i) < 2%` for
  **every** decile τ. If any decile exceeds 2% in a run, the streak resets.
- Zero-area deciles (early runs) are excluded from the test until they have a
  non-zero baseline.
- Deliverable: the converged probability raster plus per-decile
  area-versus-run diagnostics (to show the convergence history).

**Implemented** as `PREACTcli converge-trigger` (`PREACT/PREACTcli/ConvergeTrigger.cs`),
alongside the existing fixed-count `PREACTcli probabilistic-trigger`
(`ProbabilisticTrigger.cs`); both now share their per-realization run/read step via
`RealizationRunner.cs`. `converge-trigger` takes `--max` (a cap on how many pre-generated
realizations may be consumed — it does not run ELMFIRE itself, see the runner contract
below) instead of a fixed `--count`, plus `--streak`/`--tolerance` (defaulting to 20/2%
per this section), and writes `trigger_convergence.csv` (one row per realization: the
per-decile area and its Δ vs. the previous realization, plus the running streak) next to
the aggregated `trigger_probability.asc`. A decile only starts counting toward the streak
once its area has an established non-zero baseline, matching the exclusion rule above.

**Runs `--parallel` realizations concurrently** (default: CPU count), each still its own
PREACT.exe OS process via `RealizationRunner` — aggregation (the decile/streak state above)
stays single-threaded and folds in whichever realization completes next, which is valid
because realizations are i.i.d. Monte Carlo draws (order doesn't matter, only count does).
This is deliberate, not incidental: SUMO (the evacuation engine each realization spends most
of its wall-clock time in) runs via libsumo, a native library with process-global state —
`Engine.RunSimulationsParallel`'s own code comment already documents that in-process
multithreaded SUMO "can only run one instance per process" and doesn't work. The engine's
existing `RunSimulationsParallelProcess` mode (`Engine.cs`) works around exactly this by
spawning one OS process per simulation, which is the same shape `RealizationRunner` already
uses per realization — so `converge-trigger` overlapping several of those processes is safe
by the same reasoning already proven elsewhere in this codebase, and is where the real
wall-clock win is (an ELMFIRE run is comparatively quick; SUMO dominates). Verified with
synthetic realizations under concurrent scheduling: convergence triggers at the identical
`nSuccess` and decile-area/delta values as the strictly-serial version, and a streak-reset
(simulated by perturbing one realization) still resets/rebuilds correctly. Once converged,
already-in-flight realizations (up to `--parallel - 1` beyond the one that triggered
convergence) are allowed to finish rather than being killed, so a run may aggregate a few
more realizations than the strict minimum — not incorrect, just not maximally lean.

## Climatology sampling — annual fire-weather maxima

Uses the existing Open-Meteo historical archive client
(`OpenMeteoDownloader`, ERA5).

1. Pull hourly/daily weather for the area across all years on record.
2. For **each year**, compute a fire-weather index (FWI/FFMC — WUInity already
   has these calculators) over the fire season and pick that year's **peak
   fire-weather day**.
3. Take that day's full, physically-consistent set of conditions (wind speed +
   direction, temperature, RH, precipitation, solar).
4. Build the distribution (mean/std + empirical sample) of these annual-maxima
   days and sample from it per realization.

This focuses the Monte Carlo on the historic worst-day envelope for the area.

**Implemented** as `ClimatologySampler` (`PREACT/PREACTcore/Source/Utility/ClimatologySampler.cs`):
parses the CSV `OpenMeteoDownloader.Download` already writes, groups the noon
(FWI-bearing) rows by year, and keeps each year's highest-FWI day as that
year's peak. `ComputeStats` reports mean/std per variable (wind direction
excluded — it's circular, a linear mean is meaningless); `Sample` draws a
realization by uniformly resampling one of the actual historical peak days
(via the new `MonteCarloRng`, see Risks), keeping wind/temp/RH/precip/solar
physically consistent with each other rather than independently sampled.
Caveat: `FireWeatherIndex.CalculateDay` hard-zeroes FWI for October–January
(a Northern-Hemisphere fire-season assumption), which is fine for Mediterranean
domains like Mati but would suppress genuine peaks in the Southern Hemisphere
or tropical/dry-season climates — a real gap against "any location on Earth"
that needs fixing in the FWI engine itself, not the sampler.

## Fuel moisture — Nelson (dead) + ECMWF dataset (live)

Dead and live fuel moisture come from different sources, each playing to its
strength:

### Dead fuel moisture (m1/m10/m100) — Nelson

Derived per realization from the sampled weather using the existing Nelson dead
fuel moisture engine (`Source/Hazards/Wildfire/DeadFuelMoisture/`). Nelson is
hourly/time-marching, so it captures the diurnal **minimum** of the fine dead
fuels during the peak burning period — which a daily-mean product would miss and
run systematically wetter (less conservative for trigger boundaries). It needs
an antecedent hourly weather window to spin up, and adds no external dependency.

### Live fuel moisture (forest/shrub LFMC) — ECMWF derived fire-fuel dataset

Live fuel moisture varies slowly (seasonally) and has no good model-derived
source, so it is read from the **ECMWF derived fire-fuel/biomass dataset**
(`xds.ecmwf.int`, derived from ERA5-Land + satellite + ESA-CCI biomass):

- Variables used: LFMC high (forests) and LFMC low (shrubs), % of dry mass.
- Resolution ~9 km (0.07°), **daily**, global land, **2003–2021**, NetCDF4.
- Daily resolution is fine for slow-varying live fuels.
- The dataset is **stored in the repo as a local asset** (downloaded once, not
  fetched from the CDS API at run time). Proposed location:
  `Examples/<case>/climatology/` or a shared `data/ecmwf_fuel/` folder; read via
  GDAL's NetCDF driver (no new native dependency).
- Because it is ERA5-derived like the Open-Meteo weather, it is joined **by
  date**: when the climatology sampler selects a year's peak fire-weather day,
  the LFMC for that same day is read, keeping the live moisture physically
  consistent with the sampled weather. (Low fuel moisture co-occurs with high
  fire danger, so peak-danger days naturally yield dry live fuels.)

Caveats: ~9 km means the whole Mati domain is ~one cell (a single
domain-representative LFMC value — fine for the uniform-per-run inputs, but no
intra-domain spatial variation); the record is 19 years (2003–2021), which also
bounds the annual-maxima sample size.

## ELMFIRE runner contract

WUInity provides the per-realization sampled inputs; a script runs one
ELMFIRE case and returns the rasters. Verified against WildfireAV's actual
`pipeline/createElmfireInputFiles.py` (namelist writer) and
`pipeline/runElmfireCase.py` (runner) — an earlier version of this section
was inferred from public ELMFIRE docs and got several things wrong; this
replaces that with what the real pipeline does:

- **The namelist has no computational-domain group at all.** ELMFIRE reads
  EPSG/cellsize/xll/yll straight from `DEM_FILENAME`'s own georeferencing —
  `_build_namelist`'s `epsg_str`/`cellsize`/`xll`/`yll` parameters are computed
  by `_compute_domain` but never actually written to the file. There is no
  `A_SRS`/`COMPUTATIONAL_DOMAIN_*` key.
- **Ignition is `&SIMULATOR`'s `NUM_IGNITIONS` + indexed `X_IGN(1)`/`Y_IGN(1)`/
  `T_IGN(1)`**, not scalar `X_IGNITION`/`Y_IGNITION`. WildfireAV also **snaps**
  the raw ignition point to the nearest cell with a valid (burnable) fuel code
  (`_snap_to_valid_fuel`, threshold `>= 101` for Anderson FBFM40) before writing
  it — worth doing for any ignition sampler, though that exact threshold is
  LANDFIRE/FBFM40-specific and doesn't port directly to arbitrary global fuel
  models.
- **`&INPUTS` filenames are stems only** (no directory, no extension) —
  `FUELS_AND_TOPOGRAPHY_DIRECTORY`/`WEATHER_DIRECTORY` supply the directory and
  ELMFIRE appends its own extension. Full key set: `DEM_FILENAME`,
  `SLP_FILENAME`, `ASP_FILENAME`, `FBFM_FILENAME`, `CC_FILENAME`,
  `CH_FILENAME`, `CBH_FILENAME`, `CBD_FILENAME`, `ADJ_FILENAME`,
  `PHI_FILENAME` (static per-case), `WS_FILENAME`/`WD_FILENAME`/`M1_FILENAME`/
  `M10_FILENAME`/`M100_FILENAME` (per-realization weather), plus
  `DT_METEOROLOGY`, `LH_MOISTURE_CONTENT`/`LW_MOISTURE_CONTENT`, and
  `USE_BARRIERS`/`WS_AT_10M`/`BARRIER_FILENAME`.
- **`ADJ_FILENAME`/`PHI_FILENAME` are trivial**: `makePhiAndAdjFiles.py` just
  fills two rasters with `1.0`, same shape/CRS as the DEM. They are not
  related to ignition.
- **`WS_FILENAME`/`WD_FILENAME`/`M1_FILENAME`/`M10_FILENAME`/`M100_FILENAME`
  are real multi-band GeoTIFFs, one band per `DT_METEOROLOGY` step**
  (`wn_to_geotiff.py` stacks WindNinja's per-hour ASCII outputs with
  `gdalbuildvrt -separate`) — not the single-value scalar this doc originally
  assumed. `&MONTE_CARLO`'s `NUM_METEOROLOGY_TIMES` tells ELMFIRE the band
  count (this is just "how many weather timesteps", unrelated to our
  realization Monte Carlo).
- **`&OUTPUTS`** needs `OUTPUTS_DIRECTORY`, `DTDUMP`, `DUMP_TIME_OF_ARRIVAL =
  .TRUE.`, `CONVERT_TO_GEOTIFF = .TRUE.` — WildfireAV only turns on
  time-of-arrival dumping; the `vs_`/`flin_` outputs our own runner contract
  also wants aren't exercised by WildfireAV's validation use case, but their
  `DUMP_*` keys are now confirmed against ELMFIRE's own docs (see "Corrections
  from ELMFIRE's own docs" below) — `spread_dir_` is the one exception, with
  no corresponding native output.
- **`&TIME_CONTROL`**: `SIMULATION_DT`, `TARGET_CFL`, `SIMULATION_TSTOP`,
  `CURRENT_YEAR`, `HOUR_OF_YEAR` (hours since Jan 1 of `CURRENT_YEAR`).
- **`&MISCELLANEOUS`**: `PATH_TO_GDAL`, `SCRATCH`.
- **Runner**: `elmfire <case>.data`, executed with the case folder as the
  working directory; resumes by checking whether
  `outputs/time_of_arrival_*.tif` already exists (`run_elmfire`) — confirms
  what this doc already assumed.
- **Barriers** (`USE_BARRIERS`/`BARRIER_FILENAME`) are always populated in
  WildfireAV from rasterized OSM roads/waterways (`getBarrierFile.py`, US-only
  data sources) — this pipeline has no equivalent step yet, so barriers are
  left off by default rather than guessed.
- **WildfireAV does not do climatology/annual-maxima sampling at all** — its
  `downloadWeatherData.py` fetches ERA5 for a fixed window around one real
  historical fire's actual (satellite-derived) start/end time, for validating
  ELMFIRE against real fires. The "Climatology sampling" section above is a
  genuine WUInity-specific addition on top of the same building blocks
  (Open-Meteo, Nelson), not something to look for in WildfireAV.
- Nelson dead-fuel moisture in WildfireAV is a separate compiled C# exe
  (`applyNelsonModel.py`: GeoTIFF → ENVI/BSQ → `nelson_csharp <wxs> <dem.bsq>
  <slp.bsq> <asp.bsp> <cc.bsq> <conditioning_days>` → BSQ → GeoTIFF) producing
  real per-cell (not scalar) moisture from actual terrain. WUInity already has
  this same Nelson engine in-process (`Source/Hazards/Wildfire/
  DeadFuelMoisture/`) — the Phase 0 "Nelson wrapper" should call that
  directly rather than reimplement WildfireAV's exe/BSQ plumbing.

**Implemented**: `ElmfireRealizationWriter`/`ElmfireNamelistKeys`
(`PREACT/PREACTcore/Source/Utility/ElmfireRealizationWriter.cs`) patch a base
`elmfire.data` template with the real key/group set above, via the generic
`ElmfireNamelist.SetKeyInGroup` patcher (`ElmfireNamelist.cs`) — same
"clone template, patch known keys" convention `ProbabilisticTrigger` already
uses for `.wui` files, adapted to ELMFIRE's `&GROUP ... /` syntax. Wind/
moisture are written as constant-value multi-band GeoTIFFs via the new
`GeoTiffRasterWriter` (one band per `NUM_METEOROLOGY_TIMES`, all bands
holding the same Monte Carlo-sampled value) — correctly shaped as a real
ELMFIRE input, though content-wise still the "constant transient raster"
simplification rather than a genuine time-varying series. The namelist-patch
mechanics were runtime-verified against a template built from the real
`_build_namelist` output shape, including the parenthesized `X_IGN(1)`-style
keys (correct in-place replacement, no duplicate keys/groups). The multi-band
GeoTIFF writer itself shares the same GDAL-runtime caveat as
`RasterHarmonizer` below — compiles, not runtime-tested in this sandbox. The
runner (invoking the `elmfire` binary itself) is still unimplemented.

### Corrections from ELMFIRE's own docs

`elmfire`, `WildfireAV`, and `Nelson-Dead-Fuel-Moisture` are now real git
submodules under `WUInity/Assets/ThirdParty/` (the gitlinks existed but
`.gitmodules` was missing its URLs — fixed). ELMFIRE's own
`docs/archive/user_guide/io.rst`/`monte_carlo.rst` (this fork, pinned to the
`ELMFIRE-WUINITY` branch) confirm most of the above and correct two real bugs
that are now fixed in `ElmfireRealizationWriter`:

- **`WS_FILENAME` is always mph**, regardless of `WS_AT_10M` — that flag only
  tells ELMFIRE the raster is 10 m wind instead of its 20 ft default, it does
  not change the unit. The writer was passing `WindSpeedMps` straight through;
  it now converts to mph before writing the raster. It was also never actually
  setting `WS_AT_10M = .TRUE.` despite having the key defined — added.
- **`&OUTPUTS` was missing `DUMP_FLIN`/`DUMP_SPREAD_RATE`/`DUMP_SURFACE_FIRE`**
  — confirmed real keys (`DUMP_TIME_OF_ARRIVAL` alone, which is all
  WildfireAV's own validation use case needs, doesn't give k-PERIL the
  fireline-intensity/spread-rate rasters `AscImportInput` also reads). Added.
  **Still unconfirmed**: a native "spread direction" output — nothing in the
  documented `DUMP_*` list corresponds to it, so `AscImportInput`'s spread
  direction (`SD`) may need to be derived downstream from the time-of-arrival
  raster's gradient rather than read directly from an ELMFIRE output.
- **`&COMPUTATIONAL_DOMAIN` does exist** (`A_SRS`/`COMPUTATIONAL_DOMAIN_CELLSIZE`/
  `_XLLCORNER`/`_YLLCORNER`) as an optional explicit override — the earlier
  claim that there's "no computational-domain group at all" was too strong.
  WildfireAV's real writer (and this writer) still don't set it, relying on
  ELMFIRE's fallback to infer domain/CRS from `DEM_FILENAME`'s own
  georeferencing when the group is absent, which the docs confirm is
  supported (`"can be determined internally from the fuels inputs' metadata"`).
- **Nelson lineage confirmed, not just plausible**: `Nelson-Dead-Fuel-
  Moisture/DeadFuelMoisture.cs` (`namespace PREACT.Fire`) and this project's
  own `DeadFuelMoistureCSharp.cs` (`namespace PREACT.Wildfire`) are the same
  author, same Nelson/Bevins algorithm, near-identical size (2423 vs 2379
  lines) — the standalone repo is a BSQ/exe-wrapped build of (a lineage of)
  the same engine already running in-process here.

### A bigger option this surfaced: ELMFIRE's native Monte Carlo mode

ELMFIRE's `&MONTE_CARLO` group (`monte_carlo.rst`) natively supports almost
exactly what Phases 0/1/3 build externally:

- `RANDOM_IGNITIONS = .TRUE.` + `USE_IGNITION_MASK = .TRUE.` +
  `IGNITION_MASK_FILENAME` + `RANDOM_IGNITIONS_TYPE = 2` samples ignition
  points **weighted by a probability raster** — the same thing
  `IgnitionSampler.TrySampleFromRaster` does in C#.
- `NUM_METEOROLOGY_TIMES` + `METEOROLOGY_BAND_START`/`_STOP`/
  `_SKIP_INTERVAL` step through **non-contiguous blocks of a single stacked
  weather raster**, i.e. exactly "one block per climatology-sampled day",
  running one realization per block.
- `NUM_ENSEMBLE_MEMBERS` runs the **entire ensemble in one ELMFIRE
  invocation**, writing each realization's rasters with a sequential
  identifier prefix — which is already the exact shape
  `ProbabilisticTrigger`/`ConvergeTrigger` expect to read from disk.
- `CALCULATE_BURN_PROBABILITY = .TRUE.` computes a per-cell burn-probability
  raster across the whole ensemble natively (`burn_probability.tif`) — not a
  substitute for our trigger-boundary probability (that still needs
  WUInity's evacuation + k-PERIL per realization, which ELMFIRE can't do),
  but confirms the same "decile/probability aggregation" idea exists on the
  fire-only side too.

This is a real alternative to what's built: instead of our own C# code
spawning N separate ELMFIRE processes (one per realization, each with its own
patched namelist — the shape `ElmfireRealizationWriter` currently produces),
ELMFIRE could run the **whole realization ensemble in one process** via
`NUM_ENSEMBLE_MEMBERS`/`RANDOM_IGNITIONS`/`METEOROLOGY_BAND_*`, and our driver
would just read the resulting numbered rasters — which
`ProbabilisticTrigger`/`ConvergeTrigger` already do today, unchanged.

**Decided (for now): keep per-realization external orchestration.** Each
realization still needs its own full WUInity+SUMO+k-PERIL run regardless of
how ELMFIRE's share of the work is organized, and SUMO (via libsumo) is both
the dominant cost per realization and only safely parallelizable as separate
OS processes — which is exactly the shape `RealizationRunner`/
`ConvergeTrigger --parallel` already uses (see Convergence criterion). Since
that per-process shape is required for the SUMO/evacuation half no matter
what, and since it already lets multiple realizations run concurrently today
(each just currently *waits* for its ELMFIRE rasters to already exist rather
than producing them), collapsing ELMFIRE's part into one native ensemble
process wouldn't remove the need for N separate PREACT.exe processes — it
would only save ELMFIRE's own (comparatively small) per-run overhead. Revisit
this if ELMFIRE's per-process startup/static-input-loading cost turns out to
be large relative to a SUMO run once the runner is actually built and
measured; the option above is still there if so.

## Global automation: data sourcing & preprocessing

The vision is that a case can be built for **any location on Earth** from a
lat/lon + domain, with only the *fuel model* and *canopy* layers supplied by the
user (globally hard to source). Everything else on the hazard side is fetched
and prepared automatically. This mirrors the proven pipeline in the sibling
repo **`nick-cloudfire/WildfireAV`** (`pipeline/` step scripts) — port its
approach rather than reinventing it.

### Master-grid principle

One raster defines the **master grid** (CRS, cell size, extent); every other
layer is reprojected/clipped/resampled to it, so ELMFIRE's "all rasters share
one grid" requirement is met by construction. In WildfireAV the DEM/landscape
defines the grid (`createElmfireInputFiles._compute_domain` reads epsg, cellsize,
xll, yll straight from the DEM). We adopt the same: the **DEM is the master
grid**, and its local UTM zone is picked from lat/lon.

**Implemented**: `UtmUtility` (`PREACT/PREACTcore/Source/Utility/UtmUtility.cs`)
is the single lat/lon → UTM EPSG lookup, consolidated from three previously
separate copies (`SimulationData`, `WorldPopDownloader`, `LandfireDownloader`
each had their own). `MasterGrid` (`MasterGrid.cs`) reads a warped raster's
grid + EPSG back from disk; `RasterHarmonizer.BuildUtmMasterGrid` warps a
freshly-downloaded (WGS84) DEM into that UTM zone to establish it, and
`RasterHarmonizer.WarpToGrid` snaps any other raster onto it exactly (`-t_srs`/
`-te`/`-ts`, extending the single existing `Gdal.Warp` call in the codebase,
`WorldPopDownloader.ReprojectToUTM`, which only reprojects CRS without pinning
extent/pixel count). Compiles and was runtime-verified for the CRS-lookup half
(`UtmUtility`, matched Athens' known EPSG:32634); the warp calls themselves
could **not** be runtime-tested in this sandbox — GDAL's checked-in C# bindings
are built against GDAL 3.6's ABI (`libgdal.so.36`), and the only native GDAL
available to install here was 3.8.4, which isn't binary-compatible (missing
symbols) — so `WarpToGrid`/`BuildUtmMasterGrid` need a real run against a
GDAL-3.6-compatible install before trusting them beyond code review.

### 1. Topography / DEM downloader (where LANDFIRE is unavailable)

WildfireAV uses LANDFIRE (US-only) for dem/slp/asp + fuel/canopy. For global
coverage, replace the **topography** half with a global DEM:

- Source: **Copernicus GLO-30** (free, global 30 m) or SRTM, via the
  **OpenTopography API** (one key serves both) or AWS Terrain Tiles.
- Derive **slope** and **aspect** from the DEM (Horn's method — already used in
  `AscRaster`/k-PERIL); ELMFIRE can also derive them, but computing them keeps
  the grid explicit.
- The DEM download defines the domain/CRS for the whole case.

Fuel model + canopy (fbfm, cc, ch, cbh, cbd) remain **user-supplied** — outside
the US there is no clean global equivalent to LANDFIRE.

**Implemented**: `OpenTopographyDownloader`
(`PREACT/PREACTcore/Source/Utility/Downloaders/OpenTopographyDownloader.cs`) —
same async/retry shape as `LandfireLandscapeDownloader`/`WorldPopDownloader`,
takes the API key as a plain parameter (URL-building is factored out and unit-
tested separately from the network call). `SlopeAspect`
(`PREACT/PREACTcore/Source/Utility/SlopeAspect.cs`) is a standalone Horn's-
method implementation — k-PERIL's own copy (`kPERILcore/source/perilData.cs`,
`interpolateSlope()`) is private and bound to that vendored engine's instance
state, so this is a fresh implementation of the same formula rather than a
reach into third-party internals; verified against synthetic ramps (a 1:1
gradient plane gives exactly 45°, a flat plane gives exactly 0°, and
perpendicular ramps give different, correct aspect angles).

The **API key** is wired the same way WUInity already handles its Mapbox
token: a gitignored JSON file under a Unity `Resources` folder
(`WUInity/Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt`,
mirroring `Assets/Resources/Mapbox/MapboxConfiguration.txt`), read at runtime
via `OpenTopographyAccess.ApiKey`
(`WUInity/Assets/WUInity/Core/OpenTopographyAccess.cs`), with a committed
`OpenTopographyConfigurationTemplate.txt` showing the `{"ApiKey":""}` shape to
copy and fill in. `WUInity/.gitignore` gained the matching
`[Oo]pen[Tt]opography[Cc]onfiguration.txt` rule.

Not yet built: the orchestration step that actually calls
`OpenTopographyDownloader.Download` with a domain's lat/lon and
`OpenTopographyAccess.ApiKey`, then feeds the result through `SlopeAspect` and
`RasterHarmonizer` to produce the case's `dem`/`slp`/`asp` inputs — the pieces
exist, wiring them into one "build this case's topography" call doesn't yet.

### 2. Reprojection / harmonization step

Given the master grid, a GDAL warp step reprojects + clips + resamples each
input to it: the ECMWF LFMC (~9 km), the user's fuel/canopy, WindNinja wind, and
any moisture rasters. Auto UTM selection exists; the warp wiring is new. This is
what actually makes "any location" work.

### 3. Climatology sampler

Hourly **ERA5 via Open-Meteo** (WildfireAV's `downloadWeatherData.py` uses the
same source WUInity already wraps). Per realization it needs an antecedent
**conditioning window** for Nelson — WildfireAV uses `CONDITIONING_DAYS = 20`,
which settles the earlier "spin-up TBD". For the Monte-Carlo it selects each
year's **peak fire-weather (FWI) day** and builds the annual-maxima
distribution; taking the annual max of *daily* FWI needs no per-region fire-season
definition, so it works globally.

### 4. Reference pipeline (WindNinja + Nelson), per realization

Porting WildfireAV's per-case steps:

1. **DEM** → master grid (§1); slope/aspect derived.
2. **Weather**: Open-Meteo ERA5 hourly for the sampled day + 20-day conditioning
   window → RAWS-style series.
3. **WindNinja** (`WindNinja_cli` with a `.cfg` whose `elevation_file` is the
   DEM) → terrain-resolved **ws / wd** GeoTIFFs on the master grid. (ERA5 wind is
   coarse; WindNinja adds terrain realism.)
4. **Nelson** (existing C# dead-fuel-moisture engine; WildfireAV calls it as an
   exe) over the conditioned weather → **m1 / m10 / m100** GeoTIFFs.
5. **Live fuel moisture**: ECMWF LFMC for the sampled date (§ Fuel moisture) →
   the namelist's `LH_MOISTURE_CONTENT` / `LW_MOISTURE_CONTENT` (currently
   scalars in WildfireAV) or live-moisture rasters.
6. **User fuel/canopy** warped to the master grid.
7. **Namelist**: write `elmfire.data` from a template with the domain from the
   DEM (WildfireAV's `createElmfireInputFiles`).
8. **Run ELMFIRE** on the case's `.data` (WildfireAV runs the native `elmfire`
   binary in the case dir and resumes by checking for `time_of_arrival_*`
   output — the same resume trick our driver uses).

The **user will fine-tune the ELMFIRE input template**; this pipeline only has to
produce grid-aligned inputs and invoke the runner.

## Components / phases

| Phase | Component | Status |
|-------|-----------|--------|
| 0 | Global DEM downloader (Copernicus GLO-30 / SRTM) + slope/aspect | ✅ built (`OpenTopographyDownloader`, `SlopeAspect`); API key wired via the Mapbox-token pattern; not yet wired into a "build case topography" orchestration step |
| 0 | Raster harmonization (auto-UTM warp/clip/resample to master grid) | ✅ built, ⚠️ warp untested at runtime (`MasterGrid`, `RasterHarmonizer`, `UtmUtility` — see Master-grid principle) |
| 0 | Climatology sampler (ERA5, 20-day conditioning, annual FWI-max distribution) | ✅ built (annual-maxima half; conditioning window still TBD — see `ClimatologySampler`) |
| 0 | Nelson wrapper for dead moisture (weather → m1/m10/m100) | new — call the existing in-process engine directly (WildfireAV's exe/BSQ plumbing doesn't apply, see ELMFIRE runner contract) |
| 0 | ECMWF fuel-dataset reader for live moisture (NetCDF, by-date LFMC) | new — dataset stored in repo |
| 0 | WindNinja step (terrain wind → ws/wd on master grid) | new (port from WildfireAV; confirmed invoked via `conda run -n <env> WindNinja_cli <config>`) |
| 0 | Ignition sampler (mask → point) | ✅ built (`IgnitionSampler`: uniform mask + weighted-raster sampling); valid-fuel snapping not yet ported |
| 1 | ELMFIRE realization runner + namelist writer | 🟡 namelist writer built with keys verified against the real WildfireAV template *and* ELMFIRE's own docs; runner (invoking `elmfire`) still new — see ELMFIRE runner contract |
| 2 | WUInity + k-PERIL per realization | ✅ built |
| 3 | Convergence controller (decile-area, 20-run/<2% streak) | ✅ built (`converge-trigger`), now runs realizations `--parallel`-wide as concurrent OS processes (see Convergence criterion) |
| 4 | CLI `converge-trigger` | ✅ built | 
| 4 | Unity UI (ignition picker, climatology settings, live convergence view) | new — `ProbabilisticTriggerWindow.cs` only wraps the fixed-count `probabilistic-trigger` today |

User-supplied (not auto-sourced): **fuel model + canopy** rasters, and the
**evacuation scenario** (destinations/exits, groups, demographics, response
curves — human planning inputs).

## Risks

- ELMFIRE invocation is the critical path. **Decided: reuse WildfireAV's native
  `elmfire` runner** (run the binary on the case `.data`, resume via
  `time_of_arrival_*`); Docker is not used. Requires a working native ELMFIRE
  install (conda env) on the machine.
- Extra runtime dependencies for the global pipeline: a global DEM source
  (OpenTopography key) and **WindNinja** (CLI) — on top of SUMO/GDAL. Nelson
  does not need a separate dependency; WUInity's own in-process engine covers
  it (see ELMFIRE runner contract). All must be present for "any location" to
  hold end-to-end.
- Wall-clock: every realization is one ELMFIRE run **plus** a full SUMO
  evacuation; convergence may need hundreds of runs. **Partially addressed**:
  `converge-trigger --parallel` (default: CPU count) runs multiple
  realizations' PREACT.exe processes concurrently — safe because SUMO/libsumo
  can only run one instance per process anyway (confirmed via
  `Engine.RunSimulationsParallel`'s own code comment), so each realization
  was always going to be its own OS process; this just lets several run at
  once instead of strictly one at a time. Still bounded by machine core count
  and by SUMO itself being slow per-run — this reduces wall-clock, it doesn't
  remove the fundamental cost.
- Nelson conditioning window = 20 days (per WildfireAV `CONDITIONING_DAYS`).
- ECMWF fuel dataset: stored in-repo (NetCDF, potentially large); covers only
  2003–2021 at ~9 km, so LFMC is domain-uniform for Mati and the annual-maxima
  sample is bounded to 19 years. Needs a GDAL-NetCDF reader + by-date join.
- RNG seeding for reproducibility of a converged result. **Addressed**:
  `MonteCarloRng` (`PREACT/PREACTcore/Source/Utility/Math/MonteCarloRng.cs`) is
  an explicitly-seeded RNG (verified to reproduce an identical draw sequence
  given the same seed) used by `IgnitionSampler` and `ClimatologySampler`,
  deliberately separate from the pre-existing `PREACT.Math.Random` (a single
  unseeded process-wide instance with no reset/reproduction hook — still fine
  for non-Monte-Carlo uses, just not this one).
