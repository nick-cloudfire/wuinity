using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using PREACT.Utility;

namespace PREACTcli
{
    /// <summary>
    /// Convergence-driven probabilistic trigger-boundary driver (docs/probabilistic-trigger-convergence.md).
    ///
    /// Runs realizations (same per-realization mechanics as <see cref="ProbabilisticTrigger"/>:
    /// evacuation + k-PERIL boundary via PREACT.exe) and aggregates them into a running per-cell
    /// probability raster. Instead of a fixed count it stops once the probability raster's
    /// decile-area footprint has stabilized: for a streak of consecutive realizations, every
    /// decile's area changed by less than a tolerance from the previous realization. --max bounds
    /// how many pre-generated realizations may be consumed (this driver does not run ELMFIRE
    /// itself; realizations must already exist on disk).
    ///
    /// Up to <c>--parallel</c> realizations run concurrently, each as its own PREACT.exe OS
    /// process. This is deliberate, not incidental: WUInity's evacuation step runs on SUMO via
    /// libsumo, a native library with process-global state that cannot safely run more than one
    /// simulation per process (see <c>Engine.RunSimulationsParallel</c>'s own code comment on
    /// this) — so genuine concurrency across realizations only works at the OS-process level,
    /// which is exactly what <see cref="RealizationRunner"/> already does per realization. Since
    /// SUMO dominates a realization's wall-clock time (an ELMFIRE run is comparatively quick),
    /// this is where parallelism actually pays off. Aggregation itself stays single-threaded and
    /// order-independent — realizations are i.i.d. Monte Carlo draws, so folding them into the
    /// running probability/streak state in completion order (not launch order) is statistically
    /// equivalent to the strictly-serial version.
    /// </summary>
    internal static class ConvergeTrigger
    {
        private static readonly double[] Deciles = { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 };

        public static int Run(string[] args)
        {
            var opts = ParseArgs(args);

            if (opts.BaseWui == null || opts.RasterDir == null || opts.MaxRealizations <= 0)
            {
                Console.Error.WriteLine("ERROR: --wui, --dir and --max are required.");
                PrintUsage();
                return 1;
            }
            if (!File.Exists(opts.BaseWui))
            {
                Console.Error.WriteLine("ERROR: base .wui not found: " + opts.BaseWui);
                return 1;
            }
            if (!Directory.Exists(opts.RasterDir))
            {
                Console.Error.WriteLine("ERROR: raster folder not found: " + opts.RasterDir);
                return 1;
            }
            if (opts.Streak <= 0 || opts.Tolerance <= 0)
            {
                Console.Error.WriteLine("ERROR: --streak and --tolerance must be positive.");
                return 1;
            }
            if (opts.Parallelism <= 0)
            {
                Console.Error.WriteLine("ERROR: --parallel must be positive.");
                return 1;
            }

            string caseDir = Path.GetDirectoryName(Path.GetFullPath(opts.BaseWui));
            string outputDir = Path.Combine(caseDir, "_output");
            Directory.CreateDirectory(outputDir);

            string preactExe = opts.PreactExe ?? RealizationRunner.FindPreactExe();
            if (!opts.ResumeOnly && (preactExe == null || !File.Exists(preactExe)))
            {
                Console.Error.WriteLine("ERROR: could not locate PREACT.exe; pass --preact <path>. (Use --resume-only to aggregate already-computed boundaries without running.)");
                return 1;
            }

            string baseName = Path.GetFileNameWithoutExtension(opts.BaseWui);
            string[] baseLines = File.ReadAllLines(opts.BaseWui);

            string diagnosticsPath = opts.DiagnosticsPath ?? Path.Combine(outputDir, "trigger_convergence.csv");

            return RunAsync(opts, caseDir, outputDir, preactExe, baseName, baseLines, diagnosticsPath).GetAwaiter().GetResult();
        }

        private class RealizationOutcome
        {
            public bool Ok;
            public string Idx;
            public float[,] Boundary;
            public AscRaster.Header Header;
        }

        private static RealizationOutcome RunRealization(string preactExe, string caseDir, string baseName, string[] baseLines,
            int index, Options opts, string outputDir)
        {
            string idx = index.ToString().PadLeft(opts.Pad, '0');
            string toa = Path.Combine(opts.RasterDir, opts.ToaPattern.Replace("{i}", idx));
            string ros = Path.Combine(opts.RasterDir, opts.RosPattern.Replace("{i}", idx));
            string sd  = Path.Combine(opts.RasterDir, opts.SdPattern.Replace("{i}", idx));
            string fi  = Path.Combine(opts.RasterDir, opts.FiPattern.Replace("{i}", idx));

            bool ok = RealizationRunner.TryRun(
                preactExe, caseDir, baseName, baseLines, idx, toa, ros, sd, fi,
                outputDir, opts.Resume, opts.ResumeOnly,
                out float[,] boundary, out AscRaster.Header h);

            return new RealizationOutcome { Ok = ok, Idx = idx, Boundary = boundary, Header = h };
        }

        private static async Task<int> RunAsync(Options opts, string caseDir, string outputDir, string preactExe,
            string baseName, string[] baseLines, string diagnosticsPath)
        {
            using var diag = new StreamWriter(diagnosticsPath);
            WriteDiagnosticsHeader(diag);

            int[,] insideCount = null;
            AscRaster.Header header = default;
            int nSuccess = 0;
            int nFailed = 0;
            int streak = 0;
            bool converged = false;
            int launched = 0;
            int completed = 0;

            //previous realization's decile areas; null until that decile has a non-zero baseline
            double?[] previousArea = new double?[Deciles.Length];

            var pending = new List<Task<RealizationOutcome>>();

            while (true)
            {
                while (pending.Count < opts.Parallelism && launched < opts.MaxRealizations && !converged)
                {
                    int index = opts.Start + launched;
                    ++launched;
                    pending.Add(Task.Run(() => RunRealization(preactExe, caseDir, baseName, baseLines, index, opts, outputDir)));
                }

                if (pending.Count == 0) break;

                Task<RealizationOutcome> finishedTask = await Task.WhenAny(pending);
                pending.Remove(finishedTask);
                RealizationOutcome result = await finishedTask;
                ++completed;

                Console.WriteLine($"PROGRESS {completed}/{opts.MaxRealizations} realization {result.Idx}");

                if (!result.Ok)
                {
                    ++nFailed;
                    continue;
                }

                if (insideCount == null)
                {
                    header = result.Header;
                    insideCount = new int[header.Ncols, header.Nrows];
                }
                else if (result.Header.Ncols != header.Ncols || result.Header.Nrows != header.Nrows)
                {
                    Console.Error.WriteLine($"[{result.Idx}] boundary dimensions {result.Header.Ncols}x{result.Header.Nrows} differ from {header.Ncols}x{header.Nrows}, skipping.");
                    ++nFailed;
                    continue;
                }

                float[,] boundary = result.Boundary;
                for (int x = 0; x < header.Ncols; ++x)
                {
                    for (int y = 0; y < header.Nrows; ++y)
                    {
                        float v = boundary[x, y];
                        if (v >= 1f && v != header.NoDataValue)
                        {
                            insideCount[x, y] += 1;
                        }
                    }
                }
                ++nSuccess;

                double cellArea = header.CellSize * header.CellSize;
                double[] area = new double[Deciles.Length];
                double?[] delta = new double?[Deciles.Length];
                bool allWithinTolerance = true;
                bool anyDeltaChecked = false;

                for (int t = 0; t < Deciles.Length; ++t)
                {
                    long cellsAtOrAbove = 0;
                    for (int x = 0; x < header.Ncols; ++x)
                    {
                        for (int y = 0; y < header.Nrows; ++y)
                        {
                            double p = (double)insideCount[x, y] / nSuccess;
                            if (p >= Deciles[t]) ++cellsAtOrAbove;
                        }
                    }
                    area[t] = cellsAtOrAbove * cellArea;

                    //a decile with no established (non-zero) baseline yet is excluded from the
                    //convergence test rather than compared against a zero denominator
                    if (previousArea[t].HasValue && previousArea[t].Value > 0)
                    {
                        anyDeltaChecked = true;
                        double d = Math.Abs(area[t] - previousArea[t].Value) / previousArea[t].Value;
                        delta[t] = d;
                        if (d >= opts.Tolerance) allWithinTolerance = false;
                    }

                    previousArea[t] = area[t];
                }

                //the first realization aggregated (and any run where every decile is still
                //awaiting its baseline) has nothing to compare against yet, so it must not count
                //toward the streak in either direction
                if (anyDeltaChecked) streak = allWithinTolerance ? streak + 1 : 0;
                WriteDiagnosticsRow(diag, nSuccess, result.Idx, nSuccess, area, delta, streak);

                Console.WriteLine($"[{result.Idx}] streak {streak}/{opts.Streak} (nSuccess={nSuccess}).");

                if (streak >= opts.Streak)
                {
                    converged = true;
                    Console.WriteLine($"Converged after {nSuccess} realizations ({streak} consecutive within {opts.Tolerance:P0} per decile).");
                }
            }

            if (insideCount == null || nSuccess == 0)
            {
                Console.Error.WriteLine("ERROR: no realizations produced a usable trigger boundary; nothing to aggregate.");
                return 1;
            }

            if (!converged)
            {
                Console.Error.WriteLine($"WARNING: reached --max {opts.MaxRealizations} realizations ({nSuccess} successful) without converging; probability raster is not yet stable.");
            }

            float[,] probability = new float[header.Ncols, header.Nrows];
            for (int x = 0; x < header.Ncols; ++x)
            {
                for (int y = 0; y < header.Nrows; ++y)
                {
                    probability[x, y] = (float)insideCount[x, y] / nSuccess;
                }
            }

            string outPath = opts.OutPath ?? Path.Combine(outputDir, "trigger_probability.asc");
            AscRaster.Header outHeader = header;
            outHeader.NoDataValue = -9999.0;
            AscRaster.Write(probability, outHeader, outPath);

            Console.WriteLine($"Done. Aggregated {nSuccess} realizations ({nFailed} skipped/failed). Converged: {converged}.");
            Console.WriteLine("Probability raster: " + outPath);
            Console.WriteLine("Convergence diagnostics: " + diagnosticsPath);
            Console.WriteLine("PROGRESS " + opts.MaxRealizations + "/" + opts.MaxRealizations);
            return 0;
        }

        // ---- diagnostics CSV ----------------------------------------------------------------

        private static void WriteDiagnosticsHeader(StreamWriter w)
        {
            var cols = new List<string> { "run", "realization_id", "nSuccess", "streak" };
            foreach (double tau in Deciles) cols.Add("area_p" + (int)Math.Round(tau * 100));
            foreach (double tau in Deciles) cols.Add("delta_p" + (int)Math.Round(tau * 100));
            w.WriteLine(string.Join(",", cols));
        }

        private static void WriteDiagnosticsRow(StreamWriter w, int run, string realizationId, int nSuccess, double[] area, double?[] delta, int streak)
        {
            var cols = new List<string> { run.ToString(), realizationId, nSuccess.ToString(), streak.ToString() };
            foreach (double a in area) cols.Add(a.ToString(CultureInfo.InvariantCulture));
            foreach (double? d in delta) cols.Add(d.HasValue ? d.Value.ToString(CultureInfo.InvariantCulture) : "");
            w.WriteLine(string.Join(",", cols));
            w.Flush();
        }

        // ---- args -----------------------------------------------------------------------------

        private class Options
        {
            public string BaseWui;
            public string RasterDir;
            public int MaxRealizations;
            public int Start = 1;
            public int Pad = 4;
            public string ToaPattern = "TOA_{i}.tif";
            public string RosPattern = "ROS_{i}.tif";
            public string SdPattern = "SD_{i}.tif";
            public string FiPattern = "FI_{i}.tif";
            public string PreactExe;
            public string OutPath;
            public string DiagnosticsPath;
            public int Streak = 20;
            public double Tolerance = 0.02;
            public bool Resume;
            public bool ResumeOnly;
            public int Parallelism = Environment.ProcessorCount;
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; ++i)
            {
                switch (args[i])
                {
                    case "--wui":         o.BaseWui = Next(args, ref i); break;
                    case "--dir":         o.RasterDir = Next(args, ref i); break;
                    case "--max":         int.TryParse(Next(args, ref i), out o.MaxRealizations); break;
                    case "--start":       int.TryParse(Next(args, ref i), out o.Start); break;
                    case "--pad":         int.TryParse(Next(args, ref i), out o.Pad); break;
                    case "--toa":         o.ToaPattern = Next(args, ref i); break;
                    case "--ros":         o.RosPattern = Next(args, ref i); break;
                    case "--sd":          o.SdPattern = Next(args, ref i); break;
                    case "--fi":          o.FiPattern = Next(args, ref i); break;
                    case "--preact":      o.PreactExe = Next(args, ref i); break;
                    case "--out":         o.OutPath = Next(args, ref i); break;
                    case "--diagnostics": o.DiagnosticsPath = Next(args, ref i); break;
                    case "--streak":      int.TryParse(Next(args, ref i), out o.Streak); break;
                    case "--tolerance":   double.TryParse(Next(args, ref i), NumberStyles.Any, CultureInfo.InvariantCulture, out o.Tolerance); break;
                    case "--parallel":    int.TryParse(Next(args, ref i), out o.Parallelism); break;
                    case "--resume":      o.Resume = true; break;
                    case "--resume-only": o.Resume = true; o.ResumeOnly = true; break;
                }
            }
            return o;
        }

        private static string Next(string[] args, ref int i)
        {
            return (i + 1 < args.Length) ? args[++i] : null;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("  PREACTcli converge-trigger --wui <base.wui> --dir <rasterFolder> --max <N>");
            Console.WriteLine("      [--start <n=1>] [--pad <width=4>]");
            Console.WriteLine("      [--toa TOA_{i}.tif] [--ros ROS_{i}.tif] [--sd SD_{i}.tif] [--fi FI_{i}.tif]");
            Console.WriteLine("      [--preact <PREACT.exe>] [--out <probability.asc>] [--diagnostics <convergence.csv>]");
            Console.WriteLine("      [--streak <runs=20>] [--tolerance <fraction=0.02>] [--parallel <N=cpuCount>] [--resume] [--resume-only]");
        }
    }
}
