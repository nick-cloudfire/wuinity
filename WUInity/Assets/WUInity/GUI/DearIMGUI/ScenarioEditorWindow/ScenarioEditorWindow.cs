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
        private static PREACT.Input.PREACTInput _input;

        public static PREACT.Input.PREACTInput Input{ get => _input; }
        public static bool HasInput { get => _input == null ? false : true; }

        public static void SetInput(PREACT.Input.PREACTInput input)
        {
            _input = input;
            Open();
        }

        public static void ClearInput()
        {
            _input = null;
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
            PREACT.Input.PREACTInput.SaveToDisk(_input, PreactGUI.Engine.WorkingFile);
        }

        public static void SaveNewInput(string[] paths)
        {
            //ParseMainData();
            PREACT.Input.PREACTInput.SaveToDisk(_input, paths[0]);
            PreactGUI.Engine.LoadInputFromFile(paths[0], out bool success);
        }
    }
}
