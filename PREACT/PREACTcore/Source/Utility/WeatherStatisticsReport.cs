using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PREACT.Wildfire;

namespace PREACT.Utility
{
    /// <summary>
    /// Writes the mean and standard deviation of every weather variable a campaign draws from, and of
    /// every quantity derived from them, to a CSV beside the campaign's other output.
    ///
    /// The point is that a fitted ensemble is only as good as the distributions behind it, and those are
    /// invisible otherwise: the per-realization log lines say what each fire got, never what the pool it
    /// came from looked like. A boundary that comes out too tight or too wide is diagnosed here first.
    /// </summary>
    /// <remarks>
    /// Both the primary variables and the derived ones are reported, because the derived ones are what
    /// actually reach ELMFIRE. Temperature and humidity never touch the fire directly — they matter only
    /// through the dead fuel moisture they are converted into — so a spread in degrees is not
    /// interpretable until it is also shown as a spread in moisture percent.
    ///
    /// Wind direction is reported with <b>circular</b> statistics and labelled as such. Its linear mean
    /// is meaningless: two days at 10 and 350 degrees are 20 degrees apart and average to 0, not 180.
    /// </remarks>
    public static class WeatherStatisticsReport
    {
        /// <summary>One variable's distribution over a sample.</summary>
        public struct VariableStatistics
        {
            public string Name;
            public string Unit;

            /// <summary>"primary" for what the archive measures, "index" for a fire-weather code,
            /// "derived" for what is computed from the primaries and read by ELMFIRE.</summary>
            public string Kind;

            public int Count;
            public double Mean, StandardDeviation, Min, P05, P50, P95, Max;
            public string Notes;
        }

        /// <summary>
        /// Computes the statistics of every variable over a candidate pool.
        /// </summary>
        /// <param name="pool">The days the distributions are fitted to.</param>
        /// <param name="rows">The full hourly record, so the fire-weather codes can be read off the
        /// pool days' own rows — <see cref="AnnualMaximaDay"/> only carries FWI.</param>
        /// <param name="live">The GSI live moisture series, or null if it could not be marched.</param>
        /// <param name="lag10Percent">10-hour offset above the equilibrium content, percentage points.</param>
        /// <param name="lag100Percent">100-hour offset above the equilibrium content.</param>
        public static List<VariableStatistics> Compute(
            IReadOnlyList<AnnualMaximaDay> pool,
            IEnumerable<HourlyWeatherRow> rows,
            IReadOnlyDictionary<DateTime, LiveFuelMoistureDay> live,
            double lag10Percent, double lag100Percent)
        {
            var stats = new List<VariableStatistics>();
            if (pool == null || pool.Count == 0) return stats;

            //The pool days' full rows, matched on the noon hour the pool was built from, so the codes
            //belong to exactly the days the fits do.
            var byDate = new Dictionary<DateTime, HourlyWeatherRow>();
            foreach (HourlyWeatherRow r in rows ?? Enumerable.Empty<HourlyWeatherRow>())
            {
                if (r.Time.Hour == 12 && !byDate.ContainsKey(r.Time.Date)) byDate[r.Time.Date] = r;
            }
            List<HourlyWeatherRow> poolRows = pool
                .Select(d => byDate.TryGetValue(d.Date.Date, out HourlyWeatherRow r) ? (HourlyWeatherRow?)r : null)
                .Where(r => r.HasValue).Select(r => r.Value).ToList();

            // ---------------------------------------------------------------- primary
            stats.Add(Describe("temperature", "deg C", "primary", pool.Select(d => d.Temperature)));
            stats.Add(Describe("relative_humidity", "%", "primary", pool.Select(d => d.RelativeHumidity)));
            stats.Add(Describe("wind_speed", "m/s", "primary", pool.Select(d => d.WindSpeed)));
            stats.Add(DescribeCircular("wind_direction", pool.Select(d => d.WindDirection)));
            stats.Add(Describe("precipitation", "mm/h", "primary", pool.Select(d => d.Precipitation)));
            stats.Add(Describe("solar_radiation", "W/m2", "primary", pool.Select(d => d.Solar)));

            // ---------------------------------------------------------------- fire-weather codes
            if (poolRows.Count > 0)
            {
                stats.Add(Describe("ffmc", "-", "index", poolRows.Select(r => r.Ffmc)));
                stats.Add(Describe("ffmc_hourly", "-", "index", poolRows.Select(r => r.FfmcHourly)));
                stats.Add(Describe("dmc", "-", "index", poolRows.Select(r => r.Dmc)));
                stats.Add(Describe("dc", "-", "index", poolRows.Select(r => r.Dc)));
                stats.Add(Describe("isi", "-", "index", poolRows.Select(r => r.Isi)));
                stats.Add(Describe("bui", "-", "index", poolRows.Select(r => r.Bui)));
            }
            stats.Add(Describe("fwi", "-", "index", pool.Select(d => d.Fwi),
                               "the pool is the top N days per year by this"));

            // ---------------------------------------------------------------- derived
            stats.Add(Describe("dew_point", "deg C", "derived",
                pool.Select(d => FireWeatherIndex.CalcDewPoint(d.Temperature, d.RelativeHumidity))));

            stats.Add(Describe("vapour_pressure_deficit", "kPa", "derived",
                pool.Select(d => VapourPressureDeficitKpa(d.Temperature, d.RelativeHumidity))));

            //What ELMFIRE actually reads. Evaluated on each pool day rather than on the fitted means,
            //because the equilibrium relation is non-linear in humidity - its value at the mean is not
            //the mean of its values.
            IEnumerable<double> emc = pool.Select(d =>
                FireWeatherIndex.EquilibriumMoisturePercent(d.Temperature, d.RelativeHumidity));
            List<double> emcList = emc.ToList();

            stats.Add(Describe("dead_fuel_moisture_1h", "%", "derived",
                emcList.Select(v => System.Math.Max(v, 1.0)),
                "Simard equilibrium content of the day's air; what ELMFIRE reads as m1"));
            stats.Add(Describe("dead_fuel_moisture_10h", "%", "derived",
                emcList.Select(v => System.Math.Max(v + lag10Percent, 1.0)),
                $"1 h + {lag10Percent:0.#} points (field convention, no drying history)"));
            stats.Add(Describe("dead_fuel_moisture_100h", "%", "derived",
                emcList.Select(v => System.Math.Max(v + lag100Percent, 1.0)),
                $"1 h + {lag100Percent:0.#} points (field convention, no drying history)"));

            stats.Add(Describe("wind_speed_20ft", "mph", "derived",
                pool.Select(d => d.WindSpeed * 2.2369362920544),
                "before WindNinja; the written ws.tif is this bent by the terrain"));

            // ---------------------------------------------------------------- live, from the GSI march
            if (live != null && live.Count > 0)
            {
                var onPool = pool.Select(d => live.TryGetValue(d.Date.Date, out LiveFuelMoistureDay l)
                                              ? (LiveFuelMoistureDay?)l : null)
                                 .Where(l => l.HasValue).Select(l => l.Value).ToList();

                if (onPool.Count > 0)
                {
                    //Flagged as resampled in the notes because it changes how these two rows should be read:
                    //for every other variable the mean and sd here are the parameters of the distribution
                    //drawn from, and for these two they are only a description of the sample the pairs are
                    //drawn out of. The realized mean will match this one, where a fitted normal's would not.
                    stats.Add(Describe("live_herbaceous", "%", "derived",
                        onPool.Select(l => l.HerbaceousPercent),
                        "NFDRS4 GSI seasonal state; ELMFIRE LH_MOISTURE_CONTENT; resampled not fitted"));
                    stats.Add(Describe("live_woody", "%", "derived",
                        onPool.Select(l => l.WoodyPercent),
                        "NFDRS4 GSI seasonal state; ELMFIRE LW_MOISTURE_CONTENT; resampled not fitted"));
                    stats.Add(Describe("growing_season_index", "0-1", "derived",
                        onPool.Select(l => l.GrowingSeasonIndex),
                        "running average behind the two live moistures"));
                }
            }

            return stats;
        }

        /// <summary>
        /// Writes the statistics as a CSV. Returns the path, or null if there was nothing to write.
        /// </summary>
        public static string Write(string path, IReadOnlyList<VariableStatistics> stats, string header = null)
        {
            if (stats == null || stats.Count == 0) return null;

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(header))
            {
                //A leading comment rather than a separate file: the numbers are not interpretable
                //without knowing which pool they came from, and the two should not be able to drift apart.
                foreach (string line in header.Split('\n')) sb.AppendLine("# " + line.TrimEnd('\r'));
            }
            sb.AppendLine("variable,unit,kind,n,mean,sd,min,p05,p50,p95,max,notes");

            foreach (VariableStatistics s in stats)
            {
                sb.AppendLine(string.Join(",",
                    s.Name, s.Unit, s.Kind,
                    s.Count.ToString(CultureInfo.InvariantCulture),
                    F(s.Mean), F(s.StandardDeviation), F(s.Min), F(s.P05), F(s.P50), F(s.P95), F(s.Max),
                    Quote(s.Notes)));
            }

            File.WriteAllText(path, sb.ToString());
            return path;
        }

        /// <summary>
        /// Descriptive statistics of one sample. Percentiles by nearest rank, which for the few hundred
        /// days a pool holds is honest — interpolating between two of 260 samples implies a resolution
        /// the sample does not have.
        /// </summary>
        public static VariableStatistics Describe(string name, string unit, string kind,
            IEnumerable<double> sample, string notes = null)
        {
            List<double> v = sample.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
            var s = new VariableStatistics { Name = name, Unit = unit, Kind = kind, Count = v.Count, Notes = notes };
            if (v.Count == 0) return s;

            v.Sort();
            s.Mean = v.Average();
            s.StandardDeviation = v.Count > 1
                ? System.Math.Sqrt(v.Sum(x => (x - s.Mean) * (x - s.Mean)) / (v.Count - 1))
                : 0.0;
            s.Min = v[0];
            s.Max = v[v.Count - 1];
            s.P05 = v[(int)(0.05 * (v.Count - 1))];
            s.P50 = v[(v.Count - 1) / 2];
            s.P95 = v[(int)(0.95 * (v.Count - 1))];
            return s;
        }

        /// <summary>
        /// Circular statistics for a direction sample: the resultant vector's bearing as the mean, and
        /// Mardia's circular standard deviation.
        /// </summary>
        /// <remarks>
        /// The percentile columns are left empty rather than filled with sorted degrees. There is no
        /// ordering on a circle, so a "5th percentile direction" is not a quantity — writing one would
        /// invite exactly the reading the circular mean exists to prevent.
        /// </remarks>
        public static VariableStatistics DescribeCircular(string name, IEnumerable<double> degrees)
        {
            List<double> v = degrees.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToList();
            var s = new VariableStatistics
            {
                Name = name,
                Unit = "deg",
                Kind = "primary",
                Count = v.Count,
                Notes = "circular mean and Mardia circular sd; percentiles omitted - a circle has no order",
            };
            if (v.Count == 0) return s;

            double sin = v.Average(d => System.Math.Sin(d * System.Math.PI / 180.0));
            double cos = v.Average(d => System.Math.Cos(d * System.Math.PI / 180.0));

            double mean = System.Math.Atan2(sin, cos) * 180.0 / System.Math.PI;
            if (mean < 0.0) mean += 360.0;
            s.Mean = mean;

            //Resultant length: 1 is a single direction, 0 is uniform around the circle.
            double r = System.Math.Sqrt(sin * sin + cos * cos);
            s.StandardDeviation = r > 0.0
                ? System.Math.Sqrt(-2.0 * System.Math.Log(r)) * 180.0 / System.Math.PI
                : 180.0;

            s.Min = v.Min();
            s.Max = v.Max();
            return s;
        }

        /// <summary>
        /// Saturation vapour pressure deficit, kPa, by Tetens — the moisture-demand term the GSI model
        /// works in, reported so the live fuel numbers can be read against something physical.
        /// </summary>
        private static double VapourPressureDeficitKpa(double temperatureC, double relativeHumidity)
        {
            double saturation = 0.6108 * System.Math.Exp(17.27 * temperatureC / (temperatureC + 237.3));
            return System.Math.Max(saturation * (1.0 - relativeHumidity / 100.0), 0.0);
        }

        private static string F(double v)
        {
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Contains(",") || s.Contains("\"")
                ? "\"" + s.Replace("\"", "\"\"") + "\""
                : s;
        }
    }
}
