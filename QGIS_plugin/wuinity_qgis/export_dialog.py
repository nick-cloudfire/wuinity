"""
Export dialog — General simulation settings plus per-module configuration tabs.
"""

import os
import subprocess

from PyQt5.QtWidgets import (
    QDialog, QDialogButtonBox, QFormLayout, QVBoxLayout, QHBoxLayout,
    QLineEdit, QDoubleSpinBox, QSpinBox, QCheckBox, QPushButton,
    QFileDialog, QLabel, QMessageBox, QTabWidget, QWidget, QComboBox,
)
from PyQt5.QtCore import Qt
from qgis.core import QgsProject, QgsTask, QgsApplication, Qgis, QgsMessageLog
from .layers import get_domain_layer, get_project_folder
from . import exporter


class ExportDialog(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Export WUInity Input Files")
        self.setMinimumWidth(500)
        self.setMinimumHeight(520)
        self._pop_task = None  # keep alive until task finishes
        self._build_ui()
        self._populate_defaults()

    # ------------------------------------------------------------------
    # UI construction
    # ------------------------------------------------------------------

    def _build_ui(self):
        main = QVBoxLayout(self)
        self._tabs = QTabWidget()
        main.addWidget(self._tabs)

        self._tabs.addTab(self._build_general_tab(),    "General")
        self._tabs.addTab(self._build_population_tab(), "Population")
        self._tabs.addTab(self._build_pedestrian_tab(), "Pedestrian")
        self._tabs.addTab(self._build_traffic_tab(),    "Traffic")
        self._tabs.addTab(self._build_wildfire_tab(),   "Wildfire")
        self._tabs.addTab(self._build_smoke_tab(),      "Smoke")
        self._tabs.addTab(self._build_trigger_tab(),    "Trigger Buffer")

        buttons = QDialogButtonBox(Qt.Horizontal)
        buttons.addButton("Export", QDialogButtonBox.AcceptRole)
        buttons.addButton(QDialogButtonBox.Cancel)
        buttons.accepted.connect(self._on_export)
        buttons.rejected.connect(self.reject)
        main.addWidget(buttons)

    # ── General tab ───────────────────────────────────────────────────

    def _build_general_tab(self):
        w = QWidget()
        form = QFormLayout(w)
        form.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)

        folder_row = QHBoxLayout()
        self.folder_edit = QLineEdit()
        browse_btn = QPushButton("…")
        browse_btn.setFixedWidth(28)
        browse_btn.clicked.connect(self._browse_output_folder)
        folder_row.addWidget(self.folder_edit)
        folder_row.addWidget(browse_btn)
        form.addRow("Output folder:", folder_row)

        self.name_edit = QLineEdit()
        form.addRow("Simulation name:", self.name_edit)

        self.delta_spin = QDoubleSpinBox()
        self.delta_spin.setRange(0.01, 60.0)
        self.delta_spin.setValue(1.0)
        self.delta_spin.setSuffix(" s")
        self.delta_spin.setDecimals(2)
        form.addRow("Time step (ΔT):", self.delta_spin)

        self.maxtime_spin = QDoubleSpinBox()
        self.maxtime_spin.setRange(60.0, 86400.0)
        self.maxtime_spin.setValue(7200.0)
        self.maxtime_spin.setSuffix(" s")
        self.maxtime_spin.setDecimals(0)
        form.addRow("Max sim time:", self.maxtime_spin)

        self.stop_check = QCheckBox()
        self.stop_check.setChecked(True)
        form.addRow("Stop when evacuated:", self.stop_check)

        return w

    # ── Population tab ────────────────────────────────────────────────

    def _build_population_tab(self):
        w = QWidget()
        form = QFormLayout(w)
        form.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)

        form.addRow(QLabel("<b>Population generation (GPW + PREACTcli)</b>"))

        self.gpw_folder_edit = self._file_row(form, "GPW folder:", None, folder=True)

        self.preact_exe_edit = self._file_row(
            form, "PREACTcli exe:",
            "Executable (*.exe);;All files (*)"
        )

        hh_row = QHBoxLayout()
        self.pop_minhh = QSpinBox()
        self.pop_minhh.setRange(1, 20)
        self.pop_minhh.setValue(1)
        self.pop_maxhh = QSpinBox()
        self.pop_maxhh.setRange(1, 20)
        self.pop_maxhh.setValue(5)
        hh_row.addWidget(QLabel("min"))
        hh_row.addWidget(self.pop_minhh)
        hh_row.addWidget(QLabel("max"))
        hh_row.addWidget(self.pop_maxhh)
        form.addRow("Household size:", hh_row)

        gen_btn = QPushButton("Generate Population from GPW + OSM")
        gen_btn.clicked.connect(self._generate_population)
        form.addRow("", gen_btn)

        self.pop_status_label = QLabel("")
        self.pop_status_label.setWordWrap(True)
        self.pop_status_label.setStyleSheet("color: #555; font-size: 11px;")
        form.addRow("", self.pop_status_label)

        form.addRow(QLabel("<b>Population file for .wui export</b>"))

        self.pop_file_edit = self._file_row(
            form, "Population file:",
            "CSV files (*.csv);;All files (*)"
        )

        self.pop_cull_check = QCheckBox()
        self.pop_cull_check.setChecked(True)
        form.addRow("Cull outside groups:", self.pop_cull_check)

        return w

    # ── Pedestrian tab ────────────────────────────────────────────────

    def _build_pedestrian_tab(self):
        w = QWidget()
        form = QFormLayout(w)
        form.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)

        self.ped_enabled = QCheckBox()
        form.addRow("Enabled:", self.ped_enabled)

        self.ped_module_combo = QComboBox()
        self.ped_module_combo.addItems(["MacroHouseholdSim"])
        form.addRow("Module:", self.ped_module_combo)

        speed_row = QHBoxLayout()
        self.ped_speed_min = QDoubleSpinBox()
        self.ped_speed_min.setRange(0.1, 10.0)
        self.ped_speed_min.setValue(0.5)
        self.ped_speed_min.setDecimals(2)
        self.ped_speed_min.setSuffix(" m/s")
        self.ped_speed_max = QDoubleSpinBox()
        self.ped_speed_max.setRange(0.1, 10.0)
        self.ped_speed_max.setValue(1.5)
        self.ped_speed_max.setDecimals(2)
        self.ped_speed_max.setSuffix(" m/s")
        speed_row.addWidget(QLabel("min"))
        speed_row.addWidget(self.ped_speed_min)
        speed_row.addWidget(QLabel("max"))
        speed_row.addWidget(self.ped_speed_max)
        form.addRow("Walking speed:", speed_row)

        self.ped_speed_mod = QDoubleSpinBox()
        self.ped_speed_mod.setRange(0.0, 10.0)
        self.ped_speed_mod.setValue(1.0)
        self.ped_speed_mod.setDecimals(2)
        form.addRow("Speed modifier:", self.ped_speed_mod)

        self.ped_dist_mod = QDoubleSpinBox()
        self.ped_dist_mod.setRange(0.0, 10.0)
        self.ped_dist_mod.setValue(1.0)
        self.ped_dist_mod.setDecimals(2)
        form.addRow("Distance modifier:", self.ped_dist_mod)

        return w

    # ── Traffic tab ───────────────────────────────────────────────────

    def _build_traffic_tab(self):
        w = QWidget()
        form = QFormLayout(w)
        form.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)

        self.traffic_enabled = QCheckBox()
        form.addRow("Enabled:", self.traffic_enabled)

        self.traffic_module_combo = QComboBox()
        self.traffic_module_combo.addItems(["SUMO", "MacroTrafficSim", "CityFlow"])
        form.addRow("Module:", self.traffic_module_combo)

        self.traffic_visibility = QCheckBox()
        form.addRow("Visibility affects speed:", self.traffic_visibility)

        form.addRow(QLabel("<b>SUMO settings</b>"))

        self.sumo_cfg_edit = self._file_row(
            form, "SUMO config file:",
            "SUMO configuration (*.sumocfg);;All files (*)"
        )

        self.sumo_smoke_alpha = QDoubleSpinBox()
        self.sumo_smoke_alpha.setRange(0.0, 100.0)
        self.sumo_smoke_alpha.setValue(0.5)
        self.sumo_smoke_alpha.setDecimals(4)
        form.addRow("Smoke alpha:", self.sumo_smoke_alpha)

        self.sumo_smoke_beta = QDoubleSpinBox()
        self.sumo_smoke_beta.setRange(0.0, 100.0)
        self.sumo_smoke_beta.setValue(0.012)
        self.sumo_smoke_beta.setDecimals(4)
        form.addRow("Smoke beta:", self.sumo_smoke_beta)

        return w

    # ── Wildfire tab ──────────────────────────────────────────────────

    def _build_wildfire_tab(self):
        w = QWidget()
        form = QFormLayout(w)
        form.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)

        self.fire_enabled = QCheckBox()
        form.addRow("Enabled:", self.fire_enabled)

        self.fire_module_combo = QComboBox()
        self.fire_module_combo.addItems(["AscImport", "FireCell", "CellParticleHybrid"])
        form.addRow("Module:", self.fire_module_combo)

        self.fire_lcp_edit = self._file_row(
            form, "LCP file (FireCell):",
            "Landscape files (*.lcp);;All files (*)"
        )

        form.addRow(QLabel("<b>AscImport file paths</b>"))

        self.asc_root_edit  = self._file_row(form, "Root folder:", None, folder=True)
        self.asc_toa_edit   = self._file_row(form, "Time of arrival:", "ASC files (*.asc);;All files (*)")
        self.asc_ros_edit   = self._file_row(form, "Rate of spread:", "ASC files (*.asc);;All files (*)")
        self.asc_sd_edit    = self._file_row(form, "Spread direction:", "ASC files (*.asc);;All files (*)")
        self.asc_fi_edit    = self._file_row(form, "Fireline intensity:", "ASC files (*.asc);;All files (*)")
        self.asc_wx_edit    = self._file_row(form, "Weather stream:", "ASC files (*.asc);;All files (*)")

        return w

    # ── Smoke tab ─────────────────────────────────────────────────────

    def _build_smoke_tab(self):
        w = QWidget()
        form = QFormLayout(w)
        form.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)

        self.smoke_enabled = QCheckBox()
        form.addRow("Enabled:", self.smoke_enabled)

        self.smoke_module_combo = QComboBox()
        self.smoke_module_combo.addItems([
            "GlobalSmoke", "AdvectDiffuseMixingLayer", "AdvectDiffuse3D", "Lagrangian"
        ])
        form.addRow("Module:", self.smoke_module_combo)

        form.addRow(QLabel("<b>GlobalSmoke</b>"))
        self.smoke_extinction_edit = self._file_row(
            form, "Extinction file:",
            "ASC files (*.asc);;All files (*)"
        )

        form.addRow(QLabel("<b>AdvectDiffuse settings</b>"))
        self.smoke_mixing_height = QDoubleSpinBox()
        self.smoke_mixing_height.setRange(1.0, 10000.0)
        self.smoke_mixing_height.setValue(500.0)
        self.smoke_mixing_height.setDecimals(1)
        self.smoke_mixing_height.setSuffix(" m")
        form.addRow("Mixing layer height:", self.smoke_mixing_height)

        form.addRow(QLabel("<b>Lagrangian settings</b>"))
        self.smoke_particles_spin = QSpinBox()
        self.smoke_particles_spin.setRange(1, 100000)
        self.smoke_particles_spin.setValue(100)
        form.addRow("Particles per fire cell:", self.smoke_particles_spin)

        return w

    # ── Trigger Buffer tab ────────────────────────────────────────────

    def _build_trigger_tab(self):
        w = QWidget()
        form = QFormLayout(w)
        form.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)

        self.trig_enabled = QCheckBox()
        form.addRow("Enabled:", self.trig_enabled)

        self.trig_module_combo = QComboBox()
        self.trig_module_combo.addItems(["kPERIL", "BackwardsFireCell2"])
        form.addRow("Module:", self.trig_module_combo)

        form.addRow(QLabel("<b>Evacuation settings</b>"))

        self.evac_order_start = QDoubleSpinBox()
        self.evac_order_start.setRange(0.0, 86400.0)
        self.evac_order_start.setValue(0.0)
        self.evac_order_start.setDecimals(0)
        self.evac_order_start.setSuffix(" s")
        form.addRow("Evac order start:", self.evac_order_start)

        form.addRow(QLabel("<b>kPERIL settings</b>"))

        self.kperil_windspeed = QDoubleSpinBox()
        self.kperil_windspeed.setRange(0.0, 200.0)
        self.kperil_windspeed.setValue(5.0)
        self.kperil_windspeed.setDecimals(2)
        self.kperil_windspeed.setSuffix(" m/s")
        form.addRow("Midflame windspeed:", self.kperil_windspeed)

        self.kperil_ros_from_behave = QCheckBox()
        form.addRow("Calculate ROS from Behave:", self.kperil_ros_from_behave)

        self.kperil_fuel_moisture_edit = self._file_row(
            form, "Initial fuel moisture:",
            "ASC files (*.asc);;All files (*)"
        )

        self.kperil_output_name = QLineEdit()
        self.kperil_output_name.setText("trigger_buffer")
        form.addRow("Output name:", self.kperil_output_name)

        form.addRow(QLabel("<b>BackwardsFireCell2 settings</b>"))
        self.trig_buffer_file_edit = self._file_row(
            form, "Trigger buffer file:",
            "ASC files (*.asc);;All files (*)"
        )

        return w

    # ------------------------------------------------------------------
    # Population generation
    # ------------------------------------------------------------------

    def _generate_population(self):
        gpw    = self.gpw_folder_edit.text().strip()
        preact = self.preact_exe_edit.text().strip()

        if not gpw or not os.path.isdir(gpw):
            QMessageBox.warning(self, "WUInity", "Please select a valid GPW folder.")
            return
        if not preact or not os.path.isfile(preact):
            QMessageBox.warning(self, "WUInity", "Please select the PREACTcli executable.")
            return

        folder = get_project_folder()
        if not folder:
            QMessageBox.warning(
                self, "WUInity",
                "No project folder set. Run 'New WUInity Project' first."
            )
            return

        # Persist settings for next session
        QgsProject.instance().writeEntry("wuinity", "gpw_folder",  gpw)
        QgsProject.instance().writeEntry("wuinity", "preact_exe",  preact)
        QgsProject.instance().writeEntry("wuinity", "pop_minhh",   str(self.pop_minhh.value()))
        QgsProject.instance().writeEntry("wuinity", "pop_maxhh",   str(self.pop_maxhh.value()))

        from . import osm as osm_mod
        osm_path = os.path.join(folder, osm_mod.OSM_XML_FILENAME)
        pop_csv  = os.path.join(folder, "population.csv")

        if not os.path.isfile(osm_path):
            self.pop_status_label.setText(
                "OSM file not found — run 'Import OSM Roads' first, then retry."
            )
            self.pop_status_label.setStyleSheet("color: red; font-size: 11px;")
            return

        self.pop_status_label.setText("Running PREACTcli…")
        self.pop_status_label.setStyleSheet("color: #555; font-size: 11px;")

        self._pop_task = _PopGenTask(
            osm_path   = osm_path,
            preact_exe = preact,
            gpw_dir    = gpw,
            pop_csv    = pop_csv,
            minhh      = self.pop_minhh.value(),
            maxhh      = self.pop_maxhh.value(),
            on_done    = self._on_pop_gen_done,
        )
        QgsApplication.taskManager().addTask(self._pop_task)

    def _on_pop_gen_done(self, success, message, pop_csv):
        self._pop_task = None
        if success:
            self.pop_file_edit.setText(pop_csv)
            self.pop_status_label.setText(f"Done — {pop_csv}")
            self.pop_status_label.setStyleSheet("color: green; font-size: 11px;")
        else:
            self.pop_status_label.setText(f"Failed: {message}")
            self.pop_status_label.setStyleSheet("color: red; font-size: 11px;")

    # ------------------------------------------------------------------
    # Helper — file / folder picker row
    # ------------------------------------------------------------------

    def _file_row(self, form, label, file_filter, folder=False):
        edit = QLineEdit()
        btn = QPushButton("…")
        btn.setFixedWidth(28)
        row = QHBoxLayout()
        row.addWidget(edit)
        row.addWidget(btn)
        form.addRow(label, row)
        if folder:
            btn.clicked.connect(lambda _=None, e=edit: self._pick_folder(e))
        else:
            btn.clicked.connect(lambda _=None, e=edit, f=file_filter: self._pick_file(e, f))
        return edit

    def _pick_file(self, edit, file_filter):
        start = os.path.dirname(edit.text()) or self.folder_edit.text() or os.path.expanduser("~")
        path, _ = QFileDialog.getOpenFileName(self, "Select file", start, file_filter or "All files (*)")
        if path:
            edit.setText(path)

    def _pick_folder(self, edit):
        start = edit.text() or self.folder_edit.text() or os.path.expanduser("~")
        path = QFileDialog.getExistingDirectory(self, "Select folder", start)
        if path:
            edit.setText(path)

    # ------------------------------------------------------------------
    # Defaults
    # ------------------------------------------------------------------

    def _populate_defaults(self):
        domain = get_domain_layer()
        if domain:
            feats = list(domain.getFeatures())
            if feats and feats[0]["name"]:
                self.name_edit.setText(feats[0]["name"])
        if not self.name_edit.text():
            self.name_edit.setText(QgsProject.instance().title() or "simulation")

        last = QgsProject.instance().readEntry("wuinity", "last_export_dir", "")[0]
        if not last:
            last = get_project_folder() or ""
        if last:
            self.folder_edit.setText(last)

        # Population tab defaults
        gpw = QgsProject.instance().readEntry("wuinity", "gpw_folder", "")[0]
        if not gpw:
            # Fall back to the known local GPW data folder
            gpw = r"D:\WUINITY\dataSources\GPW_ASC_8SECTORS"
        if os.path.isdir(gpw):
            self.gpw_folder_edit.setText(gpw)

        preact = QgsProject.instance().readEntry("wuinity", "preact_exe", "")[0]
        if not preact:
            preact = r"D:\WUINITY\tools\PREACTcli\bin\Release\net8.0\PREACTcli.exe"
        if os.path.isfile(preact):
            self.preact_exe_edit.setText(preact)

        minhh = QgsProject.instance().readEntry("wuinity", "pop_minhh", "1")[0]
        maxhh = QgsProject.instance().readEntry("wuinity", "pop_maxhh", "5")[0]
        try:
            self.pop_minhh.setValue(int(minhh))
            self.pop_maxhh.setValue(int(maxhh))
        except ValueError:
            pass

        # Pre-fill population CSV if it already exists in the project folder
        folder = get_project_folder()
        if folder:
            candidate = os.path.join(folder, "population.csv")
            if os.path.isfile(candidate):
                self.pop_file_edit.setText(candidate)

    def _browse_output_folder(self):
        start = self.folder_edit.text() or os.path.expanduser("~")
        folder = QFileDialog.getExistingDirectory(self, "Select output folder", start)
        if folder:
            self.folder_edit.setText(folder)

    # ------------------------------------------------------------------
    # Export
    # ------------------------------------------------------------------

    def _on_export(self):
        folder = self.folder_edit.text().strip()
        name   = self.name_edit.text().strip()

        if not folder:
            QMessageBox.warning(self, "WUInity", "Please choose an output folder.")
            return
        if not name:
            QMessageBox.warning(self, "WUInity", "Please enter a simulation name.")
            return

        try:
            wui_path = exporter.export(
                output_dir          = folder,
                sim_name            = name,
                delta_time          = self.delta_spin.value(),
                max_sim_time        = self.maxtime_spin.value(),
                stop_when_evacuated = self.stop_check.isChecked(),
                module_config       = self._collect_module_config(),
            )
        except exporter.ExportError as e:
            QMessageBox.critical(self, "Export failed", str(e))
            return
        except Exception as e:
            QMessageBox.critical(self, "Export failed", f"Unexpected error:\n{e}")
            return

        QgsProject.instance().writeEntry("wuinity", "last_export_dir", folder)
        QMessageBox.information(self, "Export complete", f"Written to:\n{wui_path}")
        self.accept()

    def _collect_module_config(self):
        return {
            "population": {
                "file":              self.pop_file_edit.text().strip(),
                "cull_outside":      self.pop_cull_check.isChecked(),
            },
            "pedestrian": {
                "enabled":           self.ped_enabled.isChecked(),
                "module":            self.ped_module_combo.currentText(),
                "speed_min":         self.ped_speed_min.value(),
                "speed_max":         self.ped_speed_max.value(),
                "speed_modifier":    self.ped_speed_mod.value(),
                "distance_modifier": self.ped_dist_mod.value(),
            },
            "traffic": {
                "enabled":           self.traffic_enabled.isChecked(),
                "module":            self.traffic_module_combo.currentText(),
                "visibility_speed":  self.traffic_visibility.isChecked(),
                "sumo_cfg":          self.sumo_cfg_edit.text().strip(),
                "smoke_alpha":       self.sumo_smoke_alpha.value(),
                "smoke_beta":        self.sumo_smoke_beta.value(),
            },
            "wildfire": {
                "enabled":           self.fire_enabled.isChecked(),
                "module":            self.fire_module_combo.currentText(),
                "lcp_file":          self.fire_lcp_edit.text().strip(),
                "asc_root":          self.asc_root_edit.text().strip(),
                "toa_file":          self.asc_toa_edit.text().strip(),
                "ros_file":          self.asc_ros_edit.text().strip(),
                "sd_file":           self.asc_sd_edit.text().strip(),
                "fi_file":           self.asc_fi_edit.text().strip(),
                "wx_file":           self.asc_wx_edit.text().strip(),
            },
            "smoke": {
                "enabled":           self.smoke_enabled.isChecked(),
                "module":            self.smoke_module_combo.currentText(),
                "extinction_file":   self.smoke_extinction_edit.text().strip(),
                "mixing_height":     self.smoke_mixing_height.value(),
                "particles":         self.smoke_particles_spin.value(),
            },
            "trigger_buffer": {
                "enabled":           self.trig_enabled.isChecked(),
                "module":            self.trig_module_combo.currentText(),
                "evac_order_start":  self.evac_order_start.value(),
                "midflame_wind":     self.kperil_windspeed.value(),
                "ros_from_behave":   self.kperil_ros_from_behave.isChecked(),
                "fuel_moisture":     self.kperil_fuel_moisture_edit.text().strip(),
                "output_name":       self.kperil_output_name.text().strip(),
                "trigger_file":      self.trig_buffer_file_edit.text().strip(),
            },
        }


# ---------------------------------------------------------------------------
# Background task: OSM XML download + PREACTcli population generation
# ---------------------------------------------------------------------------

class _PopGenTask(QgsTask):
    """
    Runs PREACTcli global-gpw-to-pop in the background.
    The OSM XML file must already exist (written by the road import step).
    If it is missing the task fails with an informative message.
    """
    def __init__(self, osm_path, preact_exe, gpw_dir, pop_csv, minhh, maxhh, on_done):
        super().__init__("Generating WUInity population", QgsTask.CanCancel)
        self._osm_path   = osm_path
        self._preact_exe = preact_exe
        self._gpw_dir    = gpw_dir
        self._pop_csv    = pop_csv
        self._minhh      = minhh
        self._maxhh      = maxhh
        self._on_done    = on_done
        self._error      = None

    def run(self):
        if not os.path.isfile(self._osm_path):
            self._error = (
                f"OSM file not found: {self._osm_path}\n"
                "Run 'Import OSM Roads' first — it saves the OSM data "
                "which is reused here."
            )
            return False

        QgsMessageLog.logMessage("Running PREACTcli global-gpw-to-pop…", "WUInity", Qgis.Info)
        try:
            result = subprocess.run(
                [
                    self._preact_exe,
                    "global-gpw-to-pop",
                    "--gpw",   self._gpw_dir,
                    "--osm",   self._osm_path,
                    "--out",   self._pop_csv,
                    "--minhh", str(self._minhh),
                    "--maxhh", str(self._maxhh),
                ],
                capture_output=True,
                text=True,
                timeout=600,
            )
        except FileNotFoundError:
            self._error = f"PREACTcli not found: {self._preact_exe}"
            return False
        except subprocess.TimeoutExpired:
            self._error = "PREACTcli timed out after 10 minutes"
            return False

        if result.stdout:
            QgsMessageLog.logMessage(result.stdout.strip(), "WUInity", Qgis.Info)
        if result.returncode != 0:
            self._error = result.stderr.strip() or f"PREACTcli exited with code {result.returncode}"
            return False

        self.setProgress(100)
        return True

    def finished(self, success):
        self._on_done(success, self._error or "", self._pop_csv)
