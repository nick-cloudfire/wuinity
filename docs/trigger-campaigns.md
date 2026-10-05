# Trigger campaigns

A single run gives one trigger boundary: for one fire, the ground from which the fire reaches the WUI area in
less time than the evacuation needs. Which fire is not known in advance, so a **trigger campaign** runs many:
each **realization** draws an ignition and a fire-weather day, runs ELMFIRE, and — when that fire reaches the
WUI area — runs the evacuation and k-PERIL on it. The boundaries are folded into a per-cell probability, and
the campaign stops when that probability map stops changing.

Run one from the GUI (Run > Trigger campaign, workflow step 13) or with
[`PREACTcli converge-trigger`](command-line-tools.md#converge-trigger--probabilistic-trigger-campaign); the GUI
starts the same command.

## Before you start

A campaign checks all of this before its first fire and stops with the reason if anything is missing:

- a **built ELMFIRE case** with a fuel raster, an ignition mask with at least one ignitable cell, and
  `wui_area.tif`, which the build makes from the evacuation groups (step 9) and which has to be their union as
  they are now — a campaign on a case built before the groups were last painted is refused, saying so (or, with
  `WuiAreaSource=Raster`, the scenario's own `WuiAreaFile`);
- the base scenario's **trigger boundary** on: `[TriggerBufferModule] Enabled=true`, `Module=kPERIL`, with
  `WuiAreaSource` `Raster` or `EvacuationGroupsCombined` (`EvacuationGroupsSeparate` gives one boundary per group,
  which a campaign cannot aggregate);
- the evacuation ready to run: population, SUMO network, destinations, groups (step 11 runs a single simulation,
  which is the quickest way to find out);
- **ELMFIRE built from a7fb9d6** (it must write the midflame wind), the GDAL tools, `PREACT.exe` built by the build
  script;
- **WindNinja**, or `--allow-uniform-weather` ("Allow uniform weather" in the window) to accept one wind value
  over the whole domain;
- the ERA5 archive, which the campaign downloads once if the case has none;
- no space in the path a realization uses to reach the case's inputs or the campaign folder (ELMFIRE passes paths
  to GDAL unquoted).

The campaign reads the scenario **on disk**; the GUI asks to save unsaved changes before it starts one.

## In the GUI

Run > Trigger campaign opens the window at any time — to set a campaign up, or to watch or cancel the one
running. Its **Run** is disabled, with the reason beside it, while step 13 is blocked, a simulation runs or a data
step runs.

| Field | CLI | Default |
|---|---|---|
| Maximum realizations | `--max` | 200 |
| Parallel realizations (0 = half the cores) | `--parallel` | 0 |
| Consecutive stable realizations | `--streak` | 20 |
| Per-decile tolerance (%) | `--tolerance` (as a fraction) | 2 |
| Run each fire until it stops by itself / Fire duration (hours) | `--hours until-stopped` / `--hours <h>` | until it stops (fitted weather); 72 h, 1–240, with historical-day weather |
| Draw each parameter from a fitted distribution | off: `--historical-day-weather` | on |
| Days per year in the fitted pool | `--candidate-days-per-year` | 10 |
| Draw live fuel moisture too | off: `--no-live-fuel-moisture` | on |
| Aim the wind from the ignition at the WUI area | off: `--no-wind-to-wui` | on |
| Allow uniform weather | `--allow-uniform-weather` | off |
| Advanced: other namelist, other inputs folder, copy the probability raster to | `--elmfire-template`, `--elmfire-inputs`, `--out` | – |

On **Run** the window first asks the CLI (`--inspect`) whether a campaign with these settings exists. If it has
finished realizations it offers **Reuse N realization(s)**, **Start over** or **Back**; a campaign already running
is refused. While it runs, workflow step 13 reads *Running*, and work that would change the case (Build fire
case, the Data menu) is disabled. **Cancel** closes the CLI's input, which stops it and every child process; after
15 s the window kills what is left. Quitting cancels a running campaign too. Finished realizations are always
kept.

## One realization

Realization *i* (7-digit id, `0000001` for the first) runs in `realizations/<id>/` of the campaign folder:

1. **Reuse.** With `--resume`, a realization whose record says it finished (`ok` or `not-threatened`) is taken as
   it is, before anything is drawn. One whose fire finished but whose evacuation failed reruns only the
   evacuation.
2. **Ignition.** Drawn from the case's ignition mask the way ELMFIRE draws: a cell with a positive weight, outside
   the template's `EDGEBUFFER` (60 m unless it says otherwise), on burnable fuel, with probability proportional to
   its weight. With `--no-wind-to-wui` ELMFIRE draws it itself from the mask. Fixed `[IgnitionPoint]`s are never
   used: a campaign whose every fire starts in one cell gives that one fire's boundary at probability 1.
3. **Wind direction.** By default the wind is aimed from the ignition at the centroid of `wui_area.tif`, and
   WindNinja is solved from that direction — the fires that matter for a trigger boundary are the ones blowing
   at the community. (ELMFIRE's own `POINT_WIND_TO_CENTER` aims at the domain centre, 3.4 km from Mati's WUI
   centroid, and replaces WindNinja's field with a constant.)
4. **Weather**, into `weather/` — [below](#weather-per-realization).
5. **Namelist.** The campaign's snapshot of the template (`template.data`) with: `SEED` = seed + *i*, one
   ensemble member, the ignition, every directory relative to the realization folder, the realization's own
   weather and band count (`NUM_METEOROLOGY_TIMES` = its bands, `METEOROLOGY_BAND_START` = `STOP` = 1 - the same
   fit a single run makes on the case's weather), the drawn live fuel moisture, `SIMULATION_TSTOP` = hours × 3600 (8760 h when the fire runs until it stops),
   `MAX_RUNTIME` = the wall-clock limit, and the five required outputs. The fuel tables are the campaign's own
   copies; a template that runs the building spread model without a building fuel table gets ELMFIRE's own
   `building_fuel_models.csv`, or the campaign is refused up front.
6. **ELMFIRE** runs. No spread (0 acres, or only the ignited cell) → *not-threatened*. Stopped by the wall-clock limit → *failed* (truncated).
   No midflame raster → *failed* (the ELMFIRE build is too old).
7. **Does the fire reach the WUI area?** If no cell of `wui_area.tif` has an arrival time, the realization is
   *not-threatened* and no evacuation is run (three to five minutes of SUMO saved on Mati).
8. **Evacuation and boundary.** `PREACT.exe` runs the base scenario with the realization's fire written into it:
   `Module=AscImport` on the realization's rasters (arrival times in seconds), `[AscImport]
   MidflameWindSpeedFile` = its `mfws`, `FirelineIntensityFile` = its `flin` (or cleared) and `FuelModelFile` = the
   case fuel it burned (or cleared) - never the base scenario's own - `[kPERIL] WindDirectionFile` = its own
   `wd.tif`, `WindSpeedFile` cleared,
   `WindBandSeconds` = the template's `DT_METEOROLOGY`, `WuiAreaSource=Raster` with `WuiAreaFile` = the WUI area
   step 7 checked (the case's `wui_area.tif`), `[Landscape]` = the case's
   dem/slp/asp (k-PERIL's slope and aspect then cover the whole fire grid), and `[Simulation] RandomSeed` = seed +
   2 000 000 + *i*. Its outputs are moved into `preact/`, and a copy of the scenario it ran, with paths
   rebased so it opens where it is, is kept as `preact_scenario.wui`. Exit 0 with a boundary file is *ok*;
   anything else is *failed*, with the log's last lines in the message.
9. The boundary is folded into the probability map.

Every realization is independent of the others and of the order they finish in; up to `--parallel` run at once,
each as its own processes.

### Seeds

| Draw | Seed |
|---|---|
| ELMFIRE (`SEED`) | seed + *i* |
| Weather | seed + *i* |
| Ignition (wind aimed at the WUI area) | seed + 1 000 000 + *i* |
| Evacuation (`[Simulation] RandomSeed`) | seed + 2 000 000 + *i* (never 0) |

`--seed` defaults to 12345. The same settings reproduce the same realizations; a resume that reruns only an
evacuation reproduces its departures. The base scenario's own `RandomSeed` is not used.

## Weather per realization

Every realization draws its own weather into its own folder; terrain and fuel are shared and read-only.

**Fitted distributions (default).** The pool is the `--candidate-days-per-year` (10) highest-FWI days of each year
of the ERA5 archive, taken at local noon (FWI derived from km/h wind and 24 h rain — see
[ELMFIRE cases](elmfire-cases.md#weather)). A normal is fitted to each of temperature, relative humidity and 10 m
wind speed over the pool, and each realization draws each parameter independently, truncated only to physical
limits, so it can reach a day worse than any on record. The draw is held for the whole fire as **one band** — one
WindNinja solve per realization.

- **Direction**: aimed at the WUI area (above), or with `--no-wind-to-wui` resampled from one pool day.
- **Dead fuel moisture**: the 1-hour value from the drawn temperature and humidity (Simard's equilibrium
  moisture), +1 and +2 points for the 10- and 100-hour fuels (a field convention, Rothermel 1983). Uniform over the
  domain; Nelson cannot run without a real antecedent series.
- **Live fuel moisture**: herbaceous and woody resampled as a pair from one pool day of the NFDRS4 GSI model marched
  over the whole record, written as the namelist's `LH/LW_MOISTURE_CONTENT` constants. `--no-live-fuel-moisture`
  keeps the template's.

What this gives up: parameters drawn independently can combine into a day the record never had (hot and humid),
and there is no diurnal cycle. The pool depth is a severity dial as much as a sample size: on Mati, going from
1 to 10 days a year moved the fitted mean temperature 36.1 → 34.2 °C and humidity 16.5 → 20.7 %.

**Historical days (`--historical-day-weather`).** Each realization draws one year's peak day and replays it as a
case build does: one band per hour of fire from real consecutive hours, a WindNinja solve per band, Nelson over
20 conditioning days. Physically consistent and terrain-varying, but the ensemble can only contain the days that
happened — 25 or so on a 25-year record, each drawn many times — and a 72 h fire costs 72 WindNinja solves per
realization.

`weather_distributions.csv` in the campaign folder describes the pool and the fits; `weather_realizations.csv`
lists what each realization whose fire was used actually drew, with its mean and standard deviation.

## Convergence

After each realization with a boundary (*n* of them so far):

1. **P(cell)** = the fraction of the *n* boundaries that contain the cell.
2. For each decile τ = 0.1, 0.2, …, 1.0, the **area** of cells with P ≥ τ.
3. For each decile whose previous area is non-zero, the relative change |area − previous| / previous.
4. If every such change is below `--tolerance` (2 %), the **streak** grows by one; if any is not, it goes back to
   0. The first boundary, which has nothing to compare with, moves it neither way; the first comparison (the
   second boundary) already counts. So a campaign whose boundaries are all alike converges after `--streak` + 1 of
   them (verification case 4).
5. The campaign has **converged** when the streak reaches `--streak` (20). Realizations still running are killed;
   their records stay unfinished, so a resume with a longer streak computes them.

Reaching `--max` first is not an error: the raster is written with the warning that it is not yet stable, and
`--resume` with a higher `--max` continues (`--max`, `--streak`, `--tolerance` and `--parallel` are not part of
the campaign's identity). Realizations are folded in the order they finish; a resumed campaign folds its reused
ones in index order, so where exactly a campaign and its resume converge can differ by a few realizations.

`trigger_convergence.csv` holds one row per boundary: the count, the realization, the streak, the ten decile
areas (m²) and their relative changes.

## Statuses

| Status | Meaning | In the trigger probability | In the fire statistics |
|---|---|---|---|
| `ok` | A boundary was computed. | yes | yes |
| `not-threatened` | The fire never reached the WUI area (or burned nothing). No evacuation was run. | no — see below | yes |
| `failed` | ELMFIRE or the evacuation failed, or the fire hit the wall-clock limit. The message says why and names the log. The campaign goes on; `--resume` runs it again. | no | only if its fire completed |

The **trigger probability is conditional on the fire reaching the WUI area**: its denominator is the `ok`
realizations. It answers "given a fire that reaches the community, how likely is this cell to lie inside the
trigger boundary?", not "how likely is a fire to threaten the community". The share of `not-threatened`
realizations (in `realizations.csv`) answers the second question for the ignitions the mask allows.

Fires stopped by `--max-runtime-minutes` are counted as failed and the campaign ends with a warning saying how
many: they are the slowest fires, usually the largest, so leaving them out biases the probability towards small
fires. The limit is part of the campaign's identity, so a larger one is a new campaign.

## The campaign folder, resuming and the lock

Everything a campaign writes is in `<scenario folder>/_output/campaign_<Name>_<hash>/` — `<Name>` the scenario's
`[Simulation] Name` (with characters other than letters, digits, `-` and `_` replaced by `_`), `<hash>` the first
8 hex digits of a SHA-256 over every setting that decides what the realizations produce. The files are listed
in [Output files](output-files.md#trigger-campaigns).

`campaign.json` records those settings (`settings`), their hash, and information that does not affect the
realizations (`information`: resolved paths, the PREACT build). The settings are: the scenario file's name and
content, the template's content, the case inputs' content (except the five weather rasters, which realizations
never read), the fuel and building tables, the ELMFIRE executable, the hours, the wall-clock limit, the seed, the
weather mode, pool depth, live moisture, wind aiming, uniform-weather acceptance, the archive (name, years,
format), conditioning days, WindNinja and its mesh, the scenario start, and the evacuation seed rule.

- **A new campaign with settings that already have a folder** (no `--resume`): the old folder is moved aside as
  `..._replaced_<yyyyMMdd_HHmmss>` and a fresh one started. Nothing is deleted.
- **`--resume`** reuses that folder's finished realizations and reruns its failed ones. With no folder for these
  settings it starts one; when only folders with **other** settings exist it is refused, listing how the newest
  differs (`differs in: hours: 24 -> 12`), because their realizations are not the ones these settings produce.
- **`--resume-only`** folds in the finished realizations without running any (it does not need PREACT).
- **`--inspect`** reports, without running anything, whether a folder for these settings exists, how many
  realizations it would reuse, whether it is running, and how the others differ.
- **The locks.** A running campaign holds `campaign.lock` in its folder open without sharing; the operating system
  releases it however the process ends. A second campaign on the same folder is refused. It also holds the case's
  `campaign.lock` (in the case folder whose inputs it reads) shared, so campaigns of several scenarios can run on one
  case; a case build — GUI or CLI, from any scenario — is refused while any campaign holds either lock, because
  every realization reads the case.
- **Snapshots.** The template and the fuel tables are copied into the folder once and every realization reads
  those, so editing the case's own files mid-campaign cannot reach half of it.
- **Campaigns from before v1** (`_output/trigger_convergence.csv`, `_output/0_trigger_<n>.asc`,
  `_elmfire/<n>/`) cannot be resumed: the layout and the identity are new. Nor can a `campaign_<Name>_<hash>/`
  whose `campaign.json` has no `evacuation.seed` setting: it was made before v1's k-PERIL orientation fix and
  per-realization evacuation seeds. Step 13 warns about such a campaign, the Results window says so, and Run starts a
  new campaign beside it. The ERA5 archive's format and, with historical-day weather, the band clock are part of the
  identity too, so a campaign made with an earlier v1 build before `archive_format=3` also starts afresh.

## Cancellation

| How | Effect |
|---|---|
| Ctrl+C, SIGTERM | Kills every ELMFIRE, WindNinja and PREACT process tree the campaign started; exit 3. |
| stdin closing, with `--cancel-on-stdin-close` | The same — this is the GUI's Cancel, and what happens if the GUI goes away. |
| The GUI's Cancel | Closes stdin; kills the CLI's process tree after 15 s if it has not gone. |
| Killing the CLI hard (Task Manager) on Windows | Its children die with it: the CLI puts itself in a kill-on-close job object. |
| `kill -9` on Linux | Leaves the children running; nothing in-process can prevent it. |

A cancelled realization is neither a result nor a failure; `--resume` computes it. A cancelled or converged
campaign starts no further WindNinja band for a realization whose weather is being made.

## Reading the results

**`trigger_probability.asc`** — per cell, P(inside the trigger boundary | the fire reaches the WUI area), 0 to 1,
on the fire grid. A cell at 0.9 lies inside the boundary in 90 % of the fires that reached the community: a fire
crossing it leaves no more time than the evacuation needs in nine cases out of ten. The decile contours (0.1, 0.5,
0.9) are the natural lines to draw; the p50 contour is the median boundary, the p90 contour a boundary that almost
every threatening fire needs. `trigger_probability_live.asc` is the same map as it stood after the latest
boundary; while it is the only one, the campaign is running or was stopped.

**`ensemble_burn_probability.asc`** — the fraction of all completed fires (`ok` and `not-threatened`) in which the
cell burned.

**`ensemble_arrival_earliest/mean/p10/p50/p90.asc`** — arrival time in seconds from ignition, over the fires that
burned the cell (**conditional on burning**; nodata where none did). Read them with the burn probability: a cell
with a 5 % burn probability and an early p10 is threatened rarely but fast. The low percentiles are the
conservative ones — p10 is when the fire arrives in the fastest tenth of the fires that arrive at all.
Percentiles come from an hourly histogram (coarser bins above 96 h of fire, 3 h at 240 h; up to 96 h when the fire
runs until it stops, later arrivals sharing the last bin) and are reported at the bin's lower edge, so never later
than the truth.

All rasters are ESRI ASCII on the fire grid with a `.prj` beside them, and open in QGIS. In the GUI, Results >
Show on map draws them.

## Time, memory and disk

A Mati realization (72 h fire, fitted weather) costs about six minutes of ELMFIRE, one WindNinja solve (seconds)
and, when the fire arrives, three to five minutes of SUMO for about 900 cars. Plan on minutes per realization per
parallel slot, and on 60–200 realizations for a stable map.

- **`--parallel`** defaults to half the logical cores. SUMO dominates the wall clock and needs several GB per
  realization: a 7 GB machine ran out of memory with two at once (exit 137, recorded as failed). Size it by memory
  first.
- **`--hours`** defaults to `until-stopped` with fitted (one-band) weather: `SIMULATION_TSTOP` is a year and
  ELMFIRE's stall exit ends each run when the front stops moving and no ember is in flight. No duration has to be
  guessed, and no fire is cut short and counted as "did not threaten". Mati realization 13 stops at 100 h after 7 min.
  This needs an ELMFIRE with the ember-tracker fix (ELMFIRE-WUINITY 16f306f): before it, an ember that blew to the
  domain edge was never retired, the stall exit never fired with spotting on, and the same fire ran to its wall-clock
  limit (the pre-v1 2000 h campaign took 7–21 h per realization this way). A number of hours still works (1 to 8760).
  Historical-day weather has one band per hour of fire, so it needs hours: 72 by default, 1 to 240.
- **`--max-runtime-minutes`** defaults to 240 when the fire runs until it stops, else 2 minutes per hour of fire, at
  least 60 (144 min for 72 h). It only stops a fire that is not going to finish; that realization counts as failed.
  If every realization fails this way, the ELMFIRE build predates the fix above.
- **Historical-day weather** costs one WindNinja solve per hour of fire per realization (about six minutes for
  72 h), and holds more weather bands in memory.
- **Disk**: each realization keeps its rasters, weather, logs and PREACT outputs — tens of MB. `scratch/` can be
  deleted afterwards.
