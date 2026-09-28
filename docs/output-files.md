# Output files

A run writes into `_output/` beside the `.wui`; an ELMFIRE fire's own rasters stay in the case folder; a trigger
campaign writes everything into one folder of its own under `_output/`. In the GUI, Results > Results of the last
run and campaign lists them and Results > Show on map draws the rasters; Results > Open output folder opens
`_output/`.

## A single run

Files are named after `[Simulation] Name` (`<Name>` below) and the run index `<i>` (0 for one run;
`PREACT <file> <n>` makes one set per run). A new run overwrites files with the same names.

| File | Written when | Contents |
|---|---|---|
| `<Name>.log` | always | The run's log, starting with the scenario check made when it loaded. |
| `<Name>_<i>_arrivalData.csv` | traffic on | One line per car reaching a destination: the simulation time (s) it arrived. |
| `<Name>_traffic_output_<i>.csv` | traffic on | One row per time step: `Time(s)`, cars injected, cars arrived, cars in the system, people out, SUMO cars injected and arrived, then for each destination people arrived, cars arrived and `flow [veh./h]` (arrivals over the last 300 s). |
| `<Name>_trafficData_<i>.tiff` | traffic on | Three-band GeoTIFF on a `[SUMO] OutputRasterSize` grid over the domain, in the simulation's UTM zone: 1 accumulated time vehicles spent on roads in the cell (s), 2 average level of service (actual speed ÷ speed limit), 3 average waiting time (s). No data is -9999. |
| `<Name>_traffic_average.csv` | traffic on | The arrival curve averaged over the runs: `Time [s],ArrivalIndex [-]`. |
| `<Name>_pedestrian_output_<i>.csv` | pedestrians on | One row per time step: `Households left` and `People left` (still to go), households and people who have responded and who have reached their car, cars activated, average walking distance. |
| `<i>_<OutputName>.asc` and `.prj` | trigger boundary on, and the fire reached the WUI area | The k-PERIL trigger boundary on the fire grid: 1 inside, 0 outside. `[kPERIL] OutputName` (default `trigger_boundary`), `.asc` added if it has no extension; with one boundary per evacuation group, one file each with the group's name before the extension. The `.prj` gives the grid's CRS. |

The run's log says how many households left because the fire came within `FireReactionDistance`
(`Households that left because of the fire's proximity: N (M people)`), the WRSET (the last-arrival time, which
k-PERIL uses), and which wind k-PERIL was given.

No boundary is written for an area the fire never reached (`The fire never reached wui, so no trigger boundary
was computed for it. ...`), nor when no car arrived anywhere (the required egress time would be zero; the run
then exits 2).

### The ELMFIRE fire of a single run

In the case folder ([ELMFIRE cases](elmfire-cases.md)), not in `_output/`:

| File | Contents |
|---|---|
| `outputs/time_of_arrival_0000001_<s>.tif` | Arrival time, seconds from the start. `<s>` is the dump time in seconds, 7 digits. |
| `outputs/vs_0000001_<s>.tif` | Rate of spread, m/min. |
| `outputs/spread_dir_0000001_<s>.tif` | Spread direction, degrees clockwise from north. |
| `outputs/flin_0000001_<s>.tif` | Fireline intensity, kW/m. |
| `outputs/mfws_0000001_<s>.tif` | Midflame wind speed, ft/min, on burned cells; -9999 elsewhere. |
| `outputs/crown_fire_...`, `ember_*`, `fire_size_stats.csv`, ... | Whatever else `[ElmfireNamelist]` asks ELMFIRE to dump. |
| `outputs/run.data` | The exact namelist the run used. |
| `outputs/run.fingerprint` | What the outputs were computed from; an identical later run reuses them. |
| `elmfire.log` | ELMFIRE's own output. |

## Trigger campaigns

Everything a campaign writes is in `_output/campaign_<Name>_<hash>/` (see
[Trigger campaigns](trigger-campaigns.md#the-campaign-folder-resuming-and-the-lock) for the name). Nothing is
added to `_output/` itself.

| File | Contents |
|---|---|
| `campaign.json` | `format`, `hash`, `created`, `settings` (everything that decides the realizations; hashed into the folder name) and `information` (resolved paths, the PREACT build, the WUI area). |
| `campaign.lock` | Held open while a campaign runs in this folder. A file nobody holds is no lock. |
| `template.data` | The namelist every realization is patched from, as it was when the campaign started. |
| `fuel_models.csv`, `building_fuel_models.csv` | The fuel tables every realization reads. |
| `trigger_probability.asc` (+ `.prj`) | The result: per cell, the fraction of boundaries that contain it. Written when the campaign ends with at least one boundary. |
| `trigger_probability_live.asc` (+ `.prj`) | The same map after the latest boundary, rewritten as the campaign runs. |
| `trigger_convergence.csv` | One row per boundary: `boundaries`, `realization_id`, `streak`, `area_p10` … `area_p100` (m² of cells at or above each decile), `delta_p10` … `delta_p100` (relative change from the previous row; empty where a decile has no baseline yet). |
| `realizations.csv` | One row per realization completed this run, reused ones included: `realization`, `status` (`ok`, `not-threatened`, `failed`), `reused`, `message`, `fire_area_acres`, `elmfire_minutes`, `ignition_x`, `ignition_y` (case CRS), `wind_from_deg`, `mean_wind_10m_mph`, `dead_1h_pct`, `live_herbaceous_pct`, `live_woody_pct`, `evacuation_seed`. |
| `weather_distributions.csv` | The weather pool the realizations were drawn from and the distributions fitted to it. |
| `weather_realizations.csv` | What each realization whose fire was used drew — temperature, humidity, 10 m wind speed and direction (and whether it was aimed or resampled), dead and live fuel moisture, evacuation seed — then the realized mean and standard deviation. |
| `ensemble_burn_probability.asc` | Fraction of all completed fires in which the cell burned. |
| `ensemble_arrival_earliest.asc` | Earliest arrival seen, seconds. |
| `ensemble_arrival_mean.asc` | Mean arrival, seconds, over the fires that burned the cell. |
| `ensemble_arrival_p10.asc`, `_p50`, `_p90` | Arrival-time percentiles, seconds, over the fires that burned the cell, at the lower edge of hourly bins (coarser above 96 h of fire). |
| `*.prj` | Beside every `.asc`, the fire grid's CRS. A WGS 84 / UTM grid's is written from the zone's definition when GDAL cannot find PROJ's database; any other CRS it cannot describe is left without one, with a warning in the log. |
| `realizations/<id>/` | One folder per realization (below). |

How to read the probability rasters is in [Trigger campaigns](trigger-campaigns.md#reading-the-results).

### A realization's folder

`realizations/<7-digit id>/`:

| Path | Contents |
|---|---|
| `realization.txt` | Its record, `key=value`: `stage` (`none`, `fire`, `done`), `status`, `message`, the paths of its rasters and boundary, the ignition and wind aim, fire area, ELMFIRE minutes, evacuation seed, and the weather it drew. A resume reads this before anything else. |
| `outputs/` | Its ELMFIRE rasters (as for a single run), with `run.data` (its namelist). |
| `weather/` | Its own `ws.tif`, `wd.tif`, `m1.tif`, `m10.tif`, `m100.tif`. |
| `scratch/` | ELMFIRE's intermediates; can be deleted. |
| `elmfire.log` | ELMFIRE's output. |
| `preact_scenario.wui` | The scenario PREACT ran, with paths made relative to this folder so it opens here. |
| `preact.log` | PREACT's output for the evacuation and boundary. |
| `preact/` | What PREACT wrote — `<Name>_<hash>_<id>*` logs and CSVs, and the boundary `0_<Name>_<hash>_<id>_trigger.asc`. |

While PREACT runs, the realization's scenario sits beside the base `.wui` as `__<Name>_<hash>_<id>.wui` (its
paths are relative to the scenario folder) and its outputs go to `_output/`; both are moved or removed when it
finishes.

A folder `campaign_<Name>_<hash>_replaced_<time>/` is an earlier campaign with the same settings, moved aside when
a fresh one was started without `--resume`.

### Files from before v1

A campaign before v1 wrote into `_output/` itself (`trigger_probability*.asc`, `trigger_convergence.csv`,
`0_trigger_<n>.asc`, `logs/realization_<n>.log`, `mati_prob_<n>_*`) and `_elmfire/<n>/` beside the scenario, with
index widths that differed between campaigns. v1 neither reads nor resumes them; they can be deleted or kept for
comparison.
