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
                        //Grass west of the middle, timber east of it, a strip of urban (non-burnable) along the south.
                        fuel[x, y] = y < 10 ? 91f : x < 67 ? 102f : 165f;
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

        internal static string Tail(string text, int lines = 6)
        {
            string[] all = (text ?? "").Split('\n');
            return string.Join(" | ", all.Skip(System.Math.Max(0, all.Length - lines)).Select(l => l.TrimEnd()));
        }
    }
}
