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

        public static void Draw(PREACTInput input, EvacuationInput eInput, PedestrianModuleInput pInput, TrafficModuleInput tInput)
        {
            if (ImGui.BeginTabItem("General"))
            {
                ImGui.Checkbox(nameof(eInput.UseTriggerBufferEvacuation), ref eInput.UseTriggerBufferEvacuation);
                if(eInput.UseTriggerBufferEvacuation)
                {
                    if (ImGui.Button("Select trigger buffer file"))
                    {
                        FileBrowser.OpenSetFilePath(path => eInput.TriggerBufferFile = path, "Select trigger buffer file", true);
                    }
                    ImGui.InputText(nameof(eInput.TriggerBufferFile), ref eInput.TriggerBufferFile, 256);
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Evacuation modules"))
            {
                //Whether each module runs, which the combo underneath does not decide: the combo says which
                //module, and the engine creates one only if Enabled is set. These flags could previously
                //only be set when the scenario was created.
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

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Population"))
            {
                if (ImGui.Button("Select population file"))
                {
                    FileBrowser.OpenSetFilePath(path => input.Population.PopulationFile = path, "Select population file", true);
                }
                ImGui.InputText(nameof(input.Population.PopulationFile), ref input.Population.PopulationFile, 256);
                ImGui.Checkbox(nameof(input.Population.CullOutsideGroups), ref input.Population.CullOutsideGroups);

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Demographics"))
            {
                if (ImGui.Button("New demographics")) { DemographicsInputEditorWindow.Open(input.Population.Demographics, null); }

                //Collected and applied after the loop: removing from a dictionary while enumerating
                //it throws, so doing it inline would take the window down on the click.
                string removeDemographics = null;
                foreach (KeyValuePair<string, DemographicsInput> kV in input.Population.Demographics)
                {
                    DemographicsInput demo = kV.Value;
                    if (ImGui.TreeNode(demo.Name))
                    {
                        if (ImGui.Button("Edit")) { DemographicsInputEditorWindow.Open(input.Population.Demographics, demo); }
                        ImGui.SameLine();
                        if (ImGui.Button("Remove")) { removeDemographics = kV.Key; }

                        ImGui.TreePop();
                    }
                }
                if (removeDemographics != null)
                {
                    input.Population.Demographics.Remove(removeDemographics);
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Destinations"))
            {
                if (ImGui.Button("New destination")) { DestinationInputEditWindow.Open(eInput.EvacuationDestinationInputs, null); }

                string removeDestination = null;
                foreach (KeyValuePair<string, EvacuationDestinationInput> kV in eInput.EvacuationDestinationInputs)
                {
                    EvacuationDestinationInput dest = kV.Value;
                    if (ImGui.TreeNode(dest.Name))
                    {
                        if (ImGui.Button("Edit")) { DestinationInputEditWindow.Open(eInput.EvacuationDestinationInputs, dest); }
                        ImGui.SameLine();
                        if (ImGui.Button("Remove")) { removeDestination = kV.Key; }

                        //Where it is, since there is no way to move the camera to it yet.
                        ImGui.Text($"{nameof(dest.LatLon)}: {dest.LatLon.x}, {dest.LatLon.y}   {nameof(dest.Type)}: {dest.Type}");

                        ImGui.TreePop();
                    }
                }
                if (removeDestination != null)
                {
                    eInput.EvacuationDestinationInputs.Remove(removeDestination);
                    //Or its marker stays on the map, marking a destination the scenario no longer has.
                    PreactGUI.WUInity.RefreshDestinationMarkers();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Response curves"))
            {
                if (ImGui.Button("New response curve")) { ResponseCurveEditWindow.Open(eInput.ResponseCurves, null); }

                string removeCurve = null;
                foreach (KeyValuePair<string, ResponseCurve> kV in eInput.ResponseCurves)
                {
                    ResponseCurve rC = kV.Value;
                    if (ImGui.TreeNode(rC.Name))
                    {
                        //Passed by value, which is what the editor wants: ResponseCurve is a struct,
                        //so it commits a whole new value back into the dictionary rather than
                        //mutating this copy.
                        if (ImGui.Button("Edit")) { ResponseCurveEditWindow.Open(eInput.ResponseCurves, rC); }
                        ImGui.SameLine();
                        if (ImGui.Button("Remove")) { removeCurve = kV.Key; }

                        ImGui.Text($"{nameof(rC.TimeInput)}: {rC.TimeInput}, {(rC.DataPoints == null ? 0 : rC.DataPoints.Length)} points");

                        ImGui.TreePop();
                    }
                }
                if (removeCurve != null)
                {
                    eInput.ResponseCurves.Remove(removeCurve);
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Evacuation groups"))
            {
                if (ImGui.Button("New evacuation group")) { EvacuationGroupEditWindow.Open(eInput.EvacuationGroupInputs, null); }
                ImGui.SameLine();
                if (ImGui.Button("Paint group areas")) { EvacuationGroupPaintWindow.Open(eInput.EvacuationGroupInputs); }

                string removeGroup = null;
                foreach (KeyValuePair<string, EvacuationGroupInput> kV in eInput.EvacuationGroupInputs)
                {
                    EvacuationGroupInput eGI = kV.Value;
                    if (ImGui.TreeNode(eGI.Name))
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
                }
                if (removeGroup != null)
                {
                    eInput.EvacuationGroupInputs.Remove(removeGroup);
                }

                ImGui.EndTabItem();
            }
        }

        /// <summary>
        /// Inlined rather than hidden behind a "Module settings" button. There are three values, so
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
        }

        private static void DrawSumoSettings(SUMOInput input)
        {
            if (input == null)
            {
                return;
            }

            if (ImGui.Button("Select SUMO configuration file"))
            {
                FileBrowser.OpenSetFilePath(path => SetSumoConfiguration(input, path), "Select SUMO configuration file (.sumocfg)", true);
            }
            ImGui.InputText(nameof(input.ConfigurationFile), ref input.ConfigurationFile, 256);

            //Checked as it stands, not only as it is picked: the field is editable text and scenarios exist
            //that already hold the wrong file - which is how a run fails with "could not load configuration".
            string root = ScenarioEditorWindow.HasInput ? ScenarioEditorWindow.Input.RootFolder : string.Empty;
            bool usable = PREACT.Utility.SumoConfigurationLocator.IsConfiguration(input.ConfigurationFile)
                          && System.IO.File.Exists(System.IO.Path.Combine(root, input.ConfigurationFile ?? string.Empty));

            if (!usable)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                    "SUMO cannot start on this. It has to be the .sumocfg netconvert wrote,");
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                    "usually sumo/osm.sumocfg - not the OSM extract it was built from.");

                string found = PREACT.Utility.SumoConfigurationLocator.FindInScenario(root, true);
                if (found != null)
                {
                    string relative = found.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)
                        ? found.Substring(root.Length).TrimStart('\\', '/')
                        : found;

                    if (ImGui.Button("Use " + relative))
                    {
                        //Relative, like everything else the scenario generates, so the folder stays portable.
                        input.ConfigurationFile = relative;
                    }
                }
                else
                {
                    ImGui.TextDisabled("No .sumocfg in the scenario folder either - build the SUMO network first.");
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
            if (!LooksLikeSumoConfiguration(path))
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
