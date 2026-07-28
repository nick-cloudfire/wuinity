//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using PREACT.Utility;
using PREACT.Math;

namespace PREACT.Input
{
    public class SimulationData
    {       
        Vector2d _utmOrigin;
        LatLngUTMConverter.UTMResult _utmData;
        Vector2d _lowerLeftLatLon;

        //The zone the simulation measures in, when it is not the one the domain's own corner falls in.
        //0 means it is.
        int _pinnedEpsgCode;

        public Vector2d UTMOrigin { get => _utmOrigin; }
        public LatLngUTMConverter.UTMResult UTMData { get => _utmData; }

        /// <summary>
        /// The EPSG code of the UTM zone every simulation coordinate is measured in.
        /// </summary>
        public int UtmEpsgCode
        {
            get => _pinnedEpsgCode != 0
                ? _pinnedEpsgCode
                : UtmUtility.GetUtmEpsgCode(_lowerLeftLatLon.x, _lowerLeftLatLon.y);
        }

        /// <summary>True when the simulation measures in a zone other than its corner's own.</summary>
        public bool UtmZoneIsPinned { get => _pinnedEpsgCode != 0; }


        public SimulationData(Vector2d lowerLeftLatLon) 
        {
            UpdateData(lowerLeftLatLon);
        }

        public void UpdateData(string lat, string lon, out bool success)
        {
            Vector2d latLon;
            if (double.TryParse(lat, out latLon.x) && double.TryParse(lon, out latLon.y))
            {
                success = true;
                UpdateData(latLon);
            }
            else
            {
                success = false;
                Engine.Message(null, Engine.LogType.InputError, "Could not parse latitude/longitude.");
            }
        }

        public void UpdateData(Vector2d lowerLeftLatLon)
        {
            _lowerLeftLatLon = lowerLeftLatLon;
            _utmData = LatLngUTMConverter.WGS84.convertLatLngToUtm(lowerLeftLatLon.x, lowerLeftLatLon.y);
            _utmOrigin = new Vector2d(_utmData.Easting, _utmData.Northing);

            //A pin describes the zone the fire data is in, and moving the domain does not move the fire
            //data - so it is re-applied against the new corner rather than dropped. Dropping it would
            //put the origin back in the corner's own zone the moment anyone nudged the domain, silently
            //restoring the misplacement the pin exists to prevent. Quietly, because this setter runs on
            //every edit of the corner.
            if (_pinnedEpsgCode != 0)
            {
                int requested = _pinnedEpsgCode;
                _pinnedEpsgCode = 0;
                Pin(requested, false, out bool _);
            }
        }

        /// <summary>
        /// Measures the simulation in the given UTM zone instead of the one the domain's south-west
        /// corner happens to fall in.
        ///
        /// This exists because raster data comes with a zone of its own, and the code that places a
        /// raster in the scene subtracts the simulation's origin from the raster's corner directly -
        /// which is only meaningful if both are measured in the same zone. A domain within a few
        /// kilometres of a zone boundary can easily disagree with its own fire data: Mati's corner is
        /// at 23.93 E, the boundary is at 24 E, and its ELMFIRE output is in zone 35 while the corner
        /// is in zone 34, which displaced the fire by 526 km.
        ///
        /// Adopting the data's zone is the cheap direction to resolve that in. UTM is conformal, and a
        /// zone is usable well beyond its nominal six degrees - scale error at three degrees outside is
        /// on the order of a metre per kilometre - so measuring a small domain in a neighbouring zone
        /// costs almost nothing, whereas reprojecting every raster costs a resampling pass and its
        /// interpolation error.
        /// </summary>
        public void PinToUtmEpsg(int epsgCode, out bool success)
        {
            Pin(epsgCode, true, out success);
        }

        private void Pin(int epsgCode, bool report, out bool success)
        {
            success = false;

            bool isWgs84Utm = (epsgCode >= 32601 && epsgCode <= 32660)
                              || (epsgCode >= 32701 && epsgCode <= 32760);
            if (!isWgs84Utm)
            {
                //Deliberately narrow. The simulation's whole coordinate system is a UTM easting and
                //northing in metres, and anything else - a state plane, a national grid, geographic
                //degrees - would need more than a different origin to work.
                if (report)
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        $"EPSG:{epsgCode} is not a WGS84 UTM zone, so the simulation cannot be measured in it. "
                        + "Its own zone is used instead, and any raster in EPSG:" + epsgCode + " will be misplaced.");
                }
                return;
            }

            int naturalEpsg = UtmUtility.GetUtmEpsgCode(_lowerLeftLatLon.x, _lowerLeftLatLon.y);
            if (epsgCode == naturalEpsg)
            {
                //Already measuring in that zone; nothing to pin, and no message worth printing.
                _pinnedEpsgCode = 0;
                success = true;
                return;
            }

            (double easting, double northing) origin = Wgs84ToUtm(_lowerLeftLatLon.x, _lowerLeftLatLon.y, "EPSG:" + epsgCode);
            if (double.IsNaN(origin.easting) || double.IsNaN(origin.northing))
            {
                if (report)
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        $"Could not measure the domain corner in EPSG:{epsgCode}; keeping its own zone.");
                }
                return;
            }

            //A zone or two away is ordinary - a domain sitting on a boundary is the whole reason this
            //exists - but a long way outside stretches the projection enough to matter: scale error
            //grows roughly with the square of the distance from the central meridian, reaching about a
            //metre per kilometre three degrees out and several times that beyond. Worth saying rather
            //than refusing, since it is still the lesser error compared with a displaced raster.
            int zoneDistance = System.Math.Abs((epsgCode % 100) - (naturalEpsg % 100));
            if (report && zoneDistance > 1)
            {
                Engine.Message(null, Engine.LogType.Warning,
                    $"The fire data's UTM zone is {zoneDistance} zones from the domain's own. Distances in the "
                    + "simulation are measured in the data's zone, so they are increasingly distorted the further "
                    + "the domain sits from it. Reproject the data if that matters.");
            }

            _pinnedEpsgCode = epsgCode;
            _utmOrigin = new Vector2d(origin.easting, origin.northing);

            //The zone number has to follow, or converting a simulation position back to latitude and
            //longitude would invert with the wrong central meridian and land in the wrong country. The
            //letter is a latitude band and does not change with the zone.
            _utmData.ZoneNumber = epsgCode % 100;
            _utmData.Easting = origin.easting;
            _utmData.Northing = origin.northing;

            if (report)
            {
                Engine.Message(null, Engine.LogType.Log,
                    $"Simulation coordinates pinned to EPSG:{epsgCode} (UTM zone {_utmData.ZoneNumber}) rather than "
                    + $"zone {naturalEpsg % 100}, which is the one the domain corner falls in. Origin is now "
                    + $"{origin.easting:F1}, {origin.northing:F1}.");
            }

            success = true;
        }

        public Vector2d GetSimulationPosition(Vector2d latLon)
        {
            LatLngUTMConverter.UTMResult utmPos = LatLngUTMConverter.WGS84.convertLatLngToUtm(latLon.x, latLon.y);
            if(utmPos.ZoneNumber != _utmData.ZoneNumber)
            {
                //The zone the simulation measures in, which is not the corner's own once it is pinned -
                //and reprojecting into the corner's zone then would be the very error being avoided.
                string utmEPSG = "EPSG:" + UtmEpsgCode;
                (double easting, double northing) eastNorth = Wgs84ToUtm(latLon.x, latLon.y, utmEPSG);
                return new Vector2d(eastNorth.easting, eastNorth.northing) - _utmOrigin;
            }
            else
            {
                return new Vector2d(utmPos.Easting, utmPos.Northing) - _utmOrigin;
            }
        }

        private static (double easting, double northing) Wgs84ToUtm(double lat, double lon, string utmEpsg)
        {
            var src = new OSGeo.OSR.SpatialReference("");
            src.ImportFromEPSG(4326); // WGS84

            var dst = new OSGeo.OSR.SpatialReference("");
            dst.ImportFromEPSG(int.Parse(utmEpsg.Replace("EPSG:", "")));

            var transform = new OSGeo.OSR.CoordinateTransformation(src, dst);

            double[] point = { lat, lon, 0 }; //be careful, some versions of GDAL uses different order
            transform.TransformPoint(point);

            return (point[0], point[1]); // easting, northing
        }


        public Vector2d GetWGS84FromSimulationPosition(Vector2d pos)
        {
            pos += _utmOrigin;
            LatLngUTMConverter.LatLng wgs84 = LatLngUTMConverter.WGS84.convertUtmToLatLng(pos.x, pos.y, _utmData.ZoneNumber, _utmData.ZoneLetter);
            return new Vector2d(wgs84.Lat, wgs84.Lng);
        }

        public Vector2d GetWGS84FromUTMPosition(Vector2d pos)
        {
            LatLngUTMConverter.LatLng wgs84 = LatLngUTMConverter.WGS84.convertUtmToLatLng(pos.x, pos.y, _utmData.ZoneNumber, _utmData.ZoneLetter);
            return new Vector2d(wgs84.Lat, wgs84.Lng);
        }
    }
}