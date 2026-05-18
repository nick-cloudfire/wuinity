"""
OSM road download, exit detection, and UTM offset computation.

Download runs in a QgsTask (background thread) so the QGIS UI stays responsive.
The download uses OSM XML format so the raw file can be reused by PREACTcli
for population generation without a second Overpass request.
"""

import math
import urllib.request
import urllib.parse
import urllib.error
import xml.etree.ElementTree as ET

from qgis.core import (
    QgsTask, QgsApplication, QgsVectorLayer, QgsFeature,
    QgsGeometry, QgsPointXY, QgsField, QgsProject,
    QgsCoordinateReferenceSystem, QgsCoordinateTransform,
    QgsCategorizedSymbolRenderer, QgsRendererCategory,
    QgsLineSymbol, QgsDistanceArea, QgsMessageLog, Qgis,
    QgsWkbTypes,
)
from PyQt5.QtGui import QColor
from .compat import field_str

LOG_TAG = "WUInity"
LAYER_OSM_ROADS  = "OSM Roads"
GPKG_LAYER_ROADS = "osm_roads"
OSM_XML_FILENAME = "osm_bbox.osm.xml"

WGS84 = QgsCoordinateReferenceSystem("EPSG:4326")

OVERPASS_MIRRORS = [
    "https://overpass-api.de/api/interpreter",
    "https://lz4.overpass-api.de/api/interpreter",
    "https://overpass.kumi.systems/api/interpreter",
    "https://overpass.openstreetmap.ru/api/interpreter",
]

ROAD_FILTER = (
    "motorway|trunk|primary|secondary|tertiary|"
    "residential|unclassified|service|"
    "motorway_link|trunk_link|primary_link|secondary_link|tertiary_link"
)

ROAD_STYLE = {
    "motorway":       ("#e06060", 2.5),
    "motorway_link":  ("#e06060", 1.5),
    "trunk":          ("#e08040", 2.0),
    "trunk_link":     ("#e08040", 1.5),
    "primary":        ("#e0a040", 2.0),
    "primary_link":   ("#e0a040", 1.5),
    "secondary":      ("#d0c040", 1.5),
    "secondary_link": ("#d0c040", 1.2),
    "tertiary":       ("#c0c060", 1.2),
    "tertiary_link":  ("#c0c060", 1.0),
    "residential":    ("#b0b0b0", 1.0),
    "unclassified":   ("#b0b0b0", 1.0),
    "service":        ("#c8c8c8", 0.7),
}


def _log(msg, level=Qgis.Info):
    QgsMessageLog.logMessage(msg, LOG_TAG, level)


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def start_download(domain_layer, on_success, on_failure, expand_pct=0.1,
                   save_xml_path=None):
    """
    Kick off a background OSM download for the domain's bounding box.
    Returns the task — caller MUST keep a reference to prevent GC.

    The download uses OSM XML format. If save_xml_path is provided the raw
    bytes are written there so PREACTcli can reuse them for population
    generation without a second Overpass request.

    on_success(roads_layer, exits, utm_offset)
      roads_layer  : QgsVectorLayer (memory, in WGS84)
      exits        : list of QgsPointXY — candidate exit locations
      utm_offset   : (easting_m, northing_m, epsg) of domain lower-left
    on_failure(message: str)
    """
    south, west, north, east = _domain_bbox_wgs84(domain_layer, expand_pct)
    _log(f"Starting OSM download: S={south:.4f} W={west:.4f} N={north:.4f} E={east:.4f}")
    task = _OsmDownloadTask(
        south, west, north, east,
        domain_layer,
        on_success, on_failure,
        save_xml_path=save_xml_path,
    )
    QgsApplication.taskManager().addTask(task)
    return task


# ---------------------------------------------------------------------------
# Background task
# ---------------------------------------------------------------------------

class _OsmDownloadTask(QgsTask):
    def __init__(self, south, west, north, east, domain_layer,
                 on_success, on_failure, save_xml_path=None):
        super().__init__("Downloading OSM roads", QgsTask.CanCancel)
        self._south         = south
        self._west          = west
        self._north         = north
        self._east          = east
        self._domain_geom   = _snapshot_domain(domain_layer)
        self._domain_crs    = domain_layer.crs()
        self._on_success    = on_success
        self._on_failure    = on_failure
        self._save_xml_path = save_xml_path
        self._xml_bytes     = None
        self._error         = None

    def run(self):
        # Single XML download: roads + buildings + landuse — reusable by PREACTcli
        query = (
            f"[out:xml][timeout:180]"
            f"[bbox:{self._south:.6f},{self._west:.6f},{self._north:.6f},{self._east:.6f}];"
            f'(way["highway"~"^({ROAD_FILTER})$"];'
            f'way["building"];'
            f'way["landuse"];'
            f'relation["building"];);'
            f"out geom;"
        )
        data = urllib.parse.urlencode({"data": query}).encode()

        for url in OVERPASS_MIRRORS:
            if self.isCanceled():
                _log("OSM download cancelled")
                return False
            _log(f"Trying mirror: {url}")
            try:
                req = urllib.request.Request(
                    url, data=data,
                    headers={"Content-Type": "application/x-www-form-urlencoded"},
                )
                with urllib.request.urlopen(req, timeout=240) as resp:
                    raw = resp.read()
                _log(f"Downloaded {len(raw):,} bytes from {url}")
                self._xml_bytes = raw
                if self._save_xml_path:
                    with open(self._save_xml_path, "wb") as f:
                        f.write(raw)
                    _log(f"OSM XML saved to {self._save_xml_path}")
                return True
            except Exception as e:
                self._error = str(e)
                _log(f"Mirror failed ({url}): {e}", Qgis.Warning)
                continue

        return False

    def finished(self, success):
        if not success or self._xml_bytes is None:
            msg = self._error or "All Overpass mirrors failed"
            _log(f"OSM download failed: {msg}", Qgis.Critical)
            self._on_failure(msg)
            return

        try:
            roads_layer = _build_layer_from_xml(self._xml_bytes)
            _log(f"Built road layer with {roads_layer.featureCount()} features")

            exits = find_domain_exits(roads_layer, self._domain_geom, self._domain_crs)
            _log(f"Detected {len(exits)} exit candidate(s)")

            utm_offset = compute_utm_offset(self._domain_geom, self._domain_crs)
            _log(f"UTM offset: {utm_offset[0]:.1f}, {utm_offset[1]:.1f} (EPSG:{utm_offset[2]})")

            self._on_success(roads_layer, exits, utm_offset)
        except Exception as e:
            _log(f"Post-processing error: {e}", Qgis.Critical)
            self._on_failure(f"Post-processing failed: {e}")


# ---------------------------------------------------------------------------
# Exit detection  (mirrors getDomainExits.py logic without SUMO)
# ---------------------------------------------------------------------------

def find_domain_exits(roads_layer, domain_geom_wgs84, domain_crs,
                      collapse_tol_m=100):
    """
    Find points where driveable roads cross the domain boundary.
    Nearby crossings within collapse_tol_m are merged into one exit.

    Returns a list of QgsPointXY in WGS84.
    """
    geom = QgsGeometry(domain_geom_wgs84)
    if domain_crs != WGS84:
        xform = QgsCoordinateTransform(domain_crs, WGS84, QgsProject.instance())
        geom.transform(xform)

    boundary = geom.buffer(0, 5).convertToType(QgsWkbTypes.LineGeometry, False)

    exit_highway_types = {
        "motorway", "motorway_link", "trunk", "trunk_link",
        "primary", "primary_link", "secondary", "secondary_link",
        "tertiary", "tertiary_link", "residential", "unclassified",
    }

    raw_exits = []
    hw_idx = roads_layer.fields().indexOf("highway")

    for feat in roads_layer.getFeatures():
        hw = feat.attributes()[hw_idx] if hw_idx >= 0 else ""
        if hw not in exit_highway_types:
            continue
        road_geom = feat.geometry()
        if not road_geom.intersects(boundary):
            continue
        intersection = road_geom.intersection(boundary)
        for pt in _extract_points(intersection):
            raw_exits.append(pt)

    return _collapse_exits(raw_exits, collapse_tol_m)


def _extract_points(geom):
    """Yield all QgsPointXY from a geometry (point, multipoint, or collection)."""
    if geom.isEmpty():
        return
    gtype = geom.type()
    if gtype == QgsWkbTypes.PointGeometry:
        if geom.isMultipart():
            for pt in geom.asMultiPoint():
                yield pt
        else:
            yield geom.asPoint()
    elif gtype in (QgsWkbTypes.LineGeometry, QgsWkbTypes.PolygonGeometry):
        yield geom.centroid().asPoint()


def _collapse_exits(points, tol_m):
    """
    Greedy single-pass clustering: merge any point within tol_m of an
    already-formed cluster centre into that cluster.
    """
    if not points:
        return []

    da = QgsDistanceArea()
    da.setEllipsoid("WGS84")
    da.setSourceCrs(WGS84, QgsProject.instance().transformContext())

    clusters = []

    for pt in points:
        merged = False
        for i, (centre, members) in enumerate(clusters):
            if da.measureLine(pt, centre) < tol_m:
                members.append(pt)
                avg_x = sum(p.x() for p in members) / len(members)
                avg_y = sum(p.y() for p in members) / len(members)
                clusters[i] = (QgsPointXY(avg_x, avg_y), members)
                merged = True
                break
        if not merged:
            clusters.append((pt, [pt]))

    return [centre for centre, _ in clusters]


# ---------------------------------------------------------------------------
# UTM offset
# ---------------------------------------------------------------------------

def compute_utm_offset(domain_geom_wgs84, domain_crs):
    """
    Compute the UTM easting/northing of the domain lower-left corner.
    Returns (easting_m, northing_m, utm_epsg).
    """
    geom = QgsGeometry(domain_geom_wgs84)
    if domain_crs != WGS84:
        xform = QgsCoordinateTransform(domain_crs, WGS84, QgsProject.instance())
        geom.transform(xform)

    bbox = geom.boundingBox()
    lon  = bbox.xMinimum()
    lat  = bbox.yMinimum()

    zone     = int((lon + 180.0) / 6.0) + 1
    utm_epsg = 32600 + zone if lat >= 0 else 32700 + zone

    utm_crs = QgsCoordinateReferenceSystem(f"EPSG:{utm_epsg}")
    xform   = QgsCoordinateTransform(WGS84, utm_crs, QgsProject.instance())
    utm_pt  = xform.transform(QgsPointXY(lon, lat))

    return utm_pt.x(), utm_pt.y(), utm_epsg


# ---------------------------------------------------------------------------
# Layer builder — parses OSM XML (out geom format)
# ---------------------------------------------------------------------------

def _build_layer_from_xml(xml_bytes):
    layer = QgsVectorLayer("LineString?crs=EPSG:4326", LAYER_OSM_ROADS, "memory")
    prov  = layer.dataProvider()
    prov.addAttributes([
        field_str("osm_id"),
        field_str("highway"),
        field_str("name"),
        field_str("oneway"),
        field_str("maxspeed"),
        field_str("lanes"),
    ])
    layer.updateFields()

    root = ET.fromstring(xml_bytes)
    features = []
    for way in root.findall("way"):
        tags = {tag.get("k"): tag.get("v") for tag in way.findall("tag")}
        highway = tags.get("highway", "")
        if not highway:
            continue  # skip buildings/landuse ways

        pts = []
        for nd in way.findall("nd"):
            lat = nd.get("lat")
            lon = nd.get("lon")
            if lat and lon:
                pts.append(QgsPointXY(float(lon), float(lat)))

        if len(pts) < 2:
            continue

        feat = QgsFeature()
        feat.setGeometry(QgsGeometry.fromPolylineXY(pts))
        feat.setAttributes([
            way.get("id", ""),
            highway,
            tags.get("name",     ""),
            tags.get("oneway",   ""),
            tags.get("maxspeed", ""),
            tags.get("lanes",    ""),
        ])
        features.append(feat)

    prov.addFeatures(features)
    layer.updateExtents()
    _apply_road_style(layer)
    return layer


def _apply_road_style(layer):
    categories = []
    for hw, (color, width) in ROAD_STYLE.items():
        sym = QgsLineSymbol.createSimple({
            "color": color, "width": str(width),
            "capstyle": "round", "joinstyle": "round",
        })
        categories.append(QgsRendererCategory(hw, sym, hw))
    fallback = QgsLineSymbol.createSimple({"color": "#aaaaaa", "width": "0.7"})
    categories.append(QgsRendererCategory("", fallback, "(other)"))
    layer.setRenderer(QgsCategorizedSymbolRenderer("highway", categories))


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _snapshot_domain(domain_layer):
    for feat in domain_layer.getFeatures():
        return QgsGeometry(feat.geometry())
    return None


def _domain_bbox_wgs84(domain_layer, expand_pct):
    src_crs = domain_layer.crs()
    bbox    = domain_layer.extent()
    if src_crs != WGS84:
        xform = QgsCoordinateTransform(src_crs, WGS84, QgsProject.instance())
        bbox  = xform.transformBoundingBox(bbox)
    dx = bbox.width()  * expand_pct
    dy = bbox.height() * expand_pct
    return (
        bbox.yMinimum() - dy,
        bbox.xMinimum() - dx,
        bbox.yMaximum() + dy,
        bbox.xMaximum() + dx,
    )
