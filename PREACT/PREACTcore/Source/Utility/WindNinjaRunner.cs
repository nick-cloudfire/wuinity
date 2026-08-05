using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PREACT.Utility
{
    /// <summary>
    /// Runs <c>WindNinja_cli</c> to turn a single domain-average wind (speed + direction, as the
    /// climatology sampler draws it from a historical peak fire-weather day) into terrain-resolved
    /// wind rasters on the case's master grid — step 3 of the reference pipeline in
    /// docs/probabilistic-trigger-convergence.md. ERA5 wind is far too coarse to resolve a valley
    /// or a ridge; WindNinja is what puts the terrain back in.
    ///
    /// WindNinja is invoked directly rather than through WildfireAV's <c>conda run</c> wrapper:
    /// the Windows installer ships a self-contained <c>WindNinja_cli.exe</c> with no conda
    /// environment involved.
    /// </summary>
    public static class WindNinjaRunner
    {
        /// <summary>Anything at or below this is the -9999 NoData fill, not a wind component.</summary>
        private const double NoDataThreshold = -9000.0;

        /// <summary>
        /// Finds the Windows installer's <c>WindNinja_cli.exe</c> so the terrain-wind stage works
        /// without being pointed at it.
        ///
        /// This lives here, in core, rather than in the CLI that first needed it. It was a private
        /// helper in <c>PREACTcli</c>, which meant the CLI resolved the executable and the GUI could
        /// not: a case built from the scenario editor reported "no WindNinja executable configured"
        /// on a machine with WindNinja installed, and wrote a uniform wind field instead. Terrain
        /// wind then silently differed depending on which front end prepared the case.
        ///
        /// Probed in order of how specifically each names an install: an explicit environment
        /// variable, then <c>PATH</c>, then the standard install roots.
        /// </summary>
        public static string FindExecutable()
        {
            try
            {
                string fromEnv = Environment.GetEnvironmentVariable("WINDNINJA_CLI");
                if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;

                string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                foreach (string dir in path.Split(Path.PathSeparator))
                {
                    if (dir.Length == 0) continue;
                    string candidate;
                    try { candidate = Path.Combine(dir.Trim('"'), "WindNinja_cli.exe"); }
                    catch { continue; } //an unparseable PATH entry is not worth failing detection over
                    if (File.Exists(candidate)) return candidate;
                }

                foreach (string root in InstallRoots())
                {
                    if (!Directory.Exists(root)) continue;
                    //Installs are versioned (WindNinja-3.12.1\bin\...), so take the newest by name.
                    string[] hits = Directory.GetFiles(root, "WindNinja_cli.exe", SearchOption.AllDirectories);
                    if (hits.Length == 0) continue;
                    Array.Sort(hits, StringComparer.OrdinalIgnoreCase);
                    return hits[hits.Length - 1];
                }
            }
            catch { }

            return null;
        }

        private static System.Collections.Generic.IEnumerable<string> InstallRoots()
        {
            yield return @"C:\WindNinja";

            //Read from the environment rather than hardcoded: on a 64-bit machine the installer lands
            //under "Program Files (x86)" often enough that probing only "Program Files" misses it.
            foreach (string variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
            {
                string programFiles = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrEmpty(programFiles))
                {
                    yield return Path.Combine(programFiles, "WindNinja");
                }
            }
        }

        public class Result
        {
            public bool Ok;
            public string Message;
            /// <summary>Domain-mean of the terrain-resolved speed, mph — for reporting against the input.</summary>
            public double MeanSpeedMph;

            /// <summary>Terrain-resolved speed on the master grid, mph, lower-left origin. Null unless Ok.</summary>
            public float[,] SpeedMph;

            /// <summary>Terrain-resolved direction on the master grid, degrees. Null unless Ok.</summary>
            public float[,] DirectionDeg;

            /// <summary>Cells outside WindNinja's mesh that took the domain average instead.</summary>
            public int FilledCells;
        }

        /// <summary>
        /// Turns a domain-average wind into terrain-resolved speed (mph) and direction (degrees) on
        /// <paramref name="grid"/>, returned in <see cref="Result"/> rather than written.
        ///
        /// <paramref name="speedMps"/> is metres per second (this pipeline's convention, matching
        /// Open-Meteo's <c>wind_speed_10m</c>); the fields come out in <b>mph</b>, which is what
        /// ELMFIRE's <c>WS_FILENAME</c> always expects regardless of <c>WS_AT_10M</c>. WindNinja's
        /// own ascii output already defaults to mph, but it is requested explicitly here rather
        /// than inherited from a default that could change.
        /// </summary>
        /// <remarks>
        /// The fields are returned instead of written to <c>ws.tif</c>/<c>wd.tif</c> because the weather
        /// series needs one run per band: writing here made a single band the only thing this could
        /// produce, and stacking bands means the caller owns the file.
        ///
        /// <paramref name="outputDirectory"/> is still needed as scratch space for the warp.
        /// </remarks>
        public static Result Run(
            string windNinjaExe, string demPath, MasterGrid grid, string outputDirectory,
            double speedMps, double directionDeg,
            string vegetation = "grass", string meshChoice = "coarse", int threads = 4,
            Action<string> log = null)
        {
            var result = new Result();

            if (string.IsNullOrEmpty(windNinjaExe) || !File.Exists(windNinjaExe))
            {
                result.Message = "WindNinja_cli not found";
                return result;
            }

            //WindNinja writes its outputs next to the elevation file it was given, naming them
            //after the direction/speed/resolution of the run. A private directory per call keeps
            //concurrent realizations from reading each other's output, and makes the "newest
            //*_vel.asc" lookup below unambiguous.
            string work = Path.Combine(outputDirectory, "_windninja");
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);

            //Absolute: WindNinja resolves --elevation_file against its own working directory, so a
            //relative path built from the caller's cwd silently becomes "not found".
            string localDem = Path.GetFullPath(Path.Combine(work, "dem.tif"));
            File.Copy(demPath, localDem, overwrite: true);

            try
            {
                int exit = Invoke(windNinjaExe, work, localDem, speedMps, directionDeg, vegetation, meshChoice, threads, out string tail);
                if (exit != 0)
                {
                    result.Message = $"WindNinja_cli exited {exit}: {tail}";
                    return result;
                }

                string vel = Newest(work, "*_vel.asc");
                string ang = Newest(work, "*_ang.asc");
                if (vel == null || ang == null)
                {
                    result.Message = "WindNinja produced no *_vel.asc / *_ang.asc output";
                    return result;
                }

                const double mpsToMph = 2.2369362920544;
                result.MeanSpeedMph = Resample(vel, ang, grid, outputDirectory,
                                               speedMps * mpsToMph, directionDeg,
                                               out result.SpeedMph, out result.DirectionDeg, out int filled);
                result.FilledCells = filled;
                result.Ok = true;
                log?.Invoke($"    WindNinja: {Path.GetFileName(vel)} -> speed/direction on the master grid " +
                            $"(mean {result.MeanSpeedMph:F1} mph" +
                            (filled > 0 ? $", {filled} edge cells outside WindNinja's mesh filled with the domain average" : "") + ").");
                return result;
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { }
            }
        }

        private static int Invoke(
            string exe, string workDir, string demPath, double speedMps, double directionDeg,
            string vegetation, string meshChoice, int threads, out string tail)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = workDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            void Arg(string k, string v) { psi.ArgumentList.Add(k); psi.ArgumentList.Add(v); }

            Arg("--num_threads", threads.ToString(CultureInfo.InvariantCulture));
            Arg("--elevation_file", demPath);
            Arg("--initialization_method", "domainAverageInitialization");
            Arg("--input_speed", D(speedMps));
            Arg("--input_speed_units", "mps");
            Arg("--input_direction", D(directionDeg));
            Arg("--input_wind_height", "10");
            Arg("--units_input_wind_height", "m");
            //Matches the WS_AT_10M = .TRUE. the namelist declares.
            Arg("--output_wind_height", "10");
            Arg("--units_output_wind_height", "m");
            //ELMFIRE reads WS_FILENAME as mph; asking explicitly rather than relying on the default.
            Arg("--output_speed_units", "mph");
            Arg("--vegetation", vegetation);
            Arg("--mesh_choice", meshChoice);
            Arg("--write_ascii_output", "true");
            Arg("--ascii_out_resolution", "-1");
            Arg("--units_ascii_out_resolution", "m");

            var lines = new System.Collections.Generic.Queue<string>();
            using (var p = new Process { StartInfo = psi })
            {
                object sync = new object();
                void Take(string s)
                {
                    if (s == null) return;
                    lock (sync)
                    {
                        lines.Enqueue(s);
                        while (lines.Count > 5) lines.Dequeue();
                    }
                }

                p.OutputDataReceived += (_, e) => Take(e.Data);
                p.ErrorDataReceived += (_, e) => Take(e.Data);
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();

                lock (sync) tail = string.Join(" | ", lines);
                return p.ExitCode;
            }
        }

        /// <summary>
        /// Puts WindNinja's coarse-mesh speed/direction onto the master grid.
        ///
        /// Direction is <b>not</b> resampled directly. A bearing field wraps at 360, and
        /// interpolating across that seam averages 350° and 10° to 180° — exactly backwards, which
        /// for a northerly wind would push every fire the wrong way. Speed and direction are
        /// therefore decomposed into vector components, each component warped independently, and
        /// the bearing recomposed afterwards, which is continuous everywhere.
        /// </summary>
        private static double Resample(
            string velPath, string angPath, MasterGrid grid, string outputDirectory,
            double fallbackSpeedMph, double fallbackDirectionDeg,
            out float[,] speedOut, out float[,] directionOut, out int filled)
        {
            float[,] speed = AscRaster.ReadAsc(velPath, out AscRaster.Header header, out bool speedOk);
            float[,] angle = AscRaster.ReadAsc(angPath, out AscRaster.Header _, out bool angleOk);
            if (!speedOk || !angleOk || speed == null || angle == null)
            {
                throw new Exception("Could not read WindNinja output: " + velPath);
            }

            int nx = speed.GetLength(0);
            int ny = speed.GetLength(1);
            var u = new float[nx, ny];
            var v = new float[nx, ny];

            const double degToRad = System.Math.PI / 180.0;
            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y)
                {
                    double s = speed[x, y];
                    double a = angle[x, y] * degToRad;
                    //bearing convention: sin on the first component, cos on the second, inverted
                    //the same way below - the decomposition only has to be self-consistent.
                    u[x, y] = (float)(s * System.Math.Sin(a));
                    v[x, y] = (float)(s * System.Math.Cos(a));
                }
            }

            //WindNinja writes UTM ascii with a .prj beside it; it is the same UTM zone the master
            //grid is in, since the DEM it was handed defines both.
            var sourceGrid = new MasterGrid { Header = header, Epsg = grid.Epsg };

            string tmpU = Path.Combine(outputDirectory, "_wn_u.tif");
            string tmpV = Path.Combine(outputDirectory, "_wn_v.tif");
            string warpU = Path.Combine(outputDirectory, "_wn_u_grid.tif");
            string warpV = Path.Combine(outputDirectory, "_wn_v_grid.tif");

            try
            {
                GeoTiffRasterWriter.WriteBand(sourceGrid, u, tmpU);
                GeoTiffRasterWriter.WriteBand(sourceGrid, v, tmpV);
                RasterHarmonizer.WarpToGrid(tmpU, warpU, grid, "bilinear");
                RasterHarmonizer.WarpToGrid(tmpV, warpV, grid, "bilinear");

                float[,] gu = AscRaster.ReadGeoTiff(warpU, out AscRaster.Header _, out bool ok1);
                float[,] gv = AscRaster.ReadGeoTiff(warpV, out AscRaster.Header _, out bool ok2);
                if (!ok1 || !ok2) throw new Exception("Could not read the warped WindNinja components back.");

                int gx = grid.Header.Ncols;
                int gy = grid.Header.Nrows;
                var ws = new float[gx, gy];
                var wd = new float[gx, gy];
                double total = 0;
                filled = 0;

                for (int x = 0; x < gx; ++x)
                {
                    for (int y = 0; y < gy; ++y)
                    {
                        double uu = gu[x, y];
                        double vv = gv[x, y];

                        //WindNinja's mesh is always a little smaller than the DEM it was given, so
                        //warping it onto the master grid leaves a NoData fringe. Left alone, those
                        //-9999 components recompose into a ~14000 mph gale around the domain edge -
                        //which is both physically absurd and, because ELMFIRE reads the whole
                        //raster, would drive spread at the boundary. Fill them with the
                        //domain-average wind the run was initialized from instead.
                        if (uu <= NoDataThreshold || vv <= NoDataThreshold)
                        {
                            ++filled;
                            ws[x, y] = (float)fallbackSpeedMph;
                            wd[x, y] = (float)fallbackDirectionDeg;
                            total += fallbackSpeedMph;
                            continue;
                        }

                        double s = System.Math.Sqrt(uu * uu + vv * vv);
                        double a = System.Math.Atan2(uu, vv) / degToRad;
                        if (a < 0) a += 360.0;

                        ws[x, y] = (float)s;
                        wd[x, y] = (float)a;
                        total += s;
                    }
                }

                speedOut = ws;
                directionOut = wd;
                return total / (gx * gy);
            }
            finally
            {
                foreach (string f in new[] { tmpU, tmpV, warpU, warpV })
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                }
            }
        }

        private static string Newest(string dir, string pattern)
        {
            return Directory.GetFiles(dir, pattern)
                            .OrderByDescending(File.GetLastWriteTimeUtc)
                            .FirstOrDefault();
        }

        private static string D(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
