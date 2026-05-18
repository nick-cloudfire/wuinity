"""
Main plugin class — registers the toolbar and actions.
"""

from qgis.PyQt.QtWidgets import QAction, QMessageBox
from qgis.core import QgsProject


class WUInityPlugin:
    def __init__(self, iface):
        self.iface = iface
        self._toolbar = None
        self._actions = []
        self._osm_task = None  # keep alive until task finishes

    # ------------------------------------------------------------------
    # QGIS plugin lifecycle
    # ------------------------------------------------------------------

    def initGui(self):
        self._toolbar = self.iface.addToolBar("WUInity")
        self._toolbar.setObjectName("WUInityToolbar")

        self._add_action(
            text="New WUInity Project",
            tooltip="Create WUI Domain, Destinations and Evacuation Group layers",
            callback=self._new_project,
        )
        self._add_action(
            text="Import OSM Roads",
            tooltip="Download OSM road network for the simulation domain",
            callback=self._import_osm_roads,
        )
        self._toolbar.addSeparator()
        self._add_action(
            text="Edit Selected Group",
            tooltip="Open the group editor for the currently selected evacuation group feature",
            callback=self._edit_selected_group,
        )
        self._add_action(
            text="Manage Settings",
            tooltip="Manage demographics profiles and response curves",
            callback=self._manage_settings,
        )
        self._toolbar.addSeparator()
        self._add_action(
            text="Export to .wui",
            tooltip="Export simulation input files to disk",
            callback=self._export,
        )

    def unload(self):
        for action in self._actions:
            self.iface.removeToolBarIcon(action)
        if self._toolbar:
            self._toolbar.deleteLater()
            self._toolbar = None

    # ------------------------------------------------------------------
    # Actions
    # ------------------------------------------------------------------

    def _new_project(self):
        from .layers import (
            create_project_layers_on_disk, get_domain_layer,
            get_project_folder, LAYER_DOMAIN, GPKG_FILENAME,
        )
        import os
        from PyQt5.QtWidgets import QFileDialog, QMessageBox

        # Ask for the project folder
        last = get_project_folder() or ""
        folder = QFileDialog.getExistingDirectory(
            self.iface.mainWindow(),
            "Select WUInity project folder",
            last,
        )
        if not folder:
            return  # user cancelled

        # Warn if an existing GeoPackage is found but layers aren't loaded yet
        gpkg = os.path.join(folder, GPKG_FILENAME)
        if os.path.exists(gpkg) and not get_domain_layer():
            reply = QMessageBox.question(
                self.iface.mainWindow(),
                "WUInity",
                f"Found existing layers in:\n{gpkg}\n\nLoad them?",
                QMessageBox.Yes | QMessageBox.No,
            )
            if reply == QMessageBox.No:
                return

        try:
            created = create_project_layers_on_disk(folder)
        except Exception as e:
            self.iface.messageBar().pushCritical("WUInity", f"Failed to create layers: {e}")
            return

        if created:
            names = ", ".join(l.name() for l in created)
            self.iface.messageBar().pushSuccess("WUInity", f"Layers ready: {names}")
        else:
            self.iface.messageBar().pushInfo("WUInity", "All WUInity layers already loaded.")

        # Connect group layer signal so the dialog pops up on every new polygon
        from .layers import get_groups_layer
        groups = get_groups_layer()
        if groups:
            try:
                groups.featureAdded.disconnect(self._on_group_feature_added)
            except Exception:
                pass
            groups.featureAdded.connect(self._on_group_feature_added)

        domain = get_domain_layer()
        if domain and domain.featureCount() == 0:
            self.iface.setActiveLayer(domain)
            domain.startEditing()
            self.iface.messageBar().pushInfo(
                "WUInity",
                f"'{LAYER_DOMAIN}' is active — draw your simulation domain polygon."
            )

    def _import_osm_roads(self):
        from .layers import get_domain_layer, LAYER_DOMAIN
        from . import osm

        domain = get_domain_layer()
        if domain is None:
            self.iface.messageBar().pushWarning(
                "WUInity", "No domain layer found. Run 'New WUInity Project' first."
            )
            return

        # Commit any pending edits so features are visible to the provider
        if domain.isEditable():
            if not domain.commitChanges():
                self.iface.messageBar().pushWarning(
                    "WUInity", "Could not commit domain edits. Fix any geometry errors and try again."
                )
                return

        if domain.featureCount() == 0:
            self.iface.messageBar().pushWarning(
                "WUInity", f"'{LAYER_DOMAIN}' has no features. Draw your domain polygon first."
            )
            return

        # Remove any existing OSM roads layer before re-downloading
        existing = QgsProject.instance().mapLayersByName(osm.LAYER_OSM_ROADS)
        for lyr in existing:
            QgsProject.instance().removeMapLayer(lyr)

        self.iface.messageBar().pushInfo(
            "WUInity", "Downloading OSM roads (running in background)…"
        )

        from .layers import get_project_folder
        import os
        folder = get_project_folder()
        if folder:
            xml_path = os.path.join(folder, osm.OSM_XML_FILENAME)
        else:
            xml_path = None
            self.iface.messageBar().pushWarning(
                "WUInity",
                "No project folder set — OSM XML will not be saved to disk. "
                "Run 'New WUInity Project' first if you need population generation."
            )

        self._osm_task = osm.start_download(
            domain_layer=domain,
            on_success=self._on_osm_success,
            on_failure=self._on_osm_failure,
            save_xml_path=xml_path,
        )

    def _on_osm_success(self, roads_layer, exits, utm_offset):
        self._osm_task = None
        from .layers import get_project_folder, save_layer_to_gpkg
        from . import osm as osm_mod
        LAYER_OSM_ROADS = osm_mod.LAYER_OSM_ROADS

        # Save to project GeoPackage if a project folder is set
        folder = get_project_folder()
        if folder:
            try:
                roads_layer = save_layer_to_gpkg(
                    roads_layer, folder,
                    osm_mod.GPKG_LAYER_ROADS,
                    LAYER_OSM_ROADS,
                )
            except Exception as e:
                self.iface.messageBar().pushWarning(
                    "WUInity", f"Could not save OSM roads to GeoPackage: {e}"
                )

        # Remove any stale version already in the project
        for lyr in QgsProject.instance().mapLayersByName(LAYER_OSM_ROADS):
            QgsProject.instance().removeMapLayer(lyr)

        QgsProject.instance().addMapLayer(roads_layer)
        self._enable_snapping(roads_layer)

        # Store UTM offset in project metadata for the exporter
        easting, northing, epsg = utm_offset
        QgsProject.instance().writeEntry("wuinity", "utm_offset_e", str(easting))
        QgsProject.instance().writeEntry("wuinity", "utm_offset_n", str(northing))
        QgsProject.instance().writeEntry("wuinity", "utm_epsg",     str(epsg))

        count = roads_layer.featureCount()
        self.iface.messageBar().pushSuccess(
            "WUInity",
            f"OSM roads loaded — {count:,} segments, {len(exits)} exit candidate(s) detected."
        )

        # Offer to add detected exits to the Destinations layer
        if exits:
            self._offer_add_exits(exits)

    def _offer_add_exits(self, exits):
        from PyQt5.QtWidgets import QMessageBox
        from .layers import get_destinations_layer

        dest_layer = get_destinations_layer()
        if dest_layer is None:
            return

        reply = QMessageBox.question(
            self.iface.mainWindow(),
            "WUInity — Exit candidates",
            f"Detected {len(exits)} road exit candidate(s) on the domain boundary.\n"
            "Add them to the Destinations layer?",
            QMessageBox.Yes | QMessageBox.No,
        )
        if reply != QMessageBox.Yes:
            return

        from qgis.core import QgsFeature, QgsGeometry
        dest_layer.startEditing()
        for i, pt in enumerate(exits, start=1):
            feat = QgsFeature(dest_layer.fields())
            feat.setGeometry(QgsGeometry.fromPointXY(pt))
            feat["name"]    = f"exit_{i}"
            feat["type"]    = "Exit"
            feat["blocked"] = 0
            dest_layer.addFeature(feat)
        dest_layer.commitChanges()

        self.iface.messageBar().pushSuccess(
            "WUInity", f"Added {len(exits)} exit candidate(s) to Destinations layer."
        )

    def _on_osm_failure(self, message):
        self._osm_task = None
        self.iface.messageBar().pushCritical("WUInity", f"OSM download failed: {message}")

    def _on_group_feature_added(self, fid):
        """Called when the user finishes drawing a new evacuation group polygon."""
        from .layers import get_groups_layer
        from .group_dialog import GroupDialog

        layer = get_groups_layer()
        if layer is None or not layer.isEditable():
            return

        buf  = layer.editBuffer()
        feat = buf.addedFeatures().get(fid) if buf else None
        if feat is None or not feat.isValid():
            return

        dlg = GroupDialog(feat, layer, parent=self.iface.mainWindow())
        if dlg.exec_() == GroupDialog.Accepted:
            layer.changeAttributeValues(fid, dlg.get_attribute_map())

    def _edit_selected_group(self):
        from .layers import get_groups_layer
        from .group_dialog import GroupDialog

        layer = get_groups_layer()
        if layer is None:
            self.iface.messageBar().pushWarning("WUInity", "No evacuation groups layer found.")
            return

        selected = layer.selectedFeatures()
        if not selected:
            self.iface.messageBar().pushWarning(
                "WUInity", "Select a group feature first."
            )
            return

        feat = selected[0]
        dlg  = GroupDialog(feat, layer, parent=self.iface.mainWindow())
        if dlg.exec_() == GroupDialog.Accepted:
            attr_map = dlg.get_attribute_map()
            was_editing = layer.isEditable()
            if not was_editing:
                layer.startEditing()
            layer.changeAttributeValues(feat.id(), attr_map)
            if not was_editing:
                layer.commitChanges()
            self.iface.messageBar().pushSuccess("WUInity", "Group updated.")

    def _manage_settings(self):
        from .manage_dialog import ManageDialog
        dlg = ManageDialog(parent=self.iface.mainWindow())
        dlg.exec_()

    def _export(self):
        from .export_dialog import ExportDialog
        dlg = ExportDialog(self.iface.mainWindow())
        dlg.exec_()

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _add_action(self, text, tooltip, callback):
        action = QAction(text, self.iface.mainWindow())
        action.setToolTip(tooltip)
        action.triggered.connect(callback)
        self._toolbar.addAction(action)
        self._actions.append(action)
        return action

    def _enable_snapping(self, layer):
        """Turn on snapping to vertices and segments for the given layer."""
        try:
            from qgis.core import (
                QgsSnappingConfig, QgsTolerance,
                QgsSnappingConfig as SC,
            )
            cfg = QgsProject.instance().snappingConfig()
            cfg.setEnabled(True)
            cfg.setMode(QgsSnappingConfig.AdvancedConfiguration)
            ls = QgsSnappingConfig.IndividualLayerSettings(
                True,
                QgsSnappingConfig.VertexAndSegment,
                10,
                QgsTolerance.Pixels,
                1.0,
                1.0,
            )
            cfg.setIndividualLayerSettings(layer, ls)
            QgsProject.instance().setSnappingConfig(cfg)
        except Exception:
            pass  # snapping setup is a convenience — never crash on it
