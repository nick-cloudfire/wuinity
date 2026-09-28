using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PREACT.Input;
using PREACT.Math;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>
    /// Resolves and checks everything a campaign depends on before the first realization, so a campaign that
    /// cannot work fails in its first second with the reason, instead of failing identically hundreds of times.
    /// </summary>
    internal static class CampaignSetup
    {
        /// <summary>
        /// The scenario, the case, the tools and the campaign's identity. Cheap: reads no rasters beyond the masks
        /// and never touches the network, so <c>--inspect</c> can run it. Null after printing why, on failure.
        /// </summary>
        public static Campaign Resolve(CampaignOptions o)
        {
            var c = new Campaign { Options = o };
            bool running = !o.Inspect && !o.ResumeOnly;

            c.BaseWuiPath = Path.GetFullPath(o.BaseWui);
            if (!File.Exists(c.BaseWuiPath)) return Fail("the base scenario is not there: " + c.BaseWuiPath);

            c.ScenarioDir = Path.GetDirectoryName(c.BaseWuiPath);
            c.BaseLines = File.ReadAllLines(c.BaseWuiPath);
            c.BaseFileName = Path.GetFileNameWithoutExtension(c.BaseWuiPath);

            //Through the real parser, so the campaign reads the same keys the GUI and PREACT.exe do. It does not
            //have to be complete - the SUMO network or the population may be missing on this machine - but the
            //sections the campaign reads must have been read.
            PREACTInput input = PREACTInput.LoadFromDisk(c.BaseWuiPath, out bool _);
            if (input?.Simulation == null || input.WildfireModule?.ElmfireInput == null)
            {
                string why = PREACTInput.Requirements.Where(r => r.Critical).Select(r => r + ": " + r.Message).FirstOrDefault();
                return Fail("could not read the base scenario's [Simulation] and [ELMFIRE] sections"
                            + (why != null ? " (" + why + ")" : "") + ".");
            }

            c.ScenarioName = string.IsNullOrWhiteSpace(input.Simulation.Name) ? c.BaseFileName : input.Simulation.Name.Trim();
            c.StartDateTime = input.Simulation.StartDateTime;
            c.CentreLatLon = CentreOf(input.Simulation.LowerLeftLatLon, input.Simulation.DomainSize);

            if (!input.TriggerBufferModule.Enabled
                || input.TriggerBufferModule.Module != TriggerBufferModuleInput.TriggerBufferModules.kPERIL)
            {
                return Fail("the base scenario has no k-PERIL trigger boundary ([TriggerBufferModule] Enabled=true, "
                            + "Module=kPERIL), so its realizations would produce nothing to aggregate.");
            }

            c.WuiAreaSource = input.TriggerBufferModule.kPERILInput.WuiAreaSource.ToString();
            if (input.TriggerBufferModule.kPERILInput.WuiAreaSource == kPERILInput.WuiAreaSources.EvacuationGroupsSeparate)
            {
                //One boundary per group is written, each with the group's name appended, and the campaign aggregates
                //one boundary per realization. Per-group aggregation would be a feature, not a filename fix.
                return Fail("[kPERIL] WuiAreaSource=EvacuationGroupsSeparate writes one trigger boundary per evacuation "
                            + "group, and a campaign aggregates one per realization. Use Raster or "
                            + "EvacuationGroupsCombined, and run the per-group boundaries as single simulations.");
            }

            // ------------------------------------------------------------ the case
            ElmfireInput elmfire = input.WildfireModule.ElmfireInput;
            string caseDir = Path.GetFullPath(Path.Combine(c.ScenarioDir, elmfire.CaseDirectory));

            if (!string.IsNullOrEmpty(o.ElmfireTemplate))
            {
                c.TemplatePath = Path.GetFullPath(o.ElmfireTemplate);
                if (!File.Exists(c.TemplatePath)) return Fail("--elmfire-template is not there: " + c.TemplatePath);
            }
            else
            {
                c.TemplatePath = ElmfireCoupling.ResolveNamelist(caseDir, c.ScenarioDir, elmfire, null, out string problem);
                if (c.TemplatePath == null) return Fail(problem + " Build the case first (Prepare data, or PREACTcli build-case).");
                c.TemplatePath = Path.GetFullPath(c.TemplatePath);
            }

            c.TemplateLines = File.ReadAllLines(c.TemplatePath);

            //Relative directories in the template mean what they mean where the template runs: the case directory.
            string runRoot = Path.GetDirectoryName(c.TemplatePath);
            c.InputsDir = !string.IsNullOrEmpty(o.ElmfireInputs)
                ? Path.GetFullPath(o.ElmfireInputs)
                : ElmfireStems.ResolveDirectory(c.TemplateLines, ElmfireNamelistKeys.InputsGroup,
                      ElmfireNamelistKeys.FuelsAndTopographyDirectory, runRoot) ?? Path.Combine(caseDir, "inputs");
            c.InputsDir = c.InputsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(c.InputsDir)) return Fail("the case's inputs folder is not there: " + c.InputsDir);

            c.ElmfireExe = !string.IsNullOrEmpty(o.ElmfireExe)
                ? Path.GetFullPath(o.ElmfireExe)
                : ElmfireCoupling.ResolveExecutable(c.ScenarioDir, elmfire.ElmfireExe);
            if (running && (c.ElmfireExe == null || !File.Exists(c.ElmfireExe)))
            {
                return Fail("the ELMFIRE executable was not found; pass --elmfire <path> or set [ELMFIRE] ElmfireExe.");
            }

            c.GdalBin = o.PathToGdal
                        ?? (string.IsNullOrWhiteSpace(elmfire.PathToGdal) ? null : Absolute(c.ScenarioDir, elmfire.PathToGdal))
                        ?? GdalTools.FindBinDirectory();
            if (running && (c.GdalBin == null || !Directory.Exists(c.GdalBin)))
            {
                //Refused rather than warned about: without the tools ELMFIRE writes no rasters and still exits 0.
                return Fail("GDAL's command-line tools were not found; pass --gdal <bin folder> or set [ELMFIRE] PathToGdal.");
            }

            c.PreactExe = o.PreactExe != null ? Path.GetFullPath(o.PreactExe) : RealizationRunner.FindPreactExe();
            if (running && (c.PreactExe == null || !File.Exists(c.PreactExe)))
            {
                return Fail("PREACT.exe was not found; pass --preact <path>. (--resume-only aggregates a campaign's "
                            + "finished realizations without it.)");
            }

            // ------------------------------------------------------------ the template
            c.SecondsPerBand = ReadNumber(c.TemplateLines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.DtMeteorology, 3600.0);
            c.EdgeBufferMetres = ReadNumber(c.TemplateLines, ElmfireNamelistKeys.MonteCarloGroup, "EDGEBUFFER", 60.0);
            c.FuelStem = ElmfireStems.FuelStem(c.TemplateLines, c.InputsDir);
            if (c.FuelStem == null)
            {
                return Fail("the template names no fuel model raster that exists in " + c.InputsDir
                            + " (FBFM_FILENAME), so ELMFIRE could not start.");
            }

            string maskStem = ElmfireNamelist.GetKeyInGroup(c.TemplateLines, ElmfireNamelistKeys.InputsGroup,
                                  ElmfireNamelistKeys.IgnitionMaskFilename);
            if (!string.IsNullOrWhiteSpace(maskStem)) c.IgnitionMaskStem = maskStem;
            if (!CheckIgnitionMask(c)) return null;

            // ------------------------------------------------------------ the WUI area
            string wuiArea = ElmfireStems.Tif(c.InputsDir, ElmfireStems.WuiArea);
            if (MaskIgnitionSampler.TryGetMaskCentroid(wuiArea, out double wx, out double wy, out int cells))
            {
                c.WuiAreaFile = wuiArea;
                c.WuiCentreX = wx;
                c.WuiCentreY = wy;
                c.WuiCells = cells;
            }

            bool rasterWui = input.TriggerBufferModule.kPERILInput.WuiAreaSource == kPERILInput.WuiAreaSources.Raster;
            if (c.WuiAreaFile == null && (o.WindToWui || rasterWui))
            {
                return Fail((o.WindToWui ? "aiming the wind at the WUI area" : "the trigger boundary")
                            + " needs the case's wui_area.tif, and " + wuiArea
                            + (File.Exists(wuiArea) ? " has no marked cell." : " is not there.")
                            + " Paint a WUI area in the scenario and build the case again"
                            + (o.WindToWui ? ", or pass --no-wind-to-wui." : "."));
            }

            // ------------------------------------------------------------ fuel tables
            if (!ResolveFuelTables(c)) return null;

            // ------------------------------------------------------------ weather sources
            c.ArchivePath = Path.GetFullPath(!string.IsNullOrEmpty(o.WeatherArchive)
                ? o.WeatherArchive
                : ElmfireCaseBuilder.ArchivePath(caseDir, c.ScenarioName));
            c.ArchiveEndYear = o.ArchiveEndYear > 0 ? o.ArchiveEndYear : LastCompleteYear(c.ArchivePath);

            c.WindNinjaExe = o.WindNinjaExe
                             ?? (string.IsNullOrWhiteSpace(elmfire.WindNinjaExe) ? null : Absolute(c.ScenarioDir, elmfire.WindNinjaExe))
                             ?? WindNinjaRunner.FindExecutable();
            if (c.WindNinjaExe != null && !File.Exists(c.WindNinjaExe)) c.WindNinjaExe = null;
            if (running && c.WindNinjaExe == null && !o.AllowUniformWeather)
            {
                return Fail("WindNinja was not found (WINDNINJA_CLI, PATH, C:\\WindNinja, Program Files). Without it every "
                            + "realization's wind is one value across the whole domain, and the terrain does nothing to the "
                            + "fire or the boundary. Install it or pass --windninja <exe>, or pass --allow-uniform-weather "
                            + "to accept uniform wind.");
            }

            // ------------------------------------------------------------ paths ELMFIRE will see
            //Relative to the realization directory, so a space above the scenario folder (C:\Users\First Last\,
            //OneDrive - Org) never reaches ELMFIRE's unquoted gdal_translate command lines. What is left inside the
            //scenario's own tree has to be free of spaces too.
            string sampleRunDir = Path.Combine(c.ScenarioDir, CampaignLayout.OutputFolder, "campaign_x", CampaignLayout.RealizationsFolder, "0000001");
            foreach ((string what, string path) in new[]
                     {
                         ("the case's inputs folder", c.InputsDir),
                         ("the campaign folder", Path.Combine(c.ScenarioDir, CampaignLayout.OutputFolder)),
                     })
            {
                string relative = ElmfireStems.ForNamelist(sampleRunDir, path);
                if (relative.IndexOf(' ') >= 0)
                {
                    return Fail($"{what} is reached from a realization as '{relative}', which contains a space. ELMFIRE "
                                + "passes these paths to gdal_translate unquoted, so every realization would fail with "
                                + "\"Too many command options\". Rename the folder so the path has no spaces.");
                }
            }

            BuildSettings(c, input);
            return c;
        }

        // ------------------------------------------------------------------ identity

        /// <summary>
        /// Everything that decides what a realization produces, and so whether an existing one can stand in for
        /// it. Things that only decide when the campaign stops (--max, --streak, --tolerance, --parallel) are not
        /// in it, so a resumed campaign can be run further; nor is PREACT.exe, which is recorded and compared
        /// separately so --resume-only does not need it.
        /// </summary>
        private static void BuildSettings(Campaign c, PREACTInput input)
        {
            CampaignOptions o = c.Options;
            var s = c.Settings;

            s["format"] = "wuinity-campaign-1";
            s["scenario.file"] = c.BaseFileName;
            s["scenario.sha256"] = ElmfireFingerprint.HashFile(c.BaseWuiPath);
            s["template.sha256"] = ElmfireFingerprint.Hash(string.Join("\n", c.TemplateLines));
            s["inputs"] = InputsFingerprint(c.InputsDir);
            s["fuel_table.sha256"] = ElmfireFingerprint.HashFile(c.FuelTableSource);
            s["building_table.sha256"] = c.BuildingTableSource == null ? "(none)" : ElmfireFingerprint.HashFile(c.BuildingTableSource);
            s["elmfire.sha256"] = ElmfireFingerprint.HashFile(c.ElmfireExe);
            s["hours"] = o.Hours.ToString("R", CultureInfo.InvariantCulture);
            s["max_runtime_s"] = o.MaxRuntimeSeconds.ToString("R", CultureInfo.InvariantCulture);
            s["seed"] = o.Seed.ToString(CultureInfo.InvariantCulture);
            s["weather.mode"] = o.WeatherSampling.ToString();
            s["weather.candidate_days_per_year"] = o.CandidateDaysPerYear.ToString(CultureInfo.InvariantCulture);
            s["weather.live_fuel_moisture"] = o.FitLiveFuelMoisture ? "true" : "false";
            s["weather.wind_to_wui"] = o.WindToWui ? "true" : "false";
            s["weather.allow_uniform"] = o.AllowUniformWeather ? "true" : "false";
            s["weather.archive"] = Path.GetFileName(c.ArchivePath) + " " + o.ArchiveStartYear.ToString(CultureInfo.InvariantCulture)
                                   + "-" + c.ArchiveEndYear.ToString(CultureInfo.InvariantCulture)
                                   + " format " + ClimatologySampler.ArchiveFormatVersion.ToString(CultureInfo.InvariantCulture);
            s["weather.conditioning_days"] = o.ConditioningDays.ToString(CultureInfo.InvariantCulture);
            s["weather.windninja"] = c.WindNinjaExe == null ? "(none: uniform wind)" : Path.GetFileName(c.WindNinjaExe) + " mesh " + o.WindNinjaMesh;
            s["weather.start"] = c.StartDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

            c.SettingsHash = ElmfireFingerprint.Hash(string.Join("\n", s.Select(kv => kv.Key + "=" + kv.Value)));
            c.Folder = Path.Combine(c.ScenarioDir, CampaignLayout.OutputFolder,
                           CampaignLayout.CampaignFolderName(c.ScenarioName, c.SettingsHash.Substring(0, 8)));
            c.RealizationsDir = Path.Combine(c.Folder, CampaignLayout.RealizationsFolder);

            var info = c.Information;
            info["scenario"] = c.BaseWuiPath;
            info["template"] = c.TemplatePath;
            info["inputs_dir"] = c.InputsDir;
            info["elmfire_exe"] = c.ElmfireExe ?? "";
            info["gdal"] = c.GdalBin ?? "";
            info["preact"] = c.PreactExe == null ? "" : ElmfireFingerprint.DescribeFile(c.PreactExe) + " " + c.PreactExe;
            info["archive"] = c.ArchivePath;
            info["fuel_table"] = c.FuelTableSource;
            info["fuel_stem"] = c.FuelStem;
            info["wui_area"] = c.WuiAreaFile ?? "";
            info["wui_area_source"] = c.WuiAreaSource;
        }

        /// <summary>
        /// The case's inputs by content - without the five weather rasters, which a campaign never
        /// reads (every realization draws its own), so rebuilding the case's weather does not orphan a campaign.
        /// </summary>
        private static string InputsFingerprint(string inputsDir)
        {
            var sb = new System.Text.StringBuilder();
            ElmfireFingerprint.AppendDirectory(sb, "inputs", inputsDir, new HashSet<string>(ElmfireStems.Weather, StringComparer.OrdinalIgnoreCase));
            return ElmfireFingerprint.Hash(sb.ToString());
        }

        // ------------------------------------------------------------------ checks

        private static bool CheckIgnitionMask(Campaign c)
        {
            string maskPath = ElmfireStems.Tif(c.InputsDir, c.IgnitionMaskStem);
            if (!File.Exists(maskPath))
            {
                Fail("every realization's ignition is drawn from the ignition mask, and " + maskPath + " is not there. "
                     + "Build the case (it always writes one), or set [ELMFIRE] IgnitionMaskFile.");
                return false;
            }

            float[,] mask = AscRaster.ReadGeoTiff(maskPath, out AscRaster.Header _, out bool ok);
            if (!ok || mask == null)
            {
                Fail("the ignition mask cannot be read: " + maskPath);
                return false;
            }

            int ignitable = 0;
            foreach (float v in mask) if (v > 0f) ++ignitable;
            c.IgnitableCells = ignitable;

            if (ignitable == 0)
            {
                Fail(maskPath + " has no cell with a positive weight, so there is nowhere for a realization to ignite.");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Finds the fuel model tables the campaign will run with, to be copied into its folder once.
        /// </summary>
        /// <remarks>
        /// With FUEL_MODEL_FILE unset every ELMFIRE run wrote its built-in table into the shared inputs folder and
        /// read it back - concurrently, at --parallel width, which can yield "Problem opening new fuel model table
        /// file" or a half-written table - and silently replaced a hand-edited one. The campaign now names the
        /// table in every realization and points MISCELLANEOUS_INPUTS_DIRECTORY at its own copies.
        /// </remarks>
        private static bool ResolveFuelTables(Campaign c)
        {
            string runRoot = Path.GetDirectoryName(c.TemplatePath);
            string misc = ElmfireStems.ResolveDirectory(c.TemplateLines, ElmfireNamelistKeys.MiscellaneousGroup,
                              ElmfireNamelistKeys.MiscellaneousInputsDirectory, runRoot) ?? c.InputsDir;

            string named = ElmfireNamelist.GetKeyInGroup(c.TemplateLines, ElmfireNamelistKeys.MiscellaneousGroup,
                               ElmfireNamelistKeys.FuelModelFile);
            bool hasName = !string.IsNullOrWhiteSpace(named) && !named.Equals("null", StringComparison.OrdinalIgnoreCase);

            string candidate = Path.Combine(misc, hasName ? named : ElmfireStems.FuelModelTable);
            if (File.Exists(candidate))
            {
                c.FuelTableSource = candidate;
            }
            else if (hasName)
            {
                Fail($"the template names FUEL_MODEL_FILE = '{named}', which is not in {misc}.");
                return false;
            }
            else
            {
                c.FuelTableSource = ElmfireCaseBuilder.DefaultFuelModelTable(c.ElmfireExe);
                if (c.FuelTableSource == null)
                {
                    Fail($"there is no {ElmfireStems.FuelModelTable} in {misc} and ELMFIRE's default table could not be "
                         + "found beside the executable (build/source/fuel_models.csv). Build the case again - it writes "
                         + "one - or copy one there.");
                    return false;
                }
            }

            string building = ElmfireNamelist.GetKeyInGroup(c.TemplateLines, ElmfireNamelistKeys.MiscellaneousGroup,
                                  ElmfireNamelistKeys.BuildingFuelModelFile);
            if (!string.IsNullOrWhiteSpace(building)) c.BuildingTableName = building;
            string buildingPath = Path.Combine(misc, c.BuildingTableName);
            c.BuildingTableSource = File.Exists(buildingPath) ? buildingPath : null;
            return true;
        }

        // ------------------------------------------------------------------ weather

        /// <summary>
        /// Prepares what every realization's weather draws from, once: the archive (in the current format), the
        /// grid, the live fuel moisture march and the fitted distributions' report. Fatal on anything missing - a
        /// campaign that silently ran every realization on uniform weather looks exactly like a working one.
        /// </summary>
        public static bool PrepareWeather(Campaign c)
        {
            CampaignOptions o = c.Options;

            string dem = ElmfireStems.Tif(c.InputsDir, ElmfireStems.Dem);
            try
            {
                c.Grid = MasterGrid.FromRasterFile(dem);
            }
            catch (Exception e)
            {
                Fail("could not read the case grid from " + dem + ": " + e.Message);
                return false;
            }

            var w = new WeatherRasterPipeline.Options
            {
                Grid = c.Grid,
                TerrainDirectory = c.InputsDir,
                LatLon = c.CentreLatLon,
                ArchiveCsvPath = c.ArchivePath,
                ArchiveStartYear = o.ArchiveStartYear,
                ArchiveEndYear = c.ArchiveEndYear,
                ConditioningDays = o.ConditioningDays,
                WindNinjaExe = c.WindNinjaExe,
                WindNinjaMesh = o.WindNinjaMesh,
                SimulationStartDateTime = c.StartDateTime,
                SimulationTstopSeconds = CampaignLayout.TstopSeconds(o.Hours),
                SecondsPerBand = c.SecondsPerBand,
                MaxBands = 0,
                Sampling = o.WeatherSampling,
                CandidateDaysPerYear = o.CandidateDaysPerYear,
                FitLiveFuelMoisture = o.FitLiveFuelMoisture,
                Seed = o.Seed,
            };

            //Fetched (or brought to the current format) once, serially, rather than by whichever realizations
            //start first: concurrent processes writing the same cache file is the race that broke long campaigns.
            try
            {
                if (!File.Exists(c.ArchivePath))
                {
                    Console.WriteLine("Fetching the ERA5 climatology archive once for the whole campaign...");
                }
                c.ArchiveRows = WeatherRasterPipeline.LoadArchive(w, Console.WriteLine).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Fail("could not prepare the ERA5 climatology archive " + c.ArchivePath + ": " + e.Message);
                return false;
            }

            if (c.ArchiveRows == null || c.ArchiveRows.Count == 0)
            {
                Fail("the ERA5 archive " + c.ArchivePath + " holds no hours.");
                return false;
            }

            w.PreloadedRows = c.ArchiveRows;

            List<AnnualMaximaDay> pool = o.WeatherSampling == WeatherRasterPipeline.SamplingMode.FittedDistributions
                ? ClimatologySampler.BuildCandidatePool(c.ArchiveRows, o.CandidateDaysPerYear)
                : ClimatologySampler.BuildAnnualMaxima(c.ArchiveRows);
            if (pool.Count == 0)
            {
                Fail("the archive holds no day with a non-zero fire weather index, so there is nothing to draw weather from.");
                return false;
            }

            if (o.WeatherSampling == WeatherRasterPipeline.SamplingMode.FittedDistributions && o.FitLiveFuelMoisture)
            {
                try
                {
                    w.LiveFuelMoisture.Latitude = c.CentreLatLon.x;
                    c.LiveMoistureByDate = LiveFuelMoistureSampler.March(c.ArchiveRows, w.LiveFuelMoisture,
                        m => Console.WriteLine("        " + m.Trim()));
                    w.LiveMoistureByDate = c.LiveMoistureByDate;
                }
                catch (Exception e)
                {
                    if (!o.AllowUniformWeather)
                    {
                        Fail("the NFDRS4 live fuel moisture march failed (" + e.Message + "). Pass --no-live-fuel-moisture "
                             + "to keep the case's own LH/LW constants.");
                        return false;
                    }
                    Console.Error.WriteLine("WARNING: the live fuel moisture march failed (" + e.Message + "); every "
                                            + "realization keeps the case's own LH/LW constants.");
                    w.FitLiveFuelMoisture = false;
                }
            }

            try
            {
                c.WeatherStatistics = WeatherStatisticsReport.Compute(pool, c.ArchiveRows, c.LiveMoistureByDate,
                    w.Lag10HourPercent, w.Lag100HourPercent);
                c.WeatherStatisticsHeader =
                    $"Campaign weather distributions, over the {(o.WeatherSampling == WeatherRasterPipeline.SamplingMode.FittedDistributions ? o.CandidateDaysPerYear + " highest-FWI days" : "highest-FWI day")} "
                    + "of each year.\n"
                    + $"Pool: {pool.Count} days over {pool.Select(d => d.Year).Distinct().Count()} years, archive "
                    + $"{Path.GetFileName(c.ArchivePath)} (format {ClimatologySampler.ArchiveFormatVersion}).";
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("WARNING: could not compute the weather distribution report: " + e.Message);
            }

            c.Weather = w;

            Console.WriteLine($"Weather: {pool.Count} candidate days from {Path.GetFileName(c.ArchivePath)}; "
                              + (o.WeatherSampling == WeatherRasterPipeline.SamplingMode.FittedDistributions
                                  ? "each realization draws one value per parameter from normals fitted to them (one band, one WindNinja solve)"
                                  : $"each realization replays one of them, {System.Math.Ceiling(CampaignLayout.TstopSeconds(o.Hours) / c.SecondsPerBand):F0} hourly bands (one WindNinja solve each) and Nelson")
                              + (c.WindNinjaExe != null ? ", WindNinja " + Path.GetFileName(c.WindNinjaExe) + "." : ", NO WindNinja: uniform wind (--allow-uniform-weather)."));
            return true;
        }

        // ------------------------------------------------------------------ small helpers

        private static Vector2d CentreOf(Vector2d lowerLeftLatLon, Vector2d domainSizeMetres)
        {
            Vector2d halfDeg = PREACT.Population.LocalGPWData.SizeToDegrees(lowerLeftLatLon,
                new Vector2d(0.5 * domainSizeMetres.x, 0.5 * domainSizeMetres.y));
            return new Vector2d(lowerLeftLatLon.x + halfDeg.y, lowerLeftLatLon.y + halfDeg.x);
        }

        /// <summary>
        /// The last complete year an existing archive holds, so a campaign pins itself to the archive it has rather
        /// than to the calendar; the last complete calendar year when there is no archive yet.
        /// </summary>
        private static int LastCompleteYear(string archive)
        {
            int fallback = DateTime.UtcNow.Year - 1;
            if (!File.Exists(archive)) return fallback;

            try
            {
                string last = File.ReadLines(archive).LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                string first = last?.Split(',')[0];
                if (first != null && DateTime.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t))
                {
                    return t.Month == 12 && t.Day == 31 ? t.Year : t.Year - 1;
                }
            }
            catch { }

            return fallback;
        }

        private static double ReadNumber(string[] lines, string group, string key, double fallback)
        {
            string value = ElmfireNamelist.GetKeyInGroup(lines, group, key);
            return !string.IsNullOrEmpty(value)
                   && double.TryParse(value.Replace('d', 'e').Replace('D', 'E'), NumberStyles.Float,
                       CultureInfo.InvariantCulture, out double parsed) && parsed > 0
                ? parsed
                : fallback;
        }

        private static string Absolute(string root, string path)
        {
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
        }

        private static Campaign Fail(string message)
        {
            Console.Error.WriteLine("ERROR: " + message);
            return null;
        }
    }
}
