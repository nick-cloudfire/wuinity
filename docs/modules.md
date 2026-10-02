# Modules

A scenario switches each part of the simulation on and picks its module: `[PedestrianModule]`,
`[TrafficModule]`, `[WildfireModule]`, `[SmokeModule]` and `[TriggerBufferModule]`, each with
`Enabled` and `Module`. The keys are in [the input format](input-file-format.md). Each time step the modules
are stepped one after another, in this order: fire, smoke, pedestrians, traffic; the trigger boundary is computed
once the run has finished.

| Part | Module | Status in v1 |
|---|---|---|
| Fire | `ELMFIRE` | Default for new scenarios. Verified end to end head-less (case build, fire, evacuation, boundary, campaign) on the Linux bench; the Windows GUI path is covered by the [manual test](manual-test-v1.md). Rates of spread, fire shape, evacuation times, the k-PERIL boundary and campaign convergence are checked against independent expectations by the [verification cases](verification.md). |
| Fire | `AscImport` | Stable. Both shipped examples use it. |
| Pedestrians | `MacroHouseholdSim` | Stable. Fire reaction fixed and on by default in v1. |
| Traffic | `SUMO` (1.22) | Stable on Windows. On Linux it needs regenerated bindings ([Building](building.md#regenerating-the-sumo-c-glue-on-linux)). |
| Smoke | `GlobalSmoke` | Stable. Extinction coefficient units fixed in v1. |
| Trigger boundary | `kPERIL` | Integrated; orientation fixed in v1. See [the notes below](#trigger-boundary) before relying on its shape. |

## Fire

### `ELMFIRE`

Runs [ELMFIRE](https://github.com/lautenberger/elmfire) (the `ELMFIRE-WUINITY` fork, submodule at a7fb9d6) on the
scenario's case. ELMFIRE computes a whole fire and writes rasters, so it is run once when the simulation starts,
before the evacuation, and its rasters are then read exactly as `AscImport` reads an imported fire. An identical
earlier fire is reused. How a case is built and run: [ELMFIRE cases](elmfire-cases.md).

What ELMFIRE hands the rest of the platform: arrival time, rate of spread, spread direction, fireline intensity
and the midflame wind, on the case grid; the evacuation reads arrival times, k-PERIL all of them.

### `AscImport`

Reads a fire computed elsewhere — FARSITE, FlamMap, Prometheus, an ELMFIRE run by hand — as arrival time, rate of
spread, spread direction and (optionally) fireline intensity and midflame wind rasters on one grid. The arrival
time's unit must be stated (`TimeOfArrivalUnits`): seconds for ELMFIRE, minutes for FARSITE/FlamMap/Prometheus.
The fire starts at `[AscImport] StartDateTime`, which may differ from the simulation start. Cells with no data or
negative values are unburned.

## Pedestrians: `MacroHouseholdSim`

Households, from the [population CSV](input-file-format.md#population-csv), belong to an evacuation group by
the position of their home. Each draws a response time from one of its group's response curves (relative to the
group's evacuation order, or absolute), walks to its car's road-access point at a speed drawn from
`WalkingSpeedMinMax`, and is handed to the traffic module with its demographics' number of cars.

**Fire reaction** (`ReactToFire`, on by default): every `FireReactionUpdateInterval` (300 s) the distance from each
home to the current fire front — the cells whose arrival time has passed — is recomputed, and a household that
has not left yet and whose drawn response lies in the future leaves at once when the front is within
`FireReactionDistance` (500 m). Households whose drawn response is "never" (a curve ending below 1) stay. The log
counts the households and people that left this way. Before v1 this reaction never started anyone.

## Traffic: `SUMO`

Cars are driven by SUMO 1.22 through libsumo, stepped every simulation step at the simulation's step length. The
network (`[SUMO] ConfigurationFile`) is built from OpenStreetMap by the GUI's Roads step, in the simulation's UTM
zone; a network in another zone is reported at load. A car whose destination is reached counts as arrived. A
destination's `MaxFlow` admits cars at `MaxFlow`/3600 per second with up to a minute's worth in reserve; a car
arriving over it is sent back into traffic to the same destination until there is room, and a car arriving at a
blocked destination is sent to the best available one. A run in which most cars cannot be put into SUMO at all stops with an error instead of
evacuating nobody. The run's required safe egress time (WRSET) is the time the last car arrives.

## Smoke: `GlobalSmoke`

One extinction coefficient (1/m) over the whole domain, varying in time from a ramp file. With `[SUMO] SmokeAlpha`
set, cars slow to `1 − α·exp(β/K)` of their speed (clamped to 0.05–1; no slowing in clear air).

## Trigger boundary

### `kPERIL`

[k-PERIL](https://github.com/nikosuser/k-PERIL) computes, after the run, the ground from which the fire reaches
the WUI area in less than the required safe egress time — the **trigger boundary**. For each cell it breaks the
fire's rate of spread down into the eight neighbour directions using an elliptical spread shape whose
length-to-breadth ratio comes from the effective midflame wind (Anderson 1983: the midflame wind vector-added to
0.06 × the slope in degrees, pointing upslope), turns those rates into travel times, and marks every cell from
which a fire reaches a WUI cell within the WRSET. Inputs:

| Input | Source |
|---|---|
| Rate of spread and direction | The fire module (`vs`, `spread_dir`). |
| Wind speed | The fire's own midflame wind (ELMFIRE's `mfws`, ft/min ÷ 88 = mi/h). Without one, `[kPERIL] WindSpeedFile` used as midflame, with a warning. |
| Wind direction | The fire's own weather (`wd.tif`), each cell taking the band covering the time the fire reached it. |
| Slope and aspect | The terrain the fire burned on: for an ELMFIRE run, the case's `dem/slp/asp.tif` its namelist names (the fire grid, so 100 % covered), whatever `[Landscape]` says; otherwise `[Landscape]` (a campaign realization's is the case's). Missing cells count as flat. |
| WUI area | The evacuation groups' areas (`WuiAreaSource`): their union, or one per group; or a mask of your own, `[kPERIL] WuiAreaFile` with `WuiAreaSource=Raster`. |
| Required egress time | The run's WRSET, in minutes. |

No boundary is computed for a WUI area the fire never reached, or when no car arrived.

Notes before relying on the boundary's shape:

- **Orientation.** Before v1 the rasters were handed to kPERILcore in the engine's own array layout, which
  kPERILcore reads rotated by 90°: every boundary lay on the wrong side of the WUI area. Fixed in v1 — a fire
  spreading east now gives a boundary extending west. See the [changelog](../CHANGELOG.md).
- **Wind barely changes the boundary.** kPERILcore evaluates the ellipse at its parametric angle, so once the
  length-to-breadth ratio is above about 3 the flank rate is about half the head rate whatever the wind: 0.51 at
  5.9 mi/h, 0.50 at 16.3 mi/h. Only below about 3 mi/h of effective midflame wind does the wind matter (0.55 at
  2.75 mi/h). Moving from 10 m to midflame wind changed Mati boundaries by 1–2 %. Nothing caps the ratio. Whether
  the breakdown should follow the true polar ellipse is for k-PERIL's author to decide.
- **The slope vector.** The wind is added at its "from" bearing and the slope at its upslope bearing, so an
  upslope wind is subtracted from the slope term rather than added. It affects only the ratio's magnitude, so its
  effect on the boundary is small for the reason above.
- **A boundary is conditional on its fire.** One run's boundary is one fire's; a [trigger
  campaign](trigger-campaigns.md) gives the probability over many.

## Other engine parts

- **Weather** (`[Weather]`): reports temperature, humidity, wind and the Canadian fire-weather indices during a run,
  from an hourly CSV, at the historical day the fire was computed against when the case set an anchor. It does not
  drive the fire, the evacuation or the boundary.
- **FOFEM**: the wrapper and native library are kept, and nothing calls them yet. The committed `FOFEM.dll` is a
  Debug build that needs Visual Studio's debug runtime; rebuild it in Release before using it.
- **Downloaders**, used by the GUI's data steps: OpenStreetMap (Overpass), WorldPop, Open-Meteo (ERA5 and weather),
  OpenTopography (DEMs), LANDFIRE (LFPS, US fuels and canopy).

## Removed

Removed in v1: the `[Events]` section (never implemented), the parallel execution modes (runs in one process are
serial; parallelism is separate processes), the `probabilistic-trigger` CLI command and reading pre-generated
ensembles, the QGIS plugin, and the examples and verification data for fire models that no longer exist
(`Examples/Development`, `Examples/CFFDRS/Dogrib`, `Examples/ASDRF`, and the old `Verification/` of the cell models;
`Verification/` now holds the [verification cases](verification.md) of the ELMFIRE product).

Removed before v1, and reported by name if a scenario still asks for them: the cell-based fire model
(`ElmClone`/`CellSpread`, with its BEHAVE, CFFDRS FBP and AFDRS spread models), `SimpleWildfireCA`,
`CellParticleHybrid`, the advect–diffuse and other smoke prototypes, `MacroTrafficSim`, `CityFlow`, the `JupedSim`
placeholder, the `BackwardsFireCell2` trigger buffer, the drone/satellite detection subsystem and `WUIShow`
streaming.
