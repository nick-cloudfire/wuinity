using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>
    /// The per-user tool settings (Help &gt; External tools and keys, <c>tools.ini</c>): where the file is, that it
    /// round-trips, and that every resolver - ELMFIRE, the GDAL tools, WindNinja, SUMO, PROJ - puts a usable setting
    /// after an explicit scenario or command-line value and before its own search. Every test points
    /// <see cref="ToolPaths.FileVariable"/> at a file of its own and restores the variable afterwards.
    /// </summary>
    internal static class ToolPathsTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("tools: the settings file is PREACT_TOOLS_FILE, else %APPDATA%\\PREACT or the XDG config folder", SettingsFileLocation);
            runner.Add("tools: the settings file round-trips, keeps unknown keys and quotes, and a change on disk is seen", SettingsRoundTrip);
            runner.Add("tools: a path is accepted only when it holds the tool, at whichever level it was picked", Validation);
            runner.Add("tools: every resolver puts a usable setting after the scenario's value and before its own search", ResolverPriority);
            runner.Add("tools: an engine takes SUMO and PROJ from the settings, and a PROJ saved while it runs applies at once", EngineReadsSettings);
            runner.Add("tools: a standalone build's ELMFIRE and fuel tables are found in elmfire/ beside it, before the vendored build", ShippedElmfire);
            runner.Add("gui: Help > External tools is told which path won for each tool, and why a scenario's or a saved one did not", GuiProbe);
            runner.Add("tools: two processes writing the settings file at once lose no update and leave no temporary file", ConcurrentWriters);
        }

        /// <summary>
        /// PREACTtests --tools-ini-writer email|tools &lt;count&gt;: what a second program does to the settings file
        /// (PREACT_TOOLS_FILE) for <see cref="ConcurrentWriters"/> - <paramref name="args"/>[1] writes the LANDFIRE e-mail,
        /// as the fuels step or the migration in a CLI does, or the tool paths, as the tools window does, count times.
        /// </summary>
        internal static int WriterMain(string[] args)
        {
            try
            {
                int count = int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
                for (int i = 0; i < count; ++i)
                {
                    if (args[1] == "email")
                    {
                        ToolPaths.SaveLandfireEmail("writer" + i + "@example.org");
                    }
                    else
                    {
                        ToolPaths.Settings s = ToolPaths.Load();
                        s.Set(ToolPaths.Tool.Elmfire, "/opt/elmfire" + i);
                        ToolPaths.Save(s);
                    }
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 1;
            }
        }

        /// <summary>
        /// Review R2 MI-1: two processes saving the settings file (the GUI's tools window and the e-mail, or its migration in
        /// a CLI) shared one temporary file name and each wrote back what it had read before the other's change: 1-16
        /// exceptions per 400 writes and lost updates.
        /// </summary>
        private static void ConcurrentWriters()
        {
            WithSettingsFile((folder, file) =>
            {
                const int n = 150;
                string me = typeof(ToolPathsTests).Assembly.Location;
                var writers = new List<System.Diagnostics.Process>();
                foreach (string what in new[] { "email", "tools" })
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
                    {
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                    };
                    psi.ArgumentList.Add(me);
                    psi.ArgumentList.Add("--tools-ini-writer");
                    psi.ArgumentList.Add(what);
                    psi.ArgumentList.Add(n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    psi.Environment[ToolPaths.FileVariable] = file;
                    writers.Add(System.Diagnostics.Process.Start(psi));
                }

                foreach (System.Diagnostics.Process w in writers)
                {
                    string errors = w.StandardError.ReadToEnd();
                    Assert.True(w.WaitForExit(120000), "a writer finished");
                    Assert.True(w.ExitCode == 0, "a writer wrote every time without an exception: " + errors);
                    w.Dispose();
                }

                ToolPaths.Settings last = ToolPaths.Load();
                Assert.Equal("writer" + (n - 1) + "@example.org", last.LandfireEmail, "the e-mail writer's last e-mail is in the file");
                Assert.Equal("/opt/elmfire" + (n - 1), last.Get(ToolPaths.Tool.Elmfire), "and so is the tools writer's last path");
                Assert.True(!Directory.GetFiles(Path.GetDirectoryName(file)).Any(f => f.EndsWith(".tmp", StringComparison.Ordinal)),
                    "no temporary file is left");
            });
        }

        private static bool Windows => OperatingSystem.IsWindows();
        private static string Exe(string name) => Windows ? name + ".exe" : name;

        /// <summary>A temporary folder with a settings file named by the variable for the duration of <paramref name="body"/>.</summary>
        private static void WithSettingsFile(Action<string, string> body)
        {
            string folder = Directory.CreateTempSubdirectory("preact-tools-").FullName;
            string before = Environment.GetEnvironmentVariable(ToolPaths.FileVariable);
            string file = Path.Combine(folder, "config", "tools.ini");
            Environment.SetEnvironmentVariable(ToolPaths.FileVariable, file);
            try
            {
                body(folder, file);
            }
            finally
            {
                Environment.SetEnvironmentVariable(ToolPaths.FileVariable, before);
                GdalTools.SearchAgain();
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        private static string Touch(params string[] parts)
        {
            string path = Path.Combine(parts);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "");
            return path;
        }

        private static void SettingsFileLocation()
        {
            Assert.Equal(Path.Combine(@"C:\Users\nick\AppData\Roaming", "PREACT", "tools.ini"),
                ToolPaths.DefaultSettingsFile(true, @"C:\Users\nick\AppData\Roaming", "/xdg", "/home/nick"), "Windows: %APPDATA%\\PREACT\\tools.ini");
            Assert.Equal(Path.Combine("/xdg/config", "PREACT", "tools.ini"),
                ToolPaths.DefaultSettingsFile(false, null, "/xdg/config", "/home/nick"), "elsewhere: $XDG_CONFIG_HOME/PREACT/tools.ini");
            Assert.Equal(Path.Combine("/home/nick", ".config", "PREACT", "tools.ini"),
                ToolPaths.DefaultSettingsFile(false, null, null, "/home/nick"), "no XDG_CONFIG_HOME: ~/.config/PREACT/tools.ini");
            Assert.Equal(Path.Combine("/home/nick", ".config", "PREACT", "tools.ini"),
                ToolPaths.DefaultSettingsFile(false, null, "relative/config", "/home/nick"), "a relative XDG_CONFIG_HOME is ignored, as the spec says");

            string before = Environment.GetEnvironmentVariable(ToolPaths.FileVariable);
            try
            {
                Environment.SetEnvironmentVariable(ToolPaths.FileVariable, null);
                string standard = ToolPaths.SettingsFile;
                Assert.True(standard.EndsWith(Path.Combine("PREACT", "tools.ini")) && Path.IsPathRooted(standard),
                    "without the variable: the per-user file, " + standard);
                Environment.SetEnvironmentVariable(ToolPaths.FileVariable, "\"/somewhere/else/tools.ini\"");
                Assert.Equal("/somewhere/else/tools.ini", ToolPaths.SettingsFile, "the variable wins, quotes and all");
            }
            finally
            {
                Environment.SetEnvironmentVariable(ToolPaths.FileVariable, before);
            }
        }

        private static void SettingsRoundTrip()
        {
            WithSettingsFile((folder, file) =>
            {
                Assert.True(ToolPaths.Load().Get(ToolPaths.Tool.Gdal) == "", "no file: nothing is set");
                Assert.True(ToolPaths.ReadError == null, "and no file is not an error");

                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllLines(file, new[]
                {
                    "; written by hand",
                    "[Tools]",
                    "elmfireexe = \"/opt/elmfire/bin/elmfire\"",
                    "  GdalBin=/usr/bin  ",
                    "# a comment",
                    "FutureKey = kept = as is",
                    "not a key line",
                    "[User]",
                    "FutureUserKey = mine",
                    "[Later]",
                    "Other = 1",
                });
                ToolPaths.Settings read = ToolPaths.Load();
                Assert.Equal("/opt/elmfire/bin/elmfire", read.Get(ToolPaths.Tool.Elmfire), "keys case-insensitively, quotes stripped");
                Assert.Equal("/usr/bin", read.Get(ToolPaths.Tool.Gdal), "white space trimmed");
                Assert.Equal("", read.Get(ToolPaths.Tool.WindNinja), "an absent key is empty");

                int version = ToolPaths.Version;
                //Kept as typed - a field being typed into must keep its trailing space ("C:\Program ") - and cleaned on save.
                read.Set(ToolPaths.Tool.WindNinja, "/opt/Wind Ninja ");
                Assert.Equal("/opt/Wind Ninja ", read.Get(ToolPaths.Tool.WindNinja), "a value is kept as it is typed");
                read.Set(ToolPaths.Tool.WindNinja, " '/opt/WindNinja/bin/WindNinja_cli' ");
                ToolPaths.Save(read);
                Assert.True(ToolPaths.Version > version, "a save is a new version");
                string text = File.ReadAllText(file);
                Assert.True(text.Contains("FutureKey = kept = as is"), "a key this version does not know survives a save");
                int user = text.IndexOf("[User]", StringComparison.Ordinal), later = text.IndexOf("[Later]", StringComparison.Ordinal);
                Assert.True(user > 0 && later > user && text.IndexOf("FutureUserKey = mine", StringComparison.Ordinal) > user
                            && text.IndexOf("Other = 1", StringComparison.Ordinal) > later
                            && text.IndexOf("FutureKey", StringComparison.Ordinal) < user,
                    "each in the section it was read under: " + text);
                Assert.True(text.Contains("WindNinjaExe = /opt/WindNinja/bin/WindNinja_cli"), "the new value is written cleaned: " + text);
                Assert.True(!File.Exists(file + ".tmp"), "nothing left beside it");
                Assert.True(ToolPaths.Load().SameAs(read), "what was saved is what is read back");

                //Another program (or a text editor) changes the file: the next lookup sees it.
                version = ToolPaths.Version;
                File.WriteAllText(file, "[Tools]\nGdalBin = /somewhere/else/entirely/bin\n");
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));
                Assert.Equal("/somewhere/else/entirely/bin", ToolPaths.Load().Get(ToolPaths.Tool.Gdal), "an edit on disk is read");
                Assert.True(ToolPaths.Load().Get(ToolPaths.Tool.Elmfire) == "", "and replaces the old values");
                Assert.True(ToolPaths.Version > version, "and is a new version");
            });
        }

        private static void Validation()
        {
            WithSettingsFile((folder, file) =>
            {
                string elmfire = Touch(folder, "elmfire", "build", Windows ? "windows" : "linux", "bin", Exe("elmfire"));
                string gdal = Touch(folder, "QGIS", "bin", Exe("gdal_translate"));
                string windNinja = Touch(folder, "WindNinja-3.12.1", "bin", Exe("WindNinja_cli"));
                Touch(folder, "WindNinja-3.12.1", "bin", Exe("WindNinja"));
                string sumo = Touch(folder, "Sumo", "bin", Exe("sumo"));
                string proj = Touch(folder, "QGIS", "share", "proj", "proj.db");

                void Ok(ToolPaths.Tool tool, string given, string expected, string what)
                {
                    string problem = ToolPaths.Validate(tool, given, out string normalised);
                    Assert.True(problem == null, what + ": accepted, but " + problem);
                    Assert.True(ToolPaths.SamePath(expected, normalised), what + ": " + expected + " expected, " + normalised);
                }

                void Refused(ToolPaths.Tool tool, string given, string says, string what)
                {
                    string problem = ToolPaths.Validate(tool, given, out string normalised);
                    Assert.True(problem != null && problem.Contains(says) && normalised == null, what + ": refused saying '" + says + "': " + problem);
                }

                Assert.True(ToolPaths.Validate(ToolPaths.Tool.Gdal, "   ", out string none) == null && none == null, "empty means automatic");

                Ok(ToolPaths.Tool.Elmfire, elmfire, elmfire, "ELMFIRE: the executable");
                Ok(ToolPaths.Tool.Elmfire, Path.GetDirectoryName(elmfire), elmfire, "ELMFIRE: its folder");
                Ok(ToolPaths.Tool.Elmfire, Path.Combine(folder, "elmfire"), elmfire, "ELMFIRE: the repository it was built in");
                Refused(ToolPaths.Tool.Elmfire, Path.Combine(folder, "QGIS"), "holds no " + Exe("elmfire"), "ELMFIRE: a folder without it");

                Ok(ToolPaths.Tool.Gdal, Path.GetDirectoryName(gdal), Path.GetDirectoryName(gdal), "GDAL: the bin folder");
                Ok(ToolPaths.Tool.Gdal, Path.Combine(folder, "QGIS"), Path.GetDirectoryName(gdal), "GDAL: the install above it");
                Ok(ToolPaths.Tool.Gdal, "\"" + gdal + "\"", Path.GetDirectoryName(gdal), "GDAL: gdal_translate itself, pasted with quotes");
                Refused(ToolPaths.Tool.Gdal, Path.Combine(folder, "Sumo"), "holds no " + Exe("gdal_translate"), "GDAL: a folder without the tools");

                Ok(ToolPaths.Tool.WindNinja, windNinja, windNinja, "WindNinja: the solver");
                Ok(ToolPaths.Tool.WindNinja, Path.Combine(folder, "WindNinja-3.12.1"), windNinja, "WindNinja: its install folder");
                Refused(ToolPaths.Tool.WindNinja, Path.Combine(Path.GetDirectoryName(windNinja), Exe("WindNinja")), "is not WindNinja's command-line solver",
                    "WindNinja: its window instead of its solver");

                Ok(ToolPaths.Tool.Sumo, Path.Combine(folder, "Sumo"), Path.GetDirectoryName(sumo), "SUMO: SUMO_HOME");
                Ok(ToolPaths.Tool.Sumo, Path.GetDirectoryName(sumo), Path.GetDirectoryName(sumo), "SUMO: its bin");
                Refused(ToolPaths.Tool.Sumo, Path.Combine(folder, "QGIS"), "neither SUMO's bin nor SUMO_HOME", "SUMO: something else");

                Ok(ToolPaths.Tool.Proj, Path.GetDirectoryName(proj), Path.GetDirectoryName(proj), "PROJ: the folder holding proj.db");
                Ok(ToolPaths.Tool.Proj, Path.Combine(folder, "QGIS"), Path.GetDirectoryName(proj), "PROJ: an install with share/proj");
                Ok(ToolPaths.Tool.Proj, proj, Path.GetDirectoryName(proj), "PROJ: proj.db itself");
                Refused(ToolPaths.Tool.Proj, Path.Combine(folder, "Sumo"), "holds no proj.db", "PROJ: a folder without it");

                Refused(ToolPaths.Tool.Gdal, Path.Combine("QGIS", "bin"), "is not a full path", "a relative path");
                Refused(ToolPaths.Tool.Elmfire, Path.Combine(folder, "nowhere", Exe("elmfire")), "is not there", "a path that is not there");
            });
        }

        private static void ResolverPriority()
        {
            WithSettingsFile((folder, file) =>
            {
                //What each finds with no settings file, on this machine.
                GdalTools.SearchAgain();
                string autoElmfire = ElmfireCoupling.ResolveExecutable(null, null);
                string autoGdal = GdalTools.FindBinDirectory();
                string autoWindNinja = WindNinjaRunner.FindExecutable();
                string autoSumo = Engine.FindSumoBinFolder(out ToolPaths.Source autoSumoSource);
                string[] autoProj = ToolPaths.ProjSearchPaths(out ToolPaths.Source autoProjSource, out _);
                Assert.True(autoSumoSource == (autoSumo == null ? ToolPaths.Source.None : ToolPaths.Source.Automatic), "no file: SUMO is searched for");
                Assert.True(autoProjSource != ToolPaths.Source.UserSetting, "no file: PROJ is searched for");

                string elmfire = Touch(folder, "tools", "elmfire", Exe("elmfire"));
                string gdal = Touch(folder, "tools", "gdal", "bin", Exe("gdal_translate"));
                string windNinja = Touch(folder, "tools", "WindNinja", "bin", Exe("WindNinja_cli"));
                string sumo = Touch(folder, "tools", "sumo", "bin", Exe("sumo"));
                string proj = Touch(folder, "tools", "proj", "proj.db");

                var s = new ToolPaths.Settings();
                s.Set(ToolPaths.Tool.Elmfire, elmfire);
                s.Set(ToolPaths.Tool.Gdal, Path.Combine(folder, "tools", "gdal"));
                s.Set(ToolPaths.Tool.WindNinja, Path.Combine(folder, "tools", "WindNinja"));
                s.Set(ToolPaths.Tool.Sumo, Path.Combine(folder, "tools", "sumo"));
                s.Set(ToolPaths.Tool.Proj, Path.GetDirectoryName(proj));
                ToolPaths.Save(s);

                Assert.Equal(elmfire, ElmfireCoupling.ResolveExecutable(null, null), "ELMFIRE: the setting before the search");
                Assert.Equal(elmfire, ElmfireCoupling.ResolveExecutable(folder, ""), "ELMFIRE: an empty scenario key is no key");
                string scenarioElmfire = Touch(folder, "scenario", "my-elmfire", Exe("elmfire"));
                Assert.Equal(scenarioElmfire, ElmfireCoupling.ResolveExecutable(Path.Combine(folder, "scenario"), "my-elmfire/" + Exe("elmfire")),
                    "ELMFIRE: the scenario's [ELMFIRE] ElmfireExe before the setting");
                Assert.True(ElmfireCoupling.ResolveExecutable(folder, "missing/" + Exe("elmfire")) == null,
                    "ELMFIRE: a scenario key that names nothing is reported, not replaced by the setting");
                Assert.True(ToolPaths.Classify(ToolPaths.Tool.Elmfire, elmfire, null) == ToolPaths.Source.UserSetting, "classified as the setting");
                Assert.True(ToolPaths.Classify(ToolPaths.Tool.Elmfire, scenarioElmfire, "my-elmfire") == ToolPaths.Source.Explicit, "classified as the scenario's");

                Assert.Equal(Path.GetDirectoryName(gdal), GdalTools.FindBinDirectory(), "GDAL: the setting, made the bin folder");
                Assert.Equal(windNinja, WindNinjaRunner.FindExecutable(), "WindNinja: the setting, made the solver's path");
                Assert.Equal(Path.GetDirectoryName(sumo), Engine.FindSumoBinFolder(out ToolPaths.Source sumoSource), "SUMO: the setting, made the bin folder");
                Assert.True(sumoSource == ToolPaths.Source.UserSetting, "SUMO: from the setting");
                string[] projPaths = ToolPaths.ProjSearchPaths(out ToolPaths.Source projSource, out string projDetail);
                Assert.True(projPaths.Length == 1 && ToolPaths.SamePath(projPaths[0], Path.GetDirectoryName(proj)) && projSource == ToolPaths.Source.UserSetting,
                    "PROJ: the setting alone, not PROJ_DATA beside it: " + string.Join(";", projPaths) + " (" + projDetail + ")");
                foreach (ToolPaths.Tool tool in ToolPaths.AllTools)
                {
                    Assert.True(ToolPaths.UserSettingProblem(tool) == null, tool + ": a usable setting has no problem");
                }

                //The tools move away: every resolver falls back to its own search, and says why the setting is not used.
                Directory.Delete(Path.Combine(folder, "tools"), true);
                Assert.Equal(autoElmfire, ElmfireCoupling.ResolveExecutable(null, null), "ELMFIRE: a setting that names nothing is skipped");
                Assert.Equal(autoGdal, GdalTools.FindBinDirectory(), "GDAL: skipped");
                Assert.Equal(autoWindNinja, WindNinjaRunner.FindExecutable(), "WindNinja: skipped");
                Assert.Equal(autoSumo, Engine.FindSumoBinFolder(out _), "SUMO: skipped");
                Assert.Equal(string.Join(";", autoProj), string.Join(";", ToolPaths.ProjSearchPaths(out _, out _)), "PROJ: skipped");
                string why = ToolPaths.UserSettingProblem(ToolPaths.Tool.Gdal);
                Assert.True(why != null && why.Contains("is not there"), "and the reason is there to show: " + why);
                Assert.True(ToolPaths.Classify(ToolPaths.Tool.Gdal, autoGdal, null) == (autoGdal == null ? ToolPaths.Source.None : ToolPaths.Source.Automatic),
                    "what is in use then is the automatic search's");
            });
        }

        /// <summary>
        /// What PREACT.exe and every realization of a campaign do at start-up, and what the GUI's Save does while it runs.
        /// The PROJ part changes this process's GDAL and is undone at the end.
        /// </summary>
        private static void EngineReadsSettings()
        {
            Engine main = Program.Engine;
            if (main.GdalLibraryProblem != null)
            {
                throw new TestFailure("GDAL is not loaded in this process: " + main.GdalLibraryProblem);
            }

            string realProj = main.ProjSearchPaths.FirstOrDefault(p => File.Exists(Path.Combine(p, "proj.db")));
            string sumoHomeBefore = Environment.GetEnvironmentVariable("SUMO_HOME");
            WithSettingsFile((folder, file) =>
            {
                try
                {
                    string sumoBin = Path.GetDirectoryName(Touch(folder, "sumo-1.22", "bin", Exe("sumo")));
                    Directory.CreateDirectory(Path.Combine(folder, "sumo-1.22", "data"));
                    //A proj.db that is not one: PROJ then fails every lookup, which shows the setting is what GDAL reads.
                    string brokenProj = Path.GetDirectoryName(Touch(folder, "broken-proj", "proj.db"));

                    var s = new ToolPaths.Settings();
                    s.Set(ToolPaths.Tool.Sumo, Path.Combine(folder, "sumo-1.22"));
                    s.Set(ToolPaths.Tool.Proj, brokenProj);
                    ToolPaths.Save(s);

                    var engine = new Engine(Program.Log, false);
                    Assert.Equal(sumoBin, engine.SumoPath, "SUMO from the setting");
                    Assert.True(engine.SumoSource == ToolPaths.Source.UserSetting, "and said so");
                    Assert.True(ToolPaths.SamePath(Environment.GetEnvironmentVariable("SUMO_HOME"), Path.Combine(folder, "sumo-1.22")),
                        "SUMO_HOME follows a SUMO from the setting, for SUMO's own data");
                    Assert.True(engine.ProjSource == ToolPaths.Source.UserSetting && engine.ProjSearchPaths.Length == 1
                                && ToolPaths.SamePath(engine.ProjSearchPaths[0], brokenProj), "PROJ from the setting");

                    if (realProj == null)
                    {
                        Console.WriteLine("    (no proj.db found among " + string.Join(", ", main.ProjSearchPaths) + "; the live PROJ change is not checked)");
                        return;
                    }

                    Assert.True(!LooksUp(2100), "with the broken proj.db GDAL cannot look up EPSG:2100");

                    s.Set(ToolPaths.Tool.Proj, realProj);
                    ToolPaths.Save(s);
                    Assert.True(engine.ApplyToolSettings() == null, "the saved PROJ is applied without a restart");
                    Assert.True(ToolPaths.SamePath(engine.ProjSearchPaths[0], realProj), "and reported");
                    Assert.True(LooksUp(3035), "and GDAL looks up EPSG:3035 with it, on another thread");
                }
                finally
                {
                    Environment.SetEnvironmentVariable("SUMO_HOME", sumoHomeBefore);
                    if (main.ProjSearchPaths.Length > 0)
                    {
                        OSGeo.OSR.Osr.SetPROJSearchPaths(main.ProjSearchPaths);
                    }
                }
            });
        }

        /// <summary>
        /// build-player.ps1 puts PREACT.exe and PREACTcli in dist/WUInity/PREACT/ and ELMFIRE with its tables in
        /// dist/WUInity/elmfire/; the player's PREACTcore.dll is in dist/WUInity/WUInity_Data/Managed. The same shape is
        /// made here around this test's own PREACTcore.dll (bin/Debug/net8.0): elmfire/ one folder above it.
        /// </summary>
        private static void ShippedElmfire()
        {
            WithSettingsFile((folder, file) =>
            {
                string assemblyDir = Path.GetDirectoryName(typeof(ElmfireCoupling).Assembly.Location);
                string shippedDir = Path.Combine(Path.GetDirectoryName(assemblyDir), "elmfire");
                Assert.True(!Directory.Exists(shippedDir), "nothing is in the way: " + shippedDir);
                try
                {
                    string exe = Touch(shippedDir, Exe("elmfire"));
                    string table = Touch(shippedDir, "fuel_models.csv");
                    string building = Touch(shippedDir, "building_fuel_models.csv");

                    Assert.Equal(exe, ElmfireCoupling.ResolveExecutable(null, null), "the shipped ELMFIRE, before the vendored build");
                    Assert.Equal(table, ElmfireCaseBuilder.DefaultFuelModelTable(exe), "its fuel model table beside it");
                    Assert.Equal(building, ElmfireCaseBuilder.DefaultBuildingFuelModelTable(exe), "and the building one");

                    //A user's setting still comes first.
                    string mine = Touch(folder, "mine", Exe("elmfire"));
                    var s = new ToolPaths.Settings();
                    s.Set(ToolPaths.Tool.Elmfire, mine);
                    ToolPaths.Save(s);
                    Assert.Equal(mine, ElmfireCoupling.ResolveExecutable(null, null), "the setting before the shipped one");
                }
                finally
                {
                    try { Directory.Delete(shippedDir, true); } catch { }
                }
            });
        }

        /// <summary>
        /// The GUI's probe (WUInity/Assets/WUInity/GUI/Workflow/ExternalTools.cs, compiled into the tests): the source of
        /// every tool in use, a scenario key that names nothing, a saved path that holds nothing, and a SUMO saved since
        /// the engine started.
        /// </summary>
        private static void GuiProbe()
        {
            WithSettingsFile((folder, file) =>
            {
                string savedElmfire = Touch(folder, "tools", "elmfire", Exe("elmfire"));
                string gdalBin = Path.GetDirectoryName(Touch(folder, "tools", "QGIS", "bin", Exe("gdal_translate")));
                string newSumoBin = Path.GetDirectoryName(Touch(folder, "tools", "sumo-new", "bin", Exe("sumo")));
                Directory.CreateDirectory(Path.Combine(folder, "tools", "no-windninja-here"));
                string scenario = Path.Combine(folder, "scenario");
                string scenarioElmfire = Touch(scenario, "bin", Exe("elmfire"));

                var s = new ToolPaths.Settings();
                s.Set(ToolPaths.Tool.Elmfire, savedElmfire);
                s.Set(ToolPaths.Tool.Gdal, Path.Combine(folder, "tools", "QGIS"));
                s.Set(ToolPaths.Tool.WindNinja, Path.Combine(folder, "tools", "no-windninja-here"));
                s.Set(ToolPaths.Tool.Sumo, Path.Combine(folder, "tools", "sumo-new"));
                ToolPaths.Save(s);

                var engine = new WUInity.Workflow.EngineToolState
                {
                    SumoBin = "/opt/old-sumo/bin",
                    SumoSource = ToolPaths.Source.Automatic,
                    ProjPaths = new[] { "/usr/share/proj" },
                    ProjSource = ToolPaths.Source.Automatic,
                    ProjDetail = "PROJ_DATA",
                };

                //A scenario that names its own ELMFIRE (which is there) and a GDAL folder (which is not).
                WUInity.Workflow.ExternalToolsSnapshot named = WUInity.Workflow.ExternalTools.Probe(scenario, "bin/" + Exe("elmfire"),
                    "gdal-that-is-not-there", null, engine, "the OPENTOPOGRAPHY_API_KEY environment variable", null);
                Assert.True(named.Probed && named.ProbeError == "", "the probe ran cleanly: " + named.ProbeError);
                Assert.Equal(ToolPaths.SettingsFile, named.SettingsFile, "the window names the file it saves to");
                Assert.True(named.Elmfire.Source == ToolPaths.Source.Explicit && named.ElmfireExe == scenarioElmfire,
                    "ELMFIRE: the scenario's, over the saved one: " + named.Elmfire.InUse + " " + named.Elmfire.Source);
                Assert.Equal(savedElmfire, named.Elmfire.Setting, "and the saved one is shown beside it");
                Assert.True(!named.HaveGdal && named.Gdal.Source == ToolPaths.Source.Explicit
                            && named.Gdal.ExplicitProblem.Contains("PathToGdal names"),
                    "GDAL: a scenario key that names nothing is reported as the problem, not hidden by the setting: " + named.Gdal.ExplicitProblem);
                Assert.True(named.WindNinja.SettingProblem.Contains("holds no " + Exe("WindNinja_cli")) && named.WindNinja.Source != ToolPaths.Source.UserSetting,
                    "WindNinja: a saved folder without the solver is said not to be used: " + named.WindNinja.SettingProblem);
                Assert.True(named.Sumo.InUse == "/opt/old-sumo/bin" && named.Sumo.Source == ToolPaths.Source.Automatic,
                    "SUMO: what the engine started with");
                Assert.True(named.Sumo.NeedsRestart && ToolPaths.SamePath(named.Sumo.AfterRestart, newSumoBin),
                    "SUMO: the saved one waits for a restart: " + named.Sumo.AfterRestart);
                Assert.True(named.Proj.InUse == "/usr/share/proj" && named.Proj.Detail == "PROJ_DATA" && !named.Proj.NeedsRestart,
                    "PROJ: what GDAL has, from where");
                Assert.True(named.HaveOpenTopographyKey && named.MapboxTokenValid == null, "the keys are passed through");

                //The same machine with a scenario that names nothing: the saved paths win.
                WUInity.Workflow.ExternalToolsSnapshot plain = WUInity.Workflow.ExternalTools.Probe(scenario, "", "", "", engine, "", null);
                Assert.True(plain.Elmfire.Source == ToolPaths.Source.UserSetting && plain.ElmfireExe == savedElmfire, "ELMFIRE: the saved one");
                Assert.True(plain.Gdal.Source == ToolPaths.Source.UserSetting && ToolPaths.SamePath(plain.GdalBin, gdalBin), "GDAL: the saved bin");
                Assert.True(!plain.HaveOpenTopographyKey, "no key is no key");
            });
        }

        /// <summary>Whether GDAL can make the CRS on a fresh thread pool thread (PROJ contexts are per thread).</summary>
        /// <summary>
        /// Whether GDAL can look up <paramref name="epsg"/>, on a thread of its own.
        /// </summary>
        /// <remarks>
        /// A new thread, not a pooled one: GDAL keeps the CRSs a thread has made from EPSG codes in a per-thread cache, so a
        /// pool thread that another test had used for EPSG:2100 (Mati's grid) answered from that cache whatever proj.db
        /// said, and the "broken proj.db" check failed now and then.
        /// </remarks>
        private static bool LooksUp(int epsg)
        {
            bool found = false;
            var thread = new Thread(() =>
            {
                //The failure is expected half of the time; GDAL need not print it.
                OSGeo.GDAL.Gdal.PushErrorHandler("CPLQuietErrorHandler");
                try
                {
                    using var srs = new OSGeo.OSR.SpatialReference("");
                    found = srs.ImportFromEPSG(epsg) == 0 && !string.IsNullOrEmpty(srs.GetName());
                }
                catch
                {
                    found = false;
                }
                finally
                {
                    OSGeo.GDAL.Gdal.PopErrorHandler();
                }
            });
            thread.Start();
            thread.Join();
            return found;
        }
    }
}
