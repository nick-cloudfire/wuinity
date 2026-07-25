# WUInity QGIS plugin

A QGIS plugin for preparing WUI-NITY / PREACT wildfire-evacuation scenarios:
draw the simulation domain, place destinations, define evacuation groups,
generate population and road-network data, and export a ready-to-run
[`.wui` project](../docs/input-file-format.md).

- Requires **QGIS 3.22** or newer.
- Source: [`wuinity_qgis/`](wuinity_qgis/).

## Install (development)

From the repository root:

```sh
python QGIS_plugin/install_dev.py
```

This symlinks the plugin into your QGIS profile. Then enable **WUInity** in
QGIS → *Plugins → Manage and Install Plugins → Installed*.

## Usage

See the full guide: **[docs/qgis-plugin.md](../docs/qgis-plugin.md)**.

## Optional Python dependencies

Installed into QGIS's bundled Python as needed: `numpy` (population generation),
`osgeo.gdal`/`osr` (ships with QGIS), `pycountry` (optional WorldPop country
lookup). Some tools also invoke [`PREACTcli`](../docs/command-line-tools.md) and
SUMO's `netconvert`.
