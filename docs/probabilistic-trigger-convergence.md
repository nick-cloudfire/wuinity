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
2. **Derive dead fuel moisture** m1/m10/m100 with the Nelson model from the
   sampled weather (with an antecedent spin-up window).
3. **Run ELMFIRE** (Docker) for that single realization → `TOA/ROS/SD/FI` rasters.
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

## Dead fuel moisture — Nelson model

Historical fuel-moisture rasters are hard to source, so per realization we
derive m1/m10/m100 from the sampled weather using the existing Nelson dead fuel
moisture engine (`Source/Hazards/Wildfire/DeadFuelMoisture/`). Nelson is
time-marching, so it needs an antecedent hourly weather window to spin up.
Live-fuel and other ELMFIRE moistures not covered by Nelson are set seasonally
(TBD).

## ELMFIRE runner contract

WUInity provides the per-realization sampled inputs; a user-provided script runs
one ELMFIRE case and returns the rasters. Proposed contract (to finalize with
the script author):

- **Input**: a run id, ignition location (grid x,y or lat/lon), wind speed
  (m/s @10 m), wind direction (deg), m1/m10/m100 (%), and optional live-fuel
  moisture — passed either as CLI args or as a WUInity-written per-run
  `elmfire.data` + constant transient rasters.
- **Action**: run ELMFIRE in Docker for a single ignition / single meteorology.
- **Output**: `time_of_arrival_<id>.tif`, `vs_<id>.tif`, `spread_dir_<id>.tif`,
  `flin_<id>.tif` in a known folder (matching the existing mati naming so the
  downstream driver consumes them unchanged).

## Components / phases

| Phase | Component | Status |
|-------|-----------|--------|
| 0 | Climatology sampler (annual FWI-max distribution) | new |
| 0 | Nelson moisture wrapper (weather → m1/m10/m100) | new (engine exists) |
| 0 | Ignition sampler (mask → point) | new |
| 1 | ELMFIRE realization runner (Docker) | new — **blocked on the run script** |
| 2 | WUInity + k-PERIL per realization | ✅ built |
| 3 | Convergence controller (decile-area, 20-run/<2% streak) | new |
| 4 | CLI `converge-trigger` + Unity UI (ignition picker, climatology settings, live convergence view) | new |

## Risks

- ELMFIRE-in-Docker invocation is the critical path and gated on the run script.
- Wall-clock: every realization is one ELMFIRE run **plus** a full SUMO
  evacuation; convergence may need hundreds of runs.
- Nelson spin-up window and live-fuel handling need defining.
- RNG seeding for reproducibility of a converged result.
