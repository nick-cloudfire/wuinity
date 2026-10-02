# Verification

`Verification/` holds basic verification cases for the head-less product: small synthetic inputs, an expected value
for every check derived independently of the code under test — from the published equations (Rothermel 1972,
Andrews 2018 for BehavePlus, Andrews 2012 for the wind adjustment factor, Anderson 1983 for the fire's shape), from
geometry, or from road length over speed limit — and a one-command runner that runs every case through the product
and prints measured against expected with a PASS or FAIL per check. Each case's README states what it checks, how
each expected value was derived and why its tolerance is what it is.

The cases test the product as a user runs it: `PREACTcli build-case` builds the ELMFIRE case from the synthetic
rasters, `PREACT` runs ELMFIRE through the engine, the evacuation through SUMO and the k-PERIL boundary, and
`PREACTcli converge-trigger` runs the campaign. Nothing in `Verification/` is part of the product or the build.

## Running it

Build first (`build.ps1` / `build.sh`); the runner builds nothing.

```powershell
.\verify.ps1                       # Windows
```
```sh
./verify.sh                        # Linux
```

| Option | Default | Meaning |
|---|---|---|
| `--case 1,2,3,4` | all | Which cases. `--list` lists them. |
| `--work <dir>` | `Verification/_runs/<yyyymmdd-HHMMSS>/` | Where the inputs are generated and every case runs (ignored by git). It must be new or empty, and should have no space in its path: ELMFIRE hands paths to GDAL unquoted, and the campaign refuses them. |
| `--preact <PREACT.dll>`, `--cli <PREACTcli.dll>` | `PREACT/PREACTexecute/bin/Release/net8.0/PREACT.dll`, `PREACT/PREACTcli/bin/Release/net8.0/PREACTcli.dll` (Debug if there is no Release) | The builds under test. |
| `--elmfire <exe>` | `$ELMFIRE_EXE`, else `WUInity/Assets/ThirdParty/elmfire/build/windows/bin/elmfire.exe` (Linux: `build/linux/bin/elmfire`) | ELMFIRE; its `build/source/fuel_models.csv` is where the expected values take the fuel models from. |
| `--gdal <bin>` | `gdal_translate` on `PATH`, else QGIS or OSGeo4W | GDAL's command-line tools: passed to `build-case`, and used to read ELMFIRE's GeoTIFFs. |
| `--sumo-home <dir>` | `$SUMO_HOME` | SUMO: `netconvert` builds the cases' road networks, PREACT drives `libsumocs`. |
| `--strict` | off | Count known discrepancies (XFAIL) as failures. |
| `--generate-only` | off | Write the inputs and stop, to look at them (QGIS reads every raster). |

It needs Python 3.8 or newer with nothing but its standard library — on Windows `verify.ps1` takes the `py` launcher,
`python`, or QGIS's own Python — and the .NET 8 runtime. The inputs are generated on every run (about 7 MB of
uncompressed GeoTIFFs, OSM files and scenarios); nothing generated is committed. The whole suite takes about 1.5 min
on the 2-core Linux bench.

It prints the table, then a note for every check that did not simply pass, and writes `results.txt` and
`results.csv` into the work folder. Each case's folder keeps its inputs, its case, every log (`build-case.log`,
`preact.log`, `campaign1.log`, ...) and the product's outputs.

| Result | Meaning |
|---|---|
| `PASS` / `FAIL` | Measured within / outside the tolerance of the expected value. |
| `XFAIL` | A known discrepancy, listed [below](#discrepancies) with its id: it fails, as expected. |
| `XPASS` | A known discrepancy that no longer fails — the cue to drop its marker in the case and this page. |
| `ERROR` | The check could not be made (a run produced no output); the note says where to look. |
| `INFO` | A measurement reported for the record. |

Exit code: 0 when every check passed or failed as known (XFAIL), 1 when any failed or errored, 2 when a tool is
missing.

### Linux

PREACT needs GDAL 3.10's `libgdal.so.36` on `LD_LIBRARY_PATH` and `SUMO_HOME`, as for any run
([Building: Linux](building.md#linux)). The committed SUMO C# bindings
(`PREACT/PREACTcore/Runtimes/Managed/Eclipse.Sumo.Libsumo/*.cs`) are the files SWIG generated for the Windows SUMO
1.22; against a libsumocs built on Linux every car fails to enter SUMO ("Unable to find an entry point named '?'"),
and the traffic checks of cases 1, 2c, 3 and 4 fail. `Verification/tools/linux-sumo-glue.sh <scratch dir>` copies `PREACT/` outside the
repository, swaps in the bindings SWIG generated for the local SUMO (default
`$SUMO_HOME/build/cmake-build/src/libsumo/cs`) and builds PREACT there; pass the printed path with `--preact`. The
runner says so when it is given the repository's own PREACT on Linux. The bench run below used:

```sh
export LD_LIBRARY_PATH=/opt/gdal310/lib SUMO_HOME=/home/claude/sumo-src
Verification/tools/linux-sumo-glue.sh /home/claude/runs/bl4/preact-linux
./verify.sh --preact /home/claude/runs/bl4/preact-linux/PREACT/PREACTexecute/bin/Release/net8.0/PREACT \
            --elmfire /home/claude/work/wuinity/WUInity/Assets/ThirdParty/elmfire/build/linux/bin/elmfire
```

## The cases

| Case | What it runs | What it checks |
|---|---|---|
| [1 flat, wind](../Verification/cases/case1_flat_wind/README.md) | Anderson model 1 on level ground, 6 m/s from the west, point ignition, 2 h; a WUI box 1.2 km downwind with one household; a 1 x 1 km twin with the fuel typed Int16 and Int32; GR2 | Head and backing rate, midflame wind, spread direction, L/B, elliptical shape, symmetry; the car's arrival; the boundary's upwind extent, side and symmetry, and the engine's boundary against a re-computation of k-PERIL's algorithm; the fuel raster's type (D1); a dynamic model; k-PERIL's L/B against the fire's (D2) |
| [2 slope](../Verification/cases/case2_slope/README.md) | The same fuel on a 20° plane and on level ground, calm; then the plane with 6 m/s blowing up it and a WUI box upslope | R0; the slope and aspect rasters; the upslope rate and its ratio 1 + φ_s; the map projection; the backing rate; wind and slope adding; k-PERIL's effective wind (D6) and its rate on a slope (D7) |
| [3 evacuation](../Verification/cases/case3_evacuation/README.md) | A one-way corridor 1 / 1 / 1.5 km at 30 / 50 / 90 km/h, three households, no fire; four households and an imported front moving at 1 m/s; three scenarios the checklist accepts | Response, walk and arrival times; departures triggered by the front at 500 m; a group with no area (D3), no demographics (D4), no routable car (D5) |
| [4 campaign](../Verification/cases/case4_campaign/README.md) | `converge-trigger` on a case with one ignitable cell and constant ERA5-style weather, `--streak 3`, then again from scratch | Convergence after streak + 1 boundaries; the convergence table and probability raster against a re-aggregation of the kept boundaries; the per-realization seeds; the drawn weather; bit-identical realizations on the rerun |

## Results

On the Linux bench (2 cores), v1 @ b9b18709 plus this verification, ELMFIRE a7fb9d6, GDAL 3.8 tools / 3.10 library,
SUMO 1.22 with the scratch bindings; 95 s, exit 0:

```
Case  Check                                                               Measured       Expected      Tolerance          Result
----  ------------------------------------------------------------------  -------------  ------------  -----------------  ----------
1     head ROS (vs, 0.5-1.5 km downwind) vs Rothermel/BehavePlus          22.738 m/min   22.726 m/min  1 %                PASS
1     midflame wind (mfws) vs 20-ft wind x unsheltered WAF                372.7 ft/min   372.7 ft/min  0.5 %              PASS
1     head ROS from arrival times vs Rothermel                            22.966 m/min   22.726 m/min  2 %                PASS
1     backing ROS (vs, upwind) vs head / HB(Anderson L/B)                 3.515 m/min    3.513 m/min   1 %                PASS
1     head spread direction vs downwind (wind from 270)                   89.998 deg     90.000 deg    0.5 deg            PASS
1     burned area at 6000 s symmetric about the wind axis (Jaccard)       0.9995         1.000         >= 0.995           PASS
1     L/B of the burned area at 6000 s vs Anderson L/B                    1.475          1.468         3 %                PASS
1     burned area at 6000 s vs the ellipse of its own extents (Jaccard)   0.9937         1.000         >= 0.98            PASS
1     burned area at 6000 s vs the predicted ellipse (Jaccard)            0.9705         1.000         >= 0.95            PASS
1     the car's arrival vs response + road length / limit                 1269 s         1268 s        3 s                PASS
1     k-PERIL boundary upwind extent vs RSET x head ROS                   480.0 m        480.0 m       1 cell             PASS
1     k-PERIL boundary upwind: bearing WUI -> boundary centroid           270.0 deg      270.0 deg     5 deg              PASS
1     k-PERIL boundary symmetric about the wind axis (Jaccard)            0.9991         1.000         >= 0.99            PASS
1     k-PERIL boundary vs its algorithm re-computed (Jaccard)             1.000          1.000         >= 0.99 (Jaccard)  PASS
1     k-PERIL boundary vs the same with the fire's own L/B (Jaccard)      0.92           1.000         >= 0.99            XFAIL (D2)
1     1b fire on an Int16 fuel raster burns                               224.1 ac       -             > 0 ac             PASS
1     1b the same fire on an Int32 fuel raster (LFPS's type): same area   0 ac           224.1 ac      0.1 %              XFAIL (D1)
1     1c GR2 head ROS (dynamic, 60 % live herb) vs BehavePlus             9.263 m/min    9.256 m/min   1 %                PASS
2     level, calm: ROS (vs) vs Rothermel R0                               1.404 m/min    1.404 m/min   1 %                PASS
2     level, calm: burned area at 3000 s round (N-S / E-W)                1.000          1.000         1 cell in 15       PASS
2     slope raster of the planar DEM (Horn)                               20.000 deg     20.000 deg    0.05 deg           PASS
2     aspect raster (downhill bearing), slope rising north                180.0 deg      180.0 deg     0.5 deg            PASS
2     upslope ROS (vs, along the slope) vs R0 (1 + phi_s)                 9.060 m/min    9.055 m/min   1 %                PASS
2     upslope / level-ground ROS ratio vs 1 + phi_s                       6.451          6.451         1 %                PASS
2     upslope ROS from arrival times (map) vs R0 (1 + phi_s) cos 20       8.601 m/min    8.509 m/min   2 %                PASS
2     downslope ROS vs upslope / HB(L/B of the slope's effective wind)    2.282 m/min    2.281 m/min   2 %                PASS
2     upslope spread direction vs uphill (north)                          -0.000505 deg  0 deg         0.5 deg            PASS
2     burned area at 3000 s symmetric across the slope (Jaccard)          0.998          1.000         >= 0.995           PASS
2     2c upslope wind + slope: head ROS vs R0 (1 + phi_w + phi_s)         30.393 m/min   30.377 m/min  1 %                PASS
2     2c k-PERIL boundary vs its algorithm re-computed (Jaccard)          1.000          1.000         >= 0.99            PASS
2     2c boundary reach below the box vs RSET x the fire's map speed      640.0 m        600.0 m       1 cell             XFAIL (D7)
2     2c boundary vs the same with wind and slope terms adding (Jaccard)  0.9534         1.000         >= 0.99            XFAIL (D6)
3     3a PREACT run exits 0                                               0              0             exact              PASS
3     3a households respond at the response curve's time                  600.0 s        600.0 s       1 s                PASS
3     3a households reach their car (100 m walk at 1 m/s)                 701.0 s        700.0 s       1 s                PASS
3     3a every car arrives                                                3 cars         3 cars        exact              PASS
3     3a arrival, car starting on edge 3 (1.5 km at 90 km/h)              767.0 s        765.8 s       3 s                PASS
3     3a arrival, car starting on edges 2-3 (+1 km at 50 km/h)            837.0 s        836.6 s       3 s                PASS
3     3a arrival, car starting on edges 1-3 (+1 km at 30 km/h)            956.0 s        956.0 s       3 s                PASS
3     3b PREACT run exits 0                                               0              0             exact              PASS
3     3b household at 1.4 km leaves as the front comes 500 m near         421.0 s        405.0 s       0 to +61 s         PASS
3     3b household at 2.0 km leaves as the front comes 500 m near         1021 s         1005 s        0 to +61 s         PASS
3     3b household at 2.4 km leaves as the front comes 500 m near         1441 s         1405 s        0 to +61 s         PASS
3     3b household at 3.8 km, out of reach, leaves on its own time        3600 s         3600 s        1 s                PASS
3     3b households reported as started by the fire                       3              3             exact              PASS
3     3c a group without MaskFile/ShapeFile runs (checked as a warning)   0 cars         3 cars        3 cars, exit 0     XFAIL (D3)
3     3d a group naming no [Demographics] runs (default promised)         2              0             exit 0             XFAIL (D4)
3     3e a run in which no car can reach SUMO fails (exit 2)              0              2             exit 2             XFAIL (D5)
4     the campaign converges, after streak + 1 boundaries                 4 boundaries   4 boundaries  exact              PASS
4     premise: largest decile-area change between boundaries              0.0004719      0             < 0.02             PASS
4     convergence CSV decile areas vs re-aggregated boundaries            0 m2           0 m2          exact (m2)         PASS
4     convergence CSV streak sequence vs the rule                         0 1 2 3        0 1 2 3       exact              PASS
4     trigger_probability.asc vs fraction of boundaries per cell          0              0             1e-6               PASS
4     ELMFIRE SEED of realization i = seed + i (run.data)                 yes            yes           every realization  PASS
4     evacuation seed of realization i = seed + 2 000 000 + i             yes            yes           every realization  PASS
4     every ignition is the one cell of the ignition mask                 yes            yes           0.5 m              PASS
4     drawn weather: archive wind, aimed at WUI, Simard EMC 1-h           yes            yes           every realization  PASS
4     realization 1 head ROS vs Rothermel at its drawn weather            25.530 m/min   25.516 m/min  1 %                PASS
4     rerun from scratch: the same realization records                    yes            yes           every field        PASS
4     rerun from scratch: identical arrival rasters and boundaries        yes            yes           md5                PASS
4     distinct arrival rasters (ELMFIRE SEED, spotting off)               4              -             -                  INFO
```

What this says, in short: ELMFIRE reproduces Rothermel/BehavePlus to 0.1 % for the head, backing, slope and
wind-plus-slope rates, for a static and a dynamic fuel model; its level set grows the ellipse those rates and
Anderson's L/B describe (to about 1 % of the rates and 3 % of the area); slope and aspect are derived correctly;
SUMO's travel times match length over limit within 1.2 s; the households' reaction to the front is right to the
refresh interval; k-PERIL's boundary is exactly its algorithm applied to the fire it is handed (orientation, units
and layout are right), on the upwind side and symmetric; the campaign's convergence bookkeeping is exact and its
realizations reproducible bit for bit. The seven known discrepancies are below.

## Discrepancies

Found by these cases, with the evidence the checks print; none is fixed here (BL4 does not edit engine code).

**D1 — a fuel raster typed Int32 burns nothing (MEDIUM).** ELMFIRE reads the fuel model raster as 16-bit integers
(`FBFM%I2` throughout `elmfire_*.f90`); an Int32 raster is decoded by `READ_BSQ_RASTER_SLICE` into its float buffer
instead, the integer array stays empty, every cell is non-burnable, and ELMFIRE exits 0 with 0 acres — which PREACT
reports as "elmfire burned 0 acres (the ignition most likely landed on non-burnable fuel)". The case builder warps
source layers with `RasterHarmonizer.WarpToGrid`, which keeps the source's type, and `ElmfireCaseValidator` does not
check it. Case 1b: the same fire, 224.1 acres with Int16 fuel and 0 with Int32. LANDFIRE's LFPS delivers its bands
as Int32 (Auburn2's `LF2024_FBFM40_CONUS` band is); round 2's LANDFIRE step writes the fuel it extracts as Int16
(`LandfireLayers.cs`), which avoids it for that download, but any other Int32 fuel raster a user names — a QGIS
export, a reclassified raster — still gives a case that cannot burn and a message that blames the ignition. Fix:
write `fbfm13`/`fbfm40`, `bldg_fuel_model` and `pyromes` (the other `%I2` rasters) as Int16 (`-ot Int16` in the
warp), and have the validator refuse any other type.

**D2 — k-PERIL's ellipse is not the fire's (MEDIUM, a modelling decision).** Anderson's (1983) L/B,
0.936 e^(0.2566 U) + 0.461 e^(−0.1548 U) − 0.397, is evaluated with U in **m/s** by ELMFIRE-WUINITY since 3a0a60a
(`elmfire_level_set.f90`: `0.1147*WSMFEFF*WSMFEFF_LOW_MULT`, i.e. 0.2566 x 0.44704 per mi/h, capped at `MAX_LOW` = 8)
and with U in **mi/h** by k-PERIL (`kperil.cs` `breakdownRateOfSpread`, given `mfws` / 88), as by upstream ELMFIRE and
FARSITE. At case 1's 4.23 mi/h midflame wind the fire spreads with L/B 1.47 (measured from its head and backing
rates: 1.468) and k-PERIL builds its boundary from L/B 2.62; the same algorithm with the fire's L/B gives a boundary
8.7 % larger (5098 against 4690 cells, Jaccard 0.92), 9 cells deep downwind instead of 6. Which unit is right is
Nick's call; whichever it is, the other half should use the same (and docs/elmfire-cases.md's "defined for midflame
wind in mi/h" should say which).

**D3 — an evacuation group with neither MaskFile nor ShapeFile crashes the run (MEDIUM).** The checklist calls a
single such group a warning ("every household is outside the group and is assigned to it as the default group",
`EvacuationGroupInput.cs:226`), but `EvacuationGroup.CreateShapeFilePolygon` opens the empty path with OGR and uses
the null data source: `NullReferenceException` (`EvacuationGroup.cs:182`), exit 2. Case 3c.

**D4 — an evacuation group naming no existing `[Demographics]` crashes the run (MEDIUM).** The checklist says "default
value the default demographics has been used", but the `EvacuationGroup` constructor falls back to
`simulation.Evacuation.DefaultDemographics` while `Simulation` is still constructing that `EvacuationManager`
(`Simulation.cs:86` → `EvacuationManager.cs:46` → `EvacuationGroup.cs:57`), so `simulation.Evacuation` is null:
`NullReferenceException`, exit 2. A scenario without any `[Demographics]` section would have no default to fall back
on either. Case 3d. Fix: pass the manager's default into `CreateGroupsFromInput`, and give the engine a built-in
default demographics.

**D5 — a run in which no car reaches SUMO exits 0 when fewer than 25 cars were tried (LOW).** `SUMOModule` stops a
run in which more than 90 % of the cars could not be put into SUMO, but only once `InjectionCheckMinimumCars` (25) have
been tried; a small scenario whose every car has no route (case 3e: 4 of 4) or fails to enter SUMO (Linux with the
committed bindings: 3 of 3 in case 3a) ends "successfully" with nobody evacuated, and without k-PERIL nothing makes
the exit code non-zero. Fix: also fail at the end of the run when cars were tried and none was injected.

**D6 — k-PERIL subtracts an upslope wind from the slope (MEDIUM).** `perilData.GetEffectiveWindWithSlope` adds the
vector of 0.06 x slope (mi/h) pointing upslope (aspect + 180°, with the downhill aspect PREACT hands it) to the wind's
vector pointing where the wind blows **from** (`wd.tif`'s convention). A wind blowing up a slope therefore reduces
the effective wind and one blowing down it increases it: in case 2c, 4.23 mi/h up a 20° slope gives |4.23 − 1.20| =
3.03 mi/h where 5.43 is meant, and the boundary is 5 % larger than with the two adding (Jaccard 0.953). Fix in
kPERILcore (turn the wind to where it blows) or in the hand-over, and state the convention; kPERILcore's own
`interpolateSlope` produces yet another aspect convention (a mathematical angle of the downhill direction).

**D7 — k-PERIL reads ELMFIRE's along-slope rate as a map rate (LOW–MEDIUM, conservative).** ELMFIRE's `vs` is the
rate along the slope (`C%VELOCITY`, "parallel to slope"); the fire covers `vs` x cos(slope) of map distance, as case 2
measures. k-PERIL divides the map cell size by `vs`, so on a slope it takes the fire to be 1/cos(slope) faster than it
is: in case 2c the boundary reaches 640 m below the WUI box where the fire covers 600 m in RSET (+6.4 % at 20°, +15 %
at 30°). Fix: hand k-PERIL the map-plane rate (ELMFIRE's projected `UX`/`UY` magnitude, or `vs` with ELMFIRE's own
projection factors).

Also found, not covered by a check:

- **docs/trigger-campaigns.md#convergence** says "The first comparison moves it neither way". In the code
  (`ConvergenceAggregator.Run`) it is the first *boundary* — which has nothing to compare against — that moves the
  streak neither way; the first comparison counts. So a campaign of identical boundaries converges at streak + 1
  (case 4), not streak + 2.
- `ElmfireNamelistInput.cs` documents `MAX_LOW` as an "upper bound on the low-wind branch of the spread solution,
  mi/h" and `WSMFEFF_LOW_MULT` as turning "mid-flame wind into the low-wind branch's effective wind"; they are the cap
  on the length-to-width ratio and the ft/min-to-mi/h factor of the effective midflame wind in the L/W correlation.

Observations, not discrepancies:

- ELMFIRE's `SEED` changes the fire slightly even with spotting off: between two seeds about 7 % of cells arrive
  one time step (9.4 s) apart and the area differs by 0.07 % (case 4, INFO). Each realization is still reproducible
  bit for bit from its seed.
- The level-set front runs about 1 % ahead of the local rate it is given (head, back and upslope alike: 22.97 against
  22.74 m/min in case 1, on 10 m cells).
- ELMFIRE writes −1 as the arrival time of the ignited cell itself.
- k-PERIL's 8-neighbour graph sets its downwind (backing) extent: a backing fire steps diagonally, at the ellipse's
  rate 45° off the back, so the boundary reaches 6 cells downwind in case 1 where its own ellipse's backing rate alone
  would give 1.
- A car starts at the beginning of the road edge its household's access point snaps to and arrives at the end of the
  destination's edge (PREACT routes edge to edge); on a long edge that adds up to the whole edge to the trip.
- SUMO's default car draws its desired speed from N(1, 0.1) x the limit; the cases switch that (and the driver
  imperfection) off with a vehicle type in the SUMO configuration, to state a travel time per car.

## Adding a case

Add `Verification/cases/caseN_<name>/` with a `README.md` (what is checked, the expected value and its derivation,
the tolerance and why) and a `case.py` with two functions: `generate(ctx, folder)` writes the inputs into `folder`
(use `vlib`: `firecase.FireCase` for a synthetic ELMFIRE case, `wuibox.write` for an evacuation with a boundary,
`roads.Network` for a road network, `tiff.write_geotiff` for rasters), and `run(ctx, folder)` runs the product
(`ctx.build_case`, `ctx.preact`, `ctx.cli`) and returns a list of `report.Check`. Register it in `CASES` in
`verify.py`. A check that fails because of a reported discrepancy gets `known="Dn"` and an entry above; keep inputs
generated, not committed.
