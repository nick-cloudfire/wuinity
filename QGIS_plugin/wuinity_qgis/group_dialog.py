"""
Dialog for editing a WUI Evacuation Group feature.

Opens automatically when a new group polygon is drawn, and also when
'Edit Selected Group' is clicked. Destinations and response curves are
selected with weights that are automatically converted to CDFs.
"""

from PyQt5.QtWidgets import (
    QDialog, QVBoxLayout, QHBoxLayout, QFormLayout,
    QLineEdit, QComboBox, QCheckBox, QTableWidget,
    QTableWidgetItem, QDoubleSpinBox, QLabel, QPushButton,
    QDialogButtonBox, QHeaderView, QSizePolicy, QAbstractItemView,
)
from PyQt5.QtCore import Qt
from qgis.core import QgsFeature


class GroupDialog(QDialog):
    def __init__(self, feature, layer, parent=None):
        super().__init__(parent)
        self.setWindowTitle("Edit Evacuation Group")
        self.setMinimumWidth(500)
        self._feature = feature
        self._layer   = layer
        self._build_ui()
        self._populate()

    # ------------------------------------------------------------------
    # UI construction
    # ------------------------------------------------------------------

    def _build_ui(self):
        root = QVBoxLayout(self)
        root.setSpacing(8)

        # ── Top form ──────────────────────────────────────────────────
        form = QFormLayout()

        self._name_edit = QLineEdit()
        form.addRow("Name:", self._name_edit)

        demo_row = QHBoxLayout()
        self._demo_combo = QComboBox()
        manage_btn = QPushButton("Manage…")
        manage_btn.setFixedWidth(80)
        manage_btn.clicked.connect(self._open_manage)
        demo_row.addWidget(self._demo_combo, 1)
        demo_row.addWidget(manage_btn)
        form.addRow("Demographics:", demo_row)

        root.addLayout(form)

        # ── Destinations ──────────────────────────────────────────────
        root.addWidget(_section_label("Destinations"))
        self._dest_table = _WeightTable(self)
        self._dest_table.weights_changed.connect(self._refresh_dest_cdf)
        root.addWidget(self._dest_table)
        self._dest_cdf_label = QLabel("CDF: —")
        self._dest_cdf_label.setStyleSheet("color: #555; font-size: 11px;")
        root.addWidget(self._dest_cdf_label)

        # ── Response Curves ───────────────────────────────────────────
        root.addWidget(_section_label("Response Curves"))
        self._curve_table = _WeightTable(self)
        self._curve_table.weights_changed.connect(self._refresh_curve_cdf)
        root.addWidget(self._curve_table)
        self._curve_cdf_label = QLabel("CDF: —")
        self._curve_cdf_label.setStyleSheet("color: #555; font-size: 11px;")
        root.addWidget(self._curve_cdf_label)

        # ── Bottom form ───────────────────────────────────────────────
        form2 = QFormLayout()

        self._dest_choice_combo = QComboBox()
        self._dest_choice_combo.addItems([
            "EvacGroupWeighted", "EvacGroupClosestEuclidean",
            "Random", "ClosestEuclidean",
        ])
        form2.addRow("Destination choice:", self._dest_choice_combo)

        self._default_check = QCheckBox()
        form2.addRow("Default group:", self._default_check)

        root.addLayout(form2)

        # ── Buttons ───────────────────────────────────────────────────
        btns = QDialogButtonBox(QDialogButtonBox.Save | QDialogButtonBox.Cancel)
        btns.accepted.connect(self.accept)
        btns.rejected.connect(self.reject)
        root.addWidget(btns)

    # ------------------------------------------------------------------
    # Population
    # ------------------------------------------------------------------

    def _populate(self):
        self._populate_demographics()
        self._populate_dest_table()
        self._populate_curve_table()

        if not self._feature.isValid():
            return

        self._name_edit.setText(self._feature["name"] or "")

        demo = self._feature["demographics"] or ""
        idx  = self._demo_combo.findText(demo)
        if idx >= 0:
            self._demo_combo.setCurrentIndex(idx)

        self._dest_table.restore(
            self._feature["destinations"] or "",
            self._feature["dest_cdf"]     or "",
        )
        self._curve_table.restore(
            self._feature["resp_curves"] or "",
            self._feature["resp_cdf"]    or "",
        )

        dc  = self._feature["dest_choice"] or "EvacGroupWeighted"
        idx = self._dest_choice_combo.findText(dc)
        if idx >= 0:
            self._dest_choice_combo.setCurrentIndex(idx)

        self._default_check.setChecked(bool(self._feature["is_default"]))

        self._refresh_dest_cdf()
        self._refresh_curve_cdf()

    def _populate_demographics(self):
        from .layers import get_demographics_table
        self._demo_combo.clear()
        tbl = get_demographics_table()
        if tbl:
            tbl.reload()
            for feat in tbl.getFeatures():
                self._demo_combo.addItem(feat["name"] or "")

    def _populate_dest_table(self):
        from .layers import get_destinations_layer
        self._dest_table.clear_rows()
        layer = get_destinations_layer()
        if layer:
            for feat in layer.getFeatures():
                self._dest_table.add_row(feat["name"] or f"dest_{feat.id()}")

    def _populate_curve_table(self):
        from .layers import get_curves_table
        self._curve_table.clear_rows()
        tbl = get_curves_table()
        if tbl:
            tbl.reload()
            for feat in tbl.getFeatures():
                self._curve_table.add_row(feat["name"] or "")

    # ------------------------------------------------------------------
    # CDF previews
    # ------------------------------------------------------------------

    def _refresh_dest_cdf(self):
        self._dest_cdf_label.setText("CDF: " + _cdf_preview(self._dest_table))

    def _refresh_curve_cdf(self):
        self._curve_cdf_label.setText("CDF: " + _cdf_preview(self._curve_table))

    # ------------------------------------------------------------------
    # Manage button
    # ------------------------------------------------------------------

    def _open_manage(self):
        from .manage_dialog import ManageDialog
        dlg = ManageDialog(parent=self)
        dlg.exec_()
        current_demo = self._demo_combo.currentText()
        self._populate_demographics()
        self._populate_curve_table()
        idx = self._demo_combo.findText(current_demo)
        if idx >= 0:
            self._demo_combo.setCurrentIndex(idx)

    # ------------------------------------------------------------------
    # Result
    # ------------------------------------------------------------------

    def get_attribute_map(self):
        """Return {field_index: value} suitable for layer.changeAttributeValues()."""
        fields = self._layer.fields()
        names_d, cdf_d = self._dest_table.selected_names_and_cdf()
        names_c, cdf_c = self._curve_table.selected_names_and_cdf()

        return {
            fields.indexOf("name"):         self._name_edit.text().strip(),
            fields.indexOf("demographics"): self._demo_combo.currentText(),
            fields.indexOf("destinations"): ",".join(names_d),
            fields.indexOf("dest_cdf"):     cdf_d,
            fields.indexOf("resp_curves"):  ",".join(names_c) if names_c else "default_curve",
            fields.indexOf("resp_cdf"):     cdf_c if cdf_c else "1.0",
            fields.indexOf("dest_choice"):  self._dest_choice_combo.currentText(),
            fields.indexOf("is_default"):   1 if self._default_check.isChecked() else 0,
        }


# ---------------------------------------------------------------------------
# Reusable weight table widget
# ---------------------------------------------------------------------------

class _WeightTable(QTableWidget):
    from PyQt5.QtCore import pyqtSignal
    weights_changed = pyqtSignal()

    def __init__(self, parent=None):
        super().__init__(0, 3, parent)
        self.setHorizontalHeaderLabels(["", "Name", "Weight"])
        self.horizontalHeader().setSectionResizeMode(1, QHeaderView.Stretch)
        self.setColumnWidth(0, 28)
        self.setColumnWidth(2, 80)
        self.verticalHeader().setVisible(False)
        self.setSelectionMode(QAbstractItemView.NoSelection)
        self.setEditTriggers(QAbstractItemView.NoEditTriggers)
        self.setSizePolicy(QSizePolicy.Expanding, QSizePolicy.Minimum)
        self.setMaximumHeight(160)
        self._block_signals = False
        self.itemChanged.connect(self._on_item_changed)

    def clear_rows(self):
        self._block_signals = True
        self.setRowCount(0)
        self._block_signals = False

    def add_row(self, name, checked=False, weight=1.0):
        self._block_signals = True
        row = self.rowCount()
        self.insertRow(row)

        chk = QTableWidgetItem()
        chk.setFlags(Qt.ItemIsUserCheckable | Qt.ItemIsEnabled)
        chk.setCheckState(Qt.Checked if checked else Qt.Unchecked)
        self.setItem(row, 0, chk)

        name_item = QTableWidgetItem(name)
        name_item.setFlags(Qt.ItemIsEnabled)
        self.setItem(row, 1, name_item)

        spin = QDoubleSpinBox()
        spin.setRange(0.0, 1000.0)
        spin.setValue(weight)
        spin.setDecimals(2)
        spin.setButtonSymbols(QDoubleSpinBox.NoButtons)
        spin.valueChanged.connect(self.weights_changed.emit)
        self.setCellWidget(row, 2, spin)

        self._block_signals = False

    def restore(self, names_str, cdf_str):
        """Pre-check rows and set weights derived from the stored CDF."""
        if not names_str:
            return
        names   = [n.strip() for n in names_str.split(",") if n.strip()]
        cdfs    = _parse_floats(cdf_str)
        weights = _cdf_to_weights(cdfs, len(names))
        weight_map = dict(zip(names, weights))

        for row in range(self.rowCount()):
            name_item = self.item(row, 1)
            if name_item and name_item.text() in weight_map:
                self.item(row, 0).setCheckState(Qt.Checked)
                spin = self.cellWidget(row, 2)
                if spin:
                    spin.setValue(weight_map[name_item.text()])

    def selected_names_and_cdf(self):
        """Return ([name, ...], cdf_str) for all checked rows."""
        items = []
        for row in range(self.rowCount()):
            chk = self.item(row, 0)
            if chk and chk.checkState() == Qt.Checked:
                name = self.item(row, 1).text()
                w    = (self.cellWidget(row, 2).value()
                        if self.cellWidget(row, 2) else 1.0)
                items.append((name, w))
        if not items:
            return [], ""
        names   = [i[0] for i in items]
        weights = [i[1] for i in items]
        cdf     = _weights_to_cdf(weights)
        return names, ",".join(f"{v:.6f}" for v in cdf)

    def _on_item_changed(self, item):
        if not self._block_signals and item.column() == 0:
            self.weights_changed.emit()


# ---------------------------------------------------------------------------
# Shared helpers
# ---------------------------------------------------------------------------

def _weights_to_cdf(weights):
    total = sum(weights)
    if total <= 0:
        probs = [1.0 / len(weights)] * len(weights)
    else:
        probs = [w / total for w in weights]
    cdf, acc = [], 0.0
    for p in probs:
        acc += p
        cdf.append(round(acc, 6))
    if cdf:
        cdf[-1] = 1.0
    return cdf


def _cdf_to_weights(cdfs, expected_len):
    if len(cdfs) != expected_len or not cdfs:
        return [1.0] * expected_len
    weights = []
    prev = 0.0
    for c in cdfs:
        weights.append(round(c - prev, 6))
        prev = c
    return weights


def _parse_floats(s):
    if not s:
        return []
    try:
        return [float(v.strip()) for v in s.split(",") if v.strip()]
    except ValueError:
        return []


def _cdf_preview(table):
    names, cdf_str = table.selected_names_and_cdf()
    if not names:
        return "—"
    cdfs   = _parse_floats(cdf_str)
    return "  ".join(f"{n}: {c:.3f}" for n, c in zip(names, cdfs))


def _section_label(text):
    lbl = QLabel(f"<b>{text}</b>")
    return lbl
