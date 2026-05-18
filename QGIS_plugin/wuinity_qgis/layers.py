"""
Layer creation and lookup for WUInity QGIS plugin.
Each managed layer has a fixed name used to find it in the project.
"""

import os

from qgis.core import (
    QgsVectorLayer, QgsField, QgsProject,
    QgsCoordinateReferenceSystem, QgsEditorWidgetSetup,
    QgsVectorFileWriter, QgsWkbTypes,
)
from .compat import field_str, field_int, field_double, write_vector_layer

LAYER_DOMAIN       = "WUI Domain"
LAYER_DESTINATIONS = "WUI Destinations"
LAYER_GROUPS       = "WUI Evacuation Groups"

TABLE_DEMOGRAPHICS = "wui_demographics"
TABLE_CURVES       = "wui_response_curves"

GPKG_FILENAME = "wuinity_layers.gpkg"

# Internal GeoPackage layer names (no spaces — OGR requirement)
_GPKG_NAMES = {
    LAYER_DOMAIN:       "wui_domain",
    LAYER_DESTINATIONS: "wui_destinations",
    LAYER_GROUPS:       "wui_groups",
}

# Default response curve data: [[time_s, cumulative_probability], ...]
DEFAULT_CURVE_DATA = [
    [0, 0.0], [300, 0.037], [600, 0.15],
    [1080, 0.67], [2400, 0.93], [6300, 1.0],
]

WGS84 = QgsCoordinateReferenceSystem("EPSG:4326")


# ---------------------------------------------------------------------------
# Lookups
# ---------------------------------------------------------------------------

def _find_layer(name):
    layers = QgsProject.instance().mapLayersByName(name)
    return layers[0] if layers else None


def get_domain_layer():
    return _find_layer(LAYER_DOMAIN)


def get_destinations_layer():
    return _find_layer(LAYER_DESTINATIONS)


def get_groups_layer():
    return _find_layer(LAYER_GROUPS)


def get_demographics_table():
    return _load_table(TABLE_DEMOGRAPHICS)


def get_curves_table():
    return _load_table(TABLE_CURVES)


def _load_table(tbl_name):
    """
    Return the named non-spatial table layer, auto-loading it from the project
    GeoPackage if it isn't already registered in the project.
    """
    layer = _find_layer(tbl_name)
    if layer and layer.isValid():
        return layer

    folder = get_project_folder()
    if not folder:
        return None

    gpkg_path = os.path.join(folder, GPKG_FILENAME)
    if not os.path.exists(gpkg_path):
        return None

    if not _gpkg_layer_exists(gpkg_path, tbl_name):
        # Table missing from GeoPackage — create it with defaults now
        schema_fn, default_fn = {
            TABLE_DEMOGRAPHICS: (_make_schema_demographics, _add_default_demographics),
            TABLE_CURVES:       (_make_schema_curves,       _add_default_curve),
        }.get(tbl_name, (None, None))
        if schema_fn is None:
            return None
        mem = schema_fn()
        default_fn(mem)
        _write_to_gpkg(mem, gpkg_path, tbl_name, append=True)

    uri   = f"{gpkg_path}|layername={tbl_name}"
    layer = QgsVectorLayer(uri, tbl_name, "ogr")
    if layer.isValid():
        QgsProject.instance().addMapLayer(layer, addToLegend=False)
        return layer
    return None


# ---------------------------------------------------------------------------
# Main entry point — create layers on disk in a GeoPackage
# ---------------------------------------------------------------------------

def create_project_layers_on_disk(folder):
    """
    Create (or load) the three WUInity layers backed by a GeoPackage at
    <folder>/wuinity_layers.gpkg.  Returns a list of newly added layers.

    If the GeoPackage already exists the layers inside it are loaded directly
    without overwriting any existing data.
    """
    gpkg_path = os.path.join(folder, GPKG_FILENAME)
    existing_gpkg = os.path.exists(gpkg_path)

    # Specs for each layer: (display name, factory function, wkb type)
    specs = [
        (LAYER_DOMAIN,       _make_schema_domain,       QgsWkbTypes.Polygon),
        (LAYER_DESTINATIONS, _make_schema_destinations,  QgsWkbTypes.Point),
        (LAYER_GROUPS,       _make_schema_groups,        QgsWkbTypes.Polygon),
    ]

    created = []
    for display_name, schema_fn, _wkb in specs:
        # Skip if already loaded in the project
        if _find_layer(display_name):
            continue

        gpkg_layer_name = _GPKG_NAMES[display_name]
        uri = f"{gpkg_path}|layername={gpkg_layer_name}"

        if existing_gpkg and _gpkg_layer_exists(gpkg_path, gpkg_layer_name):
            # Load from existing GeoPackage without touching data
            layer = QgsVectorLayer(uri, display_name, "ogr")
        else:
            # Write schema (empty layer) to GeoPackage then reload from disk
            mem_layer = schema_fn()
            _write_to_gpkg(mem_layer, gpkg_path, gpkg_layer_name, existing_gpkg)
            existing_gpkg = True  # subsequent layers append rather than overwrite
            layer = QgsVectorLayer(uri, display_name, "ogr")

        if not layer.isValid():
            raise RuntimeError(f"Layer '{display_name}' could not be loaded from {gpkg_path}")

        _apply_widget_setups(layer, display_name)
        _apply_style_for(layer, display_name)
        QgsProject.instance().addMapLayer(layer)
        created.append(layer)

    # Non-spatial tables: demographics and response curves
    gpkg_path = os.path.join(folder, GPKG_FILENAME)
    existing_gpkg = os.path.exists(gpkg_path)

    for tbl_name, schema_fn, default_fn in [
        (TABLE_DEMOGRAPHICS, _make_schema_demographics, _add_default_demographics),
        (TABLE_CURVES,       _make_schema_curves,       _add_default_curve),
    ]:
        if not _find_layer(tbl_name):
            uri = f"{gpkg_path}|layername={tbl_name}"
            if existing_gpkg and _gpkg_layer_exists(gpkg_path, tbl_name):
                tbl = QgsVectorLayer(uri, tbl_name, "ogr")
            else:
                mem = schema_fn()
                default_fn(mem)
                _write_to_gpkg(mem, gpkg_path, tbl_name, append=existing_gpkg)
                existing_gpkg = True
                tbl = QgsVectorLayer(uri, tbl_name, "ogr")
            if tbl.isValid():
                QgsProject.instance().addMapLayer(tbl, addToLegend=False)
                created.append(tbl)

    # Store the folder path so other parts of the plugin can find it
    QgsProject.instance().writeEntry("wuinity", "project_folder", folder)

    return created


def get_project_folder():
    """Return the folder stored by create_project_layers_on_disk, or None."""
    path, ok = QgsProject.instance().readEntry("wuinity", "project_folder", "")
    return path if ok and path else None


# ---------------------------------------------------------------------------
# GeoPackage helpers
# ---------------------------------------------------------------------------

def _gpkg_layer_exists(gpkg_path, layer_name):
    """Return True if layer_name is already present in the GeoPackage."""
    probe = QgsVectorLayer(f"{gpkg_path}|layername={layer_name}", "probe", "ogr")
    return probe.isValid()


def _write_to_gpkg(mem_layer, gpkg_path, gpkg_layer_name, append):
    options = QgsVectorFileWriter.SaveVectorOptions()
    options.driverName   = "GPKG"
    options.layerName    = gpkg_layer_name
    options.fileEncoding = "UTF-8"
    options.actionOnExistingFile = (
        QgsVectorFileWriter.CreateOrOverwriteLayer
        if append else
        QgsVectorFileWriter.CreateOrOverwriteFile
    )
    error, msg = write_vector_layer(mem_layer, gpkg_path, options)
    if error != QgsVectorFileWriter.NoError:
        raise RuntimeError(f"Could not write '{gpkg_layer_name}' to GeoPackage: {msg}")


def save_layer_to_gpkg(layer, folder, gpkg_layer_name, display_name=None):
    """
    Save any QgsVectorLayer into the project GeoPackage, replacing any
    existing layer of the same name. Returns the reloaded disk-backed layer.
    """
    gpkg_path = os.path.join(folder, GPKG_FILENAME)
    _write_to_gpkg(layer, gpkg_path, gpkg_layer_name, append=os.path.exists(gpkg_path))
    uri = f"{gpkg_path}|layername={gpkg_layer_name}"
    return QgsVectorLayer(uri, display_name or layer.name(), "ogr")


# ---------------------------------------------------------------------------
# Schema definitions (memory layers — used only as templates for writing)
# ---------------------------------------------------------------------------

def _make_schema_domain():
    layer = QgsVectorLayer("Polygon?crs=EPSG:4326", LAYER_DOMAIN, "memory")
    layer.dataProvider().addAttributes([
        field_str("name"),
        field_double("delta_time"),
        field_double("max_sim_time"),
        field_int("stop_when_evac"),
    ])
    layer.updateFields()
    return layer


def _make_schema_destinations():
    layer = QgsVectorLayer("Point?crs=EPSG:4326", LAYER_DESTINATIONS, "memory")
    layer.dataProvider().addAttributes([
        field_str("name"),
        field_str("type"),
        field_double("max_flow"),
        field_int("max_vehicles"),
        field_int("max_people"),
        field_int("blocked"),
    ])
    layer.updateFields()
    return layer


def _make_schema_groups():
    layer = QgsVectorLayer("Polygon?crs=EPSG:4326", LAYER_GROUPS, "memory")
    layer.dataProvider().addAttributes([
        field_str("name"),
        field_str("demographics"),
        field_str("destinations"),
        field_str("dest_cdf"),
        field_str("resp_curves"),
        field_str("resp_cdf"),
        field_str("dest_choice"),
        field_int("is_default"),
    ])
    layer.updateFields()
    return layer


def _make_schema_demographics():
    layer = QgsVectorLayer("NoGeometry", TABLE_DEMOGRAPHICS, "memory")
    layer.dataProvider().addAttributes([
        field_str("name"),
        field_int("allow_more_cars"),   # 0/1
        field_int("max_cars"),
        field_double("max_cars_prob"),
    ])
    layer.updateFields()
    return layer


def _make_schema_curves():
    layer = QgsVectorLayer("NoGeometry", TABLE_CURVES, "memory")
    layer.dataProvider().addAttributes([
        field_str("name"),
        field_str("data"),   # JSON: [[time_s, cum_prob], ...]
    ])
    layer.updateFields()
    return layer


def _add_default_demographics(layer):
    import json
    from qgis.core import QgsFeature
    feat = QgsFeature(layer.fields())
    feat["name"]           = "default"
    feat["allow_more_cars"] = 1
    feat["max_cars"]       = 2
    feat["max_cars_prob"]  = 0.3
    layer.dataProvider().addFeature(feat)


def _add_default_curve(layer):
    import json
    from qgis.core import QgsFeature
    feat = QgsFeature(layer.fields())
    feat["name"] = "default_curve"
    feat["data"] = json.dumps(DEFAULT_CURVE_DATA)
    layer.dataProvider().addFeature(feat)


# ---------------------------------------------------------------------------
# Widget setup — must be reapplied after loading from disk (not in GeoPackage)
# ---------------------------------------------------------------------------

def _apply_widget_setups(layer, display_name):
    if display_name == LAYER_DESTINATIONS:
        _setup_value_map(layer, "type", ["Exit", "Refugee"])
        _setup_checkbox(layer, "blocked")
    elif display_name == LAYER_GROUPS:
        _setup_value_map(layer, "dest_choice", [
            "EvacGroupWeighted",
            "EvacGroupClosestEuclidean",
            "Random",
            "ClosestEuclidean",
        ])
        _setup_checkbox(layer, "is_default")
    elif display_name == LAYER_DOMAIN:
        _setup_checkbox(layer, "stop_when_evac")


def _field_index(layer, field_name):
    return layer.fields().indexOf(field_name)


def _setup_value_map(layer, field_name, values):
    idx = _field_index(layer, field_name)
    if idx < 0:
        return
    config = {"map": [{v: v} for v in values]}
    layer.setEditorWidgetSetup(idx, QgsEditorWidgetSetup("ValueMap", config))


def _setup_checkbox(layer, field_name):
    idx = _field_index(layer, field_name)
    if idx < 0:
        return
    layer.setEditorWidgetSetup(idx, QgsEditorWidgetSetup("CheckBox", {
        "CheckedState": "1", "UncheckedState": "0",
    }))


# ---------------------------------------------------------------------------
# Styles
# ---------------------------------------------------------------------------

def _apply_style_for(layer, display_name):
    if display_name == LAYER_DOMAIN:
        _apply_style(layer, (0.2, 0.6, 1.0, 0.15), (0.2, 0.6, 1.0, 1.0), 1.5)
    elif display_name == LAYER_DESTINATIONS:
        _apply_style(layer, (1.0, 0.4, 0.0, 1.0), (1.0, 0.4, 0.0, 1.0), 2.0)
    elif display_name == LAYER_GROUPS:
        _apply_style(layer, (0.2, 0.8, 0.3, 0.2), (0.2, 0.8, 0.3, 1.0), 1.0)


def _apply_style(layer, fill_rgba, stroke_rgba, stroke_width):
    try:
        from PyQt5.QtGui import QColor

        def _c(rgba):
            r, g, b, a = rgba
            return QColor(int(r * 255), int(g * 255), int(b * 255), int(a * 255))

        renderer = layer.renderer()
        if renderer is None:
            return
        sym = renderer.symbol()
        if sym is None:
            return
        sl = sym.symbolLayer(0)
        if hasattr(sl, "setFillColor"):
            sl.setFillColor(_c(fill_rgba))
            sl.setStrokeColor(_c(stroke_rgba))
            sl.setStrokeWidth(stroke_width)
        elif hasattr(sl, "setColor"):
            sl.setColor(_c(fill_rgba))
    except Exception:
        pass
