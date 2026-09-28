using System.Globalization;
using PREACT.Evacuation;
using PREACT.Input;

namespace PREACT.Tests
{
    /// <summary>
    /// The .wui format rules, each on a small synthetic scenario written to a temporary folder.
    /// </summary>
    internal static class FormatTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("format: synthetic scenario loads runnable and round-trips", BaseScenarioLoads);
            runner.Add("format: inline comments, '=' in values, trimmed values", CommentsAndEquals);
            runner.Add("format: duplicate key is reported, first value wins", DuplicateKey);
            runner.Add("format: duplicate section is reported, not thrown", DuplicateSection);
            runner.Add("format: a bad value does not discard later sections", BadValueIsolated);
            runner.Add("format: population CSV with blank lines", PopulationBlankLines);
            runner.Add("format: absolute response curve survives a save", AbsoluteResponseCurve);
            runner.Add("format: response curve rows with comments and blank lines", ResponseCurveRows);
            runner.Add("format: backslash paths resolve and are saved with '/'", BackslashPaths);
            runner.Add("format: optional group keys are not critical", OptionalGroupKeys);
            runner.Add("format: numbers read the same on a comma-decimal thread", CultureIndependent);
            runner.Add("format: sections of disabled modules are kept on save", DisabledModulesKept);
            runner.Add("format: retired sections and keys are tolerated and not written", RetiredTolerated);
            runner.Add("format: Revalidate writes nothing into the scenario folder", RevalidateInMemory);
            runner.Add("format: CDF problems are reported", CdfValidation);
            runner.Add("format: ASC headers in any order, with centres and without nodata", AscHeaders);
            runner.Add("format: a missing landscape file keeps the ignition points, through load and save", MissingLandscapeKeepsIgnitions);
            runner.Add("format: the shipped examples load without a single warning", ExamplesLoadQuietly);
            runner.Add("format: every [ELMFIRE] key is read back, through a save and from a case's source manifest", ElmfireKeysRoundTrip);
            runner.Add("format: a [WildfireModule] without Enabled keeps its ignition points, painting and [ELMFIRE]", MissingEnabledKeepsFire);
            runner.Add("format: a module's sections survive a save when it is off, another option is chosen, or its header is missing", ModuleSectionsKept);
            runner.Add("format: only a selected imported fire decides the simulation's UTM zone", StaleAscImportDoesNotPin);
            runner.Add("format: what no parser reads is reported once, and nothing in it stops the run", IgnoredContentReported);
            runner.Add("roundtrip: a key a realization clears on purpose (WindSpeedFile=) is not reported as dropped", EmptyKeyRoundTrip);
        }

        /// <summary>
        /// docs.md 3.6: an unknown key or section was dropped without a word, so a typo in a hand edit simply did not
        /// happen and the next GUI save deleted it. Unreadable [ELMFIRE] values were dropped the same way, and an
        /// unreadable [ElmfireNamelist] value was critical although it has a default (docs.md 3.5).
        /// </summary>
        private static void IgnoredContentReported()
        {
            using var s = new Scenario();
            s.Load(Scenario.Lines, out bool _);
            Assert.True(!PREACTInput.Requirements.Any(r => r.Message.Contains("saving the scenario does not write it")),
                "the base scenario has nothing ignored");

            var lines = new List<string>(Scenario.Lines);
            lines.Insert(0, "junk before any section");
            lines.Insert(lines.IndexOf("DeltaTime=1") + 1, "Deltatime=2");
            lines.Insert(lines.IndexOf("CullOutsideGroups=false") + 1, "a line with no equals sign");
            foreach (int at in lines.Select((l, i) => l == "Type=Exit" ? i : -1).Where(i => i >= 0).Reverse().ToList())
            {
                lines.Insert(at + 1, "Colour=1,0,0");
            }
            lines.AddRange(new[]
            {
                "", "[Destinaton]", "Name=typo",
                "", "[Weather]", "DesiredLatLon=38,23",
                "", "[Behave]", "FuelModelsFile=x.csv",
                "", "[WildfireModule]", "Enabled=true", "Module=ELMFIRE",
                "", "[ELMFIRE]", "SimulationTstopHours=24h",
                "", "[ElmfireNamelist]", "SEEED=5", "LH_MOISTURE_CONTENT=6O",
            });

            Program.Log.Take();
            PREACTInput input = s.Load(lines, out bool runnable);
            List<string> log = Program.Log.Take();
            Assert.True(runnable, "nothing ignored or unreadable stops the run; critical: " + Critical());

            PREACTInput.InputRequirement Item(string section, string key)
            {
                PREACTInput.InputRequirement r = PREACTInput.Requirements.FirstOrDefault(x => x.Section == section && x.Key == key);
                Assert.True(r != null, $"[{section}] {key} is on the checklist: " + string.Join(" | ", PREACTInput.Requirements.Select(x => x.Section + "/" + x.Key)));
                Assert.True(!r.Critical, $"[{section}] {key} is not critical");
                return r;
            }

            Assert.True(Item("Simulation", "Deltatime").Message.Contains("did you mean DeltaTime?"), "a key differing in case is named");
            Assert.True(Item("Simulation", "Deltatime").Message.Contains("saving the scenario does not write it"), "and the consequence said");
            Assert.True(Item("Destinaton", "[Destinaton]").Message.Contains("did you mean [Destination]?"), "a mistyped section is named");
            string colour = Item("Destination", "Colour").Message;
            Assert.True(colour.Contains("and 1 more"), "a key in both destinations is one item: " + colour);
            Assert.Equal(1, log.Count(m => m.Contains("[Destination] Colour")), "and one log line");
            Assert.True(Item("Population", "line " + (lines.IndexOf("a line with no equals sign") + 1)).Message.Contains("not a Key=Value line"), "a stray line");
            Assert.True(Item("Scenario", "line 1").Message.Contains("before the first [section]"), "a line before any section");
            //Quoted as written (e2e N4: "junkbeforeanysection", "alinewithnoequalssign").
            Assert.True(Item("Scenario", "line 1").Message.Contains("\"junk before any section\""), "quoted verbatim: " + Item("Scenario", "line 1").Message);
            string stray = Item("Population", "line " + (lines.IndexOf("a line with no equals sign") + 1)).Message;
            Assert.True(stray.Contains("\"a line with no equals sign\""), "quoted verbatim: " + stray);
            Assert.True(Item("Weather", "DesiredLatLon").Message.Contains("no longer used"), "a retired key says so");
            Assert.True(Item("Behave", "[Behave]").Message.Contains("rate of spread now always comes from the fire module"), "a removed module's section");
            Assert.True(Item("ElmfireNamelist", "SEEED").Message.Contains("did you mean SEED?"), "a mistyped namelist key");
            Assert.True(Item(ElmfireInput.NamelistSection, "LH_MOISTURE_CONTENT").Message.Contains("60 is used instead"), "an unreadable namelist value keeps its default");
            Assert.True(Item("ELMFIRE", "SimulationTstopHours").Message.Contains("8 is used instead"), "an unreadable [ELMFIRE] value is said, not dropped");
            Assert.Near(8.0, input.WildfireModule.ElmfireInput.SimulationTstopHours, 1e-9, "and the default is what is used");

            //None of it is written, so the saved scenario reads clean.
            string[] saved = PREACTInputWriter.Write(input);
            Assert.True(!saved.Any(l => l.StartsWith("Deltatime") || l.StartsWith("Colour") || l == "[Destinaton]" || l.StartsWith("SEEED")),
                "ignored content is not written");
            PREACTInput.LoadFromLines(saved, s.Folder, out bool _);
            Assert.True(!PREACTInput.Requirements.Any(r => r.Message.Contains("saving the scenario does not write it")),
                "the saved scenario has nothing ignored: " + string.Join(" | ", PREACTInput.Requirements.Where(r => r.Message.Contains("does not write")).Select(r => r.Message)));
        }

        private static List<string> FireSections(params string[] wildfireModuleKeys)
        {
            var lines = new List<string>(Scenario.Lines) { "", "[WildfireModule]" };
            lines.AddRange(wildfireModuleKeys);
            lines.AddRange(new[]
            {
                "GraphicalFireInputFile=painted_fire_areas.gfi",
                "",
                "[ELMFIRE]",
                "CaseDirectory=mycase",
                "SimulationTstopHours=12",
                "",
                "[ElmfireNamelist]",
                "SEED=77",
                "",
                "[IgnitionPoint]",
                "LatLon=38.0141,23.9012",
                "IgnitionTime=0",
                "",
                "[IgnitionPoint]",
                "LatLon=38.0150,23.9030",
                "IgnitionTime=1800",
            });
            return lines;
        }

        private static void AssertFireKept(PREACTInput input, string when)
        {
            WildfireModuleInput fire = input.WildfireModule;
            Assert.Equal(2, fire.Data.IgnitionPoints.Count, when + ": ignition points");
            Assert.Near(1800.0, fire.Data.IgnitionPoints[1].IgnitionTime, 1e-6, when + ": the second point's time");
            Assert.Equal("painted_fire_areas.gfi", fire.GraphicalFireInputFile, when + ": painted areas");
            Assert.Equal(WildfireModuleInput.WildfireModules.ELMFIRE, fire.Module, when + ": module");
            Assert.Equal("mycase", fire.ElmfireInput.CaseDirectory, when + ": [ELMFIRE] CaseDirectory");
            Assert.Near(12.0, fire.ElmfireInput.SimulationTstopHours, 1e-9, when + ": [ELMFIRE] SimulationTstopHours");
            Assert.Equal(77, fire.ElmfireInput.Namelist.SEED, when + ": [ElmfireNamelist] SEED");
        }

        /// <summary>
        /// docs.md 3.2: without an Enabled line the parser returned before reading GraphicalFireInputFile, Module, the
        /// [IgnitionPoint]s and [ELMFIRE], and the next save deleted them. And Enabled=false skipped Module and
        /// [ELMFIRE], so a switched-off ELMFIRE fire lost its settings on the first save.
        /// </summary>
        /// <summary>
        /// Review NIT: "PREACTtests &lt;realization .wui&gt;" failed every realization scenario with "[kPERIL] WindSpeedFile= was
        /// dropped by the writer": the CLI writes the key empty on purpose and the writer leaves empty paths out.
        /// </summary>
        private static void EmptyKeyRoundTrip()
        {
            using var s = new Scenario();
            var lines = new List<string>(Scenario.Lines)
            {
                "", "[TriggerBufferModule]", "Enabled=false", "Module=kPERIL",
                "", "[kPERIL]", "WindSpeedFile=", "OutputName=r1_trigger.asc",
            };
            string file = Path.Combine(s.Folder, "realization.wui");
            File.WriteAllLines(file, lines);
            RoundTrip.Run(file, null);
        }

        private static void MissingEnabledKeepsFire()
        {
            using var s = new Scenario();
            foreach (string[] header in new[] { new[] { "Module=ELMFIRE" }, new[] { "Enabled=false", "Module=ELMFIRE" }, new[] { "Enabled=maybe", "Module=ELMFIRE" } })
            {
                string when = string.Join(" ", header);
                PREACTInput input = s.Load(FireSections(header), out bool runnable);
                Assert.True(runnable, when + ": the fire is off, so nothing in it stops the run; critical: " + Critical());
                Assert.True(!input.WildfireModule.Enabled, when + ": read as off");
                if (!header[0].StartsWith("Enabled=false"))
                {
                    Assert.True(PREACTInput.Requirements.Any(r => r.Key == "Enabled" && !r.Critical), when + ": and said so");
                }
                if (!header[0].StartsWith("Enabled="))
                {
                    //e2e N5: said where the GUI shows it, not as one more default.
                    PREACTInput.InputRequirement off = PREACTInput.Requirements.First(r => r.Section == "WildfireModule" && r.Key == "Enabled");
                    Assert.True(off.Notice && off.Message.Contains("the fire module is off and the scenario runs without a fire")
                                && off.Message.Contains("Enabled=true"), when + ": a notice, saying what it means: " + off.Message);
                }
                AssertFireKept(input, when);

                string[] saved = PREACTInputWriter.Write(input);
                Assert.True(saved.Contains("Enabled=false") && saved.Contains("Module=ELMFIRE"), when + ": written as an ELMFIRE fire that is off");
                PREACTInput again = PREACTInput.LoadFromLines(saved, s.Folder, out bool _);
                AssertFireKept(again, when + ", after a save");
                Assert.True(PREACTInputWriter.Write(again).SequenceEqual(saved), when + ": a second save writes the same");
            }
        }

        /// <summary>
        /// The writer used to write only the selected module's sub-section, and a parser read nothing under a missing
        /// module header: an imported fire's [AscImport] went with a switch to ELMFIRE, a [GlobalSmoke] with smoke set
        /// to None, and a [SUMO] without its [TrafficModule].
        /// </summary>
        private static void ModuleSectionsKept()
        {
            using var s = new Scenario();
            var lines = FormatTests.Replace("Enabled", null, "TrafficModule");
            lines.Remove("[TrafficModule]");
            lines.Remove("Module=SUMO");
            lines.AddRange(new[]
            {
                "", "[WildfireModule]", "Enabled=true", "Module=ELMFIRE",
                "", "[AscImport]", "StartDateTime=2026-06-28T12:00:00", "TimeOfArrivalFile=old/toa.asc", "TimeOfArrivalUnits=Minutes",
                "", "[GlobalSmoke]", "ExtinctionFile=smoke/ramp.exc",
                "", "[TriggerBufferModule]", "Enabled=false", "Module=None",
                "", "[kPERIL]", "OutputName=kept_boundary",
            });
            Assert.True(!lines.Contains("[TrafficModule]") && lines.Contains("[SUMO]"), "test setup: [SUMO] without its module");

            PREACTInput input = s.Load(lines, out bool runnable);
            Assert.True(runnable, "nothing unselected or switched off is critical: " + Critical());
            Assert.True(!input.TrafficModule.Enabled, "traffic is off without its header");
            Assert.Equal("sumo/osm.sumocfg", input.TrafficModule.SumoInput.ConfigurationFile, "[SUMO] is read without its header");
            Assert.Equal("old/toa.asc", input.WildfireModule.AscImportInput.TimeOfArrivalFile, "[AscImport] is read beside ELMFIRE");
            Assert.Equal("smoke/ramp.exc", input.SmokeModule.GlobalSmokeInput.ExtinctionFile, "[GlobalSmoke] is read with no smoke module");
            Assert.Equal("kept_boundary", input.TriggerBufferModule.kPERILInput.OutputName, "[kPERIL] is read with Module=None");

            string[] saved = PREACTInputWriter.Write(input);
            foreach (string kept in new[] { "ConfigurationFile=sumo/osm.sumocfg", "TimeOfArrivalFile=old/toa.asc", "TimeOfArrivalUnits=Minutes",
                                            "ExtinctionFile=smoke/ramp.exc", "OutputName=kept_boundary" })
            {
                Assert.True(saved.Contains(kept), kept + " is saved");
            }
            PREACTInput again = PREACTInput.LoadFromLines(saved, s.Folder, out bool runnableAgain);
            Assert.True(runnableAgain, "and reloads runnable: " + Critical());
            Assert.Equal(AscImportInput.TimeUnits.Minutes, again.WildfireModule.AscImportInput.TimeOfArrivalUnits, "the imported fire's units, reloaded");
            Assert.True(PREACTInputWriter.Write(again).SequenceEqual(saved), "a second save writes the same");

            //A fresh scenario writes no sub-section for a module option nobody configured.
            string[] fresh = PREACTInputWriter.Write(new PREACTInput(s.Folder));
            Assert.True(!fresh.Contains("[AscImport]") && !fresh.Contains("[ELMFIRE]") && !fresh.Contains("[GlobalSmoke]") && !fresh.Contains("[kPERIL]"),
                "defaults are not written: " + string.Join(" ", fresh.Where(l => l.StartsWith("["))));
        }

        /// <summary>
        /// Now that a switch to ELMFIRE keeps the old [AscImport], its arrival raster must not go on deciding the
        /// simulation's UTM zone: that is only the selected imported fire's to decide.
        /// </summary>
        private static void StaleAscImportDoesNotPin()
        {
            using var s = new Scenario();
            File.WriteAllLines(Path.Combine(s.Folder, "toa.asc"), new[] { "ncols 2", "nrows 2", "xllcorner 200000", "yllcorner 4200000", "cellsize 1000", "1 1", "1 1" });
            Utility.AscRaster.WriteCompanionPrj(Path.Combine(s.Folder, "toa.asc"), 32635);
            Assert.True(File.Exists(Path.Combine(s.Folder, "toa.prj")), "test setup: the .prj was written");

            foreach (string module in new[] { "AscImport", "ELMFIRE" })
            {
                var lines = new List<string>(Scenario.Lines)
                {
                    "", "[WildfireModule]", "Enabled=false", "Module=" + module,
                    "", "[AscImport]", "StartDateTime=2026-06-28T12:00:00", "TimeOfArrivalFile=toa.asc",
                };
                PREACTInput input = s.Load(lines, out bool _);
                Assert.Equal(module == "AscImport" ? 32635 : 32634, input.Simulation.Data.UtmEpsgCode, "zone with Module=" + module);
            }
        }

        /// <summary>
        /// A value unlike the default for every writable [ELMFIRE] field, as it would be written. Paths are
        /// relative and, for the source layers, real files in the scenario folder, so the load has nothing to warn
        /// about.
        /// </summary>
        private static Dictionary<string, string> NonDefaultElmfireKeys(string folder)
        {
            var fresh = new ElmfireInput();
            var keys = new Dictionary<string, string>();
            foreach (System.Reflection.FieldInfo f in typeof(ElmfireInput).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                object value = f.GetValue(fresh);
                string text;
                if (f.FieldType == typeof(string))
                {
                    text = f.Name.EndsWith("File") ? "sources/" + f.Name + ".tif" : "custom_" + f.Name;
                    if (f.Name.EndsWith("File"))
                    {
                        Directory.CreateDirectory(Path.Combine(folder, "sources"));
                        File.WriteAllText(Path.Combine(folder, text), "not read at load");
                    }
                }
                else if (f.FieldType == typeof(bool)) text = (!(bool)value) ? "true" : "false";
                else if (f.FieldType == typeof(double)) text = ((double)value + 1.5).ToString("R", CultureInfo.InvariantCulture);
                else if (f.FieldType.IsEnum)
                {
                    Array values = Enum.GetValues(f.FieldType);
                    text = values.GetValue((Array.IndexOf(values, value) + 1) % values.Length).ToString();
                }
                else continue; //the namelist settings, which are a section of their own
                keys[f.Name] = text;
            }
            return keys;
        }

        private static string Text(object value) => value is double d ? d.ToString("R", CultureInfo.InvariantCulture)
            : value is bool b ? (b ? "true" : "false") : value?.ToString();

        /// <summary>
        /// docs.md 3.1: SuppressionDifficultyFile, LandValueFile, PopulationDensityFile, RealEstateValueFile,
        /// EnergyReleaseComponentFile and PyromesFile were written but never parsed, so reopening and saving a
        /// scenario dropped them. Every writable field is checked, so a key added later without its parse fails here.
        /// </summary>
        private static void ElmfireKeysRoundTrip()
        {
            using var s = new Scenario();
            Dictionary<string, string> keys = NonDefaultElmfireKeys(s.Folder);
            Assert.True(keys.ContainsKey(nameof(ElmfireInput.PyromesFile)) && keys.Count > 25, "the fields were found: " + keys.Count);

            var lines = new List<string>(Scenario.Lines) { "", "[WildfireModule]", "Enabled=true", "Module=ELMFIRE", "", "[ELMFIRE]" };
            lines.AddRange(keys.Select(kv => kv.Key + "=" + kv.Value));

            PREACTInput input = s.Load(lines, out bool _);
            string[] saved = PREACTInputWriter.Write(input);
            PREACTInput again = PREACTInput.LoadFromLines(saved, s.Folder, out bool _);
            foreach (KeyValuePair<string, string> kv in keys)
            {
                System.Reflection.FieldInfo f = typeof(ElmfireInput).GetField(kv.Key);
                Assert.Equal(kv.Value, Text(f.GetValue(input.WildfireModule.ElmfireInput)), "[ELMFIRE] " + kv.Key + " as read");
                Assert.True(saved.Contains(kv.Key + "=" + kv.Value), "[ELMFIRE] " + kv.Key + " is written");
                Assert.Equal(kv.Value, Text(f.GetValue(again.WildfireModule.ElmfireInput)), "[ELMFIRE] " + kv.Key + " after a save and reload");
            }

            //The other direction a case's layers come back by: the builder's case_sources.txt, stem=source.
            List<KeyValuePair<string, string>> layers = input.WildfireModule.ElmfireInput.GetSourceRasters().ToList();
            Assert.Equal(18, layers.Count, "source layers listed for the build");
            string caseDir = Path.Combine(s.Folder, "case");
            Directory.CreateDirectory(caseDir);
            File.WriteAllLines(Path.Combine(caseDir, Utility.ElmfireCaseBuilder.SourceManifestName), layers.Select(kv => kv.Key + "=" + kv.Value));
            var fromCase = new ElmfireInput();
            Assert.True(fromCase.LoadSourcesFromCase(caseDir, out int applied, out string problem), "the manifest reads: " + problem);
            Assert.Equal(18, applied, "every layer applied");
            Assert.Equal(string.Join(";", layers), string.Join(";", fromCase.GetSourceRasters()), "the same layers come back");
        }

        /// <summary>
        /// e2e F11: the examples carried retired keys, left out OutputRasterSize, had fire rasters without a CRS and
        /// were told k-PERIL needs a WUI area while k-PERIL was off - every load warned four or five times, which
        /// teaches a user to ignore warnings.
        /// </summary>
        private static List<string> ExamplesLoadQuietly()
        {
            var warnings = new List<string>();
            string repo = Program.FindRepositoryRoot();
            if (repo == null)
            {
                warnings.Add("the repository's Examples folder was not found; skipped");
                return warnings;
            }

            foreach (string wui in RoundTrip.Examples(repo))
            {
                Program.Log.Take();
                PREACTInput.LoadFromDisk(wui, out bool _);
                List<string> said = Program.Log.Take().Where(m => m.Contains("WARNING:") || m.Contains("ERROR:")).ToList();
                Assert.True(said.Count == 0, Path.GetFileName(wui) + " warns: " + string.Join(" | ", said));
            }
            return warnings;
        }

        /// <summary>
        /// e2e F3: a [Landscape] naming files that are not there (a case folder emptied or rebuilt, a scenario shared
        /// without it) used to end the [WildfireModule] section with GDAL's "No such file or directory" before its
        /// [IgnitionPoint]s were read, and the next save deleted them.
        /// </summary>
        private static void MissingLandscapeKeepsIgnitions()
        {
            using var s = new Scenario();
            var lines = new List<string>(Scenario.Lines)
            {
                "",
                "[Landscape]",
                "ElevationFile=elmfire/inputs/mati_dem.tif",
                "SlopeFile=elmfire/inputs/mati_slope.tif",
                "AspectFile=elmfire/inputs/mati_aspect.tif",
                "",
                "[WildfireModule]",
                "Enabled=true",
                "Module=ELMFIRE",
                "",
                "[ELMFIRE]",
                "CaseDirectory=elmfire",
                "SimulationTstopHours=8",
                "",
                "[IgnitionPoint]",
                "LatLon=38.0141,23.9012",
                "IgnitionTime=0",
                "",
                "[IgnitionPoint]",
                "LatLon=38.0150,23.9030",
                "IgnitionTime=1800",
            };

            PREACTInput input = s.Load(lines, out bool _);
            Assert.Equal(2, input.WildfireModule.Data.IgnitionPoints.Count, "both ignition points are read");
            Assert.True(!PREACTInput.Requirements.Any(r => r.Critical && r.Message.Contains("No such file")),
                "the missing file does not make the fire section unreadable: " + Critical());
            Assert.True(PREACTInput.Requirements.Any(r => r.Key.Contains("ElevationFile")), "the missing DEM is on the checklist");
            Assert.True(input.WildfireModule.ElmfireInput != null && input.WildfireModule.ElmfireInput.SimulationTstopHours == 8,
                "the [ELMFIRE] section is read too");

            string[] saved = PREACTInputWriter.Write(input);
            Assert.Equal(2, saved.Count(l => l.Trim() == "[IgnitionPoint]"), "the save keeps both [IgnitionPoint] sections");
            Assert.True(saved.Any(l => l.Trim() == "ElevationFile=elmfire/inputs/mati_dem.tif"), "and the landscape's path, to fix later");

            PREACTInput again = PREACTInput.LoadFromLines(saved, s.Folder, out bool _);
            Assert.Equal(2, again.WildfireModule.Data.IgnitionPoints.Count, "the saved scenario still has both points");
            Assert.Near(1800.0, again.WildfireModule.Data.IgnitionPoints[1].IgnitionTime, 1e-6, "with their times");
        }

        /// <summary>A folder with a minimal, complete scenario: pedestrians on, traffic off, no fire.</summary>
        internal sealed class Scenario : IDisposable
        {
            public readonly string Folder;
            public Scenario()
            {
                Folder = Path.Combine(Path.GetTempPath(), "preact-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(Path.Combine(Folder, "pop.csv"), new[]
                {
                    "lat,lon,carLat,carLon,people",
                    "38.0130,23.9010,38.0131,23.9011,3",
                    "38.0140,23.9020,38.0141,23.9021,2",
                    "38.0150,23.9030,38.0151,23.9031,4",
                });
                //In simulation coordinates, covering the whole 2 x 2 km domain.
                File.WriteAllLines(Path.Combine(Folder, "group.asc"), new[]
                {
                    "ncols 2", "nrows 2", "xllcorner 0", "yllcorner 0", "cellsize 1000", "NODATA_value -9999", "1 1", "1 1",
                });
            }

            public static string[] Lines => new[]
            {
                "[Simulation]",
                "Name=synthetic",
                "LowerLeftLatLon=38.0123,23.9",
                "DomainSize=2000,2000",
                "DeltaTime=1",
                "StartDateTime=2026-06-28T12:00:00",
                "EndDateTime=2026-06-28T14:00:00",
                "StopWhenEvacuated=true",
                "",
                "[Population]",
                "PopulationFile=pop.csv",
                "CullOutsideGroups=false",
                "",
                "[Demographics]",
                "Name=standard",
                "AllowMoreThanOneCar=true",
                "MaxCars=2",
                "MaxCarsProbability=0.3",
                "Default=true",
                "",
                "[ResponseCurve]",
                "Name=standard",
                "TimeInput=Relative",
                "0,0",
                "600,0.5",
                "1200,1",
                "",
                "[Destination]",
                "Name=north",
                "LatLon=38.03,23.91",
                "Type=Exit",
                "MaxFlow=-1",
                "MaxVehicles=-1",
                "MaxPeople=-1",
                "Blocked=false",
                "Color=1,0,0",
                "",
                "[Destination]",
                "Name=south",
                "LatLon=38.013,23.905",
                "Type=Exit",
                "MaxFlow=-1",
                "MaxVehicles=-1",
                "MaxPeople=-1",
                "Blocked=false",
                "Color=0,1,0",
                "",
                "[EvacuationGroup]",
                "Name=all",
                "EvacuationOrderDateTime=2026-06-28T12:00:00",
                "Color=1,1,1",
                "DestinationChoice=EvacGroupCDF",
                "Destinations=north,south",
                "DestinationsCDF=0.5,1",
                "ResponseCurves=standard",
                "Demographics=standard",
                "MaskFile=group.asc",
                "Default=true",
                "",
                "[PedestrianModule]",
                "Enabled=true",
                "Module=MacroHouseholdSim",
                "",
                "[MacroHouseholdSim]",
                "WalkingSpeedMinMax=0.7,1",
                "",
                "[TrafficModule]",
                "Enabled=false",
                "Module=SUMO",
                "",
                "[SUMO]",
                "ConfigurationFile=sumo/osm.sumocfg",
                "OutputRasterSize=25",
                "SmokeAlpha=0",
                "SmokeBeta=0",
            };

            public string Write(IEnumerable<string> lines, string name = "scenario.wui")
            {
                string path = Path.Combine(Folder, name);
                File.WriteAllLines(path, lines);
                return path;
            }

            public PREACTInput Load(IEnumerable<string> lines, out bool runnable)
            {
                return PREACTInput.LoadFromDisk(Write(lines), out runnable);
            }

            public void Dispose()
            {
                try { Directory.Delete(Folder, true); } catch { }
            }
        }

        internal static List<string> Replace(string key, string value, string section = null)
        {
            var lines = new List<string>(Scenario.Lines);
            string current = null;
            for (int i = 0; i < lines.Count; ++i)
            {
                if (lines[i].StartsWith("[")) { current = lines[i].Trim('[', ']'); continue; }
                if ((section == null || current == section) && lines[i].StartsWith(key + "="))
                {
                    if (value == null) lines.RemoveAt(i); else lines[i] = key + "=" + value;
                    return lines;
                }
            }
            throw new TestFailure("test setup: key " + key + " not found");
        }

        private static PREACTInput.InputRequirement Find(string key)
        {
            return PREACTInput.Requirements.FirstOrDefault(r => r.Key == key || r.Key.EndsWith(" " + key));
        }

        private static string Critical() => string.Join("; ", PREACTInput.Requirements.Where(r => r.Critical).Select(r => r.ToString() + ": " + r.Message));

        private static void BaseScenarioLoads()
        {
            using var s = new Scenario();
            PREACTInput input = s.Load(Scenario.Lines, out bool runnable);
            Assert.True(runnable, "the base scenario should be runnable; critical: " + Critical());
            Assert.Equal(3, input.Population.Data.Households.Length, "households");
            Assert.Equal(2, input.Evacuation.EvacuationDestinationInputs.Count, "destinations");
            RoundTrip.Run(Path.Combine(s.Folder, "scenario.wui"), null);
        }

        private static void CommentsAndEquals()
        {
            using var s = new Scenario();
            var lines = new List<string>(Scenario.Lines);
            lines[lines.IndexOf("Name=synthetic")] = "Name=a=b   # the name";
            lines[lines.IndexOf("Module=MacroHouseholdSim")] = "Module=MacroHouseholdSim #comment";
            lines.Insert(1, "   # a whole-line comment");
            lines.Insert(1, "#another");
            PREACTInput input = s.Load(lines, out bool runnable);
            Assert.Equal("a=b", input.Simulation.Name, "Name split on the first '=' and trimmed after the comment");
            Assert.Equal(PedestrianModuleInput.PedestrianModules.MacroHouseholdSim, input.PedestrianModule.Module, "Module with an inline comment");
            Assert.True(runnable, "still runnable; critical: " + Critical());
            Assert.True(Find("Module") == null, "no requirement for the commented Module");
        }

        private static void DuplicateKey()
        {
            using var s = new Scenario();
            var lines = new List<string>(Scenario.Lines);
            lines.Insert(lines.IndexOf("DeltaTime=1") + 1, "DeltaTime=5");
            PREACTInput input = s.Load(lines, out bool runnable);
            Assert.Near(1.0, input.Simulation.DeltaTime, 1e-9, "the first DeltaTime is used");
            Assert.True(runnable, "a duplicate key does not block the run");
            PREACTInput.InputRequirement r = Find("DeltaTime");
            Assert.True(r != null && !r.Critical, "the duplicate is on the checklist, non-critical");
        }

        private static void DuplicateSection()
        {
            using var s = new Scenario();
            var lines = new List<string>(Scenario.Lines);
            lines.AddRange(new[] { "", "[Simulation]", "Name=second" });
            PREACTInput input = s.Load(lines, out bool runnable);
            Assert.Equal("synthetic", input.Simulation.Name, "the first [Simulation] is read");
            Assert.True(runnable, "a duplicate section does not block the run; critical: " + Critical());
            Assert.True(PREACTInput.Requirements.Any(r => r.Key == "[Simulation]" && !r.Critical), "the duplicate section is reported");
        }

        private static void BadValueIsolated()
        {
            using var s = new Scenario();
            PREACTInput input = s.Load(Replace("LowerLeftLatLon", "37.96"), out bool runnable);
            Assert.True(!runnable, "an unreadable LowerLeftLatLon is critical");
            Assert.True(Find("LowerLeftLatLon")?.Critical == true, "LowerLeftLatLon is the critical item");
            Assert.Equal(2, input.Evacuation.EvacuationDestinationInputs.Count, "later sections are still read");
            Assert.Equal("synthetic", input.Simulation.Name, "the rest of [Simulation] is still read");
            Assert.Equal(new DateTime(2026, 6, 28, 14, 0, 0), input.Simulation.EndDateTime, "keys after the bad one are still read");
        }

        private static void PopulationBlankLines()
        {
            using var s = new Scenario();
            File.WriteAllLines(Path.Combine(s.Folder, "pop.csv"), new[]
            {
                "lat,lon,carLat,carLon,people", "38.0130,23.9010,38.0131,23.9011,3", "", "38.0140,23.9020,38.0141,23.9021,2", "", "   ", "",
            });
            PREACTInput input = s.Load(Scenario.Lines, out bool runnable);
            Assert.True(runnable, "blank lines do not break the population; critical: " + Critical());
            Assert.Equal(2, input.Population.Data.Households.Length, "households");
        }

        private static void AbsoluteResponseCurve()
        {
            using var s = new Scenario();
            var lines = new List<string>(Scenario.Lines);
            int at = lines.IndexOf("TimeInput=Relative");
            lines[at] = "TimeInput=Absolute";
            lines[at + 1] = "2026-06-28T12:00:00,0";
            lines[at + 2] = "2026-06-28T12:10:00,0.5";
            lines[at + 3] = "2026-06-28T12:20:00,1";
            PREACTInput input = s.Load(lines, out bool runnable);
            Assert.True(runnable, "absolute curve loads; critical: " + Critical());
            ResponseCurve curve = input.Evacuation.ResponseCurves["standard"];
            Assert.Equal(TimeInputs.Absolute, curve.TimeInput, "TimeInput");
            Assert.Near(600.0, curve.DataPoints[1].Time, 1e-3, "second point, seconds after the start");

            string[] written = PREACTInputWriter.Write(input);
            Assert.True(written.Contains("2026-06-28T12:10:00,0.5"), "written back as a date");
            PREACTInput again = PREACTInput.LoadFromLines(written, s.Folder, out bool runnable2);
            Assert.True(runnable2, "reloads runnable; critical: " + Critical());
            Assert.Near(1200.0, again.Evacuation.ResponseCurves["standard"].DataPoints[2].Time, 1e-3, "third point after the round trip");
        }

        private static void ResponseCurveRows()
        {
            using var s = new Scenario();
            var lines = new List<string>(Scenario.Lines);
            int at = lines.IndexOf("TimeInput=Relative");
            lines.Insert(at + 1, "# rows follow");
            lines.Insert(at + 3, "");
            lines.Insert(at + 3, "600, 0.5   # half by ten minutes");
            lines.RemoveAt(at + 5); //the original 600 row
            PREACTInput input = s.Load(lines, out bool runnable);
            Assert.True(runnable, "curve with comments and blank lines loads; critical: " + Critical());
            Assert.Equal(3, input.Evacuation.ResponseCurves["standard"].DataPoints.Length, "rows");

            var bad = new List<string>(Scenario.Lines);
            bad[bad.IndexOf("600,0.5")] = "600;0.5";
            s.Load(bad, out bool badRunnable);
            Assert.True(!badRunnable, "a bad row is critical");
            Assert.True(PREACTInput.Requirements.Any(r => r.Critical && r.Message.Contains("600;0.5")), "the message names the bad row");
        }

        private static void BackslashPaths()
        {
            using var s = new Scenario();
            Directory.CreateDirectory(Path.Combine(s.Folder, "people"));
            File.Move(Path.Combine(s.Folder, "pop.csv"), Path.Combine(s.Folder, "people", "pop.csv"));
            PREACTInput input = s.Load(Replace("PopulationFile", "people\\pop.csv"), out bool runnable);
            Assert.True(runnable, "a backslash path resolves on this platform; critical: " + Critical());
            Assert.Equal("people/pop.csv", input.Population.PopulationFile, "stored with a forward slash");
            Assert.True(PREACTInputWriter.Write(input).Contains("PopulationFile=people/pop.csv"), "written with a forward slash");
        }

        private static void OptionalGroupKeys()
        {
            using var s = new Scenario();
            var lines = Replace("EvacuationOrderDateTime", null, "EvacuationGroup");
            lines.Remove("Demographics=standard");
            PREACTInput input = s.Load(lines, out bool runnable);
            Assert.True(runnable, "EvacuationOrderDateTime and Demographics are optional; critical: " + Critical());
            Assert.Equal(input.Simulation.StartDateTime, input.Evacuation.EvacuationGroupInputs["all"].EvacuationOrderDateTime, "order defaults to the start");
            Assert.True(Find("EvacuationOrderDateTime")?.Critical == false, "reported as a default");
        }

        private static void CultureIndependent()
        {
            using var s = new Scenario();
            string path = s.Write(Scenario.Lines);
            PREACTInput input = null;
            var thread = new Thread(() =>
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("el-GR");
                input = PREACTInput.LoadFromDisk(path, out bool _);
            });
            thread.Start();
            thread.Join();
            Assert.Near(38.0123, input.Simulation.LowerLeftLatLon.x, 1e-9, "latitude on an el-GR thread");
            Assert.Near(0.3, input.Population.Demographics["standard"].MaxCarsProbability, 1e-6, "probability on an el-GR thread");
            Assert.Near(38.0130, input.Population.Data.Households[0].originLatLon.x, 1e-9, "population CSV on an el-GR thread");
        }

        private static void DisabledModulesKept()
        {
            using var s = new Scenario();
            var lines = Replace("Enabled", "false", "PedestrianModule");
            PREACTInput input = s.Load(lines, out bool runnable);
            Assert.True(runnable, "a scenario with everything off is runnable; critical: " + Critical());
            string[] written = PREACTInputWriter.Write(input);
            Assert.True(written.Contains("ConfigurationFile=sumo/osm.sumocfg"), "[SUMO] of a disabled traffic module is kept");
            Assert.True(written.Contains("PopulationFile=pop.csv"), "[Population] of a disabled pedestrian module is kept");
            Assert.True(written.Contains("Name=north"), "destinations are kept with both modules off");
            Assert.True(written.Contains("ResponseCurves=standard"), "groups are kept with both modules off");
        }

        private static void RetiredTolerated()
        {
            using var s = new Scenario();
            var lines = new List<string>(Scenario.Lines);
            lines.AddRange(new[] { "", "[Events]", "BlockGoalEventFiles=a.csv", "", "[Evacuation]", "UseTriggerBufferEvacuation=true", "TriggerBufferFile=x.asc" });
            PREACTInput input = s.Load(lines, out bool runnable);
            Assert.True(runnable, "retired keys do not block the run; critical: " + Critical());
            Assert.True(PREACTInput.Requirements.Any(r => r.Key == "[Events]" && !r.Critical), "[Events] is noted once");
            Assert.True(PREACTInput.Requirements.Any(r => r.Key == "UseTriggerBufferEvacuation" && !r.Critical), "the retired key is noted");
            string[] written = PREACTInputWriter.Write(input);
            Assert.True(!written.Any(l => l.StartsWith("UseTriggerBufferEvacuation") || l.StartsWith("TriggerBufferFile") || l == "[Events]"), "retired keys are not written");
            Assert.True(!written.Any(l => l.StartsWith("WeatherAnchorDateTime") || l.StartsWith("HasWeatherAnchor")), "no unset anchor or computed property written");
        }

        private static void RevalidateInMemory()
        {
            using var s = new Scenario();
            PREACTInput input = s.Load(Scenario.Lines, out bool _);
            string[] before = Directory.GetFiles(s.Folder);
            Assert.True(PREACTInput.Revalidate(input), "revalidates as runnable; critical: " + Critical());
            input.Simulation.Name = string.Empty;
            Assert.True(!PREACTInput.Revalidate(input), "an emptied Name is caught");
            Assert.Equal(before.Length, Directory.GetFiles(s.Folder).Length, "files in the scenario folder");
        }

        private static void AscHeaders()
        {
            using var s = new Scenario();
            string a = Path.Combine(s.Folder, "a.asc");
            File.WriteAllLines(a, new[] { "NROWS 2", "ncols 3", "xllcenter 105", "yllcenter 205", "cellsize 10", "1 2 3", "4 5 6" });
            float[,] data = Utility.AscRaster.Read(a, out Utility.AscRaster.Header header, out bool ok);
            Assert.True(ok, "reads a header in another order, with centres and no NODATA_value");
            Assert.Equal(3, header.Ncols, "ncols");
            Assert.Near(100.0, header.XllCorner, 1e-9, "xllcenter becomes the corner");
            Assert.Near(200.0, header.YllCorner, 1e-9, "yllcenter becomes the corner");
            Assert.Near(-9999.0, header.NoDataValue, 1e-9, "default nodata");
            Assert.Near(4.0, data[0, 0], 1e-9, "south-west cell (the last row)");
            Assert.Near(3.0, data[2, 1], 1e-9, "north-east cell (the first row)");
            Utility.AscRaster.Header h2 = Utility.AscRaster.ReadHeader(a, out bool ok2);
            Assert.True(ok2 && h2.Nrows == 2 && h2.CellSizeY == 10.0, "ReadHeader agrees");

            //A first data row that starts with NaN is data, not a header line.
            string b = Path.Combine(s.Folder, "b.asc");
            File.WriteAllLines(b, new[] { "ncols 2", "nrows 2", "xllcorner 0", "yllcorner 0", "cellsize 1", "nan 2", "3 4" });
            float[,] withNan = Utility.AscRaster.Read(b, out Utility.AscRaster.Header hb, out bool okb);
            Assert.True(okb && hb.Nrows == 2, "a NaN-led first row does not end the read");
            Assert.True(float.IsNaN(withNan[0, 1]) && withNan[1, 1] == 2f && withNan[0, 0] == 3f, "and is read as the north row");
        }

        private static void CdfValidation()
        {
            using var s = new Scenario();
            s.Load(Replace("DestinationsCDF", "1,1"), out bool runnable);
            Assert.True(runnable, "a zero-probability destination is legal");
            Assert.True(PREACTInput.Requirements.Any(r => r.Key.EndsWith("DestinationsCDF") && r.Message.Contains("never chosen")), "and is pointed out");

            s.Load(Replace("DestinationsCDF", "0.7,0.4"), out bool _);
            Assert.True(PREACTInput.Requirements.Any(r => r.Key.EndsWith("DestinationsCDF")), "a falling CDF is reported");
        }
    }
}
