# Case 1 — flat ground, uniform fuel, constant wind, point ignition

**What it runs.** `generate()` in [`case.py`](case.py) writes a flat DEM (100 m everywhere) and a fuel raster
(Anderson model 1 everywhere, Int16) on a 10 m UTM lattice (EPSG:32610, 670000 E 4316000 N, near Auburn, CA), a
two-edge road north out of a WUI box, one household, and `flat_wind.wui`. `run()` builds the case with
`PREACTcli build-case --dem dem.tif --no-climatology --wind 6 --wind-dir 270 --m1 6 --m10 7 --m100 8` (uniform
weather, no WindNinja), writes the WUI box onto the case grid as `case/inputs/wui_area.tif`, and runs
`PREACT flat_wind.wui`: ELMFIRE through the engine (2 h of fire, `ENABLE_SPOTTING=false`), the evacuation in SUMO and
the k-PERIL boundary. Domain 3.6 x 2.0 km plus 200 m padding; ignition at the cell centre (670305, 4317005), so the
wind axis is the row through it.

## The fire

Inputs, from ELMFIRE's own `fuel_models.csv` row 1 (`FBFM01`): 1-h load 0.034 lb/ft² (0.74 t/ac), SAV 3500 1/ft,
depth 1 ft, dead moisture of extinction 12 %, heat content 8000 Btu/lb; 1-h moisture 6 %; 10 m wind 6 m/s from 270°.

| Check | Expected | Derivation | Tolerance, and why |
|---|---|---|---|
| Midflame wind (`mfws`) | 372.7 ft/min (4.235 mi/h) | 20-ft wind = 0.87 x 10 m wind (the 1/1.15 rule ELMFIRE applies with `WS_AT_10M`) = 11.68 mi/h; unsheltered wind adjustment factor for a 1 ft bed, Andrews (2012) eq. 6 with H_F/H = 1 (BehavePlus): 1.36 / ln((20 + 0.36) / 0.13) x (ln(1.36 / 0.13) - 1) = 0.3627 | 0.5 %: the same closed form; only float32 rounding separates them. |
| Head rate of spread (`vs` raster, mean over the axis cells 0.5–1.5 km downwind) | 22.73 m/min | Rothermel (1972) as in Andrews (2018), written in `vlib/rothermel.py`: β = 0.0010625, β_op = 0.004193, Γ' = 14.20 /min, I_R = 826.1 Btu/ft²/min, ξ = 0.0578, ρ_b ε Q_ig = 10.36 Btu/ft³, R0 = 4.605 ft/min; φ_w = C U^B (β/β_op)^-E = 15.19 (C = 5.42e-5, B = 2.071, E = 0.2035); R = R0 (1 + φ_w) = 74.56 ft/min. The wind limit 0.9 I_R = 743.5 ft/min is not reached. | 1 %: ELMFIRE takes the total mineral content as 0.055 where BehavePlus takes 0.0555 (+0.05 % in R); the rest is float32. Measured +0.05 %. |
| Head rate from arrival times (least squares over the same cells) | 22.73 m/min | as above: the level-set front should travel at the local rate | 2 %: the level-set front runs about 1 % ahead of the local rate (head and back alike). |
| Backing rate (`vs` upwind, 50–250 m behind the ignition) | 3.513 m/min | R / HB with HB = (L/B + √(L/B² − 1)) / (L/B − √(L/B² − 1)) = 6.47, L/B from Anderson (1983), 0.936 e^(0.2566 U) + 0.461 e^(−0.1548 U) − 0.397, at U = 1.894 m/s = 1.468 | 1 %: closed form. See the unit note below. |
| Head spread direction | 90° | the wind blows from 270° | 0.5° |
| Mirror symmetry of the burned area at 6000 s about the wind axis | Jaccard 1 | uniform fuel and wind | ≥ 0.995: a few perimeter cells of level-set noise (measured 0.9995). |
| Length-to-breadth of the burned area at 6000 s | 1.468 | (head + back extent along the axis) / largest width, against Anderson's L/B above | 3 %: ±1 cell on a 1.3 km width (0.8 %) plus the level set. Measured 1.475. |
| The burned area is an ellipse | Jaccard ≥ 0.98 with the ellipse of its own head, back and width (ignition at the rear focus) | Huygens' principle with elliptical wavelets on uniform ground gives an elliptical fire | 0.98: 10 m cells on a 2.6 x 1.8 km ellipse; measured 0.994. |
| The burned area vs the ellipse predicted from R, R/HB and L/B after 100 min | Jaccard ≥ 0.95 | as above, nothing measured | 0.95: the ~1 % level-set overshoot in every direction is ~2 % of the area. Measured 0.97. |

**Anderson's unit.** Anderson's coefficients 0.2566 and 0.1548 are applied to U in **m/s** by ELMFIRE-WUINITY (since
3a0a60a, its code says 0.1147 and 0.0692 per mi/h, i.e. 0.2566 × 0.44704), and to U in **mi/h** by upstream ELMFIRE,
FARSITE and k-PERIL. The fire checks follow the fire model the platform runs, so they verify that ELMFIRE's level set
reproduces the L/B its spread equations use (with mi/h the L/B would be 2.617). That the two halves of the platform
disagree is discrepancy **D2** below.

## The evacuation and the k-PERIL boundary

One household in the WUI box (300 x 310 m, 30 x 31 cells, centred on the wind axis 1.2–1.5 km downwind of the
ignition) leaves at 1200 s (`[ResponseCurve]` rows `1200,0` and `1200,1`), its car starting at the road's first node
and driving 900 m at 50 km/h to the exit. The fire reaches the box after about 53 min, long after the evacuation; the
boundary uses RSET = the car's arrival.

| Check | Expected | Derivation | Tolerance |
|---|---|---|---|
| The car's arrival | 1268.5 s | 1200 s + 900 m / 13.89 m/s + 13.89 / (2 x 2.6) s (accelerating at SUMO's default 2.6 m/s²) + 1 s (the car enters SUMO on the step after its household reaches it) | 3 s: step quantization, junction internal lanes. |
| Upwind extent of the boundary on the wind axis | ⌊RSET x R / 10 m⌋ cells = 480 m at RSET 21.15 min | every cell upwind of the box on the axis burned as head fire with `vs` = R and direction 90°; k-PERIL's rate from such a cell towards the WUI (its head direction) is R, and its cell-to-cell time is cell / R, so the boundary reaches as far as the head fire travels in RSET | 1 cell |
| The boundary lies upwind | bearing 270° ± 5° from the WUI centroid to the centroid of the boundary cells outside it | a head fire from the west must be stopped west of the WUI | 5° |
| Mirror symmetry of the boundary about the wind axis | Jaccard ≥ 0.99 | the fire and the box are symmetric | 0.99 |
| The engine's boundary vs a re-computation of k-PERIL's algorithm (`vlib/kperil_ref.py`) from the fire's `vs`, `spread_dir`, `mfws`/88, `wd`, `slp` and `asp` | Jaccard ≥ 0.99 (measured 1.000) | not an independent physical expectation: it reproduces k-PERIL's modelling, so it checks the *wiring* — the rasters, their layout (the pre-v1 90° rotation) and units | 0.99 |
| **D2** (known): the boundary vs the same algorithm with the fire's own L/B (U in m/s) | Jaccard ≥ 0.99 | the boundary's ellipse should be the fire's | measured 0.92: k-PERIL's L/B is 2.62 where the fire's is 1.47; the boundary is 4690 cells where the fire's ellipse gives 5098, its downwind extent 6 cells against 9 (XFAIL). |

The downwind extent is not checked against a closed form: k-PERIL's 8-neighbour graph lets a backing fire step
diagonally, so its effective backing rate is set by the ellipse at ψ = 225°, not by the backing rate at 180°.

## 1b — the fuel raster's sample type (D1, fixed)

The same fire on a small domain (1 x 1 km, 1 h), once with the fuel raster typed **Int16** and once **Int32** —
the type LANDFIRE's LFPS service delivers its bands in (`downloads/landfire/*.tif` of Auburn2). ELMFIRE reads the fuel
model raster only as 16-bit integers (`FBFM%I2`); an Int32 one is decoded into its float buffer instead, its fuel
array stays empty, every cell is non-burnable, and the run reports "elmfire burned 0 acres". Checked: the case built
from the Int32 source stores its `fbfm13.tif` as Int16 (read from the TIFF's own tags), and the fire burns the same
area (`fire_size_stats.csv`) both times, ± 0.1 %. Before the fix (v1.1 r2-fix) the builder kept the source's type and
the second fire burned 0 acres against 224.1.

## 1c — a dynamic fuel model

GR2 (Scott and Burgan 2005, ELMFIRE row 102: 1-h 0.00459 lb/ft², live herbaceous 0.04591 lb/ft², SAV 2000/1800 1/ft,
depth 1 ft, moisture of extinction 15 %) at ELMFIRE's default 60 % live herbaceous moisture, i.e. (120 − 60) / 90 = 2/3
of the herbaceous load cured; same wind and dead moisture. Expected head ROS 9.256 m/min: BehavePlus moves the cured
load into a dead herbaceous class with the herbaceous SAV and weights the net load by SAV subclass (g_ij, Andrews
2018); ELMFIRE merges it into the 1-h class with an area-weighted SAV, which comes to the same within 0.1 %.
Tolerance 1 %.
