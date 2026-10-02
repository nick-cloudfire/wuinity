using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>
    /// Runs one realization's evacuation and k-PERIL trigger boundary through PREACT.exe, on the base scenario with
    /// the realization's own fire and weather written into it.
    /// </summary>
    internal static class RealizationRunner
    {
        /// <summary>
        /// Writes the realization's scenario, runs PREACT.exe on it, and gathers everything it wrote into the
        /// realization's folder. False (with <paramref name="message"/>) when it produced no boundary.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The scenario is written beside the base one, because every relative path in the base scenario - the
        /// population, the SUMO network, the evacuation groups - is relative to its folder. Everything the
        /// realization adds is relative to that folder too, with forward slashes, so a campaign folder can be moved
        /// with its scenario or opened on another machine; the realization .wui files used to carry absolute
        /// <c>D:\WUINITY\...</c> paths. A copy is kept in the realization folder as the record of what ran.
        /// </para>
        /// <para>
        /// PREACT writes into the scenario's <c>_output</c> under the realization's name, which is unique to this
        /// campaign; those files are moved into the realization folder afterwards, so campaigns stop accumulating in
        /// <c>_output</c> and cannot pick up each other's results.
        /// </para>
        /// <para>
        /// k-PERIL is pointed at this realization's own wind: the midflame wind its ELMFIRE run wrote
        /// (<c>[AscImport] MidflameWindSpeedFile</c>) and the wind direction of the weather it was drawn - never the
        /// case's own <c>ws.tif</c>, which every realization used to get, so every boundary's width came from one
        /// unrelated wind field. Keys the base scenario names are overridden.
        /// </para>
        /// </remarks>
        public static bool Run(Campaign c, int index, string id, string dir, RealizationRecord record, out string message,
            out bool cancelled)
        {
            message = null;
            cancelled = false;

            string name = c.RealizationName(id);
            string outputName = name + "_trigger.asc";
            string outputDir = Path.Combine(c.ScenarioDir, CampaignLayout.OutputFolder);
            Directory.CreateDirectory(outputDir);

            string[] lines = ScenarioLines(c, index, id, dir, record);

            string tempWui = Path.Combine(c.ScenarioDir, "__" + name + ".wui");
            string preactDir = Path.Combine(dir, CampaignLayout.RealizationPreactFolder);
            string logPath = Path.Combine(dir, CampaignLayout.RealizationPreactLog);

            try
            {
                File.WriteAllLines(tempWui, lines);
                File.WriteAllLines(Path.Combine(dir, CampaignLayout.RealizationScenarioCopy),
                    new[]
                    {
                        "# The scenario PREACT.exe ran for this realization (as " + Path.GetFileName(tempWui) + " beside "
                        + Path.GetFileName(c.BaseWuiPath) + "), with its paths made relative to this folder so it opens here.",
                    }.Concat(RebasePaths(lines, c.ScenarioDir, dir)));

                //Anything an interrupted earlier attempt left under this name would otherwise be read as this run's.
                RemoveOutputs(outputDir, name);
                if (Directory.Exists(preactDir)) Directory.Delete(preactDir, recursive: true);

                Console.WriteLine($"[{id}] running evacuation + k-PERIL...");
                long generation = ElmfireProcesses.Generation;
                int exit = RunProcess(c.PreactExe, tempWui, logPath);
                if (ElmfireProcesses.CancelledSince(generation))
                {
                    cancelled = true;
                    message = "cancelled";
                    return false;
                }

                int moved = CollectOutputs(outputDir, name, preactDir);

                string boundary = Path.Combine(preactDir, "0_" + outputName);
                message = DescribeRun(exit, File.Exists(boundary), moved, logPath);
                if (message != null)
                {
                    return false;
                }

                record.Boundary = Path.GetRelativePath(dir, boundary).Replace('\\', '/');
                return true;
            }
            finally
            {
                try { File.Delete(tempWui); } catch { }
            }
        }

        /// <summary>
        /// A scenario's lines with every relative path - the value of any key ending in File, Folder, Directory or Exe,
        /// and PathToGdal - rewritten from <paramref name="fromDir"/> to <paramref name="toDir"/>, with forward slashes.
        /// Absolute paths, and NamelistTemplate (which is looked for in the case folder first), are left as they are.
        /// </summary>
        /// <remarks>
        /// The copy of a realization's scenario kept in its folder used to carry the base scenario's paths unchanged, so
        /// opening it there reported the population, the network, the groups and the fire as missing (e2e F8).
        /// </remarks>
        internal static string[] RebasePaths(IReadOnlyList<string> lines, string fromDir, string toDir)
        {
            var result = new string[lines.Count];
            for (int i = 0; i < lines.Count; ++i)
            {
                string line = lines[i];
                result[i] = line;

                string trimmed = line.TrimStart();
                int eq = trimmed.IndexOf('=');
                if (eq <= 0 || trimmed.StartsWith("#", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)) continue;

                string key = trimmed.Substring(0, eq).Trim();
                bool pathKey = key.EndsWith("File", StringComparison.Ordinal) || key.EndsWith("Folder", StringComparison.Ordinal)
                               || key.EndsWith("Directory", StringComparison.Ordinal) || key.EndsWith("Exe", StringComparison.Ordinal)
                               || key == "PathToGdal";
                if (!pathKey) continue;

                string value = trimmed.Substring(eq + 1);
                string comment = string.Empty;
                int hash = value.IndexOf(" #", StringComparison.Ordinal);
                if (hash >= 0)
                {
                    comment = value.Substring(hash);
                    value = value.Substring(0, hash);
                }

                //A Windows drive path is absolute on every platform, though only Windows says so.
                string normalised = PREACT.Input.PREACTInput.NormalisePath(value);
                bool drive = normalised.Length > 2 && char.IsLetter(normalised[0]) && normalised[1] == ':' && normalised[2] == '/';
                if (normalised.Length == 0 || drive || Path.IsPathRooted(normalised)) continue;

                string full = Path.GetFullPath(Path.Combine(fromDir, normalised));
                result[i] = key + "=" + CampaignLayout.RelativeForWui(toDir, full) + comment;
            }
            return result;
        }

        /// <summary>What is added to the campaign seed to seed a realization's evacuation, apart from its fire's
        /// (seed + index), its weather's (seed + index, another generator) and its ignition's (seed + 1 000 000 + index).</summary>
        internal const int EvacuationSeedOffset = 2_000_000;

        /// <summary>
        /// The <c>[Simulation] RandomSeed</c> realization <paramref name="index"/> of a campaign seeded
        /// <paramref name="campaignSeed"/> evacuates with: <c>seed + 2 000 000 + index</c>, never 0.
        /// </summary>
        /// <remarks>
        /// The evacuation - response-curve draws, walking speeds, cars per household, destination choice - used to
        /// run on whatever the base scenario said, which for Mati is nothing, i.e. a clock seed: irreproducible although
        /// the campaign seed is part of the campaign's identity, and a resume that re-ran a failed evacuation got
        /// another WRSET for the same fire. A base scenario with a fixed seed was worse: every realization drew the
        /// same departures, so the ensemble under-represented evacuation variability. 0 is PREACT's "seed from the
        /// clock", so the one index that would land on it is moved to 1.
        /// </remarks>
        internal static int EvacuationSeed(int campaignSeed, int index)
        {
            int seed = unchecked(campaignSeed + EvacuationSeedOffset + index);
            return seed == 0 ? 1 : seed;
        }

        /// <summary>
        /// The base scenario with the realization's fire, wind, WUI area, topography and evacuation seed written into
        /// it, every added path relative to the base scenario's folder.
        /// </summary>
        internal static string[] ScenarioLines(Campaign c, int index, string id, string dir, RealizationRecord record)
        {
            string name = c.RealizationName(id);
            string outputName = name + "_trigger.asc";

            string R(string realizationRelative) =>
                CampaignLayout.RelativeForWui(c.ScenarioDir, RealizationRecord.Resolve(dir, realizationRelative));

            string[] lines = c.BaseLines;
            void Set(string section, string key, string value) => lines = WuiText.Set(lines, section, key, value);

            Set("Simulation", "Name", name);

            //The fire is already computed - it is the point of the rasters below - so the run reads it. Left on
            //ELMFIRE, every realization would run ELMFIRE again and, with BuildCase on, rebuild the shared case
            //underneath the others.
            Set("WildfireModule", "Enabled", "true");
            Set("WildfireModule", "Module", "AscImport");
            Set("ELMFIRE", "BuildCase", "false");

            //[AscImport] StartDateTime is required and parsed first; the run's start is where the fire starts.
            Set("AscImport", "StartDateTime", c.StartDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            Set("AscImport", "TimeOfArrivalFile", R(record.Toa));
            Set("AscImport", "RateOfSpreadFile", R(record.Ros));
            Set("AscImport", "SpreadDirectionFile", R(record.Sd));
            //Set or cleared, never left as the base scenario had it: [AscImport] is kept beside ELMFIRE through saves, so a
            //base scenario's own fireline intensity or fuel raster would otherwise be shown as this realization's (review
            //NIT). The fuel is the case's the realization burned.
            Set("AscImport", "FirelineIntensityFile", string.IsNullOrEmpty(record.Fi) ? string.Empty : R(record.Fi));
            string fuel = string.IsNullOrEmpty(c.FuelStem) ? null : ElmfireStems.Tif(c.InputsDir, c.FuelStem);
            Set("AscImport", "FuelModelFile", fuel != null && File.Exists(fuel) ? CampaignLayout.RelativeForWui(c.ScenarioDir, fuel) : string.Empty);
            Set("AscImport", "MidflameWindSpeedFile", R(record.Mfws));
            //ELMFIRE writes arrival times in seconds; a base scenario that once imported a FARSITE .asc may say Minutes.
            Set("AscImport", "TimeOfArrivalUnits", "Seconds");

            string weatherDir = Path.Combine(dir, CampaignLayout.RealizationWeatherFolder);
            Set("kPERIL", "WindDirectionFile", CampaignLayout.RelativeForWui(c.ScenarioDir, ElmfireStems.Tif(weatherDir, ElmfireStems.WindDirection)));
            //Cleared: the midflame wind above is the speed k-PERIL uses, and a base scenario's own field must not
            //stand in for it.
            Set("kPERIL", "WindSpeedFile", string.Empty);
            Set("kPERIL", "WindBandSeconds", c.SecondsPerBand.ToString("R", CultureInfo.InvariantCulture));
            //Exactly the cells the realization's fire was checked against (the groups' union, or the scenario's own mask),
            //rather than the groups rasterised again on the imported fire's grid.
            if (c.WuiAreaFile != null)
            {
                Set("kPERIL", "WuiAreaSource", nameof(PREACT.Input.kPERILInput.WuiAreaSources.Raster));
                Set("kPERIL", "WuiAreaFile", CampaignLayout.RelativeForWui(c.ScenarioDir, c.WuiAreaFile));
            }
            Set("kPERIL", "OutputName", outputName);

            //Contract C1: the case's dem/slp/asp are the grid of record, k-PERIL's topography included. A base
            //scenario whose landscape is still the one it was drawn on - mati.wui's 616x590 DEM covers 64 % of the
            //padded fire grid - otherwise gives k-PERIL flat ground over the rest of the fire.
            foreach ((string key, string stem) in new[]
                     {
                         ("ElevationFile", ElmfireStems.Dem), ("SlopeFile", ElmfireStems.Slope), ("AspectFile", ElmfireStems.Aspect),
                     })
            {
                string path = ElmfireStems.Tif(c.InputsDir, stem);
                if (File.Exists(path)) Set("Landscape", key, CampaignLayout.RelativeForWui(c.ScenarioDir, path));
            }

            //Reproducible and different for every realization (MA-5).
            record.EvacuationSeed = EvacuationSeed(c.Options.Seed, index);
            Set("Simulation", "RandomSeed", record.EvacuationSeed.ToString(CultureInfo.InvariantCulture));

            return lines;
        }

        private static void RemoveOutputs(string outputDir, string name)
        {
            foreach (string path in Directory.GetFiles(outputDir))
            {
                if (BelongsTo(Path.GetFileName(path), name))
                {
                    try { File.Delete(path); } catch { }
                }
            }
        }

        private static int CollectOutputs(string outputDir, string name, string destination)
        {
            int moved = 0;
            foreach (string path in Directory.GetFiles(outputDir))
            {
                string file = Path.GetFileName(path);
                if (!BelongsTo(file, name)) continue;

                Directory.CreateDirectory(destination);
                try
                {
                    File.Move(path, Path.Combine(destination, file), overwrite: true);
                    ++moved;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"  could not move {file} into the realization folder: {e.Message}");
                }
            }
            return moved;
        }

        /// <summary>PREACT names every output after the scenario, and the boundary <c>&lt;simulation index&gt;_&lt;OutputName&gt;</c>.</summary>
        private static bool BelongsTo(string file, string name)
        {
            return file.StartsWith(name, StringComparison.Ordinal)
                   || (file.Length > 2 && char.IsDigit(file[0]) && file.IndexOf("_" + name, StringComparison.Ordinal) == file.IndexOf('_'));
        }

        /// <summary>
        /// Runs PREACT.exe with both streams to <paramref name="logPath"/>: PREACT logs every step, which at
        /// --parallel width would bury the campaign's own lines. Registered with <see cref="ElmfireProcesses"/> so a
        /// cancel kills it (and SUMO with it) along with the fires.
        /// </summary>
        private static int RunProcess(string exe, string wuiPath, string logPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            psi.ArgumentList.Add(wuiPath);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
            using (var log = new StreamWriter(logPath, append: false))
            using (var p = new Process { StartInfo = psi })
            {
                object sync = new object();
                void Write(string line)
                {
                    if (line == null) return;
                    lock (sync) log.WriteLine(line);
                }

                p.OutputDataReceived += (_, e) => Write(e.Data);
                p.ErrorDataReceived += (_, e) => Write(e.Data);

                p.Start();
                ElmfireProcesses.Register(p);
                try
                {
                    p.StandardInput.Close();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                }
                finally
                {
                    ElmfireProcesses.Unregister(p);
                }
                return p.ExitCode;
            }
        }

        /// <summary>PREACT.exe's exit codes (PREACTexecute): 0 ran and succeeded, 1 nothing was run, 2 the run reported errors.</summary>
        public const int PreactSucceeded = 0, PreactNotRun = 1, PreactRunFailed = 2;

        /// <summary>
        /// Why a realization's evacuation counts as failed, or null when it produced its boundary. Always names the
        /// realization's PREACT log and quotes its last lines.
        /// </summary>
        /// <remarks>
        /// Only an exit of 0 with a boundary on disk is a success. PREACT.exe exits 1 when it ran nothing (the
        /// realization's scenario did not load or was incomplete) and 2 when a simulation stopped on an error or an
        /// error was reported - k-PERIL refusing to compute a boundary among them - in which case a boundary file
        /// left behind is not trusted. Anything else is the process dying: a kill, an out-of-memory, a crash.
        /// </remarks>
        internal static string DescribeRun(int exit, bool boundaryWritten, int outputFiles, string logPath)
        {
            string why;
            switch (exit)
            {
                case PreactSucceeded:
                    if (boundaryWritten) return null;
                    why = $"PREACT finished but wrote no trigger boundary ({outputFiles} output file(s))";
                    break;
                case PreactNotRun:
                    why = "PREACT did not run the realization's scenario (exit 1: it did not load, or is missing something "
                          + "it needs)";
                    break;
                case PreactRunFailed:
                    why = "PREACT's run reported errors (exit 2)" + (boundaryWritten ? "; the boundary it wrote is not used" : "");
                    break;
                default:
                    why = $"PREACT exited {exit}, which it never does by itself - it was killed or crashed "
                          + "(on Linux 137 is usually the out-of-memory killer)";
                    break;
            }
            return why + "; see " + logPath + ": " + LogTail(logPath);
        }

        /// <summary>The last lines of a log, for failure messages.</summary>
        public static string LogTail(string logPath, int lines = 3)
        {
            try
            {
                string[] all = File.ReadAllLines(logPath);
                int from = Math.Max(0, all.Length - lines);
                return string.Join(" | ", all[from..]);
            }
            catch
            {
                return "(no log available)";
            }
        }

        /// <summary>PREACT.exe for a campaign that was not given <c>--preact</c>, or null.</summary>
        public static string FindPreactExe()
        {
            return FindPreactExe(AppContext.BaseDirectory, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        }

        /// <summary>
        /// The head-less runner's apphost - <c>PREACT.exe</c> on Windows, <c>PREACT</c> elsewhere - looked for where
        /// build.ps1/build.sh put it (<c>PREACT/PREACTexecute/bin/Release/net8.0</c>, reached from the CLI's own
        /// build folder), then beside the CLI (a copied installation), then in the Debug build.
        /// </summary>
        /// <remarks>
        /// The name is the platform's only. Looking for <c>PREACT.exe</c> first everywhere found nothing on Linux
        /// unless <c>--preact</c> was passed, and a Windows copy lying around would have been started there.
        /// The build output comes before a copy beside the CLI because that is what the build scripts refresh.
        /// </remarks>
        internal static string FindPreactExe(string cliDirectory, bool windows)
        {
            if (string.IsNullOrEmpty(cliDirectory)) return null;
            string name = windows ? "PREACT.exe" : "PREACT";
            string preactFolder = Path.Combine(cliDirectory, "..", "..", "..", "..", "PREACTexecute", "bin");
            foreach (string candidate in new[]
                     {
                         Path.Combine(preactFolder, "Release", "net8.0", name),
                         Path.Combine(cliDirectory, name),
                         Path.Combine(preactFolder, "Debug", "net8.0", name),
                     })
            {
                string full = Path.GetFullPath(candidate);
                if (File.Exists(full)) return full;
            }
            return null;
        }
    }
}
