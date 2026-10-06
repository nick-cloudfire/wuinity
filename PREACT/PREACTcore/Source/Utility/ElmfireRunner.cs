using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace PREACT.Utility
{
    /// <summary>
    /// Runs one ELMFIRE fire: writes the patched namelist into the run directory's <c>outputs/</c>, invokes the
    /// native <c>elmfire</c> binary with the run directory as the working directory, and hands back the rasters
    /// the WUInity/k-PERIL half consumes.
    ///
    /// Invocation follows WildfireAV's <c>pipeline/runElmfireCase.py</c>: the binary is run directly on the
    /// namelist - single rank, no <c>mpiexec</c>. ELMFIRE reports "Running with 1 workers" in that mode.
    ///
    /// Each run gets its own <c>outputs/</c> and <c>scratch/</c>, while the inputs are shared read-only. That
    /// split is what makes concurrent realizations safe: ELMFIRE converts every input GeoTIFF to an ENVI
    /// .bsq/.hdr pair before reading it and writes those next to the input only when SCRATCH is unset.
    /// Output names are not unique across runs either (<c>time_of_arrival_0000001_*.tif</c> for every
    /// single-member case), so a shared outputs directory would have them overwriting each other.
    ///
    /// The namelist is written to <c>outputs/run.data</c> rather than over the case's own <c>elmfire.data</c>:
    /// the template is the user's, and a campaign reading it while a single run rewrote it picked up the change
    /// mid-campaign. The file that produced a set of outputs now lives with them.
    /// </summary>
    public static class ElmfireRunner
    {
        /// <summary>Where the run's namelist is written, inside <c>outputs/</c>.</summary>
        public const string RunNamelistName = "run.data";

        /// <summary>
        /// Written beside the outputs after a successful run: what the run was computed from, so a later run
        /// reuses the outputs only when nothing that decides them has changed.
        /// </summary>
        public const string FingerprintName = "run.fingerprint";

        public class Result
        {
            public bool Ok;
            public string Toa, Ros, Sd, Fi;

            /// <summary>Midflame wind speed, ft/min, on the cells the fire reached. Null when the build of
            /// ELMFIRE predates DUMP_MIDFLAME_WINDSPEED.</summary>
            public string Mfws;

            public string Message;

            /// <summary>Outputs from an earlier identical run were reused and ELMFIRE was not invoked.</summary>
            public bool Reused;

            /// <summary>The run was stopped by <see cref="CancelAll"/>.</summary>
            public bool Cancelled;

            /// <summary>
            /// ELMFIRE completed and the fire did not spread (0 acres). Not <see cref="Ok"/>, but the rasters are
            /// set: for a campaign this is a realization that threatened nothing, not a failure.
            /// </summary>
            public bool NoSpread;

            /// <summary>ELMFIRE hit its wall-clock <c>MAX_RUNTIME</c> and dumped a truncated fire.</summary>
            public bool MaxRuntimeHit;

            /// <summary>Final burned area from ELMFIRE's log, or -1 when it did not report one.</summary>
            public double FireAreaAcres = -1.0;

            /// <summary>The namelist the run used, <c>outputs/run.data</c>.</summary>
            public string NamelistPath;

            public TimeSpan Elapsed;
        }

        /// <summary>
        /// Kills every ELMFIRE (and WindNinja) process tree this process started, and makes the runs they
        /// belong to return a failure with <see cref="Result.Cancelled"/> set. Runs started afterwards are not
        /// affected.
        /// </summary>
        /// <remarks>
        /// Contract C3. <see cref="Engine.CloseSimulations"/> calls this - the GUI's Stop and every way of quitting
        /// go through it - because ELMFIRE otherwise runs to completion in the background after the simulation that
        /// wanted it has gone. A run cancelled before its process started is caught too: see
        /// <see cref="ElmfireProcesses.Register(System.Diagnostics.Process, long)"/>.
        /// </remarks>
        public static void CancelAll()
        {
            ElmfireProcesses.KillAll();
        }

        /// <summary>
        /// Runs (or reuses) one fire in <paramref name="runDir"/>. <paramref name="namelistLines"/> must already
        /// be patched: every directory in it relative to <paramref name="runDir"/>, OUTPUTS_DIRECTORY and
        /// SCRATCH <c>./outputs</c> and <c>./scratch</c>.
        /// </summary>
        /// <param name="reuse">
        /// Use outputs already in <c>outputs/</c> instead of running. Honoured only when
        /// <paramref name="fingerprint"/> matches the one recorded with them - a fire from another ignition,
        /// stop time or fuel is not this fire.
        /// </param>
        /// <param name="fingerprint">
        /// What decides this run's result (see <see cref="ElmfireFingerprint"/>). Recorded after a successful run.
        /// Null disables reuse.
        /// </param>
        /// <param name="timestep">
        /// Called with every "Current Timestep" line ELMFIRE prints, on the process's output thread, as it runs (the
        /// campaign's live status). The lines still go to <c>elmfire.log</c> as before. Null: not called.
        /// </param>
        public static Result Run(string elmfireExe, string runDir, string runId, string[] namelistLines,
                                 bool reuse, TextWriter log, string gdalBinDir = null, string fingerprint = null,
                                 Action<ElmfireTimestep> timestep = null)
        {
            var result = new Result();
            var clock = Stopwatch.StartNew();
            long generation = ElmfireProcesses.Generation;

            string outputsDir = Path.Combine(runDir, "outputs");
            string scratchDir = Path.Combine(runDir, "scratch");
            Directory.CreateDirectory(outputsDir);
            Directory.CreateDirectory(scratchDir);

            string fingerprintPath = Path.Combine(outputsDir, FingerprintName);
            result.NamelistPath = Path.Combine(outputsDir, RunNamelistName);

            if (reuse && fingerprint != null && TryReadAllText(fingerprintPath) == fingerprint
                && TryCollectOutputs(outputsDir, result))
            {
                result.Ok = true;
                result.Reused = true;
                TryReadFireArea(runDir, out result.FireAreaAcres);
                log?.WriteLine($"[{runId}] reusing ELMFIRE outputs computed from the same namelist and inputs.");
                return result;
            }

            if (reuse && fingerprint != null && File.Exists(fingerprintPath))
            {
                log?.WriteLine($"[{runId}] the namelist or the inputs changed since the outputs were written; running again.");
            }

            // Emptied rather than left to be overwritten: dump names carry the run's stop time and the largest
            // one is taken, so a previous longer run's raster would beat this one's. Scratch too, because with
            // USE_EXISTING_BSQS ELMFIRE reuses converted rasters it finds there.
            Empty(outputsDir);
            Empty(scratchDir);

            if (elmfireExe == null || !File.Exists(elmfireExe))
            {
                result.Message = string.IsNullOrEmpty(elmfireExe)
                    ? "no ELMFIRE executable was given."
                    : "the ELMFIRE executable is not there: " + elmfireExe + ".";
                return result;
            }

            File.WriteAllLines(result.NamelistPath, namelistLines);

            log?.WriteLine($"[{runId}] running ELMFIRE...");
            int exit = RunProcess(elmfireExe, runDir, "outputs/" + RunNamelistName, gdalBinDir, generation, timestep,
                out string stderrTail);
            result.Elapsed = clock.Elapsed;

            if (ElmfireProcesses.CancelledSince(generation))
            {
                result.Cancelled = true;
                result.Message = "ELMFIRE was cancelled.";
                return result;
            }

            //ELMFIRE's own account of what went wrong comes first, from the log, and only then the exit code.
            //Its diagnostics go to stdout via WRITE(*,*); stderr on a failure holds the MPI epilogue of a bare
            //Fortran STOP ("an MPI routine ... before initializing or after finalizing"), which is not the cause.
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
                result.Message = "elmfire reported success but " + MissingOutputs(outputsDir) + " in " + outputsDir;
                return result;
            }

            TryReadFireArea(runDir, out result.FireAreaAcres);

            if (TryReadMaxRuntimeStop(runDir, out string stopLine))
            {
                //Truncated, not finished: ELMFIRE stops propagating and dumps the fire as it stood. For a
                //campaign that is a fire that did not get the time it was given, and aggregating it would count
                //its unreached ground as safe.
                result.MaxRuntimeHit = true;
                result.Message = "ELMFIRE hit its wall-clock limit (MAX_RUNTIME) and stopped the fire early: "
                                 + stopLine;
                return result;
            }

            // A fire that never spread is not a fire, and left unchecked it is invisible: ELMFIRE exits 0 and
            // writes every raster, and only its log's fire area says anything is wrong. That area is the cells it
            // ignited: "0.0 acres" for one 10 m cell, but "0.2 acres" for one 30 m cell (Auburn2's ignition in urban
            // fuel), which a test for zero let through as a fire.
            if (DidNotSpread(result.FireAreaAcres, CellSizeOf(result.Toa), IgnitionCount(namelistLines)))
            {
                result.NoSpread = true;
                result.Message = result.FireAreaAcres == 0.0
                    ? "elmfire burned 0 acres (the ignition most likely landed on non-burnable fuel)"
                    : $"elmfire's fire did not spread beyond the cell(s) it was ignited in ({result.FireAreaAcres.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} acres): "
                      + "the ignition most likely landed on non-burnable fuel";
                return result;
            }

            if (result.Mfws == null)
            {
                log?.WriteLine($"[{runId}] WARNING: no midflame wind raster (mfws_*.tif). This elmfire build predates "
                               + "DUMP_MIDFLAME_WINDSPEED; k-PERIL will fall back to the 10 m wind.");
            }

            if (fingerprint != null)
            {
                try { File.WriteAllText(fingerprintPath, fingerprint); } catch { }
            }

            result.Ok = true;
            return result;
        }

        private static string TryReadAllText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : null; }
            catch { return null; }
        }

        /// <summary>
        /// Deletes the directory's contents, keeping the directory. Best-effort per entry: a file held open by
        /// something else must not stop the run, and a stale raster surviving is caught by the caller's checks.
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
        /// Finds this run's output rasters. Dumps are named
        /// <c>&lt;stem&gt;_&lt;7-digit ensemble member&gt;_&lt;time in seconds&gt;.tif</c>, one per DTDUMP, so the
        /// set is globbed and the final (largest-time) dump taken - the fully-grown fire k-PERIL needs.
        /// </summary>
        public static bool TryCollectOutputs(string outputsDir, Result result)
        {
            string toa = LatestDump(outputsDir, ElmfireStems.TimeOfArrival);
            if (toa == null) return false;

            // The others are matched to the time-of-arrival dump's suffix rather than globbed independently, so
            // a partially-written run cannot pair rasters from different times. The midflame wind is written on
            // the final dump only, which is the one taken.
            string suffix = Path.GetFileNameWithoutExtension(toa).Substring(ElmfireStems.TimeOfArrival.Length);

            result.Toa = toa;
            result.Ros = SiblingDump(outputsDir, ElmfireStems.SpreadRate, suffix);
            result.Sd = SiblingDump(outputsDir, ElmfireStems.SpreadDirection, suffix);
            result.Fi = SiblingDump(outputsDir, ElmfireStems.FirelineIntensity, suffix);
            result.Mfws = SiblingDump(outputsDir, ElmfireStems.MidflameWindSpeed, suffix);

            // FI and the midflame wind are optional downstream; the rest are not.
            return result.Ros != null && result.Sd != null;
        }

        /// <summary>
        /// What ELMFIRE said went wrong, read out of its log, or null if it did not complain.
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

            //An ELMFIRE built before a7fb9d6 refuses the midflame key when it reads &OUTPUTS. Its message names the
            //key but not the remedy, and the remedy is a rebuild, not a namelist edit.
            string hint = string.Empty;
            if (found.Exists(l => l.IndexOf(ElmfireNamelistKeys.DumpMidflameWindSpeed, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                hint = " - this elmfire build predates DUMP_MIDFLAME_WINDSPEED; rebuild it from the ELMFIRE-WUINITY "
                       + "submodule (a7fb9d6 or later)";
            }

            //The first few only: a cascade after the first failure says less than the first line does.
            if (found.Count > 3) found.RemoveRange(3, found.Count - 3);
            return string.Join(" | ", found) + hint + " (full output in " + logPath + ")";
        }

        /// <summary>Whether a line is MPI complaining about the way ELMFIRE exited, rather than about the run.</summary>
        private static bool IsMpiEpilogue(string line)
        {
            return line.IndexOf("MPI routine", StringComparison.OrdinalIgnoreCase) >= 0
                   || line.IndexOf("MPICH", StringComparison.OrdinalIgnoreCase) >= 0
                   || line.IndexOf("before initializing or after finalizing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Which of the rasters the reader needs are not there, with the namelist key that would produce them.
        /// </summary>
        private static string MissingOutputs(string outputsDir)
        {
            var missing = new System.Collections.Generic.List<string>();

            string toa = LatestDump(outputsDir, ElmfireStems.TimeOfArrival);
            if (toa == null)
            {
                missing.Add($"{ElmfireStems.TimeOfArrival} (DUMP_TIME_OF_ARRIVAL)");
                return "produced no " + string.Join(", no ", missing);
            }

            string suffix = Path.GetFileNameWithoutExtension(toa).Substring(ElmfireStems.TimeOfArrival.Length);
            if (SiblingDump(outputsDir, ElmfireStems.SpreadRate, suffix) == null)
                missing.Add($"{ElmfireStems.SpreadRate} (DUMP_SPREAD_RATE)");
            if (SiblingDump(outputsDir, ElmfireStems.SpreadDirection, suffix) == null)
                missing.Add($"{ElmfireStems.SpreadDirection} (DUMP_SPREAD_DIRECTION)");

            return missing.Count == 0
                ? "produced no usable set of rasters"
                : "produced no " + string.Join(", no ", missing);
        }

        /// <summary>m² per acre.</summary>
        private const double SquareMetresPerAcre = 4046.8564224;

        /// <summary>
        /// Whether a fire area ELMFIRE reported (acres, to one decimal) is no more than the cells it was ignited in, so
        /// the fire did not spread at all. A cell size that is not known (0) leaves only an area of 0 as "no spread".
        /// </summary>
        public static bool DidNotSpread(double acres, double cellSizeMetres, int ignitions)
        {
            if (acres < 0.0) return false;
            if (acres == 0.0) return true;
            if (cellSizeMetres <= 0.0) return false;
            double ignited = System.Math.Max(1, ignitions) * cellSizeMetres * cellSizeMetres / SquareMetresPerAcre;
            //ELMFIRE prints one decimal: 0.222 acres comes out as 0.2, and two 30 m cells (0.44) as 0.4.
            return acres <= ignited + 0.05;
        }

        /// <summary>The cell size of a raster ELMFIRE wrote, or 0 when it cannot be read.</summary>
        private static double CellSizeOf(string raster)
        {
            try
            {
                return string.IsNullOrEmpty(raster) || !File.Exists(raster) ? 0.0 : MasterGrid.FromRasterFile(raster).Header.CellSize;
            }
            catch
            {
                return 0.0;
            }
        }

        /// <summary>The fixed ignitions a namelist places (<c>NUM_IGNITIONS</c>), or 1 for random ignitions or none named.</summary>
        private static int IgnitionCount(string[] namelistLines)
        {
            if (namelistLines == null) return 1;
            string random = ElmfireNamelist.GetKeyInGroup(namelistLines, ElmfireNamelistKeys.MonteCarloGroup, "RANDOM_IGNITIONS");
            if (random != null && random.Trim().Trim('.').Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return 1;
            string n = ElmfireNamelist.GetKeyInGroup(namelistLines, ElmfireNamelistKeys.SimulatorGroup, "NUM_IGNITIONS");
            return int.TryParse(n?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                out int count) && count > 0 ? count : 1;
        }

        /// <summary>
        /// Reads the burned area back out of ELMFIRE's own log line, e.g.
        /// <c>[1] Meteorology band 1: Case # 1 complete.  Fire area:   3094.0 acres.</c> Returns false when there
        /// is no such line - an unparsed log is not evidence of a zero-area fire.
        /// </summary>
        public static bool TryReadFireArea(string runDir, out double acres)
        {
            acres = -1.0;
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

        /// <summary>ELMFIRE's "STOPPED: ELAPSED TIME ... GREATER THAN MAX_RUNTIME" line, when it wrote one.</summary>
        private static bool TryReadMaxRuntimeStop(string runDir, out string line)
        {
            line = null;
            string logPath = Path.Combine(runDir, "elmfire.log");
            if (!File.Exists(logPath)) return false;

            foreach (string raw in File.ReadLines(logPath))
            {
                if (raw.IndexOf("GREATER THAN MAX_RUNTIME", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    line = raw.Trim();
                    return true;
                }
            }
            return false;
        }

        private static string LatestDump(string outputsDir, string stem)
        {
            if (!Directory.Exists(outputsDir)) return null;

            return Directory.GetFiles(outputsDir, stem + "_*.tif")
                            .Select(p => new { Path = p, Time = DumpTime(p) })
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
        /// Required, not tidying. ELMFIRE shells out to gdalsrsinfo to learn the DEM's EPSG code and writes its
        /// output GeoTIFFs with <c>-a_srs</c> from the result. A conflicting PROJ data directory earlier on PATH
        /// - SUMO ships one - makes that lookup fail with "proj.db contains DATABASE.LAYOUT.VERSION.MINOR = 4";
        /// ELMFIRE then falls back to A_SRS=UNKNOWN, every gdal_translate fails, and the run still exits 0 having
        /// deleted its own .bil intermediates. So the GDAL bin directory goes to the front of the child's PATH and
        /// PROJ_DATA/PROJ_LIB are pointed at its sibling share/proj when present.
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
        /// Windows spells the search path <c>Path</c>, and whether assigning <c>psi.Environment["PATH"]</c>
        /// replaces it depends on the runtime: .NET Core keys the dictionary case-insensitively on Windows, Mono
        /// (Unity) ordinally - so under Mono the child inherited both and the original won, and ELMFIRE's
        /// <c>where gdal_translate</c> found nothing. This is why the same code worked from PREACTcli and not from
        /// the GUI.
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
        private static long DumpTime(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int lastUnderscore = name.LastIndexOf('_');
            if (lastUnderscore < 0 || lastUnderscore + 1 >= name.Length) return -1;
            return long.TryParse(name.Substring(lastUnderscore + 1), out long t) ? t : -1;
        }

        /// <summary>
        /// Runs the binary with <paramref name="runDir"/> as the working directory, since every path in the
        /// namelist is relative to it. Both streams go to <c>elmfire.log</c> - ELMFIRE prints a progress line per
        /// timestep, which at --parallel width would flood and interleave on the console - and a tail of stderr
        /// is kept for the failure message. Registered with <see cref="ElmfireProcesses"/> so a cancel reaches it.
        /// </summary>
        private static int RunProcess(string exe, string runDir, string dataFileName, string gdalBinDir, long generation,
                                      Action<ElmfireTimestep> timestep, out string stderrTail)
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

                p.OutputDataReceived += (_, e) =>
                {
                    Write(e.Data);
                    if (timestep != null && ElmfireTimestep.TryParse(e.Data, out ElmfireTimestep step))
                    {
                        //A watcher's failure is not the fire's.
                        try { timestep(step); } catch { }
                    }
                };
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
                //Killed at once if the run was cancelled while it was still being set up.
                ElmfireProcesses.Register(p, generation);
                try
                {
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();

                    // ELMFIRE prompts "Hit Enter to continue" on a fatal input error and would otherwise block
                    // forever waiting on a console that is not there. (It may already have been killed.)
                    try { p.StandardInput.Close(); } catch (IOException) { }

                    p.WaitForExit();
                }
                finally
                {
                    ElmfireProcesses.Unregister(p);
                }

                lock (stderrLines) stderrTail = string.Join(" | ", stderrLines);
                return p.ExitCode;
            }
        }
    }
}
