# Case 4 — a tiny trigger campaign

**What it runs.** A 2.4 x 1.4 km case of Anderson model 1 (10 m cells, 200 m padding) built with `PREACTcli
build-case`, whose ignition mask (`[ELMFIRE] IgnitionMaskFile`, warped by the builder) has **one** ignitable cell,
(670405, 4316705); a WUI box 200 x 210 m, 0.8 km east of it, the evacuation group's area, which `build-case` writes
onto the case grid as `case/inputs/wui_area.tif` (checked against the box);
two households in the box leaving at 600 s on a road north (SUMO). The campaign:

```
PREACTcli converge-trigger --wui campaign.wui --max 8 --hours 1 --seed 12345 --streak 3 --tolerance 0.02
    --parallel 1 --allow-uniform-weather --no-live-fuel-moisture --weather-archive era5_constant.csv
    --climatology-from 2023 --climatology-to 2024 --preact <PREACT> --elmfire <ELMFIRE> --gdal <GDAL bin>
```

`era5_constant.csv` is a two-year hourly archive in the Open-Meteo layout whose every hour is 30 °C, 20 % RH, no rain,
6 m/s from 270° (the campaign re-derives its fire-weather columns on its own copy). So every realization draws the same
weather (the normals fitted to a constant pool have no spread), the same ignition and the same evacuation departures;
only ELMFIRE's `SEED` (= seed + i) and the evacuation seed differ. The campaign is then run a second time from
scratch, after moving the first campaign's folder aside.

| Check | Expected | Derivation | Tolerance |
|---|---|---|---|
| The campaign converges after | 4 boundaries | docs/trigger-campaigns.md#convergence: the first boundary has nothing to compare against, every later one whose ten decile areas each change by less than `--tolerance` adds one to the streak; with boundaries that differ by numerical noise only, the streak after n boundaries is n − 1, so it reaches `--streak` 3 at n = 4 | exact |
| Premise: the largest decile-area change between successive boundaries | < 0.02 | see above (measured 0.0005: one 10 m cell of 2119) | |
| `trigger_convergence.csv` decile areas | the areas of P ≥ 0.1 … 1.0 recomputed from the realizations' kept boundary rasters, in the order the campaign folded them | the documented rule, applied independently (`reaggregate()`) | exact (m²) |
| `trigger_convergence.csv` streaks | 0 1 2 3 | the same | exact |
| `trigger_probability.asc` | per cell, the fraction of the boundaries holding it | the same | 1e-6 |
| ELMFIRE `SEED` in each realization's `outputs/run.data` | 12345 + i | docs/trigger-campaigns.md#seeds | exact |
| Evacuation seed (`realizations.csv`, and `[Simulation] RandomSeed` of `preact_scenario.wui`) | 12345 + 2 000 000 + i | the same | exact |
| Every realization's ignition | the mask's one cell | the ignition is drawn from the mask with probability proportional to its weight | 0.5 m |
| Drawn weather | 13.42 mi/h (6 m/s) at 10 m, aimed from 270° (the bearing from the WUI centroid to the ignition), dead 1-h moisture 4.1586 % | the archive's wind; the wind is aimed from the ignition at the WUI centroid; Simard's (1968) equilibrium moisture in its NFDRS form, 2.22749 + 0.160107 RH − 0.01478 T(°F) for 10 ≤ RH < 50 | 0.1 mi/h (the CSV rounds to 0.01, and with WindNinja installed the drawn wind is its domain mean, a few hundredths off on flat ground), 0.5°, 0.01 % |
| Realization 1's head rate of spread (`vs`, 200–500 m downwind) | 25.52 m/min | Rothermel at the drawn weather: 1-h 4.16 %, midflame wind as in case 1 | 1 % |
| Rerun from scratch: realization records | identical `status`, ignition, wind, moisture, evacuation seed and fire area | per-realization seeds make every realization reproducible on its own | every field |
| Rerun from scratch: arrival rasters and boundaries | bit-identical | the same | md5 |
| (INFO) distinct arrival rasters among the realizations | | ELMFIRE's `SEED` still changes the fire with spotting off: about 7 % of cells arrive one time step (9.4 s) earlier or later, 0.07 % of the area | |

The campaign takes about 15 s per realization here (ELMFIRE seconds, SUMO with two cars).
