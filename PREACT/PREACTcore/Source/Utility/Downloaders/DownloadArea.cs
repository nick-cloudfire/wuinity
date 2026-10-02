using System;
using PREACT.Math;

namespace PREACT.Tools
{
    /// <summary>
    /// How much larger than a latitude/longitude box a download has to be for a UTM grid cut from that box to be
    /// covered to its corners.
    /// </summary>
    /// <remarks>
    /// Every grid here is cut as the UTM bounding box of the four projected corners of a lat/lon box
    /// (<c>RasterHarmonizer.BuildUtmMasterGrid</c>),
    /// snapped outward to whole cells. Away from the zone's central meridian the meridians and parallels are not
    /// grid lines, so that rectangle reaches past the lat/lon box at two of its corners - by the box's size times the
    /// sine of the grid convergence, about 500 m on a 22 km box 2 degrees from the meridian at 39 N (Auburn2). A
    /// download cut exactly to the box leaves those corners without data: elevation 0 beside a hillside, fuel nodata
    /// the fire cannot cross. On top of that the services snap the request to their own pixel lattice, which can lose
    /// a fraction of a pixel on any side - the 0.0002 degrees Auburn2's DEM came up short by.
    /// </remarks>
    public static class DownloadArea
    {
        private const double MetresPerDegreeLatitude = 110574.0;
        private const double MetresPerDegreeLongitudeAtEquator = 111320.0;

        /// <summary>
        /// The box grown so that the UTM rectangle around it is inside it, with <paramref name="extraDegrees"/> more on
        /// every side for the service's pixel snapping and the resampling kernel.
        /// </summary>
        /// <param name="lowerLeft">(lat, lon) of the south-west corner.</param>
        /// <param name="upperRight">(lat, lon) of the north-east corner.</param>
        /// <param name="utmEpsg">The zone the grid is cut in (326zz / 327zz); 0 for the zone of the box's centre.</param>
        public static (Vector2d LowerLeft, Vector2d UpperRight) CoverUtmGrid(Vector2d lowerLeft, Vector2d upperRight,
            int utmEpsg, double extraDegrees)
        {
            double south = System.Math.Min(lowerLeft.x, upperRight.x), north = System.Math.Max(lowerLeft.x, upperRight.x);
            double west = System.Math.Min(lowerLeft.y, upperRight.y), east = System.Math.Max(lowerLeft.y, upperRight.y);

            double centralMeridian = CentralMeridian(utmEpsg, 0.5 * (west + east));

            //Grid convergence, gamma = (lon - lon0) * sin(lat), largest at the box corner furthest from the meridian
            //and nearest the pole. The rectangle reaches past the box by (its other side) * sin(gamma) at two corners.
            double farthest = System.Math.Max(System.Math.Abs(west - centralMeridian), System.Math.Abs(east - centralMeridian));
            double poleward = System.Math.Max(System.Math.Abs(south), System.Math.Abs(north));
            double gamma = farthest * System.Math.PI / 180.0 * System.Math.Sin(poleward * System.Math.PI / 180.0);
            double sinGamma = System.Math.Sin(System.Math.Min(System.Math.Abs(gamma), 0.5));

            //A quarter more than the first-order figure: the parallels also bow, and the scale factor is not one.
            const double safety = 1.25;
            double cosLat = System.Math.Cos(poleward * System.Math.PI / 180.0);
            double heightMetres = (north - south) * MetresPerDegreeLatitude;
            double widthMetres = (east - west) * MetresPerDegreeLongitudeAtEquator
                                 * System.Math.Cos(System.Math.Min(System.Math.Abs(south), System.Math.Abs(north)) * System.Math.PI / 180.0);

            double lonMargin = safety * heightMetres * sinGamma / (MetresPerDegreeLongitudeAtEquator * System.Math.Max(cosLat, 0.01));
            double latMargin = safety * widthMetres * sinGamma / MetresPerDegreeLatitude;

            lonMargin += extraDegrees / System.Math.Max(cosLat, 0.01);
            latMargin += extraDegrees;

            return (new Vector2d(System.Math.Max(-90.0, south - latMargin), west - lonMargin),
                    new Vector2d(System.Math.Min(90.0, north + latMargin), east + lonMargin));
        }

        /// <summary>The central meridian of a UTM EPSG code, or of the zone <paramref name="longitude"/> is in.</summary>
        public static double CentralMeridian(int utmEpsg, double longitude)
        {
            int zone = 0;
            if ((utmEpsg > 32600 && utmEpsg <= 32660) || (utmEpsg > 32700 && utmEpsg <= 32760))
            {
                zone = utmEpsg % 100;
            }
            if (zone == 0)
            {
                zone = PREACT.Utility.UtmUtility.GetUtmZone(longitude);
            }
            return -183.0 + 6.0 * zone;
        }
    }
}
