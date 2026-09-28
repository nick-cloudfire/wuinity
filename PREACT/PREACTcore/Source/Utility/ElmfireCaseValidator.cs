using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace PREACT.Utility
{
    /// <summary>
    /// Checks a prepared ELMFIRE case against its own master grid before the model is asked to run it.
    /// </summary>
    /// <remarks>
    /// ELMFIRE reads its rasters by filename and trusts them. It does not compare their geotransforms, so two
    /// layers on different grids are read cell-for-cell as if they lined up: a fuel raster offset by a
    /// kilometre burns the wrong vegetation, a DEM in the wrong UTM zone drives spread up slopes that are not
    /// there, and both produce a complete, plausible-looking fire. That is the failure this exists to catch,
    /// because nothing downstream can — a misregistered case does not error, it answers wrongly.
    ///
    /// Three classes of problem, kept apart because they need different responses:
    ///
    /// - <b>Missing</b> layers ELMFIRE requires. It stops on these itself, but with its own
    ///   <c>CHECK_FILEPATH_IS_SET</c> message that names a namelist key rather than a file.
    /// - <b>Misregistration</b>: CRS, size, origin or cell size disagreeing with the master grid. Silent.
    /// - <b>Non-finite data</b>: NaN and infinity, which propagate through the level-set solver and come back
    ///   as a fire that stops spreading for no stated reason.
    /// </remarks>
    public static class ElmfireCaseValidator
    {
        /// <summary>Layers ELMFIRE cannot run without, beyond the fuel model, which is resolved separately.</summary>
        private static readonly string[] RequiredStems =
        {
            ElmfireStems.Dem, ElmfireStems.Slope, ElmfireStems.Aspect, ElmfireStems.Adj, ElmfireStems.Phi,
            ElmfireStems.WindSpeed, ElmfireStems.WindDirection, ElmfireStems.M1, ElmfireStems.M10, ElmfireStems.M100,
        };

        /// <summary>The weather series. These five must agree with each other on band count.</summary>
        private static readonly string[] WeatherStems = ElmfireStems.Weather;

        public class Problem
        {
            public string Raster;
            public string Message;

            /// <summary>True when the case cannot run, as against being merely suspect.</summary>
            public bool Fatal;

            public override string ToString() => (Raster == null ? "" : Raster + ": ") + Message;
        }

        public class Report
        {
            public List<Problem> Problems = new List<Problem>();
            public int RastersChecked;

            /// <summary>Bands the weather series carries, or 0 if it could not be determined.</summary>
            public int WeatherBands;

            public bool Ok
            {
                get
                {
                    foreach (Problem p in Problems) { if (p.Fatal) return false; }
                    return true;
                }
            }

            public IEnumerable<Problem> Fatal
            {
                get { foreach (Problem p in Problems) { if (p.Fatal) yield return p; } }
            }
        }

        /// <summary>
        /// Validates every raster the case is expected to carry.
        /// </summary>
        /// <param name="fuelStem">The fuel stem the namelist references (<c>fbfm40</c>/<c>fbfm13</c>), or
        /// null when the case has neither — itself a fatal problem.</param>
        /// <param name="extraStems">Optional layers that are only required when the case uses them (canopy,
        /// the building set), checked for registration if present and not reported when absent.</param>
        public static Report Validate(string inputsDirectory, MasterGrid grid, string fuelStem,
            IEnumerable<string> extraStems = null, Action<string> log = null)
        {
            var report = new Report();

            if (grid == null)
            {
                Add(report, null, "The case has no master grid to validate against.", fatal: true);
                return report;
            }

            var required = new List<string>(RequiredStems);
            if (string.IsNullOrEmpty(fuelStem))
            {
                Add(report, null,
                    "The case carries neither fbfm40.tif nor fbfm13.tif, so ELMFIRE has no fuel model to " +
                    "spread through.", fatal: true);
            }
            else
            {
                required.Add(fuelStem);
            }

            //Optional layers are validated the same way but never reported as missing: a case with no canopy
            //is a surface-fire case, which is a choice, whereas a canopy raster on the wrong grid is a fault.
            var optional = new List<string>();
            if (extraStems != null)
            {
                foreach (string stem in extraStems)
                {
                    if (!string.IsNullOrEmpty(stem)) optional.Add(stem);
                }
            }

            var bandCounts = new Dictionary<string, int>();

            foreach (string stem in required)
            {
                string path = Path.Combine(inputsDirectory, stem + ".tif");
                if (!File.Exists(path))
                {
                    Add(report, stem + ".tif", "required by ELMFIRE but not in the case.", fatal: true);
                    continue;
                }
                CheckRaster(report, path, stem, grid, bandCounts);
            }

            foreach (string stem in optional)
            {
                string path = Path.Combine(inputsDirectory, stem + ".tif");
                if (!File.Exists(path)) continue;
                CheckRaster(report, path, stem, grid, bandCounts);
            }

            CheckWeatherBandsAgree(report, bandCounts);

            report.WeatherBands = bandCounts.TryGetValue(ElmfireStems.WindSpeed, out int wsBands) ? wsBands : 0;

            if (log != null)
            {
                if (report.Ok && report.Problems.Count == 0)
                {
                    log($"  validate: {report.RastersChecked} rasters on the master grid " +
                        $"({grid.Header.Ncols}x{grid.Header.Nrows} at {grid.Header.CellSize:F1} m, {grid.Epsg})" +
                        (report.WeatherBands > 0 ? $", weather series {report.WeatherBands} band(s)" : "") + ".");
                }
                else
                {
                    foreach (Problem p in report.Problems)
                    {
                        log("  validate: " + (p.Fatal ? "" : "(warning) ") + p);
                    }
                }
            }

            return report;
        }

        /// <summary>
        /// Checks that every raster a namelist names is there (the ones ELMFIRE requires) and lies on the grid of the
        /// DEM it names: size, origin, cell size and CRS. The same check the case builder runs, applied to the
        /// namelist that is about to run - a template, a hand-edited namelist - rather than to the stems the builder
        /// knows, so a raster left on an old grid is refused with its key and both sizes instead of ELMFIRE
        /// segfaulting or stopping with "raster dimensions mismatch" once per realization.
        /// </summary>
        /// <param name="includeWeather">Whether to check the <c>WEATHER_DIRECTORY</c> rasters too; a campaign draws its
        /// own weather per realization, so it leaves them out.</param>
        /// <param name="inputsDirectory">Replaces the namelist's <c>FUELS_AND_TOPOGRAPHY_DIRECTORY</c> when given.</param>
        public static Report ValidateNamelistRasters(string[] namelistLines, string runDirectory, bool includeWeather,
            string inputsDirectory = null, Action<string> log = null)
        {
            var report = new Report();
            List<ElmfireStems.NamelistRaster> rasters = ElmfireStems.ReferencedRasters(namelistLines, runDirectory, inputsDirectory);

            ElmfireStems.NamelistRaster dem = rasters.Find(r => r.Key == "DEM_FILENAME");
            if (dem == null || !File.Exists(dem.Path))
            {
                Add(report, dem == null ? "DEM_FILENAME" : Describe(dem),
                    "the namelist's DEM is not there, and ELMFIRE takes the grid - domain, cell size and CRS - from it.",
                    fatal: true);
                return report;
            }

            MasterGrid grid;
            try
            {
                grid = MasterGrid.FromRasterFile(dem.Path);
            }
            catch (Exception e)
            {
                Add(report, Describe(dem), "cannot be read as the case grid: " + e.Message, fatal: true);
                return report;
            }

            var bandCounts = new Dictionary<string, int>();
            foreach (ElmfireStems.NamelistRaster r in rasters)
            {
                if (r.Weather && !includeWeather) continue;
                if (!File.Exists(r.Path))
                {
                    if (r.Required) Add(report, Describe(r), "required by ELMFIRE but not there.", fatal: true);
                    continue;
                }

                CheckRaster(report, r.Path, r.Weather ? WeatherStemFor(r.Key) : r.Stem, grid, bandCounts,
                    Describe(r), checkFinite: false, gridName: $"the case grid ({Path.GetFileName(dem.Path)})");
            }

            if (includeWeather) CheckWeatherBandsAgree(report, bandCounts);

            if (log != null)
            {
                foreach (Problem p in report.Problems) log("  namelist rasters: " + (p.Fatal ? "" : "(warning) ") + p);
            }

            return report;
        }

        private static string Describe(ElmfireStems.NamelistRaster r) => $"{r.Key} = '{r.Stem}' ({r.Path})";

        /// <summary>The weather stem a weather key stands for, so the band-count check compares like with like.</summary>
        private static string WeatherStemFor(string key)
        {
            switch (key)
            {
                case "WS_FILENAME": return ElmfireStems.WindSpeed;
                case "WD_FILENAME": return ElmfireStems.WindDirection;
                case "M1_FILENAME": return ElmfireStems.M1;
                case "M10_FILENAME": return ElmfireStems.M10;
                case "M100_FILENAME": return ElmfireStems.M100;
                default: return key;
            }
        }

        /// <summary>
        /// The five weather rasters have to carry the same number of bands.
        /// </summary>
        /// <remarks>
        /// ELMFIRE reads all of them against one <c>NUM_METEOROLOGY_TIMES</c>, and the case builder counts
        /// that from <c>ws.tif</c> alone. So a wind series shorter than the moisture series does not error —
        /// ELMFIRE reads past the end of the shorter rasters. Cheap to check, invisible otherwise.
        /// </remarks>
        private static void CheckWeatherBandsAgree(Report report, Dictionary<string, int> bandCounts)
        {
            int reference = -1;
            string referenceStem = null;

            foreach (string stem in WeatherStems)
            {
                if (!bandCounts.TryGetValue(stem, out int bands)) continue;

                if (reference < 0)
                {
                    reference = bands;
                    referenceStem = stem;
                    continue;
                }

                if (bands != reference)
                {
                    Add(report, stem + ".tif",
                        $"has {bands} band(s) but {referenceStem}.tif has {reference}. ELMFIRE reads all five " +
                        "weather rasters against one NUM_METEOROLOGY_TIMES, which is taken from ws.tif, so the " +
                        "shorter ones would be read past their end.", fatal: true);
                }
            }
        }

        private static void CheckRaster(Report report, string path, string stem, MasterGrid grid,
            Dictionary<string, int> bandCounts, string displayName = null, bool checkFinite = true,
            string gridName = "the master grid")
        {
            string name = displayName ?? stem + ".tif";
            Gdal.AllRegister();

            Dataset ds = null;
            try
            {
                ds = Gdal.Open(path, Access.GA_ReadOnly);
            }
            catch (Exception e)
            {
                Add(report, name, "could not be opened: " + e.Message, fatal: true);
                return;
            }

            if (ds == null)
            {
                Add(report, name, "could not be opened as a raster.", fatal: true);
                return;
            }

            try
            {
                ++report.RastersChecked;
                bandCounts[stem] = ds.RasterCount;

                if (ds.RasterXSize != grid.Header.Ncols || ds.RasterYSize != grid.Header.Nrows)
                {
                    Add(report, name,
                        $"is {ds.RasterXSize}x{ds.RasterYSize} but {gridName} is " +
                        $"{grid.Header.Ncols}x{grid.Header.Nrows}. ELMFIRE reads every raster cell-for-cell, " +
                        "so a different size means the layers do not describe the same ground.", fatal: true);
                    //Still worth reporting the rest, since a size mismatch usually comes with others.
                }

                CheckGeoTransform(report, ds, name, grid, gridName);
                CheckProjection(report, ds, name, grid, gridName);
                if (checkFinite) CheckFinite(report, ds, name);
            }
            finally
            {
                ds.Dispose();
            }
        }

        private static void CheckGeoTransform(Report report, Dataset ds, string name, MasterGrid grid,
            string gridName = "the master grid")
        {
            double[] gt = new double[6];
            ds.GetGeoTransform(gt);

            if (gt[2] != 0.0 || gt[4] != 0.0)
            {
                Add(report, name, $"is rotated (skew {gt[2]}, {gt[4]}); the grid must be axis-aligned.", fatal: true);
                return;
            }

            double cell = grid.Header.CellSize;

            //A tenth of a cell: below that, a difference cannot move a value into another cell, and warped
            //rasters routinely differ in the last bits. Above it, layers genuinely do not line up.
            double tolerance = 0.1 * cell;

            if (System.Math.Abs(gt[1] - cell) > 0.001 * cell || System.Math.Abs(-gt[5] - cell) > 0.001 * cell)
            {
                Add(report, name,
                    $"has {gt[1]} x {-gt[5]} m cells but {gridName} has {cell} m. Every distance ELMFIRE " +
                    "and k-PERIL measure comes from the grid's own cell size.", fatal: true);
            }

            double top = grid.YMax;
            if (System.Math.Abs(gt[0] - grid.XMin) > tolerance || System.Math.Abs(gt[3] - top) > tolerance)
            {
                double dx = gt[0] - grid.XMin;
                double dy = gt[3] - top;
                Add(report, name,
                    $"starts at ({gt[0]:F1}, {gt[3]:F1}) but {gridName} starts at " +
                    $"({grid.XMin:F1}, {top:F1}) - offset by ({dx:F1}, {dy:F1}) m, " +
                    $"about ({dx / cell:F1}, {dy / cell:F1}) cells. The layers are misregistered, which " +
                    "ELMFIRE cannot detect: it would burn one place's fuel with another place's terrain.",
                    fatal: true);
            }
        }

        private static void CheckProjection(Report report, Dataset ds, string name, MasterGrid grid,
            string gridName = "the master grid")
        {
            string wkt = ds.GetProjection();
            if (string.IsNullOrEmpty(wkt))
            {
                Add(report, name,
                    "carries no projection, so nothing can confirm it is on the same ground as the rest of " +
                    "the case.", fatal: false);
                return;
            }

            if (string.IsNullOrEmpty(grid.Epsg)) return; //nothing to compare against

            string code = null;
            SpatialReference srs = null;
            try
            {
                srs = new SpatialReference(wkt);
                srs.AutoIdentifyEPSG();
                code = srs.GetAuthorityCode(null);
            }
            catch { }
            finally
            {
                srs?.Dispose();
            }

            if (string.IsNullOrEmpty(code))
            {
                //Not fatal: a valid CRS that GDAL cannot pin to an EPSG code is unusual but not wrong, and the
                //geotransform check above already catches the case where it is actually a different place.
                Add(report, name, "has a projection that could not be identified as an EPSG code.", fatal: false);
                return;
            }

            string epsg = "EPSG:" + code;
            if (!string.Equals(epsg, grid.Epsg, StringComparison.OrdinalIgnoreCase))
            {
                Add(report, name,
                    $"is in {epsg} but {gridName} is in {grid.Epsg}. Eastings in different UTM zones " +
                    "describe ground hundreds of kilometres apart while looking equally valid.", fatal: true);
            }
        }

        /// <summary>
        /// Looks for NaN and infinity, which are the values that make ELMFIRE fail in the least legible way.
        /// </summary>
        /// <remarks>
        /// Every cell of every band is read and tested. The obvious cheaper approach — asking GDAL for the
        /// band's statistics and testing those for finiteness — <b>does not work</b>: GDAL's
        /// <c>ComputeStatistics</c> excludes non-finite cells from its own min/max/mean, so a raster with a NaN
        /// in it reports perfectly finite statistics. Written that way first, this check passed a raster it had
        /// a NaN deliberately planted in.
        ///
        /// The cost is one sequential pass per band with a reused buffer, which for a case is tens of
        /// megabytes read and discarded — cheap against a build, and much cheaper than an ELMFIRE run that
        /// stalls without saying why.
        /// </remarks>
        private static void CheckFinite(Report report, Dataset ds, string name)
        {
            if (ds.RasterCount < 1) return;

            int nx = ds.RasterXSize;
            int ny = ds.RasterYSize;
            if (nx <= 0 || ny <= 0) return;

            try
            {
                float[] row = new float[nx];

                for (int b = 1; b <= ds.RasterCount; ++b)
                {
                    Band band = ds.GetRasterBand(b);

                    //Row at a time rather than the whole band: a 72-band weather series is the largest thing a
                    //case holds, and there is no reason for this check to be the thing that needs the memory.
                    for (int y = 0; y < ny; ++y)
                    {
                        band.ReadRaster(0, y, nx, 1, row, nx, 1, 0, 0);

                        for (int x = 0; x < nx; ++x)
                        {
                            if (!float.IsNaN(row[x]) && !float.IsInfinity(row[x])) continue;

                            Add(report, name,
                                $"contains {(float.IsNaN(row[x]) ? "NaN" : "infinity")} " +
                                $"(first at column {x}, row {y} from the top" +
                                (ds.RasterCount > 1 ? $", band {b}" : "") + "). These propagate through " +
                                "ELMFIRE's level-set solver and surface as a fire that stops spreading with " +
                                "nothing said about why.", fatal: true);
                            return; //one report per raster is enough to act on
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Add(report, name, "could not be read to check for NaN (" + e.Message + ").", fatal: false);
            }
        }

        private static void Add(Report report, string raster, string message, bool fatal)
        {
            report.Problems.Add(new Problem { Raster = raster, Message = message, Fatal = fatal });
        }

        /// <summary>A single line naming what is wrong, for an error message that has to be one string.</summary>
        public static string Summarize(Report report)
        {
            var parts = new List<string>();
            foreach (Problem p in report.Fatal) parts.Add(p.ToString());

            if (parts.Count == 0) return "the case is valid";
            return string.Join(" | ", parts);
        }

        private static string D(double v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
