"""
Compatibility helpers for QGIS API changes across versions.

QGIS 3.38+ deprecated:
  - QgsField(name, QVariant.Type)       → QgsField(name, QMetaType.Type)
  - QgsVectorFileWriter.writeAsVectorFormatV2 → writeAsVectorFormatV3

We detect which form is available once at import time.
"""

from qgis.core import QgsField, QgsVectorFileWriter, QgsCoordinateTransformContext

try:
    from PyQt5.QtCore import QMetaType

    def field_str(name):
        return QgsField(name, QMetaType.Type.QString)

    def field_int(name):
        return QgsField(name, QMetaType.Type.Int)

    def field_double(name):
        return QgsField(name, QMetaType.Type.Double)

except AttributeError:
    # Older PyQt5 / QGIS < 3.38 — QMetaType.Type enum not yet exposed
    from PyQt5.QtCore import QVariant

    def field_str(name):
        return QgsField(name, QVariant.String)

    def field_int(name):
        return QgsField(name, QVariant.Int)

    def field_double(name):
        return QgsField(name, QVariant.Double)


# ---------------------------------------------------------------------------
# Vector file writer — V3 available since 3.20, V2 deprecated in 3.38
# ---------------------------------------------------------------------------

def write_vector_layer(layer, path, options):
    """
    Write layer to file using the best available API.
    Returns (QgsVectorFileWriter.NoError, msg) on success.
    """
    ctx = QgsCoordinateTransformContext()
    try:
        error, msg, _, _ = QgsVectorFileWriter.writeAsVectorFormatV3(
            layer, path, ctx, options
        )
    except AttributeError:
        error, msg = QgsVectorFileWriter.writeAsVectorFormatV2(
            layer, path, ctx, options
        )
    return error, msg
