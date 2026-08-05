using System;
using ImGuiNET;
using PREACT.Input;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// What computes the fire, and how it is run.
    /// </summary>
    /// <remarks>
    /// Only the module's own plumbing lives here — where the case is, which binaries, what to build, which
    /// source layers. What the fire is <em>told</em> is the "Fire behaviour" tab, because there are a hundred
    /// of those settings and they answer a different question.
    /// </remarks>
    internal static class FireInputTab
    {
        private static readonly string[] WildfireModules = Enum.GetNames(typeof(WildfireModuleInput.WildfireModules));
        private static readonly string[] TimeOfArrivalUnits = Enum.GetNames(typeof(AscImportInput.TimeUnits));

        public static void Draw(PREACTInput input)
        {
            //Whether a module runs is its Enabled flag; which module is the combo below. Two different
            //questions, and Enabled used to be settable only while creating a scenario - so a finished one
            //could not be run without its fire, or have a hazard added, without editing the .wui by hand.
            if (Fields.Check("Simulate wildfire spread", ref input.WildfireModule.Enabled))
            {
                if (!input.WildfireModule.Enabled)
                {
                    //Both read the fire: smoke is produced by it, and k-PERIL back-propagates from its arrival
                    //times and asks the wildfire module for the grid to work on. Left enabled with no fire, the
                    //trigger boundary run dereferences a module that was never created. Switched off here
                    //rather than merely warned about, since neither can do anything.
                    if (input.SmokeModule.Enabled || input.TriggerBufferModule.Enabled)
                    {
                        PREACT.Engine.Message(null, PREACT.Engine.LogType.Log,
                            "Smoke and trigger boundaries need the fire, so they were switched off with it. "
                            + "Their settings are kept and come back when the fire does.");
                    }
                    input.SmokeModule.Enabled = false;
                    input.TriggerBufferModule.Enabled = false;
                }
            }

            if (input.WildfireModule.Enabled
                && input.WildfireModule.Module == WildfireModuleInput.WildfireModules.None)
            {
                Fields.Warn("Pick a module, or the run aborts on \"could not initiate wildfire module\".");
            }

            ImGui.BeginDisabled(!input.WildfireModule.Enabled);

            Fields.Choice("Module", ref input.WildfireModule.Module, WildfireModules);

            ImGui.Separator();

            switch (input.WildfireModule.Module)
            {
                case WildfireModuleInput.WildfireModules.ELMFIRE:
                    DrawElmfire(input.WildfireModule.ElmfireInput);
                    break;

                case WildfireModuleInput.WildfireModules.AscImport:
                    DrawAscImport(input.WildfireModule.AscImportInput);
                    break;
            }

            ImGui.EndDisabled();
        }

        /// <summary>
        /// Running ELMFIRE: four groups, each one question.
        /// </summary>
        /// <remarks>
        /// Everything here has a workable default, so it is a panel of adjustments rather than a list of
        /// requirements — which is why nothing is marked required. What has to exist (the executable, the
        /// case, its rasters) is checked when the run starts, where the paths are resolved and the reason can
        /// be specific.
        /// </remarks>
        private static void DrawElmfire(ElmfireInput elmfire)
        {
            ImGui.TextWrapped("ELMFIRE computes the whole fire and writes rasters, so it runs once when the "
                + "simulation starts and the fire is read back from its output. The first run takes minutes; "
                + "after that the output is reused.");

            if (ImGui.CollapsingHeader("Case and executables", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();

                Fields.Folder("CaseDirectory", () => elmfire.CaseDirectory, v => elmfire.CaseDirectory = v,
                    "The case folder, relative to the scenario. Holds inputs/, outputs/ and elmfire.data.");

                DrawResolvedTools(elmfire);

                Fields.Path("NamelistTemplate (empty = generate one)",
                    () => elmfire.NamelistTemplate, v => elmfire.NamelistTemplate = v);
                if (!string.IsNullOrEmpty(elmfire.NamelistTemplate))
                {
                    Fields.Warn("Set, so the Fire behaviour tab is not used.");
                }
                Fields.Hint("Looked for in the case directory first, then beside the scenario - which is what",
                            "the picker writes. The console says which one was used.");

                DrawNamelistImport(elmfire);

                ImGui.Unindent();
            }

            if (ImGui.CollapsingHeader("Timing and reuse", ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Indent();

                Fields.Real("SimulationTstopHours", ref elmfire.SimulationTstopHours);
                Fields.Hint($"{elmfire.SimulationTstopHours:F1} hours of fire ({elmfire.TstopSeconds():F0} s to",
                            "ELMFIRE, which wants seconds). The case needs at least this many hourly weather",
                            "bands or ELMFIRE refuses the run. Separate from the",
                            "evacuation's own end time.");

                Fields.Check("Reuse output already in the case folder", ref elmfire.ReuseExistingOutput,
                    "Off means ELMFIRE runs again every time the simulation starts, which is what changed "
                    + "settings need.");

                ImGui.Unindent();
            }

            if (ImGui.CollapsingHeader("Building the case"))
            {
                ImGui.Indent();

                Fields.Check("Build what the case is missing", ref elmfire.BuildCase,
                    "Produces only the layers the case does not already have - a DEM if there is none, then "
                    + "slope, aspect, adj/phi, the weather and a namelist. Safe on a prepared case: what is "
                    + "already there is kept.");

                if (elmfire.BuildCase)
                {
                    ImGui.Indent();

                    Fields.Check("Rebuild layers the case already has", ref elmfire.RebuildExistingLayers);
                    if (elmfire.RebuildExistingLayers)
                    {
                        //Worth spelling out rather than leaving to a tooltip: this replaces work that cannot
                        //always be redone. Canopy in particular has no global source, so a rebuild that is not
                        //handed canopy rasters fills them with zeros.
                        Fields.Caution("This replaces the case's rasters, its weather and its namelist.",
                                       "Canopy with no source is refilled with zeros - surface fire only.",
                                       "Only needed when the domain or the cell size changed.");
                    }
                    else
                    {
                        //The namelist is a layer for this purpose, and the one most likely to be stale: it is
                        //kept like the rasters are, so changing a behaviour setting does nothing to a case that
                        //already has one until this is on or the file is deleted.
                        Fields.Warn("A case that already has elmfire.data keeps it, so the Fire behaviour",
                                    "tab will not reach it. Turn this on, or delete the file, to rewrite it.");
                    }

                    Fields.Real("CellSizeMetres", ref elmfire.CellSizeMetres);
                    Fields.Real("PaddingMetres", ref elmfire.PaddingMetres);
                    Fields.Hint("Margin beyond the evacuation domain, so the fire is not clipped at its edge.",
                                "Both only apply to layers actually being built.");

                    ImGui.Unindent();
                }

                Fields.Hint("The same build runs from Scenario > Prepare data, where it can be done once",
                            "rather than at the start of a run.");

                ImGui.Unindent();
            }

            DrawSourceLayers(elmfire);
        }

        /// <summary>
        /// The three external tools, shown as what was found rather than asked for.
        /// </summary>
        /// <remarks>
        /// These were editable path fields, and that was the wrong shape for them: all three resolve themselves
        /// — the vendored ELMFIRE build by walking up from the assembly, GDAL from PATH or a QGIS/OSGeo4W
        /// install, WindNinja from its installer's locations — so on a working machine the correct answer was
        /// always "leave it empty", and the fields existed only to be left blank. Worse, they made the tools
        /// look like the user's responsibility: the trigger campaign window asked for the same two and refused
        /// to start without them, on a machine where everything was installed.
        ///
        /// Reported instead, because which build is in use is worth knowing and cannot be seen any other way.
        /// The <c>.wui</c> keys still exist for an install somewhere unusual — they are simply not offered here,
        /// since needing one is rare enough that a documented key is the right home for it.
        /// </remarks>
        private static void DrawResolvedTools(ElmfireInput elmfire)
        {
            string root = ScenarioEditorWindow.Input?.RootFolder;

            Tool("ELMFIRE", PREACT.Utility.ElmfireCoupling.ResolveExecutable(root, elmfire.ElmfireExe),
                elmfire.ElmfireExe,
                "the vendored build under ThirdParty/elmfire",
                "No elmfire.exe found. The run cannot compute a fire.");

            Tool("GDAL", PREACT.Utility.GdalTools.FindBinDirectory(), elmfire.PathToGdal,
                "PATH, then a QGIS or OSGeo4W install",
                "No GDAL tools found. ELMFIRE shells out to gdal_translate and gdalinfo, and fails its own "
                + "DEM check without them - reporting a problem with the DEM rather than with GDAL.");

            Tool("WindNinja", PREACT.Utility.WindNinjaRunner.FindExecutable(), elmfire.WindNinjaExe,
                "WINDNINJA_CLI, PATH, then the installer's locations",
                "No WindNinja found. The case gets one wind value for the whole domain, so the trigger "
                + "boundary comes out circular instead of wind-driven.");
        }

        /// <summary>One resolved tool: what is in use, or what is missing and what that costs.</summary>
        private static void Tool(string label, string resolved, string overridden, string where, string missing)
        {
            if (string.IsNullOrEmpty(resolved))
            {
                Fields.Warn($"{label}: not found.");
                Fields.Hint("  " + missing);
                return;
            }

            //Saying which of the two it is matters: an override that no longer exists silently falls back to
            //the probe, and the two can be different builds.
            bool isOverride = !string.IsNullOrWhiteSpace(overridden);
            ImGui.TextDisabled($"{label}: {resolved}");
            Fields.Hint(isOverride
                ? $"  from [ELMFIRE] {label} override in the scenario"
                : $"  found automatically ({where})");
        }

        private static string _importStatus = string.Empty;

        /// <summary>
        /// Reads an existing namelist into the Fire behaviour settings.
        /// </summary>
        /// <remarks>
        /// The editor's hundred settings started at their defaults regardless of what the case's own
        /// <c>elmfire.data</c> said, so a case tuned by hand — or simply built once and kept, which is the normal
        /// state — showed one set of values on screen and ran another. Reading the file in makes what is
        /// displayed what will actually run, and gives a hand-tuned namelist a way into the editor instead of
        /// being something the GUI can only overwrite.
        /// </remarks>
        private static void DrawNamelistImport(ElmfireInput elmfire)
        {
            string root = ScenarioEditorWindow.Input?.RootFolder;
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            //The template if one is named, otherwise the case's own namelist - the same two the run resolves
            //between, in the same order, so the button reads whichever file the run would use.
            string named = string.IsNullOrEmpty(elmfire.NamelistTemplate)
                ? null
                : Resolve(root, elmfire.NamelistTemplate);
            string generated = Resolve(root, System.IO.Path.Combine(elmfire.CaseDirectory ?? "elmfire", "elmfire.data"));

            string source = !string.IsNullOrEmpty(named) && System.IO.File.Exists(named) ? named
                          : (System.IO.File.Exists(generated) ? generated : null);

            if (source == null)
            {
                Fields.Hint("No namelist to read yet - build the case, or set a template above.");
                return;
            }

            //Two files, because they hold different things and the namelist cannot hold both: it names the
            //stems inside the case, never the sources they were warped out of. Read together so one button
            //restores the whole case into the editor.
            string caseDir = Resolve(root, elmfire.CaseDirectory ?? "elmfire");

            if (ImGui.Button("Read this case into the editor"))
            {
                if (elmfire.Namelist.LoadFromNamelist(source, out int applied, out System.Collections.Generic.List<string> unknown))
                {
                    //Counted, not listed. The previous message named the keys, which invited the question this
                    //answers instead: they are not missing settings, they are the ones the case decides.
                    _importStatus = $"Read {applied} setting(s) from {System.IO.Path.GetFileName(source)}."
                        + (unknown.Count > 0
                            ? $" The other {unknown.Count} are derived from the case rather than chosen here - "
                              + "the ignition coordinates, the simulated duration, the weather band count, the "
                              + "output rasters the reader requires, and the tool paths. They are rewritten "
                              + "from the case every time the namelist is generated."
                            : string.Empty);
                }
                else
                {
                    _importStatus = "Could not read " + source;
                }

                //The source layers, which the namelist has no way to express. Reported separately so a case
                //built before the manifest existed says why the layer fields stayed empty rather than looking
                //like the read half-failed.
                if (elmfire.LoadSourcesFromCase(caseDir, out int sourcesApplied, out string sourcesProblem))
                {
                    _importStatus += $" Restored {sourcesApplied} source layer path(s) from "
                        + PREACT.Utility.ElmfireCaseBuilder.SourceManifestName + ".";
                }
                else
                {
                    _importStatus += " " + sourcesProblem;
                }
            }

            Fields.Hint("Reads both files a case carries: the namelist fills the Fire behaviour tab, and",
                        "case_sources.txt restores the source layer paths above. The namelist cannot supply",
                        "those - its CC_FILENAME='cc' names a raster inside the case, not the raster it was",
                        "warped out of - so the builder records them separately on every build.");

            if (!string.IsNullOrEmpty(_importStatus))
            {
                ImGui.TextWrapped(_importStatus);
            }
        }

        private static string Resolve(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            try
            {
                return System.IO.Path.IsPathRooted(relative)
                    ? relative
                    : System.IO.Path.Combine(root, relative);
            }
            catch { return null; }
        }

        /// <summary>
        /// The layers that have to come from outside, because no global source exists to download them.
        /// </summary>
        /// <remarks>
        /// Every one is optional and every one changes what the fire can do, which is why the consequence of
        /// leaving each blank is spelled out rather than left to a tooltip. Canopy is the sharp one: absent it
        /// is filled with zeros and the run is surface fire only — a quiet switch, since a case with no canopy
        /// builds and runs perfectly happily.
        /// </remarks>
        private static void DrawSourceLayers(ElmfireInput elmfire)
        {
            if (!ImGui.CollapsingHeader("Source layers (fuel, canopy, buildings)"))
            {
                return;
            }

            ImGui.Indent();

            ImGui.TextWrapped("Any CRS and any resolution - each is warped onto the case's grid when the case "
                + "is built, nearest-neighbour for the categorical layers and bilinear for the continuous "
                + "ones. A layer the case already has is kept unless 'Rebuild layers the case already has' is "
                + "on, so naming a source here is safe on a prepared case.");

            if (!elmfire.BuildCase)
            {
                Fields.Warn("These are only read while building, and 'Build what the case is missing' is off.");
            }

            ImGui.SeparatorText("Fuel");
            Fields.Path("FuelModelFile", () => elmfire.FuelModelFile, v => elmfire.FuelModelFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Choice("FuelModelStandard", ref elmfire.FuelModelStandard, FuelModelStandards);
            Fields.Hint("Which standard the raster holds, and so whether it becomes fbfm40.tif or fbfm13.tif.",
                        "Scott & Burgan 40 is what LANDFIRE and the global products ship.");

            ImGui.SeparatorText("Canopy");

            //Offered before the individual layers because it is the answer for most cases: point it at the
            //dataset once and every case in Europe gets canopy, instead of four rasters per case or none.
            Fields.Folder("CanopyDatasetFolder", () => elmfire.CanopyDatasetFolder,
                v => elmfire.CanopyDatasetFolder = v,
                "A folder of FIRE-RES pan-European canopy rasters. Supplies whichever of the four layers "
                + "below are not named individually, clipped to this case's grid.");
            Fields.Hint("Pan-European rasters, so nothing is downloaded per case - the domain is cut out of",
                        "them. They hold real units where LANDFIRE holds scaled integers, so the case forces",
                        "CH_TIMES_10 / CBH_TIMES_10 / CBD_TIMES_100 off. Only canopy is taken: terrain stays",
                        "the case's own DEM and the fuel model stays the raster above.");

            Fields.Path("CanopyCoverFile", () => elmfire.CanopyCoverFile, v => elmfire.CanopyCoverFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("CanopyHeightFile", () => elmfire.CanopyHeightFile, v => elmfire.CanopyHeightFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("CanopyBaseHeightFile", () => elmfire.CanopyBaseHeightFile, v => elmfire.CanopyBaseHeightFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("CanopyBulkDensityFile", () => elmfire.CanopyBulkDensityFile, v => elmfire.CanopyBulkDensityFile = v,
                filter: FileBrowser.geoTiffFilter);

            bool anyCanopy = !string.IsNullOrEmpty(elmfire.CanopyDatasetFolder)
                             || !string.IsNullOrEmpty(elmfire.CanopyCoverFile)
                             || !string.IsNullOrEmpty(elmfire.CanopyHeightFile)
                             || !string.IsNullOrEmpty(elmfire.CanopyBaseHeightFile)
                             || !string.IsNullOrEmpty(elmfire.CanopyBulkDensityFile);

            if (!anyCanopy)
            {
                //Coloured rather than disabled: this is the default state, and its consequence is one a user
                //would otherwise have to infer from a fire that never crowns.
                Fields.Warn("None set, so canopy is filled with zeros: surface fire only, no crown fire.");
            }
            else
            {
                Fields.Hint("Whichever of the four are not set are filled with zeros. Check CH_TIMES_10 and",
                            "CBH_TIMES_10 under Fire behaviour if these came from LANDFIRE.");
            }

            ImGui.SeparatorText("Buildings");
            Fields.Path("BuildingAreaFile", () => elmfire.BuildingAreaFile, v => elmfire.BuildingAreaFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("BuildingSeparationFile", () => elmfire.BuildingSeparationFile, v => elmfire.BuildingSeparationFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("BuildingNonBurnableFractionFile", () => elmfire.BuildingNonBurnableFractionFile,
                v => elmfire.BuildingNonBurnableFractionFile = v, filter: FileBrowser.geoTiffFilter);
            Fields.Path("BuildingFootprintFractionFile", () => elmfire.BuildingFootprintFractionFile,
                v => elmfire.BuildingFootprintFractionFile = v, filter: FileBrowser.geoTiffFilter);
            Fields.Path("BuildingFuelModelFile", () => elmfire.BuildingFuelModelFile,
                v => elmfire.BuildingFuelModelFile = v, filter: FileBrowser.geoTiffFilter);

            int buildingLayers = 0;
            foreach (string layer in new[]
                     {
                         elmfire.BuildingAreaFile, elmfire.BuildingSeparationFile,
                         elmfire.BuildingNonBurnableFractionFile, elmfire.BuildingFootprintFractionFile,
                         elmfire.BuildingFuelModelFile,
                     })
            {
                if (!string.IsNullOrEmpty(layer)) ++buildingLayers;
            }

            //All five or none: ELMFIRE's building spread model needs the whole set, so four is the state worth
            //flagging - it looks like progress and behaves like nothing.
            if (buildingLayers == 0)
            {
                Fields.Hint("None set, so building-to-building spread is off and the fire burns vegetation only.");
            }
            else if (buildingLayers < 5)
            {
                Fields.Caution($"{buildingLayers} of 5 set. All five are needed to switch building spread on,",
                               "so as it stands these will be ingested and then not used.");
            }
            else
            {
                Fields.Hint("All five set, so USE_BLDG_SPREAD_MODEL comes on. Put building_fuel_models.csv",
                            "beside the case with the case builder's copy step.");
            }

            ImGui.SeparatorText("Other");
            Fields.Path("IgnitionMaskFile", () => elmfire.IgnitionMaskFile, v => elmfire.IgnitionMaskFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Hint("Where ELMFIRE may start its own ignitions. A painted ignition area becomes this",
                        "automatically, so it is only needed for a mask prepared elsewhere.");

            Fields.Path("BarriersFile", () => elmfire.BarriersFile, v => elmfire.BarriersFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Hint("Fuel breaks and other barriers to spread.");

            Fields.Path("SuppressionDifficultyFile",
                () => elmfire.SuppressionDifficultyFile, v => elmfire.SuppressionDifficultyFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Hint("Suppression difficulty index, for the extended attack model. Nothing here produces",
                        "one, so it is a raster you bring; without it USE_SDI cannot be switched on.");

            ImGui.Unindent();
        }

        private static readonly string[] FuelModelStandards =
            Enum.GetNames(typeof(ElmfireInput.FuelModelStandards));

        /// <summary>A fire computed elsewhere, read back from its rasters.</summary>
        private static void DrawAscImport(AscImportInput asc)
        {
            Fields.Path("TimeOfArrivalFile", () => asc.TimeOfArrivalFile, v => asc.TimeOfArrivalFile = v,
                required: true);
            Fields.Path("RateOfSpreadFile", () => asc.RateOfSpreadFile, v => asc.RateOfSpreadFile = v,
                required: true);
            Fields.Path("SpreadDirectionFile", () => asc.SpreadDirectionFile, v => asc.SpreadDirectionFile = v,
                required: true);
            Fields.Path("FirelineIntensityFile", () => asc.FirelineIntensityFile, v => asc.FirelineIntensityFile = v);
            Fields.Hint("The arrival time raster also defines the grid everything is painted on, and the one",
                        "k-PERIL computes a trigger boundary on.");

            Fields.Path("FuelModelFile", () => asc.FuelModelFile, v => asc.FuelModelFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Hint("Display only - an imported fire brings its own behaviour and needs no fuel to spread.",
                        "It is what the output window's fuel model display mode draws. Set automatically for",
                        "an ELMFIRE fire, from the case's own fuel layer.");

            //Cannot be inferred from the raster, and wrong is silent - the fire arrives 60x early or late.
            //Set automatically for an ELMFIRE fire; this is for one imported by hand.
            Fields.Choice("TimeOfArrivalUnits", ref asc.TimeOfArrivalUnits, TimeOfArrivalUnits);
            Fields.Hint("Seconds is the default, and what ELMFIRE writes. Set Minutes for a raster from",
                        "FARSITE, FlamMap or Prometheus. Wrong, the fire arrives 60x early or late - which",
                        "reads as a fire that barely moves, or one already past the town.");
        }
    }
}
