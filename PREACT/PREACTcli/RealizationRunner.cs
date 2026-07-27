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
        public static bool TryRun(
            string preactExe, string caseDir, string baseName, string[] baseLines,
            string idx, string toa, string ros, string sd, string fi,
            string outputDir, bool resume, bool resumeOnly,
            out float[,] boundary, out AscRaster.Header header)
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
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "TimeOfArrivalFile", toa);
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "RateOfSpreadFile", ros);
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "SpreadDirectionFile", sd);
                    if (File.Exists(fi)) lines = ProbabilisticTrigger.SetKeyInSection(lines, "AscImport", "FirelineIntensityFile", fi);
                    lines = ProbabilisticTrigger.SetKeyInSection(lines, "kPERIL", "OutputName", outputName);
                    File.WriteAllLines(tempWui, lines);

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
