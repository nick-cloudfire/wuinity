"""WGS 84 <-> UTM, without PROJ or GDAL.

Transverse Mercator in Krueger's series to the 4th order (Karney 2011, "Transverse Mercator with an accuracy of
a few nanometres", eqs. 35-36), which is sub-millimetre within a zone. Used to place the synthetic cases: the
corner of a scenario is given in lat/lon, everything else is laid out in metres in the zone.
"""

import math

_A = 6378137.0
_F = 1.0 / 298.257223563
_K0 = 0.9996
_N = _F / (2.0 - _F)
_AA = _A / (1.0 + _N) * (1.0 + _N ** 2 / 4.0 + _N ** 4 / 64.0)
_ALPHA = (
    _N / 2.0 - 2.0 / 3.0 * _N ** 2 + 5.0 / 16.0 * _N ** 3 + 41.0 / 180.0 * _N ** 4,
    13.0 / 48.0 * _N ** 2 - 3.0 / 5.0 * _N ** 3 + 557.0 / 1440.0 * _N ** 4,
    61.0 / 240.0 * _N ** 3 - 103.0 / 140.0 * _N ** 4,
    49561.0 / 161280.0 * _N ** 4,
)
_BETA = (
    _N / 2.0 - 2.0 / 3.0 * _N ** 2 + 37.0 / 96.0 * _N ** 3 - 1.0 / 360.0 * _N ** 4,
    1.0 / 48.0 * _N ** 2 + 1.0 / 15.0 * _N ** 3 - 437.0 / 1440.0 * _N ** 4,
    17.0 / 480.0 * _N ** 3 - 37.0 / 840.0 * _N ** 4,
    4397.0 / 161280.0 * _N ** 4,
)
_E = math.sqrt(_F * (2.0 - _F))


def zone_of(lon):
    return int(math.floor((lon + 180.0) / 6.0)) + 1


def epsg_of(lat, lon):
    return (32600 if lat >= 0 else 32700) + zone_of(lon)


def _central_meridian(zone):
    return math.radians(-183.0 + 6.0 * zone)


def to_utm(lat, lon, zone=None):
    """(easting, northing) in metres in `zone` (default: the point's own), northern or southern by latitude."""
    if zone is None:
        zone = zone_of(lon)
    phi = math.radians(lat)
    lam = math.radians(lon) - _central_meridian(zone)
    t = math.sinh(math.atanh(math.sin(phi)) - _E * math.atanh(_E * math.sin(phi)))
    xi_p = math.atan2(t, math.cos(lam))
    eta_p = math.atanh(math.sin(lam) / math.sqrt(1.0 + t * t))
    xi = xi_p
    eta = eta_p
    for j, a in enumerate(_ALPHA, start=1):
        xi += a * math.sin(2 * j * xi_p) * math.cosh(2 * j * eta_p)
        eta += a * math.cos(2 * j * xi_p) * math.sinh(2 * j * eta_p)
    easting = 500000.0 + _K0 * _AA * eta
    northing = _K0 * _AA * xi
    if lat < 0:
        northing += 10000000.0
    return easting, northing


def to_latlon(easting, northing, zone, north=True):
    """(lat, lon) in degrees of a point in a UTM zone."""
    xi = (northing - (0.0 if north else 10000000.0)) / (_K0 * _AA)
    eta = (easting - 500000.0) / (_K0 * _AA)
    xi_p = xi
    eta_p = eta
    for j, b in enumerate(_BETA, start=1):
        xi_p -= b * math.sin(2 * j * xi) * math.cosh(2 * j * eta)
        eta_p -= b * math.cos(2 * j * xi) * math.sinh(2 * j * eta)
    chi = math.asin(math.sin(xi_p) / math.cosh(eta_p))
    # Conformal latitude back to geodetic, by fixed-point iteration (converges in a few steps).
    tau_p = math.tan(chi)
    tau = tau_p
    for _ in range(20):
        sigma = math.sinh(_E * math.atanh(_E * tau / math.sqrt(1.0 + tau * tau)))
        tau_i = tau * math.sqrt(1.0 + sigma * sigma) - sigma * math.sqrt(1.0 + tau * tau)
        d_tau = (tau_p - tau_i) / math.sqrt(1.0 + tau_i * tau_i) * (
            1.0 + (1.0 - _E * _E) * tau * tau) / ((1.0 - _E * _E) * math.sqrt(1.0 + tau * tau))
        tau += d_tau
        if abs(d_tau) < 1e-14:
            break
    lat = math.degrees(math.atan(tau))
    lon = math.degrees(_central_meridian(zone) + math.atan2(math.sinh(eta_p), math.cos(xi_p)))
    return lat, lon


def utm_wkt(zone, north=True):
    """An ESRI-style .prj (WKT1) for a WGS 84 / UTM zone, which GDAL reads as EPSG:326zz/327zz."""
    name = "WGS_1984_UTM_Zone_%d%s" % (zone, "N" if north else "S")
    cm = -183 + 6 * zone
    return ('PROJCS["%s",GEOGCS["GCS_WGS_1984",DATUM["D_WGS_1984",SPHEROID["WGS_1984",6378137.0,298.257223563]],'
            'PRIMEM["Greenwich",0.0],UNIT["Degree",0.0174532925199433]],PROJECTION["Transverse_Mercator"],'
            'PARAMETER["False_Easting",500000.0],PARAMETER["False_Northing",%s],PARAMETER["Central_Meridian",%d.0],'
            'PARAMETER["Scale_Factor",0.9996],PARAMETER["Latitude_Of_Origin",0.0],UNIT["Meter",1.0]]'
            % (name, "0.0" if north else "10000000.0", cm))
