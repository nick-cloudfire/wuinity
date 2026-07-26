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
                    int exit = RunProcess(preactExe, tempWui);
                    try { File.Delete(tempWui); } catch { }

                    if (exit != 0 || !File.Exists(triggerPath))
                    {
                        Console.Error.WriteLine($"[{idx}] run failed or produced no trigger boundary (exit {exit}).");
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

        public static int RunProcess(string exe, string wuiPath)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe)),
            };
            psi.ArgumentList.Add(wuiPath);

            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
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
