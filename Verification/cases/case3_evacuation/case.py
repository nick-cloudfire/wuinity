"""Case 3 - the evacuation: (a) cars on a corridor of known lengths and speed limits, no fire; (b) households that
leave when a synthetic fire front comes within the reaction distance; (c) a scenario whose one evacuation group has
no area. See README.md beside this file."""

import csv
import math
import os
import re

from vlib import firecase, geo, roads, scenario, tiff
from vlib.report import Check, within

CASE = "3"
ZONE = firecase.ZONE
X0, Y0 = firecase.ORIGIN
RESPONSE_S = 600.0
WALK_M = 100.0               # home to car, at exactly 1 m/s
SPEEDS_KMH = [30, 50, 90]    # rising along the corridor, so no car ever brakes for a lower limit
NODE_X = [200.0, 1200.0, 2200.0, 3700.0]   # corridor nodes, metres east of the domain corner
ROAD_Y = 500.0
LATENCY_S = 1.0              # a car reaches SUMO on the step after its household reaches it

# 3b: a straight front moving east at 1 m/s from FRONT_X0, stopping at FRONT_X1.
FRONT_X0, FRONT_X1, FRONT_V = 500.0, 2500.0, 1.0
REACTION_M = 500.0
UPDATE_S = 60.0
HOMES_X = [1400.0, 2000.0, 2400.0, 3800.0]  # the last one is beyond the fire's reach
OWN_RESPONSE_S = 3600.0
CELL = 10.0


def ll(x, y):
    return geo.to_latlon(X0 + x, Y0 + y, ZONE)


def corridor_sections(name, with_mask, exit_x=NODE_X[-1] - 5.0):
    group = [("Name", "all"), ("ResponseCurves", "fixed"), ("Destinations", "exit"), ("Demographics", "single")]
    if with_mask:
        group.append(("MaskFile", "group_all.asc"))
    return [
        ("Simulation", [("Name", name), ("LowerLeftLatLon", scenario.latlon(ll(0, 0))), ("DomainSize", "4000,1000"),
                        ("DeltaTime", "1"), ("StartDateTime", "2026-07-15T12:00:00"),
                        ("EndDateTime", "2026-07-15T13:00:00"), ("StopWhenEvacuated", "true"), ("RandomSeed", "7")]),
        ("Population", [("PopulationFile", "population.csv")]),
        ("Demographics", [("Name", "single"), ("AllowMoreThanOneCar", "false")]),
        ("Destination", [("Name", "exit"), ("LatLon", scenario.latlon(ll(exit_x, ROAD_Y)))]),
        ("ResponseCurve", [("Name", "fixed"), ("TimeInput", "Relative"), "%g,0" % RESPONSE_S, "%g,1" % RESPONSE_S]),
        ("EvacuationGroup", group),
        ("PedestrianModule", [("Enabled", "true"), ("Module", "MacroHouseholdSim")]),
        ("MacroHouseholdSim", [("WalkingSpeedMinMax", "1.0,1.0"), ("ReactToFire", "false")]),
        ("TrafficModule", [("Enabled", "true"), ("Module", "SUMO")]),
        ("SUMO", [("ConfigurationFile", "sumo/network.sumocfg")]),
        ("WildfireModule", [("Enabled", "false")]),
    ]


def generate(ctx, folder):
    # (a) and (c): the corridor
    net = roads.Network(ZONE)
    ids = [net.node(i + 1, X0 + x, Y0 + ROAD_Y) for i, x in enumerate(NODE_X)]
    for i, v in enumerate(SPEEDS_KMH):
        net.way(100 + i, [ids[i], ids[i + 1]], maxspeed_kmh=v)
    # side streets, so the corridor's inner nodes are junctions and netconvert keeps the network
    net.node(5, X0 + NODE_X[1], Y0 + ROAD_Y - 300.0)
    net.node(6, X0 + NODE_X[2], Y0 + ROAD_Y - 300.0)
    net.way(200, [5, ids[1]], highway="residential", maxspeed_kmh=30, oneway=False)
    net.way(201, [6, ids[2]], highway="residential", maxspeed_kmh=30, oneway=False)
    households = []
    for i in range(3):
        xm = 0.5 * (NODE_X[i] + NODE_X[i + 1])
        households.append((ll(xm, ROAD_Y - 5.0 - WALK_M), ll(xm, ROAD_Y - 5.0), 1))
    for sub, mask in (("a_corridor", True), ("c_no_group_area", False), ("e_unroutable", True)):
        d = os.path.join(folder, sub)
        os.makedirs(os.path.join(d, "sumo"))
        net.write_osm(os.path.join(d, "sumo", "roads.osm"))
        roads.netconvert(ctx.netconvert, os.path.join(d, "sumo", "roads.osm"), os.path.join(d, "sumo", "roads.net.xml"),
                         ZONE, os.path.join(d, "netconvert.log"), ctx.env)
        scenario.write_sumo_config(os.path.join(d, "sumo"), "roads.net.xml")
        if sub == "e_unroutable":
            # Every car on the corridor's second and third edges, the exit on its first: the corridor is one-way
            # east, so no car has a route.
            scenario.write_population(os.path.join(d, "population.csv"), households[1:] + households[1:])
            sections = corridor_sections("evac", mask, exit_x=NODE_X[0] + 5.0)
        else:
            scenario.write_population(os.path.join(d, "population.csv"), households)
            sections = corridor_sections("evac", mask)
        if mask:
            scenario.write_group_mask(os.path.join(d, "group_all.asc"), 0.0, 0.0, 4000.0, 1000.0, 100.0)
        scenario.write_wui(os.path.join(d, "evac.wui"), sections)
    with open(os.path.join(folder, "corridor_lengths.csv"), "w") as f:
        f.write("edge,length_m,speed_kmh\n")
        for i, v in enumerate(SPEEDS_KMH):
            f.write("%d,%.3f,%d\n" % (100 + i, net.length(ids[i], ids[i + 1]), v))

    # (b): the synthetic front
    d = os.path.join(folder, "b_fire_reaction")
    os.makedirs(d)
    ncols, nrows = int(4000 / CELL), int(1000 / CELL)
    toa, ros, sd = [], [], []
    for r in range(nrows):
        trow, rrow, drow = [], [], []
        for c in range(ncols):
            x = (c + 0.5) * CELL
            if x <= FRONT_X1:
                trow.append(max(0.0, (x - FRONT_X0) / FRONT_V))
                rrow.append(FRONT_V * 60.0)
                drow.append(90.0)
            else:
                trow.append(-9999.0)
                rrow.append(-9999.0)
                drow.append(-9999.0)
        toa.append(trow)
        ros.append(rrow)
        sd.append(drow)
    for name, rows in (("toa", toa), ("ros", ros), ("sd", sd)):
        tiff.write_geotiff(os.path.join(d, name + ".tif"), rows, X0, Y0 + 1000.0, CELL, firecase.EPSG, "float32", -9999)
    homes = []
    for x in HOMES_X:
        cx = (math.floor(x / CELL) + 0.5) * CELL
        p = ll(cx, 505.0)
        homes.append((p, p, 1))
    scenario.write_population(os.path.join(d, "population.csv"), homes)
    scenario.write_group_mask(os.path.join(d, "group_all.asc"), 0.0, 0.0, 4000.0, 1000.0, 100.0)
    reaction = reaction_sections(True)
    scenario.write_wui(os.path.join(d, "reaction.wui"), reaction)
    # (d): the same scenario without a [Demographics] section
    d4 = os.path.join(folder, "d_no_demographics")
    os.makedirs(d4)
    for name in ("toa.tif", "ros.tif", "sd.tif", "population.csv", "group_all.asc"):
        with open(os.path.join(d, name), "rb") as src, open(os.path.join(d4, name), "wb") as dst:
            dst.write(src.read())
    scenario.write_wui(os.path.join(d4, "reaction.wui"), reaction_sections(False))


def reaction_sections(with_demographics):
    group = [("Name", "all"), ("ResponseCurves", "late"), ("MaskFile", "group_all.asc")]
    demographics = []
    if with_demographics:
        group.append(("Demographics", "single"))
        demographics = [("Demographics", [("Name", "single"), ("AllowMoreThanOneCar", "false")])]
    return [
        ("Simulation", [("Name", "reaction"), ("LowerLeftLatLon", scenario.latlon(ll(0, 0))), ("DomainSize", "4000,1000"),
                        ("DeltaTime", "1"), ("StartDateTime", "2026-07-15T12:00:00"),
                        ("EndDateTime", "2026-07-15T14:00:00"), ("StopWhenEvacuated", "true"), ("RandomSeed", "7")]),
        ("Population", [("PopulationFile", "population.csv")]),
        ("ResponseCurve", [("Name", "late"), ("TimeInput", "Relative"), "%g,0" % OWN_RESPONSE_S,
                           "%g,1" % OWN_RESPONSE_S]),
    ] + demographics + [
        ("EvacuationGroup", group),
        ("PedestrianModule", [("Enabled", "true"), ("Module", "MacroHouseholdSim")]),
        ("MacroHouseholdSim", [("WalkingSpeedMinMax", "1.0,1.0"), ("ReactToFire", "true"),
                               ("FireReactionDistance", "%g" % REACTION_M), ("FireReactionUpdateInterval", "%g" % UPDATE_S)]),
        ("TrafficModule", [("Enabled", "false")]),
        ("WildfireModule", [("Enabled", "true"), ("Module", "AscImport")]),
        ("AscImport", [("StartDateTime", "2026-07-15T12:00:00"), ("TimeOfArrivalFile", "toa.tif"),
                       ("TimeOfArrivalUnits", "Seconds"), ("RateOfSpreadFile", "ros.tif"),
                       ("SpreadDirectionFile", "sd.tif")]),
    ]


def read_series(path, column):
    """[(time, value)] of one column of a PREACT time-series CSV."""
    with open(path) as f:
        rows = list(csv.reader(f))
    header = [h.strip() for h in rows[0]]
    i = header.index(column)
    return [(float(r[0]), float(r[i])) for r in rows[1:] if r]


def times_reaching(series):
    """For a non-decreasing count, the time at which it first reached 1, 2, 3, ..."""
    out = []
    for t, v in series:
        while len(out) < int(v):
            out.append(t)
    return out


def run(ctx, folder):
    checks = []
    with open(os.path.join(folder, "corridor_lengths.csv")) as f:
        edges = [(float(r["length_m"]), int(r["speed_kmh"]) / 3.6) for r in csv.DictReader(f)]

    # (a)
    d = os.path.join(folder, "a_corridor")
    code, log, _ = ctx.preact(os.path.join(d, "evac.wui"), d, os.path.join(d, "preact.log"))
    checks.append(Check(CASE, "3a PREACT run exits 0", code, 0, "exact", code == 0, "",
                        "" if code == 0 else "see %s; %s" % (os.path.join(d, "preact.log"), ctx.preact_note)))
    ped = os.path.join(d, "_output", "evac_pedestrian_output_0.csv")
    if os.path.isfile(ped):
        responded = times_reaching(read_series(ped, "Total households responded"))
        reached = times_reaching(read_series(ped, "Total households reached car"))
        t_resp = responded[-1] if len(responded) == 3 else None
        checks.append(Check(CASE, "3a households respond at the response curve's time", t_resp, RESPONSE_S, "1 s",
                            within(t_resp, RESPONSE_S, abs_=1.0), "s", "curve: all at %g s after the order" % RESPONSE_S))
        t_car = reached[-1] if len(reached) == 3 else None
        checks.append(Check(CASE, "3a households reach their car (100 m walk at 1 m/s)", t_car,
                            RESPONSE_S + WALK_M, "1 s", within(t_car, RESPONSE_S + WALK_M, abs_=1.0), "s"))
    arrivals = []
    path = os.path.join(d, "_output", "evac_0_arrivalData.csv")
    if os.path.isfile(path):
        with open(path) as f:
            arrivals = sorted(float(line.split(",")[0]) for line in f if line.strip()[:1].isdigit())
    checks.append(Check(CASE, "3a every car arrives", len(arrivals), 3, "exact", len(arrivals) == 3, "cars"))
    # The car of the household on the last edge arrives first, then the one on the middle edge, then the first.
    for k, label in ((2, "edge 3 (1.5 km at 90 km/h)"), (1, "edges 2-3 (+1 km at 50 km/h)"),
                     (0, "edges 1-3 (+1 km at 30 km/h)")):
        expected = RESPONSE_S + WALK_M + LATENCY_S + roads.sumo_travel_time(edges[k:])
        measured = arrivals[2 - k] if len(arrivals) == 3 else None
        checks.append(Check(CASE, "3a arrival, car starting on " + label, measured, expected, "3 s",
                            within(measured, expected, abs_=3.0), "s",
                            "length / limit + acceleration at 2.6 m/s2 (SUMO's default car), node-to-node lengths"))

    # (b)
    d = os.path.join(folder, "b_fire_reaction")
    code, log, _ = ctx.preact(os.path.join(d, "reaction.wui"), d, os.path.join(d, "preact.log"))
    checks.append(Check(CASE, "3b PREACT run exits 0", code, 0, "exact", code == 0, "",
                        "" if code == 0 else "see " + os.path.join(d, "preact.log")))
    ped = os.path.join(d, "_output", "reaction_pedestrian_output_0.csv")
    responded = times_reaching(read_series(ped, "Total households responded")) if os.path.isfile(ped) else []
    for i, x in enumerate(HOMES_X):
        home = (math.floor(x / CELL) + 0.5) * CELL
        measured = responded[i] if i < len(responded) else None
        if x - REACTION_M > FRONT_X1:
            checks.append(Check(CASE, "3b household at %.1f km, out of reach, leaves on its own time"
                                % (x / 1000.0), measured, OWN_RESPONSE_S, "1 s",
                                within(measured, OWN_RESPONSE_S, abs_=1.0), "s"))
            continue
        # the first burning cell whose centre is within the reaction distance of the home's cell centre
        nearest = (math.ceil((home - REACTION_M) / CELL - 0.5) + 0.5) * CELL
        t_star = max(0.0, (nearest - FRONT_X0) / FRONT_V)
        ok = measured is not None and t_star - 1.0 <= measured <= t_star + UPDATE_S + 1.0
        checks.append(Check(CASE, "3b household at %.1f km leaves as the front comes %g m near" % (x / 1000.0, REACTION_M),
                            measured, t_star, "0 to +%g s" % (UPDATE_S + 1), ok, "s",
                            "front at %g m/s from %g m; distances are refreshed every %g s" % (FRONT_V, FRONT_X0, UPDATE_S)))
    m = re.search(r"Households that left because of the fire's proximity: (\d+)", log)
    n_fire = int(m.group(1)) if m else None
    n_exp = sum(1 for x in HOMES_X if x - REACTION_M <= FRONT_X1)
    checks.append(Check(CASE, "3b households reported as started by the fire", n_fire, n_exp, "exact",
                        n_fire == n_exp))

    # (c)
    d = os.path.join(folder, "c_no_group_area")
    code, log, _ = ctx.preact(os.path.join(d, "evac.wui"), d, os.path.join(d, "preact.log"))
    n = 0
    path = os.path.join(d, "_output", "evac_0_arrivalData.csv")
    if os.path.isfile(path):
        with open(path) as f:
            n = sum(1 for line in f if line.strip()[:1].isdigit())
    checks.append(Check(CASE, "3c a group without MaskFile/ShapeFile runs (checked as a warning)",
                        n, 3, "3 cars, exit 0", code == 0 and n == 3, "cars",
                        "exit %d%s" % (code, "; NullReferenceException in EvacuationGroup.CreateShapeFilePolygon"
                                       if "CreateShapeFilePolygon" in log else ""), known="D3"))

    # (d)
    d = os.path.join(folder, "d_no_demographics")
    code, log, _ = ctx.preact(os.path.join(d, "reaction.wui"), d, os.path.join(d, "preact.log"))
    checks.append(Check(CASE, "3d a group naming no [Demographics] runs (default promised)", code, 0,
                        "exit 0", code == 0, "", "NullReferenceException in the EvacuationGroup constructor"
                        if "NullReferenceException" in log else "", known="D4"))

    # (e)
    d = os.path.join(folder, "e_unroutable")
    code, log, _ = ctx.preact(os.path.join(d, "evac.wui"), d, os.path.join(d, "preact.log"))
    unrouted = log.count("no valid route")
    checks.append(Check(CASE, "3e a run in which no car can reach SUMO fails (exit 2)", code, 2, "exit 2", code == 2, "",
                        "%d of 4 cars had no route; SUMOModule only stops a run for this after 25 cars" % unrouted,
                        known="D5"))
    return checks
