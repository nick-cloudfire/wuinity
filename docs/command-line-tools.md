# Command-line tools

PREACT ships two console tools that host the engine without Unity. Build them
with the .NET 8 SDK — see [Building](building.md).

---

## `PREACTexecute` — run a scenario head-less

`build.sh` / `build.ps1` build it to `PREACT/PREACTexecute/bin/Release/net8.0/` as `PREACT` (Linux, macOS) or
`PREACT.exe` (Windows); `dotnet PREACT.dll` works everywhere.

### Usage

```sh
PREACT <file.wui>                    # one run
PREACT <file.wui> <numberOfRuns>     # up to that many runs, one after another
```

| Argument | Meaning |
|----------|---------|
| `file.wui` | The [project file](input-file-format.md) to run. It must load with nothing required missing. |
| `numberOfRuns` | Maximum number of runs, serial in this process (SUMO allows one instance per process). The runs stop early once the average evacuation time has converged (less than 2 % change for 10 runs in a row). A third and fourth argument (batch size, index offset) are still accepted; the batch size is ignored. |

Progress and messages go to the console; results to `_output/` next to the `.wui` — see
[Output files](output-files.md).

### Exit codes

| Code | Meaning |
|------|---------|
| `0` | Every run completed and nothing reported an error. |
| `1` | Nothing was run: bad arguments, or the scenario did not load or is incomplete. |
| `2` | A run stopped on an error, or an error was reported during it — k-PERIL refusing to compute a boundary among them. |

A trigger campaign counts a realization whose PREACT exits `1` or `2` as failed, and names its log.

---

## `PREACTcli` — utility commands

`build.sh` / `build.ps1` build it to `PREACT/PREACTcli/bin/Release/net8.0/` as `PREACTcli` (`PREACTcli.exe` on
Windows). Every command refuses unknown options, unreadable values and retired flags, naming the replacement,
and exits `2` for them.

### `global-gpw-to-pop` — generate a population CSV

Turns a Gridded Population of the World (GPW) dataset plus an OSM road network
into the [population CSV](input-file-format.md#companion-file-formats) the engine
expects. The simulation domain is taken from the OSM file's node bounds, the GPW
data is clipped to it, and each populated cell is split into households and
snapped to the nearest point on the road network.

```sh
PREACTcli global-gpw-to-pop --gpw <dir> --osm <file> --out <file> [--minhh <n>] [--maxhh <n>]
```

| Option | Required | Default | Meaning |
|--------|----------|---------|---------|
| `--gpw` | ✔ | – | Folder holding the 8-sector GPW ASCII dataset (`*.asc`). |
| `--osm` | ✔ | – | OSM road-network file (`.osm`/`.xml` or `.pbf`); defines the domain and the road network. |
| `--out` | ✔ | – | Output CSV path. |
| `--minhh` | | `1` | Minimum household size. |
| `--maxhh` | | `5` | Maximum household size. |

This command uses no GDAL, so it runs without a SUMO install. Coverage matches
the OSM file's extent; households outside your simulation domain are culled at
run time (`CullOutsideGroups` / domain culling). In the Unity visualizer the
workflow's Population step (Data > Population) builds the population too, and
downloads the OSM and WorldPop data it needs.

### `build-case` — build a scenario's ELMFIRE case

Builds the case exactly as the visualizer's Build fire case does: the domain, cell size, padding, fire duration,
source layers, ignition points and painted areas all come from the `.wui`. It prints the `[Landscape]` and
`[kPERIL] WuiAreaFile` values the scenario should then name (the visualizer sets them itself).

```sh
PREACTcli build-case --wui <scenario.wui> [--out <case dir>] [--hours <1-240>] [--dem <file>] [--rebuild] ...
```

`PREACTcli build-case` with no arguments lists every option: DEM source, cell size and padding, the baseline
weather chain (ERA5 climatology, WindNinja, Nelson) and its fallbacks, canopy, and source rasters that override
the scenario's.

### `converge-trigger` — probabilistic trigger boundary campaign

Runs fire realizations with ELMFIRE from ignitions drawn over the case, each under its own weather; for every
fire that reaches the WUI area, an evacuation and a k-PERIL trigger boundary (through PREACT); and aggregates the
boundaries into a per-cell probability raster until every decile of it moves by less than `--tolerance` for
`--streak` realizations in a row. The visualizer's Run > Probabilistic trigger campaign drives this same command.

```sh
PREACTcli converge-trigger --wui <base.wui> --max 200 [options]
PREACTcli converge-trigger --wui <base.wui> [options] --inspect     # would these settings reuse a campaign?
```

| Option | Default | Meaning |
|--------|---------|---------|
| `--wui` | – | **Required.** Base scenario. Its `[TriggerBufferModule]` must be `kPERIL`; each realization runs it with `Module=AscImport` on the realization's own fire. |
| `--max` | – | **Required** (except with `--inspect`). A ceiling, not a target. |
| `--hours` | `72` | Hours of fire per realization, 1 to 240. |
| `--seed`, `--start` | `12345`, `1` | Base seed and first realization index. |
| `--streak`, `--tolerance` | `20`, `0.02` | The convergence rule. |
| `--parallel` | half the cores | Realizations in flight, each its own processes (two SUMO evacuations at once need several GB). |
| `--resume` | off | Reuse the realizations of a campaign with exactly these settings; refused when only other settings exist. |
| `--resume-only` | off | Aggregate that campaign's finished realizations without running any. |
| `--inspect` | off | Say whether a campaign with these settings exists, what it would reuse and how others differ; exit. |
| `--preact` | found | PREACT(.exe): looked for in `PREACT/PREACTexecute/bin/Release/net8.0`, beside PREACTcli, then the Debug build. |
| `--elmfire`, `--elmfire-template`, `--elmfire-inputs`, `--gdal` | the scenario's | Override the executable, namelist, inputs folder and GDAL tools the scenario's `[ELMFIRE]` section resolves to. |
| `--max-runtime-minutes` | 2 min per hour of fire, at least 60 | Wall-clock limit per ELMFIRE run; a run that hits it counts as failed. |
| `--historical-day-weather` | off | Replay whole historical peak days (WindNinja per hour, Nelson) instead of drawing each parameter from normals fitted to them. |
| `--candidate-days-per-year`, `--no-live-fuel-moisture`, `--no-wind-to-wui` | `10`, on, on | The fitted weather pool, live fuel moisture from the NFDRS4 GSI march, and aiming the wind from the ignition at the WUI area. |
| `--weather-archive`, `--climatology-from/-to`, `--conditioning-days`, `--windninja`, `--wn-mesh` | – | The weather chain's settings. |
| `--allow-uniform-weather` | off | Go ahead without WindNinja/Nelson (uniform wind and moisture) instead of stopping before the first fire. |
| `--out` | – | Also copy the probability raster here. |
| `--cancel-on-stdin-close` | off | Stop, killing every child process, when stdin closes (the visualizer's Cancel). |

Retired, and refused with what to do instead: `--tstop` (now `--hours`), `--dir`, `--toa/--ros/--sd/--fi`,
`--pad`, `--shared-weather`, `--single-band-weather`, `--realization-weather`, `--fitted-weather`,
`--wind-to-wui`, `--live-fuel-moisture`, `--progress-json`, `--max-weather-bands`, `--diagnostics`. The
`probabilistic-trigger` command is gone too.

**Where it writes.** Everything goes into `<scenario>/_output/campaign_<name>_<settings hash>/`: `campaign.json`
(the settings), `trigger_probability.asc` (and `_live` while running), `trigger_convergence.csv`,
`realizations.csv`, the weather reports, the `ensemble_*` fire statistics below, and `realizations/<id>/` with
each realization's fire outputs, weather, PREACT outputs and logs. Other settings make another folder; starting
over moves a same-settings folder aside as `..._replaced_<time>`.

**Statuses.** A realization is `ok` (a boundary), `not-threatened` (its fire never reached the WUI area: no
evacuation is run, and it is left out of the probability's denominator but kept in the fire statistics), or
`failed` (ELMFIRE or PREACT failed; the campaign goes on and `--resume` retries it — a finished fire is reused and
only its evacuation rerun).

**Exit codes.** `0` done (converged, or reached `--max`), `1` failed, `2` bad arguments, `3` cancelled
(Ctrl+C, SIGTERM, or stdin closing with `--cancel-on-stdin-close`; every ELMFIRE, WindNinja and PREACT it started
is killed, and finished realizations are kept).

**Outputs.** Beside `trigger_probability.asc` and the convergence CSV, it writes per-cell fire statistics
aggregated from the realizations' own arrival rasters:

| Raster | Meaning |
|---|---|
| `ensemble_burn_probability.asc` | Fraction of realizations in which the cell burned. |
| `ensemble_arrival_earliest.asc` | Earliest arrival seen anywhere in the ensemble, seconds. |
| `ensemble_arrival_mean.asc` | Mean arrival, **conditional on burning**, seconds. |
| `ensemble_arrival_p10/p50/p90.asc` | Percentiles, also conditional on burning, to the nearest hour. |

The arrival statistics are conditional on the cell burning, so read them together with the burn probability —
a cell with a 5 % burn probability and an early p10 is threatened rarely but fast. The **low** percentiles are
the conservative ones: p10 is when the fire arrives in the fastest tenth of the cases it arrives at all.

**Why the fire runs three days.** A realization only contributes if its fire reaches the community, ignitions
are drawn from across the whole domain, and the ones started furthest away are exactly the ones that decide how
far out the boundary must sit. A run cut short does not merely lose those realizations — it counts them as fires
that did not threaten the town, and the boundary comes out **too tight**.
