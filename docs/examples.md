# Examples

Two scenarios ship in `Examples/`, and both load with the current engine. Their
fire is imported from rasters (`AscImport`); neither runs ELMFIRE. Start with
**Roxborough**.

## NFDRS4_Behave/Roxborough ✅ (recommended)

A Colorado (USA) scenario: `AscImport` fire + `MacroHouseholdSim` pedestrians +
`SUMO` traffic, with a `GlobalSmoke` variant. Three evacuation groups are ordered
out at the simulation start and respond by the `observed_average` curve.

```
Examples/NFDRS4_Behave/Roxborough/
├── Roxborough_no_smoke.wui        # start here
├── Roxborough_global_smoke.wui    # same scenario + global smoke
├── roxborough_weather.csv         # hourly weather for 2001
├── population/                    # worldpop_population.csv (households and road access points)
├── fire/                          # rox_big_burning_city.lcp ([Landscape]), trigger_buffer.asc
├── flammap/output/                # TOA.asc, ROS.asc, SD.asc, FI.asc (AscImport inputs, on the .lcp's grid)
├── globalSmoke/                   # extCoeffRamp.exc (GlobalSmoke extinction)
├── groups/                        # groupA/B/C .shp (one shapefile per evacuation group)
└── sumo/                          # rox.sumocfg + rox_big.net.xml
```

`fire/trigger_buffer.asc` is only read when `[Evacuation] UseTriggerBufferEvacuation=true`.

Run it:

```sh
PREACT.exe Examples/NFDRS4_Behave/Roxborough/Roxborough_no_smoke.wui
```

Results go to `_output/` next to the `.wui`.

## CFFDRS/Lytton

Lytton, British Columbia (Canada), 30 June 2021: `AscImport` fire rasters,
terrain from separate elevation, slope and aspect grids, one evacuation group of
106 households (252 people), and SUMO traffic on an OpenStreetMap network.

```
Examples/CFFDRS/Lytton/
├── Lytton.wui
├── Lytton_weather.csv
├── population/                    # worldpop_population_clipped.csv
├── wildfire/                      # TOA/ROS/SD/FI.asc (AscImport), elevation/slope/aspect.asc ([Landscape])
├── groups/                        # lytton_all.shp
└── sumo/                          # osm.sumocfg + network (the .netccfg/.polycfg record how it was built)
```

Its response curve is `Absolute`: households respond between 17:30 and 20:00 on
30 June. Absolute curve times are measured from the simulation start and then
shifted by the group's `EvacuationOrderDateTime`, which is why that is set to the
simulation start here.

## Building your own

In the [Unity visualizer](getting-started.md#5-prepare-your-own-scenario), create
the scenario with `File > New scenario`, set the domain, destinations and
evacuation groups under `Scenario > Edit`, build the population, road network and
fire case with `Scenario > Prepare data`, and `File > Save` the `.wui`. Then run
it with [`PREACT.exe`](command-line-tools.md) or open it in the
[Unity visualizer](getting-started.md#4-run-in-the-visualizer-unity).
