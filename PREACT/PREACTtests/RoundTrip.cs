using System.Globalization;
using PREACT.Input;

namespace PREACT.Tests
{
    /// <summary>
    /// Load, write, reload, write: the scenario format must survive being saved.
    /// </summary>
    internal static class RoundTrip
    {
        /// <summary>Every example scenario in the repository (<c>Examples/**/*.wui</c>).</summary>
        public static IEnumerable<string> Examples(string repositoryRoot)
        {
            string examples = Path.Combine(repositoryRoot, "Examples");
            return Directory.Exists(examples)
                ? Directory.GetFiles(examples, "*.wui", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal)
                : Enumerable.Empty<string>();
        }

        //Keys the writer drops on purpose: retired, read-only or converted. Section|Key, "*" for any section.
        private static readonly HashSet<string> RetiredKeys = new HashSet<string>
        {
            "Evacuation|UseTriggerBufferEvacuation", "Evacuation|TriggerBufferFile", "Evacuation|EvacuationOrderStart",
            "Weather|DesiredLatLon", "Weather|HasWeatherAnchor",
            "TrafficModule|VisibilityAffectsSpeed",
            "SUMO|UTMoffset",
            "kPERIL|MidflameWindspeed", "kPERIL|CalculateROSFromBehave", "kPERIL|InitialFuelMoistureFile", "kPERIL|FuelModelsFile",
            "kPERIL|WindBand",
            "ELMFIRE|SimulationTstopSeconds",
        };

        //Retired sections, and the sections of modules that have been removed from the engine.
        private static readonly HashSet<string> RetiredSections = new HashSet<string>
        {
            "Events", "WUIShow",
            "SimpleWildfireCA", "ElmClone", "CellParticleHybrid", "Behave", "Rothermel", "AdvectDiffuse3D", "AdvectDiffuseMixingLayer",
            "CityFlow", "MacroTrafficSim", "FireCell",
        };

        public static List<string> Run(string file, string outDir)
        {
            var warnings = new List<string>();
            string root = Path.GetDirectoryName(Path.GetFullPath(file));
            string[] original = File.ReadAllLines(file);

            PREACTInput first = PREACTInput.LoadFromDisk(file, out bool _);
            Assert.True(first != null, "the scenario did not load at all");
            List<PREACTInput.InputRequirement> before = new List<PREACTInput.InputRequirement>(PREACTInput.Requirements);

            string[] w1 = PREACTInputWriter.Write(first);
            PREACTInput second = PREACTInput.LoadFromLines(w1, root, out bool _);
            List<PREACTInput.InputRequirement> after = new List<PREACTInput.InputRequirement>(PREACTInput.Requirements);
            string[] w2 = PREACTInputWriter.Write(second);

            if (outDir != null)
            {
                Directory.CreateDirectory(outDir);
                File.WriteAllLines(Path.Combine(outDir, Path.GetFileName(file) + ".w1"), w1);
                File.WriteAllLines(Path.Combine(outDir, Path.GetFileName(file) + ".w2"), w2);
                File.WriteAllLines(Path.Combine(outDir, Path.GetFileName(file) + ".req"),
                    before.Select(r => "REQ1 " + Describe(r)).Concat(after.Select(r => "REQ2 " + Describe(r))));
            }

            //1. Nothing new, nothing more critical.
            var problems = new List<string>();
            foreach (PREACTInput.InputRequirement requirement in after)
            {
                PREACTInput.InputRequirement match = before.FirstOrDefault(r => r.Section == requirement.Section && r.Key == requirement.Key);
                if (match == null)
                {
                    problems.Add("new requirement after the round trip: " + Describe(requirement));
                }
                else if (requirement.Critical && !match.Critical)
                {
                    problems.Add("requirement became critical after the round trip: " + Describe(requirement));
                }
            }

            //2. The writer is idempotent.
            if (!w1.SequenceEqual(w2))
            {
                int line = 0;
                while (line < System.Math.Min(w1.Length, w2.Length) && w1[line] == w2[line]) ++line;
                problems.Add($"second write differs from the first at line {line + 1}: '{Get(w1, line)}' vs '{Get(w2, line)}'");
            }

            //3. Every key of the original is still there, with the same value - in every section, the fire and
            //k-PERIL ones included (their differences used to be warnings, while those parsers had other owners).
            problems.AddRange(CompareKeys(original, w1, before));

            //Critical items that were already there are reported, not failed: they are the data's problem.
            foreach (PREACTInput.InputRequirement requirement in before.Where(r => r.Critical))
            {
                warnings.Add("scenario itself has a critical item: " + Describe(requirement));
            }

            if (problems.Count > 0)
            {
                throw new TestFailure(string.Join(Environment.NewLine + "      ", new[] { $"{problems.Count} problem(s):" }.Concat(problems)));
            }
            return warnings;
        }

        private static string Get(string[] lines, int index) => index < lines.Length ? lines[index] : "<end>";

        private static string Describe(PREACTInput.InputRequirement r) => $"[{(r.Critical ? "CRIT" : "soft")}] {r.Section} / {r.Key}: {r.Message}";

        /// <summary>
        /// Section/key/value triples of a .wui, following the format rules; repeated sections are numbered in order.
        /// </summary>
        public static List<(string section, string key, string value)> Keys(string[] lines)
        {
            var result = new List<(string, string, string)>();
            var counts = new Dictionary<string, int>();
            var seen = new HashSet<string>();
            string section = null;
            foreach (string raw in lines)
            {
                string line = StripComment(raw).Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("["))
                {
                    string name = line.Trim('[', ']', ' ');
                    counts[name] = counts.TryGetValue(name, out int n) ? n + 1 : 1;
                    section = counts[name] == 1 ? name : name + "#" + counts[name];
                    continue;
                }
                int equals = line.IndexOf('=');
                if (section == null || equals <= 0) continue;
                string key = line.Substring(0, equals).Replace(" ", "");
                string value = line.Substring(equals + 1).Trim();
                if (seen.Add(section + "|" + key))
                {
                    result.Add((section, key, value));
                }
            }
            return result;
        }

        private static string StripComment(string line)
        {
            for (int i = 0; i < line.Length; ++i)
            {
                if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line.Substring(0, i);
            }
            return line;
        }

        private static IEnumerable<string> CompareKeys(string[] original, string[] written, List<PREACTInput.InputRequirement> requirements)
        {
            var writtenKeys = Keys(written).ToDictionary(k => k.section + "|" + k.key, k => k.value);
            var writtenSections = new HashSet<string>(Keys(written).Select(k => k.section));
            var originalKeys = Keys(original);
            var originalSections = new HashSet<string>(originalKeys.Select(k => k.section));
            //What every section says when nothing is set: the writer leaves out a section that says only that.
            var defaults = Keys(PREACTInputWriter.Write(new PREACTInput(Path.GetTempPath()), false))
                .ToDictionary(k => k.section + "|" + k.key, k => k.value);

            foreach ((string section, string key, string value) in originalKeys)
            {
                string baseSection = section.Split('#')[0];
                if (RetiredSections.Contains(baseSection) || RetiredKeys.Contains(baseSection + "|" + key))
                {
                    continue;
                }

                if (!writtenSections.Contains(section) && defaults.TryGetValue(baseSection + "|" + key, out string defaultValue)
                    && Equivalent(value, defaultValue, key, requirements))
                {
                    continue; //left out because it held only defaults
                }

                if (!writtenSections.Contains(section))
                {
                    //Every section with something in it is written, a module option that is not selected included
                    //(the [ELMFIRE] of a realization that runs AscImport, say). Said once per section.
                    if (originalKeys.First(k => k.section == section).key == key)
                    {
                        yield return $"[{section}] was dropped by the writer";
                    }
                    continue;
                }

                if (!writtenKeys.TryGetValue(section + "|" + key, out string written2))
                {
                    yield return $"[{section}] {key}={value} was dropped by the writer";
                    continue;
                }

                if (!Equivalent(value, written2, key, requirements))
                {
                    yield return $"[{section}] {key} changed: '{value}' -> '{written2}'";
                }
            }
        }

        private static bool Equivalent(string a, string b, string key, List<PREACTInput.InputRequirement> requirements)
        {
            if (a == b) return true;
            string na = a.Replace('\\', '/').Trim();
            string nb = b.Replace('\\', '/').Trim();
            if (na == nb) return true;
            if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase) && (bool.TryParse(na, out _) || bool.TryParse(nb, out _))) return true;

            //A value the reader could not interpret was replaced by its default, and said so.
            if (requirements.Any(r => (r.Key == key || r.Key.EndsWith(" " + key)) && r.Message.Contains("could not be interpreted"))) return true;

            //A path the locator found elsewhere is written corrected; that is the point of the correction.
            if (requirements.Any(r => r.Key.EndsWith(key) && r.Message.StartsWith("Found at ")) && Path.GetFileName(na) == Path.GetFileName(nb)) return true;

            string[] pa = na.Split(',');
            string[] pb = nb.Split(',');
            if (pa.Length != pb.Length) return false;
            for (int i = 0; i < pa.Length; ++i)
            {
                string x = pa[i].Trim(), y = pb[i].Trim();
                if (x == y) continue;
                if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double dx)
                    && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out double dy)
                    && System.Math.Abs(dx - dy) <= 1e-6 * System.Math.Max(1.0, System.Math.Abs(dx)))
                {
                    continue;
                }
                if (DateTime.TryParse(x, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime tx)
                    && DateTime.TryParse(y, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime ty) && tx == ty)
                {
                    continue;
                }
                return false;
            }
            return true;
        }
    }
}
