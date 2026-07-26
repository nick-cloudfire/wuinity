//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

namespace PREACT.Utility
{
    /// <summary>
    /// UTM zone/EPSG lookup from WGS84 lat/lon, shared by every downloader and by
    /// <see cref="SimulationData"/> that previously carried its own copy of this formula.
    /// </summary>
    public static class UtmUtility
    {
        public static int GetUtmZone(double longitude)
        {
            return (int)System.Math.Floor((longitude + 180.0) / 6.0) + 1;
        }

        /// <summary>EPSG code (e.g. 32634) for the UTM zone containing (latitude, longitude).</summary>
        public static int GetUtmEpsgCode(double latitude, double longitude)
        {
            int zone = GetUtmZone(longitude);
            return latitude >= 0
                ? 32600 + zone   // WGS84 / UTM north
                : 32700 + zone;  // WGS84 / UTM south
        }

        /// <summary>"EPSG:32634"-style string for the UTM zone containing (latitude, longitude).</summary>
        public static string GetUtmEpsg(double latitude, double longitude)
        {
            return "EPSG:" + GetUtmEpsgCode(latitude, longitude);
        }
    }
}
