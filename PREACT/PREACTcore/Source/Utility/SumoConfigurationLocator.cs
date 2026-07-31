using System.Collections.Generic;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// Works out which file is the scenario's SUMO configuration.
    ///
    /// The scenario stores this as a free path, set either by the build step - which writes
    /// sumo/osm.sumocfg and records it - or by a file picker. A picker asking for a "SUMO input file"
    /// invites the OSM extract, which is in the same folder, is what the network was built from, and is
    /// accepted without complaint. SUMO then refuses to start with "could not load configuration", by which
    /// point the scenario looks complete and the checklist agrees.
    ///
    /// So the path is checked rather than trusted, and what netconvert actually produced is looked for when
    /// it does not hold up. Shared between the engine, which starts SUMO on it, and the road network drawing,
    /// so both agree on what the scenario's network is.
    /// </summary>
    public static class SumoConfigurationLocator
    {
        /// <summary>A file SUMO can be started on, or a network that can be drawn.</summary>
        public static bool IsSumoFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string lower = path.ToLowerInvariant();
            return lower.EndsWith(".sumocfg") || lower.EndsWith(".sumo.cfg")
                   || lower.EndsWith(".net.xml") || lower.EndsWith(".net.xml.gz");
        }

        /// <summary>A configuration, as opposed to a bare network. This is what SUMO's -c option takes.</summary>
        public static bool IsConfiguration(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            string lower = path.ToLowerInvariant();
            return lower.EndsWith(".sumocfg") || lower.EndsWith(".sumo.cfg");
        }

        /// <summary>
        /// The configuration to use, as a full path, or null when the scenario has none.
        ///
        /// <paramref name="corrected"/> is true when the answer is not what the scenario named - the caller
        /// says so, because the scenario file still needs fixing even though this run can proceed.
        /// </summary>
        /// <param name="rootFolder">The scenario folder, which relative paths are resolved against.</param>
        /// <param name="configuredPath">Whatever the scenario's ConfigurationFile holds.</param>
        /// <param name="requireConfiguration">
        /// True for starting SUMO, which needs a .sumocfg; false for reading geometry, where a .net.xml does.
        /// </param>
        public static string Resolve(string rootFolder, string configuredPath, bool requireConfiguration,
            out bool corrected, out string explanation)
        {
            corrected = false;
            explanation = string.Empty;

            //As named, when that is a SUMO file that exists. An absolute path survives the combine.
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string asNamed = Path.Combine(rootFolder ?? string.Empty, configuredPath);
                bool typeOk = requireConfiguration ? IsConfiguration(configuredPath) : IsSumoFile(configuredPath);

                if (typeOk && File.Exists(asNamed))
                {
                    return asNamed;
                }
            }

            string found = FindInScenario(rootFolder, requireConfiguration);
            if (found == null)
            {
                explanation = string.IsNullOrWhiteSpace(configuredPath)
                    ? "This scenario names no SUMO configuration, and none was found in its folder. Build the "
                      + "SUMO network from the OSM data first."
                    : "The scenario's SUMO ConfigurationFile is \"" + configuredPath + "\", which is not a SUMO "
                      + "configuration that exists, and no other was found in the scenario folder. It has to be "
                      + "the .sumocfg netconvert wrote - usually sumo/osm.sumocfg - not the OSM extract the "
                      + "network was built from.";
                return null;
            }

            corrected = true;
            explanation = string.IsNullOrWhiteSpace(configuredPath)
                ? "This scenario names no SUMO configuration; using " + Relative(rootFolder, found) + " found in its folder."
                : "The scenario's SUMO ConfigurationFile is \"" + configuredPath + "\", which is not a usable SUMO "
                  + "configuration; using " + Relative(rootFolder, found) + " found in the scenario folder instead. "
                  + "Set the scenario to that path under Evacuation > Traffic so the two agree.";
            return found;
        }

        /// <summary>
        /// The likeliest configuration in a scenario folder: where the build step puts it first, then the
        /// folder root, then anything else that fits.
        /// </summary>
        public static string FindInScenario(string rootFolder, bool requireConfiguration)
        {
            if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder))
            {
                return null;
            }

            var candidates = new List<string>
            {
                Path.Combine(rootFolder, SumoNetworkBuilder.SumoFolderName, SumoNetworkBuilder.ConfigurationFileName),
                Path.Combine(rootFolder, SumoNetworkBuilder.ConfigurationFileName)
            };

            if (!requireConfiguration)
            {
                candidates.Add(Path.Combine(rootFolder, SumoNetworkBuilder.SumoFolderName, SumoNetworkBuilder.NetworkFileName));
                candidates.Add(Path.Combine(rootFolder, SumoNetworkBuilder.NetworkFileName));
            }

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            //Anything of the right kind, one level down included, since a hand-assembled scenario can keep
            //its network in a folder named anything at all.
            foreach (string pattern in requireConfiguration
                         ? new[] { "*.sumocfg" }
                         : new[] { "*.sumocfg", "*.net.xml" })
            {
                foreach (string match in Directory.GetFiles(rootFolder, pattern, SearchOption.AllDirectories))
                {
                    return match;
                }
            }

            return null;
        }

        private static string Relative(string rootFolder, string path)
        {
            if (string.IsNullOrWhiteSpace(rootFolder))
            {
                return path;
            }

            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(rootFolder);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                root += Path.DirectorySeparatorChar;
            }

            return full.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length)
                : full;
        }
    }
}
