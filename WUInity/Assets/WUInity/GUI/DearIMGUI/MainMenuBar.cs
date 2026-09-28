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
            bool busy = ScenarioSession.IsBusy;

            if (ImGui.MenuItem("New scenario...", "Ctrl+N", false, !busy)) { ScenarioSession.RequestNew(); }
            BusyTooltip(busy);
            //Gated like New: loading replaced the scenario a running data step was about to write its paths
            //into, and the one a running simulation was reading.
            if (ImGui.MenuItem("Open scenario...", "Ctrl+O", false, !busy)) { ScenarioSession.RequestOpen(); }
            BusyTooltip(busy);

            System.Collections.Generic.List<string> recent = global::WUInity.RecentScenario.Recent;
            if (ImGui.BeginMenu("Open recent", !busy && recent.Count > 0))
            {
                for (int i = 0; i < recent.Count; ++i)
                {
                    if (ImGui.MenuItem(recent[i] + "###recent" + i)) { ScenarioSession.RequestOpen(recent[i]); }
                }
                ImGui.Separator();
                if (ImGui.MenuItem("Clear list")) { global::WUInity.RecentScenario.ClearRecent(); }
                ImGui.EndMenu();
            }

            ImGui.Separator();

            bool canSave = ScenarioSession.HasInput && !ScenarioSession.EditingLocked;
            if (ImGui.MenuItem("Save", "Ctrl+S", false, canSave && ScenarioSession.IsDirty)) { ScenarioSession.Save(); }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(ScenarioSession.EditingLocked ? ScenarioSession.BusyTooltip
                    : ScenarioSession.IsDirty ? "Unsaved: " + ScenarioSession.DirtySummary + "."
                    : "Nothing to save.");
            }
            if (ImGui.MenuItem("Save as... (same folder)", canSave && !busy)) { FileBrowser.OpenSaveInput(); }
            BusyTooltip(busy);
            if (ImGui.MenuItem("Copy scenario to...", ScenarioSession.HasInput && !busy)) { FileBrowser.OpenCopyScenario(false); }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(busy ? ScenarioSession.BusyTooltip : "Copies the scenario's folder (without _output and campaign "
                    + "realizations) into another folder, writes the scenario as it is now into the copy, and opens it.");
            }

            if (ImGui.MenuItem("Reveal scenario folder", ScenarioSession.HasInput)) { OpenInFileManager(ScenarioSession.RootFolder); }
            if (ImGui.MenuItem("Close scenario", ScenarioSession.HasInput && !busy)) { ScenarioSession.RequestClose(); }
            BusyTooltip(busy);

            ImGui.Separator();
            if (ImGui.MenuItem("Quit")) { ScenarioSession.RequestQuit(); }

            ImGui.EndMenu();
        }

        /// <summary>The loaded scenario, in the order the work happens: prepare, check, edit, run, read.</summary>
        private static void DrawScenarioMenu()
        {
            if (!ImGui.BeginMenu("Scenario"))
            {
                return;
            }

            bool canEdit = ScenarioEditorWindow.HasInput && !ScenarioSession.EditingLocked;

            if (ImGui.MenuItem("All settings...", canEdit)) { ScenarioEditorWindow.Open(); }
            BusyTooltip(ScenarioSession.EditingLocked);

            //The data steps are rows of the workflow panel now, each with its own button.
            if (ImGui.MenuItem("Workflow panel", string.Empty, ScenarioWorkflowWindow.IsOpen)) { ScenarioWorkflowWindow.Toggle(); }

            //Reopenable, since it is dismissed as soon as it has been read but the items stay outstanding
            //until they are dealt with.
            if (ImGui.MenuItem("Check scenario...", ScenarioSession.HasInput)) { ScenarioCheckWindow.OpenAndCheck(); }

            ImGui.Separator();

            if (ImGui.MenuItem("Run simulation...", "F5", false, ScenarioSession.HasInput)) { RunSimulationWindow.Open(); }
            if (ImGui.MenuItem("Results...", ScenarioSession.HasInput)) { ResultsWindow.Open(); }
            if (ImGui.MenuItem("Live output...", HasOutput)) { LiveOutputWindow.Open(); }
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

            if (ImGui.MenuItem("Probabilistic trigger campaign...", !ScenarioSession.SimulationActive)) { ProbabilisticTriggerWindow.Open(); }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("One evacuation and trigger boundary per fire realization, aggregated into a "
                    + "probability raster. Prefilled from the loaded scenario.");
            }

            //The permanently-disabled "Landscape editor" that used to sit here is gone. Greying out an item
            //says "not available now"; there was never such an editor, so it said the wrong thing forever.

            ImGui.EndMenu();
        }

        /// <summary>Opens a folder (or file) in the operating system's own viewer.</summary>
        public static void OpenInFileManager(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (System.Exception e)
            {
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning, "Could not open " + path + ": " + e.Message);
            }
        }

        /// <summary>Says why the item just drawn is disabled, when it is disabled for being busy.</summary>
        private static void BusyTooltip(bool disabledForBusy)
        {
            if (disabledForBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(ScenarioSession.BusyTooltip);
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
