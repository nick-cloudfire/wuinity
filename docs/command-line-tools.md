# Command-line tools

PREACT ships two console tools that host the engine without Unity. Build them
with the .NET 8 SDK — see [Building](building.md).

---

## `PREACTexecute` — run simulations

Builds to `PREACT/PREACTexecute/bin/Release/net8.0/PREACT.exe`.

### Usage

```sh
# Single serial run
PREACT.exe <file.wui>

# Batch / Monte-Carlo run
PREACT.exe <file.wui> <numberOfRuns> <batchSize> <offset>
```

| Argument | Meaning |
|----------|---------|
| `file.wui` | The [project file](input-file-format.md) to run. |
| `numberOfRuns` | Maximum number of runs. The batch stops early once results converge (less than ~2 % change in average evacuation time over the last 10 runs). |
| `batchSize` | Number of runs to execute in parallel (keep at or below the number of CPU threads). |
| `offset` | Output index offset; normally `0`. |

Examples:

```sh
PREACT.exe Roxborough_no_smoke.wui              # one run
PREACT.exe Roxborough_no_smoke.wui 20 4 0       # up to 20 runs, 4 at a time
```

### Notes

- Provide **all four** batch arguments — the batch form reads the fourth
  (`offset`) argument.
- If the `.wui` file cannot be found, the tool reports it and exits.
- Progress and messages are printed to the console. Results are written to an
  `_output/` folder next to the `.wui` file — see [Output files](output-files.md).

---

## `PREACTcli` — utility commands

Builds to `PREACT/PREACTcli/bin/Release/net8.0/PREACTcli.exe`.

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
run time (`CullOutsideGroups` / domain culling). The [QGIS plugin](qgis-plugin.md)
offers the same thing with a GUI and can also fetch OSM/WorldPop data for you.

### `probabilistic-trigger` — probabilistic k-PERIL trigger boundary

Given many fire realizations (e.g. an ELMFIRE ensemble), computes a **per-cell
probability trigger boundary**. For each realization it runs a full WUInity
evacuation (via `PREACT.exe` on a per-realization `.wui` derived from a base
case), which yields that realization's k-PERIL trigger boundary using a WRSET
(the last-arrival evacuation time) taken from that run's own evacuation. It then
aggregates all boundaries: `probability = (# realizations where the cell is
inside the trigger boundary) / (# successful realizations)`.

```sh
PREACTcli probabilistic-trigger --wui <base.wui> --dir <rasterFolder> --count <N> \
    [--start <n=1>] [--pad <width=4>] \
    [--toa TOA_{i}.tif] [--ros ROS_{i}.tif] [--sd SD_{i}.tif] [--fi FI_{i}.tif] \
    [--preact <PREACT.exe>] [--out <probability.asc>] [--resume] [--resume-only]
```

| Option | Required | Default | Meaning |
|--------|----------|---------|---------|
| `--wui` | ✔ | – | Base case; every realization inherits its domain, population, SUMO, groups, WUI mask and `[kPERIL]` settings. `[TriggerBufferModule]` must be `kPERIL`. The fire module does **not** need changing: each realization is written with `Module=AscImport` and `BuildCase=false` forced, so an ELMFIRE scenario can be used as the base directly - which is what you want, since that is the scenario the case was built and tested with. |
| `--dir` | ✔ | – | Folder holding the indexed realization rasters. |
| `--count` | ✔ | – | Number of realizations to process. |
| `--start` | | `1` | First realization index. |
| `--pad` | | `4` | Zero-padding width of the index in filenames. |
| `--toa/--ros/--sd/--fi` | | `TOA_{i}.tif` etc. | Filename patterns; `{i}` is replaced by the zero-padded index. `.asc` or `.tif`. |
| `--preact` | | auto-detected | Path to `PREACT.exe` (the runner). |
| `--out` | | `<case>/_output/trigger_probability.asc` | Output probability raster. |
| `--resume` | | off | Reuse realizations whose boundary already exists; run only the missing ones. |
| `--resume-only` | | off | Never run; aggregate only the boundaries already on disk. |

Each realization's evacuation needs SUMO installed (it is a full WUInity run).
Runs are serial and long; `--resume` makes the batch restartable.

### `converge-trigger` — the same campaign, run until it stops moving

Does what `probabilistic-trigger` does, with two differences that make it the one to use for a real campaign:
it **generates the realizations itself** rather than requiring an ensemble to exist up front, and it stops on
**convergence** rather than at a fixed count. It also aggregates the fire, not just the boundary.

```sh
# Generate realizations with ELMFIRE and run until converged
PREACTcli converge-trigger --wui <base.wui> --max 200 --resume \
    --elmfire <elmfire.exe> --elmfire-template <case>/elmfire.data \
    --elmfire-inputs <case>/inputs --gdal "<gdal bin>"

# Or aggregate an ensemble that already exists
PREACTcli converge-trigger --wui <base.wui> --dir <rasterFolder> --max 200
```

| Option | Default | Meaning |
|--------|---------|---------|
| `--wui` | – | **Required.** Base case, as for `probabilistic-trigger`. |
| `--max` | – | **Required.** Ceiling on realizations, not a target — a converged campaign stops earlier. |
| `--dir` | – | Pre-generated realization folder. Required *unless* generating with `--elmfire`. |
| `--elmfire` / `--elmfire-template` / `--elmfire-inputs` | – | Generate instead of read. All three needed together. `inputs` stays shared and read-only; each realization gets its own `outputs/` and `scratch/`. |
| `--gdal` | auto-detected | GDAL bin directory, put on ELMFIRE's `PATH`. |
| `--tstop` | `259200` (3 days) | How long each realization simulates. See below — do not shorten this casually. |
| `--seed` | `12345` | Base seed; the realization index is added to it and written to the namelist's `SEED`, which is what varies the ignition. |
| `--streak` | `20` | Consecutive realizations that must all be within tolerance before it declares convergence. |
| `--tolerance` | `0.02` | Per-decile area change allowed, as a fraction. |
| `--parallel` | CPU count | Realizations in flight. Each is its own OS process, because SUMO's libsumo has process-global state. |
| `--realization-weather` | off | Draw a fresh historical peak fire-weather day per realization. Off by default; see the note below. |
| `--weather-archive`, `--climatology-from/-to`, `--conditioning-days`, `--windninja`, `--wn-mesh` | – | The weather chain's settings, used only with `--realization-weather`. |
| `--resume` / `--resume-only` | off | Reuse what is on disk / never run, aggregate only. |
| `--out`, `--diagnostics`, `--preact`, `--start`, `--pad`, `--toa/--ros/--sd/--fi` | as `probabilistic-trigger` | |
| `--progress-json` | off | Machine-readable progress, for the Unity window. |

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

**Why `--tstop` is three days.** A realization only contributes if its fire reaches the community, ignitions
are drawn from across the whole domain, and the ones started furthest away are exactly the ones that decide how
far out the boundary must sit. A run cut short does not merely lose those realizations — it counts them as fires
that did not threaten the town, and the boundary comes out **too tight**.

**Why weather is fixed by default.** Where a fire starts is the dominant uncertainty for a trigger boundary, and
the sampled day is already an annual peak fire-weather day, so holding it fixed is the conservative choice.
`--realization-weather` also costs a WindNinja solve per weather band per realization — roughly six minutes for
a 72-band three-day run, so about twenty hours across a 200-realization campaign.
