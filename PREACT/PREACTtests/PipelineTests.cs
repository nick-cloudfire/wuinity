using System.Diagnostics;
using System.Globalization;
using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>
    /// The case-building and run-preparation half of the ELMFIRE pipeline, on a small synthetic case: a UTM DEM and a
    /// fuel raster written here, no network, no WindNinja, no ELMFIRE.
    /// </summary>
    internal static class PipelineTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("cli: build-case loads its GDAL wrappers itself (only libgdal on the loader path)", CliFindsGdalWrappers);
            runner.Add("builder: a re-cut grid carries the rasters a hand-edited namelist names (Mati's fbfm40_roads101)", RecutCarriesNamelistRasters);
            runner.Add("preflight: a raster the namelist names off the case grid is refused by key, for a run and a campaign", OffGridRasterRefused);
            runner.Add("paths: Windows backslash [ELMFIRE] paths resolve for the case, its sources and its template", BackslashElmfirePaths);
            runner.Add("preflight: a missing ELMFIRE is reported with the path that was tried", MissingElmfireNamed);
            runner.Add("weather: a user's archive is never rewritten; the build works on a copy", ArchiveWorkingCopy);
            runner.Add("builder: the case grid covers the whole padded domain, in whole cells", GridCoversPaddedDomain);
            runner.Add("builder: a painting that records its grid is placed only on that grid, not on any of its size", PaintingPosition);
            runner.Add("builder: a case is not rebuilt under a running campaign, from the GUI or the CLI", NoBuildUnderCampaign);
            runner.Add("builder: a stopped build kills WindNinja, starts no other and writes no wind (no uniform field)", StoppedBuildWritesNoWind);
            runner.Add("coupling: BuildCaseOnly stopped during WindNinja reports it stopped and leaves the scenario alone", StoppedBuildCaseOnly);
            runner.Add("builder: a grid set aside by a build that then failed is carried by the next build", FailedRecutIsResumed);
            runner.Add("coupling: a template's weather band keys are fitted to the case's ws.tif; a fire longer than it is refused naming both", TemplateBandsFitted);
            runner.Add("builder: the building spread model gets ELMFIRE's building fuel table, or the build and the run are refused", BuildingFuelTable);
            runner.Add("coupling: a single ELMFIRE run gives k-PERIL the case's own dem/slp/asp, which cover the whole fire grid", FireTerrainForKperil);
            runner.Add("cli: build-case prints the keys to put in the .wui; --update-wui writes exactly those and nothing else", BuildCaseUpdatesWui);
            runner.Add("builder: a namelist's raster whose re-cut fails is still in inputs, as it was", FailedRecutKeepsOriginal);
            runner.Add("builder: a raster both a namelist and the scenario name is re-cut into a copy, never in place", ScenarioRasterNotRecutInPlace);
        }

        /// <summary>
        /// Review RC-MI-1: the carry moved an off-grid raster a namelist names into _previous_grid before warping the re-cut
        /// copy, so a warp that failed (a GDAL error, a full disk) left inputs/ without it, and the next build had nothing to
        /// carry. The warp is made into a file of its own first.
        /// </summary>
        private static void FailedRecutKeepsOriginal()
        {
            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                string inputs = Path.Combine(caseDir, "inputs");
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, new List<string>())).GetAwaiter().GetResult();

                //A raster the namelist names that GDAL cannot warp: the re-cut fails every time.
                string bad = ElmfireStems.Tif(inputs, "fbfm40_bad");
                File.WriteAllText(bad, "not a GeoTIFF");
                string namelist = Path.Combine(caseDir, "elmfire.data");
                File.WriteAllLines(namelist, ElmfireNamelist.SetKeyInGroup(File.ReadAllLines(namelist),
                    ElmfireNamelistKeys.InputsGroup, "FBFM_FILENAME", "fbfm40_bad", quoted: true));

                var log = new List<string>();
                Exception failed = null;
                try { ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, log)).GetAwaiter().GetResult(); }
                catch (Exception e) { failed = e; }
                Assert.True(failed != null && log.Any(l => l.Contains("fbfm40_bad: warping onto the master grid")),
                    "the build tried to re-cut it and failed: " + failed?.Message);
                Assert.True(File.Exists(bad) && File.ReadAllText(bad) == "not a GeoTIFF", "the raster is still in inputs/, as it was");
                Assert.True(!Directory.GetFiles(inputs).Any(f => f.Contains("recut")), "and no half-written re-cut is left beside it");
                Assert.True(!File.Exists(ElmfireStems.Tif(Path.Combine(inputs, ElmfireCaseBuilder.PreviousGridFolder), "fbfm40_bad")),
                    "nor was it moved aside");
            }
        }

        /// <summary>
        /// Review RC-MI-2: a kept or template namelist naming SLP_FILENAME = 'mati_slope', where the scenario's [Landscape]
        /// SlopeFile is elmfire/inputs/mati_slope.tif, would have moved the scenario's raster aside and replaced it with a
        /// re-cut copy, so the scenario read another raster than the one it names.
        /// </summary>
        private static void ScenarioRasterNotRecutInPlace()
        {
            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                string inputs = Path.Combine(caseDir, "inputs");
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, new List<string>())).GetAwaiter().GetResult();
                MasterGrid grid = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, ElmfireStems.Dem));

                //The scenario's own slope, in the case's inputs and on another grid (Nick's mati_slope.tif is 616x590).
                var other = new MasterGrid
                {
                    Header = new AscRaster.Header { Ncols = 10, Nrows = 10, CellSize = 30, CellSizeY = 30, XllCorner = grid.XMin + 300, YllCorner = grid.YMin + 300, NoDataValue = -9999 },
                    Epsg = grid.Epsg,
                };
                var values = new float[10, 10];
                for (int x = 0; x < 10; ++x) for (int y = 0; y < 10; ++y) values[x, y] = 7f;
                string slope = ElmfireStems.Tif(inputs, "mati_slope");
                GeoTiffRasterWriter.WriteBand(other, values, slope);
                string before = ElmfireFingerprint.HashFile(slope);

                File.WriteAllLines(Path.Combine(caseDir, "hand.data"), ElmfireNamelist.SetKeyInGroup(File.ReadAllLines(Path.Combine(caseDir, "elmfire.data")),
                    ElmfireNamelistKeys.InputsGroup, "SLP_FILENAME", "mati_slope", quoted: true));
                string wui = c.WriteScenario("case", 150.0, "NamelistTemplate=hand.data", "", "[Landscape]", "ElevationFile=source_dem.tif",
                    "SlopeFile=case/inputs/mati_slope.tif");
                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);

                var log = new List<string>();
                ElmfireCaseBuilder.Options o = ElmfireCoupling.CreateBuildOptions(input, input.WildfireModule.ElmfireInput, caseDir, m => log.Add(m));
                o.LocalDemPath = c.DemPath;
                o.Weather.UseClimatology = false;
                o.Weather.WindNinjaExe = Path.Combine(c.Folder, "no-windninja-here");
                Assert.True(o.ScenarioFiles.Any(f => f.EndsWith("mati_slope.tif", StringComparison.Ordinal)), "the scenario's files are known to the build");
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();

                string copyStem = $"mati_slope_{grid.Header.Ncols}x{grid.Header.Nrows}";
                Assert.Equal(before, ElmfireFingerprint.HashFile(slope), "the scenario's mati_slope.tif is byte for byte what it was");
                MasterGrid copy = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, copyStem));
                Assert.True(copy.Header.Ncols == grid.Header.Ncols && copy.Header.Nrows == grid.Header.Nrows, "its copy is on the case grid");
                Assert.True(r.Fallbacks.Any(f => f.Contains($"Set SLP_FILENAME = '{copyStem}'")) && log.Any(l => l.Contains("WARNING") && l.Contains(copyStem)),
                    "and the build says which key to point at it: " + string.Join(" | ", r.Fallbacks));
            }
        }

        /// <summary>
        /// e2e N7's other half: the CLI printed the C1 keys as "[Landscape] ElevationFile=..." lines and left the .wui alone,
        /// so a scenario built from the command line kept its old landscape. It now prints them as they go into the file,
        /// and <c>--update-wui</c> writes them - only them - as the GUI's Build fire case does.
        /// </summary>
        private static void BuildCaseUpdatesWui()
        {
            using (var c = new SyntheticCase())
            {
                string wui = c.WriteScenario("case", 150.0, "", "# the landscape the scenario was drawn on",
                    "[Landscape]", "ElevationFile=source_dem.tif", "AKeyNobodyReads=kept", "", "[kPERIL]", "WuiAreaSource=Raster");
                File.WriteAllText(wui, string.Join("\r\n", File.ReadAllLines(wui)) + "\r\n"); //written on Windows
                string before = File.ReadAllText(wui);
                string[] args = { "build-case", "--wui", wui, "--dem", c.DemPath, "--no-climatology", "--windninja", Path.Combine(c.Folder, "no-windninja-here") };

                (int exit, string output) = RunCli(c.Folder, args);
                Assert.Equal(0, exit, "build-case (" + Tail(output) + ")");
                string printed = output.Replace("\r", "");
                Assert.True(printed.Contains("[Landscape]\nElevationFile=case/inputs/dem.tif\nSlopeFile=case/inputs/slp.tif\nAspectFile=case/inputs/asp.tif\n"),
                    "the keys, as they go into the file: " + Tail(output, 14));
                Assert.True(printed.Contains("--update-wui"), "and the flag that writes them");
                Assert.Equal(before, File.ReadAllText(wui), "without the flag the .wui is not touched");

                (exit, output) = RunCli(c.Folder, args.Concat(new[] { "--update-wui" }).ToArray());
                Assert.Equal(0, exit, "build-case --update-wui (" + Tail(output) + ")");
                string[] after = File.ReadAllLines(wui);
                Assert.True(after.Contains("ElevationFile=case/inputs/dem.tif") && after.Contains("SlopeFile=case/inputs/slp.tif")
                            && after.Contains("AspectFile=case/inputs/asp.tif") && !after.Contains("ElevationFile=source_dem.tif"),
                    "the landscape is the case's: " + string.Join(" | ", after.Where(l => l.EndsWith(".tif", StringComparison.Ordinal))));
                Assert.True(after.Contains("# the landscape the scenario was drawn on") && after.Contains("AKeyNobodyReads=kept")
                            && after.Contains("WuiAreaSource=Raster"), "every other line stays as it was");
                string[] was = before.Replace("\r", "").Split('\n').Where(l => l.Length > 0).ToArray();
                Assert.Equal(was.Length + 2, after.Count(l => l.Length > 0), "two keys added (slope, aspect), one replaced");

                Assert.True(!File.ReadAllText(wui).Replace("\r\n", "").Contains('\n'), "with the file's own CRLF line endings");

                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                Assert.Equal("case/inputs/dem.tif", input.Landscape.ElevationFile, "and the scenario reads it");

                (exit, output) = RunCli(c.Folder, args);
                Assert.True(exit == 0 && output.Contains("already points at this case"), "built again, nothing is left to record: " + Tail(output));
            }
        }

        /// <summary>
        /// A stand-in ELMFIRE that "burns" by copying <paramref name="fixture"/>'s rasters into outputs/ and printing the
        /// lines ELMFIRE ends a good run with.
        /// </summary>
        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private static string SucceedingElmfire(string folder, string fixture)
        {
            string path = Path.Combine(folder, "elmfire_that_copies.sh");
            File.WriteAllText(path, "#!/bin/sh\ncp '" + fixture + "'/*.tif outputs/\n"
                                    + "echo '[1] Meteorology band      1: Case #       1 complete.  Fire area:   5.0 acres.'\n"
                                    + "echo ' End of simulation reached successfully. Shutting down.'\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        /// <summary>
        /// e2e N7: Nick's mati.wui built through the CLI kept [Landscape] at the 616x590 mati_dem.tif, so a single run
        /// sampled k-PERIL's topography over 63.9 % of the fire grid and treated the rest as flat, while every campaign
        /// realization used the case's dem/slp/asp. A single ELMFIRE run now hands k-PERIL the terrain its fire burned on.
        /// </summary>
        private static void FireTerrainForKperil()
        {
            if (OperatingSystem.IsWindows()) return;

            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                string inputs = Path.Combine(caseDir, "inputs");
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, new List<string>())).GetAwaiter().GetResult();
                MasterGrid grid = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, ElmfireStems.Dem));

                string fixture = Path.Combine(c.Folder, "fire");
                Directory.CreateDirectory(fixture);
                foreach (string stem in new[] { ElmfireStems.TimeOfArrival, ElmfireStems.SpreadRate, ElmfireStems.SpreadDirection, ElmfireStems.MidflameWindSpeed })
                {
                    File.Copy(ElmfireStems.Tif(inputs, ElmfireStems.Dem), Path.Combine(fixture, stem + "_0000001_0003600.tif"));
                }
                string fake = SucceedingElmfire(c.Folder, fixture);

                //The scenario's own landscape: a small DEM over one corner of the domain, as mati_dem.tif covers part of Mati's.
                string small = Path.Combine(c.Folder, "small_dem.tif");
                var corner = new MasterGrid
                {
                    Header = new AscRaster.Header { Ncols = 20, Nrows = 20, CellSize = 30, CellSizeY = 30, XllCorner = grid.XMin, YllCorner = grid.YMin, NoDataValue = -9999 },
                    Epsg = grid.Epsg,
                };
                var heights = new float[20, 20];
                for (int x = 0; x < 20; ++x) for (int y = 0; y < 20; ++y) heights[x, y] = 150f;
                GeoTiffRasterWriter.WriteBand(corner, heights, small);

                string wui = c.WriteScenario("case", 150.0, "", "[Landscape]", "ElevationFile=small_dem.tif");
                File.WriteAllLines(wui, File.ReadAllLines(wui)
                    .Select(l => l == "BuildCase=true" ? "BuildCase=false\nReuseExistingOutput=false\nElmfireExe=" + fake : l)
                    .SelectMany(l => l.Split('\n')));
                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);

                ElmfireCoupling.Result run = ElmfireCoupling.Prepare(input, input.WildfireModule.ElmfireInput, null);
                Assert.True(run.Ok, "the stand-in ELMFIRE's fire is read: " + run.Message);
                Assert.Equal("case/inputs/dem.tif", run.ElevationFile, "the fire's own DEM");
                Assert.Equal("case/inputs/slp.tif", run.SlopeFile, "slope");
                Assert.Equal("case/inputs/asp.tif", run.AspectFile, "aspect");

                //Sampled onto the fire grid (the case grid, in simulation coordinates) the way k-PERIL's are.
                Math.Vector2d origin = input.Simulation.Data.UTMOrigin;
                var offset = new Math.Vector2d(grid.XMin - origin.x, grid.YMin - origin.y);
                var size = new Math.Vector2d(grid.Header.Ncols * grid.Header.CellSize, grid.Header.Nrows * grid.Header.CellSize);
                string Coverage(Wildfire.LandscapeData landscape)
                {
                    Program.Log.Take();
                    Assert.True(Evacuation.EvacuationManager.TrySampleTopographyOntoGrid(landscape, offset, size, grid.Header.Ncols, grid.Header.Nrows,
                        out float[,] _, out float[,] _, out float[,] _), "topography sampled");
                    string line = Program.Log.Take().FirstOrDefault(m => m.Contains("% covered"));
                    return line ?? "(no coverage line)";
                }

                var fire = new Wildfire.FireWeatherRasters
                {
                    ElevationFile = Path.Combine(c.Folder, run.ElevationFile),
                    SlopeFile = Path.Combine(c.Folder, run.SlopeFile),
                    AspectFile = Path.Combine(c.Folder, run.AspectFile),
                };
                Wildfire.LandscapeData terrain = Evacuation.EvacuationManager.LoadFireTerrain(fire, origin);
                Assert.True(terrain != null, "the fire's terrain loads as a landscape");
                string own = Coverage(terrain);
                Assert.True(own.Contains("(100.0% covered)"), "the fire's own terrain covers the whole fire grid: " + own);
                string scenario = Coverage(input.WildfireModule.Data.LandscapeData);
                Assert.True(!scenario.Contains("(100.0% covered)"), "which the scenario's small landscape did not: " + scenario);
            }
        }

        /// <summary>
        /// A stand-in ELMFIRE tree, <c>build/linux/bin/&lt;exe&gt;</c> beside <c>build/source/</c>, whose executable fails at once;
        /// with <paramref name="tables"/> the source folder holds ELMFIRE's two default tables.
        /// </summary>
        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private static string FakeElmfireTree(string folder, bool tables)
        {
            string bin = Path.Combine(folder, "build", "linux", "bin");
            string source = Path.Combine(folder, "build", "source");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(source);
            if (tables)
            {
                File.WriteAllText(Path.Combine(source, ElmfireStems.FuelModelTable), "1,GR1,0.1,0,0,0.3,0,1,15,2000,1800,1800,0.4,8000,8000,0\n");
                File.WriteAllText(Path.Combine(source, ElmfireStems.BuildingFuelModelTable), "1,ST01,300,400,10080,14400,360,25,10500,9,0.89,8,0.5,100,0.0,20\n");
            }
            return FailingElmfire(bin);
        }

        /// <summary>
        /// e2e N2: with the building spread model on (Mati's setting), a case without building_fuel_models.csv passed its
        /// build and its own validation, and ELMFIRE then stopped at start-up: "Problem opening building fuel model table
        /// file ./inputs/building_fuel_models.csv". The builder copies ELMFIRE's shipped table like fuel_models.csv, or
        /// refuses before it makes anything; a run of a namelist that needs it gets it or is refused before ELMFIRE.
        /// </summary>
        private static void BuildingFuelTable()
        {
            if (OperatingSystem.IsWindows()) return;

            using (var c = new SyntheticCase())
            {
                string withTables = FakeElmfireTree(Path.Combine(c.Folder, "elmfire_full"), true);
                string withoutTables = FakeElmfireTree(Path.Combine(c.Folder, "elmfire_bare"), false);
                string caseDir = Path.Combine(c.Folder, "case");
                string inputs = Path.Combine(caseDir, "inputs");
                string table = Path.Combine(inputs, ElmfireStems.BuildingFuelModelTable);

                ElmfireCaseBuilder.Options Buildings(string exe)
                {
                    ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, new List<string>());
                    o.ElmfireExe = exe;
                    o.Namelist.USE_BLDG_SPREAD_MODEL = true;
                    o.Namelist.USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS = true;
                    return o;
                }

                // ---- no table anywhere: refused before anything is made
                Exception refused = null;
                try { ElmfireCaseBuilder.Build(Buildings(withoutTables)).GetAwaiter().GetResult(); }
                catch (InvalidDataException e) { refused = e; }
                Assert.True(refused != null && refused.Message.Contains("USE_BLDG_SPREAD_MODEL") && refused.Message.Contains("--copy"),
                    "the build is refused, saying what to do: " + refused?.Message);
                Assert.True(!File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.Dem)), "before it made the grid");

                // ---- ELMFIRE's table beside the executable: copied, and named in the namelist
                ElmfireCaseBuilder.Build(Buildings(withTables)).GetAwaiter().GetResult();
                Assert.True(File.Exists(table), "building_fuel_models.csv is in the case's inputs");
                string[] namelist = File.ReadAllLines(Path.Combine(caseDir, "elmfire.data"));
                Assert.True(ElmfireNamelist.IsTrue(ElmfireNamelist.GetKeyInGroup(namelist, ElmfireNamelistKeys.WuiGroup, ElmfireNamelistKeys.UseBuildingSpreadModel)),
                    "the namelist runs the building spread model");
                Assert.Equal(ElmfireStems.BuildingFuelModelTable, ElmfireNamelist.GetKeyInGroup(namelist, ElmfireNamelistKeys.MiscellaneousGroup,
                    ElmfireNamelistKeys.BuildingFuelModelFile), "and names the table");

                // ---- a run whose namelist needs it, after the table went missing
                File.Delete(table);
                string wui = c.WriteScenario("case", 150.0);
                string[] scenario = File.ReadAllLines(wui);
                Input.PREACTInput RunWith(string exe)
                {
                    File.WriteAllLines(wui, scenario
                        .Select(l => l == "BuildCase=true" ? "BuildCase=false\nReuseExistingOutput=false\nElmfireExe=" + exe : l)
                        .SelectMany(l => l.Split('\n')));
                    return Input.PREACTInput.LoadFromDisk(wui, out bool _);
                }

                Input.PREACTInput bare = RunWith(withoutTables);
                ElmfireCoupling.Result run = ElmfireCoupling.Prepare(bare, bare.WildfireModule.ElmfireInput, null);
                Assert.True(!run.Ok && run.Message.Contains("building spread model") && run.Message.Contains(ElmfireStems.BuildingFuelModelTable),
                    "without ELMFIRE's table the run is refused before ELMFIRE: " + run.Message);
                Assert.True(!File.Exists(Path.Combine(caseDir, "outputs", ElmfireRunner.RunNamelistName)), "which was not started");

                Input.PREACTInput full = RunWith(withTables);
                run = ElmfireCoupling.Prepare(full, full.WildfireModule.ElmfireInput, null);
                Assert.True(!run.Ok && run.Message.Contains("fake ELMFIRE"), "with it, the run reaches ELMFIRE: " + run.Message);
                Assert.True(File.Exists(table), "having copied the table in again");
            }
        }

        /// <summary>A stand-in for ELMFIRE that fails at once, so the namelist a run would hand it can be read in outputs/run.data.</summary>
        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private static string FailingElmfire(string folder)
        {
            string path = Path.Combine(folder, "elmfire_that_fails.sh");
            File.WriteAllText(path, "#!/bin/sh\necho '[ERROR] fake ELMFIRE'\nexit 1\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        /// <summary>
        /// RC-MA-1 / e2e N1: Nick's kept hand-edited namelist says NUM_METEOROLOGY_TIMES = METEOROLOGY_BAND_STOP = 72, and a
        /// single run of it on a case whose weather had 8 or 24 bands (the scenario's hours) aborted ELMFIRE at start-up:
        /// "slice band end (72) is outside the bounds of (1, 8)". The run now fits the band keys to the case's ws.tif as a
        /// campaign does per realization, and checks the namelist's demand against it before ELMFIRE starts.
        /// </summary>
        private static void TemplateBandsFitted()
        {
            if (OperatingSystem.IsWindows()) return;

            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, new List<string>());
                o.SimulationTstopSeconds = 3 * 3600.0;
                ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();
                Assert.Equal(3, AscRaster.GetBandCount(ElmfireStems.Tif(Path.Combine(caseDir, "inputs"), ElmfireStems.WindSpeed)),
                    "the case holds 3 hours of weather");

                //As Nick's kept namelist: written for a 72-band case.
                string[] hand = File.ReadAllLines(Path.Combine(caseDir, "elmfire.data"));
                foreach (string key in new[] { ElmfireNamelistKeys.NumMeteorologyTimes, ElmfireNamelistKeys.MeteorologyBandStop })
                {
                    hand = ElmfireNamelist.SetKeyInGroup(hand, ElmfireNamelistKeys.MonteCarloGroup, key, "72");
                }
                File.WriteAllLines(Path.Combine(caseDir, "hand.data"), hand);

                string fake = FailingElmfire(c.Folder);
                string wui = c.WriteScenario("case", 150.0);
                File.WriteAllLines(wui, File.ReadAllLines(wui)
                    .Select(l => l == "BuildCase=true"
                        ? "BuildCase=false\nNamelistTemplate=hand.data\nReuseExistingOutput=false\nElmfireExe=" + fake
                        : l == "SimulationTstopHours=1" ? "SimulationTstopHours=2" : l)
                    .SelectMany(l => l.Split('\n')));
                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                Assert.True(input?.WildfireModule?.ElmfireInput != null, "the scenario loads");

                var log = new List<string>();
                ElmfireCoupling.Result run = ElmfireCoupling.Prepare(input, input.WildfireModule.ElmfireInput, m => log.Add(m));
                string runData = Path.Combine(caseDir, "outputs", ElmfireRunner.RunNamelistName);
                Assert.True(!run.Ok && File.Exists(runData) && run.Message.Contains("fake ELMFIRE"),
                    "the 2 h fire on 3 bands reaches ELMFIRE (the fake one, which fails): " + run.Message);

                string[] ran = File.ReadAllLines(runData);
                string Key(string key) => ElmfireNamelist.GetKeyInGroup(ran, ElmfireNamelistKeys.MonteCarloGroup, key);
                Assert.Equal("3", Key(ElmfireNamelistKeys.NumMeteorologyTimes), "NUM_METEOROLOGY_TIMES = the bands ws.tif has");
                Assert.Equal("1", Key(ElmfireNamelistKeys.MeteorologyBandStart), "METEOROLOGY_BAND_START");
                Assert.Equal("1", Key(ElmfireNamelistKeys.MeteorologyBandStop), "METEOROLOGY_BAND_STOP: one starting band");
                Assert.True(log.Any(l => l.Contains("fitted to ws.tif (3 band(s))") && l.Contains("NUM_METEOROLOGY_TIMES: '72' -> '3'")),
                    "and the run says what it changed: " + string.Join(" | ", log.Where(l => l.Contains("band"))));
                Assert.True(ElmfireNamelist.DescribeBandShortfall(ran, 3) == null, "the fitted namelist asks for no more than there is");

                //A fire longer than the weather: refused before ELMFIRE starts, with both numbers.
                File.Delete(runData);
                input.WildfireModule.ElmfireInput.SimulationTstopHours = 5;
                run = ElmfireCoupling.Prepare(input, input.WildfireModule.ElmfireInput, null);
                Assert.True(!run.Ok && run.Message.Contains("3 bands") && run.Message.Contains("5 h") && run.Message.Contains("NamelistTemplate"),
                    "5 h on 3 bands is refused, naming both: " + run.Message);
                Assert.True(!File.Exists(runData), "before ELMFIRE was asked");

                //The demand, as the check words it: the stop time, or a band key a namelist was not fitted to.
                string[] fiveHours = ElmfireNamelist.SetKeyInGroup(ran, ElmfireNamelistKeys.TimeControlGroup,
                    ElmfireNamelistKeys.SimulationTstop, "18000.0");
                string shortfall = ElmfireNamelist.DescribeBandShortfall(fiveHours, 3);
                Assert.True(shortfall != null && shortfall.Contains("needs 5 weather band(s)") && shortfall.Contains("ws.tif has 3"),
                    "the stop time's demand: " + shortfall);
                string unfitted = ElmfireNamelist.DescribeBandShortfall(hand, 3);
                Assert.True(unfitted != null && unfitted.Contains("needs 72") && unfitted.Contains("= 72"), "an unfitted band key's: " + unfitted);
                Assert.True(ElmfireNamelist.DescribeBandShortfall(hand, 1) == null, "one band is constant weather, enough for any fire");
            }
        }

        /// <summary>
        /// A re-cut sets the old grid aside first; a build that fails after that (no DEM to cut the new grid from, as on
        /// Mati without an OpenTopography key) used to strand its layers in inputs/_previous_grid for good.
        /// </summary>
        private static void FailedRecutIsResumed()
        {
            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                string inputs = Path.Combine(caseDir, "inputs");
                string previous = Path.Combine(inputs, ElmfireCaseBuilder.PreviousGridFolder);
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, new List<string>())).GetAwaiter().GetResult();
                MasterGrid first = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, ElmfireStems.Dem));

                //A layer only the case has: made by hand, no source to warp it from again. (Barriers: the WUI area this
                //used is made from the evacuation groups on every build now, and never carried.)
                var wui = new float[first.Header.Ncols, first.Header.Nrows];
                for (int x = 10; x < 20; ++x) for (int y = 10; y < 20; ++y) wui[x, y] = 1f;
                GeoTiffRasterWriter.WriteBand(first, wui, ElmfireStems.Tif(inputs, "barriers"));

                //More padding re-cuts the grid; with no DEM and no key the build fails after setting the old grid aside.
                ElmfireCaseBuilder.Options failing = c.Options(caseDir, 600.0, new List<string>());
                failing.LocalDemPath = null;
                File.Delete(Path.Combine(inputs, "dem_source.tif"));
                failing.OpenTopographyApiKey = null;
                bool failed = false;
                try { ElmfireCaseBuilder.Build(failing).GetAwaiter().GetResult(); }
                catch (Exception) { failed = true; }
                Assert.True(failed, "the build without a DEM fails");
                Assert.True(!File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.Dem)), "after setting the old grid aside (no dem.tif)");
                Assert.True(File.Exists(ElmfireStems.Tif(previous, "barriers"))
                            && File.Exists(Path.Combine(previous, ElmfireCaseBuilder.CarryPendingMarker)), "the barriers wait there, marked");

                var log = new List<string>();
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(c.Options(caseDir, 600.0, log)).GetAwaiter().GetResult();
                MasterGrid second = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, ElmfireStems.Dem));
                Assert.True(second.Header.Ncols > first.Header.Ncols, "the next build re-cuts the grid");
                Assert.True(log.Any(l => l.Contains("did not finish carrying")), "saying it resumes the carry");
                Assert.True(r.Carried.Contains("barriers"), "and carries the barriers: " + string.Join(",", r.Carried));
                float[,] carried = AscRaster.ReadGeoTiff(ElmfireStems.Tif(inputs, "barriers"), out AscRaster.Header h, out bool ok);
                int cells = 0;
                for (int x = 0; x < h.Ncols; ++x) for (int y = 0; y < h.Nrows; ++y) if (carried[x, y] > 0.5f) ++cells;
                Assert.True(ok && h.Ncols == second.Header.Ncols && cells == 100, $"onto the new grid, all 100 cells ({cells})");
                Assert.True(!File.Exists(Path.Combine(previous, ElmfireCaseBuilder.CarryPendingMarker)), "and the set-aside is no longer pending");

                //A build after that has nothing pending: it keeps the case as it is.
                var again = new List<string>();
                ElmfireCaseBuilder.Build(c.Options(caseDir, 600.0, again)).GetAwaiter().GetResult();
                Assert.True(!again.Any(l => l.Contains("did not finish carrying")), "nothing is carried twice");
            }
        }

        /// <summary>
        /// A stand-in for WindNinja_cli: it notes each start in <paramref name="starts"/> and then sleeps, so a test
        /// can stop a build while a solve is under way. Linux (the bench); a shell script is not an executable on Windows.
        /// </summary>
        [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
        private static string FakeWindNinja(string folder, string starts)
        {
            string path = Path.Combine(folder, "fake_windninja.sh");
            File.WriteAllText(path, "#!/bin/sh\necho started >> '" + starts + "'\nsleep 60\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        /// <summary>Calls <see cref="ElmfireRunner.CancelAll"/> once the fake WindNinja has started (or after 60 s).</summary>
        private static Task StopWhenWindNinjaStarts(string starts)
        {
            return Task.Run(() =>
            {
                var wait = Stopwatch.StartNew();
                while (!File.Exists(starts) && wait.Elapsed.TotalSeconds < 60) Thread.Sleep(50);
                Thread.Sleep(200);
                ElmfireRunner.CancelAll();
            });
        }

        private static int Starts(string starts) => File.Exists(starts) ? File.ReadAllLines(starts).Length : 0;

        private static void StoppedBuildWritesNoWind()
        {
            if (OperatingSystem.IsWindows()) return;

            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                string starts = Path.Combine(c.Folder, "windninja_starts.txt");
                var log = new List<string>();
                ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, log);
                o.SimulationTstopSeconds = 3 * 3600.0;
                o.Weather.WindNinjaExe = FakeWindNinja(c.Folder, starts);
                long generation = ElmfireProcesses.Generation;
                o.Cancelled = () => ElmfireProcesses.CancelledSince(generation);

                Task stopper = StopWhenWindNinjaStarts(starts);
                var clock = Stopwatch.StartNew();
                Exception thrown = null;
                try
                {
                    ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    thrown = e;
                }
                stopper.Wait();
                string inputs = Path.Combine(caseDir, "inputs");

                Assert.True(thrown is OperationCanceledException && thrown.Message.Contains("stopped while its weather"),
                    "the build throws the stop, saying where: " + thrown?.GetType().Name + " " + thrown?.Message);
                Assert.True(clock.Elapsed.TotalSeconds < 30, $"at once, not after the 60 s solve ({clock.Elapsed.TotalSeconds:F1} s)");
                Assert.Equal(1, Starts(starts), "no WindNinja started after the stop (the series has 3+ bands)");
                Assert.Equal(0, ElmfireProcesses.Count, "and none is left running");
                Assert.True(!File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.WindSpeed)), "no ws.tif: no uniform field was written");
                Assert.True(!log.Any(l => l.Contains("uniform field")), "nor said to be: " + string.Join(" | ", log.Where(l => l.Contains("wind"))));
                Assert.True(!File.Exists(Path.Combine(caseDir, "elmfire.data")), "and no namelist");
                Assert.True(File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.Dem)), "the layers made before the stop are kept");

                //The next build carries on: the wind is made, and without the fake WindNinja it is the uniform
                //fallback, said as such - which is the only honest thing for a machine without WindNinja.
                ElmfireCaseBuilder.Options again = c.Options(caseDir, 150.0, new List<string>());
                again.SimulationTstopSeconds = 3 * 3600.0;
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(again).GetAwaiter().GetResult();
                Assert.True(File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.WindSpeed)) && r.Weather != null && !r.Weather.Cancelled,
                    "a build that is not stopped writes the weather");
            }
        }

        private static void StoppedBuildCaseOnly()
        {
            if (OperatingSystem.IsWindows()) return;

            using (var c = new SyntheticCase())
            {
                string starts = Path.Combine(c.Folder, "windninja_starts.txt");
                string wui = c.WriteScenario("case", 150.0,
                    "WindNinjaExe=" + FakeWindNinja(c.Folder, starts), "", "[Landscape]", "ElevationFile=source_dem.tif");

                //Offline, whatever the network: a file where the ERA5 archive's folder would go makes the climatology
                //fall back at once instead of downloading 26 years of it.
                Directory.CreateDirectory(Path.Combine(c.Folder, "case"));
                File.WriteAllText(Path.Combine(c.Folder, "case", "climatology"), "not a folder");

                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                input.WildfireModule.ElmfireInput.PathToGdal = GdalTools.FindBinDirectory() ?? string.Empty;
                string elevationBefore = input.Landscape.ElevationFile;

                var log = new List<string>();
                Task stopper = StopWhenWindNinjaStarts(starts);
                var clock = Stopwatch.StartNew();
                bool built = ElmfireCoupling.BuildCaseOnly(input, m => { lock (log) log.Add(m); }, out string problem);
                stopper.Wait();

                Assert.True(!built, "a stopped build is not a built case");
                Assert.True(problem != null && problem.Contains("stopped") && problem.Contains("no wind was written"),
                    "the problem says it was stopped: " + problem);
                Assert.True(clock.Elapsed.TotalSeconds < 30, $"at once ({clock.Elapsed.TotalSeconds:F1} s)");
                Assert.Equal(1, Starts(starts), "one WindNinja, the one the stop killed");
                Assert.Equal(elevationBefore, input.Landscape.ElevationFile, "the scenario is not pointed at a half-built case");
                Assert.True(!File.Exists(Path.Combine(c.Folder, "case", "inputs", "ws.tif")), "no wind in the case");
            }
        }

        private static void NoBuildUnderCampaign()
        {
            using (var c = new SyntheticCase())
            {
                string wui = c.WriteScenario("case", 150.0);
                string campaign = Path.Combine(c.Folder, CampaignLayout.OutputFolder, CampaignLayout.CampaignFolderName("synthetic", "0123abcd"));
                Directory.CreateDirectory(campaign);

                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                input.WildfireModule.ElmfireInput.PathToGdal = GdalTools.FindBinDirectory() ?? string.Empty;

                using (PREACTcli.Campaigns.CampaignLock held = PREACTcli.Campaigns.CampaignLock.Acquire(campaign, out string lockProblem))
                {
                    Assert.True(held != null, "the test holds the campaign's lock: " + lockProblem);

                    bool built = ElmfireCoupling.BuildCaseOnly(input, null, out string problem);
                    Assert.True(!built && problem != null && problem.Contains("campaign is running"), "the GUI's build is refused: " + problem);
                    Assert.True(!Directory.Exists(Path.Combine(c.Folder, "case", "inputs")), "and nothing was written");

                    (int exit, string output) = RunCli(c.Folder, "build-case", "--wui", wui, "--dem", c.DemPath, "--no-climatology");
                    Assert.True(exit == 1 && output.Contains("campaign is running"), "build-case in another process is refused: " + Tail(output));
                }

                Assert.True(!CampaignLayout.IsLockHeld(campaign), "released with the campaign");
                Assert.True(File.Exists(Path.Combine(campaign, CampaignLayout.LockFile)), "the file stays, and is no lock");

                //A campaign of another scenario, in another folder, on the same case: seen through the case's own lock, which
                //two campaigns can hold at once (review NIT: the refusal looked only under this scenario's _output).
                string caseDir = Path.Combine(c.Folder, "case");
                Directory.CreateDirectory(caseDir);
                using (PREACTcli.Campaigns.CampaignLock one = PREACTcli.Campaigns.CampaignLock.AcquireCase(caseDir))
                using (PREACTcli.Campaigns.CampaignLock two = PREACTcli.Campaigns.CampaignLock.AcquireCase(caseDir))
                {
                    Assert.True(one != null && two != null, "two campaigns share the case's lock");
                    Assert.True(CampaignLayout.IsLockHeld(caseDir), "and it reads as held");

                    bool built = ElmfireCoupling.BuildCaseOnly(input, null, out string problem);
                    Assert.True(!built && problem != null && problem.Contains("running on this case"), "the GUI's build is refused: " + problem);
                    (int exit, string output) = RunCli(c.Folder, "build-case", "--wui", wui, "--dem", c.DemPath, "--no-climatology");
                    Assert.True(exit == 1 && output.Contains("running on this case"), "and build-case: " + Tail(output));
                }
                Assert.True(!CampaignLayout.IsLockHeld(caseDir), "released when both have finished");
                (int after, string said) = RunCli(c.Folder, "build-case", "--wui", wui, "--dem", c.DemPath, "--no-climatology",
                    "--windninja", Path.Combine(c.Folder, "no-windninja-here"));
                Assert.Equal(0, after, "once it has finished the case builds (" + Tail(said) + ")");
            }
        }

        private static void PaintingPosition()
        {
            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, new List<string>())).GetAwaiter().GetResult();
                MasterGrid g = MasterGrid.FromRasterFile(ElmfireStems.Tif(Path.Combine(caseDir, "inputs"), ElmfireStems.Dem));

                var data = new Input.WildfireData();
                int cells = g.Header.Ncols * g.Header.Nrows;
                data.RandomIgnition = new bool[cells];
                for (int i = cells / 3; i < cells / 3 + 40; ++i) data.RandomIgnition[i] = true;
                string gfi = Path.Combine(c.Folder, "painted.gfi");

                (bool Ok, string Log, string Error) BuildWith(GraphicalFireInput.PaintedGrid recorded)
                {
                    if (recorded == null) GraphicalFireInput.SaveGraphicalFireInput(gfi, data, g.Header.Ncols, g.Header.Nrows);
                    else GraphicalFireInput.SaveGraphicalFireInput(gfi, data, g.Header.Ncols, g.Header.Nrows, recorded);
                    var log = new List<string>();
                    ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, log);
                    o.PaintedMasksPath = gfi;
                    try
                    {
                        ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();
                        return (true, string.Join("\n", log), null);
                    }
                    catch (InvalidDataException e)
                    {
                        return (false, string.Join("\n", log), e.Message);
                    }
                }

                var here = new GraphicalFireInput.PaintedGrid { XllCorner = g.XMin, YllCorner = g.YMin, CellSize = 30, EpsgCode = 32634 };
                var moved = new GraphicalFireInput.PaintedGrid { XllCorner = g.XMin + 300, YllCorner = g.YMin, CellSize = 30, EpsgCode = 32634 };

                var right = BuildWith(here);
                Assert.True(right.Ok && right.Log.Contains("which the file places at"), "painted on this grid: placed; " + right.Error);
                Assert.True(PaintedAreasReadable(gfi, g), "a file with the trailer still reads as every older reader reads it");

                var wrong = BuildWith(moved);
                Assert.True(!wrong.Ok && wrong.Error.Contains("starts 300 m west of the grid the painting was made on"),
                    "same size, 10 cells away: refused, saying so: " + wrong.Error);

                var legacy = BuildWith(null);
                Assert.True(legacy.Ok && legacy.Log.Contains("matched by its size alone"), "an older file is matched by size, and it is said");
            }
        }

        /// <summary>The masks of a painting read the way every older reader does: the header, then four blocks.</summary>
        private static bool PaintedAreasReadable(string gfi, MasterGrid g)
        {
            GraphicalFireInput.LoadGraphicalFireInput(gfi, out int ncols, out int nrows, out bool[] _, out bool[] area, out bool[] _, out bool[] _, out bool ok);
            return ok && ncols == g.Header.Ncols && nrows == g.Header.Nrows && area.Count(b => b) == 40;
        }

        private static void GridCoversPaddedDomain()
        {
            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                ElmfireCaseBuilder.Options o = c.Options(caseDir, 317.0, new List<string>());
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();

                (Math.Vector2d sw, Math.Vector2d ne) = ElmfireCaseBuilder.PaddedBounds(o);
                (double xMin, double yMin, double xMax, double yMax) = RasterHarmonizer.ProjectBounds(r.Grid.Epsg, sw.x, sw.y, ne.x, ne.y);
                MasterGrid g = MasterGrid.FromRasterFile(ElmfireStems.Tif(Path.Combine(caseDir, "inputs"), ElmfireStems.Dem));

                string box = $"grid {g.XMin:F2},{g.YMin:F2} - {g.XMax:F2},{g.YMax:F2}; padded domain {xMin:F2},{yMin:F2} - {xMax:F2},{yMax:F2}";
                Assert.True(g.XMin <= xMin && g.YMin <= yMin && g.XMax >= xMax && g.YMax >= yMax, "covers it: " + box);
                Assert.True(g.XMin > xMin - 30 && g.YMin > yMin - 30 && g.XMax < xMax + 30 && g.YMax < yMax + 30, "by less than a cell: " + box);
                Assert.Near(0.0, System.Math.IEEERemainder(g.XMin, 30.0), 1e-6, "west edge on the 30 m lattice");
                Assert.Near(0.0, System.Math.IEEERemainder(g.YMax, 30.0), 1e-6, "north edge on the 30 m lattice");
            }
        }

        private static void ArchiveWorkingCopy()
        {
            string dir = Directory.CreateTempSubdirectory("preact-archive-").FullName;
            try
            {
                //An archive as the first version wrote it: no archive_format marker, fire weather codes to re-derive.
                var lines = new List<string>
                {
                    "Latitide,37.996483", "Longitude,23.951612", "Elevation,274",
                    "Time,Temperature_2m [°C],Relativehumidity_2m [%],Precipitation [mm],Windspeed_10m [m/s],Winddirection_10m [°],"
                    + "Cloudcover [%],Direct_radiation [W/m²],Boundary_layer_height [m],FFMC hourly [-],FFMC [-],DMC [-],DC [-],ISI [-],BUI [-],FWI [-]",
                };
                var t = new DateTime(2020, 7, 1);
                for (int h = 0; h < 24 * 10; ++h)
                {
                    lines.Add(t.AddHours(h).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)
                              + ",30,20,0,6,300,0,500,1000,85,0,0,0,0,0,0");
                }
                string user = Path.Combine(dir, "user", "mati_era5_hourly.csv");
                Directory.CreateDirectory(Path.GetDirectoryName(user));
                File.WriteAllLines(user, lines);
                string before = ElmfireFingerprint.HashFile(user);

                string caseClimatology = Path.Combine(dir, "case", "climatology");
                string copy = ClimatologySampler.WorkingCopy(user, caseClimatology, null);
                Assert.True(copy != user && File.Exists(copy) && copy.StartsWith(caseClimatology, StringComparison.Ordinal), "a copy in the case: " + copy);
                Assert.True(ClimatologySampler.EnsureArchiveFormat(copy, null), "the copy is brought to the current format");
                Assert.Equal(ClimatologySampler.ArchiveFormatVersion, ClimatologySampler.ReadFormatVersion(File.ReadLines(copy).ElementAt(3)), "copy format");
                Assert.Equal(before, ElmfireFingerprint.HashFile(user), "the user's archive is byte for byte what it was");
                Assert.Equal(1, ClimatologySampler.ReadFormatVersion(File.ReadLines(user).ElementAt(3)), "and still in its own format");

                //Asked again: the derived copy is kept, not replaced by the old one.
                Assert.Equal(copy, ClimatologySampler.WorkingCopy(user, caseClimatology, null), "the same copy");
                Assert.Equal(ClimatologySampler.ArchiveFormatVersion, ClimatologySampler.ReadFormatVersion(File.ReadLines(copy).ElementAt(3)), "still current");

                //The case's own archive, named directly, is the case's to maintain.
                Assert.Equal(copy, ClimatologySampler.WorkingCopy(copy, caseClimatology, null), "an archive already in the folder is used as it is");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void MissingElmfireNamed()
        {
            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, new List<string>())).GetAwaiter().GetResult();

                string missing = Path.Combine(c.Folder, "nowhere", "elmfire");
                string wui = c.WriteScenario("case", 150.0);
                File.WriteAllLines(wui, File.ReadAllLines(wui)
                    .Select(l => l == "BuildCase=true" ? "BuildCase=false\nReuseExistingOutput=false\nElmfireExe=nowhere/elmfire" : l)
                    .SelectMany(l => l.Split('\n')));
                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);

                ElmfireCoupling.Result run = ElmfireCoupling.Prepare(input, input.WildfireModule.ElmfireInput, null);
                Assert.True(!run.Ok && run.Message.Contains(missing), "a run names the path it tried: " + run.Message);
                Assert.True(!run.Message.Contains("--elmfire"), "and no flag PREACT.exe does not have: " + run.Message);

                File.AppendAllLines(wui, new[] { "", "[TriggerBufferModule]", "Enabled=true", "Module=kPERIL", "", "[kPERIL]", "WuiAreaSource=Raster" });
                TextWriter error = Console.Error;
                var captured = new StringWriter();
                try
                {
                    Console.SetError(captured);
                    PREACTcli.Campaigns.CampaignSetup.Resolve(PREACTcli.Campaigns.CampaignOptions.Parse(
                        new[] { "--wui", wui, "--max", "1", "--elmfire", missing }));
                }
                finally
                {
                    Console.SetError(error);
                }
                Assert.True(captured.ToString().Contains("--elmfire names " + missing), "a campaign names it too: " + captured);
            }
        }

        /// <summary>
        /// e2e F4: a scenario written on Windows names its case folder, source layers and template with backslashes.
        /// Everything else in the .wui resolved off Windows; these were combined bare, so a case built on Linux had
        /// no fuel and no canopy ("names sources\..., which is not there").
        /// </summary>
        private static void BackslashElmfirePaths()
        {
            using (var c = new SyntheticCase())
            {
                string sources = Path.Combine(c.Folder, "sources");
                Directory.CreateDirectory(sources);
                File.Copy(c.FuelPath, Path.Combine(sources, "mati_fbfm40.tif"));
                File.Copy(c.DemPath, Path.Combine(sources, "mati_cc.tif"));
                string caseDir = Path.Combine(c.Folder, "cases", "syn");
                Directory.CreateDirectory(caseDir);
                File.WriteAllLines(Path.Combine(caseDir, "hand.data"), new[] { "&INPUTS", "FBFM_FILENAME = 'fbfm40'", "/" });

                string wui = c.WriteScenario("cases\\syn", 150.0);
                File.WriteAllLines(wui, File.ReadAllLines(wui)
                    .Select(l => l.StartsWith("FuelModelFile=", StringComparison.Ordinal)
                        ? "FuelModelFile=sources\\mati_fbfm40.tif\nCanopyCoverFile=.\\sources\\mati_cc.tif\nNamelistTemplate=cases\\syn\\hand.data"
                        : l)
                    .SelectMany(l => l.Split('\n')));

                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                Input.ElmfireInput settings = input.WildfireModule.ElmfireInput;
                Assert.True(!Input.PREACTInput.Requirements.Any(r => r.Key.Contains("FuelModelFile") || r.Key.Contains("CanopyCoverFile")),
                    "no source layer is reported missing: " + string.Join("; ", Input.PREACTInput.Requirements.Select(r => r + ": " + r.Message)));

                string resolvedCase = ElmfireCoupling.CaseDirectoryPath(input.RootFolder, settings);
                Assert.Equal(Path.GetFullPath(caseDir), resolvedCase, "the case folder");

                var log = new List<string>();
                ElmfireCaseBuilder.Options o = ElmfireCoupling.CreateBuildOptions(input, settings, resolvedCase, m => log.Add(m));
                Assert.True(o.UserRasters.TryGetValue("fbfm40", out string fuel) && File.Exists(fuel), "the fuel source layer: " + fuel);
                Assert.True(o.UserRasters.TryGetValue("cc", out string cc) && File.Exists(cc), "the canopy cover source layer: " + cc);
                Assert.True(o.TemplateNamelistPath != null && File.Exists(o.TemplateNamelistPath), "the namelist template: " + o.TemplateNamelistPath);
                Assert.True(string.Join("\n", Input.PREACTInputWriter.Write(input)).Contains("FuelModelFile=sources/mati_fbfm40.tif"),
                    "saved with forward slashes");
            }
        }

        /// <summary>A folder with a synthetic DEM and fuel raster around a small domain in UTM zone 34N.</summary>
        internal sealed class SyntheticCase : IDisposable
        {
            public readonly string Folder;
            public readonly string DemPath, FuelPath;
            public const double Lat = 38.0123, Lon = 23.9;
            public const double DomainMetres = 1500.0;

            public SyntheticCase()
            {
                Folder = Directory.CreateTempSubdirectory("preact-case-").FullName;
                Assert.True(CrsTransform.TryWgs84To("EPSG:32634", Lat, Lon, out double x0, out double y0), "corner in UTM 34N");

                //4 km of 30 m cells from 1 km south-west of the domain corner: covers any padding up to ~1 km.
                var grid = new MasterGrid
                {
                    Header = new AscRaster.Header
                    {
                        Ncols = 134, Nrows = 134, CellSize = 30, CellSizeY = 30,
                        XllCorner = System.Math.Floor(x0 - 1000.0), YllCorner = System.Math.Floor(y0 - 1000.0), NoDataValue = -9999,
                    },
                    Epsg = "EPSG:32634",
                };

                var dem = new float[134, 134];
                var fuel = new float[134, 134];
                for (int x = 0; x < 134; ++x)
                {
                    for (int y = 0; y < 134; ++y)
                    {
                        dem[x, y] = 100f + 2f * x + 1f * y;
                        //Grass west of the middle, timber east of it, and an urban (non-burnable) strip across the
                        //domain 350-500 m north of its southern edge.
                        fuel[x, y] = y >= 45 && y < 50 ? 91f : x < 67 ? 102f : 165f;
                    }
                }

                DemPath = Path.Combine(Folder, "source_dem.tif");
                FuelPath = Path.Combine(Folder, "source_fbfm40.tif");
                GeoTiffRasterWriter.WriteBand(grid, dem, DemPath);
                GeoTiffRasterWriter.WriteBand(grid, fuel, FuelPath);
            }

            /// <summary>Builder options for the domain, padded by <paramref name="padding"/> m, offline.</summary>
            public ElmfireCaseBuilder.Options Options(string caseDir, double padding, List<string> log)
            {
                var o = new ElmfireCaseBuilder.Options
                {
                    Name = "synthetic",
                    LowerLeftLatLon = new Math.Vector2d(Lat, Lon),
                    DomainSizeMetres = new Math.Vector2d(DomainMetres, DomainMetres),
                    CellSizeMetres = 30.0,
                    PaddingMetres = padding,
                    OutputDirectory = caseDir,
                    Force = true,
                    LocalDemPath = DemPath,
                    SimulationTstopSeconds = 3600.0,
                    Log = m => { lock (log) log.Add(m); },
                };
                o.UserRasters["fbfm40"] = FuelPath;
                o.Weather.UseClimatology = false;
                o.Weather.WindNinjaExe = Path.Combine(Folder, "no-windninja-here");
                return o;
            }

            /// <summary>
            /// A scenario for the domain whose [ELMFIRE] section builds into <paramref name="caseDirectory"/>;
            /// <paramref name="extra"/> lines are appended, so they land in [ELMFIRE] until one opens another section.
            /// </summary>
            public string WriteScenario(string caseDirectory, double padding, params string[] extra)
            {
                string path = Path.Combine(Folder, "synthetic.wui");
                File.WriteAllLines(path, new[]
                {
                    "[Simulation]",
                    "Name=synthetic",
                    "LowerLeftLatLon=" + Lat.ToString(CultureInfo.InvariantCulture) + "," + Lon.ToString(CultureInfo.InvariantCulture),
                    "DomainSize=" + DomainMetres.ToString(CultureInfo.InvariantCulture) + "," + DomainMetres.ToString(CultureInfo.InvariantCulture),
                    "DeltaTime=1",
                    "StartDateTime=2026-06-28T12:00:00",
                    "EndDateTime=2026-06-28T14:00:00",
                    "",
                    "[WildfireModule]",
                    "Enabled=true",
                    "Module=ELMFIRE",
                    "",
                    "[ELMFIRE]",
                    "CaseDirectory=" + caseDirectory,
                    "SimulationTstopHours=1",
                    "CellSizeMetres=30",
                    "PaddingMetres=" + padding.ToString(CultureInfo.InvariantCulture),
                    "BuildCase=true",
                    "FuelModelFile=source_fbfm40.tif",
                }.Concat(extra));
                return path;
            }

            public void Dispose()
            {
                try { Directory.Delete(Folder, true); } catch { }
            }
        }

        /// <summary>The CLI's own build output, which the harness builds beside this one.</summary>
        internal static string CliDll()
        {
            string dir = AppContext.BaseDirectory;
            foreach (string configuration in new[] { "Debug", "Release" })
            {
                string candidate = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "PREACTcli", "bin", configuration, "net8.0", "PREACTcli.dll"));
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>Runs PREACTcli with the given arguments; stdout and stderr together.</summary>
        internal static (int Exit, string Output) RunCli(string workingDirectory, params string[] args)
        {
            string cli = CliDll();
            Assert.True(cli != null, "PREACTcli's build output is there (build every PREACT project first)");

            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = workingDirectory,
            };
            psi.ArgumentList.Add(cli);
            foreach (string a in args) psi.ArgumentList.Add(a);

            //Only what libgdal itself needs. This process's engine put the Runtimes folders on the variable for its
            //own children, which would hide a CLI that cannot find its wrappers.
            string library = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
            psi.Environment["LD_LIBRARY_PATH"] = string.Join(":", library.Split(':')
                .Where(p => p.Length > 0 && p.IndexOf("Runtimes", StringComparison.Ordinal) < 0));

            using (Process p = Process.Start(psi))
            {
                p.StandardInput.Close();
                Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                Task<string> stderr = p.StandardError.ReadToEndAsync();
                Assert.True(p.WaitForExit(300000), "PREACTcli finished within five minutes");
                return (p.ExitCode, stdout.Result + stderr.Result);
            }
        }

        private static void CliFindsGdalWrappers()
        {
            using (var c = new SyntheticCase())
            {
                string wui = c.WriteScenario("case", 300.0);
                (int exit, string output) = RunCli(c.Folder, "build-case", "--wui", wui, "--dem", c.DemPath, "--no-climatology",
                    "--windninja", Path.Combine(c.Folder, "no-windninja-here"));

                Assert.True(output.IndexOf("PINVOKE", StringComparison.Ordinal) < 0
                            && output.IndexOf("type initializer", StringComparison.OrdinalIgnoreCase) < 0,
                    "no GDAL wrapper failed to load: " + Tail(output));
                Assert.Equal(0, exit, "build-case exit code (" + Tail(output) + ")");
                Assert.True(File.Exists(Path.Combine(c.Folder, "case", "inputs", "dem.tif")), "the case grid was warped");
                Assert.True(File.Exists(Path.Combine(c.Folder, "case", "inputs", "fbfm40.tif")), "the fuel was warped");
            }
        }

        /// <summary>
        /// Build, hand-edit the namelist to burn a fuel variant the builder does not know (as Nick's Mati case does),
        /// then rebuild with more padding so the grid is re-cut: the variant has to come out on the new grid with its
        /// classes intact, the hand-edited namelist has to be kept with its differences logged, and it has to be
        /// runnable as a template on the new case.
        /// </summary>
        private static void RecutCarriesNamelistRasters()
        {
            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                string inputs = Path.Combine(caseDir, "inputs");
                var log = new List<string>();

                ElmfireCaseBuilder.Result first = ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, log)).GetAwaiter().GetResult();
                Assert.True(first.Validation.Ok, "first build is valid: " + ElmfireCaseValidator.Summarize(first.Validation));
                MasterGrid g1 = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, ElmfireStems.Dem));

                //A hand-made variant: the urban strip made burnable (GR1), like fbfm40_roads101's road corridor.
                float[,] fuel = AscRaster.ReadGeoTiff(ElmfireStems.Tif(inputs, "fbfm40"), out AscRaster.Header _, out bool ok);
                Assert.True(ok, "read the case's fuel");
                int corridor = 0;
                for (int x = 0; x < fuel.GetLength(0); ++x)
                {
                    for (int y = 0; y < fuel.GetLength(1); ++y)
                    {
                        if (fuel[x, y] == 91f) { fuel[x, y] = 101f; ++corridor; }
                    }
                }
                Assert.True(corridor > 0, "the synthetic fuel has an urban strip to make burnable");
                GeoTiffRasterWriter.WriteBand(g1, fuel, ElmfireStems.Tif(inputs, "fbfm40_custom"));

                string namelist = Path.Combine(caseDir, "elmfire.data");
                File.WriteAllLines(namelist, ElmfireNamelist.SetKeyInGroup(File.ReadAllLines(namelist),
                    ElmfireNamelistKeys.InputsGroup, "FBFM_FILENAME", "fbfm40_custom", quoted: true));

                log.Clear();
                ElmfireCaseBuilder.Result second = ElmfireCaseBuilder.Build(c.Options(caseDir, 450.0, log)).GetAwaiter().GetResult();
                string all = string.Join("\n", log);
                Assert.True(second.GridRebuilt, "the second build re-cut the grid");
                MasterGrid g2 = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, ElmfireStems.Dem));
                Assert.True(g2.Header.Ncols > g1.Header.Ncols && g2.Header.Nrows > g1.Header.Nrows,
                    $"the grid grew ({g1.Header.Ncols}x{g1.Header.Nrows} -> {g2.Header.Ncols}x{g2.Header.Nrows})");

                string custom = ElmfireStems.Tif(inputs, "fbfm40_custom");
                MasterGrid gc = MasterGrid.FromRasterFile(custom);
                Assert.True(gc.Header.Ncols == g2.Header.Ncols && gc.Header.Nrows == g2.Header.Nrows
                            && System.Math.Abs(gc.XMin - g2.XMin) < 1 && System.Math.Abs(gc.YMax - g2.YMax) < 1,
                    $"the hand-made fuel is on the new grid ({gc.Header.Ncols}x{gc.Header.Nrows} at {gc.XMin:F0},{gc.YMax:F0})");
                Assert.True(File.Exists(ElmfireStems.Tif(Path.Combine(inputs, ElmfireCaseBuilder.PreviousGridFolder), "fbfm40_custom")),
                    "the original is kept with the old grid");

                //Nearest-neighbour: only the classes that were there (0 in the new padding outside the source).
                float[,] carried = AscRaster.ReadGeoTiff(custom, out AscRaster.Header _, out bool carriedOk);
                Assert.True(carriedOk, "read the carried fuel");
                int corridorAfter = 0;
                foreach (float v in carried)
                {
                    Assert.True(v == 0f || v == 101f || v == 102f || v == 165f || v <= -9000f, "a fuel class, not an interpolated value: " + v);
                    if (v == 101f) ++corridorAfter;
                }
                Assert.True(corridorAfter >= corridor, $"the burnable corridor survives the re-cut ({corridor} -> {corridorAfter} cells)");

                Assert.True(second.KeptNamelistPath != null && File.Exists(second.KeptNamelistPath), "the hand-edited namelist was kept");
                Assert.True(all.Contains("&INPUTS FBFM_FILENAME: 'fbfm40_custom' -> 'fbfm40'"), "the differing key is logged: " + all);
                ElmfireCaseValidator.Report kept = ElmfireCaseValidator.ValidateNamelistRasters(
                    File.ReadAllLines(second.KeptNamelistPath), caseDir, includeWeather: true);
                Assert.True(kept.Ok, "the kept namelist runs on the new case: " + ElmfireCaseValidator.Summarize(kept));
                Assert.True(all.Contains("so it runs as [ELMFIRE] NamelistTemplate"), "the log says the kept namelist would run");
            }
        }

        private static void OffGridRasterRefused()
        {
            using (var c = new SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                string inputs = Path.Combine(caseDir, "inputs");
                var log = new List<string>();
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, log)).GetAwaiter().GetResult();

                //A fuel variant on another grid: 10x10 cells somewhere else, as roads101 was left at 566x541.
                var small = new MasterGrid
                {
                    Header = new AscRaster.Header { Ncols = 10, Nrows = 10, CellSize = 30, CellSizeY = 30, XllCorner = 0, YllCorner = 0, NoDataValue = -9999 },
                    Epsg = "EPSG:32634",
                };
                small.Header.XllCorner = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, ElmfireStems.Dem)).XMin + 300.0;
                small.Header.YllCorner = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, ElmfireStems.Dem)).YMin + 300.0;
                var values = new float[10, 10];
                for (int x = 0; x < 10; ++x) for (int y = 0; y < 10; ++y) values[x, y] = 102f;
                GeoTiffRasterWriter.WriteBand(small, values, ElmfireStems.Tif(inputs, "fbfm40_small"));

                string template = Path.Combine(caseDir, "hand.data");
                File.WriteAllLines(template, ElmfireNamelist.SetKeyInGroup(File.ReadAllLines(Path.Combine(caseDir, "elmfire.data")),
                    ElmfireNamelistKeys.InputsGroup, "FBFM_FILENAME", "fbfm40_small", quoted: true));

                // ---- the check itself
                ElmfireCaseValidator.Report report = ElmfireCaseValidator.ValidateNamelistRasters(File.ReadAllLines(template), caseDir, true);
                string summary = ElmfireCaseValidator.Summarize(report);
                Assert.True(!report.Ok, "an off-grid fuel raster is refused");
                Assert.True(summary.Contains("FBFM_FILENAME = 'fbfm40_small'") && summary.Contains("is 10x10"), "the key and the size are named: " + summary);

                // ---- a single run, before ELMFIRE is started
                string wui = c.WriteScenario("case", 150.0);
                File.WriteAllLines(wui, File.ReadAllLines(wui)
                    .Select(l => l == "BuildCase=true" ? "BuildCase=false\nNamelistTemplate=hand.data\nReuseExistingOutput=false" : l)
                    .SelectMany(l => l.Split('\n')));
                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                Assert.True(input?.WildfireModule?.ElmfireInput != null, "the scenario loads");
                ElmfireCoupling.Result run = ElmfireCoupling.Prepare(input, input.WildfireModule.ElmfireInput, null);
                Assert.True(!run.Ok && run.Message.Contains("FBFM_FILENAME = 'fbfm40_small'") && run.Message.Contains("not on the case grid"),
                    "a single run is refused before ELMFIRE, naming the raster: " + run.Message);

                // ---- a campaign, before its first realization
                File.AppendAllLines(wui, new[] { "", "[TriggerBufferModule]", "Enabled=true", "Module=kPERIL", "", "[kPERIL]", "WuiAreaSource=Raster" });
                TextWriter error = Console.Error;
                var captured = new StringWriter();
                PREACTcli.Campaigns.Campaign campaign;
                try
                {
                    Console.SetError(captured);
                    campaign = PREACTcli.Campaigns.CampaignSetup.Resolve(PREACTcli.Campaigns.CampaignOptions.Parse(
                        new[] { "--wui", wui, "--inspect", "--elmfire-template", template }));
                }
                finally
                {
                    Console.SetError(error);
                }
                string said = captured.ToString();
                Assert.True(campaign == null, "the campaign is refused up front: " + said);
                Assert.True(said.Contains("FBFM_FILENAME = 'fbfm40_small'") && said.Contains("not on the case"), "the campaign names the raster: " + said);
            }
        }

        internal static string Tail(string text, int lines = 6)
        {
            string[] all = (text ?? "").Split('\n');
            return string.Join(" | ", all.Skip(System.Math.Max(0, all.Length - lines)).Select(l => l.TrimEnd()));
        }
    }
}
