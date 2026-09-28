# Command-line tools

Two console programs host the engine without Unity. `build.ps1` / `build.sh` build both — see
[Building](building.md). On Linux the executables are `PREACT` and `PREACTcli`; `dotnet PREACT.dll` and
`dotnet PREACTcli.dll` work on every platform.

| Program | Built to | For |
|---|---|---|
| `PREACT.exe` | `PREACT/PREACTexecute/bin/Release/net8.0/` | Running a scenario. |
| `PREACTcli.exe` | `PREACT/PREACTcli/bin/Release/net8.0/` | `build-case`, `converge-trigger`, `global-gpw-to-pop`. |

---

## PREACT.exe — run a scenario

```
PREACT <file.wui> [<numberOfRuns> [<batchSize> [<simulationIndexOffset>]]]
```

| Argument | Default | Meaning |
|---|---|---|
| `file.wui` | – | The scenario. It must load with nothing critical outstanding, or nothing is run. |
| `numberOfRuns` | `1` | The most runs to make, one after another in this process (SUMO allows one instance per process). They stop early once the average evacuation time (RSET) has changed by less than 2 % for 10 runs in a row. |
| `batchSize` | – | Accepted for compatibility and ignored. For parallel runs start several processes, as the campaign does. |
| `simulationIndexOffset` | `0` | Index of the first run, used in output file names. |

Progress goes to the console; results to `_output/` beside the `.wui` — see [Output files](output-files.md).
With `Module=ELMFIRE` the run builds the case first if `[ELMFIRE] BuildCase=true`, then runs ELMFIRE (or reuses
an identical earlier fire) before the evacuation starts — see [ELMFIRE cases](elmfire-cases.md).

| Exit code | Meaning |
|---|---|
| `0` | Every run finished and nothing reported an error. |
| `1` | Nothing was run: no arguments, a file that is not there, a scenario that did not load as runnable (`The scenario did not load as runnable; see the items listed above.`), or a run count that is not a positive integer. |
| `2` | A run stopped on an error or an error was reported during it (`The run reported N error(s); see the log above.`) — k-PERIL refusing to compute a boundary for lack of evacuation arrivals among them. |

On Linux, GDAL 3.10's `libgdal.so.36` must be on `LD_LIBRARY_PATH`, and `SUMO_HOME` must point at a SUMO 1.22
whose `bin` (or `lib`) holds `libsumocs` — see [Building: Linux](building.md#linux).

---

## PREACTcli — utility commands

```
PREACTcli <command> [options]
```

Run without arguments it prints every command's options and exits 0. Every command parses its options
strictly:

| Problem | Message | Exit |
|---|---|---|
| An option it does not know | `Unknown option: --x` | 2 |
| An option that takes a value, without one | `--x needs a value.` | 2 |
| A value that does not parse, or is out of range | `--x takes a whole number, not '2x'.` / `--x takes a number (with a '.' for decimals), not '...'` / `--x must be between A and B, not N.` | 2 |
| A retired option | `--x is no longer an option: <what to do instead>` | 2 |
| An unknown command | `Unknown command: <name>`, then the usage | 2 |
| `probabilistic-trigger` | `probabilistic-trigger is gone: converge-trigger generates the realizations with ELMFIRE and runs until the probability raster is stable.` | 2 |
| Anything the command did not handle | `ERROR: <command> stopped on an unexpected error: <message and causes>`; every child process it started is killed | 1 |

### `build-case` — build a scenario's ELMFIRE case

Builds the case exactly as the GUI's **Build fire case** does: the domain, cell size, padding, fire duration,
source layers, ignition points and painted areas all come from the `.wui`, read with the same parser. Options
override single settings. How a case is put together is in [ELMFIRE cases](elmfire-cases.md).

```
PREACTcli build-case --wui <scenario.wui> [--out <case dir>] [options]
```

| Option | Default | Meaning |
|---|---|---|
| `--wui <file>` | – | **Required.** The scenario. It must have `[Simulation]` and an `[ELMFIRE]`-configured wildfire module. |
| `--out <dir>` | `[ELMFIRE] CaseDirectory` (`elmfire` beside the `.wui`) | Where to build. |
| `--hours <h>` | `[ELMFIRE] SimulationTstopHours` | Fire duration, 1 to 240 hours. Decides how many hourly weather bands the case gets. |
| `--cellsize <m>` | `[ELMFIRE] CellSizeMetres` (30) | Grid resolution, 1 to 1000 m. |
| `--padding <m>` | `[ELMFIRE] PaddingMetres` (2000) | Margin added on every side of the domain, 0 to 100000 m. |
| `--dem <file>` | – | A local DEM (any CRS) to cut the grid from, instead of downloading one. It should cover the padded domain. |
| `--dem-type <t>` | `COP30` | `COP30`, `COP90`, `SRTMGL1` or `SRTMGL3`, for an OpenTopography download. |
| `--api-key <key>` | `OPENTOPOGRAPHY_API_KEY`, then `WUInity/Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt` | OpenTopography key. |
| `--gdal <bin>` | found: `[ELMFIRE] PathToGdal`, `PATH`, QGIS, OSGeo4W, `SUMO_HOME\bin` | GDAL tools folder, written into the namelist's `PATH_TO_GDAL`. |
| `--copy <file>` | – | Copy a loose file (e.g. `building_fuel_models.csv`) into `inputs/`. Repeatable. |
| `--painted <file>` | `[WildfireModule] GraphicalFireInputFile` | The painted areas (`.gfi`). |
| `--painted-grid <raster>` | the `[Landscape]` reference raster | The grid a legacy painting was made on. |
| `--canopy-dataset <dir>` | `[ELMFIRE] CanopyDatasetFolder` | FIRE-RES pan-European canopy rasters, for any of cc/ch/cbh/cbd not named individually. |
| `--rebuild` | off | Replace every layer the case already has (`[ELMFIRE] RebuildExistingLayers`). |
| `--fbfm40`, `--fbfm13`, `--cc`, `--ch`, `--cbh`, `--cbd`, `--bldg_area_avg`, `--bldg_separation_distance`, `--bldg_nonburnable_frac`, `--bldg_footprint_frac`, `--bldg_fuel_model`, `--ignition_mask`, `--barriers` `<tif>` | the scenario's `[ELMFIRE]` source layers | A source raster for that stem, warped onto the grid. Overrides the scenario. |
| **Weather** | | |
| `--weather-archive <csv>` | `<case>/climatology/<Name>_era5_hourly.csv` | The ERA5 hourly archive. A file you name is copied into `<case>/climatology/` and the copy is used; yours is never rewritten. |
| `--climatology-from <year>` | `2000` | First year of the archive, 1940 to 2100. |
| `--climatology-to <year>` | an existing archive's own years; else the last complete calendar year | Last year. |
| `--weather-date <yyyy-MM-dd>` | – | Use this historical day instead of drawing one. |
| `--weather-seed <n>` | `0` | Seeds which annual peak day is drawn. |
| `--conditioning-days <n>` | `20` | Days of antecedent weather Nelson is marched over, 1 to 365. |
| `--burning-from`, `--burning-to <hour>` | `10`, `18` | Local hours (0 to 23) of the drawn day over which the logged dead-moisture minimum is taken. Only that log line depends on them. |
| `--windninja <exe>` | `[ELMFIRE] WindNinjaExe`, then `WINDNINJA_CLI`, `PATH`, `C:\WindNinja`, Program Files | WindNinja. |
| `--wn-mesh <m>` | `fine` | `coarse`, `medium` or `fine`. |
| `--wn-vegetation <v>` | `grass` | `grass`, `brush` or `trees`. |
| `--no-climatology` | off | No archive: write uniform weather from the fallbacks below. |
| `--wind <m/s>`, `--wind-dir <deg>` | `5`, `0` | Uniform wind where a stage cannot run (0 to 100 m/s, 0 to 360°, direction the wind blows from). |
| `--m1`, `--m10`, `--m100 <%>` | `6`, `7`, `8` | Uniform dead fuel moisture where a stage cannot run. |

Retired: `--tstop` (the duration is `--hours` now, in hours) and `--force` (a case folder is always built into;
`--rebuild` replaces layers it keeps).

It refuses to build while a trigger campaign of the same scenario holds its lock (`a trigger campaign is running
(...) and every one of its realizations reads this case's rasters ...`), and refuses a fire duration outside 1 to
240 h (`[ELMFIRE] SimulationTstopHours: the fire duration is 500 h; it has to be between 1 and 240 hours (it is
hours, not seconds). Pass --hours.`).

When it finishes it prints the grid, the layers written, carried from an old grid, kept, missing or defaulted,
the fuel stem, the ignitions, every fallback, the namelist (and any hand-edited one it set aside), and the
weather day drawn. **It does not rewrite the `.wui`**: it prints the keys the scenario should then name —
`[Landscape] ElevationFile/SlopeFile/AspectFile` and `[kPERIL] WuiAreaFile` — which the GUI's build sets itself.

| Exit code | Meaning |
|---|---|
| `0` | The case was built and its rasters agree with each other. |
| `1` | The `.wui` is not there or its `[Simulation]`/`[ELMFIRE]` sections could not be read; the hours are out of range; a campaign is running; the build failed (`build-case failed: <message>`); or the finished case is not internally consistent (the rasters stay on disk to inspect). |
| `2` | Bad arguments. |

### `converge-trigger` — probabilistic trigger campaign

Runs ELMFIRE realizations, each from its own ignition and weather; for every fire that reaches the WUI area, an
evacuation and a k-PERIL boundary through `PREACT.exe`; and folds the boundaries into a per-cell probability
raster until it stops changing. The GUI's Run > Trigger campaign drives this command. What it does, and how to
read its results, is in [Trigger campaigns](trigger-campaigns.md).

```
PREACTcli converge-trigger --wui <base.wui> --max <N> [options]
PREACTcli converge-trigger --wui <base.wui> [options] --inspect
```

| Option | Default | Meaning |
|---|---|---|
| `--wui <file>` | – | **Required.** The base scenario. Its `[TriggerBufferModule]` must be enabled with `Module=kPERIL`, and its wildfire module must be ELMFIRE-configured with a built case. |
| `--max <n>` | – | **Required** (not with `--inspect`). Most realizations to run, at least 1. A ceiling, not a target. |
| `--hours <h>` | `72` | Hours of fire per realization, 1 to 240. |
| `--seed <n>` | `12345` | Campaign seed. Realization *i* uses seed+*i* for ELMFIRE and its weather, seed+1 000 000+*i* for its ignition, seed+2 000 000+*i* for its evacuation. |
| `--start <n>` | `1` | Index of the first realization. |
| `--streak <n>` | `20` | Consecutive stable realizations needed to converge. |
| `--tolerance <f>` | `0.02` | Largest relative change of every decile's area that counts as stable. |
| `--parallel <n>` | half the logical cores | Realizations in flight at once (1 to 1024). Each is its own ELMFIRE, WindNinja and PREACT/SUMO processes. |
| `--max-runtime-minutes <m>` | 2 per hour of fire, at least 60 | Wall-clock limit per ELMFIRE run (`MAX_RUNTIME`). A fire stopped by it counts as failed. |
| `--resume` | off | Reuse the finished realizations of the campaign with exactly these settings. Refused, with the differences listed, when only campaigns with other settings exist. |
| `--resume-only` | off | Aggregate that campaign's finished realizations without running any (implies `--resume`). |
| `--inspect` | off | Say whether a campaign with these settings exists, what it would reuse, whether it is running and how the others differ; then exit 0. |
| `--out <file>` | – | Also write the final probability raster here. |
| `--preact <exe>` | found | PREACT: looked for in `PREACT/PREACTexecute/bin/Release/net8.0/`, then beside PREACTcli, then the Debug build, by the platform's name (`PREACT.exe` or `PREACT`). |
| `--elmfire <exe>` | `[ELMFIRE] ElmfireExe`, else the build in `WUInity/Assets/ThirdParty/elmfire` | ELMFIRE. |
| `--elmfire-template <namelist>` | `[ELMFIRE] NamelistTemplate`, else the case's `elmfire.data` | The namelist each realization is patched from. |
| `--elmfire-inputs <dir>` | the template's `FUELS_AND_TOPOGRAPHY_DIRECTORY`, else `<case>/inputs` | The case's inputs folder. |
| `--gdal <bin>` | `[ELMFIRE] PathToGdal`, else found | GDAL tools folder. |
| **Weather** | | |
| `--historical-day-weather` | off | Replay whole historical peak days (one band per hour of fire, a WindNinja solve each, Nelson) instead of drawing each parameter from normals fitted to them. |
| `--candidate-days-per-year <n>` | `10` | Highest-FWI days per year in the pool the normals are fitted to (1 to 366). |
| `--no-live-fuel-moisture` | off | Keep the template's live fuel moisture instead of drawing it from the NFDRS4 GSI march. |
| `--no-wind-to-wui` | off | Keep each draw's own wind direction instead of aiming it from the ignition at the WUI area. |
| `--weather-archive <csv>` | the case's `climatology/<Name>_era5_hourly.csv` | The ERA5 archive. A file you name is copied into the campaign folder and the copy is used. |
| `--climatology-from <year>`, `--climatology-to <year>` | `2000`, the archive's last complete year | Archive range, 1940 to 2100. |
| `--conditioning-days <n>` | `20` | Nelson spin-up window (historical-day weather), 1 to 365. |
| `--windninja <exe>` | `[ELMFIRE] WindNinjaExe`, then the probe | WindNinja. |
| `--wn-mesh <m>` | `fine` | `coarse`, `medium` or `fine`. |
| `--allow-uniform-weather` | off | Run without WindNinja (uniform wind) and with Nelson or the live moisture march failing (uniform moisture), instead of stopping before the first fire. |
| **Process** | | |
| `--cancel-on-stdin-close` | off | Treat the end of stdin as a cancel. The GUI passes it, so closing the pipe — or the GUI going away — stops the campaign. |

Retired, and refused with what to do instead:

| Flag | Now |
|---|---|
| `--tstop` | `--hours`, in hours. |
| `--dir`, `--toa`, `--ros`, `--sd`, `--fi` | Pre-generated ensembles are no longer read; realizations are generated with ELMFIRE. |
| `--pad` | Realization indices are always 7 digits. |
| `--shared-weather`, `--realization-weather` | Every realization draws its own weather. |
| `--single-band-weather` | Fitted weather is one band by construction; historical-day weather covers the whole fire. |
| `--fitted-weather`, `--wind-to-wui`, `--live-fuel-moisture` | They are the defaults. |
| `--max-weather-bands` | Historical-day weather always covers the whole fire. |
| `--progress-json` | Progress lines are always written. |
| `--diagnostics` | The convergence CSV is always written. |

Everything goes into `<scenario folder>/_output/campaign_<Name>_<settings hash>/` — see
[Output files](output-files.md#trigger-campaigns).

| Exit code | Meaning |
|---|---|
| `0` | Finished: converged, or reached `--max` with at least one boundary (it then warns `reached --max N realizations (...) without converging; the probability raster is not yet stable.`). Also after `--inspect`. |
| `1` | Could not start (the checks below), `--resume` refused, another process holds the campaign's lock, or no realization produced a boundary (`no realization produced a usable trigger boundary (...); nothing to aggregate.`). |
| `2` | Bad arguments. |
| `3` | Cancelled: Ctrl+C, SIGTERM, or stdin closing with `--cancel-on-stdin-close`. Every ELMFIRE, WindNinja and PREACT it started is killed; finished realizations are kept and `--resume` continues. |

Before the first realization it checks, and stops with exit 1 and the reason, when: the base scenario is
missing or has no k-PERIL boundary; `[kPERIL] WuiAreaSource=EvacuationGroupsSeparate` (one boundary per group
cannot be aggregated per realization); the template, inputs folder, ELMFIRE, GDAL tools or PREACT cannot be
found; the template names no fuel raster, or rasters that are not on the case grid; the ignition mask is missing
or has no positive cell; `wui_area.tif` is missing or empty while it is needed; there is no fuel model table;
WindNinja is missing without `--allow-uniform-weather`; the ERA5 archive cannot be fetched or has no day with a
non-zero FWI; or a path ELMFIRE will see from a realization contains a space.

#### Lines for the GUI

Besides its readable log, the command writes tagged lines on stdout that the GUI parses:

| Tag | Content |
|---|---|
| `CAMPAIGN_DIR <path>` | The campaign folder, once at start. |
| `PROGRESS <done>/<max> realization <id> <status>` | After every realization. |
| `PROGRESS_JSON {...}` | After every realization: `realization`, `status`, `nSuccess`, `nNotThreatened`, `nFailed`, `streak`, `streakTarget`, `converged`, and — only after a realization with a boundary — `deciles`, `area` (m²) and `delta` (`null` where a decile has no baseline yet). |
| `PROGRESS_RASTER <path>` | The live probability raster was rewritten. |
| `CAMPAIGN_INSPECT {...}` | The `--inspect` answer: `match`, `folder`, `ok`, `notThreatened`, `failed`, `started`, `running`, and `others` (each with its `folder` and `differences`). |

### `global-gpw-to-pop` — population CSV from GPW

Turns a Gridded Population of the World (GPW) dataset and an OSM road network into the
[population CSV](input-file-format.md#population-csv) the engine reads. The domain is the OSM file's node
bounds; each populated GPW cell is split into households and snapped to the nearest road.

```
PREACTcli global-gpw-to-pop --gpw <dir> --osm <file> --out <file> [--minhh <n>] [--maxhh <n>]
```

| Option | Default | Meaning |
|---|---|---|
| `--gpw <dir>` | – | **Required.** Folder holding the 8-sector GPW ASCII dataset (`*.asc`). |
| `--osm <file>` | – | **Required.** OSM road network, `.osm`/`.xml` or `.pbf`. |
| `--out <file>` | – | **Required.** The CSV to write. |
| `--minhh <n>` | `1` | Smallest household, 1 to 100. |
| `--maxhh <n>` | `5` | Largest household, 1 to 100. |

Exit 0 when written; 1 when the folder or file is not there or no population was generated; 2 for bad
arguments. The GUI's Population step (WorldPop to households) is the usual way to make this file.
