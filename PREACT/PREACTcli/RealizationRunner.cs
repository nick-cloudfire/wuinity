using System;
using System.Diagnostics;
using System.IO;
using PREACT.Utility;

namespace PREACTcli
{
    /// <summary>
    /// Runs one fire realization's evacuation + k-PERIL trigger boundary through PREACT.exe.
    /// Shared by the fixed-count (<see cref="ProbabilisticTrigger"/>) and convergence-driven
    /// (<see cref="ConvergeTrigger"/>) drivers so the per-realization mechanics live in one place.
    /// </summary>
    internal static class RealizationRunner
    {
        /// <summary>
        /// Runs (or reuses) the trigger boundary for realization <paramref name="idx"/> and reads
        /// it back. Returns false (with <paramref name="boundary"/> null) if the realization is
        /// missing its source rasters, fails to run, or produces no readable boundary.
        /// </summary>
        /// <param name="elmfireInputs">
        /// The case's shared <c>inputs/</c> folder, or null when the realizations came from <c>--dir</c>.
        /// Used to hand k-PERIL the wind and WUI rasters the case already holds — see
        /// <see cref="ApplyCaseInputs"/> for why the realization cannot find them by itself.
        /// </param>
        public static bool TryRun(
            string preactExe, string caseDir, string baseName, string[] baseLines,
            string idx, string toa, string ros, string sd, string fi,
            string outputDir, bool resume, bool resumeOnly,
            out float[,] boundary, out AscRaster.Header header,
            string elmfireInputs = null)
        {
            boundary = null;
            header = default;

            string outputName = "trigger_" + idx + ".asc";
            string triggerPath = Path.Combine(outputDir, "0_" + outputName);
            bool haveResult = File.Exists(triggerPath);

            if (!haveResult || !resume)
            {
                if (resumeOnly)
                {
                    if (!haveResult)
                    {
                        Console.WriteLine($"[{idx}] no existing boundary, skipping (resume-only).");
                        return false;
                    }
                }
                else
                {
                    if (!File.Exists(toa) || !File.Exists(ros) || !File.Exists(sd))
                    {
                        Console.Error.WriteLine($"[{idx}] missing TOA/ROS/SD raster, skipping.");
                        return false;
                    }

                    string tempWui = Path.Combine(caseDir, "__prob_" + idx + ".wui");
                    string[] lines = (string[])baseLines.Clone();
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "Simulation", "Name", baseName + "_prob_" + idx);
                    //This realization's fire is already computed - it is the whole point of the rasters below -
                    //so the run must *read* it rather than produce one. Forced here rather than left as a
                    //documented requirement on the base scenario, because the natural base is the ELMFIRE
                    //scenario the case was built and tested with, and leaving it on ELMFIRE was silently
                    //catastrophic: every realization ignored the rasters it was handed, ran ELMFIRE again
                    //itself, and with BuildCase on rebuilt the shared case underneath the other realizations
                    //running in parallel. The campaign would still have produced a probability raster.
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "WildfireModule", "Module", "AscImport");
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "ELMFIRE", "BuildCase", "false");

                    //[AscImport] StartDateTime is REQUIRED and parsed first, and its parser returns early on
                    //failure - so without it none of the raster paths below are read at all, even though they
                    //are written here. The scenario then loads "with items still required", PREACT prints
                    //"Failed to read loaded file", runs nothing, and exits 0. Every realization fails.
                    //
                    //An ELMFIRE base scenario has no [AscImport] section to inherit it from, so it is taken
                    //from [Simulation], which is where the run's own start time lives and what the ELMFIRE
                    //path assigns to exactly this field.
                    string startDateTime = GetKeyInSection(baseLines, "Simulation", "StartDateTime");
                    if (!string.IsNullOrEmpty(startDateTime))
                    {
                        lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "StartDateTime", startDateTime);
                    }
                    else
                    {
                        Console.Error.WriteLine($"[{idx}] [Simulation] StartDateTime is missing from the base scenario; "
                            + "the realization cannot read its fire without it.");
                        return false;
                    }

                    lines = ApplyCaseInputs(lines, elmfireInputs, idx);

                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "TimeOfArrivalFile", toa);
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "RateOfSpreadFile", ros);
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "SpreadDirectionFile", sd);
                    if (File.Exists(fi)) lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "FirelineIntensityFile", fi);

                    //ELMFIRE writes arrival times in seconds; the reader's default is seconds too, but a base
                    //scenario that once imported .asc from FARSITE may still say Minutes, and 60x is not the
                    //kind of error a probability raster makes obvious.
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "TimeOfArrivalUnits", "Seconds");

                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "kPERIL", "OutputName", outputName);
                    File.WriteAllLines(tempWui, lines);

                    //The previous campaign's boundary for this realization has to go before the run, not after
                    //it. Success is judged on `exit == 0 && the file exists`, and PREACT can exit 0 having run
                    //nothing at all - the [AscImport] StartDateTime case above is exactly that - so an old
                    //boundary left in place would be read back and aggregated as though this realization had
                    //just produced it.
                    if (haveResult)
                    {
                        try
                        {
                            File.Delete(triggerPath);
                        }
                        catch (Exception e)
                        {
                            //Refused rather than risked: carrying on here is what silently aggregates the old
                            //boundary, and one realization skipped is a far smaller error than one faked.
                            Console.Error.WriteLine($"[{idx}] could not delete the previous boundary {triggerPath} "
                                + $"({e.Message}); skipping, since it could not be told apart from a new one.");
                            return false;
                        }
                    }

                    Console.WriteLine($"[{idx}] running evacuation + k-PERIL...");
                    string logPath = Path.Combine(outputDir, "logs", "realization_" + idx + ".log");
                    int exit = RunProcess(preactExe, tempWui, logPath);
                    try { File.Delete(tempWui); } catch { }

                    if (exit != 0 || !File.Exists(triggerPath))
                    {
                        Console.Error.WriteLine($"[{idx}] run failed or produced no trigger boundary (exit {exit}). See {logPath}");
                        Console.Error.WriteLine($"    {LogTail(logPath)}");
                        return false;
                    }
                }
            }
            else
            {
                Console.WriteLine($"[{idx}] reusing existing boundary.");
            }

            boundary = AscRaster.Read(triggerPath, out header, out bool ok);
            if (!ok || boundary == null)
            {
                Console.Error.WriteLine($"[{idx}] could not read boundary raster.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Points k-PERIL at the wind and WUI rasters the case already holds.
        /// </summary>
        /// <remarks>
        /// A realization runs as <c>AscImport</c>, and <c>HazardManager.CreateElmfireModule</c> — the only code
        /// that hands k-PERIL the case's own <c>ws.tif</c>/<c>wd.tif</c> — therefore never runs. Without this the
        /// realization loads, warns "Running ELMFIRE supplies the case's own", finds no wind, and stops with
        /// "Can't run kPERIL without wind speed and direction rasters". Every realization, every time.
        ///
        /// The WUI area is handled the same way and for a related reason: the fallback is the painted mask,
        /// which is on the landscape grid rather than the fire grid and is correctly refused. The case's
        /// <c>wui_area.tif</c> is on the fire grid by construction.
        ///
        /// Only ever fills what the scenario left empty. A base scenario that names its own wind or WUI raster
        /// is making a deliberate choice, and a campaign is not the place to overrule it.
        /// </remarks>
        private static string[] ApplyCaseInputs(string[] lines, string elmfireInputs, string idx)
        {
            if (string.IsNullOrEmpty(elmfireInputs) || !Directory.Exists(elmfireInputs))
            {
                return lines;
            }

            foreach ((string key, string stem) in new[]
                     {
                         ("WindSpeedFile", "ws"),
                         ("WindDirectionFile", "wd"),
                         ("WuiAreaFile", "wui_area"),
                     })
            {
                if (!string.IsNullOrEmpty(GetKeyInSection(lines, "kPERIL", key)))
                {
                    continue; //the scenario named one; leave it alone
                }

                string path = Path.Combine(elmfireInputs, stem + ".tif");
                if (!File.Exists(path))
                {
                    Console.Error.WriteLine($"[{idx}] the case has no {stem}.tif, so [kPERIL] {key} is left unset.");
                    continue;
                }

                lines = ProbabilisticTrigger.SetKeyInSection(lines, "kPERIL", key, path);
            }

            return lines;
        }

        /// <summary>Reads one key out of a section of a <c>.wui</c>, or null. Mirrors SetKeyInSection.</summary>
        private static string GetKeyInSection(string[] lines, string section, string key)
        {
            string header = "[" + section + "]";
            bool inSection = false;

            foreach (string raw in lines)
            {
                string trimmed = raw.Trim();

                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inSection = string.Equals(trimmed, header, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection || trimmed.Length == 0 || trimmed.StartsWith("#")) continue;

                int eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;

                if (string.Equals(trimmed.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed.Substring(eq + 1).Trim();
                }
            }

            return null;
        }

        /// <summary>
        /// Runs PREACT.exe for one realization. When <paramref name="logPath"/> is given both of
        /// its streams go to that file instead of the console.
        ///
        /// PREACT logs every simulation step, so at --parallel width the inherited console output
        /// is both unreadable (several realizations interleaving line by line) and useless (the
        /// driver's own PROGRESS/error lines get buried). Per-realization files keep the full
        /// detail for diagnosis while leaving the console to the driver. On failure the caller
        /// prints a tail of the file, so a broken run still says why without being tailed by hand.
        /// </summary>
        public static int RunProcess(string exe, string wuiPath, string logPath = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)),
                RedirectStandardOutput = logPath != null,
                RedirectStandardError = logPath != null,
            };
            psi.ArgumentList.Add(wuiPath);

            if (logPath == null)
            {
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
            using (var log = new StreamWriter(logPath, append: false))
            using (var p = new Process { StartInfo = psi })
            {
                //synchronised because stdout and stderr arrive on separate threads
                object sync = new object();
                void Write(string line)
                {
                    if (line == null) return;
                    lock (sync) log.WriteLine(line);
                }

                p.OutputDataReceived += (_, e) => Write(e.Data);
                p.ErrorDataReceived += (_, e) => Write(e.Data);

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        /// <summary>Last <paramref name="lines"/> lines of a log, for failure messages.</summary>
        public static string LogTail(string logPath, int lines = 5)
        {
            try
            {
                string[] all = File.ReadAllLines(logPath);
                int from = Math.Max(0, all.Length - lines);
                return string.Join(System.Environment.NewLine + "    ", all[from..]);
            }
            catch
            {
                return "(no log available)";
            }
        }

        public static string FindPreactExe()
        {
            string cliDir = Path.GetDirectoryName(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
            var candidates = new System.Collections.Generic.List<string>
            {
                Path.Combine(cliDir, "PREACT.exe"),
                Path.GetFullPath(Path.Combine(cliDir, "..", "..", "..", "..", "PREACTexecute", "bin", "Release", "net8.0", "PREACT.exe")),
                Path.GetFullPath(Path.Combine(cliDir, "..", "..", "..", "..", "PREACTexecute", "bin", "Debug", "net8.0", "PREACT.exe")),
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c)) return c;
            }
            return null;
        }
    }
}
