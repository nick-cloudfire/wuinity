using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PREACT.Math;

namespace PREACT.Utility
{
    /// <summary>One hourly row of the weather CSV written by <c>OpenMeteoDownloader.Download</c>.</summary>
    public struct HourlyWeatherRow
    {
        public DateTime Time;
        public double Temperature;         // deg C
        public double RelativeHumidity;    // %
        public double Precipitation;       // mm
        public double WindSpeed;           // m/s
        public double WindDirection;       // deg
        public double CloudCover;
        public double DirectRadiation;     // solar, W/m^2
        public double BoundaryLayerHeight;
        public double FfmcHourly;
        public double Ffmc, Dmc, Dc, Isi, Bui, Fwi;
    }

    /// <summary>
    /// The full, physically-consistent set of conditions on one year's peak fire-weather day
    /// (docs/probabilistic-trigger-convergence.md, "Climatology sampling").
    /// </summary>
    public struct AnnualMaximaDay
    {
        public int Year;
        public DateTime Date;
        public double Temperature;
        public double RelativeHumidity;
        public double Precipitation;
        public double WindSpeed;
        public double WindDirection;
        public double Solar;
        public double Fwi;
    }

    /// <summary>Descriptive mean/std of an annual-maxima sample, for reporting alongside the empirical draws.</summary>
    public struct ClimatologyStats
    {
        public int Count;
        public double MeanTemperature, StdTemperature;
        public double MeanRelativeHumidity, StdRelativeHumidity;
        public double MeanPrecipitation, StdPrecipitation;
        public double MeanWindSpeed, StdWindSpeed;
        public double MeanSolar, StdSolar;
        public double MeanFwi, StdFwi;
        //wind direction is circular; a linear mean/std would be meaningless, so it is reported
        //only through the empirical per-day sample, never averaged here
    }

    /// <summary>
    /// Builds the annual-maxima fire-weather distribution described in
    /// docs/probabilistic-trigger-convergence.md ("Climatology sampling — annual fire-weather
    /// maxima") on top of the CSV <c>OpenMeteoDownloader.Download</c> already writes, and draws
    /// realizations from it by empirical resampling (each Monte Carlo realization gets the full,
    /// physically-consistent conditions of one actual historical peak-fire-weather day, rather
    /// than independently-sampled variables that could combine into an implausible day).
    ///
    /// Caveat: <see cref="PREACT.Wildfire.FireWeatherIndex.CalculateDay"/> hard-zeroes FWI for
    /// October-January (`dateTime.Month &lt; 2 || dateTime.Month &gt; 9`), which encodes a
    /// Northern-Hemisphere fire season. That is fine for domains like Mediterranean Greece, but
    /// would suppress genuine peak-fire-weather days in the Southern Hemisphere or in tropical/
    /// dry-season climates — a real gap against the design doc's "works for any location on
    /// Earth" goal that a global deployment will need to revisit in the FWI engine itself, not
    /// worked around here.
    /// </summary>
    public static class ClimatologySampler
    {
        public static List<HourlyWeatherRow> ParseOpenMeteoCsv(string path)
        {
            var rows = new List<HourlyWeatherRow>();
            string[] lines = File.ReadAllLines(path);

            //lines 0-2 are "Latitide,..","Longitude,..","Elevation,.."; line 3 is the column header
            for (int i = 4; i < lines.Length; ++i)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                string[] c = line.Split(',');
                if (c.Length < 16) continue;

                if (!DateTime.TryParse(c[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)) continue;

                rows.Add(new HourlyWeatherRow
                {
                    Time = time,
                    Temperature = ParseD(c[1]),
                    RelativeHumidity = ParseD(c[2]),
                    Precipitation = ParseD(c[3]),
                    WindSpeed = ParseD(c[4]),
                    WindDirection = ParseD(c[5]),
                    CloudCover = ParseD(c[6]),
                    DirectRadiation = ParseD(c[7]),
                    BoundaryLayerHeight = ParseD(c[8]),
                    FfmcHourly = ParseD(c[9]),
                    Ffmc = ParseD(c[10]),
                    Dmc = ParseD(c[11]),
                    Dc = ParseD(c[12]),
                    Isi = ParseD(c[13]),
                    Bui = ParseD(c[14]),
                    Fwi = ParseD(c[15]),
                });
            }
            return rows;
        }

        private static double ParseD(string s)
        {
            return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0.0;
        }

        /// <summary>
        /// For each calendar year present, picks the day with the highest FWI (using the noon
        /// row, since <see cref="PREACT.Wildfire.FireWeatherIndex"/> only advances at hour 12 —
        /// other hours in the source CSV repeat that day's value). Years where every day's FWI
        /// is zero (no valid peak, e.g. entirely outside the FWI engine's coded fire season) are
        /// skipped rather than contributing a meaningless zero-FWI "peak".
        /// </summary>
        public static List<AnnualMaximaDay> BuildAnnualMaxima(IEnumerable<HourlyWeatherRow> rows)
        {
            var byYear = rows.Where(r => r.Time.Hour == 12).GroupBy(r => r.Time.Year);

            var maxima = new List<AnnualMaximaDay>();
            foreach (var year in byYear)
            {
                HourlyWeatherRow? peak = null;
                foreach (HourlyWeatherRow r in year)
                {
                    if (r.Fwi <= 0.0) continue;
                    if (peak == null || r.Fwi > peak.Value.Fwi) peak = r;
                }
                if (peak == null) continue;

                HourlyWeatherRow p = peak.Value;
                maxima.Add(new AnnualMaximaDay
                {
                    Year = year.Key,
                    Date = p.Time,
                    Temperature = p.Temperature,
                    RelativeHumidity = p.RelativeHumidity,
                    Precipitation = p.Precipitation,
                    WindSpeed = p.WindSpeed,
                    WindDirection = p.WindDirection,
                    Solar = p.DirectRadiation,
                    Fwi = p.Fwi,
                });
            }
            return maxima;
        }

        public static ClimatologyStats ComputeStats(IReadOnlyList<AnnualMaximaDay> maxima)
        {
            var stats = new ClimatologyStats { Count = maxima.Count };
            if (maxima.Count == 0) return stats;

            (stats.MeanTemperature, stats.StdTemperature) = MeanStd(maxima, d => d.Temperature);
            (stats.MeanRelativeHumidity, stats.StdRelativeHumidity) = MeanStd(maxima, d => d.RelativeHumidity);
            (stats.MeanPrecipitation, stats.StdPrecipitation) = MeanStd(maxima, d => d.Precipitation);
            (stats.MeanWindSpeed, stats.StdWindSpeed) = MeanStd(maxima, d => d.WindSpeed);
            (stats.MeanSolar, stats.StdSolar) = MeanStd(maxima, d => d.Solar);
            (stats.MeanFwi, stats.StdFwi) = MeanStd(maxima, d => d.Fwi);
            return stats;
        }

        private static (double mean, double std) MeanStd(IReadOnlyList<AnnualMaximaDay> maxima, Func<AnnualMaximaDay, double> select)
        {
            double mean = maxima.Average(select);
            double variance = maxima.Count > 1
                ? maxima.Sum(d => (select(d) - mean) * (select(d) - mean)) / (maxima.Count - 1)
                : 0.0;
            return (mean, System.Math.Sqrt(variance));
        }

        /// <summary>
        /// Draws one realization's weather by empirical resampling: uniformly picks one of the
        /// actual historical annual-maxima days and returns its full condition set intact, so
        /// wind/temperature/RH/precip/solar stay physically consistent with each other.
        /// </summary>
        public static AnnualMaximaDay Sample(IReadOnlyList<AnnualMaximaDay> maxima, MonteCarloRng rng)
        {
            if (maxima == null || maxima.Count == 0)
                throw new InvalidOperationException("No annual-maxima days to sample from.");
            return maxima[rng.Range(0, maxima.Count)];
        }
    }
}
