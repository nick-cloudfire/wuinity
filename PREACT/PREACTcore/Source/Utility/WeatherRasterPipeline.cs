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

        /// <summary>How a realization's weather is drawn out of the climatology.</summary>
        public enum SamplingMode
        {
            /// <summary>
            /// Replay one actual historical day: draw a peak fire-weather day from the record and use
            /// its own hours — WindNinja per band from the archive's wind, Nelson marched over the
            /// twenty real days before it.
            /// </summary>
            /// <remarks>
            /// Every parameter stays consistent with every other because they all come from the same
            /// day, and the dead fuel moisture has the antecedent drying history that actually
            /// determines it. The cost is that the ensemble can only ever contain days that happened:
            /// with a 25-year record there are 25 of them, so a large campaign draws each one many
            /// times over and the weather contributes far less variety than the count of realizations
            /// suggests.
            /// </remarks>
            HistoricalDay,

            /// <summary>
            /// Fit a normal distribution to each weather parameter over a pool of the record's worst
            /// fire-weather days, and draw each parameter independently per realization, held constant
            /// for the whole fire.
            /// </summary>
            /// <remarks>
            /// The ensemble is then continuous in weather rather than confined to the days on record,
            /// and can reach a day worse than any of them — which is the point, since the record is
            /// shorter than the period a trigger boundary is meant to hold for.
            ///
            /// Two things are given up, both consequences of there being no historical day any more.
            /// The parameters are drawn independently, so a realization can be hot and humid at once
            /// in a way the record never is. And Nelson cannot run at all — it needs a real series of
            /// antecedent hours — so the dead fuel moisture comes from the drawn air temperature and
            /// humidity through <see cref="FireWeatherIndex.EquilibriumMoisturePercent"/> instead,
            /// which knows nothing of antecedent drying and is spatially uniform where Nelson varied
            /// with slope, aspect and canopy.
            /// </remarks>
            FittedDistributions,
        }

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

            /// <summary>
            /// WindNinja's mesh, which sets how much terrain its solver can actually see.
            /// </summary>
            /// <remarks>
            /// <c>fine</c> rather than <c>coarse</c>, measured on the Mati domain (~15 km, 27.6 m master
            /// grid): coarse meshes at 263 m and takes 0.4 s, fine meshes at 117 m and takes 5 s, and the
            /// difference is not cosmetic — speed spans 5.3-18.2 mph on the fine mesh against 7.8-15.4 on
            /// the coarse one, from the same 11.2 mph domain average. The coarse mesh smooths away the
            /// terrain-driven variation that is the entire reason for running WindNinja, and 5 s is nothing
            /// against a case build. Worth revisiting per realization in an ensemble, where the 11x shows up
            /// multiplied.
            ///
            /// Finer than this is not simply better: the solver cost climbs roughly with the cube of the
            /// mesh count, so meshing at the master grid's 27.6 m would be some two orders of magnitude
            /// slower to resolve terrain a mass-conserving model does not claim to.
            /// </remarks>
            public string WindNinjaMesh = "fine";

            /// <summary>
            /// The fire's own window, which is what the weather series has to span. ELMFIRE reads band
            /// <c>k</c> for the <c>k</c>th <c>DT_METEOROLOGY</c> step from the start of the simulation, so a
            /// series shorter than the run leaves the later hours reading past the end of it.
            /// </summary>
            /// <remarks>
            /// Only the <b>hour of day</b> of the start is used, not the date: the weather comes from a
            /// historical day drawn out of the record, and it is the diurnal phase that has to line up. A
            /// fire starting at 13:00 gets the sampled day's 13:00 in band 1 and walks forward from there
            /// through real consecutive archive hours, so a multi-day run follows a real sequence of days
            /// rather than looping one day back onto itself.
            /// </remarks>
            public DateTime SimulationStartDateTime = new DateTime(2020, 7, 1, 12, 0, 0);

            public double SimulationTstopSeconds = 28800.0;

            /// <summary>
            /// Seconds each band covers. Must be <c>DT_METEOROLOGY</c> from the namelist, and the builder
            /// passes it from there so the two cannot disagree — a series written at one interval and read
            /// at another is silently stretched or compressed in time.
            /// </summary>
            public double SecondsPerBand = 3600.0;

            /// <summary>
            /// Ceiling on how many bands are produced, because each one is a WindNinja solve.
            /// </summary>
            /// <remarks>
            /// A band costs roughly 5 s on a 15 km domain at the default fine mesh, so a 72-hour ensemble
            /// realization would spend some six minutes in WindNinja alone.
            ///
            /// Hitting the cap writes <b>one</b> band rather than <see cref="MaxBands"/> of them, because a
            /// multi-band series shorter than the run is refused outright by ELMFIRE while a single band is
            /// held — see <see cref="BuildBandSchedule"/>. Raise it to get the full series.
            /// </remarks>
            public int MaxBands = 72;

            /// <summary>Seeds the choice of historical day, so a realization is reproducible.</summary>
            public int Seed;

            /// <summary>
            /// Whether a realization replays a historical day or draws each parameter from a fitted
            /// distribution. See <see cref="SamplingMode"/>.
            /// </summary>
            /// <remarks>
            /// Defaults to <see cref="SamplingMode.HistoricalDay"/> because that is what a case build
            /// wants: one case, one weather series, and every reason to prefer a real day with
            /// Nelson's terrain-varying moisture over a synthetic one. The trigger campaign overrides
            /// it, where the argument runs the other way.
            /// </remarks>
            public SamplingMode Sampling = SamplingMode.HistoricalDay;

            /// <summary>
            /// Days per calendar year in the pool the distributions are fitted to, highest FWI first.
            /// Only read in <see cref="SamplingMode.FittedDistributions"/>.
            /// </summary>
            /// <remarks>
            /// Ten rather than one so a 26-year record yields 260 days to take a standard deviation
            /// from instead of 26. It is a severity dial as much as a sample size: a deeper pool
            /// reaches down into milder days and pulls the fitted mean with it — measured, 1.9 °C and
            /// 4 points of humidity between 1 a year and 10. See
            /// <see cref="ClimatologySampler.BuildCandidatePool"/> for the numbers.
            /// </remarks>
            public int CandidateDaysPerYear = 10;

            /// <summary>
            /// How much moister the 10- and 100-hour dead fuels are taken to be than the 1-hour stick,
            /// in percentage points, when moisture is derived from drawn air rather than marched.
            /// </summary>
            /// <remarks>
            /// The equilibrium relation describes the 1-hour stick and nothing slower: a 10- or
            /// 100-hour fuel lags the air by construction, and how far behind it sits depends on the
            /// days before the fire, which a single drawn hour does not contain. +1 and +2 points is
            /// the standard fire-behaviour field convention for exactly this situation (Rothermel
            /// 1983) and is what these default to.
            ///
            /// It is a convention, not a model. Under a long drought the real offsets go negative —
            /// the heavier fuels end up drier than an afternoon's humidity would suggest — and nothing
            /// here can know that. <see cref="SamplingMode.HistoricalDay"/> with Nelson is the answer
            /// when the heavy fuels are what the fire turns on.
            /// </remarks>
            public double Lag10HourPercent = 1.0;
            public double Lag100HourPercent = 2.0;

            /// <summary>
            /// Also fit and draw live herbaceous and live woody moisture, from the NFDRS4 GSI model
            /// marched over the record. Only read in <see cref="SamplingMode.FittedDistributions"/>.
            /// </summary>
            /// <remarks>
            /// On by default. Left off, live moisture stays whatever the case's namelist says — which
            /// for a template-built case is one fixed pair of constants for every realization of every
            /// campaign, so the ensemble carries no live-fuel uncertainty at all.
            /// </remarks>
            public bool FitLiveFuelMoisture = true;

            /// <summary>Settings for that march. <c>Latitude</c> is filled from <see cref="LatLon"/>.</summary>
            public LiveFuelMoistureSampler.Options LiveFuelMoisture = new LiveFuelMoistureSampler.Options();

            /// <summary>
            /// A live moisture series already marched by the caller, to be used instead of marching one
            /// here.
            /// </summary>
            /// <remarks>
            /// The campaign sets this once at startup. The march is a seasonal state over the whole
            /// record and is identical for every realization of a case, so doing it per realization
            /// would repeat some 19,000 native calls per fire for a result that cannot differ — and
            /// realizations run as concurrent tasks in one process, so it would repeat them in parallel.
            /// Left null, this class marches its own, which is what a one-off case build wants.
            /// </remarks>
            public IReadOnlyDictionary<DateTime, LiveFuelMoistureDay> LiveMoistureByDate;

            /// <summary>Use this specific historical day instead of drawing one — for a deterministic
            /// baseline, or to replay a known event.</summary>
            public DateTime? ForceDate;

            /// <summary>Set false to skip the historical archive entirely and write uniform
            /// rasters from the fallback values — for an offline build, or to hold weather fixed
            /// while something else is being varied.</summary>
            public bool UseClimatology = true;

            /// <summary>
            /// Fetch and cache the ERA5 archive and stop there, writing no rasters.
            /// </summary>
            /// <remarks>
            /// For the campaign driver, which downloads the archive once at startup so that concurrent
            /// realizations cannot race to write the same cache file. It used to express that by passing a
            /// null <see cref="WindNinjaExe"/> and one conditioning day — which does not work, because
            /// <c>RunWind</c> looks the executable up itself when it is not given one. The "warm-up" was
            /// therefore solving eight WindNinja bands and marching Nelson over 34 hours before the campaign
            /// had started, and throwing all of it away with the temporary folder.
            /// </remarks>
            public bool ArchiveOnly;

            /// <summary>
            /// Overrides the direction every band's wind blows from, in degrees, before WindNinja sees it.
            /// Null leaves the sampled day's own directions alone.
            /// </summary>
            /// <remarks>
            /// Meteorological convention — the direction the wind comes <b>from</b> — which is what WindNinja
            /// takes and what ELMFIRE's <c>wd</c> raster holds.
            ///
            /// This is the input to the solve, not a substitute for it: the written <c>wd.tif</c> is still
            /// WindNinja's terrain-bent field, just one initialised from a chosen direction instead of the
            /// archive's. Overwriting <c>wd.tif</c> with a constant afterwards would throw the terrain away.
            ///
            /// Used by the campaign driver to aim each realization's wind from its ignition at the community —
            /// see <c>ConvergeTrigger.WindDirectionToWui</c>. Only the direction is replaced; speed, moisture
            /// and the day's diurnal shape remain the drawn day's.
            /// </remarks>
            public double? ForceWindDirectionDeg;

            /// <summary>Used when a stage cannot run: uniform wind (m/s) and dead moisture (%).</summary>
            public double FallbackWindSpeedMps = 5.0;
            public double FallbackWindDirectionDeg = 0.0;
            public double FallbackM1Percent = 6.0, FallbackM10Percent = 7.0, FallbackM100Percent = 8.0;

            public Action<string> Log;
        }

        public class Result
        {
            /// <summary>The historical day the realization's weather came from, if one was drawn.
            /// Null in <see cref="SamplingMode.FittedDistributions"/>, where there is no such day.</summary>
            public AnnualMaximaDay? Day;
            public int AnnualMaximaCount;

            /// <summary>The fitted distributions, and the scalars drawn from them — both null in
            /// <see cref="SamplingMode.HistoricalDay"/>. Carried out so a realization's weather can be
            /// reported and logged as the numbers it actually was.</summary>
            public ClimatologySampler.FittedFireWeather? Fitted;
            public ClimatologySampler.DrawnFireWeather? Drawn;

            public bool ClimatologyUsed;
            public bool WindNinjaUsed;
            public bool NelsonUsed;

            public double MeanWindSpeedMph;
            public double MeanM1Percent, MeanM10Percent, MeanM100Percent;

            /// <summary>Bands written to every one of the five weather rasters. Becomes NUM_METEOROLOGY_TIMES.</summary>
            public int BandCount;

            /// <summary>
            /// The moment in the record band 1 was written from — the sampled day at the simulation's own hour
            /// of day. Default when no day was drawn.
            /// </summary>
            /// <remarks>
            /// Carried out of here so the simulation's weather can be read at the same instant the fire's
            /// weather rasters were, instead of at the scenario's calendar date. It becomes
            /// <c>[Weather] WeatherAnchorDateTime</c>.
            /// </remarks>
            public DateTime BandAnchor;

            /// <summary>Seconds each band covers, which has to be the namelist's DT_METEOROLOGY.</summary>
            public double SecondsPerBand;

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
            ClimatologySampler.DrawnFireWeather? drawn = null;

            if (rows != null && rows.Count > 0)
            {
                if (o.Sampling == SamplingMode.FittedDistributions)
                {
                    drawn = DrawFromFit(o, result, rows, Log);
                }
                else
                {
                    day = DrawHistoricalDay(o, result, rows);
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
            else if (drawn.HasValue)
            {
                result.ClimatologyUsed = true;
            }
            else
            {
                Log($"  climatology: unavailable, using uniform {o.FallbackWindSpeedMps:F1} m/s @ {o.FallbackWindDirectionDeg:F0} deg.");
            }

            //A drawn scalar takes precedence over a day's, and either over the fallback. Only one of the
            //two can be set: the sampling mode decides which path ran.
            double windMps = drawn?.WindSpeedMps ?? day?.WindSpeed ?? o.FallbackWindSpeedMps;
            double windDir = drawn?.WindDirectionDeg ?? day?.WindDirection ?? o.FallbackWindDirectionDeg;

            //Nothing after this point is wanted when the caller only came for the archive.
            if (o.ArchiveOnly)
            {
                Log("  weather: archive only, no rasters written.");
                return result;
            }

            //---------------------------------------------------------------- the band schedule
            //Built once and handed to both stages, because the five rasters have to come out with the same
            //number of bands: ELMFIRE reads all of them against one NUM_METEOROLOGY_TIMES, and the case
            //builder counts that from ws.tif alone, so a wind series shorter than the moisture series would
            //have ELMFIRE read past the end of the moisture rasters.
            List<DateTime> bandTimes = BuildBandSchedule(o, day, rows, Log);
            result.BandCount = bandTimes.Count;
            result.SecondsPerBand = o.SecondsPerBand;

            //Only when a day was actually drawn: with no climatology the bands carry the fallback values and
            //are not positioned anywhere in the record, so there is nothing for the simulation to anchor to.
            result.BandAnchor = day.HasValue && bandTimes.Count > 0 ? bandTimes[0] : default;

            //---------------------------------------------------------------- WindNinja
            //A drawn realization has no place in the record, so there are no per-band archive hours to
            //read: its one band is solved from the drawn speed, and nothing is "missing" from the archive.
            RunWind(o, result, bandTimes, rows, windMps, windDir,
                    useArchivePerBand: !drawn.HasValue, log: Log);

            //---------------------------------------------------------------- dead fuel moisture
            if (drawn.HasValue)
            {
                //Not Nelson: it integrates real antecedent hours and a drawn realization has none.
                WriteEquilibriumMoisture(o, result, bandTimes.Count, drawn.Value, Log);
            }
            else
            {
                RunNelson(o, result, bandTimes, rows, day, Log);
            }

            return result;
        }

        /// <summary>
        /// Picks the historical day a realization replays: the one the caller asked for, or a uniform
        /// draw from the record's annual fire-weather maxima.
        /// </summary>
        private static AnnualMaximaDay? DrawHistoricalDay(Options o, Result result, List<HourlyWeatherRow> rows)
        {
            List<AnnualMaximaDay> maxima = ClimatologySampler.BuildAnnualMaxima(rows);
            result.AnnualMaximaCount = maxima.Count;

            if (maxima.Count == 0)
            {
                result.Fallbacks.Add("climatology: the archive contains no day with a non-zero FWI");
                return null;
            }

            if (o.ForceDate.HasValue)
            {
                DateTime want = o.ForceDate.Value.Date;
                return maxima.Any(m => m.Date.Date == want)
                    ? maxima.First(m => m.Date.Date == want)
                    : maxima.OrderBy(m => System.Math.Abs((m.Date.Date - want).TotalDays)).First();
            }

            return ClimatologySampler.Sample(maxima, new MonteCarloRng(o.Seed));
        }

        /// <summary>
        /// Fits a normal per weather parameter over the pool of the record's worst fire-weather days
        /// and draws one realization's scalars from it.
        /// </summary>
        /// <remarks>
        /// The fit is redone here per realization rather than computed once and passed in, which looks
        /// wasteful and is not: the archive has already been parsed by this point — that is the
        /// expensive part, and it happens per realization either way — and fitting six moments over a
        /// few hundred days is nothing beside the WindNinja solve that follows. Keeping it local means
        /// a realization is reproducible from its seed and the archive alone, with no fitted state
        /// threaded through the campaign that could drift out of step with the pool it came from.
        ///
        /// <c>ForceDate</c> is honoured even here, as an escape hatch: asking for a specific day is
        /// asking not to draw at all, so it falls back to replaying that day.
        /// </remarks>
        private static ClimatologySampler.DrawnFireWeather? DrawFromFit(Options o, Result result,
            List<HourlyWeatherRow> rows, Action<string> log)
        {
            List<AnnualMaximaDay> pool = ClimatologySampler.BuildCandidatePool(rows, o.CandidateDaysPerYear);
            result.AnnualMaximaCount = pool.Count;

            if (pool.Count == 0)
            {
                result.Fallbacks.Add("climatology: the archive contains no day with a non-zero FWI");
                return null;
            }

            IReadOnlyDictionary<DateTime, LiveFuelMoistureDay> live = o.LiveMoistureByDate;
            if (live == null && o.FitLiveFuelMoisture)
            {
                try
                {
                    o.LiveFuelMoisture.Latitude = o.LatLon.x;
                    live = LiveFuelMoistureSampler.March(rows, o.LiveFuelMoisture, log);
                }
                catch (Exception e)
                {
                    //Not fatal, but it must be visible: without it the realization keeps the case's own
                    //live moisture, which is a fixed pair of constants, and the ensemble then varies in
                    //dead fuel and wind only. Silently doing that while the mode claims to fit live
                    //moisture is the failure worth avoiding.
                    result.Fallbacks.Add("live moisture: " + e.Message);
                    log("  live moisture: NFDRS4 GSI unavailable (" + e.Message
                        + "); leaving the case's own live moisture in place.");
                }
            }

            ClimatologySampler.FittedFireWeather fit = ClimatologySampler.Fit(pool, live);
            result.Fitted = fit;

            var rng = new MonteCarloRng(o.Seed);
            ClimatologySampler.DrawnFireWeather d = ClimatologySampler.Draw(fit, rng);

            //A forced direction is the campaign aiming the wind at the community, which replaces the
            //resampled one. Recorded on the draw so the log does not claim a direction came from the
            //record when it did not.
            if (o.ForceWindDirectionDeg.HasValue)
            {
                d.WindDirectionDeg = o.ForceWindDirectionDeg.Value;
                d.DirectionResampled = false;
            }

            result.Drawn = d;

            log($"  climatology: {fit.Count} candidate days over {fit.Years} years " +
                $"({o.CandidateDaysPerYear}/year by FWI); fitted wind {fit.WindSpeed}, " +
                $"temperature {fit.Temperature}, RH {fit.RelativeHumidity} (mean, sd).");
            log($"  climatology: drew {d.WindSpeedMps:F1} m/s @ {d.WindDirectionDeg:F0} deg" +
                (d.DirectionResampled ? " (resampled)" : " (aimed)") +
                $", {d.Temperature:F1} C, RH {d.RelativeHumidity:F0}%, held for the whole fire.");

            if (d.LiveDrawn)
            {
                //"resampled from", not "fitted": this parameter alone is drawn from the pool's own pairs
                //rather than from a normal, and a log line saying otherwise would hide the one exception.
                log($"  live moisture: resampled from {fit.LivePairs.Length} candidate days "
                    + $"(herbaceous {fit.LiveHerbaceous}, woody {fit.LiveWoody} over them); "
                    + $"drew {d.LiveHerbaceousPercent:F0}% / {d.LiveWoodyPercent:F0}%.");
            }

            return d;
        }

        /// <summary>
        /// Writes the three moisture rasters from a drawn realization's air temperature and humidity:
        /// Simard's equilibrium content for the 1-hour stick, offset by
        /// <see cref="Options.Lag10HourPercent"/> and <see cref="Options.Lag100HourPercent"/> for the
        /// slower fuels.
        /// </summary>
        /// <remarks>
        /// Spatially uniform, unlike Nelson's, and that is a real loss rather than a detail: Nelson
        /// gave every slope and aspect its own stick, so a north-facing gully stayed damp while the
        /// ridge above it dried out, and the fire's behaviour differed accordingly. A drawn
        /// realization has one air temperature for the whole domain and no solar history to bend it
        /// with, so the raster is one value. The rasters are still written at full extent — ELMFIRE
        /// reads all five against one grid, and a constant is a legitimate field, just a flat one.
        ///
        /// Floored at 1%: Simard's fit goes very low, and at a moisture of zero a fuel model's
        /// moisture-damping coefficient stops being meaningful.
        /// </remarks>
        private static void WriteEquilibriumMoisture(Options o, Result result, int bands,
            ClimatologySampler.DrawnFireWeather drawn, Action<string> log)
        {
            double emc = FireWeatherIndex.EquilibriumMoisturePercent(drawn.Temperature, drawn.RelativeHumidity);

            double m1 = System.Math.Max(emc, 1.0);
            double m10 = System.Math.Max(emc + o.Lag10HourPercent, 1.0);
            double m100 = System.Math.Max(emc + o.Lag100HourPercent, 1.0);

            GeoTiffRasterWriter.WriteConstantTimeSeries(o.Grid, (float)m1, bands, Path.Combine(o.InputsDirectory, "m1.tif"));
            GeoTiffRasterWriter.WriteConstantTimeSeries(o.Grid, (float)m10, bands, Path.Combine(o.InputsDirectory, "m10.tif"));
            GeoTiffRasterWriter.WriteConstantTimeSeries(o.Grid, (float)m100, bands, Path.Combine(o.InputsDirectory, "m100.tif"));

            result.MeanM1Percent = m1;
            result.MeanM10Percent = m10;
            result.MeanM100Percent = m100;

            //Deliberately not added to Fallbacks. Nelson not running is what this mode is, not a stage that
            //degraded unexpectedly, and Fallbacks is printed per realization - so recording it there would
            //put an identical warning on every line of a campaign log and train the reader to skip them.
            log($"  moisture: {m1:F1}/{m10:F1}/{m100:F1} % (1/10/100 h) from the drawn " +
                $"{drawn.Temperature:F1} C / RH {drawn.RelativeHumidity:F0}% by Simard's equilibrium " +
                $"content, +{o.Lag10HourPercent:F0}/+{o.Lag100HourPercent:F0} for the slower fuels; " +
                "uniform over the domain and constant over the run.");
        }

        /// <summary>
        /// The archive hour each weather band draws from: one per <see cref="Options.SecondsPerBand"/> of
        /// the simulation, starting at the sampled day's date on the simulation's own hour of day and
        /// walking forward through consecutive hours.
        /// </summary>
        /// <remarks>
        /// Walking real consecutive hours, rather than cycling one day, is what makes a multi-day run
        /// coherent: hour 30 of a three-day ensemble realization is the second night of an actual historical
        /// sequence, with its actual overnight recovery, instead of the first day replayed.
        ///
        /// The times are advisory. A band whose hour is missing from the archive falls back to the sampled
        /// day's summary values rather than dropping the band, because a gap in the record must not change
        /// how many bands the five rasters have.
        /// </remarks>
        private static List<DateTime> BuildBandSchedule(Options o, AnnualMaximaDay? day,
            List<HourlyWeatherRow> rows, Action<string> log)
        {
            double seconds = o.SecondsPerBand > 0 ? o.SecondsPerBand : 3600.0;

            //Ceiling, not floor: a 90-minute run on hourly bands needs the second hour, and rounding down
            //would leave its last half hour reading a band that is not there.
            int wanted = (int)System.Math.Ceiling(System.Math.Max(o.SimulationTstopSeconds, 0.0) / seconds);
            if (wanted < 1) wanted = 1;

            //A drawn realization is one band by construction, whatever MaxBands says. The draw is a single
            //value per parameter held for the whole fire, so a second band could only ever be a copy of the
            //first - it would cost a WindNinja solve to write the same field again, and would then have to
            //cover the run to satisfy ELMFIRE. One band it exempts and holds, which is exactly the intent.
            if (o.Sampling == SamplingMode.FittedDistributions)
            {
                return new List<DateTime> { o.SimulationStartDateTime };
            }

            int count = wanted;
            if (o.MaxBands > 0 && count > o.MaxBands)
            {
                //Down to ONE band, not down to MaxBands. ELMFIRE does not hold the last band of a short
                //series - it refuses the run:
                //  IF (WS%NBANDS * DT_METEOROLOGY .LT. SIMULATION_TSTOP .AND. WS%NBANDS .GT. 1) -> [ERROR]
                //A single band is the one case it exempts, holding it for the whole fire. So every count
                //between 2 and `wanted` is unusable, and capping at MaxBands wrote a series that could not be
                //run at all while the log said it would be held. One band can be.
                count = 1;
                log($"  weather: {wanted} bands would cover the run, which is more than MaxBands "
                    + $"({o.MaxBands}) - each one is a WindNinja solve. Writing a single band instead, which "
                    + "ELMFIRE holds for the whole run; the diurnal cycle is lost. Raise MaxBands to at least "
                    + $"{wanted} for the full series.");
            }

            //No sampled day means no archive to walk; the schedule then exists only to give the uniform
            //fallbacks the right number of bands.
            DateTime anchor = day.HasValue
                ? day.Value.Date.Date.AddHours(o.SimulationStartDateTime.Hour)
                : o.SimulationStartDateTime;

            var times = new List<DateTime>(count);
            for (int b = 0; b < count; ++b)
            {
                times.Add(anchor.AddSeconds(b * seconds));
            }

            if (day.HasValue && rows != null && count > 1)
            {
                log($"  weather: {count} bands of {seconds / 3600.0:F0} h from {times[0]:yyyy-MM-dd HH:mm} " +
                    $"to {times[count - 1]:yyyy-MM-dd HH:mm}.");
            }

            return times;
        }

        /// <summary>
        /// Indexes the archive by whole hour, so each band's weather is one lookup.
        /// </summary>
        /// <remarks>
        /// Built once rather than searched per band: the archive is a quarter of a million rows for a
        /// 25-year record, and scanning it for every band of every realization would be the most expensive
        /// thing in the pipeline for no reason.
        /// </remarks>
        private static Dictionary<DateTime, HourlyWeatherRow> IndexByHour(List<HourlyWeatherRow> rows)
        {
            var index = new Dictionary<DateTime, HourlyWeatherRow>();
            if (rows == null) return index;

            foreach (HourlyWeatherRow r in rows)
            {
                //First wins: a duplicated hour in a re-downloaded archive should not shift the series.
                DateTime hour = new DateTime(r.Time.Year, r.Time.Month, r.Time.Day, r.Time.Hour, 0, 0);
                if (!index.ContainsKey(hour)) index[hour] = r;
            }

            return index;
        }

        /// <summary>
        /// The archive row for a band's hour, or null when the record does not reach it. Rounded to the
        /// nearest whole hour, since a band interval that is not a whole number of hours lands between rows.
        /// </summary>
        private static HourlyWeatherRow? RowAt(Dictionary<DateTime, HourlyWeatherRow> byHour, DateTime when)
        {
            DateTime rounded = new DateTime(when.Year, when.Month, when.Day, when.Hour, 0, 0);
            if (when.Minute >= 30) rounded = rounded.AddHours(1);

            return byHour.TryGetValue(rounded, out HourlyWeatherRow r) ? r : (HourlyWeatherRow?)null;
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

        /// <summary>
        /// Writes the <c>ws</c>/<c>wd</c> series: one WindNinja solve per band, from that band's own hour of
        /// the sampled day, stacked into a multi-band raster.
        /// </summary>
        /// <remarks>
        /// One solve per band rather than one for the run is the point of the series. A single solve made the
        /// wind that drives hour 8 the wind of hour 1, which for a fire whose weather turned is not merely
        /// imprecise — an afternoon shift is the mechanism that puts a community downwind in the first place,
        /// and a constant field cannot represent it at any spatial resolution.
        ///
        /// A band whose solve fails takes the previous band rather than failing the stage: the series has to
        /// keep its band count, and yesterday's hour is a far better answer for one band than abandoning
        /// terrain wind for the whole run.
        /// </remarks>
        /// <param name="useArchivePerBand">
        /// True to look each band's own hour up in the record, which is what makes a replayed day a
        /// series. False when the caller's <paramref name="windMps"/> and <paramref name="windDir"/>
        /// are the whole answer — a drawn realization, whose scalars belong to no date, so consulting
        /// the archive would silently substitute whatever weather happened to sit at the placeholder
        /// start time and report the draw as a band the record did not reach.
        /// </param>
        private static void RunWind(Options o, Result result, List<DateTime> bandTimes,
            List<HourlyWeatherRow> rows, double windMps, double windDir, bool useArchivePerBand,
            Action<string> log)
        {
            string dem = Path.Combine(o.Terrain, "dem.tif");

            //Probed here rather than by the caller. It was in the CLI's own argument parsing, which is how a
            //case built from the GUI came to write a uniform field on a machine with WindNinja installed;
            //doing it in the stage that needs it means no front end can omit it.
            if (string.IsNullOrEmpty(o.WindNinjaExe))
            {
                o.WindNinjaExe = WindNinjaRunner.FindExecutable();
                if (!string.IsNullOrEmpty(o.WindNinjaExe)) log("  wind: using " + o.WindNinjaExe);
            }

            bool haveExe = !string.IsNullOrEmpty(o.WindNinjaExe) && File.Exists(o.WindNinjaExe);

            if (haveExe && File.Exists(dem))
            {
                Dictionary<DateTime, HourlyWeatherRow> byHour = useArchivePerBand
                    ? IndexByHour(rows)
                    : new Dictionary<DateTime, HourlyWeatherRow>();

                var speeds = new List<float[,]>(bandTimes.Count);
                var directions = new List<float[,]>(bandTimes.Count);
                double meanTotal = 0, lastMean = 0;
                int solved = 0, reusedPrevious = 0, missingHours = 0;
                string firstFailure = null;

                for (int b = 0; b < bandTimes.Count; ++b)
                {
                    HourlyWeatherRow? row = RowAt(byHour, bandTimes[b]);

                    //Only a gap when the record was supposed to answer. A drawn realization never consults
                    //it, so counting these would report every band as missing from an archive it never asked.
                    if (!row.HasValue && useArchivePerBand) ++missingHours;

                    double bandMps = row?.WindSpeed ?? windMps;

                    //A forced direction replaces the archive's, and only the direction: the day's wind speed,
                    //its diurnal shape and the moisture that goes with it are still the sampled day's. WindNinja
                    //then does what it does - the field it returns is not uniform, it is this direction bent by
                    //the terrain, which is the point of forcing it here rather than writing wd.tif afterwards.
                    double bandDir = o.ForceWindDirectionDeg ?? (row?.WindDirection ?? windDir);

                    WindNinjaRunner.Result wn = WindNinjaRunner.Run(
                        o.WindNinjaExe, dem, o.Grid, o.InputsDirectory,
                        bandMps, bandDir, o.WindNinjaVegetation, o.WindNinjaMesh,
                        //Per-band logging would bury the build under one line per hour; the summary below
                        //reports the series instead.
                        log: null);

                    if (wn.Ok)
                    {
                        speeds.Add(wn.SpeedMph);
                        directions.Add(wn.DirectionDeg);
                        meanTotal += wn.MeanSpeedMph;
                        lastMean = wn.MeanSpeedMph;
                        ++solved;
                        continue;
                    }

                    firstFailure ??= wn.Message;

                    if (speeds.Count > 0)
                    {
                        speeds.Add(speeds[speeds.Count - 1]);
                        directions.Add(directions[directions.Count - 1]);
                        //The reused band's own mean, not the running average: it is a copy of that field.
                        meanTotal += lastMean;
                        ++reusedPrevious;
                        continue;
                    }

                    //The very first band failing means WindNinja cannot run here at all, so there is nothing
                    //to carry forward and the uniform fallback below is the honest outcome.
                    break;
                }

                if (speeds.Count == bandTimes.Count)
                {
                    GeoTiffRasterWriter.WriteBands(o.Grid, speeds, Path.Combine(o.InputsDirectory, "ws.tif"));
                    GeoTiffRasterWriter.WriteBands(o.Grid, directions, Path.Combine(o.InputsDirectory, "wd.tif"));

                    result.WindNinjaUsed = true;
                    result.MeanWindSpeedMph = meanTotal / speeds.Count;

                    log($"  wind: WindNinja solved {solved} of {bandTimes.Count} bands at {o.WindNinjaMesh} mesh " +
                        $"(mean {result.MeanWindSpeedMph:F1} mph across the series)." +
                        (reusedPrevious > 0 ? $" {reusedPrevious} band(s) reused the previous hour." : "") +
                        (missingHours > 0 ? $" {missingHours} band(s) fell outside the archive and used the day's summary wind." : ""));

                    if (reusedPrevious > 0) result.Fallbacks.Add($"WindNinja: {reusedPrevious} band(s) reused the previous hour ({firstFailure})");
                    if (missingHours > 0) result.Fallbacks.Add($"weather: {missingHours} band(s) outside the archive");
                    return;
                }

                result.Fallbacks.Add("WindNinja: " + firstFailure);
                log($"  wind: WindNinja unavailable ({firstFailure}); writing a uniform field instead.");
            }
            else if (haveExe)
            {
                //Distinguished from a missing executable because the remedy is unrelated: WindNinja needs the
                //terrain, and a case whose DEM stage failed cannot have terrain wind no matter what is installed.
                result.Fallbacks.Add("WindNinja: no DEM at " + dem);
                log($"  wind: no DEM at {dem}, so WindNinja cannot run; writing a uniform field.");
            }
            else
            {
                //Naming the probe matters: the fix for "not found" is to install it or set WindNinjaExe, and
                //neither is guessable from a message that only says the stage was skipped.
                result.Fallbacks.Add("WindNinja: not found");
                log("  wind: no WindNinja_cli.exe found (looked at WINDNINJA_CLI, PATH, C:\\WindNinja and "
                    + "Program Files); writing a uniform field, so the trigger boundary will be circular.");
            }

            //Still the full band count, even though every band holds the same value: the five rasters are
            //read against one NUM_METEOROLOGY_TIMES, so a fallback that wrote a single band would make the
            //moisture series unreadable past its first hour.
            const double mpsToMph = 2.2369362920544;
            double mph = windMps * mpsToMph;
            int bands = bandTimes.Count;

            //The forced direction applies here too. Without terrain to bend it this really is uniform, which
            //is a worse fire but still the direction that was asked for - silently reverting to the archive's
            //here would aim the wind somewhere else precisely when nobody is watching the solve.
            double uniformDir = o.ForceWindDirectionDeg ?? windDir;

            GeoTiffRasterWriter.WriteConstantTimeSeries(o.Grid, (float)mph, bands, Path.Combine(o.InputsDirectory, "ws.tif"));
            GeoTiffRasterWriter.WriteConstantTimeSeries(o.Grid, (float)uniformDir, bands, Path.Combine(o.InputsDirectory, "wd.tif"));
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
        private static void RunNelson(Options o, Result result, List<DateTime> bandTimes,
            List<HourlyWeatherRow> rows, AnnualMaximaDay? day, Action<string> log)
        {
            string reason = null;

            if (rows == null || !day.HasValue) reason = "no sampled day to condition from";
            else if (!File.Exists(Path.Combine(o.Terrain, "slp.tif"))) reason = "no slope raster";

            if (reason == null)
            {
                try
                {
                    NelsonMoisture(o, result, bandTimes, rows, day.Value, log);
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

            //Band count matched to the wind series, for the same reason the wind fallback matches it.
            int bands = bandTimes.Count;
            GeoTiffRasterWriter.WriteConstantTimeSeries(o.Grid, (float)o.FallbackM1Percent, bands, Path.Combine(o.InputsDirectory, "m1.tif"));
            GeoTiffRasterWriter.WriteConstantTimeSeries(o.Grid, (float)o.FallbackM10Percent, bands, Path.Combine(o.InputsDirectory, "m10.tif"));
            GeoTiffRasterWriter.WriteConstantTimeSeries(o.Grid, (float)o.FallbackM100Percent, bands, Path.Combine(o.InputsDirectory, "m100.tif"));
            result.MeanM1Percent = o.FallbackM1Percent;
            result.MeanM10Percent = o.FallbackM10Percent;
            result.MeanM100Percent = o.FallbackM100Percent;
        }

        /// <summary>
        /// Marches the sticks once and records each band's hour as it passes, giving a moisture series on the
        /// same bands as the wind.
        /// </summary>
        /// <remarks>
        /// <b>This replaces a single diurnal minimum over the burning period.</b> That minimum was chosen
        /// deliberately, for two reasons worth restating: it captured the driest hour of peak burning, which
        /// a daily mean would miss and run less conservative; and it was robust to ERA5 reporting a trace of
        /// drizzle — a few tenths of a mm averaged over ~9 km rather than rain that necessarily fell on the
        /// fuel — which Nelson correctly answers by soaking the 1-hour stick, so sampling the one hour after
        /// such a trace made the driest day of the decade come out sodden.
        ///
        /// Both concerns were about choosing <i>one</i> hour to stand for a whole day. A series does not
        /// choose: band <c>k</c> is hour <c>k</c>, drizzle included, which is what the archive says happened
        /// and what ELMFIRE should be integrating hour by hour. The trade is real — the run is no longer
        /// uniformly at the diurnal minimum, so it is less conservative — and the minimum is therefore still
        /// computed and logged beside the series mean, so a case whose series looks wet can be compared
        /// against the old figure rather than leaving the change invisible.
        /// </remarks>
        private static void NelsonMoisture(Options o, Result result, List<DateTime> bandTimes,
            List<HourlyWeatherRow> rows, AnnualMaximaDay day, Action<string> log)
        {
            int[,] elevation = ReadTerrain(o, "dem.tif", 0, 9000, required: true);
            int[,] slope = ReadTerrain(o, "slp.tif", 0, 90, required: true);
            int[,] aspect = ReadTerrain(o, "asp.tif", 0, 360, required: true);
            //Canopy cover only shades the sticks; a domain without it is treated as fully open
            //rather than refusing to run.
            int[,] canopy = ReadTerrain(o, "cc.tif", 0, 100, required: false)
                            ?? new int[o.Grid.Header.Ncols, o.Grid.Header.Nrows];

            DateTime burnStart = day.Date.Date.AddHours(o.BurningPeriodStartHour);
            DateTime burnEnd = day.Date.Date.AddHours(o.BurningPeriodEndHour);

            //The march has to reach the last band, which the burning period does not necessarily contain: the
            //fire can start before 10:00 or run past 18:00, and a multi-day run leaves that window entirely.
            //It also has to start conditioning before the earliest thing being sampled.
            DateTime firstSample = bandTimes.Count > 0 && bandTimes[0] < burnStart ? bandTimes[0] : burnStart;
            DateTime lastSample = bandTimes.Count > 0 && bandTimes[bandTimes.Count - 1] > burnEnd
                                  ? bandTimes[bandTimes.Count - 1] : burnEnd;

            DateTime start = firstSample.AddDays(-o.ConditioningDays);
            DateTime end = lastSample;

            List<HourlyWeatherRow> window = rows.Where(r => r.Time >= start && r.Time <= end)
                                                .OrderBy(r => r.Time)
                                                .ToList();

            if (window.Count < 24)
            {
                throw new Exception($"only {window.Count} hourly rows in the {o.ConditioningDays}-day conditioning window");
            }

            //Which bands each archive hour feeds. A list per hour rather than one band, because a band
            //interval shorter than an hour puts several bands on the same row.
            var bandsAtHour = new Dictionary<DateTime, List<int>>();
            for (int b = 0; b < bandTimes.Count; ++b)
            {
                DateTime t = bandTimes[b];
                DateTime hour = new DateTime(t.Year, t.Month, t.Day, t.Hour, 0, 0);
                if (t.Minute >= 30) hour = hour.AddHours(1);

                if (!bandsAtHour.TryGetValue(hour, out List<int> list))
                {
                    list = new List<int>();
                    bandsAtHour[hour] = list;
                }
                list.Add(b);
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

            //[class][band]. Per class rather than per cell for the same reason the sticks are, so the series
            //costs classes x bands rather than cells x bands - on the Mati domain that is the difference
            //between a few hundred kilobytes and gigabytes.
            int bandCount = bandTimes.Count;
            var band1 = new double[classes.Count][];
            var band10 = new double[classes.Count][];
            var band100 = new double[classes.Count][];

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

                //Seeded with -1 so a band the march never reaches is recognisable below rather than
                //silently reading as bone-dry fuel.
                band1[c] = new double[bandCount];
                band10[c] = new double[bandCount];
                band100[c] = new double[bandCount];
                for (int b = 0; b < bandCount; ++b) band1[c][b] = band10[c][b] = band100[c][b] = -1.0;
            }

            double cumulativeRainCm = 0.0;
            int sampled = 0;
            int bandsFilled = 0;

            foreach (HourlyWeatherRow r in window)
            {
                cumulativeRainCm += r.Precipitation * 0.1; //mm -> cm, and the engine wants it cumulative
                double rh = Fraction(r.RelativeHumidity);
                bool inBurningPeriod = r.Time >= burnStart && r.Time <= burnEnd;
                if (inBurningPeriod) ++sampled;

                DateTime thisHour = new DateTime(r.Time.Year, r.Time.Month, r.Time.Day, r.Time.Hour, 0, 0);
                List<int> hereBands = bandsAtHour.TryGetValue(thisHour, out List<int> found) ? found : null;
                if (hereBands != null) bandsFilled += hereBands.Count;

                //Classes are independent sticks, so the hour's integration parallelises cleanly.
                System.Threading.Tasks.Parallel.For(0, classes.Count, c =>
                {
                    TerrainClass t = classes.Items[c];
                    sticks[c].UpdateDateTime(
                        r.Time.Year, r.Time.Month, r.Time.Day, r.Time.Hour, 0, 0,
                        at: r.Temperature, rh: rh,
                        sW: TerrainSolar(o.LatLon, r, t),
                        rcum: cumulativeRainCm);

                    if (!inBurningPeriod && hereBands == null) return;

                    sticks[c].GetMoisture(out double v1, out double v10, out double v100, out double _);

                    //Kept for the log line, so the change from a single diurnal minimum to a series can be
                    //compared against what this case used to be given.
                    if (inBurningPeriod)
                    {
                        if (v1 >= 0 && v1 < min1[c]) min1[c] = v1;
                        if (v10 >= 0 && v10 < min10[c]) min10[c] = v10;
                        if (v100 >= 0 && v100 < min100[c]) min100[c] = v100;
                    }

                    if (hereBands == null) return;

                    foreach (int b in hereBands)
                    {
                        if (v1 >= 0) band1[c][b] = v1;
                        if (v10 >= 0) band10[c][b] = v10;
                        if (v100 >= 0) band100[c][b] = v100;
                    }
                });
            }

            if (sampled == 0)
            {
                throw new Exception("the conditioning window ended before the burning period began");
            }

            //A band the archive did not reach takes the last one that was filled, so the series keeps its
            //length. Done per class after the march rather than during it, because the gap can be anywhere.
            int unfilled = FillBandGaps(band1, band10, band100, bandCount, o);

            int nx = o.Grid.Header.Ncols;
            int ny = o.Grid.Header.Nrows;

            var series1 = new List<float[,]>(bandCount);
            var series10 = new List<float[,]>(bandCount);
            var series100 = new List<float[,]>(bandCount);

            double s1 = 0, s10 = 0, s100 = 0;

            for (int b = 0; b < bandCount; ++b)
            {
                var m1 = new float[nx, ny];
                var m10 = new float[nx, ny];
                var m100 = new float[nx, ny];

                for (int x = 0; x < nx; ++x)
                {
                    for (int y = 0; y < ny; ++y)
                    {
                        int c = classes.IndexOf[x, y];
                        m1[x, y] = (float)(band1[c][b] * FractionToPercent);
                        m10[x, y] = (float)(band10[c][b] * FractionToPercent);
                        m100[x, y] = (float)(band100[c][b] * FractionToPercent);
                        s1 += m1[x, y]; s10 += m10[x, y]; s100 += m100[x, y];
                    }
                }

                series1.Add(m1);
                series10.Add(m10);
                series100.Add(m100);
            }

            GeoTiffRasterWriter.WriteBands(o.Grid, series1, Path.Combine(o.InputsDirectory, "m1.tif"));
            GeoTiffRasterWriter.WriteBands(o.Grid, series10, Path.Combine(o.InputsDirectory, "m10.tif"));
            GeoTiffRasterWriter.WriteBands(o.Grid, series100, Path.Combine(o.InputsDirectory, "m100.tif"));

            long samples = (long)nx * ny * bandCount;
            result.MeanM1Percent = s1 / samples;
            result.MeanM10Percent = s10 / samples;
            result.MeanM100Percent = s100 / samples;

            //The old single-value figure, reported beside the series so the change is visible rather than
            //implicit: this is what the whole case used to be given for every hour.
            double dm1 = 0, dm10 = 0, dm100 = 0;
            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y)
                {
                    int c = classes.IndexOf[x, y];
                    dm1 += min1[c]; dm10 += min10[c]; dm100 += min100[c];
                }
            }
            long cells = (long)nx * ny;

            log($"    Nelson: {bandCount} bands, mean dead moisture {result.MeanM1Percent:F1}/" +
                $"{result.MeanM10Percent:F1}/{result.MeanM100Percent:F1} % (1/10/100 h) across the series; " +
                $"the diurnal minimum over {sampled} burning-period hours would have been " +
                $"{dm1 / cells * FractionToPercent:F1}/{dm10 / cells * FractionToPercent:F1}/" +
                $"{dm100 / cells * FractionToPercent:F1} %." +
                (unfilled > 0 ? $" {unfilled} band(s) were beyond the archive and held the previous hour." : ""));
        }

        /// <summary>
        /// Fills bands the march never reached, so every class has a value for every band.
        /// </summary>
        /// <remarks>
        /// Carries the previous filled band forward, and for a leading gap carries the first filled one
        /// backward. Returns how many bands needed it, which is reported rather than swallowed: a series
        /// mostly made of held-over values is a case whose archive does not cover its run, and that is worth
        /// knowing. Falls back to the configured uniform values only if a class was never sampled at all.
        /// </remarks>
        private static int FillBandGaps(double[][] band1, double[][] band10, double[][] band100, int bandCount, Options o)
        {
            int unfilledBands = 0;

            for (int b = 0; b < bandCount; ++b)
            {
                //A gap is the same for every class, since it comes from the archive rather than the terrain,
                //so band 0 of class 0 is a faithful probe for whether this band was reached.
                if (band1.Length > 0 && band1[0][b] >= 0) continue;
                ++unfilledBands;
            }

            for (int c = 0; c < band1.Length; ++c)
            {
                Carry(band1[c], o.FallbackM1Percent / FractionToPercent);
                Carry(band10[c], o.FallbackM10Percent / FractionToPercent);
                Carry(band100[c], o.FallbackM100Percent / FractionToPercent);
            }

            return unfilledBands;

            void Carry(double[] series, double lastResort)
            {
                double previous = -1.0;
                for (int b = 0; b < series.Length; ++b)
                {
                    if (series[b] >= 0) { previous = series[b]; continue; }
                    series[b] = previous;
                }

                //A leading gap has nothing behind it, so the first real value is carried backward instead.
                double firstReal = -1.0;
                for (int b = 0; b < series.Length; ++b)
                {
                    if (series[b] >= 0) { firstReal = series[b]; break; }
                }

                for (int b = 0; b < series.Length; ++b)
                {
                    if (series[b] < 0) series[b] = firstReal >= 0 ? firstReal : lastResort;
                }
            }
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
