using ImGuiNET;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The menu bar, grouped by what an entry is rather than where it came from.
    /// </summary>
    /// <remarks>
    /// It had five menus, two of which held two items each (<c>Console</c>: open, clear; <c>Theme</c>: dark,
    /// light) and one of which — <c>Utilities</c> — was a grab-bag of four unrelated groups: a downloader, two
    /// editors (one of them permanently disabled because it does not exist), a trigger campaign, and two map
    /// layer toggles. So "show the road network" and "run a 200-realization Monte Carlo campaign" sat in the
    /// same menu, while opening the console needed a menu of its own.
    ///
    /// Now: <b>File</b> is the scenario as a document. <b>Scenario</b> is what you do to the loaded one, in the
    /// order you do it. <b>View</b> is anything that only changes what is displayed — including the console and
    /// the theme, which change nothing else. <b>Tools</b> is the things that do work of their own.
    /// </remarks>
    public static class MainMenuBar
    {
        public static void Draw()
        {
            if (!ImGui.BeginMainMenuBar())
            {
                return;
            }

            DrawFileMenu();
            DrawScenarioMenu();
            DrawViewMenu();
            DrawToolsMenu();

            ImGui.EndMainMenuBar();
        }

        /// <summary>The scenario as a document: make one, open one, write it back.</summary>
        private static void DrawFileMenu()
        {
            if (!ImGui.BeginMenu("File"))
            {
                return;
            }

            //"New scenario" belongs beside "Load", not under Scenario: both start from nothing and replace
            //whatever is loaded, which is the property worth grouping on.
            if (ImGui.MenuItem("New scenario...", !IsRunning)) { NewScenarioWindow.Open(true); }
            if (ImGui.MenuItem("Load scenario...")) { FileBrowser.OpenLoadInput(); }

            ImGui.Separator();

            if (ImGui.MenuItem("Save", "Ctrl+S", false, ScenarioEditorWindow.HasInput)) { ScenarioEditorWindow.SaveInput(); }
            if (ImGui.MenuItem("Save as...", ScenarioEditorWindow.HasInput)) { FileBrowser.OpenSaveInput(); }

            ImGui.EndMenu();
        }

        /// <summary>The loaded scenario, in the order the work happens: prepare, check, edit, run, read.</summary>
        private static void DrawScenarioMenu()
        {
            if (!ImGui.BeginMenu("Scenario"))
            {
                return;
            }

            bool canEdit = ScenarioEditorWindow.HasInput && !IsRunning;

            if (ImGui.MenuItem("Edit...", canEdit)) { ScenarioEditorWindow.Open(); }

            //Its own entry rather than only inside the creator: opening the creator clears the loaded
            //scenario, so building a missing RouterDb, population, SUMO network or ELMFIRE case for one meant
            //discarding the scenario it was wanted for.
            if (ImGui.MenuItem("Prepare data...", canEdit)) { ScenarioDataWindow.Open(); }

            //Reopenable, since it is dismissed as soon as it has been read but the items stay outstanding
            //until they are dealt with.
            if (ImGui.MenuItem("Checklist...", ScenarioChecklistWindow.HasItems)) { ScenarioChecklistWindow.Open(); }

            ImGui.Separator();

            if (ImGui.MenuItem("Output...", HasOutput)) { OutputWindow.Open(); }
            if (ImGui.IsItemHovered() && !HasOutput)
            {
                ImGui.SetTooltip("Available once a run has started. Opens by itself when one does.");
            }

            ImGui.EndMenu();
        }

        /// <summary>Anything that only changes what is on screen.</summary>
        private static void DrawViewMenu()
        {
            if (!ImGui.BeginMenu("View"))
            {
                return;
            }

            ImGui.SeparatorText("Map layers");

            //Needs a loaded scenario, since the population file it reads is named by one.
            if (ImGui.MenuItem("Population density", ScenarioEditorWindow.HasInput))
            {
                PreactGUI.WUInity.DisplayPopulationDensityMap();
            }

            //A checkable item rather than two: the roads are a background layer, either on or off, and the
            //tick is what says which.
            bool roadsVisible = ScenarioEditorWindow.HasInput && PreactGUI.WUInity.IsRoadNetworkVisible;
            if (ImGui.MenuItem("Road network", string.Empty, roadsVisible, ScenarioEditorWindow.HasInput))
            {
                PreactGUI.WUInity.ShowRoadNetwork(!roadsVisible);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The lanes of the scenario's SUMO network - the roads traffic is actually "
                    + "routed on. Needs the SUMO network to have been built.");
            }

            ImGui.SeparatorText("Console");

            if (ImGui.MenuItem("Open console")) { ConsoleWindow.Open(); }
            if (ImGui.MenuItem("Clear console")) { PreactGUI.ClearMessages(); }

            ImGui.SeparatorText("Theme");

            if (ImGui.MenuItem("Dark")) { Themes.ApplyAdobeSpectrum(true); }
            if (ImGui.MenuItem("Light")) { Themes.ApplyAdobeSpectrum(false); }

            ImGui.EndMenu();
        }

        /// <summary>Things that do work of their own, rather than editing the loaded scenario.</summary>
        private static void DrawToolsMenu()
        {
            if (!ImGui.BeginMenu("Tools"))
            {
                return;
            }

            if (ImGui.MenuItem("Probabilistic trigger campaign...")) { ProbabilisticTriggerWindow.Open(); }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("One evacuation and trigger boundary per fire realization, aggregated into a "
                    + "probability raster. Prefilled from the loaded scenario.");
            }

            ImGui.Separator();

            if (ImGui.MenuItem("Population editor...")) { PopulationEditWindow.Open(); }

            if (ImGui.MenuItem("Download data...")) { DownloadDataWindow.Open(); }
            if (ImGui.IsItemHovered())
            {
                //Worth saying, because the overlap is otherwise the confusing part: this and Prepare data
                //fetch the same things from the same sources. The difference is only whether there is a
                //scenario to name the area and receive the paths.
                ImGui.SetTooltip("The same downloads Prepare data runs, but for an area drawn by hand into a "
                    + "folder of your choosing, with no scenario involved. Nothing is wired into a scenario.");
            }

            //The permanently-disabled "Landscape editor" that used to sit here is gone. Greying out an item
            //says "not available now"; there was never such an editor, so it said the wrong thing forever.

            ImGui.EndMenu();
        }

        private static bool IsRunning
        {
            get
            {
                return PreactGUI.Engine.Simulation != null && PreactGUI.Engine.Simulation.IsRunning;
            }
        }

        private static bool HasOutput
        {
            get
            {
                if (PreactGUI.Engine.Simulation == null)
                {
                    return false;
                }

                return PreactGUI.Engine.Simulation.State == PREACT.Simulation.SimulationState.Running
                       || PreactGUI.Engine.Simulation.State == PREACT.Simulation.SimulationState.Completed;
            }
        }
    }
}
