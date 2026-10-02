# Case 2 — slope only, no wind (and 2c: the slope with an upslope wind, through k-PERIL)

**What it runs.** Two fires of 1 h on 2 x 2 km (plus 200 m padding) of Anderson model 1 at 6 % 1-h moisture, both
with a calm uniform wind (`build-case --wind 0`), ignited at the cell centre (671005, 4316705):

- `flat/`: a level DEM, for the no-wind, no-slope rate R0;
- `slope/`: a planar DEM rising northward at 20° (z = 100 m + tan 20° x (y − 4316000)), so its aspect — the
  downhill bearing — is 180° and its upslope direction north.

Each is built with `PREACTcli build-case` and burned by `PREACT` (ELMFIRE through the engine); slope and aspect are
the builder's own (Horn's method on `dem.tif`).

| Check | Expected | Derivation | Tolerance, and why |
|---|---|---|---|
| Level, calm: rate of spread (`vs`, 50–200 m north of the ignition) | R0 = 1.404 m/min | Rothermel (1972) / Andrews (2018) with φ_w = φ_s = 0 (`vlib/rothermel.py`, the same intermediates as case 1: I_R = 826.1 Btu/ft²/min, ξ = 0.0578, heat sink 10.36 Btu/ft³) | 1 % (ELMFIRE's mineral content 0.055 vs 0.0555; float32) |
| Level, calm: the burned area at 3000 s is round | N–S extent = E–W extent | Anderson's L/B is 1 at zero wind | 1 cell |
| Slope raster | 20° | Horn's method on a plane returns the plane's slope | 0.05° |
| Aspect raster | 180° | the plane falls to the south | 0.5° |
| Upslope rate of spread (`vs`, along the slope, 100–400 m upslope) | 9.055 m/min | R0 (1 + φ_s), φ_s = 5.275 β^−0.3 tan² 20° = 5.4507 (β = 0.0010625) | 1 % |
| Upslope / level-ground ratio (both measured) | 1 + φ_s = 6.451 | as above; the ratio cancels everything but the slope factor | 1 % |
| Upslope rate from arrival times, on the map | R0 (1 + φ_s) cos 20° = 8.509 m/min | ELMFIRE spreads in the slope plane and projects the velocity onto the horizontal grid (`UYOUSY` = 1 − \|cos aspect\| (1 − cos slope)), so map distance per time is the slope-parallel rate times cos 20° | 2 %: the level-set front runs about 1 % ahead of the local rate, as in case 1 |
| Downslope (backing) rate (`vs`, 30–100 m downslope) | 2.281 m/min | upslope rate / HB, HB = 3.970 from Anderson's L/B = 1.2472 at the slope's effective wind: the wind that alone gives φ = φ_s, U_eff = (φ_s (β/β_op)^E / C)^(1/B) = 227.2 ft/min = 2.58 mi/h = 1.155 m/s (Rothermel 1972 eq. 87; U in m/s as ELMFIRE-WUINITY applies Anderson — see case 1) | 2 % |
| Upslope spread direction | 0° (north) | uphill | 0.5° |
| Mirror symmetry of the burned area at 3000 s across the slope | Jaccard 1 | the plane is symmetric about the N–S line through the ignition | ≥ 0.995: measured 0.998, a few rear-flank cells of level-set noise |

ELMFIRE tabulates tan² of the slope at whole degrees (`TANSLP2(NINT(slope))`), so the slope is a whole number of
degrees here on purpose; the slope factor is capped at the wind factor of the wind limit (0.9 I_R), which with
φ_s = 5.45 against a cap of 63.5 does not bind.

## 2c — the same slope with a wind blowing up it, and a k-PERIL boundary upslope

`slope_wind/`: the 20° plane (2.0 x 2.8 km), the 10 m wind at 6 m/s **from the south** (`--wind-dir 180`), so wind
and slope push the head the same way, north; ignition at (671005, 4316405); a WUI box 210 x 210 m whose southern edge
is 1 km upslope of the ignition, one household leaving at 1200 s north along a 900 m road (RSET 21.1 min); 1 h of fire.

| Check | Expected | Derivation | Tolerance |
|---|---|---|---|
| Head rate of spread (`vs`, 200–500 m upslope) | 30.38 m/min | R0 (1 + φ_w + φ_s) = 4.605 x (1 + 15.19 + 5.45) ft/min: Rothermel adds the two factors when they act in one direction (ELMFIRE sums them as vectors) | 1 % |
| The engine's boundary vs k-PERIL's algorithm re-computed from the fire's rasters | Jaccard ≥ 0.99 (measured 1.000) | the wiring, as in case 1 | |
| **D7** (known): how far below the box the boundary reaches | ⌊RSET x R cos 20° / 10 m⌋ cells = 600 m | the fire covers R cos 20° = 28.56 m/min of map distance upslope (case 2's projection), so that is how far down the slope a fire can start and still reach the box within RSET | 1 cell. Measured 640 m: k-PERIL divides the map cell size by `vs`, which ELMFIRE writes along the slope, so it takes the fire to be 1/cos 20° = 6.4 % faster than it is on the ground it crosses (XFAIL). |
| **D6** (known): the boundary vs the same algorithm with the wind and slope terms adding | Jaccard ≥ 0.99 | an upslope wind and the slope both push the fire upslope, so k-PERIL's effective wind should be 4.23 + 0.06 x 20 = 5.43 mi/h | Measured 0.953 (5800 vs 5530 cells): `perilData.GetEffectiveWindWithSlope` adds a vector towards where the wind comes **from** to one pointing **upslope** (aspect + 180°, PREACT handing it the downhill aspect), so the two subtract to 3.03 mi/h, and a downslope wind would add (XFAIL). |
