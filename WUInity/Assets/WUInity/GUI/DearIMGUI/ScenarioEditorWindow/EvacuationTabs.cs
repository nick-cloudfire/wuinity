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
                ImGui.SeparatorText("Pedestrian");
                _pedestrianModuleIndex = (int)pInput.Module;
                ImGui.Combo(nameof(PedestrianModuleInput.PedestrianModules), ref _pedestrianModuleIndex, PedestrianModulesStrings, PedestrianModulesStrings.Length);
                pInput.Module = (PedestrianModuleInput.PedestrianModules)_pedestrianModuleIndex;
                if (pInput.Module == PedestrianModuleInput.PedestrianModules.MacroHouseholdSim)
                {
                    DrawMacroHouseholdSimSettings(pInput.MacroHouseholdSimInput);
                }

                ImGui.SeparatorText("Traffic");
                _trafficModuleIndex = (int)tInput.Module;
                ImGui.Combo(nameof(TrafficModuleInput.TrafficModules), ref _trafficModuleIndex, TrafficModulesStrings, TrafficModulesStrings.Length);
                tInput.Module = (TrafficModuleInput.TrafficModules)_trafficModuleIndex;
                if (tInput.Module == TrafficModuleInput.TrafficModules.SUMO)
                {
                    DrawSumoSettings(tInput.SumoInput);
                }

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
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Response curves"))
            {
                //No editor for these yet, so the list is read-only apart from removal rather than
                //offering buttons that do nothing. A curve is a set of time/probability points and
                //needs a plot to edit sensibly.
                ImGui.TextDisabled("Response curves are read-only here; edit them in the .wui file.");

                string removeCurve = null;
                foreach (KeyValuePair<string, ResponseCurve> kV in eInput.ResponseCurves)
                {
                    ResponseCurve rC = kV.Value;
                    if (ImGui.TreeNode(rC.Name))
                    {
                        ImGui.Text($"{nameof(rC.TimeInput)}: {rC.TimeInput}");
                        ImGui.Text($"{(rC.DataPoints == null ? 0 : rC.DataPoints.Length)} data points");
                        if (rC.DataPoints != null)
                        {
                            for (int i = 0; i < rC.DataPoints.Length; ++i)
                            {
                                ImGui.Text($"    {rC.DataPoints[i].Time}, {rC.DataPoints[i].Probability}");
                            }
                        }

                        if (ImGui.Button("Remove")) { removeCurve = kV.Key; }

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
                ImGui.TextDisabled("Evacuation groups are read-only here; edit them in the .wui file.");

                string removeGroup = null;
                foreach (KeyValuePair<string, EvacuationGroupInput> kV in eInput.EvacuationGroupInputs)
                {
                    EvacuationGroupInput eGI = kV.Value;
                    if (ImGui.TreeNode(eGI.Name))
                    {
                        ImGui.Text($"{nameof(eGI.ShapeFile)}: {eGI.ShapeFile}");
                        ImGui.Text($"{nameof(eGI.Demographics)}: {eGI.Demographics}");
                        ImGui.Text($"{nameof(eGI.Destinations)}: {(eGI.Destinations == null ? 0 : eGI.Destinations.Count)}");
                        ImGui.Text($"{nameof(eGI.DestinationChoice)}: {eGI.DestinationChoice}");
                        ImGui.Text($"{nameof(eGI.Default)}: {eGI.Default}");

                        if (ImGui.Button("Remove")) { removeGroup = kV.Key; }

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
                FileBrowser.OpenSetFilePath(path => input.ConfigurationFile = path, "Select SUMO configuration file", true);
            }
            ImGui.InputText(nameof(input.ConfigurationFile), ref input.ConfigurationFile, 256);

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
    }
}
