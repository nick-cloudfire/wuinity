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

        /// <summary>
        /// The <paramref name="daysPerYear"/> highest-FWI days of each calendar year, as the sample
        /// a distribution is fitted to. <see cref="BuildAnnualMaxima"/> is the same thing with
        /// <paramref name="daysPerYear"/> of 1.
        /// </summary>
        /// <remarks>
        /// More days per year exist to make the fit stand on something: 26 years of record give 26
        /// annual maxima, which is a thin sample to take a standard deviation from, and ten a year
        /// gives 260.
        ///
        /// What that costs is severity, not spread, and it is worth knowing which. Measured over the
        /// Mati archive (2000–2025, 227,928 hourly rows), going from 1 to 10 days a year moves the
        /// fitted mean temperature 36.1 → 34.2 °C and mean humidity 16.5 → 20.7 %, while the
        /// standard deviations barely move and if anything widen (wind 1.55 → 1.98 m/s). The tenth
        /// worst day of a year is a materially milder day than the worst, so a deeper pool describes
        /// "a bad fire-weather day here" where a shallow one describes "the worst day of the year" —
        /// a bias in the centre of the distribution, in the direction of less severe fires.
        ///
        /// The days are also not independent: a year's worst frequently fall consecutively inside one
        /// heatwave, the same synoptic pattern counted several times. Requiring a minimum separation
        /// between them would give quasi-independent events instead. It is not done here because the
        /// top-N-by-FWI pool is what was asked for, and because the measurement above says the
        /// correlation is not what dominates — the dilution of severity is.
        /// </remarks>
        public static List<AnnualMaximaDay> BuildCandidatePool(IEnumerable<HourlyWeatherRow> rows, int daysPerYear)
        {
            if (daysPerYear < 1) daysPerYear = 1;

            //Hour 12 for the same reason BuildAnnualMaxima uses it: FireWeatherIndex only advances the
            //daily codes at noon, and the other hours of the source CSV repeat that day's value - so
            //taking every hour would return the same day 24 times.
            var byYear = rows.Where(r => r.Time.Hour == 12).GroupBy(r => r.Time.Year);

            var pool = new List<AnnualMaximaDay>();
            foreach (var year in byYear)
            {
                IEnumerable<HourlyWeatherRow> best = year.Where(r => r.Fwi > 0.0)
                                                         .OrderByDescending(r => r.Fwi)
                                                         .Take(daysPerYear);

                foreach (HourlyWeatherRow p in best)
                {
                    pool.Add(new AnnualMaximaDay
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
            }
            return pool;
        }

        /// <summary>A fitted normal, and the sample it came from.</summary>
        public struct NormalFit
        {
            public double Mean;
            public double StandardDeviation;

            /// <summary>Lowest and highest value in the sample — reported so a draw can be read against
            /// the record it claims to describe, not used to truncate.</summary>
            public double SampleMin, SampleMax;

            public override string ToString()
            {
                return $"N({Mean:F1}, {StandardDeviation:F1})";
            }
        }

        /// <summary>
        /// Normal distributions fitted to a candidate pool, one per weather parameter, for a
        /// campaign that draws each parameter independently rather than replaying a historical day.
        /// </summary>
        /// <remarks>
        /// Independent marginals are a real simplification and worth stating plainly: temperature
        /// and relative humidity are anticorrelated on fire-weather days — r = −0.59 over the Mati
        /// pool — and drawing them separately produces realizations that are hot and humid together,
        /// a combination the record does not contain. Fitting a covariance and drawing jointly would
        /// fix it, and is the natural next step if it turns out to matter.
        ///
        /// Measured, it matters less than it sounds, and in the tail rather than the body. Converting
        /// 5000 independent draws to a 1-hour moisture gives a 5th-to-95th percentile of 2.6–5.7 %
        /// against the pool days' own 2.5–5.9 % — no wider. The extremes are where independence
        /// shows: the driest draw reaches 0.4 % where no pool day goes below 1.3 %, because nothing
        /// stops the hottest draw meeting the driest one. That tail is a real fire, just a rarer one
        /// than its draw frequency implies.
        ///
        /// Wind direction is deliberately absent. It is a circular quantity, so a normal fitted to
        /// degrees is not merely imprecise but wrong — a mean near 350 degrees with any real spread
        /// draws impossible values, and 10 and 350 degrees average to 180, the exact opposite of
        /// both. The campaign aims the wind from each realization's ignition at the WUI area
        /// instead, and when that is turned off the direction is resampled from the pool
        /// (<see cref="DrawWindDirection"/>) where it can only ever be a direction that occurred.
        /// </remarks>
        public struct FittedFireWeather
        {
            public NormalFit Temperature;         // deg C
            public NormalFit RelativeHumidity;    // %
            public NormalFit WindSpeed;           // m/s
            public NormalFit Fwi;

            /// <summary>
            /// Descriptive mean and spread of the live herbaceous and live woody moisture over the
            /// candidate days, from the NFDRS4 GSI model's seasonal state rather than from anything the
            /// archive reports. <b>Reported, not drawn from</b> — see <see cref="LivePairs"/>.
            /// </summary>
            /// <remarks>
            /// A zero standard deviation here is a real answer, not a missing one. If the domain's
            /// fire season sits entirely inside the cured period, every candidate day returns the same
            /// herbaceous minimum.
            /// </remarks>
            public NormalFit LiveHerbaceous;      // %
            public NormalFit LiveWoody;           // %

            /// <summary>
            /// Every candidate day's (herbaceous, woody) live moisture pair, in percent — the sample the
            /// realizations resample from.
            /// </summary>
            /// <remarks>
            /// Empirical rather than a fitted normal, which is a deliberate exception to how every other
            /// parameter here is drawn, for two reasons that a normal cannot be talked out of.
            ///
            /// <b>The distribution is bunched on a floor, so a normal is the wrong family.</b> Live
            /// moisture on peak fire-weather days sits at the fully-cured minimum for most of the pool —
            /// over the Mati record the herbaceous 5th, 50th percentiles are both exactly 30 %, with a
            /// thin tail to 94 %. Fitting N(32.2, 9.4) to that and truncating the draws at the 30 %
            /// floor turns it into a half-normal: the realized mean came out at 38.3 %, six points
            /// wetter than the pool it was meant to represent, biasing every realization toward a less
            /// severe fire. Resampling reproduces the pool mean by construction.
            ///
            /// <b>It keeps the two fuels consistent for free.</b> The pair is taken from one day, so the
            /// herbaceous and woody values always belong to the same point in the same season. That
            /// matters because the two are the same GSI curve on different scales
            /// (<see cref="LiveFuelMoistureSampler"/>), so any independent draw would invent
            /// combinations — cured grass under green shrubs — the model cannot produce.
            ///
            /// Null when <see cref="Fit"/> was given no marched series.
            /// </remarks>
            public (double Herbaceous, double Woody)[] LivePairs;

            /// <summary>Whether live moisture is available to draw at all.</summary>
            public bool LiveFitted;

            /// <summary>Every candidate day's wind direction, for empirical resampling.</summary>
            public double[] WindDirections;

            /// <summary>Days in the pool, and how many calendar years they came from.</summary>
            public int Count, Years;
        }

        /// <summary>Fits a normal per parameter to the pool. Throws on an empty pool rather than
        /// returning a degenerate fit that would silently make every realization identical.</summary>
        /// <param name="live">
        /// The GSI model's live moisture by date, from <see cref="LiveFuelMoistureSampler.March"/>.
        /// Optional: without it the live fits are left empty and the caller keeps whatever the case's
        /// namelist already says.
        /// </param>
        public static FittedFireWeather Fit(IReadOnlyList<AnnualMaximaDay> pool,
            IReadOnlyDictionary<DateTime, LiveFuelMoistureDay> live = null)
        {
            if (pool == null || pool.Count == 0)
                throw new InvalidOperationException("No candidate days to fit a distribution to.");

            var fit = new FittedFireWeather
            {
                Temperature = FitOne(pool, d => d.Temperature),
                RelativeHumidity = FitOne(pool, d => d.RelativeHumidity),
                WindSpeed = FitOne(pool, d => d.WindSpeed),
                Fwi = FitOne(pool, d => d.Fwi),
                WindDirections = pool.Select(d => d.WindDirection).ToArray(),
                Count = pool.Count,
                Years = pool.Select(d => d.Year).Distinct().Count(),
            };

            if (live != null && live.Count > 0)
            {
                //Only the candidate days, not the whole marched record: fitting over every day of 26
                //years would describe the domain's average vegetation state, where what is wanted is
                //its state on the days fires like this one happen.
                var onPool = pool.Select(d => live.TryGetValue(d.Date.Date, out LiveFuelMoistureDay l)
                                              ? (LiveFuelMoistureDay?)l : null)
                                 .Where(l => l.HasValue).Select(l => l.Value).ToList();

                if (onPool.Count > 0)
                {
                    //The pairs are what gets drawn; the two NormalFits alongside them are descriptive, for
                    //the log and the distribution report. See FittedFireWeather.LivePairs for why this one
                    //parameter is resampled rather than fitted.
                    fit.LivePairs = onPool.Select(l => (l.HerbaceousPercent, l.WoodyPercent)).ToArray();
                    fit.LiveHerbaceous = FitNormal(onPool.Select(l => l.HerbaceousPercent).ToList());
                    fit.LiveWoody = FitNormal(onPool.Select(l => l.WoodyPercent).ToList());
                    fit.LiveFitted = true;
                }
            }

            return fit;
        }

        /// <summary>Fits a normal to a plain sample, for quantities that are derived rather than read
        /// off an <see cref="AnnualMaximaDay"/>.</summary>
        public static NormalFit FitNormal(IReadOnlyList<double> sample)
        {
            if (sample == null || sample.Count == 0) return default;

            double mean = sample.Average();
            double variance = sample.Count > 1
                ? sample.Sum(v => (v - mean) * (v - mean)) / (sample.Count - 1)
                : 0.0;

            return new NormalFit
            {
                Mean = mean,
                StandardDeviation = System.Math.Sqrt(variance),
                SampleMin = sample.Min(),
                SampleMax = sample.Max(),
            };
        }

        private static NormalFit FitOne(IReadOnlyList<AnnualMaximaDay> pool, Func<AnnualMaximaDay, double> select)
        {
            (double mean, double std) = MeanStd(pool, select);
            return new NormalFit
            {
                Mean = mean,
                StandardDeviation = std,
                SampleMin = pool.Min(select),
                SampleMax = pool.Max(select),
            };
        }

        /// <summary>One realization's weather: a scalar per parameter, held for the whole fire.</summary>
        public struct DrawnFireWeather
        {
            public double Temperature;         // deg C
            public double RelativeHumidity;    // %
            public double WindSpeedMps;
            public double WindDirectionDeg;

            /// <summary>
            /// Live herbaceous and live woody moisture, percent, drawn from the GSI-derived fits.
            /// Both zero and <see cref="LiveDrawn"/> false when no marched series was available, which
            /// means the caller must leave the case's own live moisture alone rather than write a zero.
            /// </summary>
            public double LiveHerbaceousPercent;
            public double LiveWoodyPercent;
            public bool LiveDrawn;

            /// <summary>True when the direction is a resampled historical one rather than the caller's.</summary>
            public bool DirectionResampled;
        }

        /// <summary>
        /// Draws one realization from a fit: each parameter independently from its own normal,
        /// truncated to what the parameter can physically be.
        /// </summary>
        /// <remarks>
        /// The bounds are physical limits, not the sample's own range. Truncating to the observed
        /// minimum and maximum would make the fitted distribution unable to produce a day worse than
        /// the worst on record, which is precisely the extrapolation a fitted distribution is for —
        /// the record is 25 years long and the boundary is meant to hold for longer than that.
        /// </remarks>
        public static DrawnFireWeather Draw(FittedFireWeather fit, MonteCarloRng rng)
        {
            var drawn = new DrawnFireWeather
            {
                //-60..60 C: wide enough never to bind on a real fit, narrow enough that a corrupt
                //archive producing a nonsense mean is caught by the clamp instead of reaching WindNinja.
                Temperature = rng.NextNormalInRange(fit.Temperature.Mean, fit.Temperature.StandardDeviation,
                                  -60.0, 60.0),

                //1 rather than 0: Simard's fit is evaluated at this value and a humidity of zero is
                //not a thing the atmosphere does.
                RelativeHumidity = rng.NextNormalInRange(fit.RelativeHumidity.Mean,
                                       fit.RelativeHumidity.StandardDeviation, 1.0, 100.0),

                //Floored above zero: a calm draw is physical but a fire that cannot spread contributes
                //no arrival time, so the realization would be a wasted ELMFIRE run rather than a data point.
                WindSpeedMps = rng.NextNormalInRange(fit.WindSpeed.Mean, fit.WindSpeed.StandardDeviation,
                                   0.5, 60.0),

                WindDirectionDeg = DrawWindDirection(fit, rng),
                DirectionResampled = true,
            };

            if (fit.LiveFitted)
            {
                DrawLive(fit, rng, ref drawn);
            }

            return drawn;
        }

        /// <summary>
        /// Draws the two live fuel moistures by <b>resampling one candidate day's pair intact</b>, rather
        /// than from a fitted normal like every other parameter here.
        /// </summary>
        /// <remarks>
        /// See <see cref="FittedFireWeather.LivePairs"/> for why: the distribution is bunched on the
        /// fully-cured floor, so a truncated normal fitted to it came out six percentage points wetter
        /// than the pool, and the two fuels have to stay on the same point of the same seasonal curve.
        /// Resampling fixes both at once — the pool mean is reproduced by construction, and the pair can
        /// only ever be a state the GSI model actually reached.
        ///
        /// The cost is that the ensemble cannot reach a live moisture the record does not contain, which
        /// is the extrapolation the fitted normals exist to provide for temperature, humidity and wind.
        /// That trade is right here and wrong there: those three are smooth and unbounded over their
        /// plausible range, where live moisture is a bounded seasonal state whose extremes are the
        /// model's own hard limits rather than a tail to be extended.
        /// </remarks>
        private static void DrawLive(FittedFireWeather fit, MonteCarloRng rng, ref DrawnFireWeather drawn)
        {
            if (fit.LivePairs == null || fit.LivePairs.Length == 0) return;

            (double herb, double woody) = fit.LivePairs[rng.Range(0, fit.LivePairs.Length)];

            drawn.LiveHerbaceousPercent = herb;
            drawn.LiveWoodyPercent = woody;
            drawn.LiveDrawn = true;
        }

        /// <summary>
        /// Resamples a wind direction from the pool — one candidate day's actual direction, unchanged.
        /// </summary>
        /// <remarks>
        /// Empirical rather than fitted because direction is circular; see
        /// <see cref="FittedFireWeather"/>. This is only reached when the caller is not supplying a
        /// direction of its own, which for the trigger campaign means <c>--no-wind-to-wui</c>.
        /// </remarks>
        public static double DrawWindDirection(FittedFireWeather fit, MonteCarloRng rng)
        {
            if (fit.WindDirections == null || fit.WindDirections.Length == 0) return 0.0;
            return fit.WindDirections[rng.Range(0, fit.WindDirections.Length)];
        }
    }
}
