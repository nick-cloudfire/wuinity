"""
OSM road download, exit detection, and UTM offset computation.

Download runs in a QgsTask (background thread) so the QGIS UI stays responsive.

Two download modes:
  start_download()       — roads only (fast, used for road display and SUMO)
  start_full_download()  — roads + buildings + landuse (for PREACTcli population)
"""

import os
import re
import time
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
LAYER_OSM_ROADS    = "OSM Roads"
GPKG_LAYER_ROADS   = "osm_roads"
OSM_XML_FILENAME   = "osm_bbox.osm.xml"       # roads only — for SUMO
OSM_FULL_FILENAME  = "osm_population.osm.xml"  # full data  — for PREACTcli

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
    Download roads only for the domain bbox (fast — no buildings/landuse).
    Suitable for road display and SUMO network generation.

    on_success(roads_layer, exits, utm_offset)
    on_failure(message: str)
    Returns the task — caller must keep a reference.
    """
    south, west, north, east = _domain_bbox_wgs84(domain_layer, expand_pct)
    query = (
        f"[out:xml][timeout:90]"
        f"[bbox:{south:.6f},{west:.6f},{north:.6f},{east:.6f}];"
        f'(way["highway"~"^({ROAD_FILTER})$"];);'
        f"out geom;"
    )
    _log(f"Starting road download: S={south:.4f} W={west:.4f} N={north:.4f} E={east:.4f}")
    task = _OsmDownloadTask(
        query, domain_layer, on_success, on_failure,
        task_name="Downloading OSM roads",
        save_xml_path=save_xml_path,
        build_layer=True,
    )
    QgsApplication.taskManager().addTask(task)
    return task


def start_full_download(domain_layer, on_done, expand_pct=0.1,
                        save_xml_path=None):
    """
    Download roads + buildings + landuse (larger — for PREACTcli population).

    on_done(success: bool, message: str, saved_path: str | None)
    Returns the task — caller must keep a reference.
    """
    south, west, north, east = _domain_bbox_wgs84(domain_layer, expand_pct)
    query = (
        f"[out:xml][timeout:240]"
        f"[bbox:{south:.6f},{west:.6f},{north:.6f},{east:.6f}];"
        f'(way["highway"~"^({ROAD_FILTER})$"];'
        f'way["building"];'
        f'way["landuse"];'
        f'relation["building"];);'
        f"out geom;"
    )
    _log(f"Starting full OSM download for population generation")
    task = _OsmDownloadTask(
        query, domain_layer,
        on_success=lambda *_: on_done(True, "", save_xml_path),
        on_failure=lambda msg: on_done(False, msg, None),
        task_name="Downloading full OSM (population)",
        save_xml_path=save_xml_path,
        build_layer=False,
    )
    QgsApplication.taskManager().addTask(task)
    return task


# ---------------------------------------------------------------------------
# HTTP helpers
# ---------------------------------------------------------------------------

def _fetch_overpass(url, data, timeout=120):
    """
    POST to one Overpass mirror.
    Returns (bytes, None) on success, (None, error_str) on any failure.
    Validates HTTP status, non-XML responses, and Overpass error remarks.
    """
    try:
        req = urllib.request.Request(
            url, data=data,
            headers={"Content-Type": "application/x-www-form-urlencoded"},
        )
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            code = resp.getcode()
            if code == 429:
                return None, "Rate limited (HTTP 429) — server busy, try again later"
            if code != 200:
                return None, f"HTTP {code}"
            raw = resp.read()
    except urllib.error.HTTPError as e:
        if e.code == 429:
            return None, "Rate limited (HTTP 429)"
        return None, f"HTTP {e.code}: {e.reason}"
    except urllib.error.URLError as e:
        return None, f"Connection failed: {e.reason}"
    except TimeoutError:
        return None, "Timed out waiting for server"
    except OSError as e:
        return None, f"Network error: {e}"

    if not raw:
        return None, "Empty response"

    # Must be XML, not an HTML error page
    if not raw.lstrip()[:5].startswith(b"<"):
        snippet = raw[:200].decode("utf-8", errors="replace").strip()
        return None, f"Non-XML response: {snippet!r}"

    # Check for Overpass server-side errors embedded in the XML
    overpass_err = _parse_overpass_remark(raw)
    if overpass_err:
        return None, f"Overpass error: {overpass_err}"

    return raw, None


def _parse_overpass_remark(raw):
    """Return the remark text if Overpass signalled an error, else None."""
    try:
        header = raw[:8192].decode("utf-8", errors="replace")
    except Exception:
        return None
    if "<remark>" not in header:
        return None
    m = re.search(r"<remark>(.*?)</remark>", header, re.DOTALL)
    if not m:
        return None
    text = m.group(1).strip()
    if any(w in text.lower() for w in ("error", "timeout", "memory", "exceeded", "killed")):
        return text
    return None


# ---------------------------------------------------------------------------
# Background task
# ---------------------------------------------------------------------------

class _OsmDownloadTask(QgsTask):
    def __init__(self, query, domain_layer, on_success, on_failure,
                 task_name, save_xml_path=None, build_layer=True):
        super().__init__(task_name, QgsTask.CanCancel)
        self._query         = query
        self._domain_geom   = _snapshot_domain(domain_layer)
        self._domain_crs    = domain_layer.crs()
        self._on_success    = on_success
        self._on_failure    = on_failure
        self._save_xml_path = save_xml_path
        self._build_layer   = build_layer
        self._xml_bytes     = None
        self._error         = None

    def run(self):
        data = urllib.parse.urlencode({"data": self._query}).encode()

        for i, url in enumerate(OVERPASS_MIRRORS):
            if self.isCanceled():
                _log("OSM download cancelled")
                return False

            if i > 0:
                pause = min(5 * i, 15)
                _log(f"Waiting {pause}s before next mirror…")
                time.sleep(pause)

            _log(f"Trying mirror {i + 1}/{len(OVERPASS_MIRRORS)}: {url}")
            raw, error = _fetch_overpass(url, data, timeout=150)

            if raw is not None:
                _log(f"Downloaded {len(raw):,} bytes from {url}")
                self._xml_bytes = raw
                if self._save_xml_path:
                    self._save(raw)
                return True

            self._error = error
            _log(f"Mirror failed: {error}", Qgis.Warning)

        return False

    def _save(self, raw):
        try:
            folder = os.path.dirname(self._save_xml_path)
            if folder:
                os.makedirs(folder, exist_ok=True)
            with open(self._save_xml_path, "wb") as f:
                f.write(raw)
            _log(f"OSM XML saved to {self._save_xml_path}")
        except OSError as e:
            _log(f"Warning: could not save OSM XML: {e}", Qgis.Warning)

    def finished(self, success):
        if not success or self._xml_bytes is None:
            msg = self._error or "All Overpass mirrors failed"
            _log(f"OSM download failed: {msg}", Qgis.Critical)
            self._on_failure(msg)
            return

        if not self._build_layer:
            # Full download — caller only needs the saved file
            self._on_success(None, [], (0, 0, 32633))
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
            import traceback
            _log(f"Post-processing error: {traceback.format_exc()}", Qgis.Critical)
            self._on_failure(f"Post-processing failed: {e}")


# ---------------------------------------------------------------------------
# Exit detection
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

    try:
        root = ET.fromstring(xml_bytes)
    except ET.ParseError as e:
        raise RuntimeError(f"Invalid XML in OSM response: {e}")

    features = []
    for way in root.findall("way"):
        tags    = {tag.get("k"): tag.get("v") for tag in way.findall("tag")}
        highway = tags.get("highway", "")
        if not highway:
            continue

        pts = []
        for nd in way.findall("nd"):
            lat = nd.get("lat")
            lon = nd.get("lon")
            if lat and lon:
                try:
                    pts.append(QgsPointXY(float(lon), float(lat)))
                except ValueError:
                    continue

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
