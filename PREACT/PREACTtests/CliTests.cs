using PREACT.Utility;
using PREACTcli.Campaigns;

namespace PREACT.Tests
{
    /// <summary>
    /// The campaign CLI's own bookkeeping, checked without running a campaign: its argument parser, how it reads
    /// PREACT.exe's exit codes, where it looks for PREACT.exe, and where the GUI finds a campaign's results.
    /// </summary>
    internal static class CliTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("cli: PREACT exit 1 and 2 fail a realization and name its log", ExitCodes);
            runner.Add("cli: PREACT is found where the build script puts it, by the platform's name", FindPreact);
            runner.Add("cli: converge-trigger arguments are parsed strictly", ArgumentParsing);
            runner.Add("cli: the newest campaign folder is found, replaced ones are not", LatestCampaign);
            runner.Add("cli: every realization's evacuation runs on its own reproducible seed", EvacuationSeeds);
            runner.Add("cli: progress lines carry decile areas only for a realization with a boundary", ProgressAreas);
            runner.Add("cli: a realization's archived scenario opens where it is kept", ArchivedScenarioOpensInPlace);
        }

        private static void ArchivedScenarioOpensInPlace()
        {
            using var s = new FormatTests.Scenario();
            var baseLines = new List<string>(FormatTests.Scenario.Lines) { "", "[kPERIL]", "OutputName=b.asc", "WuiAreaFile=C:/elsewhere/wui.tif" };
            Campaign c = MinimalCampaign(s.Folder, 12345, baseLines.ToArray());
            string id = CampaignLayout.RealizationId(4);
            string dir = c.RealizationDir(id);
            Directory.CreateDirectory(Path.Combine(dir, "outputs"));

            var record = new RealizationRecord { Toa = "outputs/toa.tif", Ros = "outputs/vs.tif", Sd = "outputs/sd.tif", Mfws = "outputs/mfws.tif" };
            string[] ran = RealizationRunner.ScenarioLines(c, 4, id, dir, record);
            string[] kept = RealizationRunner.RebasePaths(ran, s.Folder, dir);

            Assert.True(kept.Contains("PopulationFile=../../../../pop.csv"), "the population, from the realization's folder: "
                        + kept.FirstOrDefault(l => l.StartsWith("PopulationFile")));
            Assert.True(kept.Contains("TimeOfArrivalFile=outputs/toa.tif"), "the realization's own fire, from its folder: "
                        + kept.FirstOrDefault(l => l.StartsWith("TimeOfArrivalFile")));
            Assert.True(kept.Contains("Name=base_01234567_0000004"), "other keys are untouched");
            Assert.True(kept.Contains("WuiAreaFile=C:/elsewhere/wui.tif"), "an absolute (drive) path is left alone");

            Input.PREACTInput input = Input.PREACTInput.LoadFromLines(kept, dir, out bool _);
            Assert.Equal(3, input.Population.Data.Households.Length, "the population loads from there");
            Assert.True(!Input.PREACTInput.Requirements.Any(r => r.Key.Contains("PopulationFile") || r.Key.Contains("MaskFile")),
                "nothing the base scenario had is missing: " + string.Join("; ", Input.PREACTInput.Requirements.Select(r => r + ": " + r.Message)));
        }

        private static void ProgressAreas()
        {
            string Line(string status, double[] area)
            {
                TextWriter was = Console.Out;
                var w = new StringWriter();
                try
                {
                    Console.SetOut(w);
                    ConvergenceAggregator.EmitProgressJson("0000002", status, 1, 1, 0, 0, 20, false, area, new double?[10]);
                }
                finally
                {
                    Console.SetOut(was);
                }
                return w.ToString();
            }

            var area = new double[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0.5 };
            Assert.True(Line(CampaignLayout.StatusOk, area).Contains("\"area\":[9,8,7"), "an ok realization reports the field");
            Assert.True(!Line(CampaignLayout.StatusNotThreatened, null).Contains("\"area\""), "a not-threatened one leaves it out");

        }

        /// <summary>A campaign with just what writing a realization's scenario reads.</summary>
        internal static Campaign MinimalCampaign(string scenarioDir, int seed, string[] baseLines)
        {
            var c = new Campaign
            {
                Options = CampaignOptions.Parse(new[] { "--wui", Path.Combine(scenarioDir, "base.wui"), "--max", "3", "--seed", seed.ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                BaseWuiPath = Path.Combine(scenarioDir, "base.wui"),
                ScenarioDir = scenarioDir,
                BaseLines = baseLines,
                ScenarioName = "base",
                SettingsHash = "0123456789abcdef",
                StartDateTime = new DateTime(2026, 6, 28, 12, 0, 0),
                InputsDir = Path.Combine(scenarioDir, "case", "inputs"),
            };
            c.Folder = Path.Combine(scenarioDir, "_output", "campaign_base_01234567");
            c.RealizationsDir = Path.Combine(c.Folder, "realizations");
            return c;
        }

        private static void EvacuationSeeds()
        {
            string dir = Directory.CreateTempSubdirectory("preact-seed-").FullName;
            try
            {
                string[] baseLines = { "[Simulation]", "Name=base", "RandomSeed=42", "", "[kPERIL]", "OutputName=b.asc" };
                Campaign c = MinimalCampaign(dir, 12345, baseLines);

                int Seed(string[] lines)
                {
                    Input.PREACTInput input = Input.PREACTInput.LoadFromLines(lines, dir, out bool _);
                    return input.Simulation.RandomSeed;
                }

                var seen = new HashSet<int>();
                for (int index = 1; index <= 200; ++index)
                {
                    string id = CampaignLayout.RealizationId(index);
                    var record = new RealizationRecord { Toa = "outputs/toa.tif", Ros = "outputs/vs.tif", Sd = "outputs/sd.tif", Mfws = "outputs/mfws.tif" };
                    string[] lines = RealizationRunner.ScenarioLines(c, index, id, c.RealizationDir(id), record);
                    int seed = Seed(lines);
                    Assert.True(seed != 0, "never 0, which PREACT reads as 'seed from the clock'");
                    Assert.True(seed != 42, "not the base scenario's own seed, which every realization would share");
                    Assert.Equal(record.EvacuationSeed, seed, "the record carries the seed the scenario was written with");
                    Assert.True(seen.Add(seed), "a seed of its own for realization " + index);

                    //The same realization again - a resume re-running only its evacuation - gets the same seed.
                    var again = new RealizationRecord { Toa = record.Toa, Ros = record.Ros, Sd = record.Sd, Mfws = record.Mfws };
                    Assert.Equal(seed, Seed(RealizationRunner.ScenarioLines(c, index, id, c.RealizationDir(id), again)),
                        "the same seed when realization " + index + " is written again");
                }

                Assert.True(RealizationRunner.EvacuationSeed(-RealizationRunner.EvacuationSeedOffset - 7, 7) != 0,
                    "the one index that would land on 0 is moved off it");
                Assert.True(RealizationRunner.EvacuationSeed(12345, 1) != RealizationRunner.EvacuationSeed(54321, 1),
                    "another campaign seed gives another evacuation");

                //Kept in the realization's record and read back.
                string rdir = Path.Combine(dir, "r");
                var saved = new RealizationRecord { EvacuationSeed = RealizationRunner.EvacuationSeed(12345, 3) };
                saved.Save(rdir);
                Assert.Equal(saved.EvacuationSeed, RealizationRecord.Load(rdir).EvacuationSeed, "realization.txt keeps the seed");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void ExitCodes()
        {
            string dir = Directory.CreateTempSubdirectory("preact-cli-").FullName;
            try
            {
                string log = Path.Combine(dir, "preact.log");
                File.WriteAllLines(log, new[] { "loading", "ERROR: k-PERIL: no evacuation arrivals", "The run reported 1 error(s)" });

                Assert.True(RealizationRunner.DescribeRun(0, true, 5, log) == null, "exit 0 with a boundary is a success");

                string noBoundary = RealizationRunner.DescribeRun(0, false, 3, log);
                Assert.True(noBoundary != null && noBoundary.Contains("no trigger boundary"), "exit 0 without a boundary fails: " + noBoundary);

                string notRun = RealizationRunner.DescribeRun(1, false, 0, log);
                Assert.True(notRun != null && notRun.Contains("exit 1") && notRun.Contains(log), "exit 1 fails and names the log: " + notRun);

                string failed = RealizationRunner.DescribeRun(2, true, 5, log);
                Assert.True(failed != null && failed.Contains("exit 2") && failed.Contains("not used"),
                    "exit 2 fails even with a boundary on disk: " + failed);
                Assert.True(failed.Contains(log) && failed.Contains("no evacuation arrivals"), "and quotes the log's tail: " + failed);

                string killed = RealizationRunner.DescribeRun(137, false, 0, log);
                Assert.True(killed != null && killed.Contains("137") && killed.Contains("killed"), "a kill is said to be one: " + killed);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        private static void FindPreact()
        {
            //<root>/PREACT/PREACTcli/bin/Debug/net8.0 and <root>/PREACT/PREACTexecute/bin/{Release,Debug}/net8.0
            string root = Directory.CreateTempSubdirectory("preact-find-").FullName;
            try
            {
                string cli = Path.Combine(root, "PREACT", "PREACTcli", "bin", "Debug", "net8.0");
                string release = Path.Combine(root, "PREACT", "PREACTexecute", "bin", "Release", "net8.0");
                string debug = Path.Combine(root, "PREACT", "PREACTexecute", "bin", "Debug", "net8.0");
                foreach (string d in new[] { cli, release, debug }) Directory.CreateDirectory(d);

                Assert.True(RealizationRunner.FindPreactExe(cli, false) == null, "nothing built: not found");

                File.WriteAllText(Path.Combine(debug, "PREACT"), "");
                Assert.Equal(Path.Combine(debug, "PREACT"), RealizationRunner.FindPreactExe(cli, false), "only a Debug build: that one");

                File.WriteAllText(Path.Combine(release, "PREACT.exe"), "");
                Assert.Equal(Path.Combine(debug, "PREACT"), RealizationRunner.FindPreactExe(cli, false),
                    "Linux does not take a PREACT.exe");
                Assert.Equal(Path.Combine(release, "PREACT.exe"), RealizationRunner.FindPreactExe(cli, true),
                    "Windows takes PREACT.exe from the build script's Release folder");

                File.WriteAllText(Path.Combine(release, "PREACT"), "");
                File.WriteAllText(Path.Combine(cli, "PREACT"), "");
                Assert.Equal(Path.Combine(release, "PREACT"), RealizationRunner.FindPreactExe(cli, false),
                    "the build script's output is preferred to a copy beside the CLI");

                File.Delete(Path.Combine(release, "PREACT"));
                Assert.Equal(Path.Combine(cli, "PREACT"), RealizationRunner.FindPreactExe(cli, false),
                    "then the copy beside the CLI, before the Debug build");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void ArgumentParsing()
        {
            CampaignOptions o = CampaignOptions.Parse(new[] { "--wui", "a.wui", "--max", "4", "--hours", "24", "--parallel", "2", "--resume" });
            Assert.Equal("a.wui", o.BaseWui, "--wui");
            Assert.Equal(4, o.MaxRealizations, "--max");
            Assert.Near(24.0, o.Hours, 0.0, "--hours");
            Assert.Equal(2, o.Parallelism, "--parallel");
            Assert.True(o.Resume, "--resume");

            Assert.True(CampaignOptions.Parse(new[] { "--wui", "a.wui", "--inspect" }).Inspect, "--inspect needs no --max");

            void Refused(string why, string expected, params string[] args)
            {
                try
                {
                    CampaignOptions.Parse(args);
                }
                catch (ArgumentException e)
                {
                    Assert.True(e.Message.Contains(expected), $"{why}: message '{e.Message}' should say '{expected}'");
                    return;
                }
                throw new TestFailure(why + ": was accepted");
            }

            Refused("an unknown option", "Unknown option", "--wui", "a.wui", "--max", "4", "--tsop", "36000");
            Refused("a retired option", "--hours", "--wui", "a.wui", "--max", "4", "--tstop", "36000");
            Refused("seconds typed as hours", "hours", "--wui", "a.wui", "--max", "4", "--hours", "7200000");
            Refused("a comma decimal", "number", "--wui", "a.wui", "--max", "4", "--tolerance", "0,02");
            Refused("a flag without its value", "needs a value", "--wui", "a.wui", "--max");
            Refused("no --wui", "--wui is required", "--max", "4");
            Refused("no --max", "--max is required", "--wui", "a.wui");
        }

        private static void LatestCampaign()
        {
            string root = Directory.CreateTempSubdirectory("preact-campaign-").FullName;
            try
            {
                Assert.True(CampaignLayout.LatestCampaignFolder(root, "mati") == null, "no _output: none");
                string output = Path.Combine(root, CampaignLayout.OutputFolder);
                string older = Path.Combine(output, CampaignLayout.CampaignFolderName("mati", "0123abcd"));
                string newer = Path.Combine(output, CampaignLayout.CampaignFolderName("mati", "89abcdef"));
                string replaced = newer + "_replaced_20260928_150000";
                string other = Path.Combine(output, CampaignLayout.CampaignFolderName("rafina", "fedcba98"));
                foreach (string d in new[] { older, newer, replaced, other }) Directory.CreateDirectory(d);

                File.WriteAllText(Path.Combine(older, CampaignLayout.ConvergenceCsv), "x");
                File.SetLastWriteTimeUtc(Path.Combine(older, CampaignLayout.ConvergenceCsv), DateTime.UtcNow.AddHours(1));
                Assert.Equal(older, CampaignLayout.LatestCampaignFolder(root, "mati"), "the one written to last");

                File.WriteAllText(Path.Combine(replaced, CampaignLayout.ConvergenceCsv), "x");
                File.SetLastWriteTimeUtc(Path.Combine(replaced, CampaignLayout.ConvergenceCsv), DateTime.UtcNow.AddHours(2));
                Assert.Equal(older, CampaignLayout.LatestCampaignFolder(root, "mati"), "a replaced folder is not a campaign");

                File.WriteAllText(Path.Combine(newer, CampaignLayout.LiveProbabilityRaster), "x");
                File.SetLastWriteTimeUtc(Path.Combine(newer, CampaignLayout.LiveProbabilityRaster), DateTime.UtcNow.AddHours(3));
                Assert.Equal(newer, CampaignLayout.LatestCampaignFolder(root, "mati"), "a running campaign's live raster counts");

                Assert.Equal(other, CampaignLayout.LatestCampaignFolder(root, "rafina"), "another scenario's is its own");
                Assert.Equal("mati", CampaignLayout.CampaignScenarioName("  mati ", "x/y.wui"), "[Simulation] Name, trimmed");
                Assert.Equal("y", CampaignLayout.CampaignScenarioName("", "x/y.wui"), "else the file name");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
