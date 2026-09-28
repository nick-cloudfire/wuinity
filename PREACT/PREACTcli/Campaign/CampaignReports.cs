using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>The campaign's weather reports: what it drew from, and what it drew.</summary>
    internal static class CampaignReports
    {
        public static void WriteWeatherDistributions(Campaign c)
        {
            if (c.WeatherStatistics == null) return;

            try
            {
                string path = WeatherStatisticsReport.Write(Path.Combine(c.Folder, CampaignLayout.WeatherDistributionsCsv),
                    c.WeatherStatistics, c.WeatherStatisticsHeader);
                if (path != null) Console.WriteLine("Weather distributions: " + path);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("WARNING: could not write the weather distribution report: " + e.Message);
            }
        }

        /// <summary>
        /// One row per realization whose fire was used, then the realized mean and standard deviation of every
        /// drawn variable - the companion to the distributions report, and a check that the campaign sampled what
        /// it claims. Only fires the ensemble actually used are listed: a draw whose fire was never aggregated is
        /// not part of the ensemble.
        /// </summary>
        public static void WriteRealizedWeather(Campaign c, List<(string Id, RealizationRecord Record)> used)
        {
            var drawn = used.Where(u => u.Record.HasDraw).OrderBy(u => u.Id, StringComparer.Ordinal).ToList();
            if (drawn.Count == 0) return;

            try
            {
                string path = Path.Combine(c.Folder, CampaignLayout.WeatherRealizationsCsv);
                var sb = new StringBuilder();
                sb.AppendLine("# What each realization whose fire the ensemble used actually drew. Compare against");
                sb.AppendLine("# weather_distributions.csv. The last two rows are the realized mean and standard deviation.");
                sb.AppendLine("realization,temperature_c,relative_humidity_pct,wind_speed_10m_ms,wind_direction_deg,wind_direction_source,"
                              + "dead_1h_pct,dead_10h_pct,dead_100h_pct,live_herbaceous_pct,live_woody_pct");

                foreach ((string id, RealizationRecord d) in drawn)
                {
                    sb.AppendLine(string.Join(",", id,
                        N(d.Temperature), N(d.RelativeHumidity), N(d.WindSpeedMps), N(d.WindDirectionDeg),
                        d.DirectionResampled ? "resampled" : "aimed",
                        N(d.M1Percent), N(d.M10Percent), N(d.M100Percent),
                        d.LiveDrawn ? N(d.LiveHerbaceousPercent) : "", d.LiveDrawn ? N(d.LiveWoodyPercent) : ""));
                }

                List<RealizationRecord> records = drawn.Select(u => u.Record).ToList();
                var live = records.Where(d => d.LiveDrawn).ToList();
                Summary(sb, "MEAN", records, live, v => v.Average());
                Summary(sb, "SD", records, live, Sd);

                File.WriteAllText(path, sb.ToString());
                Console.WriteLine($"Realized ensemble weather ({drawn.Count} realizations): " + path);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("WARNING: could not write the realized weather summary: " + e.Message);
            }
        }

        private static void Summary(StringBuilder sb, string label, List<RealizationRecord> all, List<RealizationRecord> live,
            Func<IEnumerable<double>, double> f)
        {
            //Direction is left out of both rows: aimed directions are a property of where each fire started, and a
            //linear mean of bearings is wrong even when they are not.
            sb.AppendLine(label + "," + string.Join(",",
                N(f(all.Select(d => d.Temperature))), N(f(all.Select(d => d.RelativeHumidity))),
                N(f(all.Select(d => d.WindSpeedMps))), "", "",
                N(f(all.Select(d => d.M1Percent))), N(f(all.Select(d => d.M10Percent))), N(f(all.Select(d => d.M100Percent))),
                live.Count > 0 ? N(f(live.Select(d => d.LiveHerbaceousPercent))) : "",
                live.Count > 0 ? N(f(live.Select(d => d.LiveWoodyPercent))) : ""));
        }

        private static double Sd(IEnumerable<double> values)
        {
            List<double> v = values.ToList();
            if (v.Count < 2) return 0.0;
            double m = v.Average();
            return System.Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
        }

        private static string N(double v) => double.IsNaN(v) ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
