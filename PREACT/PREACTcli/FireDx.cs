using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using PREACT.Input;
using PREACT.Utility;

namespace PREACTcli
{
    /// <summary>
    /// <c>PREACTcli firedx</c> - the GUI's "Prepare buildings (FireDX)" for a scenario, head-less: FireDX run on the scenario's
    /// FBFM40 fuel source, into <c>downloads/firedx/</c>, and (with <c>--update-wui</c>) the fuel and the five building layers
    /// named in the <c>.wui</c>. The same engine function the GUI calls (<see cref="FireDxRunner"/>).
    /// </summary>
    internal static class FireDx
    {
        public static int Run(string[] args)
        {
            string wui = null, footprints = null, python = null, package = null, fireYear = null, attributes = null;
            bool updateWui = false;
            try
            {
                new CliArgs()
                    .Value("--wui", v => wui = v)
                    .Value("--fire-year", v => fireYear = v)
                    .Value("--footprints", v => footprints = v)
                    .Value("--attributes", v => attributes = v)
                    .Value("--python", v => python = v)
                    .Value("--firedx", v => package = v)
                    .Switch("--update-wui", () => updateWui = true)
                    .Parse(args);
            }
            catch (ArgumentException bad)
            {
                Console.Error.WriteLine("ERROR: " + bad.Message);
                return 2;
            }

            if (wui == null)
            {
                Console.Error.WriteLine("ERROR: --wui is required.");
                PrintUsage();
                return 2;
            }

            FireDxRunner.AttributePath path = FireDxRunner.AttributePath.Auto;
            if (attributes != null && !Enum.TryParse(attributes, true, out path))
            {
                Console.Error.WriteLine("ERROR: --attributes takes auto, california or basic, not '" + attributes + "'.");
                return 2;
            }

            string wuiPath = Path.GetFullPath(wui);
            PREACTInput input = PREACTInput.LoadFromDisk(wuiPath, out bool _);
            if (input?.Simulation == null || input.WildfireModule?.ElmfireInput == null)
            {
                Console.Error.WriteLine("ERROR: could not read the scenario's [Simulation] and [ELMFIRE] sections.");
                return 1;
            }

            int year = 0;
            if (fireYear != null)
            {
                if (string.Equals(fireYear, "scenario", StringComparison.OrdinalIgnoreCase))
                {
                    year = input.Simulation.StartDateTime.Year;
                }
                else if (!int.TryParse(fireYear, NumberStyles.Integer, CultureInfo.InvariantCulture, out year) || year < 1800 || year > 9999)
                {
                    Console.Error.WriteLine("ERROR: --fire-year takes a year or 'scenario' (the scenario's start year), not '" + fireYear + "'.");
                    return 2;
                }
            }

            ElmfireInput e = input.WildfireModule.ElmfireInput;
            string caseDir = ElmfireCoupling.CaseDirectoryPath(input.RootFolder, e);
            string fuel = FireDxRunner.ResolveFuelSource(input.RootFolder, e, caseDir, out string note, out string problem);
            if (fuel == null)
            {
                Console.Error.WriteLine("ERROR: " + problem);
                return 1;
            }
            if (note != null) Console.WriteLine(note);
            Console.WriteLine("FBFM40: " + fuel);

            using (var stop = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler onCancel = (_, a) =>
                {
                    a.Cancel = true;
                    stop.Cancel();
                };
                Console.CancelKeyPress += onCancel;
                FireDxRunner.Result result;
                try
                {
                    result = FireDxRunner.Run(new FireDxRunner.Options
                    {
                        Python = python == null ? null : Path.GetFullPath(python),
                        Package = package == null ? null : Path.GetFullPath(package),
                        Root = input.RootFolder,
                        Fbfm40Path = fuel,
                        FireYear = year,
                        FootprintsPath = footprints == null ? null : Path.GetFullPath(footprints),
                        Attributes = path,
                        CaseDirectory = updateWui ? caseDir : null,
                        Log = Console.WriteLine,
                        Cancellation = stop.Token,
                    });
                }
                finally
                {
                    Console.CancelKeyPress -= onCancel;
                }

                if (!result.Ok)
                {
                    Console.Error.WriteLine("ERROR: " + result.Message);
                    return result.Stopped ? 130 : 1;
                }
                Console.WriteLine(result.Message);

                var applied = new ElmfireInput();
                FireDxRunner.Apply(result, applied);
                var keys = new List<(string Section, string Key, string Value)>
                {
                    ("ELMFIRE", nameof(ElmfireInput.FuelModelFile), applied.FuelModelFile),
                    ("ELMFIRE", nameof(ElmfireInput.FuelModelStandard), applied.FuelModelStandard.ToString()),
                    ("ELMFIRE", nameof(ElmfireInput.BuildingAreaFile), applied.BuildingAreaFile),
                    ("ELMFIRE", nameof(ElmfireInput.BuildingSeparationFile), applied.BuildingSeparationFile),
                    ("ELMFIRE", nameof(ElmfireInput.BuildingNonBurnableFractionFile), applied.BuildingNonBurnableFractionFile),
                    ("ELMFIRE", nameof(ElmfireInput.BuildingFootprintFractionFile), applied.BuildingFootprintFractionFile),
                    ("ELMFIRE", nameof(ElmfireInput.BuildingFuelModelFile), applied.BuildingFuelModelFile),
                    (ElmfireInput.NamelistSection, "USE_BLDG_SPREAD_MODEL", "true"),
                };

                Console.WriteLine();
                Console.WriteLine("The keys that name them" + (updateWui ? ":" : " (run again with --update-wui to write them):"));
                foreach (var k in keys) Console.WriteLine($"  [{k.Section}] {k.Key}={k.Value}");
                if (updateWui)
                {
                    string[] lines = File.ReadAllLines(wuiPath);
                    foreach (var k in keys) lines = WuiText.Set(lines, k.Section, k.Key, k.Value);
                    File.WriteAllLines(wuiPath, lines);
                    Console.WriteLine($"Recorded in {wuiPath} (--update-wui; nothing else in it changed).");
                    if (result.RemovedCaseLayers.Count > 0)
                    {
                        Console.WriteLine("Build the case again (build-case) to warp them in: its old " + string.Join(", ", result.RemovedCaseLayers)
                                          + (result.RemovedCaseLayers.Count == 1 ? " was" : " were") + " moved to inputs/" + ElmfireCaseBuilder.ReplacedFolder + ".");
                    }
                }
                Console.WriteLine("FireDX's log: " + result.LogFile);
                return 0;
            }
        }

        public static void PrintUsage()
        {
            Console.WriteLine("  PREACTcli firedx --wui <file.wui> [--fire-year <year>|scenario] [--footprints <file>]");
            Console.WriteLine("                   [--attributes auto|california|basic] [--python <python>] [--firedx <folder>] [--update-wui]");
            Console.WriteLine("      FireDX's building layers for the scenario: run on its FBFM40 fuel source ([ELMFIRE] FuelModelFile,");
            Console.WriteLine("      else the case's fbfm40.tif) into downloads/firedx/ - fbfm40b.tif (urban 91 split into buildings, 91,");
            Console.WriteLine("      and pavement/roads, 256), baa_m, ssd_min, nbf, ff and bfm. --update-wui names them as the fuel and");
            Console.WriteLine("      building source layers and switches the building spread model on. Footprints are downloaded");
            Console.WriteLine("      (Microsoft, OpenStreetMap) unless --footprints names a file. --attributes: california is FireDX's");
            Console.WriteLine("      own join (US structure inventory, CAL FIRE zones; California only, online), basic takes every building");
            Console.WriteLine("      as residential and works anywhere, auto (default) picks by where the area is. --fire-year leaves out");
            Console.WriteLine("      buildings built later (a hindcast). Python: --python, else the FireDxPython setting in "
                              + ToolPaths.SettingsFile + ",");
            Console.WriteLine("      else a conda environment named firedx. FireDX: --firedx, else FireDxPackage, else the submodule");
            Console.WriteLine("      " + FireDxRunner.SubmodulePath + ".");
        }
    }
}
