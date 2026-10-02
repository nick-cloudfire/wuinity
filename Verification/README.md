# Verification

Basic verification cases for the head-less product: small synthetic inputs, an expected value for every check derived
independently of the code under test (theory or hand calculation, stated in each case's README), and a runner that
prints measured against expected with a PASS/FAIL per check. How to run it, the results and the discrepancies found
are in [docs/verification.md](../docs/verification.md).

```
./verify.sh            # Linux     (or: python3 Verification/verify.py ...)
.\verify.ps1           # Windows
```

| Path | What |
|---|---|
| `verify.py` | The runner: finds the tools, generates each case's inputs into `_runs/<time>/<case>/`, runs it, prints the table, writes `results.txt` and `results.csv`. |
| `cases/case1_flat_wind/` | Flat ground, uniform fuel, constant wind: rate of spread, L/B, elliptical shape; the k-PERIL boundary of a WUI box; the fuel raster's type; a dynamic fuel model. |
| `cases/case2_slope/` | Slope only, no wind: the upslope rate of spread and its ratio to level ground; then the slope with an upslope wind and a k-PERIL boundary. |
| `cases/case3_evacuation/` | Evacuation without fire on a corridor of known lengths and limits; households reacting to a synthetic front; three scenario-robustness checks. |
| `cases/case4_campaign/` | A tiny trigger campaign that must converge, with reproducible per-realization seeds. |
| `vlib/` | Shared helpers, standard library only: UTM (`geo.py`), a GeoTIFF writer (`tiff.py`), raster reading via `gdal_translate` (`rasters.py`), Rothermel/BehavePlus (`rothermel.py`), k-PERIL's algorithm (`kperil_ref.py`), shape measures (`shape.py`), roads and netconvert (`roads.py`), the synthetic fire case and its WUI-box evacuation (`firecase.py`, `wuibox.py`), scenario writing, the results table. |
| `tools/linux-sumo-glue.sh` | Linux bench only: a scratch PREACT with SUMO bindings matching a locally built libsumocs. |

Each case's `case.py` has `generate(ctx, folder)` (writes the inputs; the inputs are never committed) and
`run(ctx, folder)` (runs the product and returns the checks). Nothing here is part of the product or the build.
