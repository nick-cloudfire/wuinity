"""
WorldPop GeoTIFF downloader for WUInity population generation.

Mirrors WorldPopDownloader.cs in PREACTcore:
  1. Reverse-geocode domain centre → ISO3 country code (BigDataCloud API)
  2. Query WorldPop REST API for the country dataset matching the requested year
  3. Download the full country GeoTIFF (cached on disk)
  4. Clip to the domain bounding box using GDAL
  5. Return the clipped WGS84 GeoTIFF path

The clipped file is already WGS84 so population_tools.generate_population_csv()
can use it directly without any additional reprojection.
"""

import os
import json
import math
import urllib.request
import urllib.error

from qgis.core import QgsTask, QgsApplication, QgsMessageLog, Qgis

try:
    from osgeo import gdal
    gdal.UseExceptions()
except ImportError:
    gdal = None

LOG_TAG = "WUInity"
_WORLDPOP_API = "https://www.worldpop.org/rest/data/pop/wpgp"
_BIGDATA_API  = "https://api.bigdatacloud.net/data/reverse-geocode-client"

# Fallback ISO2 → ISO3 table for common countries (pycountry used first if available)
_ISO2_TO_ISO3 = {
    "AF":"AFG","AX":"ALA","AL":"ALB","DZ":"DZA","AS":"ASM","AD":"AND","AO":"AGO",
    "AI":"AIA","AQ":"ATA","AG":"ATG","AR":"ARG","AM":"ARM","AW":"ABW","AU":"AUS",
    "AT":"AUT","AZ":"AZE","BS":"BHS","BH":"BHR","BD":"BGD","BB":"BRB","BY":"BLR",
    "BE":"BEL","BZ":"BLZ","BJ":"BEN","BM":"BMU","BT":"BTN","BO":"BOL","BQ":"BES",
    "BA":"BIH","BW":"BWA","BV":"BVT","BR":"BRA","IO":"IOT","BN":"BRN","BG":"BGR",
    "BF":"BFA","BI":"BDI","CV":"CPV","KH":"KHM","CM":"CMR","CA":"CAN","KY":"CYM",
    "CF":"CAF","TD":"TCD","CL":"CHL","CN":"CHN","CX":"CXR","CC":"CCK","CO":"COL",
    "KM":"COM","CG":"COG","CD":"COD","CK":"COK","CR":"CRI","CI":"CIV","HR":"HRV",
    "CU":"CUB","CW":"CUW","CY":"CYP","CZ":"CZE","DK":"DNK","DJ":"DJI","DM":"DMA",
    "DO":"DOM","EC":"ECU","EG":"EGY","SV":"SLV","GQ":"GNQ","ER":"ERI","EE":"EST",
    "SZ":"SWZ","ET":"ETH","FK":"FLK","FO":"FRO","FJ":"FJI","FI":"FIN","FR":"FRA",
    "GF":"GUF","PF":"PYF","TF":"ATF","GA":"GAB","GM":"GMB","GE":"GEO","DE":"DEU",
    "GH":"GHA","GI":"GIB","GR":"GRC","GL":"GRL","GD":"GRD","GP":"GLP","GU":"GUM",
    "GT":"GTM","GG":"GGY","GN":"GIN","GW":"GNB","GY":"GUY","HT":"HTI","HM":"HMD",
    "VA":"VAT","HN":"HND","HK":"HKG","HU":"HUN","IS":"ISL","IN":"IND","ID":"IDN",
    "IR":"IRN","IQ":"IRQ","IE":"IRL","IM":"IMN","IL":"ISR","IT":"ITA","JM":"JAM",
    "JP":"JPN","JE":"JEY","JO":"JOR","KZ":"KAZ","KE":"KEN","KI":"KIR","KP":"PRK",
    "KR":"KOR","KW":"KWT","KG":"KGZ","LA":"LAO","LV":"LVA","LB":"LBN","LS":"LSO",
    "LR":"LBR","LY":"LBY","LI":"LIE","LT":"LTU","LU":"LUX","MO":"MAC","MG":"MDG",
    "MW":"MWI","MY":"MYS","MV":"MDV","ML":"MLI","MT":"MLT","MH":"MHL","MQ":"MTQ",
    "MR":"MRT","MU":"MUS","YT":"MYT","MX":"MEX","FM":"FSM","MD":"MDA","MC":"MCO",
    "MN":"MNG","ME":"MNE","MS":"MSR","MA":"MAR","MZ":"MOZ","MM":"MMR","NA":"NAM",
    "NR":"NRU","NP":"NPL","NL":"NLD","NC":"NCL","NZ":"NZL","NI":"NIC","NE":"NER",
    "NG":"NGA","NU":"NIU","NF":"NFK","MK":"MKD","MP":"MNP","NO":"NOR","OM":"OMN",
    "PK":"PAK","PW":"PLW","PS":"PSE","PA":"PAN","PG":"PNG","PY":"PRY","PE":"PER",
    "PH":"PHL","PN":"PCN","PL":"POL","PT":"PRT","PR":"PRI","QA":"QAT","RE":"REU",
    "RO":"ROU","RU":"RUS","RW":"RWA","BL":"BLM","SH":"SHN","KN":"KNA","LC":"LCA",
    "MF":"MAF","PM":"SPM","VC":"VCT","WS":"WSM","SM":"SMR","ST":"STP","SA":"SAU",
    "SN":"SEN","RS":"SRB","SC":"SYC","SL":"SLE","SG":"SGP","SX":"SXM","SK":"SVK",
    "SI":"SVN","SB":"SLB","SO":"SOM","ZA":"ZAF","GS":"SGS","SS":"SSD","ES":"ESP",
    "LK":"LKA","SD":"SDN","SR":"SUR","SJ":"SJM","SE":"SWE","CH":"CHE","SY":"SYR",
    "TW":"TWN","TJ":"TJK","TZ":"TZA","TH":"THA","TL":"TLS","TG":"TGO","TK":"TKL",
    "TO":"TON","TT":"TTO","TN":"TUN","TR":"TUR","TM":"TKM","TC":"TCA","TV":"TUV",
    "UG":"UGA","UA":"UKR","AE":"ARE","GB":"GBR","US":"USA","UM":"UMI","UY":"URY",
    "UZ":"UZB","VU":"VUT","VE":"VEN","VN":"VNM","VG":"VGB","VI":"VIR","WF":"WLF",
    "EH":"ESH","YE":"YEM","ZM":"ZMB","ZW":"ZWE",
}


def _iso2_to_iso3(iso2: str) -> str:
    try:
        import pycountry
        c = pycountry.countries.get(alpha_2=iso2.upper())
        if c:
            return c.alpha_3
    except ImportError:
        pass
    result = _ISO2_TO_ISO3.get(iso2.upper())
    if result:
        return result
    raise ValueError(f"Cannot convert ISO2 '{iso2}' to ISO3")


def _reverse_geocode_iso3(lat: float, lon: float) -> str:
    url = f"{_BIGDATA_API}?latitude={lat:.6f}&longitude={lon:.6f}&localityLanguage=en"
    with urllib.request.urlopen(url, timeout=15) as resp:
        data = json.loads(resp.read().decode())
    iso2 = data.get("countryCode", "")
    if not iso2:
        raise RuntimeError(f"BigDataCloud returned no countryCode for ({lat}, {lon})")
    return _iso2_to_iso3(iso2)


def _fetch_worldpop_url(iso3: str, year: int) -> str:
    """Return the direct download URL for the country/year GeoTIFF."""
    url = f"{_WORLDPOP_API}?iso3={iso3.upper()}"
    with urllib.request.urlopen(url, timeout=30) as resp:
        data = json.loads(resp.read().decode())

    datasets = data.get("data", [])
    if not datasets:
        raise RuntimeError(f"WorldPop returned no datasets for {iso3}")

    year_str = str(year)
    for entry in datasets:
        if entry.get("popyear") == year_str:
            files = entry.get("files", [])
            if not files:
                raise RuntimeError(f"No files listed for {iso3} year {year}")
            return files[0]

    raise RuntimeError(f"No WorldPop dataset found for {iso3} year {year}")


def _download_with_progress(url: str, dest_path: str, task: QgsTask) -> bool:
    """
    Stream-download url → dest_path, updating task progress.

    Downloads to a temporary ``.part`` file and only renames it into place on
    success, so an interrupted or cancelled download never leaves a truncated
    file that later runs would mistake for a valid cache entry.

    Returns True on success, False if the task was cancelled.
    """
    part_path = dest_path + ".part"
    with urllib.request.urlopen(url, timeout=600) as resp:
        total = int(resp.headers.get("Content-Length", 0))
        downloaded = 0
        chunk = 1 << 16  # 64 KB
        with open(part_path, "wb") as fh:
            while True:
                if task.isCanceled():
                    break
                buf = resp.read(chunk)
                if not buf:
                    break
                fh.write(buf)
                downloaded += len(buf)
                if total > 0:
                    task.setProgress(int(10 + 60 * downloaded / total))

    if task.isCanceled():
        if os.path.isfile(part_path):
            os.remove(part_path)
        return False

    os.replace(part_path, dest_path)
    return True


def _clip_to_bbox(src_path: str, dst_path: str,
                  west: float, south: float, east: float, north: float) -> None:
    """Clip a GeoTIFF to a WGS84 bounding box using GDAL (pixel-coordinate math)."""
    if gdal is None:
        raise RuntimeError("GDAL not available — cannot clip WorldPop raster.")

    src = gdal.Open(src_path, gdal.GA_ReadOnly)
    if src is None:
        raise RuntimeError(f"GDAL could not open: {src_path}")

    gt = src.GetGeoTransform()           # [x_origin, px_w, 0, y_origin, 0, px_h]
    px_min = max(0, int((west  - gt[0]) / gt[1]))
    px_max = min(src.RasterXSize, int(math.ceil((east  - gt[0]) / gt[1])))
    py_min = max(0, int((north - gt[3]) / gt[5]))   # gt[5] is negative
    py_max = min(src.RasterYSize, int(math.ceil((south - gt[3]) / gt[5])))

    width  = px_max - px_min
    height = py_max - py_min
    if width <= 0 or height <= 0:
        src = None
        raise RuntimeError(
            f"WorldPop raster does not cover the domain bbox "
            f"W={west:.4f} S={south:.4f} E={east:.4f} N={north:.4f}"
        )

    drv = gdal.GetDriverByName("GTiff")
    dst = drv.Create(dst_path, width, height, src.RasterCount, gdal.GDT_Float32,
                     ["COMPRESS=LZW", "TILED=YES"])

    new_gt = (
        gt[0] + px_min * gt[1],
        gt[1], gt[2],
        gt[3] + py_min * gt[5],
        gt[4], gt[5],
    )
    dst.SetGeoTransform(new_gt)
    dst.SetProjection(src.GetProjection())

    for b in range(1, src.RasterCount + 1):
        sb = src.GetRasterBand(b)
        db = dst.GetRasterBand(b)
        buf = sb.ReadAsArray(px_min, py_min, width, height)
        db.WriteArray(buf)
        nodata = sb.GetNoDataValue()
        if nodata is not None:
            db.SetNoDataValue(nodata)

    dst.FlushCache()
    dst = None
    src = None


# ---------------------------------------------------------------------------
# QgsTask
# ---------------------------------------------------------------------------

class _WorldPopDownloadTask(QgsTask):
    def __init__(self, center_lat, center_lon, west, south, east, north,
                 year, cache_folder, output_path, on_done):
        super().__init__("Downloading WorldPop", QgsTask.CanCancel)
        self._clat     = center_lat
        self._clon     = center_lon
        self._west     = west
        self._south    = south
        self._east     = east
        self._north    = north
        self._year     = year
        self._cache    = cache_folder
        self._out      = output_path
        self._on_done  = on_done
        self._error    = None

    def run(self):
        def _log(msg):
            QgsMessageLog.logMessage(msg, LOG_TAG, Qgis.Info)

        try:
            _log(f"Reverse-geocoding domain centre ({self._clat:.4f}, {self._clon:.4f})…")
            self.setProgress(2)
            iso3 = _reverse_geocode_iso3(self._clat, self._clon)
            _log(f"ISO3 country code: {iso3}")

            _log(f"Querying WorldPop API for {iso3} year {self._year}…")
            self.setProgress(5)
            tif_url = _fetch_worldpop_url(iso3, self._year)
            _log(f"Dataset URL: {tif_url}")

            # Country-level cache file (shared across runs)
            os.makedirs(self._cache, exist_ok=True)
            fname = f"worldpop_{iso3}_{self._year}.tif"
            country_path = os.path.join(self._cache, fname)

            if os.path.isfile(country_path):
                _log(f"Using cached country TIF: {country_path}")
                self.setProgress(70)
            else:
                _log(f"Downloading country TIF → {country_path} (this may take several minutes)…")
                self.setProgress(10)
                if not _download_with_progress(tif_url, country_path, self):
                    return False
                _log("Download complete.")
                self.setProgress(70)

            if self.isCanceled():
                return False

            _log("Clipping to domain bounding box…")
            self.setProgress(80)
            _clip_to_bbox(country_path, self._out,
                          self._west, self._south, self._east, self._north)
            _log(f"Clipped WorldPop saved to: {self._out}")

        except Exception as exc:
            self._error = str(exc)
            return False

        self.setProgress(100)
        return True

    def finished(self, success):
        self._on_done(success, self._error or "", self._out if success else None)


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def start_download(domain_layer, year, cache_folder, output_path, on_done):
    """
    Download, clip, and cache a WorldPop GeoTIFF for the domain.

    Parameters
    ----------
    domain_layer  : QgsVectorLayer  — the WUInity domain polygon.
    year          : int             — WorldPop population year (e.g. 2020).
    cache_folder  : str             — directory to store the full country TIF.
    output_path   : str             — destination path for the clipped TIF.
    on_done       : callable(success, message, tif_path_or_None)

    Returns the QgsTask — caller must keep a reference.
    """
    from qgis.core import QgsCoordinateReferenceSystem, QgsCoordinateTransform, QgsProject

    WGS84 = QgsCoordinateReferenceSystem("EPSG:4326")
    xform = QgsCoordinateTransform(domain_layer.crs(), WGS84, QgsProject.instance())

    extent = domain_layer.extent()
    lo = xform.transform(extent.xMinimum(), extent.yMinimum())
    hi = xform.transform(extent.xMaximum(), extent.yMaximum())

    west  = min(lo.x(), hi.x())
    east  = max(lo.x(), hi.x())
    south = min(lo.y(), hi.y())
    north = max(lo.y(), hi.y())

    # Small margin so cells on the edge are included
    margin = 0.05
    west  -= margin; east  += margin
    south -= margin; north += margin

    center_lat = (south + north) * 0.5
    center_lon = (west  + east)  * 0.5

    task = _WorldPopDownloadTask(
        center_lat=center_lat, center_lon=center_lon,
        west=west, south=south, east=east, north=north,
        year=year,
        cache_folder=cache_folder,
        output_path=output_path,
        on_done=on_done,
    )
    QgsApplication.taskManager().addTask(task)
    return task
