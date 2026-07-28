using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace PREACT.Utility
{
    /// <summary>
    /// Builds a SUMO network and configuration from an OSM extract by driving SUMO's own netconvert.
    ///
    /// This used to be a manual step, with the GUI telling the user to go and run netconvert or
    /// osmWebWizard themselves. osmWebWizard in particular is the wrong tool here: it also generates
    /// a synthetic travel demand, and WUInity supplies its own - it drives SUMO through LIBSUMO and
    /// creates each vehicle as a household decides to leave, routing it at that moment. So the
    /// configuration written here deliberately references a network and nothing else.
    /// </summary>
    public static class SumoNetworkBuilder
    {
        public const string NetworkFileName = "osm.net.xml";
        public const string ConfigurationFileName = "osm.sumocfg";

        /// <summary>
        /// Converts <paramref name="osmFilePath"/> into a SUMO network and configuration under
        /// <paramref name="outputFolder"/>. Returns the configuration's path, or null on failure.
        /// </summary>
        /// <param name="sumoBinFolder">
        /// SUMO's bin folder. When empty, netconvert is looked up on PATH and via SUMO_HOME.
        /// </param>
        public static string Build(string osmFilePath, string outputFolder, string sumoBinFolder, Action<string> log = null)
        {
            if (!File.Exists(osmFilePath))
            {
                Report(log, "OSM file not found: " + osmFilePath);
                return null;
            }

            string netconvert = FindNetconvert(sumoBinFolder);
            if (netconvert == null)
            {
                Report(log, "netconvert could not be found. Install SUMO, or set SUMO_HOME to point at it.");
                return null;
            }

            Directory.CreateDirectory(outputFolder);
            string networkPath = Path.Combine(outputFolder, NetworkFileName);

            var psi = new ProcessStartInfo
            {
                FileName = netconvert,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                //netconvert resolves its output path against its own working directory, so this is
                //set rather than assumed - the same trap WindNinja's runner hit.
                WorkingDirectory = outputFolder
            };

            psi.ArgumentList.Add("--osm-files");
            psi.ArgumentList.Add(Path.GetFullPath(osmFilePath));
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(Path.GetFullPath(networkPath));

            //Tidying that OSM data needs to become a usable network: OSM splits roads at every tag
            //change, models junctions as clusters of separate nodes, and describes signals per-node.
            psi.ArgumentList.Add("--geometry.remove");
            psi.ArgumentList.Add("--roundabouts.guess");
            psi.ArgumentList.Add("--ramps.guess");
            psi.ArgumentList.Add("--junctions.join");
            psi.ArgumentList.Add("--tls.guess-signals");
            psi.ArgumentList.Add("--tls.discard-simple");
            psi.ArgumentList.Add("--tls.join");

            //Cars only, and nothing stranded. Footways are downloaded because pedestrian evacuation
            //uses them, but they are not part of the network vehicles drive on.
            psi.ArgumentList.Add("--keep-edges.by-vclass");
            psi.ArgumentList.Add("passenger");
            psi.ArgumentList.Add("--remove-edges.isolated");

            Report(log, "Running netconvert on " + Path.GetFileName(osmFilePath) + "...");

            try
            {
                using (Process process = Process.Start(psi))
                {
                    //Both pipes have to be drained at the same time. Reading one to the end and only
                    //then the other deadlocks: netconvert emits thousands of warnings on stderr while
                    //it works, that pipe's buffer fills, and it blocks on the write while this side
                    //blocks on stdout - which never closes, because the process is stuck. Observed as
                    //netconvert sitting at 5 seconds of CPU after half an hour, having written nothing.
                    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
                    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
                    Task.WaitAll(stdoutTask, stderrTask);
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        Report(log, "netconvert failed: " + LastMeaningfulLine(stderrTask.Result + stdoutTask.Result));
                        return null;
                    }
                }
            }
            catch (Exception e)
            {
                Report(log, "netconvert could not be run: " + e.Message);
                return null;
            }

            if (!File.Exists(networkPath))
            {
                Report(log, "netconvert reported success but wrote no network.");
                return null;
            }

            long megabytes = new FileInfo(networkPath).Length / (1024 * 1024);
            Report(log, $"Built {NetworkFileName} ({megabytes} MB).");

            string configurationPath = Path.Combine(outputFolder, ConfigurationFileName);
            WriteConfiguration(configurationPath);
            Report(log, "Wrote " + ConfigurationFileName + ".");

            return configurationPath;
        }

        /// <summary>
        /// Writes the configuration. A network, and no demand: WUInity creates every vehicle itself.
        /// </summary>
        private static void WriteConfiguration(string configurationPath)
        {
            string[] lines =
            {
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                "<sumoConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"",
                "    xsi:noNamespaceSchemaLocation=\"http://sumo.dlr.de/xsd/sumoConfiguration.xsd\">",
                "    <!-- Written by WUInity. No route or demand file on purpose: WUInity drives SUMO",
                "         through LIBSUMO and creates each vehicle as a household decides to leave,",
                "         routing it then with Simulation.findRoute. The network is all SUMO needs. -->",
                "    <input>",
                "        <net-file value=\"" + NetworkFileName + "\"/>",
                "    </input>",
                "    <processing>",
                "        <!-- Vehicles are inserted at their household's own position, which can land on",
                "             an edge the current destination cannot be reached from. WUInity detects",
                "             that and relocates the car rather than the run failing. -->",
                "        <ignore-route-errors value=\"true\"/>",
                "    </processing>",
                "    <report>",
                "        <verbose value=\"true\"/>",
                "        <no-step-log value=\"true\"/>",
                "    </report>",
                "</sumoConfiguration>"
            };

            File.WriteAllLines(configurationPath, lines);
        }

        /// <summary>
        /// Locates netconvert: the folder the engine already found, then SUMO_HOME, then PATH.
        /// </summary>
        private static string FindNetconvert(string sumoBinFolder)
        {
            string executable = Environment.OSVersion.Platform == PlatformID.Win32NT ? "netconvert.exe" : "netconvert";

            if (!string.IsNullOrEmpty(sumoBinFolder))
            {
                string candidate = Path.Combine(sumoBinFolder, executable);
                if (File.Exists(candidate)) return candidate;
            }

            string sumoHome = Environment.GetEnvironmentVariable("SUMO_HOME");
            if (!string.IsNullOrEmpty(sumoHome))
            {
                string candidate = Path.Combine(sumoHome, "bin", executable);
                if (File.Exists(candidate)) return candidate;
            }

            //On PATH, which is how a shell would find it.
            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string folder in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                try
                {
                    string candidate = Path.Combine(folder.Trim(), executable);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    //an unusable PATH entry is not worth failing the search over
                }
            }

            return null;
        }

        /// <summary>
        /// netconvert ends with hundreds of aggregated warnings, so the last line that is not one of
        /// those is what actually explains a failure.
        /// </summary>
        private static string LastMeaningfulLine(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return "no output";

            string[] lines = output.Split('\n');
            string lastNonEmpty = null;
            for (int i = lines.Length - 1; i >= 0; --i)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (lastNonEmpty == null) lastNonEmpty = line;
                if (line.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase)) continue;
                return line;
            }

            //Everything was a warning, so the last of those says more than the empty string this used
            //to fall back to when the final line happened to be blank.
            return lastNonEmpty ?? "no output";
        }

        private static void Report(Action<string> log, string message)
        {
            Engine.Message(null, Engine.LogType.Log, message);
            log?.Invoke(message);
        }
    }
}
