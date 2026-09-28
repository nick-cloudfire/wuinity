using Assets.WUInity.GUI.DearIMGUI.Input;
using ImGuiNET;
using System;
using System.Collections.Generic;
using System.Text;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public  static class ScenarioEditorWindow
    {
        private static bool _isOpen;

        /// <summary>The current scenario. Kept for the windows that still read it from here; it is
        /// <see cref="ScenarioSession.Input"/>.</summary>
        public static PREACT.Input.PREACTInput Input { get => ScenarioSession.Input; }
        public static bool HasInput { get => ScenarioSession.HasInput; }

        static ScenarioEditorWindow()
        {
            ScenarioSession.ScenarioChanged += () =>
            {
                if (ScenarioSession.HasInput)
                {
                    Open();
                }
            };
        }

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
            if(_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
            _isOpen = false;            
        }

        public static void Draw()
        {
            PREACT.Input.PREACTInput _input = ScenarioSession.Input;
            if(!_isOpen || _input == null)
            {
                return;
            }

            ImGui.Begin("Scenario editor", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            //Configure, then run: the run tab is last because it is the last thing done, and it closes this
            //window when it starts. It used to be first, so the tab that discards the window you are working
            //in was the one you landed on.
            if (ImGui.BeginTabBar("Scenario"))
            {
                if (ImGui.BeginTabItem("Simulation"))
                {
                    SimulationTabs.Draw(_input);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Evacuation"))
                {
                    EvacuationTabs.Draw(_input, _input.Evacuation, _input.PedestrianModule, _input.TrafficModule);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Hazards"))
                {
                    HazardsInputTab.Draw(_input);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Run"))
                {
                    RunTab.Draw();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        public static void SaveInput()
        {
            //ParseMainData();
            PREACT.Input.PREACTInput.SaveToDisk(ScenarioSession.Input, PreactGUI.Engine.WorkingFile);
        }

        public static void SaveNewInput(string[] paths)
        {
            //ParseMainData();
            PREACT.Input.PREACTInput.SaveToDisk(ScenarioSession.Input, paths[0]);
            PreactGUI.Engine.LoadInputFromFile(paths[0], out bool success);
        }
    }
}
