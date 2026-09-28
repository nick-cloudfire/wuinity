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
                    DrawElmfire(input, input.WildfireModule.ElmfireInput);
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
        private static void DrawElmfire(PREACTInput input, ElmfireInput elmfire)
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
                Fields.Hint($"{elmfire.SimulationTstopHours:F1} hours of fire for a run of this scenario ({elmfire.TstopSeconds():F0} s",
                            "to ELMFIRE, which wants seconds); 1 to 240 h. A case whose weather is shorter is given",
                            "more when the run starts (one WindNinja solve per extra hour), unless the scenario runs",
                            "its own NamelistTemplate. Separate from the evacuation's end time, and from a trigger",
                            $"campaign's fire duration, which the campaign window sets ({PREACT.Utility.CampaignLayout.DefaultFireHours:0} h by default).");

                Fields.Check("Reuse output already in the case folder", ref elmfire.ReuseExistingOutput,
                    "Off means ELMFIRE runs again every time the simulation starts, which is what changed "
                    + "settings need.");

                ImGui.Unindent();
            }

            if (ImGui.CollapsingHeader("Building the case"))
            {
                ImGui.Indent();

                Fields.Check("Build the case before each run", ref elmfire.BuildCase,
                    "Before every run, builds the layers the case does not have yet - a DEM if there is none, then "
                    + "slope, aspect, adj/phi and the weather - and writes the namelist again from these settings. "
                    + "Layers the case has are kept, unless its dem.tif no longer covers the domain; then the grid is "
                    + "re-cut and every layer carried onto it (the old ones are kept in inputs/_previous_grid).");

                if (elmfire.BuildCase)
                {
                    ImGui.Indent();

                    Fields.Check("Rebuild layers the case already has", ref elmfire.RebuildExistingLayers);
                    if (elmfire.RebuildExistingLayers)
                    {
                        //Worth spelling out rather than leaving to a tooltip: this replaces work that cannot
                        //always be redone. Canopy in particular has no global source, so a rebuild that is not
                        //handed canopy rasters fills them with zeros.
                        Fields.Caution("This replaces the case's rasters and its weather.",
                                       "Canopy with no source is refilled with zeros - surface fire only.",
                                       "Only needed when the domain or the cell size changed.");
                    }

                    //Not a kept layer: the builder writes the namelist on every build, so the Fire behaviour settings
                    //always reach it - and an elmfire.data edited by hand is replaced (kept aside, not destroyed).
                    Fields.Hint("The namelist is not kept like the rasters: every build writes elmfire.data again",
                                "from the Fire behaviour settings. One edited by hand is set aside first as",
                                "elmfire.data.kept-<time>; to run one as it is, name it as NamelistTemplate above.");

                    Fields.Real("CellSizeMetres", ref elmfire.CellSizeMetres);
                    Fields.Real("PaddingMetres", ref elmfire.PaddingMetres);
                    Fields.Hint("Margin beyond the evacuation domain, so the fire is not clipped at its edge.",
                                "Both only apply to layers actually being built.");

                    ImGui.Unindent();
                }

                Fields.Hint("The same build runs from the workflow (step 5) and Data > Build fire case, where it is",
                            "done once rather than as a multi-minute side effect of starting a run.");

                ImGui.Unindent();
            }

            DrawSourceLayers(input);
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
            //Read from the cached probe, never probed from here: WindNinja's search is a recursive walk of its
            //install roots, and this used to run it on every frame the tab was open.
            global::WUInity.Workflow.ExternalToolsSnapshot tools = ToolsService.Current;
            if (!tools.Probed)
            {
                ImGui.TextDisabled(ToolsService.Probing ? "Looking for ELMFIRE, GDAL and WindNinja..." : "Tools not probed yet.");
                return;
            }

            Tool("ELMFIRE", tools.ElmfireExe, elmfire.ElmfireExe,
                "the vendored build under ThirdParty/elmfire",
                "No elmfire.exe found. The run cannot compute a fire.");

            Tool("GDAL", tools.GdalBin, elmfire.PathToGdal,
                "PATH, then a QGIS or OSGeo4W install",
                "No GDAL tools found. ELMFIRE shells out to gdal_translate and gdalinfo, and fails its own "
                + "DEM check without them - reporting a problem with the DEM rather than with GDAL.");

            Tool("WindNinja", tools.WindNinjaExe, elmfire.WindNinjaExe,
                "WINDNINJA_CLI, PATH, then the installer's locations",
                "No WindNinja found. The case gets one wind value for the whole domain, so the trigger "
                + "boundary comes out circular instead of wind-driven.");

            if (ImGui.SmallButton(ToolsService.Probing ? "Looking...###ToolsRefresh" : "Look again###ToolsRefresh"))
            {
                ToolsService.Refresh();
            }
            Fields.Hint("The tools are looked for when the application starts and when a scenario is opened.",
                        "Look again after installing one. Help > External tools and keys lists them all.");
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
            string root = ScenarioSession.RootFolder;
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

            string source = !string.IsNullOrEmpty(named) && GuiFiles.Exists(named) ? named
                          : (GuiFiles.Exists(generated) ? generated : null);

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
                ScenarioSession.NotifyEdited("namelist read from the case");
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

        /// <summary>The source layers, drawn by the panel that owns them (Data &gt; Fuels, canopy and buildings).</summary>
        private static void DrawSourceLayers(PREACTInput input)
        {
            if (!ImGui.CollapsingHeader("Source layers (fuel, canopy, buildings)"))
            {
                return;
            }

            ImGui.Indent();
            SourceLayersPanel.DrawContents(input);
            ImGui.Unindent();
        }

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
            Fields.Hint("Display only - an imported fire brings its own behaviour and needs no fuel to spread.");

            //Cannot be inferred from the raster, and wrong is silent - the fire arrives 60x early or late.
            //Set automatically for an ELMFIRE fire; this is for one imported by hand.
            Fields.Choice("TimeOfArrivalUnits", ref asc.TimeOfArrivalUnits, TimeOfArrivalUnits);
            Fields.Hint("Seconds is the default, and what ELMFIRE writes. Set Minutes for a raster from",
                        "FARSITE, FlamMap or Prometheus. Wrong, the fire arrives 60x early or late - which",
                        "reads as a fire that barely moves, or one already past the town.");
        }
    }
}
