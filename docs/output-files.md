# Output files

A run writes its results to an `_output/` folder next to the `.wui` file. File
names are prefixed with the simulation `Name` from the `[Simulation]` section.
Below, `<Name>` stands for that value and `<i>` for the run index (0 for a
single run; batch runs produce one set per run).

| File | Contents |
|------|----------|
| `<Name>.log` | Human-readable log of the run (messages, warnings, timings). |
| `<Name>_<i>_arrivalData.csv` | Cumulative arrivals at destinations over time for run `<i>` (the evacuation curve). |
| `<Name>_pedestrian_output_<i>.csv` | Per-household pedestrian results (departure, walking, reached-car times). |
| `<Name>_traffic_output_<i>.csv` | Per-run traffic results. |
| `<Name>_traffic_average.csv` | Traffic metrics averaged across the batch. |
| `<Name>_trafficData_<i>.tiff` | Multi-band GeoTIFF raster of traffic metrics: car count, average level of service (actual speed ÷ speed limit) and average waiting time. No-data cells use `-9999`. |
| `statistics_output.xml` | Summary statistics for the run / batch. |

## Notes

- **Batch runs.** With `PREACT.exe <file> <numberOfRuns> <batchSize> <offset>`,
  each run produces its own `_<i>_` files; the batch stops early once the
  average evacuation time converges. `_traffic_average.csv` aggregates them.
- **The GeoTIFF** can be opened in QGIS or any GIS tool. Empty (untravelled)
  cells are written as the `-9999` no-data value rather than zero.
- If an existing `_output/` folder is present, a new run overwrites files with
  matching names.

See [Command-line tools](command-line-tools.md) for how to launch runs and
[the input format](input-file-format.md) for how `Name` and the modules are set.
