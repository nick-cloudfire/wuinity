# Examples

Ready-to-run scenarios live in `Examples/`. Start with **Roxborough** — it is
the most complete and matches the current engine.

## NFDRS4_Behave/Roxborough ✅ (recommended)

A complete Colorado (USA) scenario using the production modules:
`AscImport` fire + `MacroHouseholdSim` pedestrian + `SUMO` traffic, with an
optional `GlobalSmoke` variant.

```
Examples/NFDRS4_Behave/Roxborough/
├── Roxborough_no_smoke.wui        # start here
├── Roxborough_global_smoke.wui    # same scenario + global smoke
├── roxborough_weather.csv
├── population/                    # worldpop_population.csv (+ small/big variants)
├── flammap/output/               # TOA.asc, ROS.asc, SD.asc, FI.asc  (AscImport inputs)
├── groups/                       # groupA/B/C .shp  (one shapefile per evacuation group)
├── sumo/                         # rox.sumocfg + road network
├── fire/, fireCell/              # landscape, ignition, trigger-buffer data
└── _output/                      # results from a previous run
```

Run it:

```sh
PREACT.exe Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui
```

## Other examples

| Folder | Notes |
|--------|-------|
| `CFFDRS/Lytton/` | Rich Canadian scenario (SUMO, wildfire rasters, groups, a QGIS project). Sprawling but useful as a reference. |
| `Development/` | A developer "kitchen sink". Its `.wui` selects the `SimpleWildfireCA` fire module, which has been **removed**, so it will not load as-is — kept only for its data files. |
| `CFFDRS/Dogrib/` | Historic scenario referencing a removed fire module; will not load as-is. |
| `ASDRF/` | A lookup-table fragment only (`BFC_lookup_table.csv`); no `.wui`, not runnable on its own. |

> Several older examples select modules that have since been [removed](modules.md)
> (e.g. `SimpleWildfireCA`, `MacroTrafficSim`). They will fail to load against the
> current engine. Prefer **Roxborough**, and cross-check any hand-edited file
> against [the input format reference](input-file-format.md).

## Building your own

Use the [QGIS plugin](qgis-plugin.md) to draw the domain, place destinations and
evacuation groups, generate a population CSV and road network, and export a
`.wui`. Then run it with [`PREACT.exe`](command-line-tools.md) or open it in the
[Unity visualizer](getting-started.md#4-run-in-the-visualizer-unity).
