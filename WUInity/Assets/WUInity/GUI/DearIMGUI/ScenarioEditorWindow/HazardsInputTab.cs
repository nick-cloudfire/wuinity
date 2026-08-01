using ImGuiNET;
using System;
using System.Collections.Generic;
using PREACT.Input;
using UnityEngine;
//PREACT.Wildfire is not imported wholesale: it carries the whole spread-model library, and this file
//needs exactly one type out of it.
using IgnitionPointInput = PREACT.Wildfire.IgnitionPointInput;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    internal class HazardsInputTab
    {
        static string[] WildfireModulesStrings;
        static int wildfireModuleIndex = 0;

        static string[] SmokeModulesStrings;
        static int smokeModuleIndex = 0;

        static string[] TriggerBufferModulesStrings;
        static int triggerBufferModuleIndex = 0;

        static HazardsInputTab()
        {
            WildfireModulesStrings = Enum.GetNames(typeof(WildfireModuleInput.WildfireModules));
            SmokeModulesStrings = Enum.GetNames(typeof(SmokeInput.SmokeModules));
            TriggerBufferModulesStrings = Enum.GetNames(typeof(TriggerBufferModuleInput.TriggerBufferModules));
        }


        public static void Draw(PREACTInput input)
        {
            ImGui.SeparatorText("Wildfire spread");

            //Whether a module runs is its Enabled flag, and until now that could only be set when the
            //scenario was created - the combo below chooses which module, which is a different question.
            //So a finished scenario could not be run without its fire, or with a hazard added, without
            //editing the .wui by hand.
            if (ImGui.Checkbox("Simulate wildfire spread", ref input.WildfireModule.Enabled))
            {
                if (!input.WildfireModule.Enabled)
                {
                    //Both of these read the fire: smoke is produced by it, and k-PERIL back-propagates from
                    //its arrival times and asks the wildfire module for the grid to work on. Left enabled
                    //with no fire, the trigger boundary run dereferences a module that was never created.
                    //Turned off here rather than merely warned about, since neither can do anything.
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

            if (input.WildfireModule.Enabled && input.WildfireModule.Module == WildfireModuleInput.WildfireModules.None)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                    "Pick a module below, or the run aborts on \"could not initiate wildfire module\".");
            }

            ImGui.BeginDisabled(!input.WildfireModule.Enabled);

            wildfireModuleIndex = (int)input.WildfireModule.Module;
            ImGui.Combo(nameof(input.WildfireModule), ref wildfireModuleIndex, WildfireModulesStrings, WildfireModulesStrings.Length);
            input.WildfireModule.Module = (WildfireModuleInput.WildfireModules)wildfireModuleIndex;

            //Each module's own settings, inline. There used to be a disabled "Module settings" button here
            //labelled "(edit in the .wui file)", which was accurate and is no longer good enough: selecting
            //a module writes its section, and the section then demands files nothing in the GUI could set.
            if (input.WildfireModule.Module == WildfireModuleInput.WildfireModules.ELMFIRE)
            {
                DrawElmfireInputs(input.WildfireModule.ElmfireInput);
            }
            else if (input.WildfireModule.Module == WildfireModuleInput.WildfireModules.AscImport)
            {
                DrawAscImportInputs(input.WildfireModule.AscImportInput);
            }

            ImGui.EndDisabled();

            ImGui.SeparatorText("Wildfire smoke");

            //Greyed rather than hidden when there is no fire, so it stays visible that the scenario has
            //smoke settings and what is stopping them.
            ImGui.BeginDisabled(!input.WildfireModule.Enabled);
            ImGui.Checkbox("Simulate smoke", ref input.SmokeModule.Enabled);
            if (input.SmokeModule.Module != SmokeInput.SmokeModules.None)
            {
                if (ImGui.Button("Module settings###2"))
                {
                    if (input.SmokeModule.Module == SmokeInput.SmokeModules.GlobalSmoke) { GlobalSmokeInputEditorWindow.Open(input.SmokeModule.GlobalSmokeInput); }
                }
                ImGui.SameLine();
            }
            smokeModuleIndex = (int)input.SmokeModule.Module;
            ImGui.Combo(nameof(input.SmokeModule), ref smokeModuleIndex, SmokeModulesStrings, SmokeModulesStrings.Length);
            input.SmokeModule.Module = (SmokeInput.SmokeModules)smokeModuleIndex;
            ImGui.EndDisabled();
            if (!input.WildfireModule.Enabled)
            {
                ImGui.TextDisabled("Needs the wildfire module: smoke is produced by the fire.");
            }

            //Not under "Wildfire spread" and not disabled with it: the WUI area is what the trigger
            //boundary protects and the ignition area is what an ELMFIRE ensemble draws from, so both are
            //worth painting for a scenario whose fire is imported or not yet switched on.
            ImGui.SeparatorText("Painted fire areas and ignitions");
            if (ImGui.Button("Paint fire areas")) { Editors.FirePaintWindow.Open(); }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The WUI area the trigger boundary protects, the area a fire may start in, "
                    + "and an initial ignition - painted on the fire grid and saved beside the scenario.");
            }
            ImGui.SameLine();
            if (ImGui.Button("New ignition point"))
            {
                Editors.IgnitionPointEditWindow.Open(input.WildfireModule.Data.IgnitionPoints, -1);
            }

            if (string.IsNullOrEmpty(input.WildfireModule.GraphicalFireInputFile))
            {
                ImGui.TextDisabled("Nothing painted yet.");
            }
            else
            {
                ImGui.Text("Painted areas: " + input.WildfireModule.GraphicalFireInputFile);
            }

            List<IgnitionPointInput> ignitions = input.WildfireModule.Data.IgnitionPoints;
            int removeIgnition = -1;
            for (int i = 0; i < ignitions.Count; ++i)
            {
                IgnitionPointInput point = ignitions[i];
                //Its position as the label, since that is the only thing distinguishing one from another -
                //an ignition point has no name.
                if (ImGui.TreeNode($"Ignition {i + 1}: {point.LatLon.x:F5}, {point.LatLon.y:F5}"))
                {
                    if (ImGui.Button($"Edit###ign{i}")) { Editors.IgnitionPointEditWindow.Open(ignitions, i); }
                    ImGui.SameLine();
                    if (ImGui.Button($"Remove###ignrm{i}")) { removeIgnition = i; }

                    ImGui.Text(point.AbsoluteTime
                        ? $"Starts {point.IgnitionDateTime:yyyy-MM-dd HH:mm} ({point.IgnitionTime:F0} s in)"
                        : $"Starts {point.IgnitionTime:F0} s into the simulation");

                    ImGui.TreePop();
                }
            }
            if (removeIgnition >= 0)
            {
                ignitions.RemoveAt(removeIgnition);
                //Or its marker stays on the map, marking an ignition the scenario no longer has.
                PreactGUI.WUInity.RefreshWildfireIgnitionMarkers();
            }

            //Its own section here rather than only in the new scenario window, which is where it used to be
            //set and therefore the only place it could be turned on.
            ImGui.SeparatorText("Trigger boundary");
            ImGui.BeginDisabled(!input.WildfireModule.Enabled);
            ImGui.Checkbox("Compute a trigger boundary", ref input.TriggerBufferModule.Enabled);
            triggerBufferModuleIndex = (int)input.TriggerBufferModule.Module;
            ImGui.Combo(nameof(input.TriggerBufferModule), ref triggerBufferModuleIndex, TriggerBufferModulesStrings, TriggerBufferModulesStrings.Length);
            input.TriggerBufferModule.Module = (TriggerBufferModuleInput.TriggerBufferModules)triggerBufferModuleIndex;

            if (input.TriggerBufferModule.Module == TriggerBufferModuleInput.TriggerBufferModules.kPERIL)
            {
                //A boundary is only produced for an area the fire actually reaches, so the fire's stop time
                //and the trigger boundary are not independent settings. Said where both are visible, because
                //the failure otherwise arrives at the end of a long run as "the fire never reached" - true,
                //and easy to read as a finding about the scenario rather than about the stop time.
                if (input.WildfireModule.Enabled
                    && input.WildfireModule.Module == WildfireModuleInput.WildfireModules.ELMFIRE
                    && input.WildfireModule.ElmfireInput.SimulationTstopSeconds < 24.0 * 3600.0)
                {
                    ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                        $"ELMFIRE stops after {input.WildfireModule.ElmfireInput.SimulationTstopSeconds / 3600.0:F1} h. "
                        + "No boundary is computed for an area");
                    ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                        "the fire never reaches, so a short run can produce none at all.");
                }

                DrawKPerilInputs(input.TriggerBufferModule.kPERILInput);
            }

            ImGui.EndDisabled();
            if (!input.WildfireModule.Enabled)
            {
                ImGui.TextDisabled("Needs the wildfire module: the boundary is back-propagated from the fire's");
                ImGui.TextDisabled("arrival times, on the fire's own grid.");
            }
        }

        /// <summary>
        /// Settings for running ELMFIRE itself.
        ///
        /// Everything here has a workable default, so this is a panel of adjustments rather than a list of
        /// requirements - which is why nothing in it is marked required. What has to exist (the executable,
        /// the case, its rasters) is checked when the run starts, where the paths are resolved.
        /// </summary>
        private static void DrawElmfireInputs(ElmfireInput elmfire)
        {
            ImGui.Indent();

            ImGui.TextWrapped("ELMFIRE computes the whole fire and writes rasters, so it runs once when the "
                + "simulation starts and the fire is read back from its output. The first run takes minutes; "
                + "after that the output is reused.");

            PathField("CaseDirectory", () => elmfire.CaseDirectory, v => elmfire.CaseDirectory = v, false, null);
            PathField("ElmfireExe (empty = the vendored build)", () => elmfire.ElmfireExe,
                v => elmfire.ElmfireExe = v, false, null);
            PathField("NamelistTemplate (empty = the case's elmfire.data)", () => elmfire.NamelistTemplate,
                v => elmfire.NamelistTemplate = v, false, null);
            PathField("PathToGdal", () => elmfire.PathToGdal, v => elmfire.PathToGdal = v, false, null);

            ImGui.Checkbox("Reuse output already in the case folder", ref elmfire.ReuseExistingOutput);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Off means ELMFIRE runs again every time the simulation starts, which is what "
                    + "a changed namelist needs.");
            }

            ImGui.Checkbox("Build what the case is missing", ref elmfire.BuildCase);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Produces only the layers the case does not already have - a DEM if there is "
                    + "none, then slope, aspect, adj/phi, the weather and a namelist. Safe on a prepared case: "
                    + "what is already there is kept.");
            }

            if (elmfire.BuildCase)
            {
                ImGui.Indent();
                ImGui.Checkbox("Rebuild layers the case already has", ref elmfire.RebuildExistingLayers);
                if (elmfire.RebuildExistingLayers)
                {
                    //Worth spelling out rather than leaving to the tooltip: this replaces work that cannot
                    //always be redone. Canopy in particular has no global source, so a rebuild that is not
                    //handed canopy rasters fills them with zeros.
                    ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.3f, 1f),
                        "This replaces the case's rasters, its weather and its namelist.");
                    ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.3f, 1f),
                        "Canopy with no source is refilled with zeros - surface fire only.");
                    ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.3f, 1f),
                        "Only needed when the domain or the cell size changed.");
                }

                float cellSize = (float)elmfire.CellSizeMetres;
                if (ImGui.InputFloat("CellSizeMetres", ref cellSize)) { elmfire.CellSizeMetres = cellSize; }

                float padding = (float)elmfire.PaddingMetres;
                if (ImGui.InputFloat("PaddingMetres", ref padding)) { elmfire.PaddingMetres = padding; }
                ImGui.TextDisabled("Margin beyond the evacuation domain, so the fire is not clipped at its edge.");
                ImGui.TextDisabled("Both only apply to layers actually being built.");
                ImGui.Unindent();
            }

            float tstop = (float)elmfire.SimulationTstopSeconds;
            if (ImGui.InputFloat("SimulationTstopSeconds", ref tstop)) { elmfire.SimulationTstopSeconds = tstop; }
            ImGui.TextDisabled($"{elmfire.SimulationTstopSeconds / 3600.0:F1} hours of fire. Separate from the");
            ImGui.TextDisabled("evacuation's own end time.");

            ImGui.Unindent();
        }

        /// <summary>The imported fire's rasters. Three of the four are required; the run reads them as its fire.</summary>
        private static void DrawAscImportInputs(AscImportInput asc)
        {
            ImGui.Indent();

            PathField("TimeOfArrivalFile", () => asc.TimeOfArrivalFile, v => asc.TimeOfArrivalFile = v, true, null);
            PathField("RateOfSpreadFile", () => asc.RateOfSpreadFile, v => asc.RateOfSpreadFile = v, true, null);
            PathField("SpreadDirectionFile", () => asc.SpreadDirectionFile, v => asc.SpreadDirectionFile = v, true, null);
            PathField("FirelineIntensityFile", () => asc.FirelineIntensityFile, v => asc.FirelineIntensityFile = v, false, null);
            ImGui.TextDisabled("The arrival time raster also defines the grid everything is painted on, and the one");
            ImGui.TextDisabled("k-PERIL computes a trigger boundary on.");

            ImGui.Unindent();
        }

        /// <summary>
        /// k-PERIL's own inputs, for a loaded scenario.
        ///
        /// These existed only in the new-scenario creator, so a scenario that gained a trigger boundary
        /// afterwards - which is the normal way round, since the wind rasters come out of the ELMFIRE case
        /// build - had no way to be given the files it then reported as missing.
        /// </summary>
        private static void DrawKPerilInputs(kPERILInput peril)
        {
            ImGui.Indent();

            //Not marked required: running ELMFIRE fills these in from the case's own ws/wd, which is the
            //arrangement that cannot disagree with the fire.
            PathField("WindSpeedFile", () => peril.WindSpeedFile, v => peril.WindSpeedFile = v, false, FileBrowser.geoTiffFilter);
            PathField("WindDirectionFile", () => peril.WindDirectionFile, v => peril.WindDirectionFile = v, false, FileBrowser.geoTiffFilter);
            if (string.IsNullOrEmpty(peril.WindSpeedFile) || string.IsNullOrEmpty(peril.WindDirectionFile))
            {
                ImGui.TextDisabled("Left empty, the ELMFIRE case's own ws.tif / wd.tif are used - the same wind the");
                ImGui.TextDisabled("fire was computed with. Set them here only to override that.");
            }
            ImGui.TextDisabled("Speed in MILES PER HOUR, direction in degrees, on the fire grid. The unit matters:");
            ImGui.TextDisabled("the speed goes into Anderson's length-to-breadth correlation, defined for mi/h.");

            //Hourly wind is the normal case for an ELMFIRE run, and k-PERIL has no time axis, so which hour
            //it computes on is a choice the scenario should be able to state rather than one made silently.
            int windBand = peril.WindBand;
            if (ImGui.InputInt("WindBand (hour of the run, 1-based)", ref windBand))
            {
                peril.WindBand = windBand < 1 ? 1 : windBand;
            }
            ImGui.TextDisabled("An ELMFIRE case's wind rasters hold one band per hour. k-PERIL computes on a single");
            ImGui.TextDisabled("wind field, so one hour is used and the rest are not; the console says which.");

            ImGui.Checkbox("Compute rate of spread with Behave", ref peril.CalculateROSFromBehave);
            if (peril.CalculateROSFromBehave)
            {
                PathField("InitialFuelMoistureFile", () => peril.InitialFuelMoistureFile,
                    v => peril.InitialFuelMoistureFile = v, true, null);
                PathField("FuelModelsFile (BEHAVE fuel model table)", () => peril.FuelModelsFile,
                    v => peril.FuelModelsFile = v, true, null);
                ImGui.TextDisabled("Required in this mode, and only in it. Both are k-PERIL's own now - they used");
                ImGui.TextDisabled("to be taken from the fire module, which had nothing to do with them.");
            }
            else
            {
                ImGui.TextDisabled("Rate of spread comes from the fire module's own output, which is what an");
                ImGui.TextDisabled("ELMFIRE-driven trigger campaign uses.");
            }

            //Not marked required: the WUI area falls back to what was painted, which is why the paint window
            //exists. Said rather than left to be discovered.
            PathField("WuiAreaFile", () => peril.WuiAreaFile, v => peril.WuiAreaFile = v, false, null);
            ImGui.TextDisabled("The area being protected. Left empty, the painted WUI area is used instead.");

            ImGui.Unindent();
        }

        /// <summary>
        /// A path with a picker, clearable, and typeable - the same shape the landscape bands use.
        ///
        /// The picker returns a path relative to the scenario folder, normalised to forward slashes: it goes
        /// into the .wui, and a backslash is a separator on Windows only.
        /// </summary>
        private static void PathField(string label, Func<string> get, Action<string> set, bool required, string[] filter)
        {
            if (ImGui.Button("Set###seth" + label))
            {
                FileBrowser.OpenSetFilePath(
                    path => set(string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/')),
                    "Select " + label, true, filter);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear###clearh" + label))
            {
                set(string.Empty);
            }
            ImGui.SameLine();

            string value = get() ?? string.Empty;
            if (ImGui.InputText(label, ref value, 256))
            {
                set(value);
            }

            if (required && string.IsNullOrEmpty(value))
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Required: " + label + " is not set.");
            }
        }
    }
}
