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

            /// <summary>A scenario for the domain whose [ELMFIRE] section builds into <paramref name="caseDirectory"/>.</summary>
            public string WriteScenario(string caseDirectory, double padding)
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
                });
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
