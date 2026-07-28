using ImGuiNET;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class MainMenuBar
    {
        public static void Draw()
        {
            if (ImGui.BeginMainMenuBar())
            {
                if (ImGui.BeginMenu("File"))
                {                    
                    if (ImGui.MenuItem("Load scenario")) { FileBrowser.OpenLoadInput(); }
                    if (ImGui.MenuItem("Save", ScenarioEditorWindow.HasInput)) { ScenarioEditorWindow.SaveInput(); }
                    if (ImGui.MenuItem("Save as", ScenarioEditorWindow.HasInput)) { FileBrowser.OpenSaveInput(); }

                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("Scenario"))
                {
                    bool isRunning = false;
                    if (PreactGUI.Engine.Simulation != null && PreactGUI.Engine.Simulation.IsRunning)
                    {
                        isRunning = true;
                    }  
                    if (ImGui.MenuItem("New scenario", !isRunning)) { NewScenarioWindow.Open(true); }

                    ImGui.SeparatorText("Loaded scenario");
                    bool canEdit = false;
                    if (ScenarioEditorWindow.HasInput && !isRunning)
                    {
                        canEdit = true;
                    }
                    if (ImGui.MenuItem("Run/edit", canEdit)) { ScenarioEditorWindow.Open(); }
                    //Reopenable, since it is dismissed as soon as it has been read but the items stay
                    //outstanding until they are dealt with.
                    if (ImGui.MenuItem("Checklist", ScenarioChecklistWindow.HasItems)) { ScenarioChecklistWindow.Open(); }

                    bool haveOutput = false;
                    if(PreactGUI.Engine.Simulation != null && (PreactGUI.Engine.Simulation.State == PREACT.Simulation.SimulationState.Running || PreactGUI.Engine.Simulation.State == PREACT.Simulation.SimulationState.Completed))
                    {
                        haveOutput = true;
                    }
                    if (ImGui.MenuItem("Output", haveOutput)) { OutputWindow.Open(); }

                    ImGui.EndMenu();
                }

                

                if (ImGui.BeginMenu("Console"))
                {
                    if (ImGui.MenuItem("Open")) { ConsoleWindow.Open(); }
                    if (ImGui.MenuItem("Clear")) { PreactGUI.ClearMessages(); }

                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("Utilities"))
                {
                    ImGui.SeparatorText("Download");
                    if (ImGui.MenuItem("Download data")) { DownloadDataWindow.Open(); }

                    ImGui.SeparatorText("Edit");
                    if (ImGui.MenuItem("Population editor")) { PopulationEditWindow.Open(); }
                    //Disabled rather than dead: no landscape editor exists. Passing false greys the
                    //item out, which is how the other conditional items here signal unavailability.
                    ImGui.MenuItem("Landscape editor", false);

                    ImGui.SeparatorText("Trigger boundaries");
                    if (ImGui.MenuItem("Probabilistic trigger boundary")) { ProbabilisticTriggerWindow.Open(); }

                    ImGui.SeparatorText("Viewing");
                    //Needs a loaded scenario, since the population file it reads is named by one.
                    if (ImGui.MenuItem("Visualize population", ScenarioEditorWindow.HasInput))
                    {
                        PreactGUI.WUInity.DisplayPopulationDensityMap();
                    }

                    ImGui.EndMenu();
                }

                if (ImGui.BeginMenu("Theme"))
                {
                    if (ImGui.MenuItem("Dark theme")) { Themes.ApplyAdobeSpectrum(true); }
                    if (ImGui.MenuItem("Light Theme")) { Themes.ApplyAdobeSpectrum(false); }

                    ImGui.EndMenu();
                }

                ImGui.EndMainMenuBar();
            }
        }
    }
}
