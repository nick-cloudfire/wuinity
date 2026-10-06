using System.Globalization;
using System.Runtime.InteropServices;
using PREACT.Input;
using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>
    /// FireDX (building layers from the FBFM40 fuel) and [ELMFIRE] RoadsCarryFire: the toggle the case build applies to
    /// FireDX's road cells, the settings, and the runner's command, environment and failures - with a stand-in Python, since
    /// FireDX itself is a private submodule this repository does not hold.
    /// </summary>
    internal static class FireDxTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("roads carry fire: FireDX's 256 cells become 101 and its 91 stay, every build reapplies it, unticking restores the fuel", RoadsOnFireDxFuel);
            runner.Add("roads carry fire: without FireDX road cells or a SUMO network the build says so and leaves the fuel alone", RoadsWithoutRoads);
            runner.Add("roads carry fire: without FireDX the SUMO lanes cross the non-burnable strip as GR1, joining the patches; unticking restores", RoadsFromSumoLanes);
            runner.Add("firedx: RoadsCarryFire and the FireDX tool keys round-trip; 256 and 90-100 are non-burnable, as in ELMFIRE", SettingsAndBurnable);
            runner.Add("firedx: the runner's command, environment and fuel source: its own output and roads lead back to the LANDFIRE fuel", RunnerCommand);
            runner.Add("firedx: no Python, missing modules, no network, no buildings and a stop are each reported for what they are", RunnerFailures);
        }

        private static bool Windows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// The synthetic case's fuel as FireDX leaves it: its urban strip (rows 45-49) pavement (256) but for a block of
        /// buildings (91) at columns 60-69, and a grass cell (102) at (30, 47) ringed by the pavement - an islet the roads join.
        /// </summary>
        private static void WriteFireDxFuel(PipelineTests.SyntheticCase c)
        {
            float[,] fuel = AscRaster.ReadGeoTiff(c.FuelPath, out AscRaster.Header h, out bool ok);
            Assert.True(ok, "read the synthetic fuel");
            for (int x = 0; x < fuel.GetLength(0); ++x)
            {
                for (int y = 45; y < 50; ++y)
                {
                    fuel[x, y] = x >= 60 && x < 70 ? 91f : 256f;
                }
            }
            fuel[30, 47] = 102f;
            MasterGrid grid = MasterGrid.FromRasterFile(c.FuelPath);
            GeoTiffRasterWriter.WriteBand(grid, fuel, c.FuelPath, OSGeo.GDAL.DataType.GDT_Int16);
        }

        private static int Count(float[,] a, float v)
        {
            int n = 0;
            foreach (float x in a) if (x == v) ++n;
            return n;
        }

        private static float[,] Read(string path)
        {
            float[,] a = AscRaster.ReadGeoTiff(path, out AscRaster.Header _, out bool ok);
            Assert.True(ok && a != null, "read " + path);
            return a;
        }

        private static string Manifest(string caseDir, string key)
        {
            foreach (string line in File.ReadAllLines(Path.Combine(caseDir, ElmfireCaseBuilder.SourceManifestName)))
            {
                if (line.StartsWith(key + "=", StringComparison.Ordinal)) return line.Substring(key.Length + 1);
            }
            return null;
        }

        private static void RoadsOnFireDxFuel()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                WriteFireDxFuel(c);
                string caseDir = Path.Combine(c.Folder, "case");
                string fuel = Path.Combine(caseDir, "inputs", "fbfm40.tif");
                string kept = Path.Combine(caseDir, "inputs", ElmfireCaseBuilder.RoadsFolder, "fbfm40.tif");

                //Off: the fuel as warped.
                var log = new List<string>();
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, log)).GetAwaiter().GetResult();
                float[,] plain = Read(fuel);
                int pavement = Count(plain, 256f), buildings = Count(plain, 91f), grass101 = Count(plain, 101f);
                Assert.True(pavement > 100 && buildings > 10, $"the case holds FireDX's pavement ({pavement}) and buildings ({buildings})");
                Assert.True(!File.Exists(kept) && Manifest(caseDir, "RoadsCarryFire") == "off", "off: nothing kept, recorded as off");

                //On: 256 -> 101, 91 untouched, the fuel before kept, what was done recorded and measured.
                log.Clear();
                ElmfireCaseBuilder.Options on = c.Options(caseDir, 150.0, log);
                on.RoadsCarryFire = true;
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(on).GetAwaiter().GetResult();
                float[,] roads = Read(fuel);
                Assert.Equal(0, Count(roads, 256f), "no pavement left");
                Assert.Equal(buildings, Count(roads, 91f), "the building cells stay 91");
                Assert.Equal(grass101 + pavement, Count(roads, 101f), "every pavement cell is GR1 now");
                for (int x = 0; x < plain.GetLength(0); ++x)
                {
                    for (int y = 0; y < plain.GetLength(1); ++y)
                    {
                        if (plain[x, y] != 256f && plain[x, y] != roads[x, y])
                        {
                            Assert.True(false, $"only pavement changes: ({x},{y}) {plain[x, y]} -> {roads[x, y]}");
                        }
                    }
                }
                Assert.True(File.Exists(kept), "the fuel before the roads is kept");
                Assert.Equal("on", Manifest(caseDir, "RoadsCarryFire"), "recorded as on");
                Assert.Equal("firedx-256", Manifest(caseDir, "RoadsCarryFireMethod"), "by FireDX's road cells");
                Assert.Equal(pavement.ToString(CultureInfo.InvariantCulture), Manifest(caseDir, "RoadsCarryFireCells"), "how many");
                string connectivity = Manifest(caseDir, "RoadsCarryFireConnectivity") ?? string.Empty;
                Assert.True(connectivity.Contains("burnable patches") && log.Any(l => l.Contains("single-cell islets")),
                    "the connectivity before and after is said and recorded: " + connectivity);
                Assert.True(ElmfireCaseValidator.DataTypeOf(fuel) == "Int16", "still Int16: " + ElmfireCaseValidator.DataTypeOf(fuel));
                Assert.True(r.Validation == null || r.Validation.Ok, "the case validates");

                //Again: the same, not applied on top of itself.
                ElmfireCaseBuilder.Build(on).GetAwaiter().GetResult();
                float[,] again = Read(fuel);
                Assert.True(Enumerable.Range(0, again.GetLength(0)).All(x => Enumerable.Range(0, again.GetLength(1)).All(y => again[x, y] == roads[x, y])),
                    "a second build with the toggle on gives the same fuel");

                //Off again: the fuel exactly as before the roads, and nothing kept.
                log.Clear();
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, log)).GetAwaiter().GetResult();
                float[,] back = Read(fuel);
                Assert.True(Enumerable.Range(0, back.GetLength(0)).All(x => Enumerable.Range(0, back.GetLength(1)).All(y => back[x, y] == plain[x, y])),
                    "unticked: the fuel is what it was before");
                Assert.True(!File.Exists(kept) && Manifest(caseDir, "RoadsCarryFire") == "off" && log.Any(l => l.Contains("restored")),
                    "and the build says it restored it");

                //On, then a rebuild from the source: the kept copy is out of date and is not put back over the fresh fuel.
                ElmfireCaseBuilder.Build(on).GetAwaiter().GetResult();
                log.Clear();
                ElmfireCaseBuilder.Options rebuild = c.Options(caseDir, 150.0, log);
                rebuild.RoadsCarryFire = true;
                rebuild.OverwriteExistingLayers = true;
                ElmfireCaseBuilder.Build(rebuild).GetAwaiter().GetResult();
                Assert.True(log.Any(l => l.Contains("out of date")) && Count(Read(fuel), 256f) == 0 && File.Exists(kept),
                    "a rebuild drops the stale copy and applies the roads to the fresh fuel");
            }
        }

        private static void RoadsWithoutRoads()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                var log = new List<string>();
                ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, log);
                o.RoadsCarryFire = true;
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();
                string fuel = Path.Combine(caseDir, "inputs", "fbfm40.tif");
                Assert.Equal(0, Count(Read(fuel), 101f), "nothing became GR1");
                Assert.True(r.Fallbacks.Any(f => f.Contains("no FireDX road cells") && f.Contains("no SUMO network")),
                    "the build says why: " + string.Join(" | ", r.Fallbacks));
                Assert.True(!File.Exists(Path.Combine(caseDir, "inputs", ElmfireCaseBuilder.RoadsFolder, "fbfm40.tif")), "and keeps no copy");
                Assert.True((Manifest(caseDir, "RoadsCarryFireMethod") ?? string.Empty).StartsWith("none"), "recorded as none");
            }
        }

        private static void RoadsFromSumoLanes()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                //A one-lane network running north across the urban strip (91, source rows 45-49), in UTM 34N coordinates: with
                //netOffset 0,0 a SUMO coordinate is the UTM one.
                Assert.True(CrsTransform.TryWgs84To("EPSG:32634", PipelineTests.SyntheticCase.Lat, PipelineTests.SyntheticCase.Lon,
                    out double x0, out double y0), "corner in UTM 34N");
                double yll = System.Math.Floor(y0 - 1000.0);
                string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
                string net = Path.Combine(c.Folder, "sumo", "test.net.xml");
                Directory.CreateDirectory(Path.GetDirectoryName(net));
                File.WriteAllText(net, "<net version=\"1.20\">\n"
                    + "  <location netOffset=\"0.00,0.00\" convBoundary=\"0,0,1,1\" origBoundary=\"0,0,1,1\" projParameter=\"!\"/>\n"
                    + "  <edge id=\"north\" from=\"a\" to=\"b\"><lane id=\"north_0\" index=\"0\" speed=\"13.9\" length=\"600\" shape=\""
                    + $"{F(x0 + 760.0)},{F(yll + 1200.0)} {F(x0 + 760.0)},{F(yll + 1800.0)}\"/></edge>\n</net>\n");

                PREACTInput input = PREACTInput.LoadFromDisk(c.WriteScenario("case", 150.0), out bool _);
                string caseDir = Path.Combine(c.Folder, "case");
                string fuel = Path.Combine(caseDir, "inputs", "fbfm40.tif");

                var log = new List<string>();
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, log)).GetAwaiter().GetResult();
                float[,] plain = Read(fuel);

                log.Clear();
                ElmfireCaseBuilder.Options on = c.Options(caseDir, 150.0, log);
                on.RoadsCarryFire = true;
                on.SumoConfigurationPath = net;
                on.Simulation = input.Simulation.Data;
                ElmfireCaseBuilder.Build(on).GetAwaiter().GetResult();
                float[,] roads = Read(fuel);
                int changed = 0;
                for (int x = 0; x < plain.GetLength(0); ++x)
                {
                    for (int y = 0; y < plain.GetLength(1); ++y)
                    {
                        if (plain[x, y] == roads[x, y]) continue;
                        ++changed;
                        Assert.True(plain[x, y] == 91f && roads[x, y] == 101f, $"only urban cells under the lane change, to GR1: ({x},{y}) {plain[x, y]} -> {roads[x, y]}");
                    }
                }
                Assert.True(changed >= 4 && changed <= 8, "the lane crosses the 5-cell strip: " + changed + " cells");
                Assert.Equal("sumo-lanes", Manifest(caseDir, "RoadsCarryFireMethod"), "by the SUMO lanes");
                string connectivity = Manifest(caseDir, "RoadsCarryFireConnectivity") ?? string.Empty;
                Assert.True(connectivity.StartsWith("burnable patches 2 -> 1"), "the strip split the fuel in two, the lane joins it: " + connectivity);

                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, log)).GetAwaiter().GetResult();
                float[,] back = Read(fuel);
                Assert.True(Enumerable.Range(0, back.GetLength(0)).All(x => Enumerable.Range(0, back.GetLength(1)).All(y => back[x, y] == plain[x, y])),
                    "unticked: the fuel is what it was before");
            }
        }

        private static void SettingsAndBurnable()
        {
            //[ELMFIRE] RoadsCarryFire: read, written back, absent means off.
            using (var c = new PipelineTests.SyntheticCase())
            {
                string off = c.WriteScenario("case", 150.0);
                PREACTInput plain = PREACTInput.LoadFromDisk(off, out bool _);
                Assert.True(!plain.WildfireModule.ElmfireInput.RoadsCarryFire, "absent: off");

                string on = c.WriteScenario("case", 150.0, "RoadsCarryFire=true");
                PREACTInput input = PREACTInput.LoadFromDisk(on, out bool _);
                Assert.True(input.WildfireModule.ElmfireInput.RoadsCarryFire, "read: on");
                string[] written = PREACTInputWriter.Write(input);
                Assert.True(written.Contains("RoadsCarryFire=true"), "written back");
                File.WriteAllLines(on, written);
                Assert.True(PREACTInput.LoadFromDisk(on, out bool _).WildfireModule.ElmfireInput.RoadsCarryFire, "and read again");
                input.WildfireModule.ElmfireInput.RoadsCarryFire = false;
                File.WriteAllLines(on, PREACTInputWriter.Write(input));
                Assert.True(!PREACTInput.LoadFromDisk(on, out bool _).WildfireModule.ElmfireInput.RoadsCarryFire, "unticked: off after a save");
            }

            //ELMFIRE's non-burnable codes (elmfire_init.f90): 90-100, 256, and 0 and below.
            foreach (float code in new[] { 90f, 91f, 98f, 100f, 256f, 0f, -9999f })
            {
                Assert.True(!ElmfireStems.IsBurnable(code), code + " does not burn");
            }
            foreach (float code in new[] { 1f, 101f, 102f, 165f, 204f })
            {
                Assert.True(ElmfireStems.IsBurnable(code), code + " burns");
            }

            //The FireDX keys of tools.ini.
            Assert.Equal("FireDxPython", ToolPaths.KeyOf(ToolPaths.Tool.FireDxPython), "the Python key");
            Assert.Equal("FireDxPackage", ToolPaths.KeyOf(ToolPaths.Tool.FireDx), "the package key");
            Assert.True(ToolPaths.AllTools.Contains(ToolPaths.Tool.FireDxPython) && ToolPaths.AllTools.Contains(ToolPaths.Tool.FireDx),
                "both are tools the window edits");
        }

        private static void RunnerCommand()
        {
            string folder = Directory.CreateTempSubdirectory("preact-firedx-").FullName;
            try
            {
                var o = new FireDxRunner.Options { FireYear = 2018, FootprintsPath = "/data/my footprints.geojson", Attributes = FireDxRunner.AttributePath.Basic };
                List<string> args = FireDxRunner.Arguments(o, "/in/fuel.tif", "/out/run");
                Assert.Equal("--fbfm40 /in/fuel.tif --output-dir /out/run --attributes basic --fire-year 2018 --footprints /data/my footprints.geojson",
                    string.Join(" ", args), "the driver's arguments");
                Assert.True(args.Contains("/data/my footprints.geojson"), "a path with a space stays one argument");
                List<string> plain = FireDxRunner.Arguments(new FireDxRunner.Options(), "/in/fuel.tif", "/out/run");
                Assert.Equal("--fbfm40 /in/fuel.tif --output-dir /out/run --attributes auto", string.Join(" ", plain),
                    "no fire year and no footprints: every building, downloaded footprints, the path chosen by place");

                //The environment: FireDX's source then the version shim first on PYTHONPATH, the conda environment's own
                //folders first on PATH, and PROJ/GDAL pointed at the environment's data or removed - never the host's.
                string env = Path.Combine(folder, "envs", "firedx");
                string python = Windows ? Path.Combine(env, "python.exe") : Path.Combine(env, "bin", "python");
                Directory.CreateDirectory(Path.Combine(env, "bin"));
                Directory.CreateDirectory(Path.Combine(env, "share", "proj"));
                Assert.Equal(env, FireDxRunner.EnvironmentRoot(python), "the environment a Python belongs to");
                var host = new Dictionary<string, string> { ["PATH"] = "/usr/bin", ["PROJ_LIB"] = "/qgis/share/proj", ["PYTHONPATH"] = "/mine", ["GDAL_DATA"] = "/qgis/gdal" };
                Dictionary<string, string> child = FireDxRunner.ChildEnvironment(python, "/src/firedx", "/out/.pyshim", host);
                string sep = Path.PathSeparator.ToString();
                Assert.Equal("/src/firedx" + sep + "/out/.pyshim" + sep + "/mine", child["PYTHONPATH"], "PYTHONPATH: the source, the shim, then what was there");
                Assert.True(child["PATH"].StartsWith(Path.Combine(env, "bin")) && child["PATH"].EndsWith("/usr/bin"), "PATH: the environment first: " + child["PATH"]);
                Assert.Equal(Path.Combine(env, "share", "proj"), child["PROJ_LIB"], "PROJ: the environment's own data");
                Assert.True(child.ContainsKey("GDAL_DATA") && child["GDAL_DATA"] == null, "GDAL_DATA: the host's removed, the environment has none");

                //The version shim and the driver.
                string shim = FireDxRunner.WriteVersionShim(folder);
                Assert.True(File.ReadAllText(Path.Combine(shim, "firedx-0.0.0+preact.dist-info", "METADATA")).Contains("Name: firedx"), "the shim names firedx");
                string script = FireDxRunner.Script();
                Assert.True(script.Contains("def main()") && script.Contains("PREACT-FIREDX-") && script.Contains("NOT part of FireDX"),
                    "the driver is embedded, PREACT's own, and says FireDX is not part of it");

                //Which fuel FireDX runs on.
                string root = Path.Combine(folder, "scenario");
                string landfire = Path.Combine(root, "downloads", "landfire", "s_LF2024_fbfm40.tif");
                Directory.CreateDirectory(Path.GetDirectoryName(landfire));
                RoadFuelTests.WriteRaster(landfire, new float[2, 2], OSGeo.GDAL.DataType.GDT_Int16);
                var e = new ElmfireInput { FuelModelFile = "downloads/landfire/s_LF2024_fbfm40.tif" };
                Assert.Equal(Path.GetFullPath(landfire), FireDxRunner.ResolveFuelSource(root, e, null, out _, out _), "the scenario's fuel layer");

                string output = Path.Combine(root, "downloads", "firedx");
                Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "fbfm40b.tif"), "");
                File.WriteAllLines(Path.Combine(output, FireDxRunner.ProvenanceFileName), new[] { "Fbfm40Source=downloads/landfire/s_LF2024_fbfm40.tif" });
                e.FuelModelFile = "downloads/firedx/fbfm40b.tif";
                Assert.Equal(Path.GetFullPath(landfire), FireDxRunner.ResolveFuelSource(root, e, null, out string note, out _),
                    "FireDX's own output leads back to what it was made from");
                Assert.True(note != null && note.Contains("FireDX's own output"), "and says so: " + note);

                e.FuelModelFile = string.Empty;
                string caseDir = Path.Combine(root, "elmfire");
                Directory.CreateDirectory(Path.Combine(caseDir, "inputs"));
                RoadFuelTests.WriteRaster(Path.Combine(caseDir, "inputs", "fbfm40.tif"), new float[2, 2], OSGeo.GDAL.DataType.GDT_Int16);
                Assert.Equal(Path.GetFullPath(Path.Combine(caseDir, "inputs", "fbfm40.tif")), FireDxRunner.ResolveFuelSource(root, e, caseDir, out _, out _),
                    "no fuel layer named: the case's own");
                Assert.True(FireDxRunner.ResolveFuelSource(root, e, Path.Combine(root, "nocase"), out _, out string none) == null && none.Contains("LANDFIRE"),
                    "no fuel at all: refused, saying where to get one");
                e.FuelModelStandard = ElmfireInput.FuelModelStandards.FBFM13;
                Assert.True(FireDxRunner.ResolveFuelSource(root, e, caseDir, out _, out string thirteen) == null && thirteen.Contains("Anderson 13"),
                    "an FBFM13 scenario: refused, as it has no urban 91");

                //Applying a run: the six keys and the building model.
                var result = new FireDxRunner.Result { Ok = true };
                foreach (FireDxRunner.Layer layer in FireDxRunner.Layers) result.Layers[layer.Key] = "downloads/firedx/" + layer.File;
                var applied = new ElmfireInput();
                FireDxRunner.Apply(result, applied);
                Assert.True(applied.FuelModelFile == "downloads/firedx/fbfm40b.tif" && applied.BuildingAreaFile == "downloads/firedx/baa_m.tif"
                            && applied.BuildingSeparationFile == "downloads/firedx/ssd_min.tif" && applied.BuildingNonBurnableFractionFile == "downloads/firedx/nbf.tif"
                            && applied.BuildingFootprintFractionFile == "downloads/firedx/ff.tif" && applied.BuildingFuelModelFile == "downloads/firedx/bfm.tif"
                            && applied.Namelist.USE_BLDG_SPREAD_MODEL,
                    "the fuel, the five building layers (the plan dimension in m, baa_m) and the building spread model");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }

        private static void RunnerFailures()
        {
            string folder = Directory.CreateTempSubdirectory("preact-firedx-").FullName;
            string before = Environment.GetEnvironmentVariable(ToolPaths.FileVariable);
            Environment.SetEnvironmentVariable(ToolPaths.FileVariable, Path.Combine(folder, "tools.ini"));
            try
            {
                using (var c = new PipelineTests.SyntheticCase())
                {
                    string package = Path.Combine(folder, "firedx-src");
                    Directory.CreateDirectory(Path.Combine(package, "firedx"));
                    File.WriteAllText(Path.Combine(package, "firedx", "generate.py"), "");

                    FireDxRunner.Options Options(string python) => new FireDxRunner.Options
                    {
                        Python = python,
                        Package = package,
                        Root = c.Folder,
                        Fbfm40Path = c.FuelPath,
                        Log = _ => { },
                    };

                    FireDxRunner.Result missing = FireDxRunner.Run(Options(Path.Combine(folder, "no-python")));
                    Assert.True(!missing.Ok && missing.Message.Contains("is not there"), "a named Python that is not there: " + missing.Message);

                    FireDxRunner.Result noPackage = FireDxRunner.Run(new FireDxRunner.Options
                    {
                        Python = c.FuelPath, Package = Path.Combine(folder, "nowhere"), Root = c.Folder, Fbfm40Path = c.FuelPath, Log = _ => { },
                    });
                    Assert.True(!noPackage.Ok && noPackage.Message.Contains("git submodule update --init " + FireDxRunner.SubmodulePath),
                        "no FireDX source: how to get the submodule: " + noPackage.Message);

                    if (Windows)
                    {
                        return; //The stand-in Pythons below are shell scripts.
                    }

                    string Fake(string name, string body)
                    {
                        string path = Path.Combine(folder, name, "bin", "python");
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
                        if (!OperatingSystem.IsWindows())
                        {
                            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        }
                        return path;
                    }

                    FireDxRunner.Result modules = FireDxRunner.Run(Options(Fake("nodeps",
                        "echo 'PREACT-FIREDX-MISSING-MODULES: geopandas, rasterio'; exit 3")));
                    Assert.True(!modules.Ok && modules.MissingModules.SequenceEqual(new[] { "geopandas", "rasterio" })
                                && modules.Message.Contains("lacks geopandas, rasterio") && modules.Message.Contains("environment.yml"),
                        "missing modules, named, with how to get them: " + modules.Message);
                    Assert.True(File.ReadAllText(modules.LogFile).Contains("MISSING-MODULES"), "and the log holds the output");

                    FireDxRunner.Result offline = FireDxRunner.Run(Options(Fake("offline",
                        "echo 'PREACT-FIREDX-ATTRIBUTES: california'; echo 'PREACT-FIREDX-OFFLINE: USACE National Structure Inventory (USA only): https://nsi (refused)'; exit 4")));
                    Assert.True(!offline.Ok && offline.Unreachable.Count == 1 && offline.Message.Contains("needs the network")
                                && offline.Message.Contains("footprints file") && offline.Message.Contains("basic attributes"),
                        "no network: what could not be reached and the ways round it: " + offline.Message);

                    FireDxRunner.Result abroad = FireDxRunner.Run(Options(Fake("abroad",
                        "echo 'PREACT-FIREDX-NOT-CALIFORNIA: not in California'; exit 6")));
                    Assert.True(!abroad.Ok && abroad.Message.Contains("only has data in California") && abroad.Message.Contains("basic"),
                        "outside California: the US/California-only join named, and the basic path offered: " + abroad.Message);

                    FireDxRunner.Result empty = FireDxRunner.Run(Options(Fake("empty", "echo 'PREACT-FIREDX-NO-BUILDINGS: none in the area'; exit 7")));
                    Assert.True(!empty.Ok && empty.Message.Contains("found no buildings"), "no buildings: " + empty.Message);

                    FireDxRunner.Result crash = FireDxRunner.Run(Options(Fake("crash", "echo 'Traceback ...' >&2; echo 'PREACT-FIREDX-ERROR: KeyError: tile_id'; exit 1")));
                    Assert.True(!crash.Ok && crash.Message.Contains("KeyError: tile_id") && crash.Message.Contains(FireDxRunner.LogFileName),
                        "a Python error: its last words and the log: " + crash.Message);

                    FireDxRunner.Result silent = FireDxRunner.Run(Options(Fake("silent", "exit 0")));
                    Assert.True(!silent.Ok, "exit 0 without finishing is not success: " + silent.Message);

                    //A stop kills the run and changes nothing.
                    using (var stop = new CancellationTokenSource())
                    {
                        FireDxRunner.Options slow = Options(Fake("slow", "echo started; sleep 30"));
                        slow.Cancellation = stop.Token;
                        stop.CancelAfter(TimeSpan.FromSeconds(1));
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        FireDxRunner.Result stopped = FireDxRunner.Run(slow);
                        Assert.True(!stopped.Ok && stopped.Stopped && clock.Elapsed < TimeSpan.FromSeconds(20) && stopped.Message.Contains("stopped"),
                            $"a stop ends it ({clock.Elapsed.TotalSeconds:F1} s): " + stopped.Message);
                    }
                    Assert.True(!File.Exists(Path.Combine(c.Folder, "downloads", "firedx", "fbfm40b.tif")), "no failed run left layers in place");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable(ToolPaths.FileVariable, before);
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }
}
