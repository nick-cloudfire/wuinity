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
            runner.Add("weather: the sun Nelson's terrain factor uses peaks at local solar noon, at Mati and in California", SunAtLocalNoon);
            runner.Add("weather: WindNinja_cli is found on PATH and in an install root under this platform's name", WindNinjaFound);
            runner.Add("fwi: derived all year; northern codes unchanged, southern day lengths, a southern year peaks in its summer", FwiAllYear);
            runner.Add("fwi: Mati's real archive keeps every annual peak and pool day without the old season cut (data permitting)", FwiMatiUnchanged);
        }

        /// <summary>
        /// docs.md 3.12: the probe looked for WindNinja_cli.exe alone, so on Linux only WINDNINJA_CLI or an explicit
        /// path found WindNinja, and the build wrote a uniform wind field.
        /// </summary>
        private static void WindNinjaFound()
        {
            string[] names = WindNinjaRunner.ExecutableNames;
            Assert.Equal(OperatingSystem.IsWindows() ? "WindNinja_cli.exe" : "WindNinja_cli", names[0], "the solver's name on this platform");

            string folder = Directory.CreateTempSubdirectory("preact-windninja-").FullName;
            try
            {
                string bin = Path.Combine(folder, "bin");
                Directory.CreateDirectory(bin);
                string onPath = Path.Combine(bin, names[0]);
                File.WriteAllText(onPath, "");
                string path = string.Join(Path.PathSeparator.ToString(), "/nonexistent", bin);
                Assert.Equal(onPath, WindNinjaRunner.FindExecutable(null, path, new string[0], names), "found on PATH");

                string root = Path.Combine(folder, "WindNinja");
                foreach (string version in new[] { "WindNinja-3.11.0", "WindNinja-3.12.1" })
                {
                    Directory.CreateDirectory(Path.Combine(root, version, "bin"));
                    File.WriteAllText(Path.Combine(root, version, "bin", names[0]), "");
                }
                Assert.Equal(Path.Combine(root, "WindNinja-3.12.1", "bin", names[0]),
                    WindNinjaRunner.FindExecutable(null, "", new[] { root }, names), "the newest install under a root");
                Assert.Equal(onPath, WindNinjaRunner.FindExecutable(onPath, "", new string[0], names), "WINDNINJA_CLI first");
                Assert.True(WindNinjaRunner.FindExecutable(null, "", new string[0], names) == null, "nothing when there is nothing");
                Assert.True(WindNinjaRunner.SearchDescription.StartsWith("WINDNINJA_CLI, PATH"), "the message says where it looked");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        /// <summary>One year of synthetic hourly weather, hottest and driest around <paramref name="peakDay"/>, wet in the opposite season.</summary>
        private static List<ClimatologySampler.RawHour> SyntheticYear(int year, int peakDay)
        {
            var hours = new List<ClimatologySampler.RawHour>();
            for (DateTime t = new DateTime(year, 1, 1); t.Year == year; t = t.AddHours(1))
            {
                double season = System.Math.Cos(2.0 * System.Math.PI * (t.DayOfYear - peakDay) / 365.0); //1 at the peak
                double diurnal = System.Math.Sin(2.0 * System.Math.PI * (t.Hour - 8) / 24.0);
                bool wet = season < -0.3 && t.DayOfYear % 5 == 0 && t.Hour == 6;
                hours.Add(new ClimatologySampler.RawHour
                {
                    Time = t,
                    Temperature = 18 + 10 * season + 5 * diurnal,
                    RelativeHumidity = System.Math.Max(10, 55 - 25 * season - 10 * diurnal),
                    Precipitation = wet ? 6.0 : 0.0,
                    WindSpeedMps = 5.5,
                });
            }
            return hours;
        }

        /// <summary>
        /// docs.md 3.4: FireWeatherIndex zeroed the FWI for January and October to December (its comment said November
        /// to February), a northern fire season, and its day lengths were the 46 N ones everywhere, so a southern
        /// hemisphere case's peak days were never drawn.
        /// </summary>
        private static void FwiAllYear()
        {
            //North of the equator nothing changes where it was in season: the same march with and without a latitude.
            List<ClimatologySampler.RawHour> north = SyntheticYear(2021, 200);
            ClimatologySampler.DerivedCodes[] plain = ClimatologySampler.DeriveFireWeatherCodes(north, double.NaN, Mati.lon);
            ClimatologySampler.DerivedCodes[] at38 = ClimatologySampler.DeriveFireWeatherCodes(north, Mati.lat, Mati.lon);
            for (int i = 0; i < north.Count; ++i)
            {
                Assert.True(plain[i].Fwi == at38[i].Fwi && plain[i].Dc == at38[i].Dc && plain[i].Dmc == at38[i].Dmc,
                    $"northern codes are the standard ones at {north[i].Time:MM-dd HH}:00");
            }
            int december = north.FindIndex(h => h.Time == new DateTime(2021, 12, 15, 10, 0, 0));
            Assert.True(at38[december].Fwi > 0.0, $"a northern December day has its index ({at38[december].Fwi:F2}); it was set to 0");

            //South of the equator the drying follows the southern sun.
            Assert.Equal(11.5, Wildfire.FireWeatherIndex.DmcDayLength(1, -34.0), "DMC day length, January at 34 S");
            Assert.Equal(6.5, Wildfire.FireWeatherIndex.DmcDayLength(1, 38.0), "and at 38 N");
            Assert.Equal(10.1, Wildfire.FireWeatherIndex.DmcDayLength(1, -20.0), "between 10 and 30 S");
            Assert.Equal(9.0, Wildfire.FireWeatherIndex.DmcDayLength(7, -5.0), "near the equator");
            Assert.Equal(6.4, Wildfire.FireWeatherIndex.DcDayLength(1, -34.0), "DC day length, January at 34 S");
            Assert.Equal(-1.6, Wildfire.FireWeatherIndex.DcDayLength(1, 38.0), "and at 38 N");
            Assert.Equal(1.4, Wildfire.FireWeatherIndex.DcDayLength(1, -10.0), "near the equator");

            //A Sydney year, hot and dry in January: its worst day is in its summer, not in the northern one.
            List<ClimatologySampler.RawHour> south = SyntheticYear(2019, 15);
            ClimatologySampler.DerivedCodes[] codes = ClimatologySampler.DeriveFireWeatherCodes(south, Sydney.lat, Sydney.lon);
            int noon = ClimatologySampler.FwiNoonUtcHour(Sydney.lon);
            var rows = south.Select((h, i) => new HourlyWeatherRow
            {
                Time = h.Time, Temperature = h.Temperature, RelativeHumidity = h.RelativeHumidity, WindSpeed = h.WindSpeedMps,
                Fwi = codes[i].Fwi, Dc = codes[i].Dc, IsFwiNoon = h.Time.Hour == noon,
            }).ToList();
            AnnualMaximaDay peak = ClimatologySampler.BuildAnnualMaxima(rows).Single();
            Assert.True(peak.Date.Month <= 3 || peak.Date.Month == 12, $"Sydney's peak FWI day is in its summer: {peak.Date:yyyy-MM-dd} (FWI {peak.Fwi:F1})");
            List<AnnualMaximaDay> pool = ClimatologySampler.BuildCandidatePool(rows, 10);
            Assert.True(pool.All(d => d.Date.Month <= 4 || d.Date.Month >= 11), "and so is its whole pool of ten: " + string.Join(",", pool.Select(d => d.Date.ToString("MM-dd"))));
            //January dries the duff and the deep fuel faster with the southern day lengths than the northern ones did.
            ClimatologySampler.DerivedCodes[] asNorth = ClimatologySampler.DeriveFireWeatherCodes(south, double.NaN, Sydney.lon);
            int endOfJanuary = south.FindIndex(h => h.Time == new DateTime(2019, 1, 31, noon, 0, 0));
            Assert.True(codes[endOfJanuary].Dc > asNorth[endOfJanuary].Dc + 50 && codes[endOfJanuary].Dmc > asNorth[endOfJanuary].Dmc,
                $"by 31 January DC {codes[endOfJanuary].Dc:F0} and DMC {codes[endOfJanuary].Dmc:F0} with the southern day lengths, "
                + $"{asNorth[endOfJanuary].Dc:F0} and {asNorth[endOfJanuary].Dmc:F0} with the northern ones");
        }

        /// <summary>
        /// Removing the cut instead of moving it is safe for the pipeline only if the northern draw does not change:
        /// on Mati's 26-year ERA5 archive, every annual maximum and every day of the 10-a-year pool is the same with
        /// the Oct-Jan index zeroed (the old rule) and without.
        /// </summary>
        private static List<string> FwiMatiUnchanged()
        {
            var warnings = new List<string>();
            string archive = Environment.GetEnvironmentVariable("PREACT_TEST_ERA5_ARCHIVE")
                             ?? "/home/claude/cases/mati_generated/elmfire/climatology/mati_era5_hourly.csv";
            if (!File.Exists(archive))
            {
                warnings.Add("no real ERA5 archive on this machine (set PREACT_TEST_ERA5_ARCHIVE=<csv>); skipped");
                return warnings;
            }

            string folder = Directory.CreateTempSubdirectory("preact-fwi-").FullName;
            try
            {
                string copy = Path.Combine(folder, Path.GetFileName(archive));
                File.Copy(archive, copy);
                Assert.True(ClimatologySampler.EnsureArchiveFormat(copy, null), "the copy is brought to the current format");
                List<HourlyWeatherRow> rows = ClimatologySampler.ParseOpenMeteoCsv(copy);
                List<HourlyWeatherRow> cut = rows.Select(r =>
                {
                    if (r.Time.Month < 2 || r.Time.Month > 9) r.Fwi = 0.0;
                    return r;
                }).ToList();

                List<AnnualMaximaDay> now = ClimatologySampler.BuildAnnualMaxima(rows), before = ClimatologySampler.BuildAnnualMaxima(cut);
                Assert.Equal(before.Count, now.Count, "annual maxima");
                Assert.True(before.Select(d => d.Date).SequenceEqual(now.Select(d => d.Date)), "the same peak day every year");
                List<DateTime> poolNow = ClimatologySampler.BuildCandidatePool(rows, 10).Select(d => d.Date).ToList();
                List<DateTime> poolBefore = ClimatologySampler.BuildCandidatePool(cut, 10).Select(d => d.Date).ToList();
                Assert.True(poolBefore.SequenceEqual(poolNow), "the same 10-a-year pool");
                Console.WriteLine($"  INFO {Path.GetFileName(archive)}: {now.Count} annual peaks and {poolNow.Count} pool days, "
                                  + "unchanged without the Oct-Jan cut");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
            return warnings;
        }

        /// <summary>
        /// SunRadiation.SimpleRadiation passed east-positive longitude to a library that counts west as positive, so the
        /// sun stood at the mirrored longitude: Mati's clear-sky peak came at 14:00 UTC instead of about 10:20, and in
        /// California the sun was below the horizon at local noon, so Nelson's sticks got no terrain factor in daylight.
        /// </summary>
        private static void SunAtLocalNoon()
        {
            foreach ((string name, double lat, double lon) place in new[] { Mati, California })
            {
                int peak = Enumerable.Range(0, 24).OrderByDescending(h => Weather.SunRadiation.SimpleRadiation(place.lat, place.lon, 172, h * 100, 0, 0, 0, 0, 0)).First();
                int solarNoon = (int)System.Math.Round(12.0 - place.lon / 15.0 + 24.0) % 24;
                Assert.True(System.Math.Abs(peak - solarNoon) <= 1, $"{place.name}: the flat-ground peak is at {peak}:00 UTC, solar noon about {solarNoon}:00 UTC");
                double south = Weather.SunRadiation.SimpleRadiation(place.lat, place.lon, 172, solarNoon * 100, 0, 0, 30, 180, 0);
                double north = Weather.SunRadiation.SimpleRadiation(place.lat, place.lon, 172, solarNoon * 100, 0, 0, 30, 0, 0);
                Assert.True(south > north && north > 0, $"{place.name}: at noon a south-facing slope gets more ({south:F0}) than a north-facing one ({north:F0})");
                Assert.Equal(0.0, Weather.SunRadiation.SimpleRadiation(place.lat, place.lon, 172, ((solarNoon + 12) % 24) * 100, 0, 0, 0, 0, 0), place.name + ": midnight is dark");
            }
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
