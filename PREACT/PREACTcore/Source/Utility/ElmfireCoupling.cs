//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Utility
{
    /// <summary>
    /// Gets a fire out of ELMFIRE for a scenario: builds the case if asked, runs <c>elmfire</c>, and reports where
    /// the rasters it produced are.
    ///
    /// This is the whole of the coupling. ELMFIRE computes a fire to completion and writes rasters, so there is
    /// nothing to step and nothing to interleave with the evacuation - once it has run, its output is a
    /// pre-computed fire, which is exactly what <see cref="Wildfire.AscFireImport"/> already reads. The module
    /// therefore runs this and then hands the paths to that reader rather than reimplementing it.
    ///
    /// Blocking, and deliberately so: a fire that has not finished computing cannot be evacuated from. Output
    /// computed from the same namelist and inputs is reused, so the cost falls on the first run of a case.
    /// Nothing here writes to the scenario during a run (contract C4); only <see cref="BuildCaseOnly"/>, which is
    /// a prepare step and not a run, records the case it built (contract C1).
    /// </summary>
    public static class ElmfireCoupling
    {
        public class Result
        {
            public bool Ok;

            /// <summary>The rasters, relative to the scenario root, as AscImport wants them.</summary>
            public string TimeOfArrivalFile, RateOfSpreadFile, SpreadDirectionFile, FirelineIntensityFile;

            /// <summary>
            /// Midflame wind speed (ft/min) on the cells the fire reached, relative to the scenario root; empty when
            /// the ELMFIRE build predates DUMP_MIDFLAME_WINDSPEED.
            /// </summary>
            public string MidflameWindSpeedFile = string.Empty;

            /// <summary>
            /// The historical moment the case's weather was written from, and the ERA5 record it came out of, so
            /// the simulation can report the same weather the fire was computed against. Both default when the
            /// case's weather was not drawn from a record.
            /// </summary>
            public DateTime WeatherAnchor;

            public string WeatherArchiveFile = string.Empty;

            /// <summary>
            /// The wind rasters the fire ran on - the run namelist's WEATHER_DIRECTORY with its WS/WD stems -
            /// relative to the scenario root, or empty when they cannot be found.
            /// </summary>
            public string WindSpeedFile = string.Empty, WindDirectionFile = string.Empty;

            /// <summary>The run's DT_METEOROLOGY and METEOROLOGY_BAND_START, for mapping arrival times onto bands.</summary>
            public double SecondsPerBand = 3600.0;
            public int StartBand = 1;

            /// <summary>The case's painted WUI area on the fire grid, relative to the root; empty when it has none.</summary>
            public string WuiAreaFile = string.Empty;

            /// <summary>The namelist ELMFIRE actually ran, <c>outputs/run.data</c>.</summary>
            public string RunNamelistFile = string.Empty;

            /// <summary>True when an earlier run's output was used and ELMFIRE was not invoked.</summary>
            public bool Reused;

            /// <summary>True when the run was stopped by <see cref="ElmfireRunner.CancelAll"/>.</summary>
            public bool Cancelled;

            public string Message = string.Empty;
        }

        /// <summary>
        /// Where the vendored ELMFIRE sits relative to the Unity project, for a scenario that does not name one.
        /// Probed upwards, because the working directory differs between the editor, a player build and the
        /// command line. The Linux build is found the same way, for the test bench.
        /// </summary>
        private static readonly string[] VendoredExeRelativePaths =
        {
            "WUInity/Assets/ThirdParty/elmfire/build/windows/bin/elmfire.exe",
            "Assets/ThirdParty/elmfire/build/windows/bin/elmfire.exe",
            "ThirdParty/elmfire/build/windows/bin/elmfire.exe",
            "WUInity/Assets/ThirdParty/elmfire/build/linux/bin/elmfire",
            "Assets/ThirdParty/elmfire/build/linux/bin/elmfire",
            "ThirdParty/elmfire/build/linux/bin/elmfire",
        };

        /// <summary>
        /// Builds the scenario's ELMFIRE case and stops there, without running the model.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The same build <see cref="Prepare"/> performs, exposed so it can be done once from the GUI's Build fire
        /// case rather than only as a side effect of starting a run. Ignores <see cref="ElmfireInput.BuildCase"/>: pressing the
        /// button is the instruction, and that flag governs whether a run builds on its own.
        /// </para>
        /// <para>
        /// Contract C1: on success the passed scenario is updated in place so that the case's <c>dem.tif</c> is the
        /// grid of record for everything else - <c>[Landscape] ElevationFile/SlopeFile/AspectFile</c> become the
        /// case's <c>dem/slp/asp.tif</c>, and <c>[kPERIL] WuiAreaFile</c> the case's <c>wui_area.tif</c> when the
        /// build wrote one. Paths relative to the scenario folder, with forward slashes. The weather anchor and
        /// archive are recorded too. The caller saves the scenario (and reloads the landscape it displays).
        /// </para>
        /// <para>
        /// A <see cref="ElmfireRunner.CancelAll"/> while it builds kills the WindNinja solve under way, starts no
        /// other, and makes this return false with a <paramref name="problem"/> that says the build was stopped and
        /// where; the scenario is left as it was. A stop that comes after the weather was written is too late to
        /// take: the rest of the build is quick and in-process, and leaving the namelist behind the rasters it counts
        /// would be worse, so the build finishes and says so.
        /// </para>
        /// </remarks>
        public static bool BuildCaseOnly(PREACTInput input, Action<string> log, out string problem)
        {
            problem = null;
            long generation = ElmfireProcesses.Generation;

            ElmfireInput settings = input?.WildfireModule?.ElmfireInput;
            if (settings == null)
            {
                problem = "The scenario has no ELMFIRE settings to build a case from.";
                return false;
            }

            string caseDir = CaseDirectoryPath(input.RootFolder, settings);

            if (!TryBuildCase(input, settings, caseDir, generation, log, out problem, out ElmfireCaseBuilder.Result built))
            {
                return false;
            }

            if (ElmfireProcesses.CancelledSince(generation))
            {
                log?.Invoke("  The stop came after the weather was written, so the build was finished: stopping then "
                            + "would have left elmfire.data behind the rasters it describes.");
            }

            string inputs = built.InputsDirectory;
            if (input.Landscape != null)
            {
                input.Landscape.ElevationFile = Relative(input.RootFolder, ElmfireStems.Tif(inputs, ElmfireStems.Dem));
                input.Landscape.SlopeFile = Relative(input.RootFolder, ElmfireStems.Tif(inputs, ElmfireStems.Slope));
                input.Landscape.AspectFile = Relative(input.RootFolder, ElmfireStems.Tif(inputs, ElmfireStems.Aspect));
                log?.Invoke($"  landscape: the scenario's elevation, slope and aspect are now the case's own "
                            + $"({input.Landscape.ElevationFile}), the grid everything is painted and computed on.");
            }

            if (!string.IsNullOrEmpty(built.WuiAreaFile) && input.TriggerBufferModule?.kPERILInput != null)
            {
                input.TriggerBufferModule.kPERILInput.WuiAreaFile = Relative(input.RootFolder, built.WuiAreaFile);
                log?.Invoke("  trigger boundary: [kPERIL] WuiAreaFile is now " + input.TriggerBufferModule.kPERILInput.WuiAreaFile + ".");
            }

            //Written onto the scenario, like every other data step writes the path it produced. Building from
            //the data steps happens long before any run, so without recording the day here the connection between
            //the fire's weather and the reported weather would have to be rediscovered - or lost.
            DateTime anchor = built.Weather != null ? built.Weather.BandAnchor : default;
            if (anchor != default && input.Weather != null)
            {
                input.Weather.WeatherAnchorDateTime = anchor;

                string archive = ElmfireCaseBuilder.ArchivePath(caseDir, input.Simulation.Name);
                if (File.Exists(archive))
                {
                    input.Weather.WeatherFile = Relative(input.RootFolder, archive);
                }

                log?.Invoke($"  weather: the scenario now reads its weather from {anchor:yyyy-MM-dd HH:mm}, the "
                    + "day the fire was computed against.");
            }

            log?.Invoke("  Save the scenario to keep these.");
            return true;
        }

        /// <summary>
        /// Builds the case when the scenario asks for it, then runs ELMFIRE on it (or reuses the output of an
        /// identical earlier run), for one simulation. Nothing is written into <paramref name="input"/>.
        /// </summary>
        /// <remarks>
        /// A <see cref="ElmfireRunner.CancelAll"/> at any point - while the case is still being built or the
        /// namelist written, not only while ELMFIRE runs - makes this return with <see cref="Result.Cancelled"/>.
        /// </remarks>
        public static Result Prepare(PREACTInput input, ElmfireInput settings, Action<string> log)
        {
            var result = new Result();
            void Log(string m) => log?.Invoke(m);
            long generation = ElmfireProcesses.Generation;

            string caseDir = CaseDirectoryPath(input.RootFolder, settings);

            //Checked before anything is built or run: a duration outside these bounds is a unit mistake, not a
            //long fire - the real campaign's 7 200 000 s was 2000 h typed where seconds were meant.
            if (settings.SimulationTstopHours > 0.0)
            {
                string hoursProblem = CampaignLayout.ValidateFireHours(settings.SimulationTstopHours);
                if (hoursProblem != null)
                {
                    result.Message = "[ELMFIRE] SimulationTstopHours: " + hoursProblem + ".";
                    return result;
                }
            }

            if (settings.BuildCase)
            {
                if (!TryBuildCase(input, settings, caseDir, generation, Log, out string buildProblem, out ElmfireCaseBuilder.Result built))
                {
                    result.Cancelled = ElmfireProcesses.CancelledSince(generation);
                    result.Message = buildProblem;
                    return result;
                }

                if (built.Weather != null) result.WeatherAnchor = built.Weather.BandAnchor;
            }
            else if (input.Weather != null && input.Weather.HasWeatherAnchor)
            {
                //Not built this run, so the anchor comes from what the last build saved into the scenario.
                result.WeatherAnchor = input.Weather.WeatherAnchorDateTime;
            }

            string archive = ElmfireCaseBuilder.ArchivePath(caseDir, input.Simulation.Name);
            if (File.Exists(archive))
            {
                result.WeatherArchiveFile = Relative(input.RootFolder, archive);
            }

            if (!Directory.Exists(caseDir))
            {
                result.Message = $"There is no ELMFIRE case at {settings.CaseDirectory}. Turn BuildCase on, or "
                                 + "build one with Data > Build fire case (ELMFIRE) in the GUI, or PREACTcli build-case.";
                return result;
            }

            string namelist = ResolveNamelist(caseDir, input.RootFolder, settings, Log, out string namelistProblem);
            if (namelist == null)
            {
                result.Message = namelistProblem;
                return result;
            }

            string exe = ResolveExecutable(input.RootFolder, settings.ElmfireExe);

            //ELMFIRE finds GDAL itself when PATH_TO_GDAL is left at 'auto', but on Windows getting a variable into a
            //child's environment is runtime-dependent, so the located directory is written into the namelist too.
            string gdalBin = NullIfEmpty(settings.PathToGdal) ?? GdalTools.FindBinDirectory();
            if (gdalBin == null)
            {
                Log("GDAL's command-line tools were not found. ELMFIRE shells out to gdal_translate, gdalinfo and "
                    + "gdalsrsinfo, and fails on its own DEM check without them - it reports \"DEM CRS does not "
                    + "appear to use metre linear units\". Install QGIS or OSGeo4W, or set [ELMFIRE] PathToGdal.");
            }

            string[] runLines = PatchNamelist(namelist, caseDir, settings, gdalBin, exe, Log);

            //Checked on the namelist as it will run - its band keys fitted to the case's weather, its stop time the
            //scenario's - so what is compared is what ELMFIRE will ask for. A case whose weather is shorter than the
            //fire is refused by ELMFIRE ("Not enough weather bands for given SIMULATION TSTOP"), which names neither
            //the case nor the numbers. A generated case can simply be given more weather, so it is - the builder keeps
            //every other layer. A template's case is the user's to rebuild.
            string weatherProblem = DescribeWeatherShortfall(runLines, caseDir);
            if (weatherProblem != null)
            {
                if (!string.IsNullOrEmpty(settings.NamelistTemplate))
                {
                    result.Message = $"The case's weather cannot run this fire: {weatherProblem}. The scenario runs its own "
                                     + $"NamelistTemplate ({Path.GetFileName(namelist)}) on the case's weather, so lower [ELMFIRE] "
                                     + "SimulationTstopHours to what the weather covers, or build the case again for the hours "
                                     + "the fire needs (Data > Build fire case (ELMFIRE), or PREACTcli build-case --hours), which "
                                     + "writes that much weather.";
                    return result;
                }

                Log("The case's weather is too short for this fire (" + weatherProblem + "); extending it - one "
                    + "WindNinja solve per hour of fire.");
                if (!TryBuildCase(input, settings, caseDir, generation, Log, out string extendProblem, out ElmfireCaseBuilder.Result extended))
                {
                    result.Cancelled = ElmfireProcesses.CancelledSince(generation);
                    result.Message = "Could not extend the case's weather: " + extendProblem;
                    return result;
                }
                if (extended.Weather != null) result.WeatherAnchor = extended.Weather.BandAnchor;
                namelist = ResolveNamelist(caseDir, input.RootFolder, settings, Log, out namelistProblem) ?? namelist;
                runLines = PatchNamelist(namelist, caseDir, settings, gdalBin, exe, Log);

                weatherProblem = DescribeWeatherShortfall(runLines, caseDir);
                if (weatherProblem != null)
                {
                    result.Message = "The case's weather still cannot run this fire after it was extended: " + weatherProblem + ".";
                    return result;
                }
            }

            //Every raster the namelist names, on the grid of the DEM it names, before ELMFIRE is asked: it compares
            //nothing itself, and a fuel raster left on an old grid ends in a segfault or "raster dimensions
            //mismatch" that names neither the file nor the grid.
            ElmfireCaseValidator.Report rasters = ElmfireCaseValidator.ValidateNamelistRasters(runLines, caseDir,
                includeWeather: true);
            if (!rasters.Ok)
            {
                result.Message = $"The namelist {Path.GetFileName(namelist)} names rasters that are not on the case grid, "
                                 + "so ELMFIRE cannot run it: " + ElmfireCaseValidator.Summarize(rasters).TrimEnd('.')
                                 + ". Build the case again: it re-cuts onto the grid every raster named by the case's "
                                 + "elmfire.data, its kept namelists (elmfire.data.kept-*) and the NamelistTemplate. Or "
                                 + "point the namelist at rasters on the grid.";
                return result;
            }

            string fingerprint = ElmfireFingerprint.ForRun(runLines, caseDir, exe);

            string missingExe = exe == null ? DescribeMissingExecutable(input.RootFolder, settings.ElmfireExe) : null;
            if (exe == null && !settings.ReuseExistingOutput)
            {
                result.Message = "ELMFIRE cannot run: " + missingExe;
                return result;
            }

            //A stop while the case was built or the inputs hashed: ELMFIRE has not started, so nothing was killed.
            if (ElmfireProcesses.CancelledSince(generation))
            {
                result.Cancelled = true;
                result.Message = "stopped before ELMFIRE started.";
                return result;
            }

            var writer = new StringWriter();
            ElmfireRunner.Result run;
            try
            {
                run = ElmfireRunner.Run(exe, caseDir, "scenario", runLines, settings.ReuseExistingOutput, writer,
                    gdalBin, fingerprint);
            }
            catch (Exception e)
            {
                result.Message = "Running ELMFIRE threw: " + e.Message;
                return result;
            }
            finally
            {
                //ELMFIRE's own progress, forwarded rather than dropped: it is the only account of a step that can
                //take minutes, and of a fire that ran but burned nothing.
                foreach (string line in writer.ToString().Split('\n'))
                {
                    if (line.Trim().Length > 0) Log(line.TrimEnd());
                }
            }

            if (!run.Ok)
            {
                result.Cancelled = run.Cancelled;
                result.Message = exe == null
                    ? "ELMFIRE cannot run: " + missingExe + " There were no outputs of an identical earlier run to reuse."
                    : run.Message;
                return result;
            }

            result.Reused = run.Reused;
            result.RunNamelistFile = Relative(input.RootFolder, run.NamelistPath);
            result.TimeOfArrivalFile = Relative(input.RootFolder, run.Toa);
            result.RateOfSpreadFile = Relative(input.RootFolder, run.Ros);
            result.SpreadDirectionFile = Relative(input.RootFolder, run.Sd);
            result.FirelineIntensityFile = Relative(input.RootFolder, run.Fi);
            result.MidflameWindSpeedFile = Relative(input.RootFolder, run.Mfws);

            //The weather the fire actually ran on, read from the namelist it ran rather than assumed to be
            //inputs/ws.tif: a template can put it anywhere, and k-PERIL must see the same field.
            string inputs = ElmfireStems.ResolveDirectory(runLines, ElmfireNamelistKeys.InputsGroup,
                                ElmfireNamelistKeys.FuelsAndTopographyDirectory, caseDir) ?? Path.Combine(caseDir, "inputs");
            string weatherDir = ElmfireStems.ResolveDirectory(runLines, ElmfireNamelistKeys.InputsGroup,
                                    ElmfireNamelistKeys.WeatherDirectory, caseDir) ?? inputs;
            string windSpeed = ElmfireStems.Tif(weatherDir, StemOr(runLines, ElmfireNamelistKeys.WsFilename, ElmfireStems.WindSpeed));
            string windDirection = ElmfireStems.Tif(weatherDir, StemOr(runLines, ElmfireNamelistKeys.WdFilename, ElmfireStems.WindDirection));
            if (File.Exists(windSpeed) && File.Exists(windDirection))
            {
                result.WindSpeedFile = Relative(input.RootFolder, windSpeed);
                result.WindDirectionFile = Relative(input.RootFolder, windDirection);
            }

            result.SecondsPerBand = ReadDouble(runLines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.DtMeteorology, 3600.0);
            result.StartBand = (int)ReadDouble(runLines, ElmfireNamelistKeys.MonteCarloGroup, ElmfireNamelistKeys.MeteorologyBandStart, 1.0);
            if (result.StartBand < 1) result.StartBand = 1;

            string wuiArea = ElmfireStems.Tif(inputs, ElmfireStems.WuiArea);
            if (File.Exists(wuiArea))
            {
                result.WuiAreaFile = Relative(input.RootFolder, wuiArea);
            }

            result.Ok = true;
            return result;
        }

        private static string StemOr(string[] lines, string key, string fallback)
        {
            string stem = ElmfireNamelist.GetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup, key);
            return string.IsNullOrWhiteSpace(stem) ? fallback : stem;
        }

        private static double ReadDouble(string[] lines, string group, string key, double fallback)
        {
            string value = ElmfireNamelist.GetKeyInGroup(lines, group, key);
            return !string.IsNullOrEmpty(value)
                   && double.TryParse(value.Replace('d', 'e').Replace('D', 'E'), NumberStyles.Float,
                       CultureInfo.InvariantCulture, out double parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        /// <summary>
        /// Why the weather the patched namelist <paramref name="runLines"/> reads cannot carry its fire, naming the
        /// bands it needs and the bands there are; null when it can, or when that cannot be told (no weather folder -
        /// the raster check reports the rasters).
        /// </summary>
        /// <remarks>
        /// The five standard rasters are checked for presence and agreement first, as a build keeps them, so a case
        /// that lost its weather is rebuilt rather than run; then the namelist's own demand against its wind speed
        /// raster (<see cref="ElmfireNamelist.DescribeBandShortfall"/>): the stop time, and the band keys, which
        /// <see cref="PatchNamelist"/> has fitted to that raster - so what is left is a fire longer than the weather.
        /// </remarks>
        private static string DescribeWeatherShortfall(string[] runLines, string caseDir)
        {
            string weatherDir = ElmfireStems.ResolveDirectory(runLines, ElmfireNamelistKeys.InputsGroup,
                                    ElmfireNamelistKeys.WeatherDirectory, caseDir);
            if (weatherDir == null || !Directory.Exists(weatherDir)) return null;

            double tstop = ReadDouble(runLines, ElmfireNamelistKeys.TimeControlGroup, ElmfireNamelistKeys.SimulationTstop, 0.0);
            double dt = ReadDouble(runLines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.DtMeteorology, 3600.0);
            bool standardStems = string.Equals(StemOr(runLines, ElmfireNamelistKeys.WsFilename, ElmfireStems.WindSpeed),
                                     ElmfireStems.WindSpeed, StringComparison.OrdinalIgnoreCase);
            string kept = standardStems ? ElmfireCaseBuilder.DescribeKeptWeather(weatherDir, tstop, dt) : null;
            if (kept != null) return kept;

            int bands = ElmfireNamelist.WeatherBandCount(runLines, caseDir, out string windSpeed);
            return ElmfireNamelist.DescribeBandShortfall(runLines, bands, Path.GetFileName(windSpeed));
        }

        /// <summary>
        /// The namelist with the few keys the scenario and the platform own written into it, leaving the physics
        /// alone: PATH_TO_GDAL, the stop time, the weather band keys, the outputs every run must dump, and the fuel
        /// model table.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The stop time is a property of what is being simulated, and the scenario is where that is set - in
        /// hours; this is the one place it becomes seconds.
        /// </para>
        /// <para>
        /// The band keys are a property of the weather the case holds, which a template or a kept namelist knows
        /// nothing about: they are fitted to the case's <c>ws.tif</c> exactly as a campaign fits each realization's
        /// (<see cref="ElmfireNamelist.FitWeatherBands"/>), and what changed is logged.
        /// </para>
        /// <para>
        /// FUEL_MODEL_FILE is named whenever the table exists (and made to exist from ELMFIRE's own default when
        /// it does not): with it unset ELMFIRE rewrites <c>fuel_models.csv</c> in the case's inputs on every run,
        /// which replaced a hand-edited table and, in a campaign, raced between concurrent realizations.
        /// </para>
        /// </remarks>
        private static string[] PatchNamelist(string namelistPath, string caseDir, ElmfireInput settings,
            string gdalBin, string exe, Action<string> log)
        {
            string[] lines = File.ReadAllLines(namelistPath);

            if (!string.IsNullOrEmpty(gdalBin))
            {
                //No trailing separator: ELMFIRE appends PATH_SEPARATOR itself once it has a directory.
                string gdal = gdalBin.TrimEnd('/', '\\');
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MiscellaneousGroup,
                            ElmfireNamelistKeys.PathToGdal, gdal, true);
                log("Namelist PATH_TO_GDAL set to " + gdal);
            }

            if (settings.SimulationTstopHours > 0.0)
            {
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.TimeControlGroup,
                    ElmfireNamelistKeys.SimulationTstop,
                    CampaignLayout.TstopSeconds(settings.SimulationTstopHours).ToString("0.0", CultureInfo.InvariantCulture));
            }

            int bands = ElmfireNamelist.WeatherBandCount(lines, caseDir, out string windSpeed);
            string[] unfitted = lines;
            lines = ElmfireNamelist.FitWeatherBands(lines, bands);
            List<string> fitted = ElmfireNamelist.DescribeDifferences(unfitted, lines);
            if (fitted.Count > 0)
            {
                log($"Namelist weather bands fitted to {Path.GetFileName(windSpeed)} ({bands} band(s)): " + string.Join("; ", fitted));
            }

            lines = ElmfireNamelistKeys.ForceRequiredOutputs(lines);

            string fuelTable = ElmfireNamelist.GetKeyInGroup(lines, ElmfireNamelistKeys.MiscellaneousGroup,
                                   ElmfireNamelistKeys.FuelModelFile);
            if (string.IsNullOrWhiteSpace(fuelTable) || fuelTable.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                string misc = ElmfireStems.ResolveDirectory(lines, ElmfireNamelistKeys.MiscellaneousGroup,
                                  ElmfireNamelistKeys.MiscellaneousInputsDirectory, caseDir)
                              ?? ElmfireStems.ResolveDirectory(lines, ElmfireNamelistKeys.InputsGroup,
                                  ElmfireNamelistKeys.FuelsAndTopographyDirectory, caseDir);

                if (misc != null && ElmfireCaseBuilder.EnsureFuelModelTable(misc, exe, log))
                {
                    lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MiscellaneousGroup,
                                ElmfireNamelistKeys.FuelModelFile, ElmfireStems.FuelModelTable, quoted: true);
                    lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MiscellaneousGroup,
                                ElmfireNamelistKeys.MiscellaneousInputsDirectory, ElmfireStems.ForNamelist(caseDir, misc),
                                quoted: true);
                }
            }

            return lines;
        }

        /// <summary>
        /// The case builder's options for this scenario: its domain, grid, stop time, source layers, ignition
        /// points and painted areas, as the GUI's Build fire case and <c>PREACTcli build-case</c> both build them.
        /// </summary>
        /// <remarks>
        /// One place, so the two front ends cannot build different cases from the same <c>.wui</c> - the CLI used to
        /// read the scenario with its own parser and ignored the stop time, the cell size, the padding and the
        /// painted areas, so a CLI-built case had no <c>wui_area.tif</c> and the wrong band count.
        /// </remarks>
        public static ElmfireCaseBuilder.Options CreateBuildOptions(PREACTInput input, ElmfireInput settings,
            string caseDir, Action<string> log)
        {
            var options = new ElmfireCaseBuilder.Options
            {
                Name = input.Simulation.Name,
                LowerLeftLatLon = input.Simulation.LowerLeftLatLon,
                DomainSizeMetres = input.Simulation.DomainSize,
                CellSizeMetres = settings.CellSizeMetres,
                PaddingMetres = settings.PaddingMetres,
                SimulationTstopSeconds = settings.TstopSeconds(),
                StartDateTime = input.Simulation.StartDateTime,
                OutputDirectory = caseDir,
                PathToGdal = NullIfEmpty(settings.PathToGdal),
                Namelist = settings.Namelist,
                ElmfireExe = ResolveExecutable(input.RootFolder, settings.ElmfireExe),

                //Force is permission to write into a folder that is not empty, which a case folder never is. It
                //is not permission to replace what is in it - that is OverwriteExistingLayers.
                Force = true,
                OverwriteExistingLayers = settings.RebuildExistingLayers,

                //Never from the scenario: a .wui is meant to be shared and a key in one is a key published.
                OpenTopographyApiKey = OpenTopographyKey.Resolve(),

                Log = log,

                //Absolute, because the builder hands these straight to gdalwarp.
                CanopyDatasetFolder = ResolveFolder(input.RootFolder, settings.CanopyDatasetFolder),
            };

            options.Weather.WindNinjaExe = NullIfEmpty(settings.WindNinjaExe);

            if (!string.IsNullOrEmpty(settings.NamelistTemplate))
            {
                string template = ResolveNamelist(caseDir, input.RootFolder, settings, null, out string _);
                if (template != null) options.TemplateNamelistPath = template;
            }

            //Fuel, canopy and buildings, which have no global source and so have to be named. Resolved against the
            //scenario folder, so the .wui stays portable. A path that does not resolve is dropped: the scenario
            //parse has already said so once with the key name.
            foreach (KeyValuePair<string, string> layer in settings.GetSourceRasters())
            {
                string path = PREACTInput.ResolvePath(input.RootFolder, layer.Value);
                if (File.Exists(path)) options.UserRasters[layer.Key] = Path.GetFullPath(path);
            }

            //The same points the ignition editor placed, in WGS84. The builder measures them in the case's own CRS
            //once its grid exists.
            foreach (Wildfire.IgnitionPointInput point in input.WildfireModule.Data.IgnitionPoints)
            {
                options.IgnitionPoints.Add(new ElmfireCaseBuilder.IgnitionPoint
                {
                    LatLon = point.LatLon,
                    TimeSeconds = point.IgnitionTime,
                });
            }

            //The scenario's own DEM, when it has one: used for the grid if it covers the padded domain, so the
            //case needs no second download. Not the case's own dem.tif, which is the grid being checked.
            string landscapeDem = input.Landscape == null ? null : ResolveFile(input.RootFolder, input.Landscape.ElevationFile);
            string caseDem = ElmfireStems.Tif(Path.Combine(caseDir, "inputs"), ElmfireStems.Dem);
            if (landscapeDem != null && !string.Equals(Path.GetFullPath(landscapeDem), Path.GetFullPath(caseDem),
                    StringComparison.OrdinalIgnoreCase))
            {
                options.CandidateDemPaths.Add(landscapeDem);
            }

            //Painted masks, when the scenario has them: the ignition area becomes the ignition mask and the WUI
            //area is written out for k-PERIL. The landscape raster is only the legacy grid for a painting made
            //before the case existed (contract C2); the builder checks the case grid first.
            if (!string.IsNullOrEmpty(input.WildfireModule.GraphicalFireInputFile))
            {
                options.PaintedMasksPath = PREACTInput.ResolvePath(input.RootFolder, input.WildfireModule.GraphicalFireInputFile);
                string grid = input.Landscape?.GetReferenceFile();
                if (!string.IsNullOrEmpty(grid))
                {
                    options.PaintedMasksGridPath = PREACTInput.ResolvePath(input.RootFolder, grid);
                }
            }

            return options;
        }

        /// <summary>
        /// Builds the case's rasters and namelist for the scenario's own domain. A <see cref="ElmfireRunner.CancelAll"/>
        /// after <paramref name="generation"/> stops it at the builder's next safe point.
        /// </summary>
        private static bool TryBuildCase(PREACTInput input, ElmfireInput settings, string caseDir, long generation,
            Action<string> log, out string problem, out ElmfireCaseBuilder.Result built)
        {
            problem = null;
            built = null;

            string hoursProblem = CampaignLayout.ValidateFireHours(settings.SimulationTstopHours);
            if (hoursProblem != null)
            {
                problem = "[ELMFIRE] SimulationTstopHours: " + hoursProblem + ".";
                return false;
            }

            string campaign = CampaignLayout.DescribeRunningCampaign(input.RootFolder);
            if (campaign != null)
            {
                problem = "The case was not built: " + campaign;
                return false;
            }

            try
            {
                ElmfireCaseBuilder.Options options = CreateBuildOptions(input, settings, caseDir, log);
                options.Cancelled = () => ElmfireProcesses.CancelledSince(generation);

                //Waited on rather than awaited: module creation is synchronous, and a fire that has not been
                //computed cannot be evacuated from, so there is nothing useful to do meanwhile.
                built = ElmfireCaseBuilder.Build(options).GetAwaiter().GetResult();

                //Refused here rather than in the builder: a case with a misregistered layer is still worth having
                //on disk to inspect, but running it is not - ELMFIRE reads rasters cell-for-cell without comparing
                //their geotransforms.
                if (built.Validation != null && !built.Validation.Ok)
                {
                    problem = "The ELMFIRE case is not internally consistent, so it was not run: "
                              + ElmfireCaseValidator.Summarize(built.Validation);
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException e)
            {
                //The builder's own words: where it stopped, and what the next build does about it.
                problem = e.Message;
                log?.Invoke(problem);
                return false;
            }
            catch (Exception e)
            {
                problem = ElmfireProcesses.CancelledSince(generation)
                    ? "The case build was stopped, and ended with: " + e.Message
                    : "Could not build the ELMFIRE case: " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// The namelist to run: the scenario's template when it names one, otherwise whatever the case holds.
        /// </summary>
        /// <remarks>
        /// A relative <c>NamelistTemplate</c> is tried against the case directory and then the scenario folder,
        /// because both are reasonable readings and the GUI produces the second.
        /// </remarks>
        public static string ResolveNamelist(string caseDir, string rootFolder, ElmfireInput settings,
            Action<string> log, out string problem)
        {
            problem = null;

            if (!string.IsNullOrEmpty(settings.NamelistTemplate))
            {
                //Either slash, like every other path in a scenario: a template named on Windows as
                //"templates\mati.data" is one file name with a backslash in it anywhere else.
                string template = PREACTInput.NormalisePath(settings.NamelistTemplate);
                if (Path.IsPathRooted(template))
                {
                    if (File.Exists(template)) return template;

                    problem = "The namelist template named by the scenario is not there: " + settings.NamelistTemplate;
                    return null;
                }

                string inCase = Path.Combine(caseDir, template);
                if (File.Exists(inCase)) return inCase;

                string inRoot = PREACTInput.ResolvePath(rootFolder, template);
                if (File.Exists(inRoot))
                {
                    log?.Invoke($"Namelist template {settings.NamelistTemplate} resolved against the scenario "
                                + $"folder: {inRoot}");
                    return inRoot;
                }

                problem = $"The namelist template named by the scenario is not there: "
                          + $"{settings.NamelistTemplate}. Looked in the case directory ({inCase}) and beside "
                          + $"the scenario ({inRoot}).";
                return null;
            }

            string standard = Path.Combine(caseDir, "elmfire.data");
            if (File.Exists(standard))
            {
                return standard;
            }

            //Any single .data in the case folder, since a hand-prepared case is often named after the town.
            string[] candidates = Directory.Exists(caseDir)
                ? Directory.GetFiles(caseDir, "*.data", SearchOption.TopDirectoryOnly)
                : new string[0];
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
        /// The executable the scenario names, or the vendored build. Returns null when neither is there, which is
        /// only fatal if ELMFIRE actually has to run. Public because the trigger campaign needs the same answer.
        /// </summary>
        public static string ResolveExecutable(string rootFolder, string named)
        {
            if (!string.IsNullOrEmpty(named))
            {
                string path = string.IsNullOrEmpty(rootFolder) ? PREACTInput.NormalisePath(named) : PREACTInput.ResolvePath(rootFolder, named);
                return File.Exists(path) ? path : null;
            }

            //The executable for this platform first: a checkout carries both builds.
            bool windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows);

            //Upwards from the running assembly, then from the working directory: the same build is reached by
            //different relative paths from the editor, a player build and the CLI.
            foreach (string start in new[] { AssemblyDirectory(), Directory.GetCurrentDirectory() })
            {
                string dir = start;
                for (int up = 0; up < 8 && !string.IsNullOrEmpty(dir); ++up, dir = Path.GetDirectoryName(dir))
                {
                    foreach (string relative in VendoredExeRelativePaths)
                    {
                        if (relative.EndsWith(".exe", StringComparison.Ordinal) != windows) continue;

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

        /// <summary>
        /// Why <see cref="ResolveExecutable"/> found nothing, naming what it tried: the path the scenario's
        /// <c>[ELMFIRE] ElmfireExe</c> resolves to, or the vendored build it looked for.
        /// </summary>
        public static string DescribeMissingExecutable(string rootFolder, string named)
        {
            bool windows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows);
            if (!string.IsNullOrWhiteSpace(named))
            {
                string path = string.IsNullOrEmpty(rootFolder) ? PREACTInput.NormalisePath(named) : PREACTInput.ResolvePath(rootFolder, named);
                return $"[ELMFIRE] ElmfireExe names {path}, which is not there. Correct the path in the scenario (Hazards "
                       + "tab), or clear it to use the build in WUInity/Assets/ThirdParty/elmfire.";
            }

            string relative = windows ? "ThirdParty/elmfire/build/windows/bin/elmfire.exe" : "ThirdParty/elmfire/build/linux/bin/elmfire";
            return $"[ELMFIRE] ElmfireExe is not set and there is no {relative} above {AssemblyDirectory() ?? "this program"} "
                   + $"or {Directory.GetCurrentDirectory()}. Set ElmfireExe to the executable, or build ELMFIRE there.";
        }

        /// <summary>
        /// The scenario's <c>[ELMFIRE] CaseDirectory</c> as a full path: relative to the scenario folder, either
        /// slash. One rule for the GUI's build, a run, build-case and a campaign - a Windows-written
        /// <c>cases\mati</c> used to be one folder name with a backslash in it off Windows.
        /// </summary>
        public static string CaseDirectoryPath(string rootFolder, ElmfireInput settings)
        {
            string named = string.IsNullOrWhiteSpace(settings?.CaseDirectory) ? "elmfire" : settings.CaseDirectory;
            return ResolveFolder(rootFolder, named);
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
        /// A produced raster as a scenario-relative path with forward slashes, since that is what the .wui records
        /// and what the reader resolves. Absolute is kept when the file sits outside the scenario folder.
        /// </summary>
        private static string Relative(string rootFolder, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(string.IsNullOrEmpty(rootFolder) ? "." : rootFolder);
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

        /// <summary>A scenario-relative file made absolute, or null when unset or missing.</summary>
        private static string ResolveFile(string root, string file)
        {
            if (string.IsNullOrWhiteSpace(file)) return null;
            try
            {
                string full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(root, file.Replace('\\', '/')));
                return File.Exists(full) ? full : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// A scenario-relative folder made absolute, or null when unset. Returned even when the folder does not
        /// exist, so the build can report what was named.
        /// </summary>
        private static string ResolveFolder(string root, string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;

            try
            {
                string normalised = PREACTInput.NormalisePath(folder);
                string full = Path.IsPathRooted(normalised) || string.IsNullOrEmpty(root) ? normalised : Path.Combine(root, normalised);
                return Path.GetFullPath(full);
            }
            catch
            {
                return null;
            }
        }

        private static string NullIfEmpty(string value)
        {
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}
