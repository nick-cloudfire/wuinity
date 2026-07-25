"""
Pure-Python population CSV generation from a WorldPop GeoTIFF + OSM XML road network.
No C# / PREACTcli dependency — uses QGIS's bundled GDAL/OSR and numpy.

Output CSV format: OriginLat,OriginLon,AccessLat,AccessLon,People
  (one row per household, matching what WUInity expects)

Algorithm mirrors PopulationMap.CreatePopulation in PREACTcore.
"""

import math
import os
import random
import xml.etree.ElementTree as ET
from typing import Callable, Dict, List, Optional, Tuple

import numpy as np

try:
    from osgeo import gdal, osr
    gdal.UseExceptions()
except ImportError:
    raise ImportError(
        "GDAL Python bindings not found. "
        "Run this from within the QGIS Python environment."
    )


# ---------------------------------------------------------------------------
# OSM highway-node parsing
# ---------------------------------------------------------------------------

def _parse_osm_highway_nodes(
    osm_path: str,
) -> Tuple[Optional[Dict], np.ndarray, np.ndarray]:
    """
    Parse an OSM XML file and return highway node positions.

    Handles both formats produced by Overpass `out geom;`:
      - Inline coords:  <nd ref="123" lat="59.1" lon="18.1"/>  (no separate <node>)
      - Traditional:    <node id="123" lat="59.1" lon="18.1"/> + <nd ref="123"/>

    Returns
    -------
    bounds : dict with minlat/minlon/maxlat/maxlon, or None
    hw_lats, hw_lons : float64 arrays of all positions on highway ways.
    """
    bounds: Optional[Dict] = None

    # Traditional format support
    all_nodes: Dict[str, Tuple[float, float]] = {}

    # Accumulators
    hw_lats: List[float] = []
    hw_lons: List[float] = []

    in_way = False
    is_highway = False
    # Per-way buffers (we don't know if it's a highway until we see the tags,
    # which in OSM XML come after the <nd> elements)
    way_inline_coords: List[Tuple[float, float]] = []  # from nd lat/lon (out geom)
    way_nd_refs: List[str] = []                        # from nd ref (traditional)

    for event, elem in ET.iterparse(osm_path, events=('start', 'end')):
        if event == 'start':
            tag = elem.tag
            if tag == 'bounds':
                bounds = {
                    'minlat': float(elem.get('minlat', 0)),
                    'minlon': float(elem.get('minlon', 0)),
                    'maxlat': float(elem.get('maxlat', 0)),
                    'maxlon': float(elem.get('maxlon', 0)),
                }
            elif tag == 'node':
                # Traditional format: standalone node elements
                nid = elem.get('id')
                lat = elem.get('lat')
                lon = elem.get('lon')
                if nid and lat and lon:
                    all_nodes[nid] = (float(lat), float(lon))
            elif tag == 'way':
                in_way = True
                is_highway = False
                way_inline_coords = []
                way_nd_refs = []
        else:  # 'end'
            tag = elem.tag
            if tag == 'way':
                if is_highway:
                    if way_inline_coords:
                        # Overpass `out geom;` — coordinates were on the <nd> elements
                        hw_lats.extend(c[0] for c in way_inline_coords)
                        hw_lons.extend(c[1] for c in way_inline_coords)
                    else:
                        # Traditional format — resolve refs via the node table
                        for ref in way_nd_refs:
                            pos = all_nodes.get(ref)
                            if pos is not None:
                                hw_lats.append(pos[0])
                                hw_lons.append(pos[1])
                in_way = False
                elem.clear()
            elif in_way:
                if tag == 'nd':
                    lat_s = elem.get('lat')
                    lon_s = elem.get('lon')
                    if lat_s and lon_s:
                        # Inline geometry (out geom; format)
                        way_inline_coords.append((float(lat_s), float(lon_s)))
                    else:
                        ref = elem.get('ref')
                        if ref:
                            way_nd_refs.append(ref)
                elif tag == 'tag' and elem.get('k') == 'highway':
                    is_highway = True

    return bounds, np.array(hw_lats, dtype=np.float64), np.array(hw_lons, dtype=np.float64)


# ---------------------------------------------------------------------------
# Nearest-highway-node lookup
# ---------------------------------------------------------------------------

def _find_nearest_nodes(
    cell_lats: np.ndarray,
    cell_lons: np.ndarray,
    hw_lats: np.ndarray,
    hw_lons: np.ndarray,
    chunk: int = 1024,
) -> Tuple[np.ndarray, np.ndarray]:
    """
    Vectorised flat-earth nearest-neighbour from cell centres to highway nodes.
    Processed in chunks to keep peak memory manageable.
    """
    ref_lat = float(np.mean(cell_lats))
    cos_lat = math.cos(math.radians(ref_lat))

    scaled_cell_lon = cell_lons * cos_lat
    scaled_hw_lon   = hw_lons  * cos_lat

    n = len(cell_lats)
    access_lat = np.empty(n, dtype=np.float64)
    access_lon = np.empty(n, dtype=np.float64)

    for start in range(0, n, chunk):
        end = min(start + chunk, n)
        clat = cell_lats[start:end, np.newaxis]
        clon = scaled_cell_lon[start:end, np.newaxis]
        dlat = clat - hw_lats[np.newaxis, :]
        dlon = clon - scaled_hw_lon[np.newaxis, :]
        idx  = np.argmin(dlat * dlat + dlon * dlon, axis=1)
        access_lat[start:end] = hw_lats[idx]
        access_lon[start:end] = hw_lons[idx]

    return access_lat, access_lon


# ---------------------------------------------------------------------------
# WorldPop TIF reader
# ---------------------------------------------------------------------------

def _read_worldpop_tif(tif_path: str) -> Tuple[np.ndarray, object, object, float, float]:
    """
    Open a WorldPop GeoTIFF (UTM or WGS84 projection) and return:
      pop_array  : 2-D float32 array of population counts, shape (yDim, xDim)
      utm_srs    : osr.SpatialReference of the raster
      transform  : 6-element GDAL geotransform
      x_size     : pixel width  in projection units (metres for UTM)
      y_size     : pixel height in projection units (positive)
    """
    ds = gdal.Open(tif_path, gdal.GA_ReadOnly)
    if ds is None:
        raise RuntimeError(f"GDAL could not open: {tif_path}")

    wkt = ds.GetProjection()
    srs = osr.SpatialReference(wkt)

    xdim = ds.RasterXSize
    ydim = ds.RasterYSize
    transform = ds.GetGeoTransform()

    band = ds.GetRasterBand(1)
    arr = band.ReadAsArray(0, 0, xdim, ydim).astype(np.float32)
    nodata = band.GetNoDataValue()
    if nodata is not None:
        arr[arr == nodata] = 0.0
    arr[arr < 0] = 0.0

    ds = None  # close

    x_size =  transform[1]
    y_size = -transform[5]   # make positive

    return arr, srs, transform, x_size, y_size


# ---------------------------------------------------------------------------
# Public entry point
# ---------------------------------------------------------------------------

def generate_population_csv(
    osm_path: str,
    worldpop_path: str,
    output_csv: str,
    min_hh: int = 1,
    max_hh: int = 5,
    progress_cb: Optional[Callable[[float, str], None]] = None,
) -> None:
    """
    Generate a WUInity population CSV from a WorldPop GeoTIFF + OSM road network.

    Mirrors the logic in PopulationMap.CreatePopulation (PREACTcore).

    Parameters
    ----------
    osm_path      : Path to OSM XML (.osm / .xml) for the domain.
    worldpop_path : Path to a WorldPop GeoTIFF (population count per pixel, UTM or WGS84).
    output_csv    : Destination path for the output CSV.
    min_hh        : Minimum household size.
    max_hh        : Maximum household size.
    progress_cb   : Optional callback(fraction 0..1, status_message).
    """
    def _prog(frac: float, msg: str) -> None:
        if progress_cb:
            progress_cb(frac, msg)

    # 1. Parse OSM -------------------------------------------------------
    _prog(0.00, "Parsing OSM XML for highway nodes…")
    bounds, hw_lats, hw_lons = _parse_osm_highway_nodes(osm_path)

    if len(hw_lats) == 0:
        raise RuntimeError("No highway nodes found in OSM file.")

    # 2. Read WorldPop TIF -----------------------------------------------
    _prog(0.15, "Opening WorldPop GeoTIFF…")
    pop_array, raster_srs, geotransform, x_size, y_size = _read_worldpop_tif(worldpop_path)

    xdim = pop_array.shape[1]
    ydim = pop_array.shape[0]

    west_proj  = geotransform[0]
    north_proj = geotransform[3]
    south_proj = north_proj - y_size * ydim
    east_proj  = west_proj  + x_size * xdim

    _prog(0.25, f"WorldPop raster: {xdim}×{ydim} pixels, pixel size {x_size:.1f}×{y_size:.1f}")

    # 3. Collect populated pixels ----------------------------------------
    _prog(0.35, "Collecting populated pixels…")

    # threshold: > 1 to mirror C# code (rasterPopCount > 1)
    iy, ix = np.where(pop_array > 1)
    if len(iy) == 0:
        raise RuntimeError(
            "No pixels with population > 1 found in WorldPop raster. "
            "Check that the file covers the simulation domain."
        )

    # Pixel centres in raster projection units
    px_x = west_proj  + (ix + 0.5) * x_size
    px_y = south_proj + (ydim - iy - 0.5) * y_size  # flip: row 0 = north in GDAL

    pixel_pops = pop_array[iy, ix]

    _prog(0.45, f"Converting {len(iy)} populated pixels to WGS84…")

    if raster_srs.IsGeographic():
        # Raster is already geographic: geotransform x = longitude, y = latitude.
        # No axis-order ambiguity — use values directly.
        cell_lons_wgs = px_x
        cell_lats_wgs = px_y
    else:
        # Projected CRS (e.g. UTM): transform to WGS84.
        # Use OAMS_TRADITIONAL_GIS_ORDER on both ends so TransformPoints
        # returns (lon, lat, z) unambiguously.
        wgs84 = osr.SpatialReference()
        wgs84.SetWellKnownGeogCS("WGS84")
        wgs84.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        raster_srs.SetAxisMappingStrategy(osr.OAMS_TRADITIONAL_GIS_ORDER)
        to_wgs84 = osr.CoordinateTransformation(raster_srs, wgs84)
        coords = np.column_stack([px_x, px_y, np.zeros(len(px_x))])
        transformed = np.array(to_wgs84.TransformPoints(coords.tolist()))
        cell_lons_wgs = transformed[:, 0]
        cell_lats_wgs = transformed[:, 1]

    # 4. Nearest highway node for each populated pixel -------------------
    _prog(0.60, f"Finding nearest road node for {len(iy)} pixels…")
    access_lats, access_lons = _find_nearest_nodes(
        cell_lats_wgs, cell_lons_wgs, hw_lats, hw_lons
    )


    # 5. Write CSV -------------------------------------------------------
    _prog(0.80, "Writing population CSV…")
    rng = random.Random()

    with open(output_csv, 'w', newline='', encoding='utf-8') as fh:
        fh.write("OriginLat,OriginLon,AccessLat,AccessLon,People\n")

        for i in range(len(iy)):
            pop      = int(pixel_pops[i] + 0.5)
            orig_lat = float(cell_lats_wgs[i])
            orig_lon = float(cell_lons_wgs[i])
            acc_lat  = float(access_lats[i])
            acc_lon  = float(access_lons[i])

            # Convert pixel half-size to degrees for jitter
            # x_size is in UTM metres; rough degree conversion at this lat
            lat_r     = math.radians(orig_lat)
            half_dlat = (y_size * 0.5) * 180.0 / (40_075_000.0 * 0.5)
            half_dlon = (x_size * 0.5) * 180.0 / (math.cos(lat_r) * 40_075_000.0 * 0.5 + 1e-9)

            remaining = pop
            while remaining > 0:
                hh_size = min(rng.randint(min_hh, max_hh), remaining)
                hh_lat  = orig_lat + (rng.random() - 0.5) * 2 * half_dlat
                hh_lon  = orig_lon + (rng.random() - 0.5) * 2 * half_dlon
                fh.write(f"{hh_lat},{hh_lon},{acc_lat},{acc_lon},{hh_size}\n")
                remaining -= hh_size

    _prog(1.00, f"Done — wrote {output_csv}")
