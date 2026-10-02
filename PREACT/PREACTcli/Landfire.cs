using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PREACT.Input;
using PREACT.Tools;

namespace PREACTcli
{
    /// <summary>
    /// <c>PREACTcli landfire</c> - the GUI's fuels-step LANDFIRE download for a scenario: the release <c>[ELMFIRE]
    /// LandfireVersion</c> picks (or <c>--version</c>), over the case's padded domain, split into the fuel and canopy
    /// source layers, with the scaling flags their units call for. The same engine function the GUI calls
    /// (<see cref="LandfireFuels"/>), so the two cannot download different things for one <c>.wui</c>.
    /// </summary>
    internal static class Landfire
    {
        public static int Run(string[] args)
        {
            string wui = null, version = null, email = null, service = null;
            bool updateWui = false;
            int? pollSeconds = null;
            try
            {
                new CliArgs()
                    .Value("--wui", v => wui = v)
                    .Value("--version", v =>
                    {
                        if (LandfireVersions.Normalise(v) == null)
                            throw new ArgumentException($"--version takes {LandfireVersions.Closest} or one of "
                                + string.Join(", ", LandfireVersions.Names()) + ", not '" + v + "'.");
                        version = LandfireVersions.Normalise(v);
                    })
                    .Value("--email", v => email = v)
                    .Value("--service-url", v => service = v)
                    .Int("--poll-seconds", v => pollSeconds = v, 1, 600)
                    .Switch("--update-wui", () => updateWui = true)
                    .Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                return 2;
            }

            if (wui == null)
            {
                Console.Error.WriteLine("ERROR: --wui is required.");
                PrintUsage();
                return 2;
            }

            string wuiPath = Path.GetFullPath(wui);
            PREACTInput input = PREACTInput.LoadFromDisk(wuiPath, out bool _);
            if (input?.Simulation == null || input.WildfireModule?.ElmfireInput == null)
            {
                Console.Error.WriteLine("ERROR: could not read the scenario's [Simulation] and [ELMFIRE] sections.");
                return 1;
            }

            LandfireFuels.Options options = LandfireFuels.Options.FromScenario(input);
            if (version != null) options.Version = version;
            if (email != null) options.Email = email;
            if (service != null) options.ServiceUrl = service;
            if (pollSeconds.HasValue) options.PollInterval = TimeSpan.FromSeconds(pollSeconds.Value);
            options.Log = Console.WriteLine;

            LandfireFuels.Result result;
            try
            {
                result = LandfireFuels.DownloadAsync(options).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: " + Program.Describe(e));
                return 1;
            }

            ElmfireInput e2 = new ElmfireInput();
            LandfireFuels.Apply(result, e2);
            var keys = new List<(string Section, string Key, string Value)>
            {
                ("ELMFIRE", nameof(ElmfireInput.FuelModelFile), e2.FuelModelFile),
                ("ELMFIRE", nameof(ElmfireInput.FuelModelStandard), e2.FuelModelStandard.ToString()),
            };
            if (e2.CanopyCoverFile.Length > 0) keys.Add(("ELMFIRE", nameof(ElmfireInput.CanopyCoverFile), e2.CanopyCoverFile));
            if (e2.CanopyHeightFile.Length > 0) keys.Add(("ELMFIRE", nameof(ElmfireInput.CanopyHeightFile), e2.CanopyHeightFile));
            if (e2.CanopyBaseHeightFile.Length > 0) keys.Add(("ELMFIRE", nameof(ElmfireInput.CanopyBaseHeightFile), e2.CanopyBaseHeightFile));
            if (e2.CanopyBulkDensityFile.Length > 0) keys.Add(("ELMFIRE", nameof(ElmfireInput.CanopyBulkDensityFile), e2.CanopyBulkDensityFile));
            if (version != null) keys.Add(("ELMFIRE", nameof(ElmfireInput.LandfireVersion), version));
            keys.Add((ElmfireInput.NamelistSection, "CC_IN_PERCENT", Bool(e2.Namelist.CC_IN_PERCENT)));
            keys.Add((ElmfireInput.NamelistSection, "CH_TIMES_10", Bool(e2.Namelist.CH_TIMES_10)));
            keys.Add((ElmfireInput.NamelistSection, "CBH_TIMES_10", Bool(e2.Namelist.CBH_TIMES_10)));
            keys.Add((ElmfireInput.NamelistSection, "CBD_TIMES_100", Bool(e2.Namelist.CBD_TIMES_100)));

            Console.WriteLine();
            Console.WriteLine($"{result.Release} layers; the keys that name them" + (updateWui ? ":" : " (run again with --update-wui to write them):"));
            foreach (var k in keys) Console.WriteLine($"  [{k.Section}] {k.Key}={k.Value}");

            if (updateWui)
            {
                string[] lines = File.ReadAllLines(wuiPath);
                foreach (var k in keys) lines = WuiText.Set(lines, k.Section, k.Key, k.Value);
                File.WriteAllLines(wuiPath, lines);
                Console.WriteLine($"Recorded in {wuiPath} (--update-wui; nothing else in it changed).");
            }
            if (result.RemovedCaseLayers.Count > 0)
            {
                Console.WriteLine("Build the case again (build-case) to warp them in: its old "
                    + string.Join(", ", result.RemovedCaseLayers) + " were moved to inputs/" + PREACT.Utility.ElmfireCaseBuilder.ReplacedFolder + ".");
            }
            return 0;
        }

        private static string Bool(bool b) => b ? "true" : "false";

        public static void PrintUsage()
        {
            Console.WriteLine("  PREACTcli landfire --wui <file.wui> [--version closest|" + string.Join("|", LandfireVersions.Names())
                              + "] [--email <you@example.org>] [--poll-seconds <n=20>] [--update-wui]");
            Console.WriteLine("      LANDFIRE fuel model and canopy (US) for the scenario's padded fire domain, as the GUI's fuels step");
            Console.WriteLine("      downloads them: the release [ELMFIRE] LandfireVersion picks unless --version says, split into");
            Console.WriteLine("      downloads/landfire/<name>_<release>_<stem>.tif. LFPS asks for a contact e-mail: --email, else");
            Console.WriteLine("      LANDFIRE_EMAIL, else the one the GUI keeps for this user (" + LandfireContact.SettingsPath + ").");
            Console.WriteLine("      --update-wui writes the source-layer keys and the canopy scaling flags into the .wui.");
        }
    }
}
