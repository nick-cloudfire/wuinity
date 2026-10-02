using System.Globalization;
using OSGeo.OSR;
using PREACT.Math;
using PREACT.Tools;
using PREACT.Utility;

namespace PREACT.Tests
{
    internal static partial class DownloaderTests
    {
        private static void RegisterTerrain(Runner runner)
        {
            runner.Add("dem: the OpenTopography request covers the UTM grid cut from the padded domain to its corners, with pixels to spare", DemMarginCoversGrid);
            runner.Add("dem: the downloader asks for the grown box itself, so the case build's own download gets the margin too", DemDownloadAsksForMargin);
        }

        /// <summary>
        /// Whether the lat/lon box [<paramref name="requestLowerLeft"/>, <paramref name="requestUpperRight"/>] contains the
        /// UTM grid a case cuts from [<paramref name="southWest"/>, <paramref name="northEast"/>] - the bounding box of its
        /// projected corners, snapped out to 30 m - with <paramref name="slackDegrees"/> to spare, checked exactly with PROJ
        /// along the grid's whole outline. Returns the smallest margin found, in degrees.
        /// </summary>
        internal static double UtmGridMargin(Vector2d requestLowerLeft, Vector2d requestUpperRight, Vector2d southWest, Vector2d northEast, int epsg)
        {
            (double xMin, double yMin, double xMax, double yMax) = RasterHarmonizer.ProjectBounds("EPSG:" + epsg,
                southWest.x, southWest.y, northEast.x, northEast.y);
            (xMin, yMin, xMax, yMax) = RasterHarmonizer.SnapOutward(xMin, yMin, xMax, yMax, 30.0);

            using (var utm = new SpatialReference(""))
            using (var wgs84 = new SpatialReference(""))
            {
                utm.ImportFromEPSG(epsg);
                utm.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                wgs84.ImportFromEPSG(4326);
                wgs84.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                using (var toLatLon = new CoordinateTransformation(utm, wgs84))
                {
                    double margin = double.MaxValue;
                    const int steps = 50;
                    for (int i = 0; i <= steps; ++i)
                    {
                        double t = (double)i / steps;
                        foreach ((double x, double y) in new[]
                                 {
                                     (xMin + t * (xMax - xMin), yMin), (xMin + t * (xMax - xMin), yMax),
                                     (xMin, yMin + t * (yMax - yMin)), (xMax, yMin + t * (yMax - yMin)),
                                 })
                        {
                            double[] p = { x, y, 0 };
                            toLatLon.TransformPoint(p);
                            double lon = p[0], lat = p[1];
                            margin = System.Math.Min(margin, System.Math.Min(
                                System.Math.Min(lat - requestLowerLeft.x, requestUpperRight.x - lat),
                                System.Math.Min(lon - requestLowerLeft.y, requestUpperRight.y - lon)));
                        }
                    }
                    return margin;
                }
            }
        }

        internal static void AssertCoversUtmGrid(Vector2d requestLowerLeft, Vector2d requestUpperRight, Vector2d southWest, Vector2d northEast,
            int epsg, string what, double slackDegrees = 2.0 / 3600.0)
        {
            double margin = UtmGridMargin(requestLowerLeft, requestUpperRight, southWest, northEast, epsg);
            Assert.True(margin >= slackDegrees, $"{what}: the UTM grid's outline comes within {margin:F5} deg of the request's edge "
                + $"(at least {slackDegrees:F5} needed)");
        }

        private static (Vector2d, Vector2d) Padded(Vector2d lowerLeft, Vector2d size, double padding)
        {
            return ElmfireCaseBuilder.PaddedBounds(new ElmfireCaseBuilder.Options { LowerLeftLatLon = lowerLeft, DomainSizeMetres = size, PaddingMetres = padding });
        }

        private static void DemMarginCoversGrid()
        {
            //Auburn2 as Nick made it: 2 degrees east of zone 10's meridian, at 39 N.
            Vector2d auburnLowerLeft = new Vector2d(38.818923064967734, -121.19282894713631);
            Vector2d auburnSize = new Vector2d(20939.844352100859, 18542.447478438728);
            (Vector2d sw, Vector2d ne) = Padded(auburnLowerLeft, auburnSize, 2000);
            Assert.Near(38.80096, sw.x, 1e-5, "the padded domain the build logged (south)");
            Assert.Near(-120.92834, ne.y, 1e-5, "the padded domain the build logged (east)");

            //What the build used to ask for - the padded box itself - does not reach the grid's corners.
            double before = UtmGridMargin(sw, ne, sw, ne, 32610);
            Assert.True(before < -0.004, $"the exact box leaves the grid's corners uncovered by {-before:F4} deg");

            //What it asks for now covers them, with pixels to spare, for the 30 m and the 90 m products.
            foreach (string type in new[] { OpenTopographyDownloader.DemTypeCopernicus30, OpenTopographyDownloader.DemTypeCopernicus90 })
            {
                (Vector2d ll, Vector2d ur) = OpenTopographyDownloader.RequestBounds(sw, ne, type, 32610);
                AssertCoversUtmGrid(ll, ur, sw, ne, 32610, "Auburn2 padded, " + type, 2.0 * OpenTopographyDownloader.PixelDegrees(type));
                //Not absurdly more: under a kilometre on each side.
                Assert.True(sw.x - ll.x < 0.01 && ll.y - sw.y > -0.015, $"{type}: a margin, not a different area ({ll.x:F4},{ll.y:F4})");
            }

            //The GUI's DEM-only step, unpadded: the fixed 0.005 deg it used to add, against what the grid needs.
            Vector2d ur0 = new Vector2d(auburnLowerLeft.x + 18542.447478438728 / 110574.0 * 1.0, -120.951398535358);
            double oldFixed = UtmGridMargin(new Vector2d(auburnLowerLeft.x - 0.005, auburnLowerLeft.y - 0.005),
                new Vector2d(ur0.x + 0.005, ur0.y + 0.005), auburnLowerLeft, ur0, 32610);
            (Vector2d gl, Vector2d gu) = OpenTopographyDownloader.RequestBounds(auburnLowerLeft, ur0, OpenTopographyDownloader.DemTypeCopernicus30, 32610);
            double now = UtmGridMargin(gl, gu, auburnLowerLeft, ur0, 32610);
            Assert.True(now > oldFixed && now >= 3.0 / 3600.0, $"the DEM-only step keeps {now * 3600:F1} arc-seconds to spare (it had {oldFixed * 3600:F1})");
            (Vector2d al, Vector2d au) = OpenTopographyDownloader.RequestBounds(sw, ne, OpenTopographyDownloader.DemTypeCopernicus30, 32610);
            Console.WriteLine(FormattableString.Invariant($"  INFO Auburn2 padded domain: the exact box missed the grid corners by {-before * 3600:F0}\", ")
                + FormattableString.Invariant($"the request now keeps {UtmGridMargin(al, au, sw, ne, 32610) * 3600:F1}\" to spare ({sw.x - al.x:F4} deg S, {ne.y - au.y:F4} deg E grown); ")
                + FormattableString.Invariant($"the DEM-only step's fixed 0.005 deg kept {oldFixed * 3600:F1}\", now {now * 3600:F1}\"."));

            //Mati (zone 34, 1 degree west of its meridian), a box on the edge of zone 33 at 60 N, and one in the south.
            foreach ((Vector2d ll, Vector2d size, int epsg, string what) in new[]
                     {
                         (new Vector2d(38.0123, 23.9), new Vector2d(12000, 9000), 32634, "Mati"),
                         (new Vector2d(60.1, 17.6), new Vector2d(40000, 40000), 32633, "60 N on a zone edge"),
                         (new Vector2d(-33.95, 151.0), new Vector2d(25000, 20000), 32756, "Sydney"),
                     })
            {
                (Vector2d psw, Vector2d pne) = Padded(ll, size, 2000);
                (Vector2d rl, Vector2d ru) = OpenTopographyDownloader.RequestBounds(psw, pne, OpenTopographyDownloader.DemTypeCopernicus30, epsg);
                AssertCoversUtmGrid(rl, ru, psw, pne, epsg, what);
            }

            //The zone's meridian, from the EPSG code or from the longitude.
            Assert.Equal(-123.0, DownloadArea.CentralMeridian(32610, 0), "zone 10");
            Assert.Equal(21.0, DownloadArea.CentralMeridian(32734, 0), "zone 34 south");
            Assert.Equal(-123.0, DownloadArea.CentralMeridian(0, -121.1), "from the longitude");
        }

        private static void DemDownloadAsksForMargin()
        {
            string query = null;
            byte[] tiff;
            string scratch = TempFolder("preact-dem-");
            try
            {
                //Any small GeoTIFF will do for an answer.
                var grid = new MasterGrid
                {
                    Header = new AscRaster.Header { Ncols = 4, Nrows = 4, CellSize = 30, CellSizeY = 30, XllCorner = 666000, YllCorner = 4300000, NoDataValue = -9999 },
                    Epsg = "EPSG:32610",
                };
                string sample = Path.Combine(scratch, "sample.tif");
                GeoTiffRasterWriter.WriteBand(grid, new float[4, 4], sample);
                tiff = File.ReadAllBytes(sample);

                using (var server = new StubHttpServer((r, b) => { query = r.Url.Query; return StubHttpServer.Reply.Bytes(tiff, "image/tiff"); }))
                {
                    OpenTopographyDownloader.BaseUrlOverride = server.BaseUrl + "/API/globaldem";
                    Vector2d sw = new Vector2d(38.80096, -121.21589), ne = new Vector2d(39.00346, -120.92834);
                    string output = Path.Combine(scratch, "dem.tif");
                    OpenTopographyDownloader.Download(sw, ne, "KEY", output).GetAwaiter().GetResult();
                    Assert.True(File.Exists(output), "written");

                    var parameters = query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                    double south = double.Parse(parameters["south"], CultureInfo.InvariantCulture);
                    double north = double.Parse(parameters["north"], CultureInfo.InvariantCulture);
                    double west = double.Parse(parameters["west"], CultureInfo.InvariantCulture);
                    double east = double.Parse(parameters["east"], CultureInfo.InvariantCulture);
                    AssertCoversUtmGrid(new Vector2d(south, west), new Vector2d(north, east), sw, ne, 32610, "the request the case build sends");
                    Assert.Equal("COP30", parameters["demtype"], "Copernicus 30 m by default");
                }
            }
            finally
            {
                OpenTopographyDownloader.BaseUrlOverride = null;
                try { Directory.Delete(scratch, true); } catch { }
            }
        }
    }
}
