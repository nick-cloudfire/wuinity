"""Synthetic road networks: an OpenStreetMap file with known node positions and speed limits, turned into a SUMO
network by netconvert with the same options the GUI's Roads step (SumoNetworkBuilder) uses."""

import os
import subprocess

from . import geo

# SumoNetworkBuilder.Build's options, in its order (PREACT/PREACTcore/Source/Utility/SumoNetworkBuilder.cs).
NETCONVERT_OPTIONS = ["--geometry.remove", "--roundabouts.guess", "--ramps.guess", "--junctions.join",
                      "--tls.guess-signals", "--tls.discard-simple", "--tls.join",
                      "--keep-edges.by-vclass", "passenger", "--remove-edges.isolated"]


class Network(object):
    def __init__(self, zone):
        self.zone = zone
        self.nodes = {}   # id -> (x, y) UTM
        self.ways = []    # (id, [node ids], tags)

    def node(self, nid, x, y):
        self.nodes[nid] = (x, y)
        return nid

    def way(self, wid, node_ids, highway="primary", maxspeed_kmh=50, oneway=True, lanes=1):
        tags = {"highway": highway, "maxspeed": str(int(maxspeed_kmh)), "lanes": str(lanes)}
        if oneway:
            tags["oneway"] = "yes"
        self.ways.append((wid, list(node_ids), tags))

    def length(self, a, b):
        (xa, ya), (xb, yb) = self.nodes[a], self.nodes[b]
        return ((xb - xa) ** 2 + (yb - ya) ** 2) ** 0.5

    def write_osm(self, path):
        out = ['<?xml version="1.0" encoding="UTF-8"?>', '<osm version="0.6" generator="WUInity verification">']
        for nid in sorted(self.nodes):
            lat, lon = geo.to_latlon(self.nodes[nid][0], self.nodes[nid][1], self.zone)
            out.append('  <node id="%d" lat="%.9f" lon="%.9f" version="1"/>' % (nid, lat, lon))
        for wid, refs, tags in self.ways:
            out.append('  <way id="%d" version="1">' % wid)
            for r in refs:
                out.append('    <nd ref="%d"/>' % r)
            for k in sorted(tags):
                out.append('    <tag k="%s" v="%s"/>' % (k, tags[k]))
            out.append('  </way>')
        out.append('</osm>')
        with open(path, "w") as f:
            f.write("\n".join(out) + "\n")


def netconvert(netconvert_exe, osm_path, net_path, zone, log_path, env):
    proj = "+proj=utm +zone=%d +ellps=WGS84 +datum=WGS84 +units=m +no_defs" % zone
    args = [netconvert_exe, "--proj", proj, "--osm-files", os.path.abspath(osm_path), "-o",
            os.path.abspath(net_path)] + NETCONVERT_OPTIONS
    with open(log_path, "w") as log:
        log.write("$ " + " ".join(args) + "\n")
        log.flush()
        code = subprocess.call(args, cwd=os.path.dirname(os.path.abspath(net_path)), stdout=log,
                               stderr=subprocess.STDOUT, env=env)
    if code != 0 or not os.path.isfile(net_path):
        raise RuntimeError("netconvert failed (exit %d); see %s" % (code, log_path))


def sumo_travel_time(segments, accel=2.6):
    """Travel time of a car starting at rest at the start of the first segment and driving to the end of the last,
    each segment (length m, speed limit m/s) driven at its limit, accelerating at `accel` (SUMO's default passenger
    car, 2.6 m/s2) at the start and wherever the limit rises. Speeds must not fall along the route (no braking
    term). Point-mass kinematics: time lost accelerating from v1 to v2 is (v2 - v1)^2 / (2 a v2)."""
    t = 0.0
    v_prev = 0.0
    for length, v in segments:
        if v < v_prev:
            raise ValueError("speed limits must not fall along the route")
        t += length / v + (v - v_prev) ** 2 / (2.0 * accel * v)
        v_prev = v
    return t
