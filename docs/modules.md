# Module status

WUI-NITY is modular: each hazard and evacuation aspect is handled by a
selectable module. The codebase has been trimmed to the modules that are in
active use plus the ones on the near-term roadmap; unfinished experimental
prototypes were removed.

Legend: ✅ production · 🚧 present but under active development / not yet validated.

## Pedestrian

| Module | Status | Notes |
|--------|--------|-------|
| `MacroHouseholdSim` | ✅ | Household-based macro pedestrian model. |

Population input is a single CSV of household origins, vehicle road-access
points and household sizes (see
[the format reference](input-file-format.md#companion-file-formats)). Any tool
can produce it — use the [QGIS plugin](qgis-plugin.md).

## Traffic

| Module | Status | Notes |
|--------|--------|-------|
| `SUMO` | ✅ | Requires **SUMO 1.22** installed with extras. |

Get OSM road data for your region from <https://www.geofabrik.de/> or by
exporting a small area from <https://www.openstreetmap.org>.

## Fire

| Module | Status | Notes |
|--------|--------|-------|
| `AscImport` | ✅ | Imports pre-computed fire rasters (FARSITE, FlamMap, Prometheus, WISE — anything that outputs `.asc`). The recommended, validated fire input. |
| `ELMFIRE` | 🚧 | Runs **ELMFIRE itself** and reads the fire back. It is a batch program, so it runs once when the simulation starts and its rasters are then read through the same path `AscImport` uses; output for an unchanged case is reused. Not validated for production use yet. |

`ElmClone` — the cell-based spread model that used to sit here — has been **removed**. Only its
`LookupROS` rate-of-spread path was ever implemented; `Behave` and `CanadianFBP` built no spread model at
all and crashed on the first step. A scenario naming it says so on load and points at `ELMFIRE` or
`AscImport`. Removed with it: the orphaned `SpreadModels` wrappers (AFDRS, the CFFDRS FBP system,
LookupROS, the `SpreadModel` base) and the native BEHAVE layer, which nothing instantiated. What stayed is
what is still used — the managed `BehaveCSharp` port and Rothermel (k-PERIL's own rate of spread), and the
CFFDRS fire-weather indices (the weather manager's FFMC/DMC/DC).

Required `AscImport` rasters: `FI.asc` (fireline intensity), `ROS.asc` (rate of
spread), `SD.asc` (spread direction), `TOA.asc` (time of arrival). LCP and
GeoTIFF landscape formats are also supported.

## Smoke

| Module | Status | Notes |
|--------|--------|-------|
| `GlobalSmoke` | ✅ | User-specified global extinction coefficient that can vary over time. |

## Trigger buffers

| Module | Status | Notes |
|--------|--------|-------|
| `kPERIL` | 🚧 | [k-PERIL](https://github.com/nikosuser/k-PERIL) integration. Kept for upcoming work on probabilistic trigger-boundary calculation (planned once ELMFIRE is coupled). |

---

## Removed modules

The following experimental/unfinished modules were removed to establish a clean
base. They can be reintroduced later if development resumes:

- **Fire:** `SimpleWildfireCA`, `CellParticleHybrid`.
- **Smoke:** `AdvectDiffuseMixingLayer`, `AdvectDiffuse3D`, box, Gaussian and
  Lagrangian dispersion prototypes.
- **Traffic:** `MacroTrafficSim` (broken), `CityFlow`.
- **Pedestrian:** `JupedSim` placeholder.
- **Trigger buffers:** `BackwardsFireCell2`.
- **Detection:** the drone and satellite/VIIRS fire-detection subsystem.
- **Visualization:** the `WUIShow` TCP streaming output.

---

For a first run, use `AscImport` (fire) + `MacroHouseholdSim` (pedestrian) +
`SUMO` (traffic) + optionally `GlobalSmoke` — the combination in the
[Roxborough example](examples.md).
