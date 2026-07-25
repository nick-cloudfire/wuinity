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

WUInity provides the per-realization sampled inputs; a user-provided script runs
one ELMFIRE case and returns the rasters. Proposed contract (to finalize with
the script author):

- **Input**: a run id, ignition location (grid x,y or lat/lon), wind speed
  (m/s @10 m), wind direction (deg), m1/m10/m100 (%), and optional live-fuel
  moisture — passed either as CLI args or as a WUInity-written per-run
  `elmfire.data` + constant transient rasters.
- **Action**: run the native `elmfire` binary on the case `.data` (WildfireAV's
  `runElmfireCase` pattern) for a single ignition / single meteorology.
- **Output**: `time_of_arrival_<id>.tif`, `vs_<id>.tif`, `spread_dir_<id>.tif`,
  `flin_<id>.tif` in a known folder (matching the existing mati naming so the
  downstream driver consumes them unchanged).

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
grid**, and its local UTM zone is picked from lat/lon (WUInity already has
`SimulationData.GetUtmZone/GetUtmEpsg`).

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
| 0 | Global DEM downloader (Copernicus GLO-30 / SRTM) + slope/aspect | new — replaces LANDFIRE outside US |
| 0 | Raster harmonization (auto-UTM warp/clip/resample to master grid) | new (GDAL; UTM selection exists) |
| 0 | Climatology sampler (ERA5, 20-day conditioning, annual FWI-max distribution) | new (Open-Meteo wrapped) |
| 0 | Nelson wrapper for dead moisture (weather → m1/m10/m100) | new (engine exists; WildfireAV exe) |
| 0 | ECMWF fuel-dataset reader for live moisture (NetCDF, by-date LFMC) | new — dataset stored in repo |
| 0 | WindNinja step (terrain wind → ws/wd on master grid) | new (port from WildfireAV) |
| 0 | Ignition sampler (mask → point) | new |
| 1 | ELMFIRE realization runner + namelist writer | new — port from WildfireAV; user tunes template |
| 2 | WUInity + k-PERIL per realization | ✅ built |
| 3 | Convergence controller (decile-area, 20-run/<2% streak) | ✅ built (`converge-trigger`) |
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
  (OpenTopography key), **WindNinja** (CLI), and the Nelson exe — on top of
  SUMO/GDAL. All must be present for "any location" to hold end-to-end.
- Wall-clock: every realization is one ELMFIRE run **plus** a full SUMO
  evacuation; convergence may need hundreds of runs.
- Nelson conditioning window = 20 days (per WildfireAV `CONDITIONING_DAYS`).
- ECMWF fuel dataset: stored in-repo (NetCDF, potentially large); covers only
  2003–2021 at ~9 km, so LFMC is domain-uniform for Mati and the annual-maxima
  sample is bounded to 19 years. Needs a GDAL-NetCDF reader + by-date join.
- RNG seeding for reproducibility of a converged result.
