using System.Globalization;
using PREACT.Input;
using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>
    /// The weather clock and the fire weather codes: which archive hour a case band reads, and the FWI system's
    /// season and day length, for the places the platform is meant for - Mati (UTC+2, +3 in summer), California
    /// (UTC-8, -7 in summer) and the southern hemisphere.
    /// </summary>
    internal static class WeatherTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("time: a scenario's zone, its daylight saving and its gaps, from the domain's coordinates", ScenarioZone);
            runner.Add("weather: a case's bands start at the scenario's local hour, read at the archive's UTC hour", BandsStartAtLocalHour);
        }

        private static readonly (string name, double lat, double lon) Mati = ("Mati", 38.03, 23.99);
        private static readonly (string name, double lat, double lon) California = ("Santa Rosa, California", 38.44, -122.71);
        private static readonly (string name, double lat, double lon) Sydney = ("Sydney", -33.87, 151.21);

        private static DateTime D(string text) => DateTime.Parse(text, CultureInfo.InvariantCulture);

        private static void ScenarioZone()
        {
            TimeZoneInfo athens = LocalTime.ZoneAt(Mati.lat, Mati.lon);
            Assert.Equal(D("2026-06-28T10:00"), LocalTime.ToUtc(D("2026-06-28T13:00"), athens), "Mati in summer is UTC+3");
            Assert.Equal(D("2026-01-15T11:00"), LocalTime.ToUtc(D("2026-01-15T13:00"), athens), "and UTC+2 in winter");

            TimeZoneInfo pacific = LocalTime.ZoneAt(California.lat, California.lon);
            Assert.Equal(D("2017-10-09T20:00"), LocalTime.ToUtc(D("2017-10-09T13:00"), pacific), "California in October is UTC-7");
            Assert.Equal(D("2017-12-10T21:00"), LocalTime.ToUtc(D("2017-12-10T13:00"), pacific), "and UTC-8 in December");
            Assert.Equal(D("2017-10-09T13:00"), LocalTime.FromUtc(D("2017-10-09T20:00"), pacific), "back again");

            //The spring-forward gap (02:30 does not exist on 2017-03-12) and the autumn hour that happens twice.
            Assert.Equal(D("2017-03-12T10:30"), LocalTime.ToUtc(D("2017-03-12T02:30"), pacific), "a time in the gap is taken an hour on");
            Assert.Equal(D("2017-11-05T08:30"), LocalTime.ToUtc(D("2017-11-05T01:30"), pacific), "an ambiguous time is its first occurrence");

            TimeZoneInfo sydney = LocalTime.ZoneAt(Sydney.lat, Sydney.lon);
            Assert.Equal(D("2019-12-30T02:00"), LocalTime.ToUtc(D("2019-12-30T13:00"), sydney), "Sydney in its summer is UTC+11");

            //The simulation clock reads the same zone.
            using var s = new FormatTests.Scenario();
            var lines = FormatTests.Replace("LowerLeftLatLon", California.lat.ToString("R", CultureInfo.InvariantCulture) + ","
                                                               + California.lon.ToString("R", CultureInfo.InvariantCulture));
            var simulation = new Simulation(Program.Engine, PREACTInput.LoadFromLines(lines.ToArray(), s.Folder, out bool _), 0);
            Assert.Equal(D("2026-06-28T19:00"), simulation.Time.StartUTCDateTime, "the run's 12:00 start is 19:00 UTC in California");
        }

        /// <summary>
        /// docs.md 3.3: the band schedule added the scenario's local start hour to the drawn day's UTC date, so band 1
        /// read the record at that hour UTC - 16:00 local at Mati in summer, 06:00 in California for a 13:00 start.
        /// </summary>
        private static void BandsStartAtLocalHour()
        {
            //The drawn day is the archive's fire-weather noon row: 12:00 local standard time from the longitude, in UTC.
            (string where, (string name, double lat, double lon) place, string noonRowUtc, string firstBandUtc)[] cases =
            {
                ("Mati, August", Mati, "2007-08-25T10:00", "2007-08-25T10:00"),
                ("Mati, January", Mati, "2008-01-15T10:00", "2008-01-15T11:00"),
                ("California, October", California, "2017-10-09T20:00", "2017-10-09T20:00"),
                ("California, December", California, "2017-12-10T20:00", "2017-12-10T21:00"),
                ("Sydney, December", Sydney, "2019-12-30T02:00", "2019-12-30T02:00"),
            };

            foreach ((string where, (string name, double lat, double lon) place, string noon, string first) in cases)
            {
                Assert.Equal(D(noon).Hour, ClimatologySampler.FwiNoonUtcHour(place.lon), where + ": test setup, the noon row's UTC hour");
                var o = new WeatherRasterPipeline.Options
                {
                    LatLon = new Math.Vector2d(place.lat, place.lon),
                    SimulationStartDateTime = D("2026-06-28T13:00"),
                    SimulationTstopSeconds = 3 * 3600.0,
                    SecondsPerBand = 3600.0,
                    MaxBands = 0,
                    Sampling = WeatherRasterPipeline.SamplingMode.HistoricalDay,
                };
                var log = new List<string>();
                List<DateTime> bands = WeatherRasterPipeline.BuildBandSchedule(o, new AnnualMaximaDay { Date = D(noon) }, new List<HourlyWeatherRow>(), log.Add);

                Assert.Equal(3, bands.Count, where + ": bands");
                Assert.Equal(D(first), bands[0], where + ": band 1 is 13:00 local on the drawn day, in UTC");
                Assert.Equal(D(first).AddHours(2), bands[2], where + ": and the series walks on hour by hour");
                Assert.True(log.Any(l => l.Contains(D(first).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC (13:00 local")),
                    where + ": the log says both clocks: " + string.Join(" | ", log));
            }

            //A zone the caller gives wins over the one at LatLon (the builder passes the scenario's, at its corner).
            var given = new WeatherRasterPipeline.Options
            {
                LatLon = new Math.Vector2d(Mati.lat, Mati.lon),
                StartTimeZone = LocalTime.ZoneAt(California.lat, California.lon),
                SimulationStartDateTime = D("2026-06-28T13:00"),
                SimulationTstopSeconds = 3600.0,
                MaxBands = 0,
            };
            List<DateTime> one = WeatherRasterPipeline.BuildBandSchedule(given, new AnnualMaximaDay { Date = D("2017-10-09T10:00") },
                new List<HourlyWeatherRow>(), _ => { });
            Assert.Equal(D("2017-10-09T20:00"), one[0], "the given zone is used");
        }
    }
}
