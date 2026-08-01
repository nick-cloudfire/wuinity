//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Utility
{
    /// <summary>
    /// Gets a fire out of ELMFIRE for a scenario: builds the case if asked, runs <c>elmfire.exe</c>, and
    /// reports where the four rasters it produced are.
    ///
    /// This is the whole of the coupling. ELMFIRE computes a fire to completion and writes rasters, so there
    /// is nothing to step and nothing to interleave with the evacuation - once it has run, its output is a
    /// pre-computed fire, which is exactly what <see cref="Wildfire.AscFireImport"/> already reads. The
    /// module therefore runs this and then hands the paths to that reader rather than reimplementing it, so
    /// an ELMFIRE fire and an imported one are the same code from that point on.
    ///
    /// Blocking, and deliberately so: a fire that has not finished computing cannot be evacuated from. What
    /// keeps it tolerable is that output for an unchanged case is reused, so the cost falls on the first run
    /// of a case and not on every run.
    /// </summary>
    public static class ElmfireCoupling
    {
        public class Result
        {
            public bool Ok;

            /// <summary>The four rasters, relative to the scenario root, as AscImport wants them.</summary>
            public string TimeOfArrivalFile, RateOfSpreadFile, SpreadDirectionFile, FirelineIntensityFile;

            /// <summary>
            /// The case's own wind rasters, relative to the scenario root, or empty when it has none.
            ///
            /// Handed back so k-PERIL can be given the same wind the fire was computed with rather than a
            /// separately configured field that can silently disagree with it. Hourly, one band per hour.
            /// </summary>
            public string WindSpeedFile = string.Empty, WindDirectionFile = string.Empty;

            /// <summary>True when an earlier run's output was used and ELMFIRE was not invoked.</summary>
            public bool Reused;

            public string Message = string.Empty;
        }

        /// <summary>
        /// Where the vendored ELMFIRE sits relative to the Unity project, for a scenario that does not name
        /// one. Probed upwards, because the working directory differs between the editor, a player build and
        /// the command line.
        /// </summary>
        private static readonly string[] VendoredExeRelativePaths =
        {
            "WUInity/Assets/ThirdParty/elmfire/build/windows/bin/elmfire.exe",
            "Assets/ThirdParty/elmfire/build/windows/bin/elmfire.exe",
            "ThirdParty/elmfire/build/windows/bin/elmfire.exe",
        };

        public static Result Prepare(PREACTInput input, ElmfireInput settings, Action<string> log)
        {
            var result = new Result();
            void Log(string m) => log?.Invoke(m);

            string caseDir = Path.Combine(input.RootFolder, settings.CaseDirectory);

            if (settings.BuildCase)
            {
                if (!TryBuildCase(input, settings, caseDir, Log, out string buildProblem))
                {
                    result.Message = buildProblem;
                    return result;
                }
            }

            if (!Directory.Exists(caseDir))
            {
                result.Message = $"There is no ELMFIRE case at {settings.CaseDirectory}. Turn BuildCase on, or "
                                 + "build one with PREACTcli build-case.";
                return result;
            }

            string namelist = ResolveNamelist(caseDir, settings, out string namelistProblem);
            if (namelist == null)
            {
                result.Message = namelistProblem;
                return result;
            }

            string exe = ResolveExecutable(input.RootFolder, settings.ElmfireExe);
            //Not fatal yet: with output already present, the run is skipped and the executable is not needed
            //at all. ElmfireRunner makes that decision, so it is told what there is.
            if (exe == null && !settings.ReuseExistingOutput)
            {
                result.Message = "elmfire.exe was not found. Set [ELMFIRE] ElmfireExe, or put the build under "
                                 + "ThirdParty/elmfire/build/windows/bin.";
                return result;
            }

            //ELMFIRE finds GDAL itself when PATH_TO_GDAL is left at 'auto' - it runs "where gdal_translate" -
            //so the useful thing is to make sure the tools are on the PATH it inherits. Only when the scenario
            //names a directory is that used instead, and only then is the namelist key written.
            string gdalBin = NullIfEmpty(settings.PathToGdal) ?? GdalTools.FindBinDirectory();
            if (string.IsNullOrEmpty(settings.PathToGdal))
            {
                Log(gdalBin == null
                    ? "GDAL's command-line tools were not found. ELMFIRE shells out to gdal_translate, gdalinfo "
                      + "and gdalsrsinfo, and fails on its own DEM check without them - it reports \"DEM CRS does "
                      + "not appear to use metre linear units\". Install QGIS or OSGeo4W, or set [ELMFIRE] "
                      + "PathToGdal."
                    : "Found GDAL's tools in " + gdalBin + "; ELMFIRE will auto-detect them from there.");
            }

            var writer = new StringWriter();
            ElmfireRunner.Result run;
            try
            {
                run = ElmfireRunner.Run(exe, caseDir, "scenario", PatchNamelist(namelist, settings, Log),
                    settings.ReuseExistingOutput, writer, gdalBin);
            }
            catch (Exception e)
            {
                result.Message = "Running ELMFIRE threw: " + e.Message;
                return result;
            }
            finally
            {
                //ELMFIRE's own progress, forwarded rather than dropped: it is the only account of a step
                //that can take minutes, and of a fire that ran but burned nothing.
                foreach (string line in writer.ToString().Split('\n'))
                {
                    if (line.Trim().Length > 0) Log(line.TrimEnd());
                }
            }

            if (!run.Ok)
            {
                result.Message = run.Message;
                return result;
            }

            result.Reused = run.Reused;
            result.TimeOfArrivalFile = Relative(input.RootFolder, run.Toa);
            result.RateOfSpreadFile = Relative(input.RootFolder, run.Ros);
            result.SpreadDirectionFile = Relative(input.RootFolder, run.Sd);
            result.FirelineIntensityFile = Relative(input.RootFolder, run.Fi);

            //Spread direction is the one ELMFIRE only writes when asked (DUMP_SPREAD_DIRECTION), and the
            //reader needs it. Said with the key to set, because the namelist is the user's to edit.
            if (string.IsNullOrEmpty(result.SpreadDirectionFile))
            {
                result.Message = "ELMFIRE produced no spread direction raster. Add DUMP_SPREAD_DIRECTION = .TRUE. "
                                 + "to the &OUTPUTS group of " + Path.GetFileName(namelist) + " and run again.";
                return result;
            }

            //The wind the fire was actually computed with, for whatever else wants it - k-PERIL above all.
            //Named from the case's inputs folder rather than parsed out of the namelist's WS_FILENAME: the
            //stems are fixed by the contract every case is built to.
            string windSpeed = Path.Combine(caseDir, "inputs", "ws.tif");
            string windDirection = Path.Combine(caseDir, "inputs", "wd.tif");
            if (File.Exists(windSpeed) && File.Exists(windDirection))
            {
                result.WindSpeedFile = Relative(input.RootFolder, windSpeed);
                result.WindDirectionFile = Relative(input.RootFolder, windDirection);
            }

            result.Ok = true;
            return result;
        }

        /// <summary>
        /// The namelist with the few keys the scenario owns written into it, leaving the physics alone.
        ///
        /// <c>PATH_TO_GDAL</c> only when the scenario names a directory. Left alone otherwise, because
        /// ELMFIRE's own default for it is <c>'auto'</c> - it runs <c>where gdal_translate</c> and reports
        /// what it found - and writing the key would override that. What makes the auto-detection work is
        /// GDAL being on the PATH the process inherits, which the caller arranges.
        ///
        /// The stop time is written for a different reason: it is a property of what is being simulated, and
        /// the scenario is where that is set.
        /// </summary>
        private static string[] PatchNamelist(string namelistPath, ElmfireInput settings, Action<string> log)
        {
            string[] lines = File.ReadAllLines(namelistPath);

            if (!string.IsNullOrEmpty(settings.PathToGdal))
            {
                //Trailing separator, since ELMFIRE concatenates the tool name straight onto it.
                string gdal = settings.PathToGdal.Replace('\\', '/').TrimEnd('/') + "/";
                lines = ElmfireNamelist.SetKeyInGroup(lines, "MISCELLANEOUS", "PATH_TO_GDAL", gdal, true);
                log("Namelist PATH_TO_GDAL set to " + gdal);
            }

            if (settings.SimulationTstopSeconds > 0.0)
            {
                lines = ElmfireNamelist.SetKeyInGroup(lines, "TIME_CONTROL", "SIMULATION_TSTOP",
                    settings.SimulationTstopSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
            }

            //The dumps the reader needs, which are not the user's choice to make: without them ELMFIRE runs
            //perfectly well and produces a fire nothing here can read. Spread direction is the one usually
            //missing - it is off by default and a namelist written for a look at the fire in QGIS has no
            //reason to enable it - and its absence used to surface as "produced no time_of_arrival raster",
            //about the one raster that was definitely there.
            //
            //SPREAD_RATE_IN_M belongs with them: the reader takes the rate as m/min and ELMFIRE dumps ft/min
            //without it, which is not an error anywhere, just a fire spreading 3.28 times too fast.
            foreach ((string key, string value) in new[]
                     {
                         ("DUMP_TIME_OF_ARRIVAL", ".TRUE."),
                         ("DUMP_SPREAD_RATE", ".TRUE."),
                         ("DUMP_SPREAD_DIRECTION", ".TRUE."),
                         ("SPREAD_RATE_IN_M", ".TRUE."),
                     })
            {
                lines = ElmfireNamelist.SetKeyInGroup(lines, "OUTPUTS", key, value);
            }

            return lines;
        }

        /// <summary>
        /// Builds the case's rasters and namelist for the scenario's own domain, carrying the scenario's
        /// ignition points across so the fire starts where the scenario says.
        /// </summary>
        private static bool TryBuildCase(PREACTInput input, ElmfireInput settings, string caseDir,
            Action<string> log, out string problem)
        {
            problem = null;

            var options = new ElmfireCaseBuilder.Options
            {
                Name = input.Simulation.Name,
                LowerLeftLatLon = input.Simulation.LowerLeftLatLon,
                DomainSizeMetres = input.Simulation.DomainSize,
                CellSizeMetres = settings.CellSizeMetres,
                PaddingMetres = settings.PaddingMetres,
                SimulationTstopSeconds = settings.SimulationTstopSeconds,
                StartDateTime = input.Simulation.StartDateTime,
                OutputDirectory = caseDir,
                PathToGdal = NullIfEmpty(settings.PathToGdal),

                //Force is permission to write into a folder that is not empty, which a case folder never is.
                //It is not permission to replace what is in it - that is OverwriteExistingLayers, left off so
                //a prepared case keeps its rasters, its weather and its namelist. Conflating the two is what
                //made "build the case" mean "rebuild everything".
                Force = true,
                OverwriteExistingLayers = settings.RebuildExistingLayers,

                //Never from the scenario: a .wui is meant to be shared and a key in one is a key published.
                //Only needed when a DEM actually has to be downloaded, which a case that already has one
                //does not.
                OpenTopographyApiKey = OpenTopographyKey.Resolve(),

                Log = log,
            };

            //The same points the ignition editor placed, in WGS84. The builder measures them in the case's
            //own CRS once its grid exists, which is the one thing that cannot be done here.
            foreach (Wildfire.IgnitionPointInput point in input.WildfireModule.Data.IgnitionPoints)
            {
                options.IgnitionPoints.Add(new ElmfireCaseBuilder.IgnitionPoint
                {
                    LatLon = point.LatLon,
                    TimeSeconds = point.IgnitionTime,
                });
            }

            //Painted masks, when the scenario has them: the ignition area becomes the ignition mask and the
            //WUI area is written out for k-PERIL.
            if (!string.IsNullOrEmpty(input.WildfireModule.GraphicalFireInputFile))
            {
                options.PaintedMasksPath = Path.Combine(input.RootFolder, input.WildfireModule.GraphicalFireInputFile);
                string grid = input.Landscape?.GetReferenceFile();
                if (!string.IsNullOrEmpty(grid))
                {
                    options.PaintedMasksGridPath = Path.Combine(input.RootFolder, grid);
                }
            }

            try
            {
                //Waited on rather than awaited: module creation is synchronous, and a fire that has not been
                //computed cannot be evacuated from, so there is nothing useful to do meanwhile.
                ElmfireCaseBuilder.Build(options).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception e)
            {
                problem = "Could not build the ELMFIRE case: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// The namelist to run: the scenario's template when it names one, otherwise whatever the case holds.
        /// </summary>
        private static string ResolveNamelist(string caseDir, ElmfireInput settings, out string problem)
        {
            problem = null;

            if (!string.IsNullOrEmpty(settings.NamelistTemplate))
            {
                string named = Path.IsPathRooted(settings.NamelistTemplate)
                    ? settings.NamelistTemplate
                    : Path.Combine(caseDir, settings.NamelistTemplate);

                if (File.Exists(named))
                {
                    return named;
                }

                problem = "The namelist template named by the scenario is not there: " + settings.NamelistTemplate;
                return null;
            }

            string standard = Path.Combine(caseDir, "elmfire.data");
            if (File.Exists(standard))
            {
                return standard;
            }

            //Any single .data in the case folder, since a hand-prepared case is often named after the town.
            string[] candidates = Directory.GetFiles(caseDir, "*.data", SearchOption.TopDirectoryOnly);
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            problem = candidates.Length == 0
                ? $"No namelist in {caseDir}. Turn BuildCase on to write one, or put an elmfire.data there."
                : $"{caseDir} holds {candidates.Length} .data files; name the one to use in [ELMFIRE] NamelistTemplate.";
            return null;
        }

        /// <summary>
        /// The executable the scenario names, or the vendored build. Returns null when neither is there,
        /// which is only fatal if ELMFIRE actually has to run.
        /// </summary>
        private static string ResolveExecutable(string rootFolder, string named)
        {
            if (!string.IsNullOrEmpty(named))
            {
                string path = Path.IsPathRooted(named) ? named : Path.Combine(rootFolder, named);
                return File.Exists(path) ? path : null;
            }

            //Upwards from the running assembly, then from the working directory: the same build is reached
            //by different relative paths from the editor, a player build and the CLI.
            foreach (string start in new[] { AssemblyDirectory(), Directory.GetCurrentDirectory() })
            {
                string dir = start;
                for (int up = 0; up < 8 && !string.IsNullOrEmpty(dir); ++up, dir = Path.GetDirectoryName(dir))
                {
                    foreach (string relative in VendoredExeRelativePaths)
                    {
                        string candidate = Path.Combine(dir, Path.Combine(relative.Split('/')));
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                }
            }

            return null;
        }

        private static string AssemblyDirectory()
        {
            try
            {
                return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// A produced raster as a scenario-relative path, since that is what the .wui records and what the
        /// reader resolves. Absolute is kept when the case sits outside the scenario folder.
        /// </summary>
        private static string Relative(string rootFolder, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(rootFolder);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                root += Path.DirectorySeparatorChar;
            }

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }

            return full.Substring(root.Length).Replace('\\', '/');
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
