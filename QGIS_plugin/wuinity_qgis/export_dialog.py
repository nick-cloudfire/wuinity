"""
Export dialog — General simulation settings plus per-module configuration tabs.
"""

import os
import subprocess

from PyQt5.QtWidgets import (
    QDialog, QDialogButtonBox, QFormLayout, QVBoxLayout, QHBoxLayout,
    QLineEdit, QDoubleSpinBox, QSpinBox, QCheckBox, QPushButton,
    QFileDialog, QLabel, QMessageBox, QTabWidget, QWidget, QComboBox,
    QStackedWidget, QDateTimeEdit,
)
from PyQt5.QtCore import Qt, QDateTime
from qgis.core import QgsProject, QgsTask, QgsApplication, Qgis, QgsMessageLog
from .layers import get_domain_layer, get_project_folder
from . import exporter


class ExportDialog(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Export WUInity Input Files")
        self.setMinimumWidth(500)
        self.setMinimumHeight(520)
        self._pop_task  = None
        self._sumo_task = None
        self._osm_full_task = None
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

        self.start_dt_edit = QDateTimeEdit()
        self.start_dt_edit.setDisplayFormat("yyyy-MM-dd HH:mm:ss")
        self.start_dt_edit.setCalendarPopup(True)
        self.start_dt_edit.setDateTime(QDateTime.currentDateTime().toUTC())
        form.addRow("Start date/time (UTC):", self.start_dt_edit)

        self.delta_spin = QDoubleSpinBox()
        self.delta_spin.setRange(0.01, 60.0)
        self.delta_spin.setValue(1.0)
        self.delta_spin.setSuffix(" s")
        self.delta_spin.setDecimals(2)
        form.addRow("Time step (ΔT):", self.delta_spin)

        self.maxtime_spin = QDoubleSpinBox()
        self.maxtime_spin.setRange(60.0, 86400.0 * 7)
        self.maxtime_spin.setValue(7200.0)
        self.maxtime_spin.setSuffix(" s")
        self.maxtime_spin.setDecimals(0)
        form.addRow("Duration (max sim time):", self.maxtime_spin)

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

        self.osm_xml_edit = self._file_row(
            form, "OSM XML file:",
            "OSM XML files (*.xml *.osm *.osm.xml);;All files (*)"
        )
        osm_dl_btn = QPushButton("Download OSM for Population…")
        osm_dl_btn.setToolTip(
            "Downloads roads + buildings + landuse from OpenStreetMap.\n"
            "This is larger than the road-only download but required for population generation."
        )
        osm_dl_btn.clicked.connect(self._download_osm_for_population)
        self._osm_dl_status = QLabel("")
        self._osm_dl_status.setWordWrap(True)
        self._osm_dl_status.setStyleSheet("color: #555; font-size: 11px;")
        form.addRow("", osm_dl_btn)
        form.addRow("", self._osm_dl_status)

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
        vbox = QVBoxLayout(w)
        vbox.setContentsMargins(8, 8, 8, 8)

        fixed = QFormLayout()
        fixed.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.ped_enabled = QCheckBox()
        fixed.addRow("Enabled:", self.ped_enabled)
        self.ped_module_combo = QComboBox()
        self.ped_module_combo.addItems(["MacroHouseholdSim"])
        fixed.addRow("Module:", self.ped_module_combo)
        vbox.addLayout(fixed)

        self._ped_stack = QStackedWidget()
        vbox.addWidget(self._ped_stack)
        vbox.addStretch()

        # Page 0: MacroHouseholdSim
        p = QWidget(); pf = QFormLayout(p); pf.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        speed_row = QHBoxLayout()
        self.ped_speed_min = QDoubleSpinBox()
        self.ped_speed_min.setRange(0.1, 10.0); self.ped_speed_min.setValue(0.5)
        self.ped_speed_min.setDecimals(2); self.ped_speed_min.setSuffix(" m/s")
        self.ped_speed_max = QDoubleSpinBox()
        self.ped_speed_max.setRange(0.1, 10.0); self.ped_speed_max.setValue(1.5)
        self.ped_speed_max.setDecimals(2); self.ped_speed_max.setSuffix(" m/s")
        speed_row.addWidget(QLabel("min")); speed_row.addWidget(self.ped_speed_min)
        speed_row.addWidget(QLabel("max")); speed_row.addWidget(self.ped_speed_max)
        pf.addRow("Walking speed:", speed_row)
        self.ped_speed_mod = QDoubleSpinBox()
        self.ped_speed_mod.setRange(0.0, 10.0); self.ped_speed_mod.setValue(1.0); self.ped_speed_mod.setDecimals(2)
        pf.addRow("Speed modifier:", self.ped_speed_mod)
        self.ped_dist_mod = QDoubleSpinBox()
        self.ped_dist_mod.setRange(0.0, 10.0); self.ped_dist_mod.setValue(1.0); self.ped_dist_mod.setDecimals(2)
        pf.addRow("Distance modifier:", self.ped_dist_mod)
        self._ped_stack.addWidget(p)

        self.ped_module_combo.currentIndexChanged.connect(self._ped_stack.setCurrentIndex)
        return w

    # ── Traffic tab ───────────────────────────────────────────────────

    def _build_traffic_tab(self):
        w = QWidget()
        vbox = QVBoxLayout(w)
        vbox.setContentsMargins(8, 8, 8, 8)

        fixed = QFormLayout()
        fixed.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.traffic_enabled = QCheckBox()
        fixed.addRow("Enabled:", self.traffic_enabled)
        self.traffic_module_combo = QComboBox()
        self.traffic_module_combo.addItems(["SUMO"])
        fixed.addRow("Module:", self.traffic_module_combo)
        self.traffic_visibility = QCheckBox()
        fixed.addRow("Visibility affects speed:", self.traffic_visibility)
        vbox.addLayout(fixed)

        self._traffic_stack = QStackedWidget()
        vbox.addWidget(self._traffic_stack)
        vbox.addStretch()

        # Page 0: SUMO
        p = QWidget(); pf = QFormLayout(p); pf.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)

        pf.addRow(QLabel("<b>Generate network from OSM</b>"))
        self.netconvert_exe_edit = self._file_row(pf, "netconvert exe:", "Executable (*.exe);;All files (*)")
        self._sumo_gen_btn = QPushButton("Generate SUMO Network from OSM")
        self._sumo_gen_btn.clicked.connect(self._generate_sumo_network)
        pf.addRow("", self._sumo_gen_btn)
        self._sumo_gen_status = QLabel("")
        self._sumo_gen_status.setWordWrap(True)
        self._sumo_gen_status.setStyleSheet("color: #555; font-size: 11px;")
        pf.addRow("", self._sumo_gen_status)

        pf.addRow(QLabel("<b>Config file</b>"))
        self.sumo_cfg_edit = self._file_row(pf, "SUMO config file:", "SUMO configuration (*.sumocfg);;All files (*)")

        pf.addRow(QLabel("<b>Smoke visibility parameters</b>"))
        self.sumo_smoke_alpha = QDoubleSpinBox()
        self.sumo_smoke_alpha.setRange(0.0, 100.0); self.sumo_smoke_alpha.setValue(0.5); self.sumo_smoke_alpha.setDecimals(4)
        pf.addRow("Smoke alpha:", self.sumo_smoke_alpha)
        self.sumo_smoke_beta = QDoubleSpinBox()
        self.sumo_smoke_beta.setRange(0.0, 100.0); self.sumo_smoke_beta.setValue(0.012); self.sumo_smoke_beta.setDecimals(4)
        pf.addRow("Smoke beta:", self.sumo_smoke_beta)
        self._traffic_stack.addWidget(p)

        self.traffic_module_combo.currentIndexChanged.connect(self._traffic_stack.setCurrentIndex)
        return w

    # ── Wildfire tab ──────────────────────────────────────────────────

    def _build_wildfire_tab(self):
        w = QWidget()
        vbox = QVBoxLayout(w)
        vbox.setContentsMargins(8, 8, 8, 8)

        fixed = QFormLayout()
        fixed.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.fire_enabled = QCheckBox()
        fixed.addRow("Enabled:", self.fire_enabled)
        self.fire_module_combo = QComboBox()
        self.fire_module_combo.addItems(["AscImport", "ElmClone"])
        fixed.addRow("Module:", self.fire_module_combo)
        vbox.addLayout(fixed)

        self._fire_stack = QStackedWidget()
        vbox.addWidget(self._fire_stack)
        vbox.addStretch()

        # Page 0: AscImport
        p = QWidget(); pf = QFormLayout(p); pf.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.asc_root_edit = self._file_row(pf, "Root folder:", None, folder=True)
        self.asc_toa_edit  = self._file_row(pf, "Time of arrival:", "ASC files (*.asc);;All files (*)")
        self.asc_ros_edit  = self._file_row(pf, "Rate of spread:", "ASC files (*.asc);;All files (*)")
        self.asc_sd_edit   = self._file_row(pf, "Spread direction:", "ASC files (*.asc);;All files (*)")
        self.asc_fi_edit   = self._file_row(pf, "Fireline intensity:", "ASC files (*.asc);;All files (*)")
        self.asc_wx_edit   = self._file_row(pf, "Weather stream:", "ASC files (*.asc);;All files (*)")
        self._fire_stack.addWidget(p)

        # Page 1: ElmClone (uses the FireCellInput parser)
        p = QWidget(); pf = QFormLayout(p); pf.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.fire_lcp_edit = self._file_row(pf, "LCP file:", "Landscape files (*.lcp);;All files (*)")

        pf.addRow(QLabel("<b>Spread rate model</b>"))
        self.fire_spread_model_combo = QComboBox()
        self.fire_spread_model_combo.addItems(["Behave", "CanadianFBP", "LookupROS"])
        pf.addRow("Model:", self.fire_spread_model_combo)

        self._fire_spread_stack = QStackedWidget()
        pf.addRow(self._fire_spread_stack)

        # Spread stack — page 0: Behave
        sp = QWidget(); spf = QFormLayout(sp); spf.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.fire_fuel_models_edit   = self._file_row(spf, "Fuel models file:", "All files (*)")
        self.fire_fuel_moisture_edit = self._file_row(spf, "Initial fuel moisture:", "Fuel moisture files (*.fmc);;All files (*)")
        self._fire_spread_stack.addWidget(sp)

        # Spread stack — page 1: CanadianFBP
        sp = QWidget(); spf = QFormLayout(sp); spf.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.fire_fbp_lookup_edit = self._file_row(spf, "FBP lookup table:", "All files (*)")
        self.fire_start_dc = QDoubleSpinBox()
        self.fire_start_dc.setRange(0.0, 999.0); self.fire_start_dc.setValue(15.0); self.fire_start_dc.setDecimals(1)
        spf.addRow("Start DC:", self.fire_start_dc)
        self.fire_start_dmc = QDoubleSpinBox()
        self.fire_start_dmc.setRange(0.0, 999.0); self.fire_start_dmc.setValue(6.0); self.fire_start_dmc.setDecimals(1)
        spf.addRow("Start DMC:", self.fire_start_dmc)
        self.fire_start_ffmc = QDoubleSpinBox()
        self.fire_start_ffmc.setRange(0.0, 101.0); self.fire_start_ffmc.setValue(85.0); self.fire_start_ffmc.setDecimals(1)
        spf.addRow("Start FFMC:", self.fire_start_ffmc)
        self.fire_start_hourly_ffmc = QDoubleSpinBox()
        self.fire_start_hourly_ffmc.setRange(0.0, 101.0); self.fire_start_hourly_ffmc.setValue(85.0); self.fire_start_hourly_ffmc.setDecimals(1)
        spf.addRow("Start hourly FFMC:", self.fire_start_hourly_ffmc)
        self._fire_spread_stack.addWidget(sp)

        # Spread stack — page 2: LookupROS
        sp = QWidget(); spf = QFormLayout(sp); spf.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.fire_ros_lookup_edit = self._file_row(spf, "ROS lookup table:", "CSV files (*.csv);;All files (*)")
        self._fire_spread_stack.addWidget(sp)

        self.fire_spread_model_combo.currentIndexChanged.connect(self._fire_spread_stack.setCurrentIndex)

        pf.addRow(QLabel("<b>Spread settings</b>"))
        self.fire_centroid_mode_combo = QComboBox()
        self.fire_centroid_mode_combo.addItems(["Random", "Center", "RandomCross", "RandomCircle"])
        pf.addRow("Centroid mode:", self.fire_centroid_mode_combo)
        self.fire_random_amount_spin = QDoubleSpinBox()
        self.fire_random_amount_spin.setRange(0.0, 1.0); self.fire_random_amount_spin.setValue(0.5); self.fire_random_amount_spin.setDecimals(3)
        pf.addRow("Random amount:", self.fire_random_amount_spin)
        self.fire_theta_limit_spin = QDoubleSpinBox()
        self.fire_theta_limit_spin.setRange(0.0, 180.0); self.fire_theta_limit_spin.setValue(5.0); self.fire_theta_limit_spin.setDecimals(1); self.fire_theta_limit_spin.setSuffix(" °")
        pf.addRow("Theta limit:", self.fire_theta_limit_spin)
        self.fire_spread_mode_combo = QComboBox()
        self.fire_spread_mode_combo.addItems(["SixteenDirections", "EightDirections", "FourDirections"])
        pf.addRow("Spread mode:", self.fire_spread_mode_combo)

        pf.addRow(QLabel("<b>Ignition</b>"))
        self.fire_ign_shp_edit = self._file_row(pf, "Ignition shapefile:", "Shapefiles (*.shp);;All files (*)")
        self.fire_ign_time_spin = QDoubleSpinBox()
        self.fire_ign_time_spin.setRange(0.0, 86400.0)
        self.fire_ign_time_spin.setValue(0.0)
        self.fire_ign_time_spin.setDecimals(1)
        self.fire_ign_time_spin.setSuffix(" s")
        pf.addRow("Ignition time (relative):", self.fire_ign_time_spin)
        self._fire_stack.addWidget(p)

        # idx 0 = AscImport -> page 0, idx 1 = ElmClone -> page 1
        self.fire_module_combo.currentIndexChanged.connect(self._fire_stack.setCurrentIndex)
        return w

    # ── Smoke tab ─────────────────────────────────────────────────────

    def _build_smoke_tab(self):
        w = QWidget()
        vbox = QVBoxLayout(w)
        vbox.setContentsMargins(8, 8, 8, 8)

        fixed = QFormLayout()
        fixed.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.smoke_enabled = QCheckBox()
        fixed.addRow("Enabled:", self.smoke_enabled)
        self.smoke_module_combo = QComboBox()
        self.smoke_module_combo.addItems(["GlobalSmoke"])
        fixed.addRow("Module:", self.smoke_module_combo)
        vbox.addLayout(fixed)

        self._smoke_stack = QStackedWidget()
        vbox.addWidget(self._smoke_stack)
        vbox.addStretch()

        # Page 0: GlobalSmoke
        p = QWidget(); pf = QFormLayout(p); pf.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.smoke_extinction_edit = self._file_row(pf, "Extinction file:", "ASC files (*.asc);;All files (*)")
        self._smoke_stack.addWidget(p)

        self.smoke_module_combo.currentIndexChanged.connect(self._smoke_stack.setCurrentIndex)
        return w

    # ── Trigger Buffer tab ────────────────────────────────────────────

    def _build_trigger_tab(self):
        w = QWidget()
        vbox = QVBoxLayout(w)
        vbox.setContentsMargins(8, 8, 8, 8)

        fixed = QFormLayout()
        fixed.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.trig_enabled = QCheckBox()
        fixed.addRow("Enabled:", self.trig_enabled)
        self.trig_module_combo = QComboBox()
        self.trig_module_combo.addItems(["kPERIL"])
        fixed.addRow("Module:", self.trig_module_combo)
        self.evac_order_start = QDoubleSpinBox()
        self.evac_order_start.setRange(0.0, 86400.0); self.evac_order_start.setValue(0.0)
        self.evac_order_start.setDecimals(0); self.evac_order_start.setSuffix(" s")
        fixed.addRow("Evac order start:", self.evac_order_start)
        vbox.addLayout(fixed)

        self._trig_stack = QStackedWidget()
        vbox.addWidget(self._trig_stack)
        vbox.addStretch()

        # Page 0: kPERIL
        p = QWidget(); pf = QFormLayout(p); pf.setFieldGrowthPolicy(QFormLayout.ExpandingFieldsGrow)
        self.kperil_windspeed = QDoubleSpinBox()
        self.kperil_windspeed.setRange(0.0, 200.0); self.kperil_windspeed.setValue(5.0)
        self.kperil_windspeed.setDecimals(2); self.kperil_windspeed.setSuffix(" m/s")
        pf.addRow("Midflame windspeed:", self.kperil_windspeed)
        self.kperil_ros_from_behave = QCheckBox()
        pf.addRow("Calculate ROS from Behave:", self.kperil_ros_from_behave)
        self.kperil_fuel_moisture_edit = self._file_row(pf, "Initial fuel moisture:", "ASC files (*.asc);;All files (*)")
        self.kperil_output_name = QLineEdit(); self.kperil_output_name.setText("trigger_buffer")
        pf.addRow("Output name:", self.kperil_output_name)
        self._trig_stack.addWidget(p)

        self.trig_module_combo.currentIndexChanged.connect(self._trig_stack.setCurrentIndex)
        return w

    # ------------------------------------------------------------------
    # Population generation
    # ------------------------------------------------------------------

    def _download_osm_for_population(self):
        from .layers import get_domain_layer
        from . import osm as osm_mod

        domain = get_domain_layer()
        if domain is None or domain.featureCount() == 0:
            QMessageBox.warning(
                self, "WUInity",
                "No domain layer found. Run 'New WUInity Project' and draw the domain first."
            )
            return

        folder = get_project_folder() or os.path.dirname(self.osm_xml_edit.text().strip())
        if not folder:
            QMessageBox.warning(self, "WUInity", "No project folder set — cannot determine where to save the file.")
            return

        save_path = os.path.join(folder, osm_mod.OSM_FULL_FILENAME)
        self._osm_dl_status.setText("Downloading full OSM data (roads + buildings + landuse)…")
        self._osm_dl_status.setStyleSheet("color: #555; font-size: 11px;")

        self._osm_full_task = osm_mod.start_full_download(
            domain_layer   = domain,
            on_done        = self._on_osm_full_done,
            save_xml_path  = save_path,
        )

    def _on_osm_full_done(self, success, message, saved_path):
        self._osm_full_task = None
        if success and saved_path:
            self.osm_xml_edit.setText(saved_path)
            self._osm_dl_status.setText(f"Done — {saved_path}")
            self._osm_dl_status.setStyleSheet("color: green; font-size: 11px;")
        else:
            self._osm_dl_status.setText(f"Failed: {message}")
            self._osm_dl_status.setStyleSheet("color: red; font-size: 11px;")

    def _generate_population(self):
        osm_path = self.osm_xml_edit.text().strip()
        gpw      = self.gpw_folder_edit.text().strip()
        preact   = self.preact_exe_edit.text().strip()

        if not osm_path or not os.path.isfile(osm_path):
            QMessageBox.warning(
                self, "WUInity",
                "OSM XML file not found.\n\n"
                "Either run 'Import OSM Roads' first (it saves the file automatically "
                "when a project folder is set), or browse to an existing .osm.xml file."
            )
            return
        if not gpw or not os.path.isdir(gpw):
            QMessageBox.warning(self, "WUInity", "Please select a valid GPW folder.")
            return
        if not preact or not os.path.isfile(preact):
            QMessageBox.warning(self, "WUInity", "Please select the PREACTcli executable.")
            return

        # Output CSV next to the OSM file (or project folder if available)
        folder = get_project_folder() or os.path.dirname(osm_path)
        pop_csv = os.path.join(folder, "population.csv")

        # Persist settings for next session
        QgsProject.instance().writeEntry("wuinity", "gpw_folder", gpw)
        QgsProject.instance().writeEntry("wuinity", "preact_exe", preact)
        QgsProject.instance().writeEntry("wuinity", "pop_minhh",  str(self.pop_minhh.value()))
        QgsProject.instance().writeEntry("wuinity", "pop_maxhh",  str(self.pop_maxhh.value()))

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

        # netconvert exe
        netconvert = QgsProject.instance().readEntry("wuinity", "netconvert_exe", "")[0]
        if not netconvert:
            # Common SUMO install locations
            for candidate in [
                r"C:\Program Files (x86)\Eclipse\Sumo\bin\netconvert.exe",
                r"C:\Program Files\Eclipse\Sumo\bin\netconvert.exe",
            ]:
                if os.path.isfile(candidate):
                    netconvert = candidate
                    break
        if netconvert and os.path.isfile(netconvert):
            self.netconvert_exe_edit.setText(netconvert)

        # Pre-fill OSM XML, population CSV and SUMO config from project folder
        folder = get_project_folder()
        if folder:
            from . import osm as osm_mod
            # Prefer the full OSM file for population; fall back to roads-only
            for osm_name in (osm_mod.OSM_FULL_FILENAME, osm_mod.OSM_XML_FILENAME):
                osm_candidate = os.path.join(folder, osm_name)
                if os.path.isfile(osm_candidate):
                    self.osm_xml_edit.setText(osm_candidate)
                    break
            pop_candidate = os.path.join(folder, "population.csv")
            if os.path.isfile(pop_candidate):
                self.pop_file_edit.setText(pop_candidate)
            sumo_candidate = os.path.join(folder, "sumo", "osm.sumocfg")
            if os.path.isfile(sumo_candidate):
                self.sumo_cfg_edit.setText(sumo_candidate)

    def _browse_output_folder(self):
        start = self.folder_edit.text() or os.path.expanduser("~")
        folder = QFileDialog.getExistingDirectory(self, "Select output folder", start)
        if folder:
            self.folder_edit.setText(folder)

    # ------------------------------------------------------------------
    # SUMO network generation
    # ------------------------------------------------------------------

    def _generate_sumo_network(self):
        osm_path    = self.osm_xml_edit.text().strip()
        netconvert  = self.netconvert_exe_edit.text().strip()

        if not osm_path or not os.path.isfile(osm_path):
            QMessageBox.warning(
                self, "WUInity",
                "OSM XML file not found.\n"
                "Set the OSM XML path in the Population tab first."
            )
            return
        if not netconvert or not os.path.isfile(netconvert):
            QMessageBox.warning(
                self, "WUInity",
                "netconvert.exe not found.\n"
                "Install SUMO (https://sumo.dlr.de) and point to netconvert.exe."
            )
            return

        out_dir = os.path.join(
            get_project_folder() or os.path.dirname(osm_path),
            "sumo"
        )
        os.makedirs(out_dir, exist_ok=True)

        QgsProject.instance().writeEntry("wuinity", "netconvert_exe", netconvert)

        self._sumo_gen_status.setText("Running netconvert…")
        self._sumo_gen_status.setStyleSheet("color: #555; font-size: 11px;")
        self._sumo_gen_btn.setEnabled(False)

        self._sumo_task = _NetconvertTask(
            osm_path     = osm_path,
            netconvert   = netconvert,
            out_dir      = out_dir,
            on_done      = self._on_sumo_gen_done,
        )
        QgsApplication.taskManager().addTask(self._sumo_task)

    def _on_sumo_gen_done(self, success, message, sumocfg_path):
        self._sumo_task = None
        self._sumo_gen_btn.setEnabled(True)
        if success:
            self.sumo_cfg_edit.setText(sumocfg_path)
            self._sumo_gen_status.setText(f"Done — {sumocfg_path}")
            self._sumo_gen_status.setStyleSheet("color: green; font-size: 11px;")
        else:
            self._sumo_gen_status.setText(f"Failed: {message}")
            self._sumo_gen_status.setStyleSheet("color: red; font-size: 11px;")

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

        start_dt = self.start_dt_edit.dateTime().toString("yyyy-MM-ddTHH:mm:ss")

        try:
            wui_path = exporter.export(
                output_dir          = folder,
                sim_name            = name,
                start_datetime      = start_dt,
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
                "enabled":              self.fire_enabled.isChecked(),
                "module":               self.fire_module_combo.currentText(),
                "lcp_file":             self.fire_lcp_edit.text().strip(),
                "spread_model":         self.fire_spread_model_combo.currentText(),
                "centroid_mode":        self.fire_centroid_mode_combo.currentText(),
                "random_amount":        self.fire_random_amount_spin.value(),
                "theta_limit":          self.fire_theta_limit_spin.value(),
                "spread_mode":          self.fire_spread_mode_combo.currentText(),
                "fuel_models_file":     self.fire_fuel_models_edit.text().strip(),
                "fuel_moisture_file":   self.fire_fuel_moisture_edit.text().strip(),
                "fbp_lookup_file":      self.fire_fbp_lookup_edit.text().strip(),
                "start_dc":             self.fire_start_dc.value(),
                "start_dmc":            self.fire_start_dmc.value(),
                "start_ffmc":           self.fire_start_ffmc.value(),
                "start_hourly_ffmc":    self.fire_start_hourly_ffmc.value(),
                "ros_lookup_file":      self.fire_ros_lookup_edit.text().strip(),
                "ign_shapefile":        self.fire_ign_shp_edit.text().strip(),
                "ign_time":             self.fire_ign_time_spin.value(),
                "asc_root":             self.asc_root_edit.text().strip(),
                "toa_file":             self.asc_toa_edit.text().strip(),
                "ros_file":             self.asc_ros_edit.text().strip(),
                "sd_file":              self.asc_sd_edit.text().strip(),
                "fi_file":              self.asc_fi_edit.text().strip(),
                "wx_file":              self.asc_wx_edit.text().strip(),
            },
            "smoke": {
                "enabled":           self.smoke_enabled.isChecked(),
                "module":            self.smoke_module_combo.currentText(),
                "extinction_file":   self.smoke_extinction_edit.text().strip(),
            },
            "trigger_buffer": {
                "enabled":           self.trig_enabled.isChecked(),
                "module":            self.trig_module_combo.currentText(),
                "evac_order_start":  self.evac_order_start.value(),
                "midflame_wind":     self.kperil_windspeed.value(),
                "ros_from_behave":   self.kperil_ros_from_behave.isChecked(),
                "fuel_moisture":     self.kperil_fuel_moisture_edit.text().strip(),
                "output_name":       self.kperil_output_name.text().strip(),
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


# ---------------------------------------------------------------------------
# Background task: netconvert OSM → SUMO network + sumocfg
# ---------------------------------------------------------------------------

class _NetconvertTask(QgsTask):
    """
    Runs netconvert to produce osm.net.xml.gz from the OSM XML file,
    then writes a minimal osm.sumocfg pointing to it.
    """
    NETCONVERT_FLAGS = [
        "--geometry.remove",
        "--roundabouts.guess",
        "--ramps.guess",
        "--junctions.join",
        "--tls.guess-signals",
        "--tls.discard-simple",
        "--tls.join",
        "--output.original-names",
        "--output.street-names",
        "--osm.sidewalks", "false",
        "--osm.crossings", "false",
        "--keep-edges.by-type",
        "highway.motorway,highway.trunk,highway.primary,highway.secondary,"
        "highway.tertiary,highway.residential,highway.unclassified,highway.service,"
        "highway.motorway_link,highway.trunk_link,highway.primary_link,"
        "highway.secondary_link,highway.tertiary_link",
    ]

    SUMOCFG_TEMPLATE = """\
<?xml version="1.0" encoding="UTF-8"?>
<sumoConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
    xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/sumoConfiguration.xsd">
    <input>
        <net-file value="osm.net.xml.gz"/>
    </input>
    <processing>
        <ignore-route-errors value="true"/>
    </processing>
    <report>
        <verbose value="true"/>
        <no-step-log value="true"/>
    </report>
</sumoConfiguration>
"""

    def __init__(self, osm_path, netconvert, out_dir, on_done):
        super().__init__("Generating SUMO network", QgsTask.CanCancel)
        self._osm_path    = osm_path
        self._netconvert  = netconvert
        self._out_dir     = out_dir
        self._on_done     = on_done
        self._error       = None
        self._sumocfg     = os.path.join(out_dir, "osm.sumocfg")

    def run(self):
        net_out = os.path.join(self._out_dir, "osm.net.xml.gz")
        cmd = [
            self._netconvert,
            "--osm-files", self._osm_path,
            "--output-file", net_out,
        ] + self.NETCONVERT_FLAGS

        QgsMessageLog.logMessage("Running netconvert…", "WUInity", Qgis.Info)
        try:
            result = subprocess.run(
                cmd, capture_output=True, text=True, timeout=300
            )
        except FileNotFoundError:
            self._error = f"netconvert not found: {self._netconvert}"
            return False
        except subprocess.TimeoutExpired:
            self._error = "netconvert timed out after 5 minutes"
            return False

        if result.stdout:
            QgsMessageLog.logMessage(result.stdout.strip(), "WUInity", Qgis.Info)
        if result.returncode != 0:
            self._error = result.stderr.strip() or f"netconvert exited with code {result.returncode}"
            return False

        with open(self._sumocfg, "w", encoding="utf-8") as f:
            f.write(self.SUMOCFG_TEMPLATE)

        QgsMessageLog.logMessage(f"SUMO network written to {self._out_dir}", "WUInity", Qgis.Info)
        self.setProgress(100)
        return True

    def finished(self, success):
        self._on_done(success, self._error or "", self._sumocfg)
