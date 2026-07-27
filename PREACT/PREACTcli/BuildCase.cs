using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PREACT.Math;
using PREACT.Tools;
using PREACT.Utility;

namespace PREACTcli
{
    /// <summary>
    /// <c>PREACTcli build-case</c> — builds a complete ELMFIRE case folder for a WUInity domain
    /// from nothing but the <c>.wui</c> file's lat/lon and domain size, so a probabilistic trigger
    /// campaign no longer needs a hand-prepared <c>ELMFIRE/inputs</c> tree to exist first.
    ///
    /// Thin wrapper over <see cref="ElmfireCaseBuilder"/>: this half only parses arguments, reads
    /// the domain out of the <c>.wui</c>, and resolves the OpenTopography key. The actual
    /// download/warp/derive work lives in core so Unity can drive the same code path.
    /// </summary>
    internal static class BuildCase
    {
        /// <summary>ELMFIRE input stems a user raster can be supplied for, each as <c>--&lt;stem&gt; &lt;path&gt;</c>.</summary>
        private static readonly string[] UserRasterStems =
        {
            "fbfm13", "cc", "ch", "cbh", "cbd",
            "bldg_area_avg", "bldg_separation_distance", "bldg_nonburnable_frac",
            "bldg_footprint_frac", "bldg_fuel_model",
            "ignition_mask", "barriers",
        };

        public static int Run(string[] args)
        {
            var o = new ElmfireCaseBuilder.Options { Log = Console.WriteLine };
            string wui = null;
            string apiKey = null;

            for (int i = 0; i < args.Length; ++i)
            {
                string a = args[i];
                switch (a)
                {
                    case "--wui":       wui = Next(args, ref i); break;
                    case "--out":       o.OutputDirectory = Next(args, ref i); break;
                    case "--cellsize":  o.CellSizeMetres = Dbl(Next(args, ref i)); break;
                    case "--padding":   o.PaddingMetres = Dbl(Next(args, ref i)); break;
                    case "--dem-type":  o.DemType = Next(args, ref i); break;
                    case "--dem":       o.LocalDemPath = Next(args, ref i); break;
                    case "--api-key":   apiKey = Next(args, ref i); break;
                    case "--gdal":      o.PathToGdal = Next(args, ref i); break;
                    case "--tstop":     o.SimulationTstopSeconds = Dbl(Next(args, ref i)); break;
                    case "--windninja":     o.Weather.WindNinjaExe = Next(args, ref i); break;
                    case "--wn-mesh":       o.Weather.WindNinjaMesh = Next(args, ref i); break;
                    case "--wn-vegetation": o.Weather.WindNinjaVegetation = Next(args, ref i); break;
                    case "--climatology-from": o.Weather.ArchiveStartYear = int.Parse(Next(args, ref i)); break;
                    case "--climatology-to":   o.Weather.ArchiveEndYear = int.Parse(Next(args, ref i)); break;
                    case "--conditioning-days": o.Weather.ConditioningDays = int.Parse(Next(args, ref i)); break;
                    case "--burning-from":  o.Weather.BurningPeriodStartHour = int.Parse(Next(args, ref i)); break;
                    case "--burning-to":    o.Weather.BurningPeriodEndHour = int.Parse(Next(args, ref i)); break;
                    case "--weather-seed":  o.Weather.Seed = int.Parse(Next(args, ref i)); break;
                    case "--weather-date":  o.Weather.ForceDate = DateTime.Parse(Next(args, ref i), CultureInfo.InvariantCulture); break;
                    case "--no-climatology": o.Weather.UseClimatology = false; break;
                    case "--wind":      o.Weather.FallbackWindSpeedMps = Dbl(Next(args, ref i)); break;
                    case "--wind-dir":  o.Weather.FallbackWindDirectionDeg = Dbl(Next(args, ref i)); break;
                    case "--m1":        o.Weather.FallbackM1Percent = Dbl(Next(args, ref i)); break;
                    case "--m10":       o.Weather.FallbackM10Percent = Dbl(Next(args, ref i)); break;
                    case "--m100":      o.Weather.FallbackM100Percent = Dbl(Next(args, ref i)); break;
                    case "--copy":      o.CopyFiles.Add(Next(args, ref i)); break;
                    case "--force":     o.Force = true; break;
                    default:
                        if (a.StartsWith("--") && Array.IndexOf(UserRasterStems, a.Substring(2)) >= 0)
                        {
                            o.UserRasters[a.Substring(2)] = Next(args, ref i);
                            break;
                        }
                        Console.Error.WriteLine($"Unknown option: {a}");
                        PrintUsage();
                        return 1;
                }
            }

            if (string.IsNullOrEmpty(wui) || string.IsNullOrEmpty(o.OutputDirectory))
            {
                Console.Error.WriteLine("--wui and --out are both required.");
                PrintUsage();
                return 1;
            }

            if (!File.Exists(wui))
            {
                Console.Error.WriteLine("No such .wui file: " + wui);
                return 1;
            }

            if (!TryReadDomain(wui, o))
            {
                return 1;
            }

            //Env var as well as a flag: the key is a secret, and putting it in a flag puts it in
            //the shell history of every campaign launch. Not needed at all with --dem.
            o.OpenTopographyApiKey = apiKey
                ?? Environment.GetEnvironmentVariable("OPENTOPOGRAPHY_API_KEY")
                ?? TryReadUnityApiKey();

            if (string.IsNullOrEmpty(o.LocalDemPath) && string.IsNullOrEmpty(o.OpenTopographyApiKey))
            {
                Console.Error.WriteLine(
                    "No DEM source. Either pass --dem <file> to use a local DEM, or supply an\n" +
                    "OpenTopography API key (--api-key, $OPENTOPOGRAPHY_API_KEY, or\n" +
                    "WUInity/Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt).");
                return 1;
            }

            o.Weather.WindNinjaExe ??= FindWindNinja();

            try
            {
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();

                Console.WriteLine();
                Console.WriteLine($"Case built in {Path.GetFullPath(o.OutputDirectory)}");
                Console.WriteLine($"  grid      {r.Grid.Header.Ncols}x{r.Grid.Header.Nrows} @ {r.Grid.Header.CellSize:F1} m ({r.Grid.Epsg})");
                Console.WriteLine($"  layers    {string.Join(", ", r.Written)}");
                if (r.Skipped.Count > 0)
                {
                    Console.WriteLine($"  missing   {string.Join(", ", r.Skipped)}");
                }
                if (r.Defaulted.Count > 0)
                {
                    Console.WriteLine($"  defaulted {string.Join(", ", r.Defaulted)} to zero (surface fire only)");
                }
                if (!r.Written.Contains("fbfm13"))
                {
                    Console.WriteLine("  WARNING   no fuel model raster (--fbfm13); ELMFIRE will refuse to start on this case.");
                }
                Console.WriteLine($"  namelist  {r.NamelistPath}");

                if (r.Weather != null)
                {
                    string day = r.Weather.Day.HasValue
                        ? $"{r.Weather.Day.Value.Date:yyyy-MM-dd} (FWI {r.Weather.Day.Value.Fwi:F1}, 1 of {r.Weather.AnnualMaximaCount} annual peaks)"
                        : "none (uniform weather)";
                    Console.WriteLine($"  weather   {day}");
                    Console.WriteLine($"            wind {r.Weather.MeanWindSpeedMph:F1} mph mean" +
                                      (r.Weather.WindNinjaUsed ? " (WindNinja, terrain-resolved)" : " (uniform)"));
                    Console.WriteLine($"            dead moisture {r.Weather.MeanM1Percent:F1}/{r.Weather.MeanM10Percent:F1}/{r.Weather.MeanM100Percent:F1} %" +
                                      (r.Weather.NelsonUsed ? " (Nelson, per-cell)" : " (uniform)"));

                    //Surfaced rather than swallowed: a case that quietly used placeholder weather
                    //looks identical on disk to one that used the real chain.
                    foreach (string f in r.Weather.Fallbacks) Console.WriteLine($"            ! {f}");
                }
                Console.WriteLine();
                Console.WriteLine("Run a campaign against it with:");
                Console.WriteLine($"  PREACTcli converge-trigger --wui {wui} --max 200 --resume \\");
                Console.WriteLine($"    --elmfire <elmfire.exe> --elmfire-template {r.NamelistPath} \\");
                Console.WriteLine($"    --elmfire-inputs {r.InputsDirectory} --gdal \"<gdal bin>\"");
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("build-case failed: " + e.Message);
                return 1;
            }
        }

        /// <summary>
        /// Pulls the domain out of the <c>.wui</c>'s <c>[Simulation]</c> section. Deliberately a
        /// local parse rather than a full <c>PREACTInput</c> load: that path validates (and would
        /// reject) the whole scenario — population, SUMO config, fire rasters — none of which
        /// exists yet at case-build time. This is the one step that must run *before* the case is
        /// complete, so it can only depend on the four keys that describe the domain itself.
        /// </summary>
        /// <summary>
        /// The domain centre in (lat, lon), for callers that need the same figure this builder
        /// derives — the point the ERA5 archive is queried at and solar geometry is computed for.
        /// Returns false if the <c>.wui</c> does not describe a domain.
        /// </summary>
        public static bool TryReadCentreLatLon(string wuiPath, double paddingMetres, out Vector2d centre)
        {
            centre = default;
            var probe = new ElmfireCaseBuilder.Options { PaddingMetres = paddingMetres };
            if (!TryReadDomain(wuiPath, probe, quiet: true)) return false;

            //Half the unpadded domain east/north of the corner: the padding is symmetric, so it
            //does not move the centre.
            Vector2d halfDeg = PREACT.Population.LocalGPWData.SizeToDegrees(
                probe.LowerLeftLatLon,
                new Vector2d(0.5 * probe.DomainSizeMetres.x, 0.5 * probe.DomainSizeMetres.y));

            centre = new Vector2d(probe.LowerLeftLatLon.x + halfDeg.y, probe.LowerLeftLatLon.y + halfDeg.x);
            return true;
        }

        private static bool TryReadDomain(string wuiPath, ElmfireCaseBuilder.Options o, bool quiet = false)
        {
            string[] lines = File.ReadAllLines(wuiPath);
            string name = Get(lines, "Simulation", "Name");
            string latLon = Get(lines, "Simulation", "LowerLeftLatLon");
            string size = Get(lines, "Simulation", "DomainSize");
            string start = Get(lines, "Simulation", "StartDateTime");

            if (!TryPair(latLon, out double lat, out double lon))
            {
                Console.Error.WriteLine("[Simulation] LowerLeftLatLon missing or unparseable in " + wuiPath);
                return false;
            }

            if (!TryPair(size, out double east, out double north))
            {
                Console.Error.WriteLine("[Simulation] DomainSize missing or unparseable in " + wuiPath);
                return false;
            }

            o.Name = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(wuiPath) : name;
            o.LowerLeftLatLon = new Vector2d(lat, lon);
            o.DomainSizeMetres = new Vector2d(east, north);

            if (!string.IsNullOrEmpty(start) &&
                DateTime.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
            {
                o.StartDateTime = dt;
            }

            if (!quiet)
            {
                Console.WriteLine($"Case '{o.Name}': lower-left {lat},{lon}, domain {east}x{north} m, start {o.StartDateTime:u}");
            }
            return true;
        }

        private static string Get(string[] lines, string section, string key)
        {
            bool inSection = false;
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inSection = string.Equals(line.Substring(1, line.Length - 2), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection || line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(eq + 1).Trim();
                }
            }
            return null;
        }

        private static bool TryPair(string s, out double a, out double b)
        {
            a = b = 0;
            if (string.IsNullOrEmpty(s)) return false;
            string[] parts = s.Split(',');
            return parts.Length == 2
                   && double.TryParse(parts[0].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out a)
                   && double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out b);
        }

        /// <summary>
        /// Falls back to the gitignored Unity resource the editor already reads the key from, so a
        /// machine set up for the Unity build does not need the key configured twice.
        /// </summary>
        private static string TryReadUnityApiKey()
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
                for (int up = 0; up < 8 && dir != null; ++up, dir = Path.GetDirectoryName(dir))
                {
                    string p = Path.Combine(dir, "WUInity", "Assets", "Resources", "OpenTopography", "OpenTopographyConfiguration.txt");
                    if (!File.Exists(p)) continue;

                    foreach (string part in File.ReadAllText(p).Split('"'))
                    {
                        //{"ApiKey":"<key>"} - the value is the first non-empty token that is
                        //neither the property name nor JSON punctuation.
                        string t = part.Trim();
                        if (t.Length > 0 && t != "ApiKey" && !t.StartsWith("{") && !t.StartsWith(":") && !t.StartsWith("}"))
                        {
                            return t;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Looks for the Windows installer's <c>WindNinja_cli.exe</c> so the terrain-wind stage
        /// works without being pointed at it. Only the standard install root is probed — anywhere
        /// else, pass <c>--windninja</c>.
        /// </summary>
        public static string FindWindNinja()
        {
            try
            {
                foreach (string root in new[] { @"C:\WindNinja", @"C:\Program Files\WindNinja" })
                {
                    if (!Directory.Exists(root)) continue;
                    //Installs are versioned (WindNinja-3.12.1\bin\...), so take the newest.
                    string[] hits = Directory.GetFiles(root, "WindNinja_cli.exe", SearchOption.AllDirectories);
                    if (hits.Length > 0)
                    {
                        Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
                        return hits[hits.Length - 1];
                    }
                }
            }
            catch { }
            return null;
        }

        private static string Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Missing value after {args[i]}");
            return args[++i];
        }

        private static double Dbl(string s) => double.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture);

        public static void PrintUsage()
        {
            Console.WriteLine("  PREACTcli build-case --wui <case.wui> --out <dir> [options]");
            Console.WriteLine("      Builds an ELMFIRE case (dem/slp/asp/adj/phi + namelist) for the .wui's domain.");
            Console.WriteLine("      --cellsize <m>     master-grid resolution (default 30)");
            Console.WriteLine("      --padding <m>      margin around the domain so fire can grow past it (default 2000)");
            Console.WriteLine("      --dem <file>       use a local DEM instead of downloading one (no API key needed)");
            Console.WriteLine("      --dem-type <t>     COP30 (default) | COP90 | SRTMGL1 | SRTMGL3");
            Console.WriteLine("      --api-key <k>      OpenTopography key (else $OPENTOPOGRAPHY_API_KEY, else Unity Resources)");
            Console.WriteLine("      --gdal <bin>       GDAL bin directory written into the namelist's PATH_TO_GDAL");
            Console.WriteLine("      --tstop <s>        SIMULATION_TSTOP (default 72000)");
            Console.WriteLine("      --copy <file>      copy a loose file (e.g. fuel_models.csv) into inputs/");
            Console.WriteLine("      baseline weather (ERA5 climatology -> WindNinja -> Nelson):");
            Console.WriteLine("      --climatology-from/-to <year>  archive range (default 2000 to last complete year)");
            Console.WriteLine("      --weather-date <date>          use this historical day instead of sampling one");
            Console.WriteLine("      --weather-seed <n>             seeds which peak day is drawn");
            Console.WriteLine("      --conditioning-days <n>        Nelson spin-up window (default 20)");
            Console.WriteLine("      --burning-from/-to <0-23>      burning period the moisture minimum is taken over (default 10-18)");
            Console.WriteLine("      --windninja <exe>              WindNinja_cli (auto-detected under C:\\WindNinja)");
            Console.WriteLine("      --wn-mesh <choice>             coarse (default) | medium | fine");
            Console.WriteLine("      --wn-vegetation <type>         grass (default) | brush | trees");
            Console.WriteLine("      --no-climatology               skip the chain, write uniform rasters");
            Console.WriteLine("      fallbacks, used only where a stage above cannot run:");
            Console.WriteLine("      --wind <m/s> --wind-dir <deg> --m1/--m10/--m100 <%>");
            Console.WriteLine("      --force            build into a non-empty output directory");
            Console.WriteLine("      user-supplied rasters, warped onto the master grid:");
            Console.WriteLine("        " + string.Join(", ", Array.ConvertAll(UserRasterStems, s => "--" + s + " <tif>")));
        }
    }
}
