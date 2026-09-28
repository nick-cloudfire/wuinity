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

                //A layer only the case has: made by hand, no source to warp it from again.
                var wui = new float[first.Header.Ncols, first.Header.Nrows];
                for (int x = 10; x < 20; ++x) for (int y = 10; y < 20; ++y) wui[x, y] = 1f;
                GeoTiffRasterWriter.WriteBand(first, wui, ElmfireStems.Tif(inputs, ElmfireStems.WuiArea));

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
                Assert.True(File.Exists(ElmfireStems.Tif(previous, ElmfireStems.WuiArea))
                            && File.Exists(Path.Combine(previous, ElmfireCaseBuilder.CarryPendingMarker)), "the WUI area waits there, marked");

                var log = new List<string>();
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(c.Options(caseDir, 600.0, log)).GetAwaiter().GetResult();
                MasterGrid second = MasterGrid.FromRasterFile(ElmfireStems.Tif(inputs, ElmfireStems.Dem));
                Assert.True(second.Header.Ncols > first.Header.Ncols, "the next build re-cuts the grid");
                Assert.True(log.Any(l => l.Contains("did not finish carrying")), "saying it resumes the carry");
                Assert.True(r.Carried.Contains(ElmfireStems.WuiArea), "and carries the WUI area: " + string.Join(",", r.Carried));
                float[,] carried = AscRaster.ReadGeoTiff(ElmfireStems.Tif(inputs, ElmfireStems.WuiArea), out AscRaster.Header h, out bool ok);
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
                data.WuiArea = new bool[cells];
                for (int i = cells / 3; i < cells / 3 + 40; ++i) data.WuiArea[i] = true;
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
            GraphicalFireInput.LoadGraphicalFireInput(gfi, out int ncols, out int nrows, out bool[] wui, out bool[] _, out bool[] _, out bool[] _, out bool ok);
            return ok && ncols == g.Header.Ncols && nrows == g.Header.Nrows && wui.Count(b => b) == 40;
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
