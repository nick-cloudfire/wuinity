# Case 3 — the evacuation

## 3a — cars on a corridor of known lengths and speed limits, no fire

**What it runs.** A one-way corridor of three OSM ways east along y = 4316500 (`sumo/roads.osm`, converted by
netconvert with the options the GUI's Roads step uses, `SumoNetworkBuilder.Build`): 1000 m at 30 km/h, 1000 m at
50 km/h and 1500 m at 90 km/h, node to node, with a residential side street at each inner node so that netconvert
keeps the network (a road without junctions is removed by `--remove-edges.isolated`). One household beside the middle
of each edge, its home 100 m south of its car (walking at exactly 1 m/s, `WalkingSpeedMinMax=1.0,1.0`), every one
leaving at 600 s (`[ResponseCurve]` rows `600,0` and `600,1`), one car each (`AllowMoreThanOneCar=false`), all to one
exit at the corridor's end. `PREACT evac.wui` runs MacroHouseholdSim and SUMO; no fire.

The SUMO configuration adds one vehicle type, `DEFAULT_VEHTYPE` with `sigma="0" speedDev="0"`, so drivers neither
dawdle nor pick their own speed: SUMO's defaults otherwise draw each car's desired speed from N(1, 0.1) x the limit,
and no expectation could be stated per car. Everything else is SUMO's default passenger car (accel 2.6 m/s²).

| Check | Expected | Derivation | Tolerance |
|---|---|---|---|
| Response time (pedestrian output: households responded) | 600 s | the curve | 1 s |
| Households reach their car | 700 s | 600 s + 100 m / 1 m/s | 1 s (float position rounding) |
| Every car arrives | 3 | | exact |
| Arrival of the car on edge 3 | 765.8 s | 700 s + 1 s (a car enters SUMO on the step after its household reaches it) + 1500 / 25 + 25 / (2 x 2.6) | 3 s |
| ... on edge 2 | 836.6 s | + 1000 / 13.89; accelerating from 0 to 13.89 and from 13.89 to 25 m/s costs (Δv)² / (2 a v) each | 3 s |
| ... on edge 1 | 956.0 s | + 1000 / 8.33 | 3 s |

A car starts at the beginning of the edge its household's access point snaps to and arrives at the end of the
destination's edge: PREACT routes from edge to edge (`Simulation.findRoute`) and inserts the car with SUMO's default
departure position. The node-to-node lengths are the expectation; netconvert's lane lengths plus its junction
lanes add up to them within centimetres here. The 3 s tolerance covers the 1 s steps (insertion, arrival detection)
and Euler integration of the acceleration.

## 3b — households leaving ahead of their time when a fire front comes within 500 m

**What it runs.** An imported fire (`[WildfireModule] Module=AscImport`) on a synthetic 10 m grid: arrival time
t = (x − 500 m) / (1 m/s) east of x = 500 m, 0 west of it, nothing beyond x = 2500 m (the fire stops there); rate of
spread 60 m/min, direction 90°. Four households, each with its car at its home, at x = 1400, 2000, 2400 and 3800 m,
would leave at 3600 s by their response curve. `ReactToFire=true`, `FireReactionDistance=500`,
`FireReactionUpdateInterval=60`. Pedestrians only (no traffic).

| Check | Expected | Derivation | Tolerance |
|---|---|---|---|
| Household at 1.4 km leaves | 405 s | the first cell whose centre is within 500 m of the home's cell centre (1405 m) is the one centred at 905 m, burning at (905 − 500) / 1 = 405 s | 0 to +61 s: the distance to the front is recomputed every 60 s, plus a step |
| at 2.0 km | 1005 s | same, cell at 1505 m | 0 to +61 s |
| at 2.4 km | 1405 s | same, cell at 1905 m | 0 to +61 s |
| at 3.8 km | 3600 s (its own time) | the nearest burning cell (2495 m) is 1310 m away, beyond the 500 m | 1 s |
| Households the run reports as started by the fire | 3 | | exact |

Measured: 421, 1021, 1441 and 3600 s — 16, 16 and 36 s after the front came within reach, i.e. at the next refresh
of the distance field.

## 3c — one evacuation group with no area (D3)

3a with the group's `MaskFile` removed. The scenario check reports "no MaskFile or ShapeFile: every household is outside
the group and is assigned to it as the default group" as a warning, so the run should go ahead with every household in
that group: expected exit 0 and 3 cars. Measured: exit 2, `NullReferenceException` in
`EvacuationGroup.CreateShapeFilePolygon` (it opens the empty ShapeFile path with OGR and uses the null data source).
XFAIL.

## 3d — a group that names no `[Demographics]` (D4)

3b without the `[Demographics]` section and without `Demographics=` in the group. The scenario check says "all
Demographics was not found, default value the default demographics has been used": expected exit 0. Measured: exit 2,
`NullReferenceException` in the `EvacuationGroup` constructor, which falls back to
`simulation.Evacuation.DefaultDemographics` while `Simulation` is still constructing its `EvacuationManager` (so
`simulation.Evacuation` is null) — and with no `[Demographics]` at all there is no default either. Any group whose
`Demographics` names nothing that exists hits it. XFAIL.

## 3e — a run in which no car can reach SUMO (D5)

3a with every household on the corridor's second and third edges (two each) and the exit at its start: the corridor is
one-way east, so no car has a route ("Car could not be injected as no valid route was found or cached", four times).
Expected: the run fails (exit 2), as `SUMOModule` stops a run in which more than 90 % of the cars could not be put
into SUMO. Measured: exit 0 with nobody evacuated — the rule only applies once 25 cars have been tried
(`InjectionCheckMinimumCars`), so a small scenario (or a small group run on its own) passes with an empty evacuation.
The same happens on Linux with the committed SUMO bindings (every injection fails with "Unable to find an entry point
named '?'"): case 3a then reports 0 cars arrived although PREACT exits 0. XFAIL.
