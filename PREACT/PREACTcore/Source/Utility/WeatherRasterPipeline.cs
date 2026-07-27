using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PREACT.Math;
using PREACT.Tools;
using PREACT.Weather;
using PREACT.Wildfire;

namespace PREACT.Utility
{
    /// <summary>
    /// The history-based weather chain from docs/probabilistic-trigger-convergence.md's "Reference
    /// pipeline (WindNinja + Nelson), per realization": ERA5 climatology → a sampled historical
    /// peak fire-weather day → WindNinja terrain wind + Nelson dead fuel moisture → the five
    /// weather rasters (<c>ws</c>/<c>wd</c>/<c>m1</c>/<c>m10</c>/<c>m100</c>) ELMFIRE reads.
    ///
    /// This replaces the constant-value placeholder rasters the case builder used to write. The
    /// difference is not cosmetic: a uniform wind field ignores every ridge and valley in the
    /// domain, and a guessed dead fuel moisture ignores the antecedent weather that actually
    /// determines it, both of which the trigger boundary is directly sensitive to.
    ///
    /// Each stage degrades rather than fails. No archive or no usable fire-weather day, no
    /// WindNinja install, no terrain rasters — any of these falls back to the caller's uniform
    /// values for the affected stage only, and says so in <see cref="Result"/>, because a case
    /// that builds with honest placeholders beats one that cannot build at all.
    /// </summary>
    public static class WeatherRasterPipeline
    {
        /// <summary>Nelson returns moisture as a fraction (g/g); ELMFIRE's M*_FILENAME rasters are percent.</summary>
        private const double FractionToPercent = 100.0;

        public class Options
        {
            public MasterGrid Grid;

            /// <summary>Receives ws/wd/m1/m10/m100.</summary>
            public string InputsDirectory;

            /// <summary>
            /// Where the terrain rasters (dem/slp/asp/cc) are read from; defaults to
            /// <see cref="InputsDirectory"/>. They are separate so a per-realization run can write
            /// its own private weather while still reading the one shared, read-only copy of the
            /// terrain — which is also exactly the split ELMFIRE's
            /// FUELS_AND_TOPOGRAPHY_DIRECTORY / WEATHER_DIRECTORY pair expresses.
            /// </summary>
            public string TerrainDirectory;

            public string Terrain => string.IsNullOrEmpty(TerrainDirectory) ? InputsDirectory : TerrainDirectory;

            /// <summary>Domain centre, (lat, lon) — the point the ERA5 archive is queried for.</summary>
            public Vector2d LatLon;

            /// <summary>Cached hourly ERA5 CSV, in the format <see cref="OpenMeteoDownloader"/> writes.
            /// Downloaded if absent, then reused: it is the same for every realization of a case, and
            /// re-fetching decades of hourly weather per realization is what makes campaigns fragile.</summary>
            public string ArchiveCsvPath;

            /// <summary>First year of the climatology record. ERA5 via Open-Meteo starts in 1940;
            /// the default keeps the request to a size that reliably completes.</summary>
            public int ArchiveStartYear = 2000;

            /// <summary>Last year of the record; defaults to the last complete calendar year.</summary>
            public int ArchiveEndYear = 0;

            /// <summary>Antecedent hourly window Nelson is marched over before the fire day, so the
            /// sticks reflect real drying/wetting history. 20 days matches WildfireAV's
            /// CONDITIONING_DAYS.</summary>
            public int ConditioningDays = 20;

            /// <summary>
            /// The burning period of the sampled day, over which the <b>minimum</b> dead fuel
            /// moisture is taken. Nelson is time-marching, and the design intent is to capture the
            /// diurnal minimum of the fine dead fuels during peak burning — which a daily-mean
            /// product would miss and run systematically wetter, i.e. less conservative for a
            /// trigger boundary.
            ///
            /// Taking the minimum over a window rather than the value at one instant also matters
            /// for a reason that only shows up on real data: ERA5 routinely reports a trace of
            /// drizzle (a few tenths of a mm, an area-average over ~9 km rather than rain that
            /// necessarily fell on the fuel), and Nelson correctly responds by soaking the 1-hour
            /// stick to near saturation. Sampling a single hour that happens to sit just after such
            /// a trace makes the driest day of the decade come out sodden.
            /// </summary>
            public int BurningPeriodStartHour = 10;
            public int BurningPeriodEndHour = 18;

            public string WindNinjaExe;
            public string WindNinjaVegetation = "grass";
            public string WindNinjaMesh = "coarse";

            /// <summary>Seeds the choice of historical day, so a realization is reproducible.</summary>
            public int Seed;

            /// <summary>Use this specific historical day instead of drawing one — for a deterministic
            /// baseline, or to replay a known event.</summary>
            public DateTime? ForceDate;

            /// <summary>Set false to skip the historical archive entirely and write uniform
            /// rasters from the fallback values — for an offline build, or to hold weather fixed
            /// while something else is being varied.</summary>
            public bool UseClimatology = true;

            /// <summary>Used when a stage cannot run: uniform wind (m/s) and dead moisture (%).</summary>
            public double FallbackWindSpeedMps = 5.0;
            public double FallbackWindDirectionDeg = 0.0;
            public double FallbackM1Percent = 6.0, FallbackM10Percent = 7.0, FallbackM100Percent = 8.0;

            public Action<string> Log;
        }

        public class Result
        {
            /// <summary>The historical day the realization's weather came from, if one was drawn.</summary>
            public AnnualMaximaDay? Day;
            public int AnnualMaximaCount;

            public bool ClimatologyUsed;
            public bool WindNinjaUsed;
            public bool NelsonUsed;

            public double MeanWindSpeedMph;
            public double MeanM1Percent, MeanM10Percent, MeanM100Percent;

            /// <summary>Why a stage fell back, when one did — surfaced so a silently-degraded case is visible.</summary>
            public List<string> Fallbacks = new List<string>();
        }

        public static async Task<Result> Run(Options o)
        {
            void Log(string m) => o.Log?.Invoke(m);
            var result = new Result();

            //---------------------------------------------------------------- climatology
            List<HourlyWeatherRow> rows = null;
            if (!o.UseClimatology)
            {
                result.Fallbacks.Add("climatology: disabled");
                Log("  climatology: disabled, using uniform weather.");
            }
            else
            {
                try
                {
                    rows = await LoadArchive(o, Log);
                }
                catch (Exception e)
                {
                    result.Fallbacks.Add("climatology: " + e.Message);
                }
            }

            AnnualMaximaDay? day = null;
            if (rows != null && rows.Count > 0)
            {
                List<AnnualMaximaDay> maxima = ClimatologySampler.BuildAnnualMaxima(rows);
                result.AnnualMaximaCount = maxima.Count;

                if (maxima.Count == 0)
                {
                    result.Fallbacks.Add("climatology: the archive contains no day with a non-zero FWI");
                }
                else if (o.ForceDate.HasValue)
                {
                    DateTime want = o.ForceDate.Value.Date;
                    day = maxima.Any(m => m.Date.Date == want)
                        ? maxima.First(m => m.Date.Date == want)
                        : maxima.OrderBy(m => System.Math.Abs((m.Date.Date - want).TotalDays)).First();
                }
                else
                {
                    day = ClimatologySampler.Sample(maxima, new MonteCarloRng(o.Seed));
                }
            }

            if (day.HasValue)
            {
                result.ClimatologyUsed = true;
                result.Day = day;
                AnnualMaximaDay d = day.Value;
                Log($"  climatology: {result.AnnualMaximaCount} annual peak-fire-weather days on record; " +
                    $"drew {d.Date:yyyy-MM-dd} (FWI {d.Fwi:F1}, {d.WindSpeed:F1} m/s @ {d.WindDirection:F0} deg, " +
                    $"{d.Temperature:F1} C, RH {d.RelativeHumidity:F0}%).");
            }
            else
            {
                Log($"  climatology: unavailable, using uniform {o.FallbackWindSpeedMps:F1} m/s @ {o.FallbackWindDirectionDeg:F0} deg.");
            }

            double windMps = day?.WindSpeed ?? o.FallbackWindSpeedMps;
            double windDir = day?.WindDirection ?? o.FallbackWindDirectionDeg;

            //---------------------------------------------------------------- WindNinja
            RunWind(o, result, windMps, windDir, Log);

            //---------------------------------------------------------------- Nelson
            RunNelson(o, result, rows, day, Log);

            return result;
        }

        /// <summary>
        /// Downloads the hourly ERA5 record once and reuses it. A cached file is accepted only if
        /// it actually spans the requested years — a truncated or half-written archive would
        /// otherwise silently narrow the climatology to whatever happened to be in it.
        /// </summary>
        private static async Task<List<HourlyWeatherRow>> LoadArchive(Options o, Action<string> log)
        {
            int endYear = o.ArchiveEndYear > 0 ? o.ArchiveEndYear : DateTime.UtcNow.Year - 1;
            if (endYear < o.ArchiveStartYear) endYear = o.ArchiveStartYear;

            var start = new DateTime(o.ArchiveStartYear, 1, 1);
            var end = new DateTime(endYear, 12, 31);

            bool usable = false;
            if (File.Exists(o.ArchiveCsvPath))
            {
                try
                {
                    List<HourlyWeatherRow> cached = ClimatologySampler.ParseOpenMeteoCsv(o.ArchiveCsvPath);
                    if (cached.Count > 0 && cached[0].Time <= start.AddDays(1) && cached[cached.Count - 1].Time >= end.AddDays(-1))
                    {
                        log($"  climatology: reusing cached archive ({cached.Count} hourly rows, " +
                            $"{cached[0].Time:yyyy-MM-dd} to {cached[cached.Count - 1].Time:yyyy-MM-dd}).");
                        return cached;
                    }
                    usable = cached.Count > 0;
                }
                catch { }
            }

            log($"  climatology: downloading ERA5 hourly {o.ArchiveStartYear}-{endYear} for {o.LatLon.x:F4},{o.LatLon.y:F4}" +
                (usable ? " (cached archive does not cover the requested range)" : "") + "...");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(o.ArchiveCsvPath)));
            await OpenMeteoDownloader.Download(o.LatLon, start, end, o.ArchiveCsvPath);

            return ClimatologySampler.ParseOpenMeteoCsv(o.ArchiveCsvPath);
        }

        private static void RunWind(Options o, Result result, double windMps, double windDir, Action<string> log)
        {
            string dem = Path.Combine(o.Terrain, "dem.tif");

            if (!string.IsNullOrEmpty(o.WindNinjaExe) && File.Exists(o.WindNinjaExe) && File.Exists(dem))
            {
                WindNinjaRunner.Result wn = WindNinjaRunner.Run(
                    o.WindNinjaExe, dem, o.Grid, o.InputsDirectory,
                    windMps, windDir, o.WindNinjaVegetation, o.WindNinjaMesh, log: log);

                if (wn.Ok)
                {
                    result.WindNinjaUsed = true;
                    result.MeanWindSpeedMph = wn.MeanSpeedMph;
                    return;
                }

                result.Fallbacks.Add("WindNinja: " + wn.Message);
                log($"  wind: WindNinja unavailable ({wn.Message}); writing a uniform field instead.");
            }
            else
            {
                result.Fallbacks.Add("WindNinja: not configured");
                log("  wind: no WindNinja executable configured; writing a uniform field.");
            }

            const double mpsToMph = 2.2369362920544;
            double mph = windMps * mpsToMph;
            GeoTiffRasterWriter.WriteConstant(o.Grid, (float)mph, Path.Combine(o.InputsDirectory, "ws.tif"));
            GeoTiffRasterWriter.WriteConstant(o.Grid, (float)windDir, Path.Combine(o.InputsDirectory, "wd.tif"));
            result.MeanWindSpeedMph = mph;
        }

        /// <summary>
        /// Marches the in-process Nelson engine over the conditioning window and samples it per
        /// cell. WildfireAV shells out to a separate exe with BSQ intermediates for this; WUInity
        /// already has the same engine in-process, so none of that plumbing is needed.
        ///
        /// The engine bins cells by (elevation, slope, aspect, canopy cover) and integrates one
        /// stick per bin, which is what makes a per-cell result affordable: the domain has far
        /// fewer distinct terrain classes than cells.
        /// </summary>
        private static void RunNelson(Options o, Result result, List<HourlyWeatherRow> rows, AnnualMaximaDay? day, Action<string> log)
        {
            string reason = null;

            if (rows == null || !day.HasValue) reason = "no sampled day to condition from";
            else if (!File.Exists(Path.Combine(o.Terrain, "slp.tif"))) reason = "no slope raster";

            if (reason == null)
            {
                try
                {
                    NelsonMoisture(o, result, rows, day.Value, log);
                    result.NelsonUsed = true;
                    return;
                }
                catch (Exception e)
                {
                    reason = e.Message;
                }
            }

            result.Fallbacks.Add("Nelson: " + reason);
            log($"  moisture: Nelson unavailable ({reason}); writing uniform " +
                $"{o.FallbackM1Percent:F0}/{o.FallbackM10Percent:F0}/{o.FallbackM100Percent:F0} %.");

            GeoTiffRasterWriter.WriteConstant(o.Grid, (float)o.FallbackM1Percent, Path.Combine(o.InputsDirectory, "m1.tif"));
            GeoTiffRasterWriter.WriteConstant(o.Grid, (float)o.FallbackM10Percent, Path.Combine(o.InputsDirectory, "m10.tif"));
            GeoTiffRasterWriter.WriteConstant(o.Grid, (float)o.FallbackM100Percent, Path.Combine(o.InputsDirectory, "m100.tif"));
            result.MeanM1Percent = o.FallbackM1Percent;
            result.MeanM10Percent = o.FallbackM10Percent;
            result.MeanM100Percent = o.FallbackM100Percent;
        }

        private static void NelsonMoisture(Options o, Result result, List<HourlyWeatherRow> rows, AnnualMaximaDay day, Action<string> log)
        {
            int[,] elevation = ReadTerrain(o, "dem.tif", 0, 9000, required: true);
            int[,] slope = ReadTerrain(o, "slp.tif", 0, 90, required: true);
            int[,] aspect = ReadTerrain(o, "asp.tif", 0, 360, required: true);
            //Canopy cover only shades the sticks; a domain without it is treated as fully open
            //rather than refusing to run.
            int[,] canopy = ReadTerrain(o, "cc.tif", 0, 100, required: false)
                            ?? new int[o.Grid.Header.Ncols, o.Grid.Header.Nrows];

            DateTime end = day.Date.Date.AddHours(o.BurningPeriodEndHour);
            DateTime start = day.Date.Date.AddHours(o.BurningPeriodStartHour).AddDays(-o.ConditioningDays);

            List<HourlyWeatherRow> window = rows.Where(r => r.Time >= start && r.Time <= end)
                                                .OrderBy(r => r.Time)
                                                .ToList();

            if (window.Count < 24)
            {
                throw new Exception($"only {window.Count} hourly rows in the {o.ConditioningDays}-day conditioning window");
            }

            //One stick per distinct terrain class rather than per cell: the moisture only depends
            //on the cell through the solar radiation its terrain receives, and a domain has orders
            //of magnitude fewer terrain classes than cells.
            var classes = TerrainClasses.Build(elevation, slope, aspect, canopy);

            log($"  moisture: marching Nelson over {window.Count} hours " +
                $"({start:yyyy-MM-dd HH:mm} to {end:yyyy-MM-dd HH:mm}), {classes.Count} terrain classes...");

            var sticks = new DeadFuelMoistureBin[classes.Count];
            var min1 = new double[classes.Count];
            var min10 = new double[classes.Count];
            var min100 = new double[classes.Count];

            HourlyWeatherRow first = window[0];
            double h0 = Fraction(first.RelativeHumidity);

            for (int c = 0; c < classes.Count; ++c)
            {
                sticks[c] = new DeadFuelMoistureBin(useSimpleOneHour: false, include1000hour: false);
                sticks[c].InitializeEnvironment(
                    first.Time.Year, first.Time.Month, first.Time.Day, first.Time.Hour, 0, 0,
                    ta: first.Temperature, ha: h0, sr: first.DirectRadiation, rc: 0.0,
                    //the stick starts in equilibrium with the air rather than at an arbitrary
                    //value; the conditioning window exists precisely so this guess stops mattering.
                    ti: first.Temperature, hi: h0, wi: 0.2);

                min1[c] = min10[c] = min100[c] = double.MaxValue;
            }

            DateTime burnStart = day.Date.Date.AddHours(o.BurningPeriodStartHour);
            double cumulativeRainCm = 0.0;
            int sampled = 0;

            foreach (HourlyWeatherRow r in window)
            {
                cumulativeRainCm += r.Precipitation * 0.1; //mm -> cm, and the engine wants it cumulative
                double rh = Fraction(r.RelativeHumidity);
                bool inBurningPeriod = r.Time >= burnStart;
                if (inBurningPeriod) ++sampled;

                //Classes are independent sticks, so the hour's integration parallelises cleanly.
                System.Threading.Tasks.Parallel.For(0, classes.Count, c =>
                {
                    TerrainClass t = classes.Items[c];
                    sticks[c].UpdateDateTime(
                        r.Time.Year, r.Time.Month, r.Time.Day, r.Time.Hour, 0, 0,
                        at: r.Temperature, rh: rh,
                        sW: TerrainSolar(o.LatLon, r, t),
                        rcum: cumulativeRainCm);

                    if (!inBurningPeriod) return;

                    sticks[c].GetMoisture(out double v1, out double v10, out double v100, out double _);
                    if (v1 >= 0 && v1 < min1[c]) min1[c] = v1;
                    if (v10 >= 0 && v10 < min10[c]) min10[c] = v10;
                    if (v100 >= 0 && v100 < min100[c]) min100[c] = v100;
                });
            }

            if (sampled == 0)
            {
                throw new Exception("the conditioning window ended before the burning period began");
            }

            int nx = o.Grid.Header.Ncols;
            int ny = o.Grid.Header.Nrows;
            var m1 = new float[nx, ny];
            var m10 = new float[nx, ny];
            var m100 = new float[nx, ny];

            double s1 = 0, s10 = 0, s100 = 0;

            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y)
                {
                    int c = classes.IndexOf[x, y];
                    m1[x, y] = (float)(min1[c] * FractionToPercent);
                    m10[x, y] = (float)(min10[c] * FractionToPercent);
                    m100[x, y] = (float)(min100[c] * FractionToPercent);
                    s1 += m1[x, y]; s10 += m10[x, y]; s100 += m100[x, y];
                }
            }

            GeoTiffRasterWriter.WriteBand(o.Grid, m1, Path.Combine(o.InputsDirectory, "m1.tif"));
            GeoTiffRasterWriter.WriteBand(o.Grid, m10, Path.Combine(o.InputsDirectory, "m10.tif"));
            GeoTiffRasterWriter.WriteBand(o.Grid, m100, Path.Combine(o.InputsDirectory, "m100.tif"));

            int cells = nx * ny;
            result.MeanM1Percent = s1 / cells;
            result.MeanM10Percent = s10 / cells;
            result.MeanM100Percent = s100 / cells;

            log($"    Nelson: mean dead moisture {result.MeanM1Percent:F1}/{result.MeanM10Percent:F1}/" +
                $"{result.MeanM100Percent:F1} % (1/10/100 h), minimum over {sampled} burning-period hours.");
        }

        /// <summary>
        /// Solar radiation reaching a terrain class's fuel: the archive's own (flat-site) direct
        /// radiation, scaled by how much more or less this slope/aspect/canopy intercepts than a
        /// flat open site at the same place and time.
        ///
        /// Using the ratio rather than <see cref="SunRadiation.SimpleRadiation"/> outright keeps
        /// the real cloud and haze of the historical day — which is why the day was drawn from a
        /// record in the first place — while still letting a north-facing slope stay damper than
        /// a south-facing one, the terrain dependence that makes this per-cell at all.
        /// </summary>
        private static double TerrainSolar(Vector2d latLon, HourlyWeatherRow r, TerrainClass t)
        {
            if (r.DirectRadiation <= 0.0) return 0.0; //night: no terrain factor to apply

            long dayOfYear = r.Time.DayOfYear;
            long cloud = (long)System.Math.Round(r.CloudCover);

            //SimpleRadiation wants the hour as HHMM, not 0-23 - internally it does `hour / 100` in
            //integer arithmetic, so an hour-of-day argument silently collapses to midnight and the
            //function returns 0 for every terrain, every time.
            double hhmm = r.Time.Hour * 100;
            //...and elevation in feet, which it divides by 3.2808 to get metres.
            long elevationFeet = (long)System.Math.Round(t.Elevation * 3.2808);

            double flat = SunRadiation.SimpleRadiation(latLon.x, latLon.y, dayOfYear, hhmm, cloud, elevationFeet, 0, 0, 0);
            if (flat <= 1.0) return r.DirectRadiation; //sun too low for a meaningful ratio

            double onSlope = SunRadiation.SimpleRadiation(latLon.x, latLon.y, dayOfYear, hhmm, cloud, elevationFeet, t.Slope, t.Aspect, t.CanopyCover);

            double factor = onSlope / flat;
            if (factor < 0.0) factor = 0.0;
            if (factor > 1.5) factor = 1.5;
            return r.DirectRadiation * factor;
        }

        /// <summary>One distinct (elevation, slope, aspect, canopy) class, integrated as a single stick.</summary>
        private struct TerrainClass
        {
            public int Elevation, Slope, Aspect, CanopyCover;
        }

        /// <summary>
        /// Groups cells into terrain classes on the same bin widths the moisture engine uses
        /// (200 m elevation, 10 deg slope, 45 deg aspect, 15 % canopy), and remembers which class
        /// each cell landed in. Unlike <see cref="DeadFuelMoistureEngine"/> this keeps the mapping
        /// both ways, which is what allows a per-class solar forcing — the engine's own API drives
        /// every bin with one shared radiation value, so its output is necessarily uniform.
        /// </summary>
        private class TerrainClasses
        {
            public List<TerrainClass> Items = new List<TerrainClass>();
            public int[,] IndexOf;
            public int Count => Items.Count;

            public static TerrainClasses Build(int[,] elevation, int[,] slope, int[,] aspect, int[,] canopy)
            {
                int nx = elevation.GetLength(0);
                int ny = elevation.GetLength(1);

                var result = new TerrainClasses { IndexOf = new int[nx, ny] };
                var seen = new Dictionary<long, int>();

                for (int x = 0; x < nx; ++x)
                {
                    for (int y = 0; y < ny; ++y)
                    {
                        int e = elevation[x, y] / 200;
                        int s = slope[x, y] / 10;
                        int a = aspect[x, y] / 45;
                        int c = canopy[x, y] / 15;

                        long key = (((long)e * 16 + s) * 16 + a) * 16 + c;
                        if (!seen.TryGetValue(key, out int index))
                        {
                            index = result.Items.Count;
                            seen[key] = index;
                            //bin centres, so one stick represents its class rather than its corner
                            result.Items.Add(new TerrainClass
                            {
                                Elevation = e * 200 + 100,
                                Slope = s * 10 + 5,
                                Aspect = a * 45 + 22,
                                CanopyCover = c * 15 + 7,
                            });
                        }
                        result.IndexOf[x, y] = index;
                    }
                }
                return result;
            }
        }

        /// <summary>Nelson takes relative humidity as a fraction (g/g), the CSV stores percent.</summary>
        private static double Fraction(double relativeHumidityPercent)
        {
            double f = relativeHumidityPercent / 100.0;
            return System.Math.Min(1.0, System.Math.Max(0.0, f));
        }

        /// <summary>
        /// Reads a terrain raster as the integer grid the moisture engine bins on, clamping to a
        /// physical range. The clamp is load-bearing: the bin hash casts to <c>uint</c>, so a
        /// NoData -9999 would wrap to an enormous bin index and silently strand that cell.
        /// </summary>
        private static int[,] ReadTerrain(Options o, string fileName, int min, int max, bool required)
        {
            string path = Path.Combine(o.Terrain, fileName);
            if (!File.Exists(path))
            {
                if (required) throw new Exception("missing terrain raster " + fileName);
                return null;
            }

            float[,] data = AscRaster.ReadGeoTiff(path, out AscRaster.Header _, out bool ok);
            if (!ok || data == null)
            {
                if (required) throw new Exception("could not read " + fileName);
                return null;
            }

            int nx = o.Grid.Header.Ncols;
            int ny = o.Grid.Header.Nrows;
            if (data.GetLength(0) != nx || data.GetLength(1) != ny)
            {
                throw new Exception($"{fileName} is {data.GetLength(0)}x{data.GetLength(1)}, expected {nx}x{ny}");
            }

            var result = new int[nx, ny];
            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y)
                {
                    float v = data[x, y];
                    int i = float.IsNaN(v) ? min : (int)System.Math.Round(v);
                    result[x, y] = System.Math.Min(max, System.Math.Max(min, i));
                }
            }
            return result;
        }
    }
}
