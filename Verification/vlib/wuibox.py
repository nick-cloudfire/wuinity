"""The evacuation side of the fire cases: a WUI box, households in it, a road out of it to the north, and the
trigger boundary switched on. k-PERIL needs an evacuation - its required safe egress time is the last car's
arrival - so every case with a boundary has one."""

import os

from . import firecase, roads, scenario

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
    # The group's area is the WUI box, in simulation coordinates (metres from the domain's south-west corner).
    scenario.write_group_mask(os.path.join(folder, "group_wui.asc"), bx0 - fc.x0, by0 - 5.0 - fc.y0, bx1 - bx0,
                              by1 - by0 + 10.0, 10.0)
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
        ("kPERIL", [("WuiAreaSource", "Raster"), ("WuiAreaFile", "case/inputs/wui_area.tif")]
         + ([("OutputName", output_name)] if output_name else [])),
    ]
    return sections, [(road_m, ROAD_KMH / 3.6)]
