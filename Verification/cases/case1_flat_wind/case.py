"""Case 1 - flat ground, uniform Anderson fuel model 1, constant wind, point ignition; then an evacuation and the
k-PERIL boundary of a WUI box downwind. See README.md beside this file for what is checked and why."""

import math
import os

from vlib import firecase, kperil_ref, rasters, roads, rothermel, scenario, shape, tiff, wuibox
from vlib.context import elmfire_acres, elmfire_outputs, grep_float
from vlib.report import Check, within

CASE = "1"
FUEL = 1                     # Anderson (1982) model 1, short grass: ELMFIRE's fuel_models.csv row 1 (FBFM01)
WIND_MS = 6.0                # 10 m wind, m/s (build-case --wind), uniform
WIND_FROM = 270.0            # from the west: the head runs east
M1, M10, M100 = 6.0, 7.0, 8.0
HOURS = 2
T_SHAPE = 6000.0             # s, the arrival-time contour the shape is measured on
RESPONSE_S = 1200.0          # every household leaves at 20 min
ROAD_KMH = wuibox.ROAD_KMH

FC = firecase.FireCase("flat_wind", width=3600.0, height=2000.0, cell=10.0, padding=200.0)
IGNITION = FC.centre_of(FC.x0 + 300.0, FC.y0 + 1000.0)
AXIS_Y = IGNITION[1]
WUI_BOX = (FC.x0 + 1500.0, FC.x0 + 1800.0, AXIS_Y - 150.0, AXIS_Y + 150.0)   # x0, x1, y0, y1
GR2 = 102                    # Scott and Burgan (2005) GR2, a dynamic grass model (ELMFIRE's row 102)
GR2_LH = 60.0                # live herbaceous moisture, %: ELMFIRE's default LH_MOISTURE_CONTENT
GR2_CASE = firecase.FireCase("gr2", width=1500.0, height=1000.0, cell=10.0, padding=100.0)
GR2_IGNITION = GR2_CASE.centre_of(GR2_CASE.x0 + 300.0, GR2_CASE.y0 + 500.0)


def expected_fire(ctx):
    fm = rothermel.read_fuel_models(ctx.fuel_table)[FUEL]
    waf = rothermel.unsheltered_waf(fm.depth)
    ws20_mph = 0.87 * WIND_MS / rothermel.MPH_TO_MS      # ELMFIRE's 10 m -> 20 ft rule (WS_AT_10M), 1/1.15
    umf = ws20_mph * waf * rothermel.MPH_TO_FTMIN        # ft/min
    r = rothermel.surface_fire(fm, M1 / 100, M10 / 100, M100 / 100, 0.6, 0.9, umf)
    lb_ms = rothermel.anderson_lb(umf / rothermel.MPH_TO_FTMIN * rothermel.MPH_TO_MS, "m/s")
    lb_mph = rothermel.anderson_lb(umf / rothermel.MPH_TO_FTMIN, "mi/h")
    head = r["ros"] * 0.3048                              # m/min
    return {"fm": fm, "waf": waf, "umf": umf, "ros": head, "lb": lb_ms, "lb_mph": lb_mph,
            "back": head / rothermel.head_to_back(lb_ms), "r": r}


def generate(ctx, folder):
    FC.write_rasters(folder, lambda x, y: 100.0, FUEL, "int16")
    evac, _ = wuibox.write(ctx, folder, FC, WUI_BOX, RESPONSE_S)
    scenario.write_wui(os.path.join(folder, "flat_wind.wui"),
                       FC.sections(HOURS, "fuel_int16.tif", IGNITION, extra=evac, elmfire_exe=ctx.elmfire))

    # 1b: the same fire on a small domain, fuel typed Int16 and Int32 (LANDFIRE's LFPS delivers Int32 bands).
    for dtype in ("int16", "int32"):
        sub = os.path.join(folder, "fuel_" + dtype)
        os.makedirs(sub)
        small = firecase.FireCase("fuel_" + dtype, width=1000.0, height=1000.0, cell=10.0, padding=100.0)
        small.write_rasters(sub, lambda x, y: 100.0, FUEL, dtype)
        scenario.write_wui(os.path.join(sub, "fuel.wui"),
                           small.sections(1, "fuel_%s.tif" % dtype,
                                          small.centre_of(small.x0 + 300.0, small.y0 + 500.0),
                                          elmfire_exe=ctx.elmfire))

    # 1c: a dynamic Scott and Burgan model, GR2, at the default 60 % live herbaceous moisture (2/3 cured).
    sub = os.path.join(folder, "gr2")
    os.makedirs(sub)
    GR2_CASE.write_rasters(sub, lambda x, y: 100.0, GR2, "int16")
    scenario.write_wui(os.path.join(sub, "gr2.wui"),
                       GR2_CASE.sections(1, "fuel_int16.tif", GR2_IGNITION, elmfire_exe=ctx.elmfire, standard="FBFM40",
                                         namelist=[("LH_MOISTURE_CONTENT", "%g" % GR2_LH)]))


def gr2_check(ctx, folder):
    sub = os.path.join(folder, "gr2")
    wui = os.path.join(sub, "gr2.wui")
    code, _, _ = ctx.build_case(sub, wui, ["--dem", os.path.join(sub, "dem.tif")]
                                + firecase.uniform_weather_args(WIND_MS, WIND_FROM, M1, M10, M100),
                                os.path.join(sub, "build-case.log"))
    if code != 0:
        return Check.error(CASE, "1c build-case (GR2)", "see " + os.path.join(sub, "build-case.log"))
    ctx.preact(wui, sub, os.path.join(sub, "preact.log"))
    o = elmfire_outputs(os.path.join(sub, "case"))
    if not o["vs"]:
        return Check.error(CASE, "1c GR2 fire", "no rate-of-spread raster, see " + os.path.join(sub, "preact.log"))
    vs = ctx.read(o["vs"], os.path.join(sub, "_asc"))
    ic, ir = vs.cell_of(*GR2_IGNITION)
    head = shape.mean_along(vs, [(d, c, r) for d, c, r in shape.axis_cells(vs, ic, ir, 1, 0, 60) if 200 <= d <= 500])
    fm = rothermel.read_fuel_models(ctx.fuel_table)[GR2]
    umf = 0.87 * WIND_MS / rothermel.MPH_TO_MS * rothermel.unsheltered_waf(fm.depth) * rothermel.MPH_TO_FTMIN
    r = rothermel.surface_fire(fm, M1 / 100, M10 / 100, M100 / 100, GR2_LH / 100, 0.9, umf)
    exp = r["ros"] * 0.3048
    return Check(CASE, "1c GR2 head ROS (dynamic, %g %% live herb) vs BehavePlus" % GR2_LH, head, exp, "1 %",
                 within(head, exp, rel=0.01), "m/min",
                 "cured load moved to a dead herbaceous class (Scott and Burgan 2005), net load by SAV subclass (g_ij)")


def run(ctx, folder):
    checks = []
    exp = expected_fire(ctx)
    wui = os.path.join(folder, "flat_wind.wui")
    case = os.path.join(folder, "case")

    code, out, _ = ctx.build_case(folder, wui, ["--dem", os.path.join(folder, "dem.tif")]
                                  + firecase.uniform_weather_args(WIND_MS, WIND_FROM, M1, M10, M100),
                                  os.path.join(folder, "build-case.log"))
    if code != 0:
        return [Check.error(CASE, "build-case", "exit %d, see %s" % (code, os.path.join(folder, "build-case.log")))]
    grid = firecase.case_grid(ctx, case)
    area_check, wui_cells = wuibox.check_wui_area(ctx, CASE, case, grid, WUI_BOX, os.path.join(folder, "_asc"))
    checks.append(area_check)

    code, log, seconds = ctx.preact(wui, folder, os.path.join(folder, "preact.log"))
    if code != 0:
        checks.append(Check(CASE, "PREACT run (ELMFIRE + evacuation + k-PERIL) exits 0", code, 0, "exact", False,
                            note="see " + os.path.join(folder, "preact.log") + "; " + ctx.preact_note))
        if not elmfire_outputs(case)["time_of_arrival"]:
            return checks

    o = elmfire_outputs(case)
    scratch = os.path.join(folder, "_asc")
    toa = ctx.read(o["time_of_arrival"], scratch)
    vs = ctx.read(o["vs"], scratch)
    sd = ctx.read(o["spread_dir"], scratch)
    mf = ctx.read(o["mfws"], scratch)
    ic, ir = grid.cell_of(*IGNITION)

    # --- the fire --------------------------------------------------------------------------------------------
    head_cells = [(d, c, r) for d, c, r in shape.axis_cells(vs, ic, ir, 1, 0, 160) if 500.0 <= d <= 1500.0]
    head = shape.mean_along(vs, head_cells)
    checks.append(Check(CASE, "head ROS (vs, 0.5-1.5 km downwind) vs Rothermel/BehavePlus", head, exp["ros"],
                        "1 %", within(head, exp["ros"], rel=0.01), "m/min",
                        "FBFM01, M1 %g %%, U_mf %.1f ft/min, phi_w %.3f, R0 %.4f m/min"
                        % (M1, exp["umf"], exp["r"]["phi_w"], exp["r"]["r0"] * 0.3048)))
    umf = shape.mean_along(mf, head_cells)
    checks.append(Check(CASE, "midflame wind (mfws) vs 20-ft wind x unsheltered WAF", umf, exp["umf"], "0.5 %",
                        within(umf, exp["umf"], rel=0.005), "ft/min",
                        "WAF %.4f (Andrews 2012, depth %.1f ft); 20-ft = 0.87 x 10 m wind" % (exp["waf"], exp["fm"].depth)))
    toa_head = shape.toa_slope(toa, head_cells)
    checks.append(Check(CASE, "head ROS from arrival times vs Rothermel", toa_head, exp["ros"], "2 %",
                        within(toa_head, exp["ros"], rel=0.02), "m/min", "least squares over 0.5-1.5 km"))
    back_cells = [(d, c, r) for d, c, r in shape.axis_cells(vs, ic, ir, -1, 0, 60) if 50.0 <= d <= 250.0]
    back = shape.mean_along(vs, back_cells)
    checks.append(Check(CASE, "backing ROS (vs, upwind) vs head / HB(Anderson L/B)", back, exp["back"], "1 %",
                        within(back, exp["back"], rel=0.01), "m/min",
                        "L/B %.4f from U_mf in m/s, as ELMFIRE-WUINITY applies Anderson (1983)" % exp["lb"]))
    direction = shape.mean_direction(sd, head_cells)
    checks.append(Check(CASE, "head spread direction vs downwind (wind from 270)", direction, 90.0, "0.5 deg",
                        within(direction, 90.0, abs_=0.5), "deg"))

    burned = shape.burned_at(toa, T_SHAPE)
    sym = shape.mirror_jaccard(burned, axis_row=ir)
    checks.append(Check(CASE, "burned area at %.0f s symmetric about the wind axis (Jaccard)" % T_SHAPE, sym,
                        1.0, ">= 0.995", sym is not None and sym >= 0.995, "",
                        "a few perimeter cells of level-set noise"))
    t0 = 0.0  # the ignition is at t = 0 (ELMFIRE writes -1 for the ignited cell itself)
    h_cells = shape.extent_along(burned, ic, ir, 1, 0)
    b_cells = shape.extent_along(burned, ic, ir, -1, 0)
    length = (h_cells + b_cells + 1) * grid.cell
    rows_burned = {}
    for c, r in burned:
        rows_burned.setdefault(c, []).append(r)
    width = max((max(v) - min(v) + 1) for v in rows_burned.values()) * grid.cell if rows_burned else None
    lb_meas = length / width if width else None
    checks.append(Check(CASE, "L/B of the burned area at %.0f s vs Anderson L/B" % T_SHAPE, lb_meas,
                        exp["lb"], "3 %", within(lb_meas, exp["lb"], rel=0.03), "",
                        "length %.0f m / width %s m; Anderson with U in mi/h would give %.3f"
                        % (length, width, exp["lb_mph"])))
    head_m = (h_cells + 0.5) * grid.cell
    back_m = (b_cells + 0.5) * grid.cell
    fitted = shape.ellipse_cells(grid, ic, ir, 90.0, head_m, back_m, lb_meas)
    jf = shape.jaccard(burned, fitted)
    checks.append(Check(CASE, "burned area at %.0f s vs the ellipse of its own extents (Jaccard)"
                        % T_SHAPE, jf, 1.0, ">= 0.98", jf >= 0.98, "",
                        "head %.0f m, back %.0f m, L/B %.3f, ignition at the rear focus" % (head_m, back_m, lb_meas)))
    elapsed = (T_SHAPE - t0) / 60.0
    ideal = shape.ellipse_cells(grid, ic, ir, 90.0, exp["ros"] * elapsed, exp["back"] * elapsed, exp["lb"])
    jac = shape.jaccard(burned, ideal)
    checks.append(Check(CASE, "burned area at %.0f s vs the predicted ellipse (Jaccard)"
                        % T_SHAPE, jac, 1.0, ">= 0.95", jac >= 0.95, "",
                        "head %.0f m, back %.0f m after %.1f min" % (exp["ros"] * elapsed, exp["back"] * elapsed,
                                                                    elapsed)))

    # --- the evacuation and the boundary ---------------------------------------------------------------------
    arrivals = read_arrivals(os.path.join(folder, "_output", "flat_wind_0_arrivalData.csv"))
    drive = roads.sumo_travel_time([(900.0, ROAD_KMH / 3.6)])
    expected_arrival = RESPONSE_S + drive + 1.0
    checks.append(Check(CASE, "the car's arrival vs response + road length / limit", arrivals[-1]
                        if arrivals else None, expected_arrival, "3 s", within(arrivals[-1] if arrivals else None,
                                                                              expected_arrival, abs_=3.0), "s"))
    rset = grep_float(log, r"RSET = ([0-9.]+) minutes")
    boundary_path = os.path.join(folder, "_output", "0_trigger_boundary.asc")
    if rset is None or not os.path.isfile(boundary_path):
        checks.append(Check.error(CASE, "k-PERIL boundary", "no boundary or RSET in " + os.path.join(folder, "preact.log")))
        return checks
    bgrid, inside = firecase.read_boundary(boundary_path)
    if not bgrid.same_grid(grid):
        checks.append(Check(CASE, "k-PERIL boundary on the fire grid", False, True, "exact", False))
    outside = inside - wui_cells
    wx0 = min(c for c, r in wui_cells)
    wx1 = max(c for c, r in wui_cells)
    cell = grid.cell

    up = shape.extent_along(outside, wx0, ir, -1, 0)
    up_exp = math.floor(rset * head / cell + 1e-9)
    checks.append(Check(CASE, "k-PERIL boundary upwind extent vs RSET x head ROS", up * cell, up_exp * cell,
                        "1 cell", abs(up - up_exp) <= 1, "m", "RSET %.2f min (the car's arrival)" % rset))
    wc = shape.centroid(grid, wui_cells)
    bc = shape.centroid(grid, outside)
    brg = shape.bearing(wc, bc) if bc else None
    checks.append(Check(CASE, "k-PERIL boundary upwind: bearing WUI -> boundary centroid", brg, 270.0,
                        "5 deg", brg is not None and abs(brg - 270.0) <= 5.0, "deg",
                        "boundary centroid %.0f m from the WUI centroid" % math.hypot(bc[0] - wc[0], bc[1] - wc[1])
                        if bc else ""))
    msym = shape.mirror_jaccard(outside, axis_row=ir)
    checks.append(Check(CASE, "k-PERIL boundary symmetric about the wind axis (Jaccard)", msym, 1.0, ">= 0.99",
                        msym is not None and msym >= 0.99))

    # The engine's boundary against a re-computation of k-PERIL's documented algorithm from the fire's rasters.
    case_inputs = os.path.join(case, "inputs")
    wd = ctx.read(os.path.join(case_inputs, "wd.tif"), scratch)
    mph = rasters.Grid(mf.ncols, mf.nrows, mf.xll, mf.yll, mf.cell,
                       [[(v / rothermel.MPH_TO_FTMIN) if v is not None and v >= 0 else 0.0 for v in row]
                        for row in mf.values])
    slp = ctx.read(os.path.join(case_inputs, "slp.tif"), scratch)
    asp = ctx.read(os.path.join(case_inputs, "asp.tif"), scratch)
    ref = kperil_ref.boundary(vs, sd, mph, wd, slp, asp, wui_cells, rset, cell)
    jr = shape.jaccard(outside, ref)
    checks.append(Check(CASE, "k-PERIL boundary vs its algorithm re-computed (Jaccard)",
                        jr, 1.0, ">= 0.99 (Jaccard)", jr >= 0.99, "",
                        "%d cells (engine) vs %d (reference)" % (len(outside), len(ref))))
    # D2: the same algorithm with the ellipse the fire itself spread with (Anderson's L/B with U in m/s).
    mps = rasters.Grid(mph.ncols, mph.nrows, mph.xll, mph.yll, mph.cell,
                       [[v * rothermel.MPH_TO_MS for v in row] for row in mph.values])
    consistent = kperil_ref.boundary(vs, sd, mps, wd, slp, asp, wui_cells, rset, cell)
    jd = shape.jaccard(outside, consistent)
    down = shape.extent_along(outside, wx1, ir, 1, 0)
    down_c = shape.extent_along(consistent, wx1, ir, 1, 0)
    u_mph = umf / rothermel.MPH_TO_FTMIN
    checks.append(Check(CASE, "k-PERIL boundary vs the same with the fire's own L/B (Jaccard)", jd, 1.0,
                        ">= 0.99", jd >= 0.99, "",
                        "k-PERIL L/B %.3f at %.2f mi/h midflame, the fire's %.3f; %d vs %d cells; downwind %d vs %d cells "
                        "(the fire backs %.1f cells in RSET)"
                        % (rothermel.anderson_lb(u_mph, "mi/h"), u_mph, exp["lb"], len(outside), len(consistent),
                           down, down_c, rset * back / cell), known="D2"))

    checks += fuel_type_checks(ctx, folder)
    checks.append(gr2_check(ctx, folder))
    return checks


def fuel_type_checks(ctx, folder):
    acres = {}
    for dtype in ("int16", "int32"):
        sub = os.path.join(folder, "fuel_" + dtype)
        wui = os.path.join(sub, "fuel.wui")
        code, _, _ = ctx.build_case(sub, wui, ["--dem", os.path.join(sub, "dem.tif")]
                                    + firecase.uniform_weather_args(WIND_MS, WIND_FROM, M1, M10, M100),
                                    os.path.join(sub, "build-case.log"))
        if code != 0:
            return [Check.error(CASE, "1b build-case (%s fuel)" % dtype, "see " + os.path.join(sub, "build-case.log"))]
        ctx.preact(wui, sub, os.path.join(sub, "preact.log"))
        acres[dtype] = elmfire_acres(os.path.join(sub, "case"))
    ok = acres["int16"] is not None and acres["int16"] > 0
    out = [Check(CASE, "1b fire on an Int16 fuel raster burns", acres["int16"], None, "> 0 ac", ok, "ac")]
    stored = tiff.sample_type(os.path.join(folder, "fuel_int32", "case", "inputs", "fbfm13.tif"))
    out.append(Check(CASE, "1b an Int32 fuel source is stored in the case as Int16", stored, "int16", "exact",
                     stored == "int16", "", "ELMFIRE reads the fuel model only as 16-bit integers"))
    same = acres["int32"] is not None and acres["int16"] and abs(acres["int32"] - acres["int16"]) <= 0.001 * acres["int16"]
    out.append(Check(CASE, "1b the same fire on an Int32 fuel raster (LFPS's type): same area",
                     acres["int32"], acres["int16"], "0.1 %", bool(same), "ac",
                     "" if same else "the case's fbfm40.tif is %s" % stored))
    return out


def read_arrivals(path):
    if not os.path.isfile(path):
        return []
    with open(path) as f:
        return sorted(float(line.split(",")[0]) for line in f if line.strip() and line.strip()[0].isdigit())
