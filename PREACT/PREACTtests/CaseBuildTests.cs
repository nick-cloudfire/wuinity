using System.Globalization;
using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>
    /// Round 2's case build and fire areas (BL2): the fuel check that comes before any download, rebuilding only the
    /// weather, the WUI area as the evacuation groups' union, and painted initial ignition turned into an ignition point.
    /// On the synthetic case of <see cref="PipelineTests.SyntheticCase"/>: no network, no WindNinja, no ELMFIRE.
    /// </summary>
    internal static class CaseBuildTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("builder: a case without a fuel model is refused before its DEM, weather or WindNinja", NoFuelRefusedFirst);
            runner.Add("weather: rebuilding only the weather changes the five rasters and the namelist's time and band keys, nothing else", RebuildWeatherOnly);
            runner.Add("cli: build-case --weather-only rebuilds the weather and records only the [Weather] keys", CliWeatherOnly);
        }

        /// <summary>Every file of a case by content, without the five weather rasters (and GDAL's .aux.xml beside any raster).</summary>
        private static Dictionary<string, string> HashesWithoutWeather(string caseDir)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string f in Directory.GetFiles(caseDir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".aux.xml", StringComparison.Ordinal)) continue;
                if (ElmfireStems.Weather.Any(w => name == w + ".tif")) continue;
                hashes[Path.GetRelativePath(caseDir, f)] = ElmfireFingerprint.HashFile(f);
            }
            return hashes;
        }

        /// <summary>
        /// Nick: "Rebuild the weather now should not need to rebuild the entire case, just weather." A 1 h case from 12:00
        /// has its weather made again for 3 h from 15:30: ws/wd/m1/m10/m100 get 3 bands, the namelist's start and band keys
        /// follow, and every other file in the case is byte for byte what it was. The record of the namelist's hash follows
        /// it, so the next full build keeps it and keeps the weather.
        /// </summary>
        private static void RebuildWeatherOnly()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                string inputs = Path.Combine(caseDir, "inputs");
                ElmfireCaseBuilder.Options first = c.Options(caseDir, 150.0, new List<string>());
                first.StartDateTime = new DateTime(2026, 6, 28, 12, 0, 0);
                ElmfireCaseBuilder.Build(first).GetAwaiter().GetResult();
                Assert.Equal(1, AscRaster.GetBandCount(ElmfireStems.Tif(inputs, ElmfireStems.WindSpeed)), "a 1 h fire's weather bands");
                Dictionary<string, string> before = HashesWithoutWeather(caseDir);
                string namelistBefore = before["elmfire.data"];
                string[] linesBefore = File.ReadAllLines(Path.Combine(caseDir, "elmfire.data"));

                var log = new List<string>();
                ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, log);
                o.StartDateTime = new DateTime(2026, 6, 28, 15, 30, 0);
                o.SimulationTstopSeconds = 3 * 3600.0;
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.RebuildWeather(o).GetAwaiter().GetResult();

                foreach (string stem in ElmfireStems.Weather)
                {
                    Assert.Equal(3, AscRaster.GetBandCount(ElmfireStems.Tif(inputs, stem)), stem + ".tif bands for 3 h of fire");
                }
                Assert.True(r.Validation != null && r.Validation.Ok, "the case is consistent: " + ElmfireCaseValidator.Summarize(r.Validation));
                Assert.True(!log.Any(l => l.Contains("warping onto the master grid") || l.Contains("Warping DEM")),
                    "no layer was warped: " + string.Join(" | ", log));

                Dictionary<string, string> after = HashesWithoutWeather(caseDir);
                foreach (KeyValuePair<string, string> kv in before)
                {
                    if (kv.Key == "elmfire.data" || kv.Key == ElmfireCaseBuilder.SourceManifestName) continue;
                    Assert.True(after.TryGetValue(kv.Key, out string now) && now == kv.Value, kv.Key + " is unchanged");
                }
                Assert.True(after.Keys.All(k => before.ContainsKey(k)), "and no file was added: "
                    + string.Join(", ", after.Keys.Where(k => !before.ContainsKey(k))));

                string[] linesAfter = File.ReadAllLines(Path.Combine(caseDir, "elmfire.data"));
                List<string> changed = ElmfireNamelist.DescribeDifferences(linesBefore, linesAfter);
                var allowed = new[] { "BAND_ONE_HOUR_OF_YEAR", "HOUR_OF_YEAR", "FORECAST_START_HOUR", "SIMULATION_TSTOP", "NUM_METEOROLOGY_TIMES" };
                Assert.True(changed.Count > 0 && changed.All(d => allowed.Any(k => d.Contains(" " + k + ":"))),
                    "only the time and band keys changed: " + string.Join(" | ", changed));
                Assert.Equal("15.5", ElmfireNamelist.GetKeyInGroup(linesAfter, ElmfireNamelistKeys.TimeControlGroup, "FORECAST_START_HOUR"),
                    "the fire starts at 15:30");
                Assert.Equal("3", ElmfireNamelist.GetKeyInGroup(linesAfter, ElmfireNamelistKeys.MonteCarloGroup, ElmfireNamelistKeys.NumMeteorologyTimes),
                    "and reads the three bands");

                ElmfireCaseBuilder.WeatherRecord record = ElmfireCaseBuilder.ReadWeatherRecord(caseDir);
                Assert.True(record != null && record.Start == o.StartDateTime && System.Math.Abs(record.Hours - 3.0) < 1e-9,
                    "the case records what its weather was made for");

                //The next full build of the same fire keeps both, and does not take the fitted namelist for a hand edit.
                var next = new List<string>();
                ElmfireCaseBuilder.Options same = c.Options(caseDir, 150.0, next);
                same.StartDateTime = o.StartDateTime;
                same.SimulationTstopSeconds = o.SimulationTstopSeconds;
                ElmfireCaseBuilder.Result rebuilt = ElmfireCaseBuilder.Build(same).GetAwaiter().GetResult();
                Assert.True(rebuilt.KeptNamelistPath == null, "the fitted namelist is the builder's own, not a hand edit");
                Assert.True(rebuilt.Reused.Contains(ElmfireStems.WindSpeed), "and the weather is kept: " + string.Join(" | ", next));
                Assert.True(namelistBefore != ElmfireFingerprint.HashFile(Path.Combine(caseDir, "elmfire.data")), "(the namelist did change)");

                //A full build for another start hour makes the weather again, saying why.
                var moved = new List<string>();
                ElmfireCaseBuilder.Options later = c.Options(caseDir, 150.0, moved);
                later.StartDateTime = new DateTime(2026, 6, 28, 9, 0, 0);
                later.SimulationTstopSeconds = o.SimulationTstopSeconds;
                ElmfireCaseBuilder.Result redone = ElmfireCaseBuilder.Build(later).GetAwaiter().GetResult();
                Assert.True(!redone.Reused.Contains(ElmfireStems.WindSpeed)
                            && moved.Any(l => l.Contains("made for a fire starting at 15:30 and this one starts at 09:00")),
                    "weather made for another start hour is made again: " + string.Join(" | ", moved));

                //No case, no weather: refused, not built.
                Exception none = null;
                try { ElmfireCaseBuilder.RebuildWeather(c.Options(Path.Combine(c.Folder, "nothing"), 150.0, new List<string>())).GetAwaiter().GetResult(); }
                catch (Exception e) { none = e; }
                Assert.True(none is FileNotFoundException && !Directory.Exists(Path.Combine(c.Folder, "nothing", "inputs")),
                    "a folder without a case is refused and left alone: " + none?.Message);
            }
        }

        /// <summary>The same through PREACTcli: <c>--weather-only</c> on a built case, which records only the weather keys.</summary>
        private static void CliWeatherOnly()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string wui = c.WriteScenario("case", 300.0);
                string windNinja = Path.Combine(c.Folder, "no-windninja-here");
                (int exit, string output) = PipelineTests.RunCli(c.Folder, "build-case", "--wui", wui, "--dem", c.DemPath,
                    "--no-climatology", "--windninja", windNinja);
                Assert.Equal(0, exit, "the case builds (" + PipelineTests.Tail(output) + ")");
                string caseDir = Path.Combine(c.Folder, "case");
                Dictionary<string, string> before = HashesWithoutWeather(caseDir);

                (exit, output) = PipelineTests.RunCli(c.Folder, "build-case", "--wui", wui, "--weather-only", "--hours", "2",
                    "--no-climatology", "--windninja", windNinja);
                Assert.Equal(0, exit, "--weather-only exit code (" + PipelineTests.Tail(output) + ")");
                Assert.True(output.Contains("Weather rebuilt in") && output.Contains("Rebuilding the weather only"),
                    "it says what it did: " + PipelineTests.Tail(output, 12));
                Assert.Equal(2, AscRaster.GetBandCount(ElmfireStems.Tif(Path.Combine(caseDir, "inputs"), ElmfireStems.WindSpeed)),
                    "ws.tif covers 2 h");
                Dictionary<string, string> after = HashesWithoutWeather(caseDir);
                Assert.True(before.Where(kv => kv.Key != "elmfire.data" && kv.Key != ElmfireCaseBuilder.SourceManifestName)
                        .All(kv => after.TryGetValue(kv.Key, out string h) && h == kv.Value),
                    "no other file of the case changed");
                Assert.True(!output.Contains("[Landscape]") && !output.Contains("[kPERIL]"),
                    "only [Weather] keys would be recorded: " + PipelineTests.Tail(output, 12));

                (exit, output) = PipelineTests.RunCli(c.Folder, "build-case", "--wui", wui, "--weather-only", "--rebuild");
                Assert.True(exit == 2 && output.Contains("Pass one of them"), "--weather-only with --rebuild is refused: " + PipelineTests.Tail(output));
            }
        }

        /// <summary>
        /// Auburn2: the build downloaded ERA5 and ran 24 WindNinja bands before its validation found no fbfm40/fbfm13. Asked
        /// first now: nothing is written, and the message names the missing file when the scenario names one.
        /// </summary>
        private static void NoFuelRefusedFirst()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                var log = new List<string>();
                ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, log);
                o.UserRasters.Remove("fbfm40");
                o.UnresolvedSourceRasters["fbfm40"] = "downloads/landfire/fbfm40.tif";

                Exception failed = null;
                try { ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult(); }
                catch (Exception e) { failed = e; }

                Assert.True(failed is InvalidDataException && failed.Message.Contains("no fuel model")
                            && failed.Message.Contains("downloads/landfire/fbfm40.tif"),
                    "refused for want of fuel, naming the file the scenario names: " + failed?.Message);
                string inputs = Path.Combine(caseDir, "inputs");
                Assert.True(!File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.Dem)), "no DEM was made");
                Assert.True(!File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.WindSpeed)), "no weather was made");
                Assert.True(!log.Any(l => l.Contains("Warping DEM") || l.Contains("baseline weather")),
                    "nothing was started: " + string.Join(" | ", log));

                //The case's own fuel is enough: a second build of a built case needs no source for it.
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, new List<string>())).GetAwaiter().GetResult();
                ElmfireCaseBuilder.Options again = c.Options(caseDir, 150.0, new List<string>());
                again.UserRasters.Remove("fbfm40");
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(again).GetAwaiter().GetResult();
                Assert.True(r.FuelStem == "fbfm40" && r.Validation.Ok, "a case that has its fuel builds without a source for it");
            }
        }
    }
}
