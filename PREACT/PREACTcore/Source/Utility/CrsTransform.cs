//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using OSGeo.OSR;

namespace PREACT.Utility
{
    /// <summary>
    /// Puts a WGS84 latitude and longitude into a named projected CRS.
    ///
    /// Exists because "the coordinate system a raster is in" and "the coordinate system a scenario
    /// measures in" are not the same question, and answering the first with the second is how the Mati
    /// case came to hold zone-35 eastings in a zone-34 case: the same point 526 km from where it was
    /// meant to be, with nothing in the file to say so. Any code putting a point into a case takes the
    /// CRS from the case's own grid and comes through here.
    /// </summary>
    public static class CrsTransform
    {
        /// <summary>
        /// Transforms (latitude, longitude) into <paramref name="epsg"/> ("EPSG:32634" or a bare code),
        /// giving easting and northing. False when the CRS could not be resolved or the point does not
        /// transform into it at all.
        /// </summary>
        public static bool TryWgs84To(string epsg, double latitude, double longitude, out double x, out double y)
        {
            x = y = 0.0;

            if (string.IsNullOrEmpty(epsg))
            {
                return false;
            }

            string code = epsg.Replace("EPSG:", string.Empty).Trim();
            if (!int.TryParse(code, out int epsgCode))
            {
                return false;
            }

            SpatialReference wgs84 = null;
            SpatialReference target = null;
            CoordinateTransformation transform = null;
            try
            {
                wgs84 = new SpatialReference(string.Empty);
                wgs84.ImportFromEPSG(4326);
                //Longitude first, matching how the point is passed below. Stated rather than relied on:
                //EPSG:4326's own axis order is latitude first, and which of the two GDAL uses by default
                //has changed between major versions - a silent swap here puts the point in another
                //hemisphere.
                wgs84.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

                target = new SpatialReference(string.Empty);
                if (target.ImportFromEPSG(epsgCode) != 0)
                {
                    return false;
                }
                target.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

                transform = new CoordinateTransformation(wgs84, target);

                double[] p = { longitude, latitude, 0.0 };
                transform.TransformPoint(p);

                if (double.IsNaN(p[0]) || double.IsNaN(p[1]) || double.IsInfinity(p[0]) || double.IsInfinity(p[1]))
                {
                    return false;
                }

                x = p[0];
                y = p[1];
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                transform?.Dispose();
                target?.Dispose();
                wgs84?.Dispose();
            }
        }
    }
}
