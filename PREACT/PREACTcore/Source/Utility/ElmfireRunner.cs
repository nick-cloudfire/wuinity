using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PREACT.Utility
{
    /// <summary>
    /// Runs one ELMFIRE realization: writes the realization's patched namelist into its own run
    /// directory, invokes the native <c>elmfire</c> binary there, and hands back the four output
    /// rasters the WUInity/k-PERIL half consumes.
    ///
    /// Invocation follows WildfireAV's <c>pipeline/runElmfireCase.py</c>: the binary is run
    /// directly on the case's <c>.data</c> file with the case directory as the working directory —
    /// single rank, no <c>mpiexec</c>. ELMFIRE reports "Running with 1 workers" in that mode.
    ///
    /// Each realization gets a private run directory holding its own <c>outputs/</c> and
    /// <c>scratch/</c>, while <c>inputs/</c> is shared read-only across all of them. That split is
    /// what makes concurrent realizations safe: ELMFIRE converts every input GeoTIFF to an
    /// intermediate ENVI .bsq/.hdr pair before reading it, and writes those next to the input only
    /// when SCRATCH is unset — with SCRATCH set (this writer always sets it) they land there
    /// instead, so parallel runs never write to the same path. Output filenames are not unique
    /// either: with one ensemble member per realization every run produces
    /// <c>time_of_arrival_0000001_*.tif</c>, so a shared outputs directory would have them
    /// overwriting each other.
    /// </summary>
    public static class ElmfireRunner
    {
        /// <summary>ELMFIRE output stems, mapped to the roles AscImport expects.</summary>
        private const string ToaStem = "time_of_arrival";
        private const string RosStem = "vs";           // velocity of spread (m/min with SPREAD_RATE_IN_M)
        private const string SdStem = "spread_dir";
        private const string FiStem = "flin";          // fireline intensity

        public class Result
        {
            public bool Ok;
            public string Toa, Ros, Sd, Fi;
            public string Message;
            public bool Reused;
        }

        /// <summary>
        /// Runs (or reuses) realization <paramref name="runId"/> in <paramref name="runDir"/>.
        /// <paramref name="namelistLines"/> is a template already patched by
        /// the campaign driver, whose OUTPUTS_DIRECTORY / SCRATCH must be the
        /// './outputs' and './scratch' inside <paramref name="runDir"/>.
        /// </summary>
        public static Result Run(string elmfireExe, string runDir, string runId, string[] namelistLines,
                                 bool resume, TextWriter log, string gdalBinDir = null)
        {
            var result = new Result();

            string outputsDir = Path.Combine(runDir, "outputs");
            string scratchDir = Path.Combine(runDir, "scratch");
            Directory.CreateDirectory(outputsDir);
            Directory.CreateDirectory(scratchDir);

            // Resume off means this realization is being computed again, so last time's files must not be
            // able to stand in for it. Emptied rather than left to be overwritten, because ELMFIRE's dump
            // names carry the run's stop time and TryCollectOutputs takes the largest one it finds - so a
            // previous longer run's raster beats this one's and the campaign silently aggregates the old
            // fire. RANDOMIZE_SIMULATION_TSTOP makes that likely even at identical settings, since every
            // realization draws its own stop time.
            //
            // Scratch too: with USE_EXISTING_BSQS on, ELMFIRE reuses the converted rasters it finds there,
            // which would be the ones made from the case's inputs as they were before they changed.
            if (!resume)
            {
                Empty(outputsDir);
                Empty(scratchDir);
            }

            // Resume the same way WildfireAV does: an existing time-of-arrival dump means this
            // realization already ran. Checked before writing the namelist so a resumed run does
            // not need the binary at all.
            if (resume && TryCollectOutputs(outputsDir, result))
            {
                result.Ok = true;
                result.Reused = true;
                log?.WriteLine($"[{runId}] reusing existing ELMFIRE outputs.");
                return result;
            }

            if (elmfireExe == null || !File.Exists(elmfireExe))
            {
                result.Message = "elmfire executable not found; pass --elmfire <path>.";
                return result;
            }

            string dataFile = Path.Combine(runDir, "elmfire.data");
            File.WriteAllLines(dataFile, namelistLines);

            log?.WriteLine($"[{runId}] running ELMFIRE...");
            int exit = RunProcess(elmfireExe, runDir, Path.GetFileName(dataFile), gdalBinDir, out string stderrTail);

            //ELMFIRE's own account of what went wrong comes first, from the log, and only then the exit code.
            //Its diagnostics go to stdout via WRITE(*,*), so the stderr tail never held them - and what stderr
            //does hold on a failure is the MPI epilogue, which is not the problem: every error path in ELMFIRE
            //is a bare Fortran STOP that never calls MPI_FINALIZE, so Intel MPI then complains about "an MPI
            //routine (internal_barrier) before initializing or after finalizing MPICH". Reporting that instead
            //of the line above it describes the exit rather than the cause.
            string diagnosis = DescribeFailure(runDir);

            if (exit != 0)
            {
                result.Message = diagnosis
                                 ?? $"elmfire exited {exit}" + (stderrTail.Length > 0 ? ": " + stderrTail : "");
                return result;
            }

            //A STOP exits 0, so a run can fail without the exit code saying so. The log is what knows.
            if (diagnosis != null)
            {
                result.Message = diagnosis;
                return result;
            }

            if (!TryCollectOutputs(outputsDir, result))
            {
                //Says which raster is missing, not just that something is. This used to blame the
                //time-of-arrival dump whatever was absent, and the one usually absent is the spread
                //direction - it is off by default in ELMFIRE - so the message named the one file that was
                //definitely there.
                result.Message = "elmfire reported success but " + MissingOutputs(outputsDir) + " in " + outputsDir;
                return result;
            }

            // A fire that never spread is not a realization, it is a wasted draw — and left
            // unchecked it is invisible: ELMFIRE exits 0, writes all four rasters, and only the
            // "Fire area: 0.0 acres" line in its log says anything is wrong. Downstream, k-PERIL
            // produces an empty trigger boundary and the driver folds it into the probability
            // raster as though it were a real outcome, biasing every decile toward zero.
            if (TryReadFireArea(runDir, out double acres) && acres <= 0.0)
            {
                result.Message = "elmfire burned 0 acres (the ignition most likely landed on non-burnable fuel)";
                return result;
            }

            result.Ok = true;
            return result;
        }

        /// <summary>
        /// Deletes the directory's contents, keeping the directory. Best-effort per entry: a file held open
        /// by something else must not stop the run, and the one real consequence — a stale raster surviving —
        /// is reported by the caller's own checks rather than papered over here.
        /// </summary>
        private static void Empty(string directory)
        {
            foreach (string path in Directory.GetFiles(directory))
            {
                try { File.Delete(path); } catch { }
            }
            foreach (string path in Directory.GetDirectories(directory))
            {
                try { Directory.Delete(path, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// Finds this run's output rasters. ELMFIRE names dumps
        /// <c>&lt;stem&gt;_&lt;7-digit ensemble member&gt;_&lt;time in seconds&gt;.tif</c> and writes one
        /// per DTDUMP interval, so the set is globbed rather than reconstructed, and the final
        /// (largest-time) dump is the one taken — that is the fully-grown fire k-PERIL needs.
        /// </summary>
        private static bool TryCollectOutputs(string outputsDir, Result result)
        {
            string toa = LatestDump(outputsDir, ToaStem);
            if (toa == null) return false;

            // The other three are matched to the time-of-arrival dump's suffix rather than globbed
            // independently, so a partially-written run cannot pair rasters from different times.
            string suffix = Path.GetFileNameWithoutExtension(toa).Substring(ToaStem.Length);

            result.Toa = toa;
            result.Ros = SiblingDump(outputsDir, RosStem, suffix);
            result.Sd = SiblingDump(outputsDir, SdStem, suffix);
            result.Fi = SiblingDump(outputsDir, FiStem, suffix);

            // FI is optional downstream (AscImport only requires TOA/ROS/SD), the rest are not.
            return result.Ros != null && result.Sd != null;
        }

        /// <summary>
        /// What ELMFIRE said went wrong, read out of its log, or null if it did not complain.
        ///
        /// Needed because ELMFIRE reports failures in a way that hides them: the diagnostic goes to stdout,
        /// the process then dies on a bare Fortran STOP that skips MPI_FINALIZE, and Intel MPI writes its own
        /// complaint to stderr afterwards. So the last thing anyone sees is
        /// "Attempting to use an MPI routine (internal_barrier) before initializing or after finalizing
        /// MPICH", which is true, unhelpful, and about MPI rather than about the input that was wrong.
        /// </summary>
        private static string DescribeFailure(string runDir)
        {
            string logPath = Path.Combine(runDir, "elmfire.log");
            if (!File.Exists(logPath))
            {
                return null;
            }

            //ELMFIRE's own error vocabulary, from the WRITE(*,*) lines that precede each STOP.
            string[] markers =
            {
                "Error", "Problem opening", "not found", "is a required input", "Bad header", "Bad weather row",
                "does not appear to use", "is not specified", "not recognized",
            };

            var found = new System.Collections.Generic.List<string>();
            try
            {
                foreach (string raw in File.ReadAllLines(logPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || IsMpiEpilogue(line))
                    {
                        continue;
                    }

                    foreach (string marker in markers)
                    {
                        if (line.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            //Deduplicated: with several MPI ranks the same line appears once per rank.
                            if (!found.Contains(line)) found.Add(line);
                            break;
                        }
                    }
                }
            }
            catch
            {
                return null;
            }

            if (found.Count == 0)
            {
                return null;
            }

            //The first few only: a cascade after the first failure says less than the first line does.
            if (found.Count > 3) found.RemoveRange(3, found.Count - 3);
            return string.Join(" | ", found) + " (full output in " + logPath + ")";
        }

        /// <summary>
        /// Whether a line is MPI complaining about the way ELMFIRE exited, rather than about the run.
        /// </summary>
        private static bool IsMpiEpilogue(string line)
        {
            return line.IndexOf("MPI routine", StringComparison.OrdinalIgnoreCase) >= 0
                   || line.IndexOf("MPICH", StringComparison.OrdinalIgnoreCase) >= 0
                   || line.IndexOf("before initializing or after finalizing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Which of the rasters the reader needs are not there, with the namelist key that would produce
        /// them - because that, not the file name, is what the user has to change.
        /// </summary>
        private static string MissingOutputs(string outputsDir)
        {
            var missing = new System.Collections.Generic.List<string>();

            string toa = LatestDump(outputsDir, ToaStem);
            if (toa == null)
            {
                missing.Add($"{ToaStem} (DUMP_TIME_OF_ARRIVAL)");
                return "produced no " + string.Join(", no ", missing);
            }

            string suffix = Path.GetFileNameWithoutExtension(toa).Substring(ToaStem.Length);
            if (SiblingDump(outputsDir, RosStem, suffix) == null) missing.Add($"{RosStem} (DUMP_SPREAD_RATE)");
            if (SiblingDump(outputsDir, SdStem, suffix) == null) missing.Add($"{SdStem} (DUMP_SPREAD_DIRECTION)");

            return missing.Count == 0
                ? "produced no usable set of rasters"
                : "produced no " + string.Join(", no ", missing);
        }

        /// <summary>
        /// Reads the burned area back out of ELMFIRE's own log line, e.g.
        /// <c>[1] Meteorology band 1: Case # 1 complete.  Fire area:   3094.0 acres.</c>
        /// Returns false when no such line is present, in which case the caller must not treat the
        /// run as empty — an unparsed log is not evidence of a zero-area fire.
        /// </summary>
        private static bool TryReadFireArea(string runDir, out double acres)
        {
            acres = 0;
            string logPath = Path.Combine(runDir, "elmfire.log");
            if (!File.Exists(logPath)) return false;

            bool found = false;
            foreach (string line in File.ReadLines(logPath))
            {
                int at = line.IndexOf("Fire area:", StringComparison.OrdinalIgnoreCase);
                if (at < 0) continue;

                string rest = line.Substring(at + "Fire area:".Length).TrimStart();
                int end = 0;
                while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.' || rest[end] == '-')) ++end;

                if (end > 0 && double.TryParse(rest.Substring(0, end),
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double v))
                {
                    //take the last reported area: one line per ensemble member/dump
                    acres = v;
                    found = true;
                }
            }
            return found;
        }

        private static string LatestDump(string outputsDir, string stem)
        {
            if (!Directory.Exists(outputsDir)) return null;

            return Directory.GetFiles(outputsDir, stem + "_*.tif")
                            .Select(p => new { Path = p, Time = DumpTime(p, stem) })
                            .Where(x => x.Time >= 0)
                            .OrderByDescending(x => x.Time)
                            .Select(x => x.Path)
                            .FirstOrDefault();
        }

        private static string SiblingDump(string outputsDir, string stem, string suffix)
        {
            string p = Path.Combine(outputsDir, stem + suffix + ".tif");
            return File.Exists(p) ? p : null;
        }

        /// <summary>
        /// Pins the child's GDAL/PROJ resolution to the GDAL installation we were pointed at.
        ///
        /// This is not defensive tidying, it is required. ELMFIRE shells out to gdalsrsinfo to
        /// learn the DEM's EPSG code, and writes its output GeoTIFFs with <c>-a_srs</c> set from
        /// the result. A conflicting PROJ data directory earlier on PATH — SUMO ships one, and
        /// SUMO is on PATH on any machine set up to run WUInity's traffic half — makes that lookup
        /// fail with "proj.db contains DATABASE.LAYOUT.VERSION.MINOR = 4 whereas a number >= 5 is
        /// expected". ELMFIRE then falls back to A_SRS=UNKNOWN, every gdal_translate call fails
        /// with "Failed to process SRS definition", and the run *still exits 0* having deleted its
        /// own .bil intermediates — a silent, total loss of the realization's output.
        ///
        /// So the GDAL bin directory goes to the front of the child's PATH, and PROJ_DATA/PROJ_LIB
        /// (the PROJ 9+ and pre-9 spellings) are pointed at its sibling share\proj when present.
        /// </summary>
        private static void ApplyGdalEnvironment(ProcessStartInfo psi, string gdalBinDir)
        {
            if (string.IsNullOrEmpty(gdalBinDir) || !Directory.Exists(gdalBinDir)) return;

            string existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            SetEnvironmentVariable(psi, "PATH", gdalBinDir + Path.PathSeparator + existingPath);

            string projData = Path.GetFullPath(Path.Combine(gdalBinDir, "..", "share", "proj"));
            if (Directory.Exists(projData))
            {
                SetEnvironmentVariable(psi, "PROJ_DATA", projData);
                SetEnvironmentVariable(psi, "PROJ_LIB", projData);
            }
        }

        /// <summary>
        /// Sets a variable in the child's environment, replacing any entry that differs only in case.
        /// </summary>
        /// <remarks>
        /// Windows environment variable names are case-insensitive, and Windows spells the search path
        /// <c>Path</c>. Whether assigning <c>psi.Environment["PATH"]</c> overwrites that or adds a second
        /// entry beside it depends on the runtime: .NET Core keys this dictionary with
        /// <c>OrdinalIgnoreCase</c> on Windows, Mono keys it ordinally. Under Mono - which is what Unity
        /// runs - the child therefore inherited both <c>Path</c> (the original) and <c>PATH</c> (ours),
        /// and the original won, so ELMFIRE's <c>where gdal_translate</c> found nothing and it fell back
        /// to a blank PATH_TO_GDAL. The failure then surfaced as "DEM CRS does not appear to use metre
        /// linear units", an error about the DEM, which is fine.
        ///
        /// This is why the same code worked from PREACTcli and not from the GUI.
        /// </remarks>
        private static void SetEnvironmentVariable(ProcessStartInfo psi, string name, string value)
        {
            var stale = new System.Collections.Generic.List<string>();
            foreach (string key in psi.Environment.Keys)
            {
                if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(key, name, StringComparison.Ordinal)) stale.Add(key);
            }

            foreach (string key in stale) psi.Environment.Remove(key);
            psi.Environment[name] = value;
        }

        /// <summary>Parses the trailing "_&lt;seconds&gt;" of a dump filename; -1 if it does not parse.</summary>
        private static long DumpTime(string path, string stem)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore < 0 || lastUnderscore + 1 >= name.Length) return -1;
            return long.TryParse(name.Substring(lastUnderscore + 1), out long t) ? t : -1;
        }

        /// <summary>
        /// Runs the binary with <paramref name="runDir"/> as the working directory, since every
        /// path in the namelist is relative to it. Both streams are redirected: ELMFIRE prints a
        /// progress line per timestep, which at --parallel width would otherwise flood the console
        /// and interleave unreadably. Only a tail of stderr is kept, for the failure message.
        /// </summary>
        private static int RunProcess(string exe, string runDir, string dataFileName, string gdalBinDir, out string stderrTail)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = runDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            psi.ArgumentList.Add(dataFileName);
            ApplyGdalEnvironment(psi, gdalBinDir);

            var stderrLines = new System.Collections.Generic.Queue<string>();

            // Kept rather than discarded: ELMFIRE's per-timestep chatter is far too noisy for the
            // console at --parallel width, but it is the only record of what the fire actually did
            // and is needed to diagnose a realization after the fact.
            string logPath = Path.Combine(runDir, "elmfire.log");

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
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;
                    Write(e.Data);
                    lock (stderrLines)
                    {
                        stderrLines.Enqueue(e.Data);
                        while (stderrLines.Count > 5) stderrLines.Dequeue();
                    }
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                // ELMFIRE prompts "Hit Enter to continue" on a fatal input error and would
                // otherwise block forever waiting on a console that is not there.
                p.StandardInput.Close();

                p.WaitForExit();

                lock (stderrLines) stderrTail = string.Join(" | ", stderrLines);
                return p.ExitCode;
            }
        }
    }
}
