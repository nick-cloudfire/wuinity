using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using PREACT.Input;
using PREACT.Utility;
using PREACT.Wildfire;

namespace PREACT.Tests
{
    /// <summary>
    /// The seams between the packages that can be checked head-less: stopping ELMFIRE (contract C3), a run
    /// leaving its scenario alone (C4), the one fire-weather derivation, and how an imported fire finds and
    /// measures its rasters.
    /// </summary>
    internal static class IntegrationTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("seam: Engine.CloseSimulations kills a running ELMFIRE, and its run returns Cancelled", StopKillsElmfire);
            runner.Add("seam: a process that starts after a cancel is killed as it registers", LateRegistrationIsKilled);
            runner.Add("seam: a run leaves the scenario exactly as it was loaded (C4)", RunLeavesScenarioAlone);
            runner.Add("seam: the run's fire weather codes are the archive's derivation, at local noon", FireWeatherCodes);
            runner.Add("seam: an imported fire resolves backslash paths and keeps rectangular cells", ImportedFireGrid);
            runner.Add("seam: a new scenario's response curve survives its first save (no destinations or groups yet)", NewScenarioKeepsCurve);
            runner.Add("seam: the campaign's weather report reads each day's codes at its local noon, not 12 UTC", WeatherReportLocalNoon);
        }

        /// <summary>
        /// Review MI-6: the distributions report took each pool day's FFMC/DMC/DC/ISI/BUI from its 12:00 UTC row, which
        /// west of Greenwich is before local noon - for all of CONUS the previous day's codes. It matches on the FWI noon
        /// the pool was built from (fixed at integration, 5f21346e); this keeps it so.
        /// </summary>
        private static void WeatherReportLocalNoon()
        {
            var day = new DateTime(2021, 8, 10);
            var rows = new List<HourlyWeatherRow>
            {
                new HourlyWeatherRow { Time = day.AddHours(12), Dc = 100, Dmc = 10, IsFwiNoon = false },
                new HourlyWeatherRow { Time = day.AddHours(20), Dc = 500, Dmc = 50, IsFwiNoon = true }, //noon LST at 120 W
            };
            var pool = new List<AnnualMaximaDay>
            {
                new AnnualMaximaDay { Year = 2021, Date = day, Temperature = 35, RelativeHumidity = 12, WindSpeed = 8, WindDirection = 270, Fwi = 60 },
            };

            List<WeatherStatisticsReport.VariableStatistics> stats = WeatherStatisticsReport.Compute(pool, rows, null, 1.0, 2.0);
            WeatherStatisticsReport.VariableStatistics dc = stats.Single(v => v.Name == "dc");
            Assert.Near(500.0, dc.Mean, 1e-9, "DC from the day's local-noon row");
            Assert.True(stats.Any(v => v.Name == "wind_speed_10m") && !stats.Any(v => v.Name == "wind_speed_20ft"),
                "the derived wind is labelled as the 10 m wind it is");
        }

        private static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>A stand-in for ELMFIRE that just waits a minute.</summary>
        private static string FakeElmfire(string dir)
        {
            if (OperatingSystem.IsWindows())
            {
                string cmd = Path.Combine(dir, "elmfire.cmd");
                File.WriteAllText(cmd, "@ping -n 61 127.0.0.1 >nul\r\n");
                return cmd;
            }

            string sh = Path.Combine(dir, "elmfire");
            File.WriteAllText(sh, "#!/bin/sh\nsleep 60\n");
            File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return sh;
        }

        private static Process StartWaiter()
        {
            var psi = Windows
                ? new ProcessStartInfo("ping", "-n 61 127.0.0.1")
                : new ProcessStartInfo("sleep", "60");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.CreateNoWindow = true;
            return Process.Start(psi);
        }

        private static void StopKillsElmfire()
        {
            string dir = Directory.CreateTempSubdirectory("preact-c3-").FullName;
            try
            {
                string exe = FakeElmfire(dir);
                ElmfireRunner.Result result = null;
                Task run = Task.Run(() => result = ElmfireRunner.Run(exe, dir, "test", new[] { "&MISC", "/" }, false, null));

                var wait = Stopwatch.StartNew();
                while (ElmfireProcesses.Count == 0 && wait.Elapsed.TotalSeconds < 10 && !run.IsCompleted)
                {
                    Thread.Sleep(50);
                }
                Assert.True(ElmfireProcesses.Count > 0, "the fake ELMFIRE started and registered");

                var stop = Stopwatch.StartNew();
                Program.Engine.CloseSimulations(false);
                Assert.True(run.Wait(15000), "the run returned after the stop");
                Assert.True(stop.Elapsed.TotalSeconds < 10, $"within seconds, not the minute ELMFIRE would take ({stop.Elapsed.TotalSeconds:F1} s)");
                Assert.True(result != null && result.Cancelled && !result.Ok, "the result says Cancelled: " + result?.Message);
                Assert.Equal(0, ElmfireProcesses.Count, "no ELMFIRE left running");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void LateRegistrationIsKilled()
        {
            long before = ElmfireProcesses.Generation;
            ElmfireProcesses.KillAll(); //the cancel, before the process exists
            using (Process p = StartWaiter())
            {
                bool kept = ElmfireProcesses.Register(p, before);
                try
                {
                    Assert.True(!kept, "Register reports that it killed the process");
                    Assert.True(p.WaitForExit(10000), "and it is gone");
                }
                finally
                {
                    ElmfireProcesses.Unregister(p);
                    try { if (!p.HasExited) p.Kill(true); } catch { }
                }
            }

            //A process for work started after the cancel is left alone.
            long now = ElmfireProcesses.Generation;
            using (Process q = StartWaiter())
            {
                try
                {
                    Assert.True(ElmfireProcesses.Register(q, now), "a process after the cancel is kept");
                    Assert.True(!q.HasExited, "and runs");
                }
                finally
                {
                    ElmfireProcesses.Unregister(q);
                    try { q.Kill(true); q.WaitForExit(5000); } catch { }
                }
            }
        }

        /// <summary>
        /// Hourly weather from 2026-06-27 00:00 to 2026-06-30 23:00 UTC at longitude -120 (local noon is 20:00
        /// UTC), with a daily temperature cycle and rain on the 28th's early hours.
        /// </summary>
        private static string WriteWeather(string folder)
        {
            var lines = new List<string>
            {
                "Latitide,38.0", "Longitude,-120.0", "Elevation,100",
                "Time,Temperature_2m [°C],Relativehumidity_2m [%],Precipitation [mm],Windspeed_10m [m/s],"
                + "Winddirection_10m [°],Cloudcover [%],Direct_radiation [W/m²],Boundary_layer_height [m]",
            };
            var start = new DateTime(2026, 6, 27, 0, 0, 0);
            for (int h = 0; h < 96; ++h)
            {
                DateTime t = start.AddHours(h);
                double temp = 22 + 10 * System.Math.Sin((t.Hour - 14) / 24.0 * 2 * System.Math.PI + System.Math.PI / 2);
                double rh = 60 - 30 * System.Math.Sin((t.Hour - 14) / 24.0 * 2 * System.Math.PI + System.Math.PI / 2);
                double rain = t.Day == 28 && t.Hour >= 2 && t.Hour <= 5 ? 1.5 : 0.0;
                lines.Add(string.Join(",", t.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
                    temp.ToString("R", CultureInfo.InvariantCulture), rh.ToString("R", CultureInfo.InvariantCulture),
                    rain.ToString("R", CultureInfo.InvariantCulture), "6", "200", "0", "500", "1000"));
            }
            string path = Path.Combine(folder, "weather.csv");
            File.WriteAllLines(path, lines);
            return path;
        }

        private static List<string> WithWeather(List<string> lines)
        {
            lines.AddRange(new[] { "", "[Weather]", "WeatherFile=weather.csv", "StartFFMC=80", "StartDMC=10", "StartDC=40" });
            return lines;
        }

        private static void RunLeavesScenarioAlone()
        {
            using var s = new FormatTests.Scenario();
            WriteWeather(s.Folder);
            PREACTInput probe = s.Load(FormatTests.Scenario.Lines, out bool _);
            PREACTInput input = EngineTests.Load(s, WithWeather(EngineTests.FireLines(s, probe, true)));
            string before = string.Join("\n", PREACTInputWriter.Write(input));

            Program.Engine.SetInput(input, Path.Combine(s.Folder, "scenario.wui"));
            Program.Engine.RunSimulations(new EngineTask(1)).GetAwaiter().GetResult();
            Assert.True(Program.Engine.Simulation.State == Simulation.SimulationState.Completed,
                "the run completed: " + Program.Engine.Simulation.State);

            string after = string.Join("\n", PREACTInputWriter.Write(input));
            if (before != after)
            {
                var added = after.Split('\n').Except(before.Split('\n')).Take(5);
                throw new TestFailure("the run changed the scenario it ran: " + string.Join("; ", added));
            }
        }

        private static void FireWeatherCodes()
        {
            using var s = new FormatTests.Scenario();
            string weatherPath = WriteWeather(s.Folder);
            var lines = WithWeather(FormatTests.Replace("EndDateTime", "2026-06-29T14:00:00"));
            PREACTInput input = EngineTests.Load(s, lines);
            var simulation = new Simulation(Program.Engine, input, 0);
            Assert.True(simulation.Weather.HasFireWeatherCodes, "the run has fire weather codes");

            //What the archive derivation gives for the same hours and seeds.
            WeatherInput seeds = input.Weather;
            var raw = new List<ClimatologySampler.RawHour>();
            string[] rows = File.ReadAllLines(weatherPath);
            DateTime first = input.Simulation.StartDateTime, last = input.Simulation.EndDateTime;
            for (int i = 4; i < rows.Length; ++i)
            {
                string[] c = rows[i].Split(',');
                DateTime t = DateTime.Parse(c[0], CultureInfo.InvariantCulture);
                if (t < first || t > last) continue;
                raw.Add(new ClimatologySampler.RawHour
                {
                    Time = t,
                    Temperature = double.Parse(c[1], CultureInfo.InvariantCulture),
                    RelativeHumidity = double.Parse(c[2], CultureInfo.InvariantCulture),
                    Precipitation = double.Parse(c[3], CultureInfo.InvariantCulture),
                    WindSpeedMps = double.Parse(c[4], CultureInfo.InvariantCulture),
                });
            }
            ClimatologySampler.DerivedCodes[] expected = ClimatologySampler.DeriveFireWeatherCodes(raw, -120.0,
                new FireWeatherIndex(seeds.StartFFMC, seeds.StartDMC, seeds.StartDC), new HourlyFFMC(seeds.StartHourlyFFMC));

            var daily = new Dictionary<int, double>();
            for (int h = 0; h < raw.Count; ++h)
            {
                DateTime t = first.AddHours(h);
                simulation.Weather.Update(t);
                ClimatologySampler.DerivedCodes got = simulation.Weather.FireWeatherCodes;
                Assert.Near(expected[h].Ffmc, got.Ffmc, 1e-4, $"daily FFMC at {t:MM-dd HH}:00");
                Assert.Near(expected[h].Dc, got.Dc, 1e-4, $"DC at {t:MM-dd HH}:00");
                Assert.Near(expected[h].FfmcHourly, got.FfmcHourly, 1e-4, $"hourly FFMC at {t:MM-dd HH}:00");
                if (t.Day == 28) daily[t.Hour] = got.Ffmc;
            }

            //Local noon at -120 is 20:00 UTC: the daily codes hold from 12:00 to 19:00 and move at 20:00. The run
            //used to advance them at 12:00 of the simulation clock.
            Assert.Near(daily[12], daily[19], 1e-9, "the daily FFMC is unchanged from 12:00 to 19:00");
            Assert.True(System.Math.Abs(daily[20] - daily[19]) > 1e-6, "it moves at 20:00, local noon");
        }

        /// <summary>
        /// A scenario as the New scenario dialog writes it: curves and demographics, but no destinations or groups
        /// yet, and no [Evacuation] header (the writer omits it, since it holds nothing current). The parser used to
        /// return before reading the curves in that case, so the first save dropped them.
        /// </summary>
        private static void NewScenarioKeepsCurve()
        {
            using var s = new FormatTests.Scenario();
            var lines = new List<string>();
            string section = null;
            foreach (string line in FormatTests.Scenario.Lines)
            {
                if (line.StartsWith("[")) section = line;
                if (section == "[Destination]" || section == "[EvacuationGroup]") continue;
                lines.Add(line);
            }
            Assert.True(!lines.Contains("[Evacuation]"), "no [Evacuation] header, as the writer leaves it out");

            PREACTInput input = s.Load(lines, out bool _);
            Assert.Equal(1, input.Evacuation.ResponseCurves.Count, "the curve is read");
            Assert.True(PREACTInput.Requirements.Any(r => r.Critical && r.Key == "EvacuationGroup"),
                "what is missing is named: the groups");

            PREACTInput again = s.Load(PREACTInputWriter.Write(input), out bool _);
            Assert.Equal(1, again.Evacuation.ResponseCurves.Count, "and still there after a save and reload");
            Assert.Equal(1, again.Population.Demographics.Count, "as are the demographics");
        }

        private static void ImportedFireGrid()
        {
            using var s = new FormatTests.Scenario();
            PREACTInput probe = s.Load(FormatTests.Scenario.Lines, out bool _);
            PREACTInput input = EngineTests.Load(s, EngineTests.FireLines(s, probe, false));

            //The same fire on 20 x 40 cells of 100 x 50 m, written with dx/dy, in a subfolder named with a backslash.
            Math.Vector2d origin = input.Simulation.Data.UTMOrigin;
            string Grid(Func<double, double, double> value)
            {
                var text = new List<string>
                {
                    "ncols 20", "nrows 40",
                    "xllcorner " + origin.x.ToString("R", CultureInfo.InvariantCulture),
                    "yllcorner " + origin.y.ToString("R", CultureInfo.InvariantCulture),
                    "dx 100", "dy 50", "NODATA_value -9999",
                };
                for (int row = 0; row < 40; ++row)
                {
                    double y = (40 - row - 0.5) * 50.0;
                    text.Add(string.Join(" ", Enumerable.Range(0, 20).Select(col =>
                        value((col + 0.5) * 100.0, y).ToString("R", CultureInfo.InvariantCulture))));
                }
                return string.Join("\n", text) + "\n";
            }
            Directory.CreateDirectory(Path.Combine(s.Folder, "rect"));
            File.WriteAllText(Path.Combine(s.Folder, "rect", "toa.asc"), Grid((x, y) => System.Math.Sqrt((x - 1000) * (x - 1000) + (y - 1000) * (y - 1000)) / 0.5));
            File.WriteAllText(Path.Combine(s.Folder, "rect", "ros.asc"), Grid((x, y) => 0.5));
            File.WriteAllText(Path.Combine(s.Folder, "rect", "sd.asc"), Grid((x, y) => 225));

            AscImportInput asc = input.WildfireModule.AscImportInput.Clone();
            asc.TimeOfArrivalFile = "rect\\toa.asc";
            asc.RateOfSpreadFile = "rect\\ros.asc";
            asc.SpreadDirectionFile = "rect\\sd.asc";
            asc.FirelineIntensityFile = string.Empty;

            var simulation = new Simulation(Program.Engine, input, 0);
            var fire = new AscFireImport(simulation, asc, null);
            Assert.Equal(20, fire.GetCellCountX(), "read the backslash path: columns");
            Assert.Equal(40, fire.GetCellCountY(), "rows");
            Assert.Near(100.0, fire.GetCellSizeX(), 1e-6, "cell width");
            Assert.Near(50.0, fire.GetCellSizeY(), 1e-6, "cell height, not the width");
            fire.GetOffsetAndSize(out Math.Vector2d _, out Math.Vector2d size);
            Assert.Near(2000.0, size.y, 1e-6, "the grid is 40 x 50 m = 2 km tall");

            //A point 1.9 km north is in the grid's top row, which a square-cell reading put outside it.
            Math.Vector2int cell = fire.SimulationPosToCellIndex(new Math.Vector2d(1050, 1975), out bool inside);
            Assert.True(inside && cell.y == 39 && cell.x == 10, $"(1050, 1975) m is cell (10, 39), got ({cell.x}, {cell.y}) inside={inside}");
        }
    }
}
