using System.Globalization;
using OSGeo.GDAL;
using PREACT.Utility;
using PerilCore = global::kPERIL.kPERIL;

namespace PREACT.Tests
{
    /// <summary>
    /// The k-PERIL trigger boundary: which side of the WUI area it lands on, that the wrapper hands kPERILcore
    /// its rasters in kPERILcore's own layout, and that the saved boundary opens where it belongs.
    /// </summary>
    internal static class TriggerTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("k-PERIL: a fire spreading east puts the boundary west (upwind) of the WUI area", FireSpreadingEast);
            runner.Add("k-PERIL: a fire spreading north puts the boundary south of the WUI area", FireSpreadingNorth);
            runner.Add("k-PERIL: the wrapper matches kPERILcore reading the same GeoTIFFs itself", WrapperMatchesCoreLayout);
            runner.Add("k-PERIL: a real Mati realization matches kPERILcore's own layout (data permitting)", RealRealizationMatchesCore);
            runner.Add("k-PERIL: the saved boundary is north-up at the grid's corner, with a .prj, as GDAL reads it", SavedBoundaryGeoreferenced);
            runner.Add("k-PERIL: a boundary's file name always has an extension", BoundaryFileNames);
            runner.Add("k-PERIL: the spread ellipse is capped at the fire's MAX_LOW", LengthToBreadthCap);
        }

        private static void BoundaryFileNames()
        {
            Assert.Equal("trigger_boundary.asc", Evacuation.EvacuationManager.BoundaryFileName("trigger_boundary"), "Mati's OutputName gets .asc");
            Assert.Equal("trigger_buffer.asc", Evacuation.EvacuationManager.BoundaryFileName("trigger_buffer.asc"), "an .asc name is kept");
            Assert.Equal("b_west.asc", Evacuation.EvacuationManager.BoundaryFileName("b", "west"), "a group's name goes before the extension");
            Assert.Equal("b_west.asc", Evacuation.EvacuationManager.BoundaryFileName("b.asc", "west"), "also when it had one");
            Assert.Equal(Input.kPERILInput.DefaultOutputName + ".asc", Evacuation.EvacuationManager.BoundaryFileName(" "), "an empty name is the default");
        }

        private const int N = 201;
        private const int WuiLow = 99, WuiHigh = 101;

        /// <summary>
        /// Uniform fire, flat ground, 3x3 WUI area at the centre, RSET 30 min: the reviewer's synthetic case. The
        /// wind blows the way the fire spreads (from the opposite bearing), as it does in an ELMFIRE run.
        /// </summary>
        private static float[,] UniformBoundary(float spreadDirection, float windMph = 10f, float maxLengthToBreadth = 0f)
        {
            var ros = new float[N, N];
            var sd = new float[N, N];
            var ws = new float[N, N];
            var wd = new float[N, N];
            var slope = new float[N, N];
            var aspect = new float[N, N];
            for (int x = 0; x < N; ++x)
            {
                for (int y = 0; y < N; ++y)
                {
                    ros[x, y] = 10f;
                    sd[x, y] = spreadDirection;
                    ws[x, y] = windMph;
                    wd[x, y] = (spreadDirection + 180f) % 360f;
                }
            }

            var wui = new bool[N * N];
            for (int x = WuiLow; x <= WuiHigh; ++x)
            {
                for (int y = WuiLow; y <= WuiHigh; ++y) wui[x + y * N] = true;
            }

            var k = new kPERIL(30f, wui, ws, wd, ros, sd, 30f, null, slope, aspect, maxLengthToBreadth);
            k.Run();
            return k.TriggerBufferOutput;
        }

        /// <summary>How many cells the boundary reaches past the WUI area on each side, engine layout (y north).</summary>
        private static (int West, int East, int North, int South) Reach(float[,] boundary)
        {
            int west = 0, east = 0, north = 0, south = 0;
            for (int x = 0; x < boundary.GetLength(0); ++x)
            {
                for (int y = 0; y < boundary.GetLength(1); ++y)
                {
                    if (boundary[x, y] != 1f) continue;
                    if (x < WuiLow) west = System.Math.Max(west, WuiLow - x);
                    if (x > WuiHigh) east = System.Math.Max(east, x - WuiHigh);
                    if (y > WuiHigh) north = System.Math.Max(north, y - WuiHigh);
                    if (y < WuiLow) south = System.Math.Max(south, WuiLow - y);
                }
            }
            return (west, east, north, south);
        }

        private static void FireSpreadingEast()
        {
            float[,] b = UniformBoundary(90f);
            Assert.Equal(N, b.GetLength(0), "the boundary comes back in the engine's [x, y] layout (columns)");
            Assert.Equal(N, b.GetLength(1), "the boundary comes back in the engine's [x, y] layout (rows)");

            (int w, int e, int n, int s) = Reach(b);
            string reach = $"W {w} / E {e} / N {n} / S {s} cells";
            //A fire coming from the west reaches the area from there, so the cells it must be stopped at lie west.
            Assert.True(w >= 3 * System.Math.Max(1, e), "the boundary extends upwind (west), far more than downwind: " + reach);
            Assert.True(w > n && w > s, "the boundary reaches further west than across the wind: " + reach);
            Assert.True(System.Math.Abs(n - s) <= 1, "the boundary is symmetric across the wind (north/south): " + reach);
        }

        /// <summary>
        /// At 20 mi/h ELMFIRE's L/B (11.4) is above the default cap of 8, so the boundary is the same at the default and
        /// at 8 given explicitly; a cap of 1.5 makes the ellipse rounder - faster flanks and back - so the boundary
        /// covers more cells. Its head-fire reach upwind is the same: the head rate is the ROS whatever the ellipse.
        /// </summary>
        private static void LengthToBreadthCap()
        {
            Assert.Near(8.0, global::kPERIL.kPERIL.LengthToBreadth(20.0), 0.0, "L/B at 20 mi/h is capped at 8 by default");
            float[,] at8 = UniformBoundary(90f, 20f, 8f), atDefault = UniformBoundary(90f, 20f), at15 = UniformBoundary(90f, 20f, 1.5f);
            int Cells(float[,] b) => b.Cast<float>().Count(v => v == 1f);
            Assert.Equal(Cells(at8), Cells(atDefault), "the default cap is 8");
            Assert.Equal(Reach(at8).West, Reach(at15).West, "the upwind (head fire) reach does not depend on the cap");
            Assert.True(Cells(at15) > Cells(at8), $"a rounder ellipse gives a larger boundary ({Cells(at15)} cells at a cap of 1.5, {Cells(at8)} at 8)");
        }

        private static void FireSpreadingNorth()
        {
            (int w, int e, int n, int s) = Reach(UniformBoundary(0f));
            string reach = $"W {w} / E {e} / N {n} / S {s} cells";
            Assert.True(s >= 3 * System.Math.Max(1, n), "the boundary extends upwind (south): " + reach);
            Assert.True(s > w && s > e, "the boundary reaches further south than across the wind: " + reach);
            Assert.True(System.Math.Abs(w - e) <= 1, "the boundary is symmetric across the wind (east/west): " + reach);
        }

        // ------------------------------------------------------------------ against kPERILcore's own layout

        /// <summary>The rasters a k-PERIL run takes, as files, and what to do to each before it goes in.</summary>
        private sealed class PerilFiles
        {
            public string Ros, SpreadDirection, WindSpeed, WindDirection, Slope, Aspect, Wui;

            /// <summary>Applied cell by cell to the wind speed as read (ELMFIRE's mfws: ft/min, nodata -9999).</summary>
            public Func<float, float> WindSpeedToMph = v => v;

            public float RsetMinutes;
            public float CellSize;
        }

        /// <summary>
        /// Runs the same files both ways and compares cell for cell: kPERILcore's own reader (its layout, row 0 the
        /// north edge) straight into kPERILcore, and the engine's reader (<see cref="AscRaster"/>, y north) through
        /// the wrapper. Returns the Jaccard index of the two boundaries and the wrapper's boundary.
        /// </summary>
        private static (double Jaccard, int Cells, float[,] Boundary) CompareWithCore(PerilFiles f)
        {
            // ---- kPERILcore on its own, in its own layout
            float[,] cRos = ReadCoreLayout(f.Ros);
            float[,] cSd = ReadCoreLayout(f.SpreadDirection);
            float[,] cWs = Map(ReadCoreLayout(f.WindSpeed), f.WindSpeedToMph);
            float[,] cWd = ReadCoreLayout(f.WindDirection);
            float[,] cSlope = ReadCoreLayout(f.Slope);
            float[,] cAspect = ReadCoreLayout(f.Aspect);
            float[,] cWui = Map(ReadCoreLayout(f.Wui), v => v > 0f && v != -9999f ? 1f : 0f);

            var core = new PerilCore();
            core.perilData.importFireRastersByVariable(cRos, cSd);
            core.perilData.cellSize = f.CellSize;
            core.perilData.noDataValue = -9999f;
            core.perilData.importTopographyRastersByVariable(new float[cRos.GetLength(0), cRos.GetLength(1)], cSlope, cAspect);
            core.perilData.importWeatherRastersByVariable(cWs, cWd);
            core.perilData.importWuiRastersByVariable(cWui);
            float[,] native = core.getTriggerBoundary(core.getTravelTimeFromBrokenDownRos(core.breakdownRateOfSpread()), f.RsetMinutes);

            // ---- the engine's reader and the wrapper
            float[,] Read(string path)
            {
                float[,] r = AscRaster.Read(path, out AscRaster.Header _, out bool ok);
                Assert.True(ok && r != null, "read " + path);
                return r;
            }

            float[,] eRos = Read(f.Ros);
            int nx = eRos.GetLength(0), ny = eRos.GetLength(1);
            float[,] eWuiRaster = Read(f.Wui);
            var eWui = new bool[nx * ny];
            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y)
                {
                    float v = eWuiRaster[x, y];
                    eWui[x + y * nx] = v > 0f && v != -9999f;
                }
            }

            var wrapper = new kPERIL(f.RsetMinutes, eWui, Map(Read(f.WindSpeed), f.WindSpeedToMph), Read(f.WindDirection),
                eRos, Read(f.SpreadDirection), f.CellSize, null, Read(f.Slope), Read(f.Aspect));
            wrapper.Run();
            float[,] engine = wrapper.TriggerBufferOutput;

            Assert.Equal(nx, engine.GetLength(0), "wrapper boundary columns");
            Assert.Equal(ny, engine.GetLength(1), "wrapper boundary rows");
            Assert.Equal(ny, native.GetLength(0), "kPERILcore boundary rows");
            Assert.Equal(nx, native.GetLength(1), "kPERILcore boundary columns");

            int both = 0, either = 0, cells = 0, different = 0;
            for (int r = 0; r < ny; ++r)
            {
                for (int c = 0; c < nx; ++c)
                {
                    float e = engine[c, ny - 1 - r];
                    float n = native[r, c];
                    if (e != n) ++different;
                    bool p = e == 1f, q = n == 1f;
                    if (p) ++cells;
                    if (p && q) ++both;
                    if (p || q) ++either;
                }
            }

            Assert.True(cells > 0, "the boundary is not empty");
            Assert.Equal(0, different, "cells whose value differs between the wrapper and kPERILcore's own layout");
            return (either == 0 ? 1.0 : (double)both / either, cells, engine);
        }

        /// <summary>
        /// A GeoTIFF's first band the way kPERIL's own reader (kPERILdll's <c>perilInputOutput.ReadGeoTiff</c>, which
        /// kPERILcore leaves out so it needs no GDAL) fills it: <c>[row from the top, column]</c>.
        /// </summary>
        private static float[,] ReadCoreLayout(string path)
        {
            Gdal.AllRegister();
            using (Dataset ds = Gdal.Open(path, Access.GA_ReadOnly))
            {
                Assert.True(ds != null, "open " + path);
                Band band = ds.GetRasterBand(1);
                int width = band.XSize, height = band.YSize;
                var buffer = new float[width * height];
                band.ReadRaster(0, 0, width, height, buffer, width, height, 0, 0);
                var data = new float[height, width];
                for (int y = 0; y < height; ++y)
                {
                    for (int x = 0; x < width; ++x) data[y, x] = buffer[y * width + x];
                }
                return data;
            }
        }

        private static float[,] Map(float[,] raster, Func<float, float> f)
        {
            var result = new float[raster.GetLength(0), raster.GetLength(1)];
            for (int i = 0; i < raster.GetLength(0); ++i)
            {
                for (int j = 0; j < raster.GetLength(1); ++j) result[i, j] = f(raster[i, j]);
            }
            return result;
        }

        /// <summary>
        /// A non-square, spatially varying case written as GeoTIFFs, so a transposed or flipped layout cannot pass by
        /// symmetry: ROS rising to the north-east, spread and wind turning across the grid, a ridge, an off-centre
        /// L-shaped WUI area.
        /// </summary>
        private static void WrapperMatchesCoreLayout()
        {
            string dir = Directory.CreateTempSubdirectory("preact-kperil-").FullName;
            try
            {
                const int nx = 90, ny = 60;
                var grid = new MasterGrid
                {
                    Header = new AscRaster.Header { Ncols = nx, Nrows = ny, CellSize = 30, CellSizeY = 30, XllCorner = 750000, YllCorner = 4200000, NoDataValue = -9999 },
                    Epsg = "EPSG:32634",
                };

                float[,] F(Func<int, int, float> f)
                {
                    var r = new float[nx, ny];
                    for (int x = 0; x < nx; ++x) for (int y = 0; y < ny; ++y) r[x, y] = f(x, y);
                    return r;
                }

                string W(string stem, float[,] data)
                {
                    string path = Path.Combine(dir, stem + ".tif");
                    GeoTiffRasterWriter.WriteBand(grid, data, path);
                    return path;
                }

                var files = new PerilFiles
                {
                    Ros = W("vs", F((x, y) => x < 3 && y < 3 ? -9999f : 4f + 0.1f * x + 0.05f * y)),
                    SpreadDirection = W("sd", F((x, y) => (60f + 1.5f * y) % 360f)),
                    WindSpeed = W("ws", F((x, y) => 3f + 0.05f * x)),
                    WindDirection = W("wd", F((x, y) => (240f + 1.5f * y) % 360f)),
                    Slope = W("slp", F((x, y) => 20f * (float)System.Math.Exp(-System.Math.Pow((x - 30) / 10.0, 2)))),
                    Aspect = W("asp", F((x, y) => x < 30 ? 270f : 90f)),
                    Wui = W("wui", F((x, y) => (x >= 55 && x <= 60 && y >= 35 && y <= 44) || (x >= 55 && x <= 66 && y >= 35 && y <= 38) ? 1f : 0f)),
                    RsetMinutes = 45f,
                    CellSize = 30f,
                };

                (double jaccard, int cells, float[,] boundary) = CompareWithCore(files);
                Assert.Near(1.0, jaccard, 1e-12, "Jaccard of the wrapper's boundary against kPERILcore's own layout");

                //And the geography: spread towards 60-150 degrees (from the west-south-west to the south-east as y
                //grows), so the boundary lies mostly west of the area.
                int west = 0, east = 0;
                for (int x = 0; x < nx; ++x)
                {
                    for (int y = 0; y < ny; ++y)
                    {
                        if (boundary[x, y] != 1f) continue;
                        if (x < 55) ++west;
                        if (x > 66) ++east;
                    }
                }
                Assert.True(west > 3 * east, $"the boundary lies upwind, west of the area ({west} cells west, {east} east; {cells} in all)");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        /// <summary>
        /// The same comparison on a real Mati campaign realization: its ELMFIRE vs, spread_dir and mfws, its own
        /// wind direction, the case's slope/aspect and WUI area, and the WRSET its evacuation produced. Taken from
        /// <c>PREACT_TEST_REALIZATION</c> (<c>&lt;realization dir&gt;;&lt;case inputs dir&gt;;&lt;WRSET minutes&gt;</c>), else
        /// the bench's copy of WP1's campaign; skipped with a warning when neither is there.
        /// </summary>
        private static List<string> RealRealizationMatchesCore()
        {
            var warnings = new List<string>();
            string spec = Environment.GetEnvironmentVariable("PREACT_TEST_REALIZATION")
                          ?? "/home/claude/runs/wp1/mati/_output/campaign_mati_8a356e1e/realizations/0000001;"
                          + "/home/claude/runs/wp1/mati/elmfire/inputs;115.95";
            string[] parts = spec.Split(';');
            if (parts.Length != 3 || !Directory.Exists(parts[0]) || !Directory.Exists(parts[1]))
            {
                warnings.Add("no real realization on this machine (set PREACT_TEST_REALIZATION=<dir>;<inputs>;<WRSET min>); skipped");
                return warnings;
            }

            string outputs = Path.Combine(parts[0], "outputs");
            string Output(string stem) => Directory.GetFiles(outputs, stem + "_*.tif").OrderBy(s => s, StringComparer.Ordinal).Last();

            var files = new PerilFiles
            {
                Ros = Output(ElmfireStems.SpreadRate),
                SpreadDirection = Output(ElmfireStems.SpreadDirection),
                WindSpeed = Output(ElmfireStems.MidflameWindSpeed),
                WindSpeedToMph = v => v < 0f || float.IsNaN(v) ? 0f : (float)(v / ElmfireStems.FeetPerMinutePerMph),
                WindDirection = Path.Combine(parts[0], "weather", "wd.tif"),
                Slope = ElmfireStems.Tif(parts[1], ElmfireStems.Slope),
                Aspect = ElmfireStems.Tif(parts[1], ElmfireStems.Aspect),
                Wui = ElmfireStems.Tif(parts[1], ElmfireStems.WuiArea),
                RsetMinutes = float.Parse(parts[2], CultureInfo.InvariantCulture),
                CellSize = (float)MasterGrid.FromRasterFile(ElmfireStems.Tif(parts[1], ElmfireStems.Dem)).Header.CellSize,
            };

            (double jaccard, int cells, float[,] _) = CompareWithCore(files);
            Assert.Near(1.0, jaccard, 1e-12, "Jaccard against kPERILcore's own layout on the real realization");
            Console.WriteLine($"  INFO real realization {Path.GetFileName(parts[0])}: {cells} boundary cells "
                              + $"({cells * files.CellSize * files.CellSize / 1e6:F2} km2), Jaccard vs kPERILcore's layout {jaccard:F3}");
            return warnings;
        }

        // ------------------------------------------------------------------ the file

        private static void SavedBoundaryGeoreferenced()
        {
            string dir = Directory.CreateTempSubdirectory("preact-kperil-asc-").FullName;
            try
            {
                const int nx = 5, ny = 3;
                var b = new float[nx, ny];
                b[0, ny - 1] = 1f; //north-west corner cell
                b[nx - 1, 0] = 2f; //south-east corner cell
                string path = Path.Combine(dir, "0_boundary.asc");
                var origin = new Math.Vector2d(750300.0, 4204016.0);

                //On a thread that writes decimal commas, as Nick's Greek Windows does.
                CultureInfo before = CultureInfo.CurrentCulture;
                try
                {
                    CultureInfo.CurrentCulture = new CultureInfo("el-GR");
                    kPERIL.SaveToFile(b, 27.5f, path, origin, 32634);
                }
                finally
                {
                    CultureInfo.CurrentCulture = before;
                }

                Assert.True(File.Exists(Path.ChangeExtension(path, ".prj")), "a .prj is written beside the boundary");
                Assert.True(File.ReadAllText(path).Contains("cellsize 27.5"), "the cell size is written with a decimal point");

                Gdal.AllRegister();
                using (Dataset ds = Gdal.Open(path, Access.GA_ReadOnly))
                {
                    Assert.True(ds != null, "GDAL opens the boundary");
                    var gt = new double[6];
                    ds.GetGeoTransform(gt);
                    Assert.Near(origin.x, gt[0], 1e-6, "west edge");
                    Assert.Near(origin.y + ny * 27.5, gt[3], 1e-6, "north edge (the corner written is the south-west one)");
                    Assert.Near(27.5, gt[1], 1e-9, "cell width");
                    Assert.Near(-27.5, gt[5], 1e-9, "north-up: rows run south");

                    var row0 = new float[nx];
                    var rowLast = new float[nx];
                    Band band = ds.GetRasterBand(1);
                    band.ReadRaster(0, 0, nx, 1, row0, nx, 1, 0, 0);
                    band.ReadRaster(0, ny - 1, nx, 1, rowLast, nx, 1, 0, 0);
                    Assert.Equal(1f, row0[0], "GDAL's first row is the north edge: the north-west cell");
                    Assert.Equal(2f, rowLast[nx - 1], "GDAL's last row is the south edge: the south-east cell");

                    string wkt = ds.GetProjectionRef();
                    Assert.True(!string.IsNullOrEmpty(wkt), "GDAL reads the CRS from the .prj");
                    using (var srs = new OSGeo.OSR.SpatialReference(wkt))
                    {
                        srs.AutoIdentifyEPSG();
                        Assert.Equal("32634", srs.GetAuthorityCode(null), "the CRS is UTM zone 34N");
                    }
                }

                //And the engine's own reader agrees on the corner and gets the CRS back.
                float[,] back = AscRaster.Read(path, out AscRaster.Header h, out bool ok);
                Assert.True(ok, "the engine reads it back");
                Assert.Equal(32634, h.EpsgCode, "the engine reads the CRS back from the .prj");
                Assert.Equal(1f, back[0, ny - 1], "north-west cell survives the round trip");
                Assert.Equal(2f, back[nx - 1, 0], "south-east cell survives the round trip");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
