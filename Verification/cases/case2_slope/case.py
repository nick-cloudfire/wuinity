"""Case 2 - slope only, no wind: a planar 20-degree slope rising to the north against the same fuel on level ground;
then (2c) the same slope with a wind blowing up it and a k-PERIL boundary. See README.md beside this file."""

import math
import os

from vlib import firecase, kperil_ref, rasters, rothermel, scenario, shape, wuibox
from vlib.context import elmfire_outputs, grep_float
from vlib.report import Check, within

CASE = "2"
FUEL = 1
SLOPE_DEG = 20.0
M1, M10, M100 = 6.0, 7.0, 8.0
T_SHAPE = 3000.0

FC = firecase.FireCase("slope", width=2000.0, height=2000.0, cell=10.0, padding=200.0)
IGNITION = FC.centre_of(FC.x0 + 1000.0, FC.y0 + 700.0)
TAN = math.tan(math.radians(SLOPE_DEG))

# 2c: the same slope with a 6 m/s wind blowing up it (from the south), and a WUI box upslope of the ignition.
WIND_MS = 6.0
FC_W = firecase.FireCase("slope_wind", width=2000.0, height=2800.0, cell=10.0, padding=200.0)
IGNITION_W = FC_W.centre_of(FC_W.x0 + 1000.0, FC_W.y0 + 400.0)
WUI_BOX_W = (IGNITION_W[0] - 100.0, IGNITION_W[0] + 100.0, IGNITION_W[1] + 1000.0, IGNITION_W[1] + 1200.0)
RESPONSE_W = 1200.0


def expected(ctx):
    fm = rothermel.read_fuel_models(ctx.fuel_table)[FUEL]
    flat = rothermel.surface_fire(fm, M1 / 100, M10 / 100, M100 / 100, 0.6, 0.9, 0.0, 0.0)
    slope = rothermel.surface_fire(fm, M1 / 100, M10 / 100, M100 / 100, 0.6, 0.9, 0.0, SLOPE_DEG)
    u_eff_ms = slope["u_eff_ftmin"] / rothermel.MPH_TO_FTMIN * rothermel.MPH_TO_MS
    lb = rothermel.anderson_lb(u_eff_ms, "m/s")
    up = slope["ros"] * 0.3048
    return {"r0": flat["r0"] * 0.3048, "up": up, "ratio": 1.0 + slope["phi_s"], "phi_s": slope["phi_s"],
            "beta": slope["beta"], "lb": lb, "u_eff_mph": slope["u_eff_ftmin"] / rothermel.MPH_TO_FTMIN,
            "down": up / rothermel.head_to_back(lb)}


def generate(ctx, folder):
    for name, elevation in (("flat", lambda x, y: 100.0),
                            ("slope", lambda x, y: 100.0 + TAN * (y - FC.y0))):
        sub = os.path.join(folder, name)
        os.makedirs(sub)
        FC.write_rasters(sub, elevation, FUEL, "int16")
        scenario.write_wui(os.path.join(sub, name + ".wui"),
                           FC.sections(1, "fuel_int16.tif", IGNITION, elmfire_exe=ctx.elmfire))
    sub = os.path.join(folder, "slope_wind")
    os.makedirs(sub)
    FC_W.write_rasters(sub, lambda x, y: 100.0 + TAN * (y - FC_W.y0), FUEL, "int16")
    evac, _ = wuibox.write(ctx, sub, FC_W, WUI_BOX_W, RESPONSE_W)
    scenario.write_wui(os.path.join(sub, "slope_wind.wui"),
                       FC_W.sections(1, "fuel_int16.tif", IGNITION_W, extra=evac, elmfire_exe=ctx.elmfire))


def fire(ctx, sub, name, wind_ms=0.0, wind_from=0.0):
    wui = os.path.join(sub, name + ".wui")
    code, _, _ = ctx.build_case(sub, wui, ["--dem", os.path.join(sub, "dem.tif")]
                                + firecase.uniform_weather_args(wind_ms, wind_from, M1, M10, M100),
                                os.path.join(sub, "build-case.log"))
    if code != 0:
        raise RuntimeError("build-case failed, see " + os.path.join(sub, "build-case.log"))
    case = os.path.join(sub, "case")
    code, _, _ = ctx.preact(wui, sub, os.path.join(sub, "preact.log"))
    o = elmfire_outputs(case)
    if not o["time_of_arrival"]:
        raise RuntimeError("ELMFIRE wrote no arrival raster (PREACT exit %d), see %s" % (code, os.path.join(sub, "preact.log")))
    scratch = os.path.join(sub, "_asc")
    return (firecase.case_grid(ctx, case), ctx.read(o["time_of_arrival"], scratch), ctx.read(o["vs"], scratch),
            ctx.read(o["spread_dir"], scratch), case)


def run(ctx, folder):
    exp = expected(ctx)
    checks = []
    grid, toa0, vs0, _, _ = fire(ctx, os.path.join(folder, "flat"), "flat")
    ic, ir = grid.cell_of(*IGNITION)
    ring = [(d, c, r) for d, c, r in shape.axis_cells(vs0, ic, ir, 0, -1, 40) if 50.0 <= d <= 200.0]
    r0 = shape.mean_along(vs0, ring)
    checks.append(Check(CASE, "level, calm: ROS (vs) vs Rothermel R0", r0, exp["r0"], "1 %",
                        within(r0, exp["r0"], rel=0.01), "m/min"))
    burned = shape.burned_at(toa0, T_SHAPE)
    ns = shape.extent_along(burned, ic, ir, 0, -1) + shape.extent_along(burned, ic, ir, 0, 1) + 1
    ew = shape.extent_along(burned, ic, ir, 1, 0) + shape.extent_along(burned, ic, ir, -1, 0) + 1
    circ = ns / float(ew) if ew else None
    checks.append(Check(CASE, "level, calm: burned area at %.0f s round (N-S / E-W)" % T_SHAPE, circ, 1.0,
                        "1 cell in %d" % ew, circ is not None and abs(ns - ew) <= 1, "",
                        "Anderson's L/B is 1 without wind"))

    grid, toa, vs, sd, case = fire(ctx, os.path.join(folder, "slope"), "slope")
    scratch = os.path.join(folder, "slope", "_asc")
    slp = ctx.read(os.path.join(case, "inputs", "slp.tif"), scratch)
    asp = ctx.read(os.path.join(case, "inputs", "asp.tif"), scratch)
    s_mid, a_mid = slp.get(ic, ir), asp.get(ic, ir)
    checks.append(Check(CASE, "slope raster of the planar DEM (Horn)", s_mid, SLOPE_DEG, "0.05 deg",
                        within(s_mid, SLOPE_DEG, abs_=0.05), "deg"))
    checks.append(Check(CASE, "aspect raster (downhill bearing), slope rising north", a_mid, 180.0, "0.5 deg",
                        within(a_mid, 180.0, abs_=0.5), "deg"))
    upc = [(d, c, r) for d, c, r in shape.axis_cells(vs, ic, ir, 0, -1, 60) if 100.0 <= d <= 400.0]
    up = shape.mean_along(vs, upc)
    checks.append(Check(CASE, "upslope ROS (vs, along the slope) vs R0 (1 + phi_s)", up, exp["up"], "1 %",
                        within(up, exp["up"], rel=0.01), "m/min",
                        "phi_s = 5.275 beta^-0.3 tan^2 = %.4f (beta %.6f)" % (exp["phi_s"], exp["beta"])))
    ratio = up / r0 if up and r0 else None
    checks.append(Check(CASE, "upslope / level-ground ROS ratio vs 1 + phi_s", ratio, exp["ratio"], "1 %",
                        within(ratio, exp["ratio"], rel=0.01)))
    horiz = shape.toa_slope(toa, upc)
    checks.append(Check(CASE, "upslope ROS from arrival times (map) vs R0 (1 + phi_s) cos 20", horiz,
                        exp["up"] * math.cos(math.radians(SLOPE_DEG)), "2 %",
                        within(horiz, exp["up"] * math.cos(math.radians(SLOPE_DEG)), rel=0.02), "m/min",
                        "ELMFIRE spreads along the slope and projects onto the map"))
    downc = [(d, c, r) for d, c, r in shape.axis_cells(vs, ic, ir, 0, 1, 30) if 30.0 <= d <= 100.0]
    down = shape.mean_along(vs, downc)
    checks.append(Check(CASE, "downslope ROS vs upslope / HB(L/B of the slope's effective wind)", down,
                        exp["down"], "2 %", within(down, exp["down"], rel=0.02), "m/min",
                        "slope-equivalent wind %.2f mi/h, L/B %.4f (U in m/s)" % (exp["u_eff_mph"], exp["lb"])))
    direction = shape.mean_direction(sd, upc)
    if direction is not None and direction > 180.0:
        direction -= 360.0
    checks.append(Check(CASE, "upslope spread direction vs uphill (north)", direction, 0.0, "0.5 deg",
                        within(direction, 0.0, abs_=0.5), "deg"))
    burned = shape.burned_at(toa, T_SHAPE)
    sym = shape.mirror_jaccard(burned, axis_col=ic)
    checks.append(Check(CASE, "burned area at %.0f s symmetric across the slope (Jaccard)" % T_SHAPE, sym, 1.0,
                        ">= 0.995", sym is not None and sym >= 0.995, "",
                        "a few perimeter cells of level-set noise"))
    return checks + slope_wind(ctx, folder)


def slope_wind(ctx, folder):
    """2c: wind and slope together, and the k-PERIL boundary upslope."""
    sub = os.path.join(folder, "slope_wind")
    checks = []
    grid, toa, vs, sd, case = fire(ctx, sub, "slope_wind", WIND_MS, 180.0)
    scratch = os.path.join(sub, "_asc")
    checks.append(wuibox.check_wui_area(ctx, CASE, case, grid, WUI_BOX_W, scratch)[0])
    fm = rothermel.read_fuel_models(ctx.fuel_table)[FUEL]
    umf = 0.87 * WIND_MS / rothermel.MPH_TO_MS * rothermel.unsheltered_waf(fm.depth) * rothermel.MPH_TO_FTMIN
    r = rothermel.surface_fire(fm, M1 / 100, M10 / 100, M100 / 100, 0.6, 0.9, umf, SLOPE_DEG)
    head_exp = r["ros"] * 0.3048
    ic, ir = grid.cell_of(*IGNITION_W)
    upc = [(d, c, rr) for d, c, rr in shape.axis_cells(vs, ic, ir, 0, -1, 60) if 200.0 <= d <= 500.0]
    head = shape.mean_along(vs, upc)
    checks.append(Check(CASE, "2c upslope wind + slope: head ROS vs R0 (1 + phi_w + phi_s)", head, head_exp, "1 %",
                        within(head, head_exp, rel=0.01), "m/min",
                        "phi_w %.3f + phi_s %.3f, the two vectors aligned" % (r["phi_w"], r["phi_s"])))

    with open(os.path.join(sub, "preact.log")) as f:
        log = f.read()
    rset = grep_float(log, r"RSET = ([0-9.]+) minutes")
    path = os.path.join(sub, "_output", "0_trigger_boundary.asc")
    if rset is None or not os.path.isfile(path):
        checks.append(Check.error(CASE, "2c k-PERIL boundary", "no boundary or RSET in " + os.path.join(sub, "preact.log")))
        return checks
    wui_cells = firecase.box_cells(grid, *WUI_BOX_W)
    outside = firecase.read_boundary(path)[1] - wui_cells
    mf = ctx.read(elmfire_outputs(case)["mfws"], scratch)
    mph = rasters.Grid(mf.ncols, mf.nrows, mf.xll, mf.yll, mf.cell,
                       [[(v / rothermel.MPH_TO_FTMIN) if v is not None and v >= 0 else 0.0 for v in row]
                        for row in mf.values])
    inputs = os.path.join(case, "inputs")
    wd = ctx.read(os.path.join(inputs, "wd.tif"), scratch)
    slp = ctx.read(os.path.join(inputs, "slp.tif"), scratch)
    asp = ctx.read(os.path.join(inputs, "asp.tif"), scratch)
    ref = kperil_ref.boundary(vs, sd, mph, wd, slp, asp, wui_cells, rset, grid.cell)
    jr = shape.jaccard(outside, ref)
    checks.append(Check(CASE, "2c k-PERIL boundary vs its algorithm re-computed (Jaccard)", jr, 1.0, ">= 0.99",
                        jr >= 0.99, "", "%d cells (engine) vs %d (reference)" % (len(outside), len(ref))))

    # D7: how far downslope of the box the boundary reaches, against how far the fire travels on the map in RSET.
    wy0 = max(rr for c, rr in wui_cells)          # the box's southernmost row
    down = shape.extent_along(outside, ic, wy0, 0, 1)
    cos = math.cos(math.radians(SLOPE_DEG))
    exp_cells = math.floor(rset * head * cos / grid.cell + 1e-9)
    checks.append(Check(CASE, "2c boundary reach below the box vs RSET x the fire's map speed", down * grid.cell,
                        exp_cells * grid.cell, "1 cell", abs(down - exp_cells) <= 1, "m",
                        "the fire covers %.2f m/min of map upslope (vs x cos 20); k-PERIL reads vs = %.2f m/min as map "
                        "speed: %d cells" % (head * cos, head, math.floor(rset * head / grid.cell + 1e-9)), known="D7"))

    # D6: the same algorithm with the wind and slope terms pointing the way they push the fire.
    aligned = kperil_ref.boundary(vs, sd, mph, wd, slp, asp, wui_cells, rset, grid.cell,
                                  effective=kperil_ref.effective_wind_aligned)
    ja = shape.jaccard(outside, aligned)
    u = umf / rothermel.MPH_TO_FTMIN
    s_term = 0.06 * SLOPE_DEG
    checks.append(Check(CASE, "2c boundary vs the same with wind and slope terms adding (Jaccard)", ja, 1.0,
                        ">= 0.99", ja >= 0.99, "",
                        "k-PERIL's effective wind %.2f mi/h (|%.2f - %.2f|), aligned %.2f; %d vs %d cells"
                        % (abs(u - s_term), u, s_term, u + s_term, len(outside), len(aligned)), known="D6"))
    return checks
