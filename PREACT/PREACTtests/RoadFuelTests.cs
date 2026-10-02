using OSGeo.GDAL;
using PREACT.Math;
using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>RoadFuelRasterizer (Nick's Aug 2026 road-to-fuel conversion, ported to v1) on a 20 x 20 synthetic grid.</summary>
    internal static class RoadFuelTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("roads in fuel: lanes become fuel 101 only where the fuel is non-burnable, not on buildings or nodata; Int16, georeferenced, original kept", BurnsOnlyNonBurnable);
            runner.Add("roads in fuel: a width paints a corridor, islets are counted, a raster in another zone or a building layer off-grid is refused", WidthIsletsAndRefusals);
        }

        private const double X0 = 666000.0, Y0 = 4300000.0, Cell = 30.0;
        private const int N = 20;

        /// <summary>
        /// Grass (102) everywhere, an urban strip (91) down column 10 cutting it in two, a single grass cell (3,3) ringed by
        /// urban, and no data at (18,5). x east and y north from the south-west corner.
        /// </summary>
        private static float[,] Fuel()
        {
            var fuel = new float[N, N];
            for (int x = 0; x < N; ++x)
            {
                for (int y = 0; y < N; ++y)
                {
                    fuel[x, y] = x == 10 ? 91f : 102f;
                    if (x >= 2 && x <= 4 && y >= 2 && y <= 4) fuel[x, y] = x == 3 && y == 3 ? 102f : 91f;
                }
            }
            fuel[18, 5] = -9999f;
            return fuel;
        }

        /// <summary>Writes a single-band raster the way a case holds one: north-up, in UTM 10N, nodata -9999.</summary>
        internal static void WriteRaster(string path, float[,] data, DataType type, int epsg = 32610)
        {
            int nx = data.GetLength(0), ny = data.GetLength(1);
            Gdal.AllRegister();
            using (Driver driver = Gdal.GetDriverByName("GTiff"))
            using (Dataset ds = driver.Create(path, nx, ny, 1, type, null))
            {
                ds.SetGeoTransform(new[] { X0, Cell, 0.0, Y0 + ny * Cell, 0.0, -Cell });
                using (var srs = new OSGeo.OSR.SpatialReference(""))
                {
                    srs.ImportFromEPSG(epsg);
                    srs.ExportToWkt(out string wkt, null);
                    ds.SetProjection(wkt);
                }
                var buffer = new double[nx * ny];
                for (int row = 0; row < ny; ++row)
                {
                    for (int x = 0; x < nx; ++x) buffer[row * nx + x] = data[x, ny - 1 - row];
                }
                using (Band band = ds.GetRasterBand(1))
                {
                    band.SetNoDataValue(-9999);
                    band.WriteRaster(0, 0, nx, ny, buffer, nx, ny, 0, 0);
                }
            }
        }

        /// <summary>A lane along cell row <paramref name="row"/>, west to east, in simulation coordinates (metres from the origin).</summary>
        private static SumoNetworkGeometry.Lane Row(int row, double fromX = 15.0, double toX = 585.0)
        {
            return new SumoNetworkGeometry.Lane
            {
                Id = "row" + row,
                EdgeId = "e" + row,
                Points = new[] { new Vector2d(fromX, row * Cell + 15.0), new Vector2d(toX, row * Cell + 15.0) },
            };
        }

        private static float At(string path, int x, int y)
        {
            float[,] data = AscRaster.ReadGeoTiff(path, 1, out AscRaster.Header _, out bool ok, out int _);
            Assert.True(ok, "read " + path);
            return data[x, y];
        }

        private static void BurnsOnlyNonBurnable()
        {
            string folder = Directory.CreateTempSubdirectory("preact-roads-").FullName;
            try
            {
                string fuel = Path.Combine(folder, "fbfm40.tif");
                WriteRaster(fuel, Fuel(), DataType.GDT_Int16);
                byte[] before = File.ReadAllBytes(fuel);

                //A building where the second road crosses the urban strip.
                var baa = new float[N, N];
                for (int x = 0; x < N; ++x) for (int y = 0; y < N; ++y) baa[x, y] = -9999f;
                baa[10, 15] = 120f;
                string buildings = Path.Combine(folder, "bldg_area_avg.tif");
                WriteRaster(buildings, baa, DataType.GDT_Float32);

                var log = new List<string>();
                string output = Path.Combine(folder, "out", "Auburn2_fbfm40_roads101.tif");
                RoadFuelRasterizer.Result r = RoadFuelRasterizer.Run(new RoadFuelRasterizer.Options
                {
                    FuelRasterPath = fuel,
                    OutputRasterPath = output,
                    Lanes = new[] { Row(5), Row(15) },
                    UtmOrigin = new Vector2d(X0, Y0),
                    UtmEpsg = 32610,
                    BuildingAreaRasterPath = buildings,
                    OriginalSource = "downloads/landfire/Auburn2_LF2024_fbfm40.tif",
                    Log = log.Add,
                });

                Assert.True(r.Ok, "ran: " + r.Message);
                Assert.Equal(40, r.RoadCells, "two roads of 20 cells");
                Assert.Equal(1, r.Changed, "only the urban cell road 5 crosses");
                Assert.Equal(1, r.SkippedBuildings, "the building cell is left to the building model");
                Assert.Equal(1, r.SkippedNoData, "the nodata cell is left as it is");
                Assert.Equal(37, r.SkippedBurnable, "grass stays grass");
                Assert.Equal(3, r.PatchesBefore, "west, east and the islet");
                Assert.Equal(2, r.PatchesAfter, "west and east joined across the road");
                Assert.True(r.IsletsBefore == 1 && r.IsletsAfter == 1, "the islet is not on a road");
                Assert.True(log.Any(l => l.Contains("1 of 40 road cells set to fuel model 101") && l.Contains("1 without fuel data")), string.Join(" | ", log));

                Assert.Equal(101f, At(output, 10, 5), "road 5 over the strip");
                Assert.Equal(91f, At(output, 10, 15), "road 15 over the building");
                Assert.Equal(102f, At(output, 9, 5), "grass beside it");
                Assert.Equal(-9999f, At(output, 18, 5), "nodata kept");
                Assert.Equal(91f, At(output, 10, 0), "the strip off the roads");

                using (Dataset ds = Gdal.Open(output, Access.GA_ReadOnly))
                using (Band band = ds.GetRasterBand(1))
                {
                    Assert.Equal(DataType.GDT_Int16, band.DataType, "Int16, as ELMFIRE reads FBFM");
                    double[] gt = new double[6];
                    ds.GetGeoTransform(gt);
                    Assert.True(gt[0] == X0 && gt[3] == Y0 + N * Cell && gt[1] == Cell && gt[5] == -Cell, "the source's grid");
                    Assert.True(ds.GetProjection().Contains("32610") || ds.GetProjection().Contains("UTM zone 10N"), "the source's CRS");
                }
                Assert.Equal("downloads/landfire/Auburn2_LF2024_fbfm40.tif", RoadFuelRasterizer.BurnedFrom(output), "it records what it was burned from");
                Assert.True(RoadFuelRasterizer.BurnedFrom(fuel) == null, "the original records nothing");
                Assert.True(before.SequenceEqual(File.ReadAllBytes(fuel)), "the original is untouched");

                //Again, over its own output (the GUI burns the case's fuel in place the second time): nothing more to change.
                RoadFuelRasterizer.Result again = RoadFuelRasterizer.Run(new RoadFuelRasterizer.Options
                {
                    FuelRasterPath = output,
                    OutputRasterPath = output,
                    Lanes = new[] { Row(5), Row(15) },
                    UtmOrigin = new Vector2d(X0, Y0),
                    UtmEpsg = 32610,
                });
                Assert.True(again.Ok && again.Changed == 1 && again.SkippedBurnable == 38, "a second pass changes only the building cell it is no longer told about: " + again.Summary);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        private static void WidthIsletsAndRefusals()
        {
            string folder = Directory.CreateTempSubdirectory("preact-roads-").FullName;
            try
            {
                string fuel = Path.Combine(folder, "fbfm40.tif");
                WriteRaster(fuel, Fuel(), DataType.GDT_Int16);

                //90 m wide: rows 4 to 6, so the islet's northern ring (y = 4) is crossed too.
                RoadFuelRasterizer.Result r = RoadFuelRasterizer.Run(new RoadFuelRasterizer.Options
                {
                    FuelRasterPath = fuel,
                    Lanes = new[] { Row(5, -600.0, 585.0) },
                    UtmOrigin = new Vector2d(X0, Y0),
                    UtmEpsg = 32610,
                    RoadWidthMetres = 90.0,
                });
                Assert.True(r.Ok, r.Message);
                Assert.True(r.OutputPath.EndsWith("fbfm40_roads101.tif"), "beside the source by default: " + r.OutputPath);
                Assert.Equal(3 + 3, r.Changed, "three strip cells and the ring's three northern cells");
                Assert.True(r.IsletsBefore == 1 && r.IsletsAfter == 0, "the islet is joined to the rest");
                Assert.Equal(1, r.PatchesAfter, "one patch");
                Assert.True(r.OutsideGrid == 1 && r.RoadCells == 60, $"a lane starting west of the grid is drawn up to its edge ({r.OutsideGrid} outside, {r.RoadCells} cells)");

                //Another zone: refused, nothing written.
                string elsewhere = Path.Combine(folder, "zone11.tif");
                WriteRaster(elsewhere, Fuel(), DataType.GDT_Int16, 32611);
                r = RoadFuelRasterizer.Run(new RoadFuelRasterizer.Options
                {
                    FuelRasterPath = elsewhere, Lanes = new[] { Row(5) }, UtmOrigin = new Vector2d(X0, Y0), UtmEpsg = 32610,
                });
                Assert.True(!r.Ok && r.Message.Contains("EPSG:32611") && !File.Exists(Path.Combine(folder, "zone11_roads101.tif")), "another zone: " + r.Message);

                //A building layer on another grid: refused rather than compared cell by cell against the wrong ground.
                string small = Path.Combine(folder, "baa_small.tif");
                WriteRaster(small, new float[5, 5], DataType.GDT_Float32);
                r = RoadFuelRasterizer.Run(new RoadFuelRasterizer.Options
                {
                    FuelRasterPath = fuel, Lanes = new[] { Row(5) }, UtmOrigin = new Vector2d(X0, Y0), BuildingAreaRasterPath = small,
                });
                Assert.True(!r.Ok && r.Message.Contains("same grid"), "off-grid buildings: " + r.Message);

                //No roads: said.
                r = RoadFuelRasterizer.Run(new RoadFuelRasterizer.Options { FuelRasterPath = fuel, Lanes = new SumoNetworkGeometry.Lane[0] });
                Assert.True(!r.Ok && r.Message.Contains("No road network"), r.Message);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }
}
