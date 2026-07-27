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
`RealizationRunner.cs`. `converge-trigger` takes `--max` (a cap on how many realizations
may be consumed, whether read from `--dir` or generated via `--elmfire`; see Realization
generation) instead of a fixed `--count`, plus `--streak`/`--tolerance` (defaulting to 20/2%
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

**Now built**: that orchestration step is `ElmfireCaseBuilder`
(`PREACT/PREACTcore/Source/Utility/ElmfireCaseBuilder.cs`), driven by
`PREACTcli build-case` — see "Building a case from scratch" below.

Two things had to be fixed on the way, both found by actually running the code
rather than by review:

- **`SlopeAspect` returned a mirrored aspect.** Its `dzdx`/`dzdy` locals were
  named after the wrong axes — the "`dzdx`" kernel differentiates along y and
  vice versa — so `Atan2(dzdy, -dzdx)` produced a bearing reflected about the
  north–south axis. North and south slopes came out right, which is why the
  original synthetic-ramp check passed; east and west were swapped, and a
  north-east-facing slope read 315° instead of 45°. Now computed as the compass
  bearing of the downslope vector, `Atan2(-dzdEast, -dzdNorth)`, and verified
  against all four cardinal ramps plus a diagonal. Slope magnitude was never
  affected. This mattered: aspect drives ELMFIRE's slope-weighted spread, so
  every generated case would have burned the wrong way across east–west terrain.
- **`BuildUtmMasterGrid` did not clip to the requested domain.** It warped the
  whole source raster, which is harmless for a DEM downloaded to the domain's
  bounding box but silently wrong for a local or cached DEM covering a wider
  area — the master grid, and therefore ELMFIRE's whole computational domain,
  would inherit the source's extent instead of the domain asked for. There is
  now an overload taking the lat/lon corners, which reprojects all four (a
  lat/lon box is not a rectangle in UTM) and warps with `-te`.

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

## Building a case from scratch

`PREACTcli build-case` turns a `.wui`'s `[Simulation]` domain (lower-left lat/lon
+ domain size in metres) into a complete, runnable ELMFIRE case, so a campaign no
longer needs a hand-prepared `ELMFIRE/inputs` tree to exist first:

```
PREACTcli build-case --wui <case>\<name>.wui --out <case>\ELMFIRE ^
  --cellsize 30 --padding 2000 ^
  --fbfm13 <fuel.tif> --cc <cc.tif> --ch <ch.tif> --cbh <cbh.tif> --cbd <cbd.tif> ^
  --wind 11.8 --wind-dir 225 --m1 11.1 --m10 15.8 --m100 17.9 ^
  --gdal "C:\Program Files\QGIS 3.44.2\bin"
```

| Produced | How |
|----------|-----|
| `dem` | OpenTopography (COP30 by default), or `--dem <file>` for a local DEM |
| `slp`, `asp` | derived from the warped DEM (Horn's method, `SlopeAspect`) |
| `adj`, `phi` | constant 1.0 rasters, per WildfireAV's `makePhiAndAdjFiles.py` |
| `ws`, `wd`, `m1`, `m10`, `m100` | constant-value baseline weather (see below) |
| `ignition_mask` | all-ones (ignite anywhere) unless one is supplied |
| `elmfire.data` | namelist referencing exactly the layers that were produced |

Everything is snapped to the **master grid** established by the DEM: the local
UTM zone from the domain centre, clipped to the domain, at `--cellsize`. Any
user-supplied raster (`--fbfm13`, `--cc`, `--bldg_*`, …) is warped onto that same
grid — nearest-neighbour for categorical layers (fuel codes, ignition mask),
bilinear for continuous ones.

The domain is **padded** (`--padding`, default 2 km) on every side. A fire is
free to burn past the evacuation domain's edge, and clipping it there would
truncate the very spread the trigger boundary measures.

What it deliberately does **not** source, per "User-supplied (not auto-sourced)"
below: fuel model and canopy. Pass them in and they are reprojected; leave them
out and the namelist simply omits those keys. Same for the ELMFIRE-WUINITY fork's
building layers — the `&WUI` urban-spread group is only emitted when all five
`bldg_*` rasters are present, since a partial set makes ELMFIRE read a raster
nobody wrote.

### History-based weather (climatology → WindNinja → Nelson)

`WeatherRasterPipeline` builds the five weather rasters from the historical
record rather than from constants:

1. **Climatology** — hourly ERA5 for the domain centre via `OpenMeteoDownloader`,
   cached once per case (`<case>/climatology/<name>_era5_hourly.csv`) and reused,
   since it is identical for every realization. `ClimatologySampler` reduces it to
   one peak-FWI day per year and draws one, keeping that day's wind/temperature/
   RH/precipitation physically consistent with each other.
2. **WindNinja** — `WindNinja_cli` (auto-detected under `C:\WindNinja`) turns the
   drawn day's single domain-average wind into a terrain-resolved field.
3. **Nelson** — the in-process dead-fuel-moisture engine is marched hourly over
   the 20-day conditioning window ending on the drawn day, one stick per distinct
   terrain class, each with its own solar forcing.

Each stage degrades independently to a uniform fallback and *says so* in the
output, so a silently-placeholder case is impossible to mistake for a real one.

Verified on Mati (2015–2020 archive, seed 1): drew 2016-06-19, FWI 49.6, 39.6 °C,
RH 17%; WindNinja spread a 1.9 m/s domain wind into 0.3–8.2 mph across the
terrain; Nelson returned 3.7/5.6/8.6 % mean dead moisture varying per cell
(m100 8.2–10.7 %, damper on shaded slopes). ELMFIRE ran on the result to
3094 acres. Whole build takes ~8 s once the archive is cached.

Four bugs had to be fixed to get physically correct numbers out of it, none of
which review would have caught:

- **The 100-hour stick was a 10-hour stick.** `DeadFuelMoistureBin` built it with
  `createDeadFuelMoisture10`, so m10 and m100 were identical by construction.
- **Terrain had no effect on solar radiation.** `SunRadiation.SimpleRadiation`
  takes the hour as `HHMM` — internally it computes `hour / 100` in integer
  arithmetic — so an hour-of-day argument collapses to midnight and it returns 0
  for every cell. It also wants elevation in feet. With both fixed, a south slope
  gets 904 W/m² at noon against a north slope's 543. `SimpleRadiation` now
  documents both units on the parameters themselves, since nothing in the
  signature hints at either. (`CellDeadFuelMoisture` calls it the same wrong way,
  but its entire class body is inside a `/* */` block — it is dead code with no
  callers, so there is nothing live to fix there.)
- **Sampling one hour made the result hostage to drizzle.** ERA5 reported 0.3 mm
  at 13:00 on the drawn day — an area-average over ~9 km, not necessarily rain on
  the fuel — and Nelson correctly soaked the 1-hour stick from 3.7 % to 60 %. The
  driest day of the record came out sodden. The pipeline now takes the **minimum
  over the burning period** (10:00–18:00 by default), which is what this document
  specified all along ("captures the diurnal minimum … during the peak burning
  period") and is inherently robust to a single trace-rain hour.
- **`DeadFuelMoistureEngine`'s bin constructor bounded `x` by `yDim`**, so on any
  non-square raster the terrain classes occurring only in the columns past `yDim`
  were never created and those cells returned -1. The pipeline drives its own
  per-class sticks (it has to, for per-class solar), but the engine is fixed too.

### Burnable-only ignition

`build-case` zeroes the ignition mask wherever the fuel model cannot carry fire
(codes 0, 14 and the 91–99 block — non-burnable in both Anderson FBFM13 and
Scott & Burgan FBFM40 — plus NoData, which is what a clipped domain is padded
with). Disable with `RestrictIgnitionToBurnableFuel = false`.

This is the cheap half of WildfireAV's `_snap_to_valid_fuel`, and restricting the
*mask* is preferable to snapping the point afterwards because it leaves ignition
placement with ELMFIRE's own `RANDOM_IGNITIONS` rather than taking it over in C#.

It is not a tidiness measure. **64 % of the padded Mati domain is non-burnable**
(sea and urban), so an all-ones mask ignited open water most of the time: ELMFIRE
ran to completion, exited 0, wrote all four output rasters, and reported
`Fire area: 0.0 acres`. Nothing downstream noticed — k-PERIL produced an empty
boundary and the driver aggregated it as a successful realization, biasing every
decile toward zero. With the mask restricted to the 36 % that can burn, three
seeds on the previously-empty case gave 3990, 6099 and 4460 acres.

As a second line of defence `ElmfireRunner` parses ELMFIRE's own `Fire area:`
line and **fails** a realization that burned nothing, rather than passing it on.
An unparsed log is deliberately not treated as empty — absence of the line is not
evidence of a zero-area fire.

### The evacuation half's own weather download

Separate from the ELMFIRE weather chain above, `WeatherManager` fetches a year of
hourly weather for the *evacuation* simulation. It used to cache that against
`{Simulation.Name}_weather.csv` — and the driver overrides `Simulation.Name` per
realization, so that file never existed on a fresh run and **every realization
downloaded its own identical copy**. A 990-realization campaign made 990
identical Open-Meteo requests and left 990 × 1.4 MB of duplicate CSV in the case
folder. One of them (realization 0093) died mid-download and took the whole
process with it, discarding that realization's already-completed ELMFIRE run.

Three changes:

- The cache is now keyed on **location and year range** rather than the
  simulation name, so all realizations of a campaign share one file.
- It is written to a per-process temp file and moved into place, so a concurrent
  realization never reads a half-written cache and a died-midway download leaves
  nothing behind. Losing the move race to another realization is benign — the two
  files hold the same weather — so the loser just discards its copy.
- A download failure is caught and fails that realization cleanly instead of
  aborting the process.

Verified with 4 concurrent realizations from a cold cache: 4/4 successful, one
weather file on disk, no leftover temp files. Note what the logs show — the two
realizations that started before any cache existed both downloaded, and one of
them lost the move race and discarded its copy; the two that started later read
the shared file. So this bounds concurrent downloads by **`--parallel`, not by
campaign length**: 4 instead of 990, rather than 1. Closing that last gap would
need a cross-process lock or a serial pre-warm like the one `converge-trigger`
already does for the ELMFIRE-side ERA5 archive, and at 4 requests per campaign
it is no longer the thing that breaks long runs.

### Per-realization weather

`converge-trigger --realization-weather` runs the same chain **per realization**,
so the ensemble varies in weather and not only in ignition location. Each
realization draws its own historical day (seeded `--seed` + realization index, so
the campaign stays reproducible) and writes its own `ws`/`wd`/`m1`/`m10`/`m100`
into `_elmfire/<idx>/weather`, which the namelist's `WEATHER_DIRECTORY` points at
while `FUELS_AND_TOPOGRAPHY_DIRECTORY` still points at the one shared, read-only
terrain copy.

The ERA5 archive is fetched **once, serially, at startup** rather than by
whichever realizations happen to begin first — several concurrent processes
downloading and writing the same cache file is precisely the race that made the
existing per-realization `WeatherManager` download fail one run in ~95. A
realization whose weather chain throws falls back to the shared case-level
rasters rather than failing outright.

Verified on Mati (3 realizations, 2015–2020 archive): drew 2018-06-14
(10.9 mph, 6.4/10.3/14.0 %) and 2015-07-21 (17.4 mph, 4.1/5.6/8.9 %), 3/3
successful. Note two of the three drew the same day — with only 6 annual maxima
on record, repeat draws are expected from empirical resampling, and the fix is a
longer archive (the default 2000–last-complete-year gives ~25) rather than a
change to the sampler.

Variation on top of the drawn day still comes from the template's
`RASTER_TO_PERTURB` blocks, which `build-case` leaves as a commented placeholder.

**Verified end-to-end** on the Mati domain, built into a clean folder from
nothing but `mati.wui` + the user fuel/canopy/building layers: 21 layers on one
423×317 @ 30 m EPSG:32634 grid, ELMFIRE ran to `End of simulation reached
successfully` (1496 acres, all four output rasters, correctly georeferenced —
no `A_SRS=UNKNOWN`), and a 3-realization `converge-trigger` campaign against the
generated case produced three genuinely distinct fires and a probability raster
holding exactly `{0, ⅓, ⅔, 1}`.

### In-process GDAL

`MasterGrid`/`RasterHarmonizer`/`GeoTiffRasterWriter` are **now runtime-verified**
(the earlier "⚠️ warp untested" caveat is resolved) — EPSG lookup, warp, and
GeoTIFF creation all work from the CLI. Two things make that work, and both are
worth knowing before this runs on another machine:

- The GDAL C# bindings P/Invoke `gdal_wrap.dll` **by bare name**, so it must sit
  next to the executable or on `PATH`. PREACTcore's copy rules preserve a
  `Runtimes\Native\GDAL\x64` subpath that .NET's native resolver does not search,
  so `PREACTcli.csproj` now flattens those shims into its own output directory.
- The GDAL **core** (`gdal.dll` + PROJ data) still comes from a system install.
  On the development machine that resolves to **SUMO's** GDAL 3.9.3, which is
  self-consistent with its own `proj.db` and works. Note the vendored shims are
  built against the 3.9 ABI, so QGIS 3.44's `gdal311.dll` is *not* a drop-in
  substitute for the in-process path — which is separate from, and can differ
  from, the `--gdal` directory ELMFIRE shells out to.

## Realization generation

`converge-trigger` gets each realization's fire rasters one of two ways. By default it reads a
pre-generated set from `--dir` using indexed filename patterns. With `--elmfire <exe>
--elmfire-template <elmfire.data> --elmfire-inputs <folder>` it generates them on demand instead,
so the ensemble no longer has to exist up front.

The ensemble comes from **ELMFIRE's own Monte Carlo machinery**, not from a second sampler on the
C# side: the template keeps whatever `RANDOM_IGNITIONS` / `USE_IGNITION_MASK` /
`RASTER_TO_PERTURB` configuration it was written with, and only `SEED` changes per realization
(`NUM_ENSEMBLE_MEMBERS` is pinned to 1, since the unit of parallelism here is the realization).
This is how the reference Mati ensemble was produced, it keeps the fire physics in one place, and
it means a hand-tuned template behaves identically under the driver. Verified: two realizations
from one template produce genuinely different fires (max time-of-arrival 3596 s vs 3249 s).

Two things make concurrent realizations safe, both load-bearing:

- **Private scratch and outputs per realization, shared read-only inputs.** ELMFIRE converts every
  input GeoTIFF to an intermediate ENVI `.bsq`/`.hdr` before reading it, writing those next to the
  input *only when `SCRATCH` is unset*; with `SCRATCH` set they go there instead. Output names are
  not unique either — every realization dumps `time_of_arrival_0000001_*.tif` — so a shared outputs
  directory would have realizations overwriting each other.
- **A pinned GDAL/PROJ environment for the child process.** ELMFIRE calls `gdalsrsinfo` to learn
  the DEM's EPSG and writes its GeoTIFFs with `-a_srs` from the result. A conflicting PROJ data
  directory earlier on `PATH` — **SUMO ships one**, and SUMO is on `PATH` on any machine set up for
  WUInity's traffic half — makes that lookup fail, ELMFIRE falls back to `A_SRS=UNKNOWN`, every
  `gdal_translate` fails, and the run **still exits 0** having deleted its own `.bil` intermediates:
  a silent, total loss of the realization. `ElmfireRunner` therefore puts the GDAL bin directory
  first on the child's `PATH` and points `PROJ_DATA`/`PROJ_LIB` at its sibling `share\proj`.

Not yet wired: per-realization weather from the climatology/WindNinja/Nelson chain. Until those
land, weather variation is whatever the template's own `RASTER_TO_PERTURB` block specifies.

### Output and logs

The console carries only the driver's own lines — `PROGRESS n/N`, the per-realization streak, and
the final summary. Both child processes are redirected to files, because at `--parallel` width
their combined per-timestep output is unreadable and buries the driver's own messages:

| Path | Contents |
|------|----------|
| `_output/trigger_probability.asc` | the aggregated per-cell probability raster |
| `_output/trigger_convergence.csv` | one row per realization: per-decile areas, Δ vs. previous, running streak |
| `_output/0_trigger_<idx>.asc` | each realization's k-PERIL boundary |
| `_output/logs/realization_<idx>.log` | that realization's full PREACT/SUMO/k-PERIL output |
| `_elmfire/<idx>/outputs/*.tif` | that realization's fire rasters (TOA, vs, spread_dir, flin) |
| `_elmfire/<idx>/elmfire.log` | that realization's ELMFIRE output |

When a realization fails the driver prints the log path plus a short tail, so a redirected run
still says why it broke without needing the file opened. `--resume` reads `_elmfire/` and the
existing boundaries to skip completed work, so that directory is worth keeping between runs;
`_output/logs/` is never read back and can be deleted freely.

## Running the pipeline

### Build

```
dotnet build PREACT/PREACTcli/PREACTcli.csproj -c Release
dotnet build PREACT/PREACTexecute/PREACTexecute.csproj -c Release
WUInity\Assets\ThirdParty\elmfire\build\windows\make_windows.bat
```

The ELMFIRE build needs an ordinary `cmd` (it sources oneAPI's `setvars.bat` itself) plus the
Intel Fortran compiler, Intel MPI, and the MSVC toolset — `ifx` compiles the Fortran but links
against the Microsoft C runtime, so "Desktop development with C++" must be installed or the build
fails at link time with cryptic `lld-link` errors. It produces `build\windows\bin\elmfire.exe` and
stages `impi.dll` beside it, which is what lets the binary run outside an oneAPI shell — including
when spawned by this driver, which inherits WUInity's environment rather than a oneAPI one. Only
`elmfire` is built; upstream's `elmfire_post` is skipped as unused here.

### Runtime prerequisites

- **GDAL command-line tools.** ELMFIRE shells out to `gdalinfo`, `gdalsrsinfo` and
  `gdal_translate`; a QGIS or OSGeo4W install provides them. WUInity's own
  `Runtimes/Native/GDAL` does *not* — those are the C# binding's DLLs, with no executables.
- **SUMO**, for the evacuation half, as for any WUInity run.

### Invocation

```
PREACTcli.exe converge-trigger ^
  --wui <case>\<name>.wui ^
  --max 200 --parallel 8 --pad 4 --resume ^
  --elmfire <...>\build\windows\bin\elmfire.exe ^
  --elmfire-template <case>\ELMFIRE\<name>.data ^
  --elmfire-inputs <case>\ELMFIRE\inputs ^
  --gdal "C:\Program Files\QGIS 3.44.2\bin"
```

Drop the four `--elmfire*`/`--gdal` flags and pass `--dir` instead to consume a pre-generated
ensemble the original way.

**`--gdal` is effectively mandatory when generating realizations.** Without it ELMFIRE inherits
the ambient `PATH`, and on any machine set up for WUInity that includes SUMO — whose bundled
`proj.db` shadows GDAL's, breaking the EPSG lookup. ELMFIRE then falls back to `A_SRS=UNKNOWN`,
every `gdal_translate` fails, and **the run still exits 0** having deleted its own intermediates.
The failure is silent and total, so treat the flag as required rather than optional.

Other flags worth knowing: `--max` is a ceiling, not a target — the run stops early when the
convergence criterion is met. `--resume` reuses both existing ELMFIRE outputs and existing
boundaries, making an interrupted campaign continuable. `--parallel` is one ELMFIRE *and* one
SUMO process per slot, and SUMO dominates the wall clock.

### Unity constraints

Both `elmfire` and `Nelson-Dead-Fuel-Moisture` are submodules under `Assets/`, and Unity tries to
import everything it finds there — compiling .NET-only sources with its own older C#, loading
managed DLLs as game assemblies, and importing Fortran `.obj`/`.mod` files as FBX models and
AudioClips. Two mitigations are in place: `make_windows.bat` sets the Windows hidden attribute on
its `bin\`/`obj\` output every build (Unity skips hidden paths), and
`Assets/WUInity/Compatibility/IsExternalInit.cs` polyfills the marker type that C# 9 `init`
accessors need but netstandard2.1 lacks. The hidden attribute is not stored in git, so a fresh
clone needs it re-applied to Nelson's `bin\`/`obj\`. The durable fix is moving these submodules
out of `Assets/` — nothing in this pipeline requires them there, since the runner takes
`--elmfire <path>`.

## Components / phases

| Phase | Component | Status |
|-------|-----------|--------|
| 0 | Global DEM downloader (Copernicus GLO-30 / SRTM) + slope/aspect | ✅ built and wired (`OpenTopographyDownloader`, `SlopeAspect`, orchestrated by `ElmfireCaseBuilder` / `PREACTcli build-case`); API key via the Mapbox-token pattern, `$OPENTOPOGRAPHY_API_KEY`, or `--api-key`; `--dem <file>` skips the download entirely. **The download path itself is still unrun** — no key was configured on the development machine, so end-to-end verification used `--dem` |
| 0 | Raster harmonization (auto-UTM warp/clip/resample to master grid) | ✅ built and **runtime-verified** (`MasterGrid`, `RasterHarmonizer`, `UtmUtility`) — warp, EPSG lookup and GeoTIFF writing all exercised by `build-case`; gained a domain-clipping overload (see In-process GDAL) |
| 0 | Case builder (domain → complete ELMFIRE input set + namelist) | ✅ built (`ElmfireCaseBuilder`, `PREACTcli build-case`) — see Building a case from scratch |
| 0 | Climatology sampler (ERA5, 20-day conditioning, annual FWI-max distribution) | ✅ built (annual-maxima half; conditioning window still TBD — see `ClimatologySampler`) |
| 0 | Nelson wrapper for dead moisture (weather → m1/m10/m100) | ✅ built (`WeatherRasterPipeline`) — in-process engine, one stick per terrain class with per-class solar, minimum over the burning period; four correctness bugs fixed on the way (see History-based weather) |
| 0 | Fuel-dataset reader for live moisture (by-date LFMC) | new — dataset is in `WUInity/Assets/ThirdParty/LFMC/` as ~250 monthly GeoTIFFs (`LFMC_MAP_<year>_<month>.tif`), **not** NetCDF as earlier planned (confirmed by the user). So this is a by-date file lookup plus a `RasterHarmonizer` call onto the master grid — no NetCDF dependency needed |
| 0 | WindNinja step (terrain wind → ws/wd on master grid) | ✅ built (`WindNinjaRunner`) — the Windows installer's `WindNinja_cli.exe` is self-contained, so no `conda run` wrapper is needed; speed/direction are warped as vector components rather than as a bearing, and the NoData fringe outside WindNinja's mesh is filled with the domain average |
| 0 | Climatology → WindNinja → Nelson, wired per realization | new — `build-case` draws one historical day for the whole case; `converge-trigger` does not yet draw a fresh day per realization |
| 0 | Ignition valid-fuel snapping | ✅ built — `build-case` zeroes the ignition mask wherever the fuel model cannot burn, and `ElmfireRunner` now fails any realization that burns 0 acres instead of silently aggregating it (see Burnable-only ignition) |
| 0 | Ignition sampler (mask → point) | ✅ built (`IgnitionSampler`: uniform mask + weighted-raster sampling); valid-fuel snapping not yet ported |
| 1 | ELMFIRE realization runner + namelist writer | ✅ built (`ElmfireRunner`, wired into `converge-trigger --elmfire`): per realization the template is re-seeded and `elmfire` runs in its own directory. Ensemble variation comes from ELMFIRE's own Monte Carlo (`SEED` + the template's `RANDOM_IGNITIONS`/`RASTER_TO_PERTURB`), not a second sampler on the C# side — see Realization generation |
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
