using Assets.WUInity.GUI.DearIMGUI.Editors;
using ImGuiNET;
using PREACT;
using PREACT.Evacuation;
using PREACT.Input;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class EvacuationTabs
    {
        public static string[] TrafficModulesStrings;
        public static string[] PedestrianModulesStrings;
        static int _pedestrianModuleIndex, _trafficModuleIndex;



        static EvacuationTabs()
        {
            PedestrianModulesStrings = Enum.GetNames(typeof(PedestrianModuleInput.PedestrianModules));
            TrafficModulesStrings = Enum.GetNames(typeof(TrafficModuleInput.TrafficModules));
        }

        /// <summary>
        /// The evacuation sub-tabs, bar included.
        /// </summary>
        /// <remarks>
        /// The bar is opened here rather than by the caller so that every top-level tab has the same shape:
        /// the parent creates the tab item and the child owns whatever is inside it. Two of the three used to
        /// have their bar opened by <see cref="ScenarioEditorWindow"/> and one by itself, which is the kind of
        /// difference that makes adding a tab a guess.
        /// </remarks>
        public static void Draw(PREACTInput input, EvacuationInput eInput, PedestrianModuleInput pInput, TrafficModuleInput tInput)
        {
            if (!ImGui.BeginTabBar("EvacuationBar"))
            {
                return;
            }

            DrawTabs(input, eInput, pInput, tInput);

            ImGui.EndTabBar();
        }

        private static void DrawTabs(PREACTInput input, EvacuationInput eInput, PedestrianModuleInput pInput, TrafficModuleInput tInput)
        {
            if (ImGui.BeginTabItem("Modules"))
            {
                DrawModules(input);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Population"))
            {
                DrawPopulation(input);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Destinations"))
            {
                DrawDestinations(input);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Response curves"))
            {
                DrawResponseCurves(input);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Demographics"))
            {
                DrawDemographics(input);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Evacuation groups"))
            {
                DrawGroups(input);
                ImGui.EndTabItem();
            }
        }

        /// <summary>Whether the pedestrian and traffic modules run, and their settings.</summary>
        public static void DrawModules(PREACTInput input)
        {
            PedestrianModuleInput pInput = input.PedestrianModule;
            TrafficModuleInput tInput = input.TrafficModule;

            //Whether each module runs, which the combo underneath does not decide: the combo says which
            //module, and the engine creates one only if Enabled is set.
            ImGui.SeparatorText("Pedestrian");
            ImGui.Checkbox("Simulate pedestrians (households leaving their homes)", ref pInput.Enabled);
            ImGui.BeginDisabled(!pInput.Enabled);
            _pedestrianModuleIndex = (int)pInput.Module;
            ImGui.Combo(nameof(PedestrianModuleInput.PedestrianModules), ref _pedestrianModuleIndex, PedestrianModulesStrings, PedestrianModulesStrings.Length);
            pInput.Module = (PedestrianModuleInput.PedestrianModules)_pedestrianModuleIndex;
            if (pInput.Module == PedestrianModuleInput.PedestrianModules.MacroHouseholdSim)
            {
                DrawMacroHouseholdSimSettings(pInput.MacroHouseholdSimInput);
            }
            ImGui.EndDisabled();
            if (!pInput.Enabled)
            {
                //Said plainly, because this is the flag that decides whether the population file is read
                //at all - PopulationData.LoadAll only loads households when the pedestrian module is on.
                ImGui.TextDisabled("With this off, the population file is not read and nobody evacuates.");
            }

            ImGui.SeparatorText("Traffic");
            ImGui.Checkbox("Simulate traffic (vehicles on the road network)", ref tInput.Enabled);
            ImGui.BeginDisabled(!tInput.Enabled);
            _trafficModuleIndex = (int)tInput.Module;
            ImGui.Combo(nameof(TrafficModuleInput.TrafficModules), ref _trafficModuleIndex, TrafficModulesStrings, TrafficModulesStrings.Length);
            tInput.Module = (TrafficModuleInput.TrafficModules)_trafficModuleIndex;
            if (tInput.Module == TrafficModuleInput.TrafficModules.SUMO)
            {
                DrawSumoSettings(tInput.SumoInput);
            }
            ImGui.EndDisabled();
        }

        public static void DrawPopulation(PREACTInput input)
        {
            Fields.Path(nameof(input.Population.PopulationFile), () => input.Population.PopulationFile,
                v => input.Population.PopulationFile = v);
            Fields.Hint("The household CSV the pedestrian module reads. Data > Population makes one from WorldPop",
                        "and the RouterDb; any file in the same format will do.");
            ImGui.Checkbox(nameof(input.Population.CullOutsideGroups), ref input.Population.CullOutsideGroups);
            Fields.Hint("Drop households outside every evacuation group's area.");

            ImGui.SeparatorText("Generating households");
            ImGui.SetNextItemWidth(120f);
            ImGui.InputInt("Min household size", ref ScenarioDataSteps.MinHouseholdSize);
            ImGui.SetNextItemWidth(120f);
            ImGui.InputInt("Max household size", ref ScenarioDataSteps.MaxHouseholdSize);
            if (ScenarioDataSteps.MinHouseholdSize < 1) ScenarioDataSteps.MinHouseholdSize = 1;
            if (ScenarioDataSteps.MaxHouseholdSize < ScenarioDataSteps.MinHouseholdSize) ScenarioDataSteps.MaxHouseholdSize = ScenarioDataSteps.MinHouseholdSize;
            Fields.Hint("Used the next time households are generated (Data > Population).");
        }

        public static void DrawDemographics(PREACTInput input)
        {
            if (ImGui.Button("New demographics")) { DemographicsInputEditorWindow.Open(input.Population.Demographics, null); }

            //Collected and applied after the loop: removing from a dictionary while enumerating
            //it throws, so doing it inline would take the window down on the click.
            string removeDemographics = null;
            foreach (KeyValuePair<string, DemographicsInput> kV in input.Population.Demographics)
            {
                DemographicsInput demo = kV.Value;
                //Keyed by the dictionary key, so two entries showing the same name still get their own IDs.
                ImGui.PushID("demo" + kV.Key);
                if (ImGui.TreeNode(demo.Name + (demo.Default ? "  (default)" : "") + "###node"))
                {
                    if (ImGui.Button("Edit")) { DemographicsInputEditorWindow.Open(input.Population.Demographics, demo); }
                    ImGui.SameLine();
                    if (ImGui.Button("Remove")) { removeDemographics = kV.Key; }
                    ImGui.TextDisabled($"Up to {demo.MaxCars} car(s), {demo.MaxCarsProbability:P0} of households take more than one.");

                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            if (removeDemographics != null)
            {
                input.Population.Demographics.Remove(removeDemographics);
                ScenarioSession.NotifyEdited("demographics removed");
            }
        }

        public static void DrawDestinations(PREACTInput input)
        {
            EvacuationInput eInput = input.Evacuation;
            if (ImGui.Button("New destination")) { DestinationInputEditWindow.Open(eInput.EvacuationDestinationInputs, null); }
            ImGui.SameLine();
            if (ImGui.Button("Show road network")) { PreactGUI.WUInity.ShowRoadNetwork(true); }

            string removeDestination = null;
            foreach (KeyValuePair<string, EvacuationDestinationInput> kV in eInput.EvacuationDestinationInputs)
            {
                EvacuationDestinationInput dest = kV.Value;
                ImGui.PushID("dest" + kV.Key);
                if (ImGui.TreeNode(dest.Name + "###node"))
                {
                    if (ImGui.Button("Edit")) { DestinationInputEditWindow.Open(eInput.EvacuationDestinationInputs, dest); }
                    ImGui.SameLine();
                    if (ImGui.Button("Remove")) { removeDestination = kV.Key; }

                    //Where it is, since there is no way to move the camera to it yet.
                    ImGui.Text($"{nameof(dest.LatLon)}: {dest.LatLon.x:F5}, {dest.LatLon.y:F5}   {nameof(dest.Type)}: {dest.Type}");

                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            if (removeDestination != null)
            {
                eInput.EvacuationDestinationInputs.Remove(removeDestination);
                //Or its marker stays on the map, marking a destination the scenario no longer has.
                PreactGUI.WUInity.RefreshDestinationMarkers();
                ScenarioSession.NotifyEdited("destination removed");
            }
        }

        public static void DrawResponseCurves(PREACTInput input)
        {
            EvacuationInput eInput = input.Evacuation;
            if (ImGui.Button("New response curve")) { ResponseCurveEditWindow.Open(eInput.ResponseCurves, null); }

            string removeCurve = null;
            foreach (KeyValuePair<string, ResponseCurve> kV in eInput.ResponseCurves)
            {
                ResponseCurve rC = kV.Value;
                ImGui.PushID("curve" + kV.Key);
                if (ImGui.TreeNode(rC.Name + "###node"))
                {
                    //Passed by value, which is what the editor wants: ResponseCurve is a struct, so it commits a
                    //whole new value back into the dictionary rather than mutating this copy.
                    if (ImGui.Button("Edit")) { ResponseCurveEditWindow.Open(eInput.ResponseCurves, rC); }
                    ImGui.SameLine();
                    if (ImGui.Button("Remove")) { removeCurve = kV.Key; }

                    ImGui.Text($"{nameof(rC.TimeInput)}: {rC.TimeInput}, {(rC.DataPoints == null ? 0 : rC.DataPoints.Length)} points");

                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            if (removeCurve != null)
            {
                eInput.ResponseCurves.Remove(removeCurve);
                ScenarioSession.NotifyEdited("response curve removed");
            }
        }

        public static void DrawGroups(PREACTInput input)
        {
            EvacuationInput eInput = input.Evacuation;
            if (ImGui.Button("New evacuation group")) { EvacuationGroupEditWindow.Open(eInput.EvacuationGroupInputs, null); }
            ImGui.SameLine();
            if (ImGui.Button("Paint group areas")) { EvacuationGroupPaintWindow.Open(eInput.EvacuationGroupInputs); }

            string removeGroup = null;
            foreach (KeyValuePair<string, EvacuationGroupInput> kV in eInput.EvacuationGroupInputs)
            {
                EvacuationGroupInput eGI = kV.Value;
                ImGui.PushID("group" + kV.Key);
                if (ImGui.TreeNode(eGI.Name + "###node"))
                {
                    if (ImGui.Button("Edit")) { EvacuationGroupEditWindow.Open(eInput.EvacuationGroupInputs, eGI); }
                    ImGui.SameLine();
                    if (ImGui.Button("Remove")) { removeGroup = kV.Key; }

                    //Says which of the two actually defines the area, since the mask wins.
                    if (!string.IsNullOrEmpty(eGI.MaskFile))
                    {
                        ImGui.Text($"{nameof(eGI.MaskFile)}: {eGI.MaskFile} (painted)");
                    }
                    else
                    {
                        ImGui.Text($"{nameof(eGI.ShapeFile)}: {eGI.ShapeFile}");
                    }
                    ImGui.Text($"{nameof(eGI.Demographics)}: {eGI.Demographics}");
                    ImGui.Text($"{nameof(eGI.Destinations)}: {(eGI.Destinations == null ? 0 : eGI.Destinations.Count)}");
                    ImGui.Text($"{nameof(eGI.DestinationChoice)}: {eGI.DestinationChoice}");
                    ImGui.Text($"{nameof(eGI.Default)}: {eGI.Default}");

                    ImGui.TreePop();
                }
                ImGui.PopID();
            }
            if (removeGroup != null)
            {
                eInput.EvacuationGroupInputs.Remove(removeGroup);
                ScenarioSession.NotifyEdited("evacuation group removed");
            }
        }

        /// <summary>
        /// Inlined rather than hidden behind a "Module settings" button. There are a handful of values, so
        /// a separate window would be more clicks for less information - and the button it replaces
        /// had an empty body, which is why module settings appeared to do nothing.
        /// </summary>
        private static void DrawMacroHouseholdSimSettings(MacroHouseholdSimInput input)
        {
            if (input == null)
            {
                return;
            }

            //WalkingSpeedMinMax is a System.Numerics.Vector2, while ImGui here works in
            //UnityEngine.Vector2, so it is converted both ways rather than passed by reference.
            Vector2 speedMinMax = new Vector2(input.WalkingSpeedMinMax.X, input.WalkingSpeedMinMax.Y);
            if (ImGui.InputFloat2(nameof(input.WalkingSpeedMinMax), ref speedMinMax))
            {
                input.WalkingSpeedMinMax = new System.Numerics.Vector2(speedMinMax.x, speedMinMax.y);
            }
            ImGui.InputFloat(nameof(input.WalkingSpeedModifier), ref input.WalkingSpeedModifier);
            ImGui.InputFloat(nameof(input.WalkingDistanceModifier), ref input.WalkingDistanceModifier);

            ImGui.SeparatorText("Reacting to the fire");
            ImGui.Checkbox("Leave early when the fire comes close###" + nameof(input.ReactToFire), ref input.ReactToFire);
            ImGui.BeginDisabled(!input.ReactToFire);
            ImGui.SetNextItemWidth(140f);
            ImGui.InputFloat("Distance to the fire front (m)###" + nameof(input.FireReactionDistance), ref input.FireReactionDistance, 50f, 250f, "%.0f");
            if (input.FireReactionDistance < 0f) input.FireReactionDistance = 0f;
            ImGui.SetNextItemWidth(140f);
            ImGui.InputFloat("Look at the front every (s)###" + nameof(input.FireReactionUpdateInterval), ref input.FireReactionUpdateInterval, 60f, 300f, "%.0f");
            if (input.FireReactionUpdateInterval < 1f) input.FireReactionUpdateInterval = 1f;
            ImGui.EndDisabled();
            Fields.Hint("A household that has not left yet leaves as soon as the burning front is this close to its home,",
                        "instead of waiting for its response time; one whose response is \"never\" still stays. Needs a",
                        "fire module. The distance to the front is recomputed at the interval (it costs one pass over",
                        $"the fire grid). Defaults: {(MacroHouseholdDefaults.ReactToFire ? "on" : "off")}, "
                        + $"{MacroHouseholdDefaults.FireReactionDistance:0} m, every {MacroHouseholdDefaults.FireReactionUpdateInterval:0} s.");
            if (ImGui.SmallButton("Reset to the defaults###FireReactionDefaults"))
            {
                input.ReactToFire = MacroHouseholdDefaults.ReactToFire;
                input.FireReactionDistance = MacroHouseholdDefaults.FireReactionDistance;
                input.FireReactionUpdateInterval = MacroHouseholdDefaults.FireReactionUpdateInterval;
            }
        }

        //The engine's own defaults, so the hint and the reset cannot drift from what a .wui without the keys gets.
        private static readonly MacroHouseholdSimInput MacroHouseholdDefaults = new MacroHouseholdSimInput();

        //The last on-request search for a .sumocfg, and the folder it searched.
        private static string _sumoSearchRoot, _sumoFound;

        private static void DrawSumoSettings(SUMOInput input)
        {
            if (input == null)
            {
                return;
            }

            Fields.Path(nameof(input.ConfigurationFile), () => input.ConfigurationFile,
                v => SetSumoConfiguration(input, v));

            //Checked as it stands, not only as it is picked: the field is editable text and scenarios exist
            //that already hold the wrong file - which is how a run fails with "could not load configuration".
            string root = ScenarioSession.RootFolder ?? string.Empty;
            bool usable = PREACT.Utility.SumoConfigurationLocator.IsConfiguration(input.ConfigurationFile)
                          && GuiFiles.Exists(GuiFiles.Resolve(root, input.ConfigurationFile));

            if (!usable)
            {
                ImGui.TextColored(Fields.Warning,
                    "SUMO cannot start on this. It has to be the .sumocfg netconvert wrote,");
                ImGui.TextColored(Fields.Warning,
                    "usually sumo/osm.sumocfg - not the OSM extract it was built from.");

                //Searched on request, not every frame: FindInScenario walks the whole scenario folder, and on
                //a case with a campaign behind it that is hundreds of realization folders.
                if (_sumoSearchRoot != root)
                {
                    _sumoSearchRoot = null;
                    _sumoFound = null;
                }

                if (_sumoSearchRoot == null)
                {
                    if (ImGui.Button("Look for a .sumocfg in the scenario folder"))
                    {
                        _sumoSearchRoot = root;
                        _sumoFound = PREACT.Utility.SumoConfigurationLocator.FindInScenario(root, true);
                    }
                }
                else if (_sumoFound != null)
                {
                    string relative = _sumoFound.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)
                        ? _sumoFound.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
                        : _sumoFound.Replace('\\', '/');

                    if (ImGui.Button("Use " + relative))
                    {
                        //Relative, like everything else the scenario generates, so the folder stays portable.
                        input.ConfigurationFile = relative;
                        _sumoSearchRoot = null;
                    }
                }
                else
                {
                    ImGui.TextDisabled("No .sumocfg in the scenario folder either - build the road network first.");
                }
            }

            //Stored as a double, which ImGui has no two-way binding for here, so it goes through a
            //float and is only written back when actually changed.
            float outputRasterSize = (float)input.OutputRasterSize;
            if (ImGui.InputFloat(nameof(input.OutputRasterSize), ref outputRasterSize))
            {
                input.OutputRasterSize = outputRasterSize;
            }

            ImGui.InputFloat(nameof(input.SmokeAlpha), ref input.SmokeAlpha);
            ImGui.InputFloat(nameof(input.SmokeBeta), ref input.SmokeBeta);
        }

        /// <summary>
        /// Whether a path is the kind of file SUMO can be started on. Empty counts as fine: a scenario
        /// without vehicle evacuation has nothing to complain about. Defers to the locator the engine uses,
        /// so the pickers accept exactly what a run will accept.
        /// </summary>
        public static bool LooksLikeSumoConfiguration(string path)
        {
            return string.IsNullOrWhiteSpace(path) || PREACT.Utility.SumoConfigurationLocator.IsSumoFile(path);
        }

        /// <summary>
        /// Takes a picked path, saying so when it is not a SUMO configuration rather than storing it and
        /// leaving the failure for the first run.
        /// </summary>
        private static void SetSumoConfiguration(SUMOInput input, string path)
        {
            //Typing goes straight through, character by character, since a half-typed name is not a verdict;
            //a picked file that is plainly not a SUMO file is refused.
            if (!string.IsNullOrEmpty(path) && path.Length > 4 && System.IO.Path.HasExtension(path)
                && !LooksLikeSumoConfiguration(path) && System.IO.File.Exists(GuiFiles.Resolve(ScenarioSession.RootFolder, path)))
            {
                Engine.Message(null, Engine.LogType.Warning,
                    System.IO.Path.GetFileName(path) + " is not a SUMO configuration. SUMO needs the .sumocfg "
                    + "netconvert wrote, or a .net.xml. An OSM extract is what the network is built from, not the "
                    + "network itself.");
                return;
            }

            input.ConfigurationFile = path;
        }
    }
}
