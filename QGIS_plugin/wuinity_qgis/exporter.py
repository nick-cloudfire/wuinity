"""
Exports WUInity layers to a .wui file + group shapefiles.
"""

import os
from qgis.core import (
    QgsProject, QgsDistanceArea, QgsPointXY,
    QgsCoordinateTransform, QgsCoordinateReferenceSystem,
    QgsVectorFileWriter, QgsFeature, QgsGeometry, QgsVectorLayer,
)
from .layers import (
    get_domain_layer, get_destinations_layer, get_groups_layer,
    get_demographics_table, get_curves_table,
)
from .compat import write_vector_layer

WGS84 = QgsCoordinateReferenceSystem("EPSG:4326")

# Default response curve: observed average (time_s, cumulative_probability)
DEFAULT_RESPONSE_CURVE = [
    (0,    0.0),
    (300,  0.037),
    (600,  0.15),
    (1080, 0.67),
    (2400, 0.93),
    (6300, 1.0),
]


class ExportError(Exception):
    pass


def export(output_dir, sim_name, delta_time, max_sim_time, stop_when_evacuated,
           module_config=None):
    """
    Main entry point. Returns the path to the written .wui file.
    Raises ExportError with a human-readable message on failure.
    """
    if module_config is None:
        module_config = {}
    domain_layer = get_domain_layer()
    dest_layer   = get_destinations_layer()
    groups_layer = get_groups_layer()

    if domain_layer is None:
        raise ExportError("WUI Domain layer not found. Run 'New WUInity Project' first.")
    if dest_layer is None:
        raise ExportError("WUI Destinations layer not found.")
    if groups_layer is None:
        raise ExportError("WUI Evacuation Groups layer not found.")

    # Commit any layers still in edit mode so all features are readable
    for lyr in (domain_layer, dest_layer, groups_layer):
        if lyr.isEditable():
            lyr.commitChanges()

    domain_feats = list(domain_layer.getFeatures())
    if not domain_feats:
        raise ExportError("WUI Domain layer has no features. Draw the simulation domain polygon first.")
    if dest_layer.featureCount() == 0:
        raise ExportError("No destinations defined. Add at least one destination point.")
    if groups_layer.featureCount() == 0:
        raise ExportError("No evacuation groups defined. Add at least one group polygon.")

    os.makedirs(output_dir, exist_ok=True)
    groups_dir = os.path.join(output_dir, "groups")
    os.makedirs(groups_dir, exist_ok=True)

    lower_left, domain_size = _domain_bounds(domain_layer, domain_feats[0])
    destinations            = _read_destinations(dest_layer)
    groups, shp_paths       = _write_group_shapefiles(groups_layer, groups_dir)

    wui_path = os.path.join(output_dir, f"{sim_name}.wui")
    _write_wui(
        wui_path, sim_name, lower_left, domain_size,
        delta_time, max_sim_time, stop_when_evacuated,
        destinations, groups, shp_paths, module_config,
    )
    return wui_path


# ---------------------------------------------------------------------------
# Domain helpers
# ---------------------------------------------------------------------------

def _domain_bounds(layer, feature):
    """Return ((lat, lon), (width_m, height_m)) for the domain feature."""
    geom = feature.geometry()

    # Transform to WGS84 if needed
    src_crs = layer.crs()
    if src_crs != WGS84:
        xform = QgsCoordinateTransform(src_crs, WGS84, QgsProject.instance())
        geom.transform(xform)

    bbox = geom.boundingBox()
    lower_left = (bbox.yMinimum(), bbox.xMinimum())

    da = QgsDistanceArea()
    da.setSourceCrs(WGS84, QgsProject.instance().transformContext())
    da.setEllipsoid("WGS84")

    sw = QgsPointXY(bbox.xMinimum(), bbox.yMinimum())
    se = QgsPointXY(bbox.xMaximum(), bbox.yMinimum())
    nw = QgsPointXY(bbox.xMinimum(), bbox.yMaximum())

    width_m  = da.measureLine(sw, se)
    height_m = da.measureLine(sw, nw)

    return lower_left, (width_m, height_m)


# ---------------------------------------------------------------------------
# Destination helpers
# ---------------------------------------------------------------------------

def _read_destinations(layer):
    dests = []
    src_crs = layer.crs()
    xform = None
    if src_crs != WGS84:
        xform = QgsCoordinateTransform(src_crs, WGS84, QgsProject.instance())

    for feat in layer.getFeatures():
        pt = feat.geometry().asPoint()
        if xform:
            pt = xform.transform(pt)
        dests.append({
            "name":         feat["name"] or f"dest_{feat.id()}",
            "lat":          pt.y(),
            "lon":          pt.x(),
            "type":         feat["type"] or "Exit",
            "max_flow":     feat["max_flow"]     if feat["max_flow"]     is not None else -1,
            "max_vehicles": feat["max_vehicles"] if feat["max_vehicles"] is not None else -1,
            "max_people":   feat["max_people"]   if feat["max_people"]   is not None else -1,
            "blocked":      bool(feat["blocked"]),
        })
    return dests


# ---------------------------------------------------------------------------
# Group shapefiles
# ---------------------------------------------------------------------------

def _write_group_shapefiles(layer, groups_dir):
    """
    Write one shapefile per evacuation group feature.
    Returns (groups list, dict of name→relative_shp_path).
    """
    groups = []
    shp_paths = {}

    src_crs = layer.crs()
    xform = None
    if src_crs != WGS84:
        xform = QgsCoordinateTransform(src_crs, WGS84, QgsProject.instance())

    for feat in layer.getFeatures():
        name = (feat["name"] or f"group_{feat.id()}").strip()
        safe_name = _safe_filename(name)
        shp_path = os.path.join(groups_dir, f"{safe_name}.shp")

        # Write a single-feature shapefile for this group
        writer_options = QgsVectorFileWriter.SaveVectorOptions()
        writer_options.driverName = "ESRI Shapefile"
        writer_options.fileEncoding = "UTF-8"

        tmp_layer = QgsVectorLayer("Polygon?crs=EPSG:4326", "tmp", "memory")
        tmp_feat = QgsFeature()
        geom = feat.geometry()
        if xform:
            geom.transform(xform)
        tmp_feat.setGeometry(geom)
        tmp_layer.dataProvider().addFeature(tmp_feat)

        error, msg = write_vector_layer(tmp_layer, shp_path, writer_options)
        if error != QgsVectorFileWriter.NoError:
            raise ExportError(f"Failed to write shapefile for group '{name}': {msg}")

        rel_path = f"groups/{safe_name}.shp"
        shp_paths[name] = rel_path

        groups.append({
            "name":         name,
            "demographics": (feat["demographics"] or "default").strip(),
            "destinations": (feat["destinations"] or "").strip(),
            "dest_cdf":     (feat["dest_cdf"]     or "").strip(),
            "resp_curves":  (feat["resp_curves"]  or "default_curve").strip(),
            "resp_cdf":     (feat["resp_cdf"]     or "1.0").strip(),
            "dest_choice":  (feat["dest_choice"]  or "EvacGroupWeighted").strip(),
            "is_default":   bool(feat["is_default"]),
            "shp_path":     rel_path,
        })

    return groups, shp_paths


# ---------------------------------------------------------------------------
# .wui writer
# ---------------------------------------------------------------------------

def _write_wui(path, sim_name, lower_left, domain_size,
               delta_time, max_sim_time, stop_when_evacuated,
               destinations, groups, shp_paths, module_config):

    lat0, lon0    = lower_left
    width, height = domain_size

    utm_e = QgsProject.instance().readEntry("wuinity", "utm_offset_e", "")[0]
    utm_n = QgsProject.instance().readEntry("wuinity", "utm_offset_n", "")[0]

    mc_pop   = module_config.get("population",     {})
    mc_ped   = module_config.get("pedestrian",     {})
    mc_traf  = module_config.get("traffic",        {})
    mc_fire  = module_config.get("wildfire",       {})
    mc_smoke = module_config.get("smoke",          {})
    mc_trig  = module_config.get("trigger_buffer", {})

    lines = []

    def w(s=""):
        lines.append(s)

    def _bool(v):
        return "true" if v else "false"

    # ── [Simulation] ──────────────────────────────────────────────────
    w("[Simulation]")
    w(f"Name={sim_name}")
    w(f"LowerLeftLatLon={lat0:.6f},{lon0:.6f}")
    w(f"DomainSize={width:.1f},{height:.1f}")
    w(f"DeltaTime={delta_time}")
    w(f"MaxSimTime={max_sim_time}")
    w(f"StopWhenEvacuated={_bool(stop_when_evacuated)}")

    # ── [Map] ─────────────────────────────────────────────────────────
    w()
    w("[Map]")
    w("MapProvider=Mapbox")
    w("ZoomLevel=13")

    # ── [Population] ─────────────────────────────────────────────────
    w()
    w("[Population]")
    if mc_pop.get("file"):
        w(f"PopulationFile={mc_pop['file']}")
    w(f"CullOutsideGroups={_bool(mc_pop.get('cull_outside', True))}")

    # ── [Evacuation] ─────────────────────────────────────────────────
    w()
    w("[Evacuation]")
    w(f"EvacuationOrderStart={int(mc_trig.get('evac_order_start', 0))}")
    use_trigger = mc_trig.get("enabled", False)
    w(f"UseTriggerBufferEvacuation={_bool(use_trigger)}")
    if use_trigger and mc_trig.get("module") == "BackwardsFireCell2":
        trig_file = mc_trig.get("trigger_file", "")
        if trig_file:
            w(f"TriggerBufferFile={trig_file}")

    # ── [Demographics] ────────────────────────────────────────────────
    demo_tbl   = get_demographics_table()
    demo_feats = list(demo_tbl.getFeatures()) if demo_tbl else []
    if demo_feats:
        for d in demo_feats:
            w()
            w("[Demographics]")
            w(f"Name={d['name']}")
            w(f"AllowMoreThanOneCar={_bool(d['allow_more_cars'])}")
            w(f"MaxCars={d['max_cars'] or 2}")
            w(f"MaxCarsProbability={d['max_cars_prob'] or 0.3}")
            if d["name"] == "default":
                w("Default=true")
    else:
        w()
        w("[Demographics]")
        w("Name=default")
        w("Default=true")
        w("AllowMoreThanOneCar=true")
        w("MaxCars=2")
        w("MaxCarsProbability=0.3")

    # ── [ResponseCurve] ───────────────────────────────────────────────
    import json as _json
    curve_tbl   = get_curves_table()
    curve_feats = list(curve_tbl.getFeatures()) if curve_tbl else []
    if curve_feats:
        for c in curve_feats:
            w()
            w("[ResponseCurve]")
            w(f"Name={c['name']}")
            try:
                points = _json.loads(c["data"] or "[]")
                for t, p in points:
                    w(f"{int(t)},{p}")
            except Exception:
                for t, p in DEFAULT_RESPONSE_CURVE:
                    w(f"{t},{p}")
    else:
        w()
        w("[ResponseCurve]")
        w("Name=default_curve")
        for t, p in DEFAULT_RESPONSE_CURVE:
            w(f"{t},{p}")

    # ── [Destination] ─────────────────────────────────────────────────
    for d in destinations:
        w()
        w("[Destination]")
        w(f"Name={d['name']}")
        w(f"LatLon={d['lat']:.6f},{d['lon']:.6f}")
        w(f"Type={d['type']}")
        if d["max_flow"] != -1:
            w(f"MaxFlow={d['max_flow']}")
        if d["max_vehicles"] != -1:
            w(f"MaxVehicles={d['max_vehicles']}")
        if d["max_people"] != -1:
            w(f"MaxPeople={d['max_people']}")
        if d["blocked"]:
            w("Blocked=true")

    # ── [EvacuationGroup] ─────────────────────────────────────────────
    for g in groups:
        w()
        w("[EvacuationGroup]")
        w(f"Name={g['name']}")
        w(f"ShapeFile={g['shp_path']}")
        w(f"Demographics={g['demographics']}")
        if g["destinations"]:
            w(f"Destinations={g['destinations']}")
        if g["dest_cdf"] and g["destinations"] and "," in g["destinations"]:
            w(f"DestinationsCDF={g['dest_cdf']}")
        w(f"ResponseCurves={g['resp_curves']}")
        if g["resp_cdf"] and "," in g["resp_curves"]:
            w(f"ResponseCurvesCDF={g['resp_cdf']}")
        w(f"DestinationChoice={g['dest_choice']}")
        if g["is_default"]:
            w("Default=true")

    # ── [PedestrianModule] / [MacroHouseholdSim] ─────────────────────
    w()
    w("[PedestrianModule]")
    w(f"Enabled={_bool(mc_ped.get('enabled', False))}")
    w(f"Module={mc_ped.get('module', 'MacroHouseholdSim')}")

    w()
    w("[MacroHouseholdSim]")
    w(f"WalkingSpeedMinMax={mc_ped.get('speed_min', 0.5)},{mc_ped.get('speed_max', 1.5)}")
    w(f"WalkingSpeedModifier={mc_ped.get('speed_modifier', 1.0)}")
    w(f"WalkingDistanceModifier={mc_ped.get('distance_modifier', 1.0)}")

    # ── [TrafficModule] / [SUMO] ──────────────────────────────────────
    w()
    w("[TrafficModule]")
    w(f"Enabled={_bool(mc_traf.get('enabled', False))}")
    w(f"Module={mc_traf.get('module', 'SUMO')}")
    w(f"VisibilityAffectsSpeed={_bool(mc_traf.get('visibility_speed', False))}")

    w()
    w("[SUMO]")
    sumo_cfg = mc_traf.get("sumo_cfg", "")
    if sumo_cfg:
        w(f"ConfigurationFile={sumo_cfg}")
    if utm_e and utm_n:
        w(f"UTMoffset={float(utm_e):.3f},{float(utm_n):.3f}")
    w(f"SmokeAlpha={mc_traf.get('smoke_alpha', 0.5)}")
    w(f"SmokeBeta={mc_traf.get('smoke_beta', 0.012)}")

    # ── [WildfireModule] / [AscImport] ───────────────────────────────
    w()
    w("[WildfireModule]")
    w(f"Enabled={_bool(mc_fire.get('enabled', False))}")
    fire_module = mc_fire.get("module", "AscImport")
    w(f"Module={fire_module}")
    lcp = mc_fire.get("lcp_file", "")
    if lcp:
        w(f"LcpFile={lcp}")

    if fire_module == "AscImport":
        w()
        w("[AscImport]")
        if mc_fire.get("asc_root"):
            w(f"RootFolder={mc_fire['asc_root']}")
        if mc_fire.get("toa_file"):
            w(f"TimeOfArrivalFile={mc_fire['toa_file']}")
        if mc_fire.get("ros_file"):
            w(f"RateOfSpreadFile={mc_fire['ros_file']}")
        if mc_fire.get("sd_file"):
            w(f"SpreadDirectionFile={mc_fire['sd_file']}")
        if mc_fire.get("fi_file"):
            w(f"FirelineIntensityFile={mc_fire['fi_file']}")
        if mc_fire.get("wx_file"):
            w(f"WeatherStreamFile={mc_fire['wx_file']}")

    # ── [SmokeModule] + sub-section ───────────────────────────────────
    smoke_module = mc_smoke.get("module", "GlobalSmoke")
    w()
    w("[SmokeModule]")
    w(f"Enabled={_bool(mc_smoke.get('enabled', False))}")
    w(f"Module={smoke_module}")

    w()
    if smoke_module == "GlobalSmoke":
        w("[GlobalSmoke]")
        ext = mc_smoke.get("extinction_file", "")
        if ext:
            w(f"ExtinctionFile={ext}")
    elif smoke_module in ("AdvectDiffuseMixingLayer", "AdvectDiffuse3D"):
        w(f"[{smoke_module}]")
        w(f"MixingLayerHeight={mc_smoke.get('mixing_height', 500.0)}")
    elif smoke_module == "Lagrangian":
        w("[Lagrangian]")
        w(f"ParticlesPerFireCell={int(mc_smoke.get('particles', 100))}")

    # ── [TriggerBufferModule] / [kPERIL] ─────────────────────────────
    trig_module = mc_trig.get("module", "kPERIL")
    w()
    w("[TriggerBufferModule]")
    w(f"Enabled={_bool(mc_trig.get('enabled', False))}")
    w(f"Module={trig_module}")

    if trig_module == "kPERIL":
        w()
        w("[kPERIL]")
        w(f"MidflameWindspeed={mc_trig.get('midflame_wind', 5.0)}")
        w(f"CalculateROSFromBehave={_bool(mc_trig.get('ros_from_behave', False))}")
        fm = mc_trig.get("fuel_moisture", "")
        if fm:
            w(f"InitialFuelMoistureFile={fm}")
        w(f"OutputName={mc_trig.get('output_name', 'trigger_buffer')}")

    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def _safe_filename(name):
    """Strip characters that are unsafe in filenames."""
    keep = set("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-")
    return "".join(c if c in keep else "_" for c in name)
