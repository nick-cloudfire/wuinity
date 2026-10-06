#!/usr/bin/env python3
# This file is part of PREACT (GPL-3.0-or-later). It is NOT part of FireDX: it is the small driver PREACT's
# FireDxRunner writes next to its output and runs with the FireDX package on PYTHONPATH. FireDX itself (Copyright (c)
# 2025 Maria Faye Theodori, proprietary licence) is never copied into PREACT; this script only imports it.
#
# What it adds around FireDX's own entry point (firedx.generate.main):
#   * a check that every module FireDX imports is there, reported all at once, before anything runs;
#   * a check that the hosts the chosen path downloads from can be reached, before minutes of work;
#   * the choice of attribute path. FireDX's own attribute join (USACE NSI structures + CAL FIRE fire hazard
#     severity zones) only works in California with network access: outside the US the NSI returns nothing and the
#     join stops with "Could not infer feature type for run job", and outside California the FHSZ service returns no
#     features and the join stops on None. The "basic" path skips that join: it takes the footprints (FireDX's own
#     Microsoft/OSM download, or a file), computes FireDX's footprint metrics, assumes residential occupancy where
#     nothing says otherwise and assigns FireDX's building fuel models (firedx.bldg_fuel_models_mod.assign_bldgfm),
#     then runs FireDX's raster generation on them - which is the part a fire model needs, and works anywhere;
#   * FireDX's building fuel model table (building_fuel_models.csv), which goes with the codes in bfm.tif.
#
# Lines starting with "PREACT-FIREDX-" are read by FireDxRunner; everything else is shown as FireDX's log.
# Exit codes: 0 done, 2 bad arguments, 3 modules missing, 4 network needed and not reachable, 5 FireDX package not
# found, 6 attribute path not possible for this area, 7 no buildings, 1 anything else (a Python error).

import argparse
import importlib.util
import os
import sys
import traceback

# Every module FireDX's generate path imports, with the conda package that provides it (FireDX's environment.yml).
REQUIRED = [
    ("numpy", "numpy"), ("pandas", "pandas"), ("geopandas", "geopandas"), ("rasterio", "rasterio"),
    ("shapely", "shapely"), ("pyproj", "pyproj"), ("fiona", "fiona"), ("pyogrio", "pyogrio"), ("rtree", "rtree"),
    ("mercantile", "mercantile"), ("osmnx", "osmnx"), ("requests", "requests"), ("tqdm", "tqdm"),
    ("psutil", "psutil"), ("pyarrow", "pyarrow"), ("scipy", "scipy"), ("statsmodels", "statsmodels"),
]

# Where FireDX downloads from, by what for.
FOOTPRINT_HOSTS = [
    ("https://minedbuildings.z5.web.core.windows.net/global-buildings/dataset-links.csv", "Microsoft building footprints"),
    ("https://overpass-api.de/api/status", "OpenStreetMap buildings (Overpass)"),
]
ATTRIBUTE_HOSTS = [
    ("https://nsi.sec.usace.army.mil/nsiapi/structures?fmt=fc", "USACE National Structure Inventory (USA only)"),
    ("https://services1.arcgis.com/jUJYIo9tSA7EHvfZ/arcgis/rest/services", "CAL FIRE fire hazard severity zones (California only)"),
]

# California, coarsely (lon, lat): its coast, the 42nd parallel, the 120th meridian, the Nevada diagonal and the
# Colorado River. Only used to choose the attribute path; a place within a few km of the line may be misjudged, which
# is what --attributes is for.
CALIFORNIA = [
    (-124.4, 42.0), (-120.0, 42.0), (-120.0, 39.0), (-114.63, 35.0), (-114.6, 34.3), (-114.13, 34.27),
    (-114.72, 32.72), (-117.12, 32.53), (-118.5, 33.7), (-120.65, 34.55), (-121.9, 36.3), (-122.5, 37.5),
    (-123.0, 38.0), (-123.8, 39.6), (-124.4, 40.4), (-124.4, 42.0),
]

# The 48 contiguous states, as a box: only to say "outside the US" plainly.
CONUS = (-125.0, 24.4, -66.9, 49.4)


def say(tag, text):
    print("PREACT-FIREDX-" + tag + ": " + text, flush=True)


def inside(lon, lat, ring):
    hit = False
    j = len(ring) - 1
    for i in range(len(ring)):
        xi, yi = ring[i]
        xj, yj = ring[j]
        if (yi > lat) != (yj > lat) and lon < (xj - xi) * (lat - yi) / (yj - yi) + xi:
            hit = not hit
        j = i
    return hit


def missing_modules():
    return [(m, p) for m, p in REQUIRED if importlib.util.find_spec(m) is None]


def unreachable(hosts):
    import requests
    bad = []
    for url, what in hosts:
        try:
            # Any answer at all - a 405 to a HEAD, a 404 - means the host is reachable; only no answer is a problem.
            requests.head(url, timeout=15, allow_redirects=True)
        except Exception as e:  # noqa: BLE001 - every failure means the same thing here
            text = str(e)
            if "Tunnel connection failed" in text or "ProxyError" in text:
                why = "refused by the proxy: " + (text.split("Tunnel connection failed:")[-1].split("'")[0].strip()
                                                  if "Tunnel connection failed:" in text else type(e).__name__)
            elif "timed out" in text.lower():
                why = "timed out"
            else:
                why = type(e).__name__ + (": " + text.splitlines()[0][:120] if text else "")
            bad.append((url, what, why))
    return bad


def year_built_column(gdf):
    for c in ("YR_BUILT", "YEAR_BUILT_JOINED", "YEAR_BUILT", "YEARBUILT", "med_yr_blt"):
        if c in gdf.columns:
            return c
    return None


def basic_buildings(fbfm40, crs, aoi_geojson, footprints_path, fire_year, output_dir):
    """FireDX's footprints, metrics and building fuel models without its US/California attribute join."""
    import firedx.footprints as footprints
    from firedx.utils import read_data_as_gdf
    from firedx.bldg_fuel_models_mod import assign_bldgfm

    if footprints_path:
        say("STEP", "reading the building footprints from " + footprints_path)
        buildings = read_data_as_gdf(footprints_path)
        if buildings is None or buildings.empty:
            say("NO-BUILDINGS", footprints_path + " holds no footprints")
            sys.exit(7)
        if buildings.crs is None:
            say("ERROR", footprints_path + " has no coordinate reference system; FireDX cannot place its footprints")
            sys.exit(2)
        buildings = buildings.to_crs(crs)
    else:
        say("STEP", "downloading building footprints (Microsoft global ML footprints and OpenStreetMap)")
        try:
            buildings = footprints.get_footprints(aoi_geojson, crs, calculate_metrics_flag=False)
        except SystemExit:
            # FireDX ends the process (exit 0) when the download finds no building at all.
            say("NO-BUILDINGS", "the footprint download found no building in the area")
            sys.exit(7)

    # Only polygons are buildings; a points file (an address list) has no footprint to rasterise.
    buildings = buildings[buildings.geometry.notna() & buildings.geom_type.isin(["Polygon", "MultiPolygon"])].copy()
    if buildings.empty:
        say("NO-BUILDINGS", "no building footprint polygons in the area")
        sys.exit(7)
    buildings = buildings.reset_index(drop=True)

    if fire_year < 9999:
        column = year_built_column(buildings)
        if column:
            before = len(buildings)
            buildings = buildings[buildings[column].isna() | (buildings[column] <= fire_year)].copy()
            say("STEP", f"fire year {fire_year}: {before - len(buildings)} of {before} buildings built later left out "
                        f"(by {column})")
        else:
            say("NOTE", f"the footprints carry no year built, so the fire year {fire_year} could not leave out "
                        "buildings built after it")

    valid_area = "BLDG_AREA_m2" in buildings.columns and buildings["BLDG_AREA_m2"].notna().all() \
        and (buildings["BLDG_AREA_m2"] > 0).all()
    if not valid_area or "BLDG_SEPARATION_DIST_MIN" not in buildings.columns:
        say("STEP", f"measuring {len(buildings)} footprints (area, separation)")
        buildings = footprints.calculate_bldg_metrics(buildings)

    if "occtype" not in buildings.columns:
        buildings["occtype"] = "RES"
        say("NOTE", "no occupancy data outside FireDX's US attribute join: every building is taken as residential")
    else:
        buildings["occtype"] = buildings["occtype"].fillna("RES")

    if "BLDG_FUEL_MODEL" not in buildings.columns:
        buildings = assign_bldgfm(buildings)

    # FireDX's raster join works tile by tile and reads each building's tile_id, which only its own attribute join
    # assigns; footprints that skip that join (as these do) need it set the same way - the same 5 km tiles over the
    # same area, which generate.main builds again from the same raster.
    from firedx.tiling import create_tile_grid, assign_tile_ids
    from firedx.utils import aoi_geom_proj
    tiles = create_tile_grid(aoi_geom_proj(aoi_geojson, crs), tile_size=5000, target_crs=crs)
    buildings = assign_tile_ids(buildings, tiles, method="representative")

    prepared = os.path.join(output_dir, "buildings_prepared.geojson")
    buildings.to_file(prepared, driver="GeoJSON")
    say("STEP", f"{len(buildings)} buildings with FireDX fuel models written to {prepared}")
    return prepared


def main():
    parser = argparse.ArgumentParser(description="PREACT's driver for FireDX's building raster generation.")
    parser.add_argument("--fbfm40", required=True)
    parser.add_argument("--output-dir", required=True)
    parser.add_argument("--fire-year", type=int, default=9999)
    parser.add_argument("--footprints", default=None)
    parser.add_argument("--attributes", choices=["auto", "california", "basic"], default="auto")
    parser.add_argument("--check-only", action="store_true")
    args = parser.parse_args()

    say("PYTHON", sys.version.split()[0] + " at " + sys.executable)

    missing = missing_modules()
    if missing:
        say("MISSING-MODULES", ", ".join(m for m, _ in missing))
        say("INSTALL", "conda install -c conda-forge " + " ".join(sorted({p for _, p in missing})))
        return 3

    if importlib.util.find_spec("firedx") is None:
        say("NO-PACKAGE", "the firedx package is not importable from " + os.pathsep.join(sys.path))
        return 5
    try:
        import firedx  # noqa: F401 - imported for its own checks (its version lookup)
    except Exception as e:  # noqa: BLE001
        say("NO-PACKAGE", "firedx could not be imported: " + str(e).splitlines()[0])
        return 5
    say("PACKAGE", os.path.dirname(os.path.dirname(os.path.abspath(firedx.__file__))))

    import rasterio
    from firedx.utils import aoi_to_epsg4326

    with rasterio.open(args.fbfm40) as src:
        crs = src.crs
        bounds = src.bounds
    units = (crs.linear_units or "").lower() if crs is not None else ""
    if crs is None or not crs.is_projected or units not in ("metre", "meter", "metres", "meters"):
        say("ERROR", f"{args.fbfm40} is not in a projected CRS in metres ({crs}); FireDX measures footprints in its CRS")
        return 2

    aoi = aoi_to_epsg4326(bounds=bounds, crs=crs)
    ring = aoi["geometry"]["coordinates"][0]
    lons = [p[0] for p in ring]
    lats = [p[1] for p in ring]
    lon, lat = 0.5 * (min(lons) + max(lons)), 0.5 * (min(lats) + max(lats))
    in_california = inside(lon, lat, CALIFORNIA)
    in_conus = CONUS[0] <= lon <= CONUS[2] and CONUS[1] <= lat <= CONUS[3]
    say("AOI", f"{min(lons):.5f},{min(lats):.5f} -> {max(lons):.5f},{max(lats):.5f}; centre {lat:.5f},{lon:.5f}; "
               f"california={'yes' if in_california else 'no'}; usa={'yes' if in_conus else 'no'}")

    attributes = args.attributes
    if attributes == "auto":
        attributes = "california" if in_california else "basic"
    elif attributes == "california" and not in_california:
        say("NOT-CALIFORNIA", "FireDX's attribute join (USACE NSI structures and CAL FIRE fire hazard severity zones) "
                              "only has data in California, and this area's centre is not in California")
        return 6
    say("ATTRIBUTES", attributes)

    hosts = []
    if not args.footprints:
        hosts += FOOTPRINT_HOSTS
    if attributes == "california":
        hosts += ATTRIBUTE_HOSTS
    if hosts:
        bad = unreachable(hosts)
        for url, what, why in bad:
            say("OFFLINE", f"{what}: {url} ({why})")
        if bad:
            return 4

    if args.check_only:
        say("DONE", "checks only")
        return 0

    os.makedirs(args.output_dir, exist_ok=True)
    from firedx import generate

    footprints_in = args.footprints
    if attributes == "basic":
        footprints_in = basic_buildings(args.fbfm40, crs, aoi, args.footprints, args.fire_year, args.output_dir)

    say("STEP", "FireDX generate")
    try:
        generate.main(args.fbfm40, fire_year=args.fire_year, footprints_in=footprints_in, output_dir=args.output_dir)
    except SystemExit as e:
        if e.code not in (None, 0):
            raise
        # FireDX ends the process (exit 0) when the footprint download finds no building at all.
        say("NO-BUILDINGS", "FireDX found no building in the area")
        return 7

    from firedx.bldg_fuel_models_mod import bldg_fuel_models
    table = os.path.join(args.output_dir, "building_fuel_models.csv")
    bldg_fuel_models().to_csv(table, index=False, header=False)
    say("TABLE", table)
    say("DONE", args.output_dir)
    return 0


if __name__ == "__main__":
    try:
        code = main()
    except SystemExit:
        raise
    except Exception as e:  # noqa: BLE001
        traceback.print_exc()
        say("ERROR", f"{type(e).__name__}: {str(e).splitlines()[0] if str(e) else ''}")
        code = 1
    sys.exit(code)
