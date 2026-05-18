"""
Dialog for managing Demographics profiles and Response Curves stored in the
project GeoPackage.  Opened from the 'Manage Settings' toolbar button or
from within the Group Dialog.
"""

import json

from PyQt5.QtWidgets import (
    QDialog, QVBoxLayout, QHBoxLayout, QFormLayout,
    QTabWidget, QWidget, QTableWidget, QTableWidgetItem,
    QListWidget, QPushButton, QLineEdit, QDoubleSpinBox,
    QSpinBox, QCheckBox, QDialogButtonBox, QLabel,
    QHeaderView, QMessageBox, QAbstractItemView,
)
from PyQt5.QtCore import Qt
from qgis.core import QgsFeature


# ---------------------------------------------------------------------------
# Main management dialog
# ---------------------------------------------------------------------------

class ManageDialog(QDialog):
    def __init__(self, parent=None):
        super().__init__(parent)
        self.setWindowTitle("WUInity — Settings")
        self.setMinimumSize(520, 420)
        self._build_ui()
        self._populate()

    def _build_ui(self):
        root = QVBoxLayout(self)
        self._tabs = QTabWidget()
        root.addWidget(self._tabs)

        self._tabs.addTab(self._build_demographics_tab(), "Demographics")
        self._tabs.addTab(self._build_curves_tab(),       "Response Curves")

        close = QPushButton("Close")
        close.clicked.connect(self.accept)
        root.addWidget(close)

    # ── Demographics tab ──────────────────────────────────────────────

    def _build_demographics_tab(self):
        w = QWidget()
        lay = QVBoxLayout(w)

        self._demo_table = QTableWidget(0, 4)
        self._demo_table.setHorizontalHeaderLabels(
            ["Name", "Allow >1 Car", "Max Cars", "Max Cars Prob"]
        )
        self._demo_table.horizontalHeader().setSectionResizeMode(0, QHeaderView.Stretch)
        self._demo_table.setSelectionBehavior(QAbstractItemView.SelectRows)
        lay.addWidget(self._demo_table)

        btns = QHBoxLayout()
        add_btn = QPushButton("Add")
        del_btn = QPushButton("Delete selected")
        sav_btn = QPushButton("Save changes")
        add_btn.clicked.connect(self._demo_add_row)
        del_btn.clicked.connect(self._demo_delete_row)
        sav_btn.clicked.connect(self._demo_save)
        btns.addWidget(add_btn)
        btns.addWidget(del_btn)
        btns.addStretch()
        btns.addWidget(sav_btn)
        lay.addLayout(btns)
        return w

    # ── Response Curves tab ───────────────────────────────────────────

    def _build_curves_tab(self):
        w = QWidget()
        lay = QVBoxLayout(w)

        self._curve_list = QListWidget()
        self._curve_list.setSelectionMode(QAbstractItemView.SingleSelection)
        lay.addWidget(self._curve_list)

        btns = QHBoxLayout()
        add_btn  = QPushButton("Add")
        edit_btn = QPushButton("Edit selected")
        del_btn  = QPushButton("Delete selected")
        add_btn.clicked.connect(self._curve_add)
        edit_btn.clicked.connect(self._curve_edit)
        del_btn.clicked.connect(self._curve_delete)
        btns.addWidget(add_btn)
        btns.addWidget(edit_btn)
        btns.addWidget(del_btn)
        btns.addStretch()
        lay.addLayout(btns)
        return w

    # ------------------------------------------------------------------
    # Populate
    # ------------------------------------------------------------------

    def _populate(self):
        self._populate_demographics()
        self._populate_curves()

    def _populate_demographics(self):
        from .layers import get_demographics_table
        self._demo_table.setRowCount(0)
        tbl = get_demographics_table()
        if not tbl:
            return
        tbl.reload()
        for feat in tbl.getFeatures():
            self._demo_add_row(
                name           = feat["name"]           or "",
                allow_more_cars= bool(feat["allow_more_cars"]),
                max_cars       = int(feat["max_cars"] or 2),
                max_cars_prob  = float(feat["max_cars_prob"] or 0.3),
            )

    def _populate_curves(self):
        from .layers import get_curves_table
        self._curve_list.clear()
        tbl = get_curves_table()
        if not tbl:
            return
        tbl.reload()
        for feat in tbl.getFeatures():
            self._curve_list.addItem(feat["name"] or "")

    # ------------------------------------------------------------------
    # Demographics helpers
    # ------------------------------------------------------------------

    def _demo_add_row(self, name="", allow_more_cars=True, max_cars=2, max_cars_prob=0.3):
        row = self._demo_table.rowCount()
        self._demo_table.insertRow(row)
        self._demo_table.setItem(row, 0, QTableWidgetItem(name))

        chk = QTableWidgetItem()
        chk.setFlags(Qt.ItemIsUserCheckable | Qt.ItemIsEnabled)
        chk.setCheckState(Qt.Checked if allow_more_cars else Qt.Unchecked)
        self._demo_table.setItem(row, 1, chk)

        cars_spin = QSpinBox()
        cars_spin.setRange(1, 10)
        cars_spin.setValue(max_cars)
        self._demo_table.setCellWidget(row, 2, cars_spin)

        prob_spin = QDoubleSpinBox()
        prob_spin.setRange(0.0, 1.0)
        prob_spin.setSingleStep(0.05)
        prob_spin.setDecimals(2)
        prob_spin.setValue(max_cars_prob)
        self._demo_table.setCellWidget(row, 3, prob_spin)

    def _demo_delete_row(self):
        rows = sorted(
            {idx.row() for idx in self._demo_table.selectedIndexes()},
            reverse=True,
        )
        for row in rows:
            self._demo_table.removeRow(row)

    def _demo_save(self):
        from .layers import get_demographics_table
        tbl = get_demographics_table()
        if not tbl:
            QMessageBox.warning(
                self, "WUInity",
                "Demographics table not found.\n"
                "Run 'New WUInity Project' first to set up the project folder."
            )
            return

        # Collect rows from the UI table
        rows = []
        for row in range(self._demo_table.rowCount()):
            name = (self._demo_table.item(row, 0) or QTableWidgetItem("")).text().strip()
            if not name:
                continue
            chk       = self._demo_table.item(row, 1)
            allow     = chk.checkState() == Qt.Checked if chk else True
            cars_spin = self._demo_table.cellWidget(row, 2)
            prob_spin = self._demo_table.cellWidget(row, 3)
            rows.append({
                "name":            name,
                "allow_more_cars": 1 if allow else 0,
                "max_cars":        cars_spin.value() if cars_spin else 2,
                "max_cars_prob":   prob_spin.value() if prob_spin else 0.3,
            })

        # Replace all features in the table
        tbl.startEditing()
        tbl.deleteFeatures([f.id() for f in tbl.getFeatures()])
        for r in rows:
            feat = QgsFeature(tbl.fields())
            feat["name"]            = r["name"]
            feat["allow_more_cars"] = r["allow_more_cars"]
            feat["max_cars"]        = r["max_cars"]
            feat["max_cars_prob"]   = r["max_cars_prob"]
            tbl.addFeature(feat)
        tbl.commitChanges()
        tbl.reload()
        QMessageBox.information(self, "WUInity", f"Saved {len(rows)} demographic profile(s).")

    # ------------------------------------------------------------------
    # Response curves helpers
    # ------------------------------------------------------------------

    def _curve_add(self):
        dlg = _CurveEditorDialog(parent=self)
        if dlg.exec_() == QDialog.Accepted:
            self._save_curve(dlg.get_values())
            self._populate_curves()

    def _curve_edit(self):
        item = self._curve_list.currentItem()
        if not item:
            return
        name = item.text()
        data = self._load_curve_data(name)
        dlg  = _CurveEditorDialog(name=name, data=data, parent=self)
        if dlg.exec_() == QDialog.Accepted:
            vals = dlg.get_values()
            self._delete_curve_by_name(name)
            self._save_curve(vals)
            self._populate_curves()

    def _curve_delete(self):
        item = self._curve_list.currentItem()
        if not item:
            return
        reply = QMessageBox.question(
            self, "WUInity",
            f"Delete response curve '{item.text()}'?",
            QMessageBox.Yes | QMessageBox.No,
        )
        if reply == QMessageBox.Yes:
            self._delete_curve_by_name(item.text())
            self._populate_curves()

    def _save_curve(self, vals):
        from .layers import get_curves_table
        tbl = get_curves_table()
        if not tbl:
            QMessageBox.warning(
                self, "WUInity",
                "Response curves table not found.\n"
                "Run 'New WUInity Project' first to set up the project folder."
            )
            return
        feat = QgsFeature(tbl.fields())
        feat["name"] = vals["name"]
        feat["data"] = json.dumps(vals["data"])
        tbl.startEditing()
        tbl.addFeature(feat)
        tbl.commitChanges()
        tbl.reload()

    def _delete_curve_by_name(self, name):
        from .layers import get_curves_table
        tbl = get_curves_table()
        if not tbl:
            return
        ids = [f.id() for f in tbl.getFeatures() if f["name"] == name]
        if ids:
            tbl.startEditing()
            tbl.deleteFeatures(ids)
            tbl.commitChanges()
            tbl.reload()

    def _load_curve_data(self, name):
        from .layers import get_curves_table
        tbl = get_curves_table()
        if not tbl:
            return []
        for feat in tbl.getFeatures():
            if feat["name"] == name:
                try:
                    return json.loads(feat["data"] or "[]")
                except Exception:
                    return []
        return []


# ---------------------------------------------------------------------------
# Response curve editor sub-dialog
# ---------------------------------------------------------------------------

class _CurveEditorDialog(QDialog):
    def __init__(self, name="", data=None, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Response Curve Editor")
        self.setMinimumSize(360, 340)
        self._build_ui()
        if name:
            self._name_edit.setText(name)
        if data:
            self._load_data(data)
        elif not data:
            self._add_default_points()

    def _build_ui(self):
        root = QVBoxLayout(self)

        form = QFormLayout()
        self._name_edit = QLineEdit()
        form.addRow("Name:", self._name_edit)
        root.addLayout(form)

        root.addWidget(QLabel(
            "Time/probability pairs — must start at (0, 0) and end at (_, 1.0):"
        ))

        self._point_table = QTableWidget(0, 2)
        self._point_table.setHorizontalHeaderLabels(["Time (s)", "Cum. Probability"])
        self._point_table.horizontalHeader().setSectionResizeMode(QHeaderView.Stretch)
        root.addWidget(self._point_table)

        row_btns = QHBoxLayout()
        add_row  = QPushButton("Add point")
        del_row  = QPushButton("Remove last")
        add_row.clicked.connect(self._add_row)
        del_row.clicked.connect(self._remove_last_row)
        row_btns.addWidget(add_row)
        row_btns.addWidget(del_row)
        row_btns.addStretch()
        root.addLayout(row_btns)

        btns = QDialogButtonBox(QDialogButtonBox.Ok | QDialogButtonBox.Cancel)
        btns.accepted.connect(self._on_accept)
        btns.rejected.connect(self.reject)
        root.addWidget(btns)

    def _add_default_points(self):
        from .layers import DEFAULT_CURVE_DATA
        self._load_data(DEFAULT_CURVE_DATA)

    def _load_data(self, data):
        self._point_table.setRowCount(0)
        for t, p in data:
            self._add_row(t, p)

    def _add_row(self, t=0, p=0.0):
        row = self._point_table.rowCount()
        self._point_table.insertRow(row)

        t_spin = QDoubleSpinBox()
        t_spin.setRange(0, 86400)
        t_spin.setDecimals(0)
        t_spin.setValue(float(t))
        self._point_table.setCellWidget(row, 0, t_spin)

        p_spin = QDoubleSpinBox()
        p_spin.setRange(0.0, 1.0)
        p_spin.setSingleStep(0.01)
        p_spin.setDecimals(4)
        p_spin.setValue(float(p))
        self._point_table.setCellWidget(row, 1, p_spin)

    def _remove_last_row(self):
        if self._point_table.rowCount() > 0:
            self._point_table.removeRow(self._point_table.rowCount() - 1)

    def _on_accept(self):
        name = self._name_edit.text().strip()
        if not name:
            QMessageBox.warning(self, "WUInity", "Please enter a curve name.")
            return
        data = self._collect_data()
        if not data:
            QMessageBox.warning(self, "WUInity", "Add at least one time/probability point.")
            return
        self.accept()

    def get_values(self):
        return {
            "name": self._name_edit.text().strip(),
            "data": self._collect_data(),
        }

    def _collect_data(self):
        points = []
        for row in range(self._point_table.rowCount()):
            t_spin = self._point_table.cellWidget(row, 0)
            p_spin = self._point_table.cellWidget(row, 1)
            if t_spin and p_spin:
                points.append([t_spin.value(), p_spin.value()])
        return points
