# Command-line tools

Two console programs host the engine without Unity. `build.ps1` / `build.sh` build both — see
[Building](building.md). On Linux the executables are `PREACT` and `PREACTcli`; `dotnet PREACT.dll` and
`dotnet PREACTcli.dll` work on every platform.

| Program | Built to | For |
|---|---|---|
| `PREACT.exe` | `PREACT/PREACTexecute/bin/Release/net8.0/` | Running a scenario. |
| `PREACTcli.exe` | `PREACT/PREACTcli/bin/Release/net8.0/` | `build-case`, `converge-trigger`, `global-gpw-to-pop`. |

A standalone build has both in `dist\WUInity\PREACT\` ([Distribution](distribution.md)).

Both read the tool paths saved under the GUI's Help > External tools and keys, from `%APPDATA%\PREACT\tools.ini`
(Linux: `$XDG_CONFIG_HOME/PREACT/tools.ini` or `~/.config/PREACT/tools.ini`; or the file `PREACT_TOOLS_FILE`
names). A path there comes after the option or scenario key that names the tool, and before the search listed in
the defaults below. "Your setting" in the tables means that file.

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

`PREACT --help` (or `-h`, `help`) prints this usage and exits 0. "Simulation run executed, shutting down (exit code
N)." is printed only after a run.

Progress goes to the console; results to `_output/` beside the `.wui` — see [Output files](output-files.md).
With `Module=ELMFIRE` the run builds the case first if `[ELMFIRE] BuildCase=true`, then runs ELMFIRE (or reuses
an identical earlier fire) before the evacuation starts — see [ELMFIRE cases](elmfire-cases.md).

| Exit code | Meaning |
|---|---|
| `0` | Every run finished and nothing reported an error. |
| `1` | Nothing was run: no arguments (the usage is printed), a file that is not there, a scenario that did not load as runnable (`The scenario did not load as runnable; see the items listed above.`), or a run count that is not a positive integer. |
| `2` | A run stopped on an error or an error was reported during it (`The run reported N error(s); see the log above.`) — k-PERIL refusing to compute a boundary for lack of evacuation arrivals among them. |

On Linux, GDAL 3.10's `libgdal.so.36` must be on `LD_LIBRARY_PATH`, and `SUMO_HOME` must point at a SUMO 1.22
whose `bin` (or `lib`) holds `libsumocs` — see [Building: Linux](building.md#linux).

---

## PREACTcli — utility commands

```
PREACTcli <command> [options]
```

Run without arguments, or with `--help`, `-h`, `help` or `/?` (alone or after a command), it prints every
command's options and exits 0 - without loading GDAL, so it works where GDAL cannot load. Every command parses its
options strictly:

| Problem | Message | Exit |
|---|---|---|
| An option it does not know | `Unknown option: --x` | 2 |
| An option that takes a value, without one | `--x needs a value.` | 2 |
| A value that does not parse, or is out of range | `--x takes a whole number, not '2x'.` / `--x takes a number (with a '.' for decimals), not '...'` / `--x must be between A and B, not N.` | 2 |
| A retired option | `--x is no longer an option: <what to do instead>` | 2 |
| An unknown command | `Unknown command: <name>`, then the usage | 2 |
| `probabilistic-trigger` | `probabilistic-trigger is gone: converge-trigger generates the realizations with ELMFIRE and runs until the probability raster is stable.` | 2 |
| Anything the command did not handle | `ERROR: <command> stopped on an unexpected error: <message and causes>`; every child process it started is killed | 1 |
| GDAL's native libraries cannot be loaded (on Linux: no `libgdal.so.36` on `LD_LIBRARY_PATH`) | the same, naming the library that would not load (`libosr_wrap: cannot open shared object file ...`) | 1 |

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
| `--gdal <bin>` | found: `[ELMFIRE] PathToGdal`, your setting, `PATH`, QGIS, OSGeo4W, `SUMO_HOME\bin` | GDAL tools folder, written into the namelist's `PATH_TO_GDAL`. |
| `--copy <file>` | – | Copy a loose file (e.g. `building_fuel_models.csv`) into `inputs/`. Repeatable. |
| `--painted <file>` | `[WildfireModule] GraphicalFireInputFile` | The painted areas (`.gfi`). |
| `--painted-grid <raster>` | the `[Landscape]` reference raster | The grid a legacy painting was made on. |
| `--canopy-dataset <dir>` | `[ELMFIRE] CanopyDatasetFolder` | FIRE-RES pan-European canopy rasters, for any of cc/ch/cbh/cbd not named individually. |
| `--rebuild` | off | Replace every layer the case already has (`[ELMFIRE] RebuildExistingLayers`). |
| `--weather-only` | off | Make the case's weather (`ws`/`wd`/`m1`/`m10`/`m100`) again for the scenario's start, `--hours` and the weather options below, and nothing else: no layer is warped or re-cut, and `elmfire.data` keeps every key but its time base and weather band keys. Source-layer flags are ignored; with `--rebuild` it is refused. Only the `[Weather]` keys are printed or written. |
| `--update-wui` | off | Point the scenario at the case, as the GUI's build does: write the keys listed below into the `.wui`. Only those keys change; every other line, comment and unknown key stays, and the file keeps its line endings. |
| `--fbfm40`, `--fbfm13`, `--cc`, `--ch`, `--cbh`, `--cbd`, `--bldg_area_avg`, `--bldg_separation_distance`, `--bldg_nonburnable_frac`, `--bldg_footprint_frac`, `--bldg_fuel_model`, `--ignition_mask`, `--barriers` `<tif>` | the scenario's `[ELMFIRE]` source layers | A source raster for that stem, warped onto the grid. Overrides the scenario. |
| **Weather** | | |
| `--weather-archive <csv>` | `<case>/climatology/<Name>_era5_hourly.csv` | The ERA5 hourly archive. A file you name is copied into `<case>/climatology/` and the copy is used; yours is never rewritten. |
| `--climatology-from <year>` | `2000` | First year of the archive, 1940 to 2100. |
| `--climatology-to <year>` | an existing archive's own years; else the last complete calendar year | Last year. |
| `--weather-date <yyyy-MM-dd>` | – | Use this historical day instead of drawing one. |
| `--weather-seed <n>` | `0` | Seeds which annual peak day is drawn. |
| `--conditioning-days <n>` | `20` | Days of antecedent weather Nelson is marched over, 1 to 365. |
| `--burning-from`, `--burning-to <hour>` | `10`, `18` | Local hours (0 to 23) of the drawn day over which the logged dead-moisture minimum is taken. Only that log line depends on them. |
| `--windninja <exe>` | `[ELMFIRE] WindNinjaExe`, then your setting, `WINDNINJA_CLI`, `PATH`, `C:\WindNinja`, Program Files (Linux: `/opt/WindNinja`, `/usr/local/WindNinja`, `~/WindNinja`) | WindNinja. |
| `--wn-mesh <m>` | `fine` | `coarse`, `medium` or `fine`. |
| `--wn-vegetation <v>` | `grass` | `grass`, `brush` or `trees`. |
| `--no-climatology` | off | No archive: write uniform weather from the fallbacks below. |
| `--wind <m/s>`, `--wind-dir <deg>` | `5`, `0` | Uniform wind where a stage cannot run (0 to 100 m/s, 0 to 360°, direction the wind blows from). |
| `--m1`, `--m10`, `--m100 <%>` | `6`, `7`, `8` | Uniform dead fuel moisture where a stage cannot run. |

Retired: `--tstop` (the duration is `--hours` now, in hours) and `--force` (a case folder is always built into;
`--rebuild` replaces layers it keeps).

It refuses to build while a trigger campaign runs on the case - one of the same scenario, which holds its
campaign folder's lock (`a trigger campaign is running (...) and every one of its realizations reads this case's
rasters ...`), or of any scenario, which holds the case's `campaign.lock` (`a trigger campaign is running on this
case (it holds .../campaign.lock), started from another scenario or folder ...`) - and refuses a fire duration
outside 1 to 240 h (`[ELMFIRE] SimulationTstopHours: the fire duration is 500 h; it has to be between 1 and 240 hours (it is
hours, not seconds). Pass --hours.`).

When it finishes it prints the grid, the layers written, carried from an old grid, kept, missing or defaulted,
the fuel stem, the ignitions, every fallback, the namelist (and any hand-edited one it set aside), and the
weather day drawn. Then the keys that point the scenario at the case, which the GUI's build sets itself -
`[Landscape] ElevationFile/SlopeFile/AspectFile` (the case's `dem/slp/asp.tif`) and `[Weather] WeatherAnchorDateTime` and `WeatherFile` (when the weather was drawn from a
historical day) - only those the `.wui` does not already say. **Without `--update-wui` the `.wui` is not touched**,
and they are printed as they go into the file, one block per section:

```
The scenario does not point at this case yet. Put these keys into mati.wui, each in its
section and replacing the key of that name there (the GUI's Build fire case sets them itself), or
run build-case again with --update-wui, which writes exactly these and changes nothing else:

[Landscape]
ElevationFile=elmfire/inputs/dem.tif
SlopeFile=elmfire/inputs/slp.tif
AspectFile=elmfire/inputs/asp.tif
```

With `--update-wui` they are written and listed (`Recorded in mati.wui (--update-wui; nothing else in it
changed): ...`); a scenario that already points at the case gets `... already points at this case: nothing to
record.` One exception: when the painted areas were placed via the old landscape raster (a painting not on the
case grid, such as Mati's 616 x 590 one), `[Landscape]` is left as it is and the reason printed - the next build
needs that raster to place the painting until it is moved onto the case grid (the GUI's step 6) or repainted. A
run's trigger boundary takes the fire's own terrain either way.

| Exit code | Meaning |
|---|---|
| `0` | The case was built and its rasters agree with each other. |
| `1` | The `.wui` is not there or its `[Simulation]`/`[ELMFIRE]` sections could not be read; the hours are out of range; a campaign is running; the build failed (`build-case failed: <message>`); or the finished case is not internally consistent (the rasters stay on disk to inspect). |
| `2` | Bad arguments. |

### `landfire` — LANDFIRE fuel and canopy for a scenario (US)

```
PREACTcli landfire --wui <file.wui> [--version closest|LF2016|LF2022|LF2023|LF2024|LF2025]
                   [--email <you@example.org>] [--poll-seconds <n=20>] [--update-wui]
```

The GUI's fuels-step download (the same engine function): the release `[ELMFIRE] LandfireVersion` picks (or
`--version`, which `--update-wui` also records), over the case's padded domain, split into
`downloads/landfire/<Name>_<release>_<stem>.tif` with the canopy scaling flags their units call for, and the
case's old fuel and canopy moved to `inputs/_replaced/`. LFPS asks for a contact e-mail: `--email`, else `LANDFIRE_EMAIL`, else the
one the GUI keeps for this user in `tools.ini` (`[User] LandfireEmail`). Without `--update-wui` the source-layer keys and flags are printed; with it they
are written into the `.wui` and nothing else changes. Then run `build-case`. Exit 0 on success, 1 when the download
or the split failed (LFPS's message is printed), 2 for a bad argument.

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
| `--elmfire <exe>` | `[ELMFIRE] ElmfireExe`, else your setting, else `elmfire\` beside a standalone build, else the build in `WUInity/Assets/ThirdParty/elmfire` | ELMFIRE. |
| `--elmfire-template <namelist>` | `[ELMFIRE] NamelistTemplate`, else the case's `elmfire.data` | The namelist each realization is patched from. |
| `--elmfire-inputs <dir>` | the template's `FUELS_AND_TOPOGRAPHY_DIRECTORY`, else `<case>/inputs` | The case's inputs folder. |
| `--gdal <bin>` | `[ELMFIRE] PathToGdal`, else your setting, else found | GDAL tools folder. |
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
or has no positive cell; `wui_area.tif` is missing, empty or not the union of the scenario's evacuation groups as
they are now (build the case again); there is no fuel model table;
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
