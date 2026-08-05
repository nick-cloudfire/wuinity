using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace PREACT.Utility
{
    /// <summary>One day's live fuel moisture, as the NFDRS4 GSI model has it, in percent.</summary>
    public struct LiveFuelMoistureDay
    {
        public DateTime Date;

        /// <summary>Live herbaceous, percent of oven-dry weight.</summary>
        public double HerbaceousPercent;

        /// <summary>Live woody, percent of oven-dry weight.</summary>
        public double WoodyPercent;

        /// <summary>The running-average Growing Season Index behind the two, 0..1. Reported rather than
        /// used: it is the diagnostic that tells you whether greenup was ever reached.</summary>
        public double GrowingSeasonIndex;
    }

    /// <summary>
    /// Drives the vendored NFDRS4 live fuel moisture engine
    /// (<see cref="NFDRS4.LiveFuelMoisture"/>) over a whole hourly weather record and returns a live
    /// herbaceous and live woody moisture for every day in it.
    ///
    /// Live fuel moisture is a seasonal state, not a weather observation. It is where the vegetation
    /// has got to in its growing season â€” driven by day length, night temperatures, vapour pressure
    /// deficit and antecedent rain integrated over weeks â€” so unlike wind or humidity there is no
    /// meaningful way to read it off the hour a fire starts. That is why this marches the record
    /// rather than evaluating a formula: the engine carries the state, and the state is the answer.
    /// </summary>
    /// <remarks>
    /// Consequences worth knowing before reading its numbers.
    ///
    /// <b>It is spatially uniform, and correctly so.</b> The NFDRS4 model takes latitude and weather
    /// and nothing else â€” no slope, no aspect, no elevation, no fuel type. There is no per-cell answer
    /// to extract, which is why the campaign carries live moisture as a namelist scalar rather than as
    /// an <c>MLH</c>/<c>MLW</c> raster: a raster would be a constant written a million times over, and
    /// would imply a spatial resolution the model does not have.
    ///
    /// <b>It is a US NFDRS model in US units.</b> The engine wants Fahrenheit and inches, and gets them
    /// here; passing ERA5's Celsius and millimetres straight through would leave the precipitation index
    /// saturated and the fuels permanently green.
    ///
    /// <b>Greenup is thresholded.</b> Below <see cref="Options.GreenupThreshold"/> of the running GSI
    /// the herbaceous fuel is held fully cured at <see cref="Options.HerbaceousMinimumPercent"/>. On a
    /// domain whose fire season is deep in the cured period this means every candidate day returns the
    /// same herbaceous minimum, so the fitted distribution has zero spread â€” which is a true statement
    /// about the fuel, not a failure, and <see cref="March"/> logs the spread so it is visible either way.
    ///
    /// <b>Two defects in the vendored binding, both measured, both worked around here. Do not "simplify"
    /// this back.</b>
    ///
    /// <see cref="NFDRS4.LiveFuelMoisture.GetMoisture()"/> â€” the accessor the API is obviously built
    /// around â€” <b>is not deterministic</b>. Marching the same 9,497 days of the Mati record three times
    /// in three processes returned a herbaceous range of 60â€“60 % once and 60â€“185 % twice, from byte-identical
    /// input. Its value also changed when an unrelated overload was swapped at the call site, and it
    /// sometimes returns exactly the configured minimum for every day of the record. That is the signature
    /// of a read of uninitialised memory, not of a model. <see cref="NFDRS4.LiveFuelMoisture.CalcRunningAvgHerbFM()"/>
    /// is no better: it returned the bare minimum for every day in every run. Only
    /// <see cref="NFDRS4.LiveFuelMoisture.CalcRunningAvgWoodyFM(bool)"/> and
    /// <see cref="NFDRS4.LiveFuelMoisture.CalcRunningAvgGSI"/> were stable across every run and every
    /// configuration, so those are the only two used â€” including for the herbaceous fuel, despite the name.
    ///
    /// The <c>IsHerb</c> and <c>IsAnnual</c> constructor flags are <b>inert</b> in this build: all four
    /// combinations returned bit-identical series. So the herbaceous and woody fuels here differ only by
    /// the <c>[min, max]</c> range their shared GSI curve is mapped onto, and <b>the annual-curing rule is
    /// not applied</b> â€” <see cref="Options.HerbaceousAnnual"/> is passed through for when the binding is
    /// fixed, but changes nothing today. The practical consequence is that the two live moistures are
    /// perfectly rank-correlated, which is why <see cref="ClimatologySampler.Draw"/> couples them with one
    /// shared deviate rather than drawing them independently: independent draws would manufacture
    /// realizations with cured grass under fully green shrubs, which this model cannot produce.
    /// </remarks>
    public static class LiveFuelMoistureSampler
    {
        public class Options
        {
            /// <summary>Domain latitude, degrees. Sets day length, which is a GSI input.</summary>
            public double Latitude;

            /// <summary>
            /// Days of antecedent rain summed into the engine's precipitation index, and told to the
            /// engine so its own bookkeeping agrees with the total being passed in.
            /// </summary>
            public int PrecipitationDays = 30;

            /// <summary>Days in the GSI running average. NFDRS4's own default is 21.</summary>
            public int MovingAveragePeriodDays = 21;

            /// <summary>
            /// True when the herbaceous fuel is annual â€” it cures out completely and does not recover
            /// until the next greenup. False for perennial, which recovers within a season.
            /// </summary>
            /// <remarks>
            /// Annual by default, which is the Mediterranean grass case the trigger work is aimed at.
            /// It matters: a perennial herbaceous fuel keeps a residual moisture through the summer and
            /// so spreads fire less readily than the fully cured annual one.
            /// </remarks>
            public bool HerbaceousAnnual = true;

            /// <summary>Maximum GSI, and the fraction of it at which greenup starts.</summary>
            public double MaximumGsi = 1.0;
            public double GreenupThreshold = 0.5;

            /// <summary>
            /// The range each fuel's moisture is mapped onto between full cure and full greenup, percent.
            /// These are NFDRS4's documented defaults, set explicitly rather than left to the native
            /// side so a campaign's numbers do not depend on which build of it is linked.
            /// </summary>
            public double HerbaceousMinimumPercent = 30.0;
            public double HerbaceousMaximumPercent = 250.0;
            public double WoodyMinimumPercent = 60.0;
            public double WoodyMaximumPercent = 200.0;
        }

        /// <summary>One calendar day reduced to what the GSI model asks for.</summary>
        private struct DailyWeather
        {
            public DateTime Date;
            public double MeanTemperatureF, MaxTemperatureF, MinTemperatureF;
            public double MeanRelativeHumidity, MinRelativeHumidity;
            public double PrecipitationMm;
        }

        private const double MmToInches = 1.0 / 25.4;

        private static double CToF(double c) => c * 9.0 / 5.0 + 32.0;

        /// <summary>
        /// Marches the engine day by day over the whole record and returns every day's live moisture,
        /// keyed by date at midnight.
        /// </summary>
        /// <remarks>
        /// One continuous march rather than one per day of interest: the state is the point, and
        /// restarting it for each candidate day would throw away exactly the seasonal history that
        /// distinguishes a June day from a September one. It costs one pass and no repetition.
        ///
        /// Throws if the native engine cannot be reached, rather than returning silently empty â€” a
        /// campaign that quietly fell back to a guessed live moisture while reporting a fitted one
        /// would be worse than one that stops.
        /// </remarks>
        public static Dictionary<DateTime, LiveFuelMoistureDay> March(
            IEnumerable<HourlyWeatherRow> rows, Options o, Action<string> log = null)
        {
            List<DailyWeather> days = ToDailyWeather(rows);
            var result = new Dictionary<DateTime, LiveFuelMoistureDay>(days.Count);

            if (days.Count == 0) return result;

            //Running antecedent rain, in the window the engine is told about. Kept as a queue rather
            //than recomputed per day so the march stays one pass over the record.
            var window = new Queue<double>();
            double windowTotalMm = 0.0;

            var herb = new NFDRS4.LiveFuelMoisture(o.Latitude, true, o.HerbaceousAnnual);
            var woody = new NFDRS4.LiveFuelMoisture(o.Latitude, false, false);

            //time_t is a 64-bit signed count of seconds on every platform this ships to (MSVC's
            //__time64_t and glibc's time_t alike). Allocated once and rewritten per day rather than
            //allocated 9,000 times.
            IntPtr time = Marshal.AllocHGlobal(sizeof(long));

            try
            {
                foreach (NFDRS4.LiveFuelMoisture stick in new[] { herb, woody })
                {
                    stick.SetMAPeriod((uint)System.Math.Max(o.MovingAveragePeriodDays, 1));
                    stick.SetNumPrecipDays(System.Math.Max(o.PrecipitationDays, 1));
                }

                herb.SetLFMParameters(o.MaximumGsi, o.GreenupThreshold,
                                      o.HerbaceousMinimumPercent, o.HerbaceousMaximumPercent);
                woody.SetLFMParameters(o.MaximumGsi, o.GreenupThreshold,
                                       o.WoodyMinimumPercent, o.WoodyMaximumPercent);

                foreach (DailyWeather d in days)
                {
                    window.Enqueue(d.PrecipitationMm);
                    windowTotalMm += d.PrecipitationMm;
                    while (window.Count > System.Math.Max(o.PrecipitationDays, 1))
                    {
                        windowTotalMm -= window.Dequeue();
                    }

                    double runningPrecipInches = windowTotalMm * MmToInches;
                    Marshal.WriteInt64(time, ToUnixSeconds(d.Date));
                    var when = new NFDRS4.SWIGTYPE_p_time_t(time, false);

                    herb.Update(d.MeanTemperatureF, d.MaxTemperatureF, d.MinTemperatureF,
                                d.MeanRelativeHumidity, d.MinRelativeHumidity,
                                d.Date.DayOfYear, runningPrecipInches, when);
                    woody.Update(d.MeanTemperatureF, d.MaxTemperatureF, d.MinTemperatureF,
                                 d.MeanRelativeHumidity, d.MinRelativeHumidity,
                                 d.Date.DayOfYear, runningPrecipInches, when);

                    //CalcRunningAvgWoodyFM, not GetMoisture, and for both sticks. See the class remarks:
                    //GetMoisture in this build returns a value that varies between processes on identical
                    //input, so it cannot be used for anything reproducible.
                    result[d.Date.Date] = new LiveFuelMoistureDay
                    {
                        Date = d.Date.Date,
                        HerbaceousPercent = herb.CalcRunningAvgWoodyFM(false),
                        WoodyPercent = woody.CalcRunningAvgWoodyFM(false),
                        GrowingSeasonIndex = herb.CalcRunningAvgGSI(),
                    };
                }
            }
            finally
            {
                Marshal.FreeHGlobal(time);
            }

            if (log != null && result.Count > 0)
            {
                List<LiveFuelMoistureDay> v = result.Values.ToList();
                log($"  live moisture: NFDRS4 GSI marched over {result.Count} days at lat {o.Latitude:F2}; "
                    + $"herbaceous {v.Min(x => x.HerbaceousPercent):F0}-{v.Max(x => x.HerbaceousPercent):F0} %, "
                    + $"woody {v.Min(x => x.WoodyPercent):F0}-{v.Max(x => x.WoodyPercent):F0} %, "
                    + $"GSI {v.Min(x => x.GrowingSeasonIndex):F2}-{v.Max(x => x.GrowingSeasonIndex):F2}"
                    + (v.Max(x => x.GrowingSeasonIndex) < o.GreenupThreshold
                        ? " - never reached greenup, so both fuels stay fully cured all year." : "."));
            }

            return result;
        }

        /// <summary>
        /// Reduces the hourly record to the daily extremes and means the GSI model reads, converting to
        /// the engine's Fahrenheit as it goes.
        /// </summary>
        /// <remarks>
        /// Days with fewer than 24 rows are kept rather than dropped. A part-day at either end of the
        /// record, or a gap in ERA5, gives a slightly less extreme min and max than the real day had;
        /// dropping it instead would break the march's continuity, and a discontinuity in a state
        /// variable is a worse error than a slightly smoothed one.
        /// </remarks>
        private static List<DailyWeather> ToDailyWeather(IEnumerable<HourlyWeatherRow> rows)
        {
            return rows.GroupBy(r => r.Time.Date)
                       .OrderBy(g => g.Key)
                       .Select(g => new DailyWeather
                       {
                           Date = g.Key,
                           MeanTemperatureF = CToF(g.Average(r => r.Temperature)),
                           MaxTemperatureF = CToF(g.Max(r => r.Temperature)),
                           MinTemperatureF = CToF(g.Min(r => r.Temperature)),
                           MeanRelativeHumidity = g.Average(r => r.RelativeHumidity),
                           MinRelativeHumidity = g.Min(r => r.RelativeHumidity),
                           PrecipitationMm = g.Sum(r => r.Precipitation),
                       })
                       .ToList();
        }

        private static long ToUnixSeconds(DateTime date)
        {
            return (long)(DateTime.SpecifyKind(date.Date, DateTimeKind.Utc)
                          - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}
