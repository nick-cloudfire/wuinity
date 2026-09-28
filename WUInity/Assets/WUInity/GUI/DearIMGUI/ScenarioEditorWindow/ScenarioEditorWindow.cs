using Assets.WUInity.GUI.DearIMGUI.Input;
using ImGuiNET;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Scenario &gt; All settings: every setting of the open scenario in one tabbed window, for when the
    /// workflow's one-page-per-step windows are not the quickest way in.
    /// </summary>
    /// <remarks>
    /// Was the "Scenario editor", which opened itself on every load and every Save as, floated (it could not
    /// be docked), and held the Run tab - which closed the window it was in when a run started. Running is the
    /// Run simulation window now, and this opens only when asked. The pages are the same drawers the workflow's
    /// settings windows use, so there is one control per setting whichever way it is reached.
    /// </remarks>
    public static class ScenarioEditorWindow
    {
        private static bool _isOpen;

        /// <summary>The current scenario. Kept for the windows that still read it from here (the campaign
        /// window); it is <see cref="ScenarioSession.Input"/>.</summary>
        public static PREACT.Input.PREACTInput Input { get => ScenarioSession.Input; }
        public static bool HasInput { get => ScenarioSession.HasInput; }

        public static bool IsOpen { get => _isOpen; }

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
        }

        public static void Close()
        {
            if (_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
            _isOpen = false;
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(640f, 680f));
            if (ImGui.Begin("All settings###AllSettings", ref _isOpen, PreactGUI.ToolWindowFlags))
            {
                PREACT.Input.PREACTInput input = ScenarioSession.Input;
                if (input == null)
                {
                    ImGui.TextDisabled("No scenario is open.");
                }
                else
                {
                    if (ScenarioSession.EditingLocked)
                    {
                        ImGui.TextDisabled("Read-only while " + ScenarioSession.BusyReason + ".");
                    }

                    //Read-only while a run or a data step is using the scenario: they read it from another thread.
                    ImGui.BeginDisabled(ScenarioSession.EditingLocked);
                    if (ImGui.BeginTabBar("AllSettingsTabs"))
                    {
                        if (ImGui.BeginTabItem("Scenario###AllScenario"))
                        {
                            SimulationTabs.Draw(input);
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Evacuation###AllEvacuation"))
                        {
                            EvacuationTabs.Draw(input, input.Evacuation, input.PedestrianModule, input.TrafficModule);
                            ImGui.EndTabItem();
                        }

                        if (ImGui.BeginTabItem("Fire###AllFire"))
                        {
                            FireTabs.Draw(input);
                            ImGui.EndTabItem();
                        }

                        ImGui.EndTabBar();
                    }
                    ImGui.EndDisabled();
                }
            }
            ImGui.End();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }
    }
}
