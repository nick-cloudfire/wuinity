using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
        public static bool Run(Campaign c, string id, string dir, RealizationRecord record, out string message, out bool cancelled)
        {
            message = null;
            cancelled = false;

            string name = c.RealizationName(id);
            string outputName = name + "_trigger.asc";
            string outputDir = Path.Combine(c.ScenarioDir, CampaignLayout.OutputFolder);
            Directory.CreateDirectory(outputDir);

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
            if (!string.IsNullOrEmpty(record.Fi)) Set("AscImport", "FirelineIntensityFile", R(record.Fi));
            Set("AscImport", "MidflameWindSpeedFile", R(record.Mfws));
            //ELMFIRE writes arrival times in seconds; a base scenario that once imported a FARSITE .asc may say Minutes.
            Set("AscImport", "TimeOfArrivalUnits", "Seconds");

            string weatherDir = Path.Combine(dir, CampaignLayout.RealizationWeatherFolder);
            Set("kPERIL", "WindDirectionFile", CampaignLayout.RelativeForWui(c.ScenarioDir, ElmfireStems.Tif(weatherDir, ElmfireStems.WindDirection)));
            //Cleared: the midflame wind above is the speed k-PERIL uses, and a base scenario's own field must not
            //stand in for it.
            Set("kPERIL", "WindSpeedFile", string.Empty);
            Set("kPERIL", "WindBandSeconds", c.SecondsPerBand.ToString("R", CultureInfo.InvariantCulture));
            if (c.WuiAreaFile != null && c.WuiAreaSource == "Raster")
            {
                Set("kPERIL", "WuiAreaFile", CampaignLayout.RelativeForWui(c.ScenarioDir, c.WuiAreaFile));
            }
            Set("kPERIL", "OutputName", outputName);

            string tempWui = Path.Combine(c.ScenarioDir, "__" + name + ".wui");
            string preactDir = Path.Combine(dir, CampaignLayout.RealizationPreactFolder);
            string logPath = Path.Combine(dir, CampaignLayout.RealizationPreactLog);

            try
            {
                File.WriteAllLines(tempWui, lines);
                File.WriteAllLines(Path.Combine(dir, CampaignLayout.RealizationScenarioCopy),
                    new[] { "# The scenario PREACT.exe ran for this realization, written as " + Path.GetFileName(tempWui)
                            + " beside " + Path.GetFileName(c.BaseWuiPath) + "; its paths are relative to that folder." }
                        .Concat(lines));

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
                if (exit != 0 || !File.Exists(boundary))
                {
                    message = $"the evacuation produced no trigger boundary (PREACT exit {exit}, {moved} output file(s)); "
                              + "see " + logPath + ": " + LogTail(logPath);
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

        public static string FindPreactExe()
        {
            string cliDir = Path.GetDirectoryName(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
            foreach (string name in new[] { "PREACT.exe", "PREACT" })
            {
                foreach (string c in new[]
                         {
                             Path.Combine(cliDir, name),
                             Path.GetFullPath(Path.Combine(cliDir, "..", "..", "..", "..", "PREACTexecute", "bin", "Release", "net8.0", name)),
                             Path.GetFullPath(Path.Combine(cliDir, "..", "..", "..", "..", "PREACTexecute", "bin", "Debug", "net8.0", name)),
                         })
                {
                    if (File.Exists(c)) return c;
                }
            }
            return null;
        }
    }
}
