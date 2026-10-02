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

            //Through the real parser, so the campaign reads the same keys the GUI and PREACT.exe do - and on the
            //very lines the campaign hashes and copies into every realization, rather than a second read of the
            //file that could differ from them. It does not have to be complete - the SUMO network or the
            //population may be missing on this machine - but the sections the campaign reads must have been read.
            PREACTInput input = PREACTInput.LoadFromLines(c.BaseLines, c.ScenarioDir, out bool _);
            if (input?.Simulation == null || input.WildfireModule?.ElmfireInput == null)
            {
                string why = PREACTInput.Requirements.Where(r => r.Critical).Select(r => r + ": " + r.Message).FirstOrDefault();
                return Fail("could not read the base scenario's [Simulation] and [ELMFIRE] sections"
                            + (why != null ? " (" + why + ")" : "") + ".");
            }

            c.ScenarioName = CampaignLayout.CampaignScenarioName(input.Simulation.Name, c.BaseWuiPath);
            c.StartDateTime = input.Simulation.StartDateTime;
            c.TimeZone = LocalTime.ZoneAt(input.Simulation.LowerLeftLatLon.x, input.Simulation.LowerLeftLatLon.y);
            c.BaseRandomSeed = input.Simulation.RandomSeed;
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
            string caseDir = ElmfireCoupling.CaseDirectoryPath(c.ScenarioDir, elmfire);

            if (!string.IsNullOrEmpty(o.ElmfireTemplate))
            {
                c.TemplatePath = Path.GetFullPath(o.ElmfireTemplate);
                if (!File.Exists(c.TemplatePath)) return Fail("--elmfire-template is not there: " + c.TemplatePath);
            }
            else
            {
                c.TemplatePath = ElmfireCoupling.ResolveNamelist(caseDir, c.ScenarioDir, elmfire, null, out string problem);
                if (c.TemplatePath == null) return Fail(problem + " Build the case first (Data > Build fire case (ELMFIRE) in the GUI, or PREACTcli build-case).");
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
                return Fail((!string.IsNullOrEmpty(o.ElmfireExe)
                                ? "--elmfire names " + c.ElmfireExe + ", which is not there."
                                : ElmfireCoupling.DescribeMissingExecutable(c.ScenarioDir, elmfire.ElmfireExe))
                            + " Or pass --elmfire <path>.");
            }

            c.GdalBin = o.PathToGdal
                        ?? (string.IsNullOrWhiteSpace(elmfire.PathToGdal) ? null : Absolute(c.ScenarioDir, elmfire.PathToGdal))
                        ?? GdalTools.FindBinDirectory();
            if (running && (c.GdalBin == null || !Directory.Exists(c.GdalBin)))
            {
                //Refused rather than warned about: without the tools ELMFIRE writes no rasters and still exits 0.
                return Fail("GDAL's command-line tools were not found (looked on PATH, in QGIS and OSGeo4W installs, and in "
                            + "SUMO_HOME's bin); pass --gdal <bin folder>, set [ELMFIRE] PathToGdal, or "
                            + ToolPaths.WhereToSet(ToolPaths.Tool.Gdal) + ".");
            }

            c.PreactExe = o.PreactExe != null ? Path.GetFullPath(o.PreactExe) : RealizationRunner.FindPreactExe();
            if (running && (c.PreactExe == null || !File.Exists(c.PreactExe)))
            {
                return Fail((o.PreactExe != null ? "--preact names " + c.PreactExe + ", which is not there"
                                                 : "PREACT (PREACT.exe on Windows) was not found in PREACT/PREACTexecute/bin/Release/"
                                                   + "net8.0, where build.sh/build.ps1 put it, nor beside PREACTcli")
                            + "; build it or pass --preact <path>. (--resume-only aggregates a campaign's finished "
                            + "realizations without it.)");
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

            //Every raster the template names, on the case grid - checked here, once, rather than by ELMFIRE in every
            //realization: a fuel raster left on an old grid failed each one with "Fuel Model raster dimensions
            //mismatch (680 vs 541)", only after its weather had been drawn, up to --max times. The weather is left
            //out: every realization writes its own onto the case grid.
            ElmfireCaseValidator.Report rasters = ElmfireCaseValidator.ValidateNamelistRasters(c.TemplateLines, runRoot,
                includeWeather: false, inputsDirectory: c.InputsDir);
            if (!rasters.Ok)
            {
                return Fail("the template " + Path.GetFileName(c.TemplatePath) + " names rasters that are not on the case "
                            + "grid or not stored the way ELMFIRE reads them, so ELMFIRE could not run a single realization:\n         "
                            + string.Join("\n         ", rasters.Fatal)
                            + "\n       Build the case again (Data > Build fire case (ELMFIRE) in the GUI, or PREACTcli build-case): it re-cuts onto the grid "
                            + "every raster named by the case's elmfire.data, its kept namelists (elmfire.data.kept-*) and "
                            + "the scenario's [ELMFIRE] NamelistTemplate, and stores their fuel models as Int16. Or point the "
                            + "template at rasters that are.");
            }

            string maskStem = ElmfireNamelist.GetKeyInGroup(c.TemplateLines, ElmfireNamelistKeys.InputsGroup,
                                  ElmfireNamelistKeys.IgnitionMaskFilename);
            if (!string.IsNullOrWhiteSpace(maskStem)) c.IgnitionMaskStem = maskStem;
            if (!CheckIgnitionMask(c)) return null;

            // ------------------------------------------------------------ the WUI area
            //The case's wui_area.tif, which the case build writes as the union of the evacuation groups: every realization
            //protects exactly those cells, aims its wind at their centre and is checked against them before its evacuation
            //runs. A mask of the scenario's own ([kPERIL] WuiAreaSource=Raster with a WuiAreaFile) takes their place.
            kPERILInput peril = input.TriggerBufferModule.kPERILInput;
            string caseWui = ElmfireStems.Tif(c.InputsDir, ElmfireStems.WuiArea);
            string named = peril.WuiAreaSource == kPERILInput.WuiAreaSources.Raster && !string.IsNullOrWhiteSpace(peril.WuiAreaFile)
                ? Absolute(c.ScenarioDir, peril.WuiAreaFile)
                : null;
            bool ownMask = named != null && !string.Equals(named, Path.GetFullPath(caseWui), StringComparison.OrdinalIgnoreCase);
            string wuiArea = ownMask ? named : caseWui;

            if (!MaskIgnitionSampler.TryGetMaskCentroid(wuiArea, out double wx, out double wy, out int cells))
            {
                return Fail("every realization protects the WUI area, and " + wuiArea
                            + (File.Exists(wuiArea) ? " has no marked cell." : " is not there.")
                            + (ownMask
                                ? " It is the scenario's [kPERIL] WuiAreaFile; clear that to protect the evacuation groups."
                                : " The case build writes it from the evacuation groups: paint the groups (workflow step 9) "
                                  + "and build the case again (Apply to case, or PREACTcli build-case)."));
            }

            if (!ownMask)
            {
                string stale = DescribeStaleWuiArea(input, c, caseWui);
                if (stale != null) return Fail(stale);
            }
            else
            {
                string offGrid = DescribeOffGridMask(c, named);
                if (offGrid != null) return Fail(offGrid);
            }

            c.WuiAreaFile = wuiArea;
            c.WuiCentreX = wx;
            c.WuiCentreY = wy;
            c.WuiCells = cells;

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
                return Fail("WindNinja was not found (looked at " + WindNinjaRunner.SearchDescription + "). Without it every "
                            + "realization's wind is one value across the whole domain, and the terrain does nothing to the "
                            + "fire or the boundary. Install it and " + ToolPaths.WhereToSet(ToolPaths.Tool.WindNinja)
                            + ", or pass --windninja <exe>, or pass --allow-uniform-weather to accept uniform wind.");
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
            //A historical day's bands start at the scenario's local hour, converted to the archive's UTC; they used to
            //start at that hour read as UTC, so realizations from before and after are different fires.
            if (o.WeatherSampling == WeatherRasterPipeline.SamplingMode.HistoricalDay)
            {
                s["weather.band_clock"] = "local start hour in " + c.TimeZone.Id + ", archive UTC";
            }

            //How each realization's evacuation is seeded. Recorded because realizations computed before it existed ran
            //on a clock seed, and reusing them beside reproducible ones would mix the two.
            s[CampaignLayout.EvacuationSeedSetting] = "seed + " + RealizationRunner.EvacuationSeedOffset.ToString(CultureInfo.InvariantCulture) + " + index";

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

        /// <summary>
        /// Why the case's wui_area.tif is not the union of the scenario's evacuation groups on the case grid, or null when
        /// it is: the groups were painted again since the case was built, or an older build wrote it from a painted WUI
        /// area. Every realization protects that file, so a campaign on it would answer for other ground than the
        /// scenario evacuates.
        /// </summary>
        /// <summary>
        /// Why the scenario's own WUI mask ([kPERIL] WuiAreaSource=Raster with a WuiAreaFile) cannot be used on the case's
        /// fire grid, or null when it lies on it: same size, cell size and origin (to a tenth of a cell) as
        /// <c>inputs/dem.tif</c>.
        /// </summary>
        /// <remarks>
        /// Review R2 MI-3: a mask on another grid passed the centroid check, and then every realization ran its fire for
        /// hours, ran its evacuation (the "does the fire reach it" test could not compare and let it through) and failed in
        /// k-PERIL ("could not be used on the fire grid"). The GUI's step 10 said so; a campaign from a shell did not.
        /// </remarks>
        private static string DescribeOffGridMask(Campaign c, string mask)
        {
            MasterGrid grid, own;
            try { grid = MasterGrid.FromRasterFile(ElmfireStems.Tif(c.InputsDir, ElmfireStems.Dem)); }
            catch { return null; }
            try { own = MasterGrid.FromRasterFile(mask); }
            catch (Exception e) { return "the scenario's [kPERIL] WuiAreaFile, " + mask + ", cannot be read as a raster (" + e.Message + ")."; }

            double cell = grid.Header.CellSize;
            bool onGrid = own.Header.Ncols == grid.Header.Ncols && own.Header.Nrows == grid.Header.Nrows
                          && System.Math.Abs(own.Header.CellSize - cell) <= 0.001 * cell
                          && System.Math.Abs(own.XMin - grid.XMin) <= 0.1 * cell && System.Math.Abs(own.YMax - grid.YMax) <= 0.1 * cell;
            if (onGrid) return null;

            string I(double v) => v.ToString("F0", CultureInfo.InvariantCulture);
            return $"the scenario's [kPERIL] WuiAreaFile, {mask}, is {own.Header.Ncols}x{own.Header.Nrows} cells of "
                   + $"{own.Header.CellSize.ToString("0.##", CultureInfo.InvariantCulture)} m from ({I(own.XMin)}, {I(own.YMax)}), but the "
                   + $"case's fire grid (inputs/dem.tif) is {grid.Header.Ncols}x{grid.Header.Nrows} cells of "
                   + $"{cell.ToString("0.##", CultureInfo.InvariantCulture)} m from ({I(grid.XMin)}, {I(grid.YMax)}). k-PERIL takes the "
                   + "WUI area on the fire grid, so every realization would run its fire and its evacuation and then fail. Make "
                   + "the mask on the case grid, or clear WuiAreaFile to protect the evacuation groups (the case's wui_area.tif).";
        }

        private static string DescribeStaleWuiArea(PREACTInput input, Campaign c, string caseWui)
        {
            var problems = new List<string>();
            List<PREACT.Evacuation.EvacuationGroupArea> areas = PREACT.Evacuation.EvacuationGroupArea.LoadAll(
                input.Evacuation?.EvacuationGroupInputs?.Values, c.ScenarioDir, input.Simulation.Data, problems.Add);
            foreach (string p in problems) Console.Error.WriteLine("WARNING: " + p);
            if (areas.Count == 0)
            {
                return "the WUI area every realization protects is the evacuation groups' area, and no group of the scenario "
                       + "has one: paint the groups (workflow step 9), then build the case again.";
            }

            MasterGrid grid;
            try { grid = MasterGrid.FromRasterFile(ElmfireStems.Tif(c.InputsDir, ElmfireStems.Dem)); }
            catch (Exception e) { return "could not read the case grid to check its WUI area: " + e.Message; }

            bool[] union = PREACT.Evacuation.EvacuationGroupArea.Rasterize(areas, grid, input.Simulation.Data, out int groupCells);
            float[,] mask = AscRaster.ReadGeoTiff(caseWui, out AscRaster.Header header, out bool ok);
            if (!ok || mask == null || header.Ncols != grid.Header.Ncols || header.Nrows != grid.Header.Nrows)
            {
                return caseWui + " is not on the case grid; build the case again.";
            }

            int fileCells = 0, differ = 0;
            for (int y = 0; y < header.Nrows; ++y)
            {
                for (int x = 0; x < header.Ncols; ++x)
                {
                    bool marked = mask[x, y] > 0f && mask[x, y] != (float)header.NoDataValue;
                    if (marked) ++fileCells;
                    if (marked != union[x + y * header.Ncols]) ++differ;
                }
            }

            if (differ == 0) return null;
            return $"the case's wui_area.tif ({fileCells} cells) is not the WUI area of the scenario's evacuation groups "
                   + $"({PREACT.Evacuation.EvacuationGroupArea.Names(areas)}: {groupCells} cells; {differ} cells differ) - the "
                   + "groups changed since the case was built, or an older build wrote it from a painted WUI area. Build the "
                   + "case again (Apply to case in the GUI, or PREACTcli build-case), then start the campaign.";
        }

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

            //Read by ELMFIRE whenever the building spread model is on, and every realization would stop at start-up
            //without it ("Problem opening building fuel model table file", e2e N2): ELMFIRE's own table when it is the
            //default name, otherwise refused here.
            bool buildings = ElmfireNamelist.IsTrue(ElmfireNamelist.GetKeyInGroup(c.TemplateLines, ElmfireNamelistKeys.WuiGroup,
                                 ElmfireNamelistKeys.UseBuildingSpreadModel));
            if (buildings && c.BuildingTableSource == null)
            {
                if (string.Equals(c.BuildingTableName, ElmfireStems.BuildingFuelModelTable, StringComparison.OrdinalIgnoreCase))
                {
                    c.BuildingTableSource = ElmfireCaseBuilder.DefaultBuildingFuelModelTable(c.ElmfireExe);
                }
                if (c.BuildingTableSource == null)
                {
                    Fail($"the template switches the building spread model on (&WUI USE_BLDG_SPREAD_MODEL), and there is no "
                         + $"{c.BuildingTableName} in {misc}"
                         + (c.BuildingTableName == ElmfireStems.BuildingFuelModelTable
                             ? " nor ELMFIRE's default beside the executable (build/source/building_fuel_models.csv)" : "")
                         + ". Build the case again - it copies ELMFIRE's - or put one there, or switch the model off.");
                    return false;
                }
            }
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

            //A --weather-archive is the user's file: the campaign reads a copy kept in its own folder, which is also
            //the record of the archive it drew from.
            if (!string.IsNullOrEmpty(o.WeatherArchive))
            {
                c.ArchivePath = ClimatologySampler.WorkingCopy(c.ArchivePath, c.Folder, Console.WriteLine);
            }

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
                StartTimeZone = c.TimeZone,
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
            string normalised = PREACTInput.NormalisePath(path);
            return Path.GetFullPath(Path.IsPathRooted(normalised) ? normalised : Path.Combine(root, normalised));
        }

        private static Campaign Fail(string message)
        {
            Console.Error.WriteLine("ERROR: " + message);
            return null;
        }
    }
}
