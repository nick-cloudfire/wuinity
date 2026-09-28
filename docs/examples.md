# Examples

Two scenarios ship in `Examples/`. Both import their fire from rasters (`AscImport`), so they need neither
ELMFIRE nor an OpenTopography key — only SUMO for the traffic — and both load with no warning. Neither turns
the trigger boundary on. They are the quickest check that a build works; for an ELMFIRE scenario, build your own
([below](#building-your-own)).

## `NFDRS4_Behave/Roxborough` (start here)

Roxborough Park, Colorado, 5–6 June 2001: FlamMap fire rasters on the grid of a FARSITE landscape, three evacuation
groups ordered out at 11:00 and responding by the `observed_average` curve, SUMO traffic to three exits, and a
variant with global smoke.

```
Examples/NFDRS4_Behave/Roxborough/
├── Roxborough_no_smoke.wui        # start here
├── Roxborough_global_smoke.wui    # the same, plus GlobalSmoke (a constant 0.2 /m) slowing the cars
├── roxborough_weather.csv         # hourly weather for the run, reported only
├── population/                    # worldpop_population.csv: households and their road access points
├── fire/                          # rox_big_burning_city.lcp: the [Landscape] (414 x 405 cells of 30 m)
├── flammap/output/                # TOA/ROS/SD/FI.asc (+ .prj, EPSG:32613): the AscImport fire, arrival times in minutes
├── globalSmoke/                   # extCoeffRamp.exc
├── groups/                        # groupA/B/C.shp: one polygon per evacuation group
└── sumo/                          # rox.sumocfg + rox_big.net.xml
```

```powershell
PREACT\PREACTexecute\bin\Release\net8.0\PREACT.exe Examples\NFDRS4_Behave\Roxborough\Roxborough_no_smoke.wui
```

On the Linux bench (with SUMO) both variants ran in seconds; all 649 cars arrived by about 6600 s without smoke
and about 7400 s with it. Results go to `_output/` beside the `.wui`.

## `CFFDRS/Lytton`

Lytton, British Columbia, from 29 June 2021: imported fire rasters, terrain from separate elevation, slope and
aspect grids (100 m), one evacuation group of 106 households (252 people), and SUMO traffic on an OpenStreetMap
network.

```
Examples/CFFDRS/Lytton/
├── Lytton.wui
├── Lytton_weather.csv
├── population/                    # worldpop_population_clipped.csv
├── wildfire/                      # TOA/ROS/SD/FI.asc (+ .prj, EPSG:32610), elevation/slope/aspect.asc
├── groups/                        # lytton_all.shp
└── sumo/                          # osm.sumocfg + network (osm.netccfg/.polycfg record how it was built)
```

Its response curve is `Absolute`: households respond between 17:30 and 20:00 on 30 June, on the scenario's
calendar, whatever the group's order time. The run covers 5.5 days (it does not stop when everyone has left); all
106 households arrive.

## What to expect from v1

Both scenarios use the household model's defaults, so **fire reaction is on**: a household leaves as soon as the
fire front is within 500 m of its home, if that is before its drawn response. Earlier versions never did this; set
`[MacroHouseholdSim] ReactToFire=false` to compare with old results.

## Building your own

In the GUI: File > New scenario, then down the workflow panel — see [Getting started](getting-started.md). The
panel builds the roads, population, fuels and the ELMFIRE case, and the scenario is saved as you go
(File > Save). Then run it from the GUI, or with [`PREACT.exe`](command-line-tools.md#preactexe--run-a-scenario),
and run a [trigger campaign](trigger-campaigns.md) on it.
