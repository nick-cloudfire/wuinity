"""The evacuation side of the fire cases: a WUI box, households in it, a road out of it to the north, and the
trigger boundary switched on. k-PERIL needs an evacuation - its required safe egress time is the last car's
arrival - so every case with a boundary has one."""

import math
import os

from . import firecase, roads, scenario
from .report import Check

ROAD_KMH = 50


def write(ctx, folder, fc, box, response_s, households=1, road_m=900.0, output_name="trigger_boundary.asc"):
    """Writes sumo/, population.csv and group_wui.asc into `folder` and returns the scenario sections. `box` is
    (x0, x1, y0, y1) in UTM metres; the road starts at the box's centre and runs `road_m` north at 50 km/h (in two
    edges, with a side street at the middle node so netconvert keeps it), every household's car starting at its
    first node. Returns (sections, [(length m, speed m/s)] of the drive)."""
    zone = firecase.ZONE
    net = roads.Network(zone)
    bx0, bx1, by0, by1 = box
    ax, ay = 0.5 * (bx0 + bx1), 0.5 * (by0 + by1)
    a = net.node(1, ax, ay)
    m = net.node(2, ax, ay + 0.5 * road_m)
    b = net.node(3, ax, ay + road_m)
    c = net.node(4, ax + 300.0, ay + 0.5 * road_m)
    net.way(100, [a, m], maxspeed_kmh=ROAD_KMH)
    net.way(101, [m, b], maxspeed_kmh=ROAD_KMH)
    net.way(200, [m, c], highway="residential", maxspeed_kmh=30, oneway=False)
    sumo = os.path.join(folder, "sumo")
    os.makedirs(sumo)
    net.write_osm(os.path.join(sumo, "roads.osm"))
    roads.netconvert(ctx.netconvert, os.path.join(sumo, "roads.osm"), os.path.join(sumo, "roads.net.xml"), zone,
                     os.path.join(folder, "netconvert.log"), ctx.env)
    scenario.write_sumo_config(sumo, "roads.net.xml")
    home = fc.latlon(ax + 5.0, ay)
    scenario.write_population(os.path.join(folder, "population.csv"), [(home, home, 1)] * households)
    # The group's area is the WUI box, in simulation coordinates (metres from the domain's south-west corner): exactly
    # the lattice cells whose centres lie in the box, so the WUI area build-case makes from it (a case cell is in when its
    # centre is in the group) is the box's cells, whether the box's edges fall on cell edges (cases 1, 4) or on cell
    # centres (2c).
    (x_lo, x_hi), (y_lo, y_hi) = _cell_span(bx0, bx1, fc.cell), _cell_span(by0, by1, fc.cell)
    scenario.write_group_mask(os.path.join(folder, "group_wui.asc"), x_lo - fc.x0, y_lo - fc.y0, x_hi - x_lo,
                              y_hi - y_lo, fc.cell)
    sections = [
        ("Population", [("PopulationFile", "population.csv")]),
        ("Demographics", [("Name", "single"), ("AllowMoreThanOneCar", "false")]),
        ("Destination", [("Name", "north_exit"), ("LatLon", scenario.latlon(fc.latlon(ax, ay + road_m - 5.0)))]),
        ("ResponseCurve", [("Name", "fixed"), ("TimeInput", "Relative"), "%g,0" % response_s, "%g,1" % response_s]),
        ("EvacuationGroup", [("Name", "wui"), ("ResponseCurves", "fixed"), ("Destinations", "north_exit"),
                             ("Demographics", "single"), ("MaskFile", "group_wui.asc")]),
        ("PedestrianModule", [("Enabled", "true"), ("Module", "MacroHouseholdSim")]),
        ("MacroHouseholdSim", [("WalkingSpeedMinMax", "1.0,1.0"), ("ReactToFire", "false")]),
        ("TrafficModule", [("Enabled", "true"), ("Module", "SUMO")]),
        ("SUMO", [("ConfigurationFile", "sumo/network.sumocfg")]),
        ("TriggerBufferModule", [("Enabled", "true"), ("Module", "kPERIL")]),
        # The WUI area is the evacuation groups (round 2): build-case writes it as case/inputs/wui_area.tif, and k-PERIL
        # protects the group. check_wui_area() compares that file with the box.
        ("kPERIL", [("WuiAreaSource", "EvacuationGroupsCombined")]
         + ([("OutputName", output_name)] if output_name else [])),
    ]
    return sections, [(road_m, ROAD_KMH / 3.6)]


def _cell_span(lo, hi, cell):
    """The outer edges of the lattice cells (edges at multiples of `cell`) whose centres lie in [lo, hi]."""
    first = math.ceil(lo / cell - 0.5 - 1e-9)
    last = math.floor(hi / cell - 0.5 + 1e-9)
    return first * cell, (last + 1) * cell


def check_wui_area(ctx, case, case_dir, grid, box, scratch):
    """The check that build-case's wui_area.tif - the union of the evacuation groups, made from the group's mask that
    write() gives the WUI box - holds exactly the case-grid cells whose centres lie in the box. Returns (check, cells)."""
    path = os.path.join(case_dir, "inputs", "wui_area.tif")
    expected = firecase.box_cells(grid, *box)
    name = "build-case's wui_area.tif (the evacuation group) is the WUI box"
    if not os.path.isfile(path):
        return Check(case, name, None, len(expected), "exact", False, "cells",
                     "build-case wrote no inputs/wui_area.tif"), expected
    area = ctx.read(path, scratch)
    cells = set((c, r) for c, r, v in area.cells() if v is not None and v > 0.5)
    differ = len(cells ^ expected)
    return Check(case, name, differ, 0, "exact", differ == 0, "cells differ",
                 "%d cells in wui_area.tif, %d in the box" % (len(cells), len(expected))), expected
