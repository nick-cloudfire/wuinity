using System;
using System.Collections.Generic;
using System.IO;
using PREACT.Utility;

namespace PREACTcli
{
    /// <summary>
    /// Probabilistic trigger-boundary driver.
    ///
    /// For each fire realization (indexed TOA/ROS/SD/FI rasters in one folder), it runs a
    /// full WUInity evacuation (via PREACT.exe on a per-realization .wui derived from a base
    /// case), which produces that realization's k-PERIL trigger boundary using a WRSET taken
    /// from that run's own evacuation. It then aggregates all boundaries into a per-cell
    /// probability raster: probability = (# realizations in which the cell lies inside the
    /// trigger boundary) / (# successful realizations).
    /// </summary>
    internal static class ProbabilisticTrigger
    {
        public static int Run(string[] args)
        {
            var opts = ParseArgs(args);

            if (opts.BaseWui == null || opts.RasterDir == null || opts.Count <= 0)
            {
                Console.Error.WriteLine("ERROR: --wui, --dir and --count are required.");
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

            int[,] insideCount = null;
            AscRaster.Header header = default;
            int nSuccess = 0;
            int nFailed = 0;

            for (int n = 0; n < opts.Count; ++n)
            {
                int index = opts.Start + n;
                string idx = index.ToString().PadLeft(opts.Pad, '0');

                //machine-parseable progress line for GUI consumers (n completed of Count)
                Console.WriteLine($"PROGRESS {n}/{opts.Count} realization {idx}");

                string toa = Path.Combine(opts.RasterDir, opts.ToaPattern.Replace("{i}", idx));
                string ros = Path.Combine(opts.RasterDir, opts.RosPattern.Replace("{i}", idx));
                string sd  = Path.Combine(opts.RasterDir, opts.SdPattern.Replace("{i}", idx));
                string fi  = Path.Combine(opts.RasterDir, opts.FiPattern.Replace("{i}", idx));

                bool ok = RealizationRunner.TryRun(
                    preactExe, caseDir, baseName, baseLines, idx, toa, ros, sd, fi,
                    outputDir, opts.Resume, opts.ResumeOnly,
                    out float[,] boundary, out AscRaster.Header h);
                if (!ok)
                {
                    ++nFailed;
                    continue;
                }

                if (insideCount == null)
                {
                    header = h;
                    insideCount = new int[h.Ncols, h.Nrows];
                }
                else if (h.Ncols != header.Ncols || h.Nrows != header.Nrows)
                {
                    Console.Error.WriteLine($"[{idx}] boundary dimensions {h.Ncols}x{h.Nrows} differ from {header.Ncols}x{header.Nrows}, skipping.");
                    ++nFailed;
                    continue;
                }

                for (int x = 0; x < header.Ncols; ++x)
                {
                    for (int y = 0; y < header.Nrows; ++y)
                    {
                        float v = boundary[x, y];
                        //"inside the trigger boundary" = reachable within WRSET (1) or the WUI itself (>=2)
                        if (v >= 1f && v != header.NoDataValue)
                        {
                            insideCount[x, y] += 1;
                        }
                    }
                }
                ++nSuccess;
            }

            if (insideCount == null || nSuccess == 0)
            {
                Console.Error.WriteLine("ERROR: no realizations produced a usable trigger boundary; nothing to aggregate.");
                return 1;
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

            Console.WriteLine($"Done. Aggregated {nSuccess} realizations ({nFailed} skipped/failed).");
            Console.WriteLine("Probability raster: " + outPath);
            Console.WriteLine("PROGRESS " + opts.Count + "/" + opts.Count); //final tick for progress consumers
            return 0;
        }

        // ---- helpers ----------------------------------------------------------------

        private class Options
        {
            public string BaseWui;
            public string RasterDir;
            public int Count;
            public int Start = 1;
            public int Pad = 4;
            public string ToaPattern = "TOA_{i}.tif";
            public string RosPattern = "ROS_{i}.tif";
            public string SdPattern = "SD_{i}.tif";
            public string FiPattern = "FI_{i}.tif";
            public string PreactExe;
            public string OutPath;
            public bool Resume;      //reuse existing boundaries, run only the missing ones
            public bool ResumeOnly;  //never run; aggregate only what already exists
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; ++i)
            {
                switch (args[i])
                {
                    case "--wui":     o.BaseWui = Next(args, ref i); break;
                    case "--dir":     o.RasterDir = Next(args, ref i); break;
                    case "--count":   int.TryParse(Next(args, ref i), out o.Count); break;
                    case "--start":   int.TryParse(Next(args, ref i), out o.Start); break;
                    case "--pad":     int.TryParse(Next(args, ref i), out o.Pad); break;
                    case "--toa":     o.ToaPattern = Next(args, ref i); break;
                    case "--ros":     o.RosPattern = Next(args, ref i); break;
                    case "--sd":      o.SdPattern = Next(args, ref i); break;
                    case "--fi":      o.FiPattern = Next(args, ref i); break;
                    case "--preact":  o.PreactExe = Next(args, ref i); break;
                    case "--out":     o.OutPath = Next(args, ref i); break;
                    case "--resume":  o.Resume = true; break;
                    case "--resume-only": o.Resume = true; o.ResumeOnly = true; break;
                }
            }
            return o;
        }

        private static string Next(string[] args, ref int i)
        {
            return (i + 1 < args.Length) ? args[++i] : null;
        }

        /// <summary>
        /// Replace (or insert) Key=Value inside [Section] of a .wui line array. The .wui
        /// parser strips spaces, so keys are matched on the trimmed "Key=" prefix.
        /// </summary>
        public static string[] SetKeyInSection(string[] lines, string section, string key, string value)
        {
            var result = new List<string>(lines);
            string sectionHeader = "[" + section + "]";
            bool inSection = false;
            int sectionStart = -1;
            int insertAt = -1;

            for (int i = 0; i < result.Count; ++i)
            {
                string trimmed = result[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    if (inSection && insertAt < 0) insertAt = i; //end of our section
                    inSection = string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase);
                    if (inSection) sectionStart = i;
                    continue;
                }
                if (inSection)
                {
                    string noSpace = trimmed.Replace(" ", "");
                    if (noSpace.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        result[i] = key + "=" + value;
                        return result.ToArray();
                    }
                }
            }

            //key not present in the section: insert it (add the section if absent)
            if (sectionStart < 0)
            {
                result.Add("");
                result.Add(sectionHeader);
                result.Add(key + "=" + value);
            }
            else
            {
                if (insertAt < 0) insertAt = result.Count;
                result.Insert(insertAt, key + "=" + value);
            }
            return result.ToArray();
        }

        public static void PrintUsage()
        {
            Console.WriteLine("  PREACTcli probabilistic-trigger --wui <base.wui> --dir <rasterFolder> --count <N>");
            Console.WriteLine("      [--start <n=1>] [--pad <width=4>]");
            Console.WriteLine("      [--toa TOA_{i}.tif] [--ros ROS_{i}.tif] [--sd SD_{i}.tif] [--fi FI_{i}.tif]");
            Console.WriteLine("      [--preact <PREACT.exe>] [--out <probability.asc>] [--resume] [--resume-only]");
        }
    }
}
