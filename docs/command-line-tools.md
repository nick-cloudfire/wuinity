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
| `--wui` | ✔ | – | Base case; every realization inherits its domain, population, SUMO, groups, WUI mask and `[kPERIL]` settings. Its `[WildfireModule]` must be `AscImport` and `[TriggerBufferModule]` must be `kPERIL`. |
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
