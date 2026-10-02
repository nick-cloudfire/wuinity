"""Case 4 - a tiny trigger campaign. Every realization is made identical on purpose (one ignitable cell, a weather
archive whose every hour is the same, a response curve with one departure time), so the convergence rule has an
exact expected outcome; the campaign is then run a second time from scratch and every realization compared. See
README.md beside this file."""

import csv
import datetime
import hashlib
import os
import re

from vlib import firecase, rasters, rothermel, scenario, shape, tiff, wuibox
from vlib.report import Check

CASE = "4"
FUEL = 1
SEED = 12345
STREAK = 3
TOLERANCE = 0.02
MAX = 8
HOURS = 1
RESPONSE_S = 600.0
WEATHER = {"t": 30.0, "rh": 20.0, "wind": 6.0, "dir": 270.0}   # every hour of the synthetic ERA5 archive
YEARS = (2023, 2024)

FC = firecase.FireCase("campaign", width=2400.0, height=1400.0, cell=10.0, padding=200.0)
IGNITION = FC.centre_of(FC.x0 + 400.0, FC.y0 + 700.0)
AXIS_Y = IGNITION[1]
WUI_BOX = (FC.x0 + 1200.0, FC.x0 + 1400.0, AXIS_Y - 100.0, AXIS_Y + 100.0)


def write_archive(path):
    """An ERA5-style hourly archive in the Open-Meteo layout the engine reads, with no fire-weather columns: a
    campaign re-derives them on its own copy (ClimatologySampler.EnsureArchiveFormat)."""
    lat, lon = FC.latlon(FC.x0 + 0.5 * FC.width, FC.y0 + 0.5 * FC.height)
    t = datetime.datetime(YEARS[0], 1, 1)
    end = datetime.datetime(YEARS[1] + 1, 1, 1)
    with open(path, "w", encoding="utf-8") as f:
        f.write("Latitide,%.5f\nLongitude,%.5f\nElevation,100\n" % (lat, lon))
        f.write("Time,Temperature_2m [°C],Relativehumidity_2m [%],Precipitation [mm],Windspeed_10m [m/s],"
                "Winddirection_10m [°],Cloudcover [%],Direct_radiation [W/m²],Boundary_layer_height [m]\n")
        while t < end:
            f.write("%s,%g,%g,0,%g,%g,0,0,1000\n" % (t.strftime("%Y-%m-%dT%H:%M"), WEATHER["t"], WEATHER["rh"],
                                                   WEATHER["wind"], WEATHER["dir"]))
            t += datetime.timedelta(hours=1)


def generate(ctx, folder):
    FC.write_rasters(folder, lambda x, y: 100.0, FUEL, "int16")
    # The ignition mask as a source layer: 1 in the one cell every fire is to start from.
    north = FC.src_y0 + FC.src_rows * FC.cell
    ic = int((IGNITION[0] - FC.src_x0) // FC.cell)
    ir = FC.src_rows - 1 - int((IGNITION[1] - FC.src_y0) // FC.cell)
    rows = [[1 if (c, r) == (ic, ir) else 0 for c in range(FC.src_cols)] for r in range(FC.src_rows)]
    tiff.write_geotiff(os.path.join(folder, "ignition_mask.tif"), rows, FC.src_x0, north, FC.cell, firecase.EPSG,
                       "int16", -9999)
    write_archive(os.path.join(folder, "era5_constant.csv"))

    evac, _ = wuibox.write(ctx, folder, FC, WUI_BOX, RESPONSE_S, households=2, road_m=600.0, output_name=None)
    sections = FC.sections(HOURS, "fuel_int16.tif", None, extra=evac, elmfire_exe=ctx.elmfire,
                           end="2026-07-15T13:00:00")
    for header, entries in sections:
        if header == "ELMFIRE":
            entries.append(("IgnitionMaskFile", "ignition_mask.tif"))
    scenario.write_wui(os.path.join(folder, "campaign.wui"), sections)


def campaign_args(ctx, folder):
    return ["converge-trigger", "--wui", os.path.join(folder, "campaign.wui"), "--max", str(MAX), "--hours", str(HOURS),
            "--seed", str(SEED), "--streak", str(STREAK), "--tolerance", str(TOLERANCE), "--parallel", "1",
            "--allow-uniform-weather", "--no-live-fuel-moisture", "--weather-archive",
            os.path.join(folder, "era5_constant.csv"), "--climatology-from", str(YEARS[0]), "--climatology-to",
            str(YEARS[1]), "--preact", ctx.preact_exe(), "--elmfire", ctx.elmfire, "--gdal", ctx.gdal_bin]


def simard_emc(t_c, rh):
    """Equilibrium moisture content, % (Simard 1968, in the NFDRS form of Bradshaw et al. 1984), T in deg F."""
    t_f = t_c * 9.0 / 5.0 + 32.0
    if rh < 10.0:
        return 0.03229 + 0.281073 * rh - 0.000578 * rh * t_f
    if rh < 50.0:
        return 2.22749 + 0.160107 * rh - 0.01478 * t_f
    return 21.0606 + 0.005565 * rh ** 2 - 0.00035 * rh * t_f - 0.483199 * rh


def md5(path):
    with open(path, "rb") as f:
        return hashlib.md5(f.read()).hexdigest()


def campaign_folder(folder):
    out = os.path.join(folder, "_output")
    live = [d for d in os.listdir(out) if d.startswith("campaign_") and "_replaced_" not in d
            and not d.endswith("_first")]
    return os.path.join(out, live[0]) if len(live) == 1 else None


def read_csv(path):
    with open(path) as f:
        return list(csv.DictReader(f))


def realization_files(camp):
    """{id: {"toa": path, "boundary": path, "record": {key: value}}} of a campaign folder."""
    out = {}
    base = os.path.join(camp, "realizations")
    for rid in sorted(os.listdir(base)):
        rec = {}
        with open(os.path.join(base, rid, "realization.txt")) as f:
            for line in f:
                if "=" in line and not line.startswith("#"):
                    k, v = line.rstrip("\n").split("=", 1)
                    rec[k] = v
        out[rid] = {"record": rec,
                    "toa": os.path.join(base, rid, rec.get("toa", "")) if rec.get("toa") else None,
                    "boundary": os.path.join(base, rid, rec.get("boundary", "")) if rec.get("boundary") else None,
                    "dir": os.path.join(base, rid)}
    return out


def reaggregate(boundaries, cell_area, tolerance):
    """The documented convergence rule (docs/trigger-campaigns.md#convergence) applied to boundary sets in order:
    per-decile areas of P >= tau and the streak after each."""
    deciles = [0.1 * k for k in range(1, 11)]
    counts = {}
    previous = [None] * 10
    streak = 0
    rows = []
    for n, inside in enumerate(boundaries, start=1):
        for cell_ in inside:
            counts[cell_] = counts.get(cell_, 0) + 1
        areas = []
        within_all, compared = True, False
        for t, tau in enumerate(deciles):
            a = sum(1 for v in counts.values() if float(v) / n >= tau - 1e-12) * cell_area
            if previous[t]:
                compared = True
                if abs(a - previous[t]) / previous[t] >= tolerance:
                    within_all = False
            previous[t] = a
            areas.append(a)
        if compared:
            streak = streak + 1 if within_all else 0
        rows.append((n, areas, streak))
    return rows


def run(ctx, folder):
    checks = []
    wui = os.path.join(folder, "campaign.wui")
    code, _, _ = ctx.build_case(folder, wui, ["--dem", os.path.join(folder, "dem.tif")]
                                + firecase.uniform_weather_args(WEATHER["wind"], WEATHER["dir"], 6, 7, 8),
                                os.path.join(folder, "build-case.log"))
    if code != 0:
        return [Check.error(CASE, "build-case", "see " + os.path.join(folder, "build-case.log"))]
    case = os.path.join(folder, "case")
    grid = firecase.case_grid(ctx, case)
    wui_cells = firecase.box_cells(grid, *WUI_BOX)
    firecase.write_mask_on_grid(os.path.join(case, "inputs", "wui_area.tif"), grid, wui_cells)

    runs = []
    for k in (1, 2):
        code, out, seconds = ctx.cli(campaign_args(ctx, folder), folder, os.path.join(folder, "campaign%d.log" % k),
                                     timeout=3600)
        camp = campaign_folder(folder)
        if code != 0 or camp is None:
            checks.append(Check(CASE, "campaign run %d exits 0" % k, code, 0, "exact", False, "",
                                "see %s; %s" % (os.path.join(folder, "campaign%d.log" % k), ctx.preact_note)))
            return checks
        # Keep the first run's folder under its own name: the second run moves it aside as *_replaced_<time>.
        runs.append((out, seconds))
        if k == 1:
            first = camp + "_first"
            os.rename(camp, first)
    camp2 = campaign_folder(folder)
    first_out, second_out = runs[0][0], runs[1][0]

    m = re.search(r"Converged after (\d+) boundaries", first_out)
    n_conv = int(m.group(1)) if m else None
    checks.append(Check(CASE, "the campaign converges, after streak + 1 boundaries", n_conv, STREAK + 1, "exact",
                        n_conv == STREAK + 1, "boundaries",
                        "--streak %d --tolerance %g --max %d; one ignitable cell and constant weather, so the boundaries "
                        "differ by numerical noise only" % (STREAK, TOLERANCE, MAX)))

    rows = read_csv(os.path.join(first, "realizations.csv"))
    conv = read_csv(os.path.join(first, "trigger_convergence.csv"))
    files = realization_files(first)
    deltas = [float(r[k]) for r in conv for k in r if k.startswith("delta_") and r[k] not in ("", None)]
    checks.append(Check(CASE, "premise: largest decile-area change between boundaries", max(deltas)
                        if deltas else None, 0.0, "< %g" % TOLERANCE, bool(deltas) and max(deltas) < TOLERANCE))

    # The documented rule applied independently to the boundaries the campaign kept, in the order it folded them.
    order = [r["realization_id"] for r in conv]
    boundaries = [firecase.read_boundary(files[rid]["boundary"])[1] for rid in order]
    ref = reaggregate(boundaries, grid.cell * grid.cell, TOLERANCE)
    worst = 0.0
    streak_ok = True
    for r, (n, areas, streak) in zip(conv, ref):
        for t in range(10):
            worst = max(worst, abs(float(r["area_p%d" % (10 * (t + 1))]) - areas[t]))
        streak_ok = streak_ok and int(r["streak"]) == streak
    checks.append(Check(CASE, "convergence CSV decile areas vs re-aggregated boundaries",
                        worst, 0.0, "exact (m2)", worst == 0.0 and len(conv) == len(ref), "m2",
                        "max |difference| over %d rows x 10 deciles" % len(conv)))
    checks.append(Check(CASE, "convergence CSV streak sequence vs the rule", " ".join(r["streak"] for r in conv),
                        " ".join(str(s) for _, _, s in ref), "exact", streak_ok))
    prob = rasters.read_asc(os.path.join(first, "trigger_probability.asc"))
    n_ok = len(boundaries)
    counts = {}
    for b in boundaries:
        for cell_ in b:
            counts[cell_] = counts.get(cell_, 0) + 1
    perr = 0.0
    for c, r, v in prob.cells():
        perr = max(perr, abs((v or 0.0) - counts.get((c, r), 0) / float(n_ok)))
    checks.append(Check(CASE, "trigger_probability.asc vs fraction of boundaries per cell", perr, 0.0,
                        "1e-6", perr <= 1e-6))

    # Seeds and draws, per realization.
    seed_ok, evac_ok, draw_ok, ign_ok = True, True, True, True
    emc = simard_emc(WEATHER["t"], WEATHER["rh"])
    wui_c = shape.centroid(grid, wui_cells)
    aim = shape.bearing(wui_c, IGNITION)
    for r in rows:
        i = int(r["realization"])
        run_data = os.path.join(files[r["realization"]]["dir"], "outputs", "run.data")
        with open(run_data) as f:
            m = re.search(r"^\s*SEED\s*=\s*(\d+)", f.read(), re.M)
        seed_ok = seed_ok and m is not None and int(m.group(1)) == SEED + i
        with open(os.path.join(files[r["realization"]]["dir"], "preact_scenario.wui")) as f:
            m = re.search(r"^RandomSeed=(\d+)", f.read(), re.M)
        evac_ok = evac_ok and int(r["evacuation_seed"]) == SEED + 2000000 + i and m is not None \
            and int(m.group(1)) == SEED + 2000000 + i
        ign_ok = ign_ok and abs(float(r["ignition_x"]) - IGNITION[0]) < 0.5 and abs(float(r["ignition_y"]) - IGNITION[1]) < 0.5
        draw_ok = draw_ok and abs(float(r["dead_1h_pct"]) - emc) < 0.01 \
            and abs(float(r["mean_wind_10m_mph"]) - WEATHER["wind"] / 0.44704) < 0.01 \
            and abs(float(r["wind_from_deg"]) - aim) < 0.5
    checks.append(Check(CASE, "ELMFIRE SEED of realization i = seed + i (run.data)", seed_ok, True, "every realization",
                        seed_ok))
    checks.append(Check(CASE, "evacuation seed of realization i = seed + 2 000 000 + i", evac_ok, True,
                        "every realization", evac_ok, "", "realizations.csv and preact_scenario.wui [Simulation] RandomSeed"))
    checks.append(Check(CASE, "every ignition is the one cell of the ignition mask", ign_ok, True, "0.5 m", ign_ok))
    checks.append(Check(CASE, "drawn weather: archive wind, aimed at WUI, Simard EMC 1-h", draw_ok, True,
                        "every realization", draw_ok, "",
                        "%.1f mi/h from %.1f deg, 1-h %.4f %% (%.0f C, %.0f %% RH)" % (WEATHER["wind"] / 0.44704, aim,
                                                                                    emc, WEATHER["t"], WEATHER["rh"])))
    fm = rothermel.read_fuel_models(ctx.fuel_table)[FUEL]
    umf = 0.87 * WEATHER["wind"] / 0.44704 * rothermel.unsheltered_waf(fm.depth) * rothermel.MPH_TO_FTMIN
    rf = rothermel.surface_fire(fm, emc / 100, (emc + 1) / 100, (emc + 2) / 100, 0.6, 0.9, umf)["ros"] * 0.3048
    r1 = files[rows[0]["realization"]]
    vs = ctx.read(r1["record"].get("ros") and os.path.join(r1["dir"], r1["record"]["ros"]), os.path.join(folder, "_asc"))
    ic, ir = grid.cell_of(*IGNITION)
    head = shape.mean_along(vs, [(d, c, r) for d, c, r in shape.axis_cells(vs, ic, ir, 1, 0, 60) if 200 <= d <= 500])
    checks.append(Check(CASE, "realization 1 head ROS vs Rothermel at its drawn weather", head, rf, "1 %",
                        head is not None and abs(head - rf) <= 0.01 * rf, "m/min"))

    # The same campaign from scratch: every realization again.
    files2 = realization_files(camp2)
    rows2 = {r["realization"]: r for r in read_csv(os.path.join(camp2, "realizations.csv"))}
    keys = ("status", "ignition_x", "ignition_y", "wind_from_deg", "mean_wind_10m_mph", "dead_1h_pct", "evacuation_seed",
            "fire_area_acres")
    same_rec = all(rid in rows2 and all(r[k] == rows2[rid][k] for k in keys) for rid, r in
                   ((r["realization"], r) for r in rows))
    same_toa = all(md5(files[rid]["toa"]) == md5(files2[rid]["toa"]) for rid in files if rid in files2)
    same_b = all(md5(files[rid]["boundary"]) == md5(files2[rid]["boundary"]) for rid in files if rid in files2)
    checks.append(Check(CASE, "rerun from scratch: the same realization records", same_rec, True, "every field",
                        same_rec, "", ", ".join(keys)))
    checks.append(Check(CASE, "rerun from scratch: identical arrival rasters and boundaries", same_toa and same_b,
                        True, "md5", same_toa and same_b))
    distinct = len(set(md5(files[rid]["toa"]) for rid in files))
    checks.append(Check(CASE, "distinct arrival rasters (ELMFIRE SEED, spotting off)",
                        distinct, None, "-", True, "",
                        "spotting is off; SEED still shifts some cells by one time step", info=True))
    return checks
