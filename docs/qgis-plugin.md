# QGIS plugin

The `WUInity` QGIS plugin (`QGIS_plugin/wuinity_qgis/`) is the easiest way to
prepare a scenario: draw the simulation domain, place destinations, define
evacuation groups, generate the population and road network, and export a
ready-to-run [`.wui` project](input-file-format.md).

- Requires **QGIS 3.22** or newer.
- Some features shell out to the [`PREACTcli`](command-line-tools.md) and SUMO's
  `netconvert` tools; point the plugin at them when prompted.

## Installation (development)

From the repository root:

```sh
python QGIS_plugin/install_dev.py
```

This symlinks `wuinity_qgis` into your QGIS profile's plugin directory. Then, in
QGIS: **Plugins → Manage and Install Plugins → Installed**, and enable
**WUInity**. A "WUInity" toolbar appears.

After changing plugin code, reload it with the *Plugin Reloader* plugin or
restart QGIS.

### Optional Python dependencies

Some tools need extra packages, installed into QGIS's Python:

| Package | Used for |
|---------|----------|
| `numpy` | population generation |
| `osgeo.gdal` / `osr` | raster clipping (ships with QGIS) |
| `pycountry` | optional country-code lookup for WorldPop |

## Toolbar workflow

The toolbar buttons follow the natural order of building a scenario:

1. **New WUInity Project** – choose a project folder and create the three
   managed layers (WUI Domain, WUI Destinations, WUI Evacuation Groups) backed
   by a GeoPackage. Draw your domain polygon, then add destination points and
   group polygons.
   - **Destination `type`** must be `Exit` or `Shelter`.
   - **Group `dest_choice`** must be one of `EvacGroupCDF`,
     `EvacGroupClosestEuclidean`, `Random`, `ClosestEuclidean`.
2. **Import OSM Roads** – download the OSM road network for the domain (used to
   build the SUMO network and to snap population to roads).
3. **Edit Selected Group** – open the editor for the currently selected
   evacuation-group feature (demographics, destinations + CDF, response curves).
4. **Manage Settings** – manage reusable demographics profiles and response
   curves.
5. **Export to .wui** – write the `.wui` file plus companion group shapefiles
   (and, for cellular-automata fire modules, an ignition `.ign`) to disk.

The exported values are written to match the engine's enums and CSV formats
exactly — see the [input file format reference](input-file-format.md).

## Notes

- Coordinates are normalised to EPSG:4326 (lon/lat) on export; the domain
  corner and destinations are written lat-first (`lat,lon`).
- The population CSV the plugin produces has the header
  `OriginLat,OriginLon,AccessLat,AccessLon,People`, matching the engine.
