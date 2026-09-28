using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using UnityEngine;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The menu bar, in the order the work is done: the scenario as a document, its place and time, the data
    /// it needs, the fire, the evacuation, running it, and what came out.
    /// </summary>
    /// <remarks>
    /// It used to have a grab-bag "Utilities" menu (a downloader, two editors - one of which did not exist -
    /// a campaign and two map toggles) beside menus of two items each, and nothing said why an item was
    /// greyed out. Now every item that cannot be used is disabled with a tooltip that names what is in the
    /// way - the same words as the workflow row it belongs to, because both come from
    /// <see cref="WorkflowService.WhyNot(WorkflowAction, WorkflowStepId)"/> - and every item that does
    /// something goes through <see cref="WorkflowService.Perform"/>, so a menu item and its row cannot differ.
    /// </remarks>
    public static class MainMenuBar
    {
        private static bool _openAbout;
        private static bool _aboutOpen;

        public static void Draw()
        {
            if (ImGui.BeginMainMenuBar())
            {
                DrawFileMenu();
                DrawScenarioMenu();
                DrawDataMenu();
                DrawFireMenu();
                DrawEvacuationMenu();
                DrawRunMenu();
                DrawResultsMenu();
                DrawViewMenu();
                DrawHelpMenu();
                DrawStatus();

                ImGui.EndMainMenuBar();
            }

            //At the top of the ID stack, where BeginPopupModal looks for it.
            if (_openAbout)
            {
                _openAbout = false;
                _aboutOpen = true;
                ImGui.OpenPopup("About###About");
            }
            DrawAbout();
        }

        // ------------------------------------------------------------------ File

        /// <summary>The scenario as a document: make one, open one, write it back.</summary>
        private static void DrawFileMenu()
        {
            if (!ImGui.BeginMenu("File"))
            {
                return;
            }

            bool busy = ScenarioSession.IsBusy;

            if (ImGui.MenuItem("New scenario...", "Ctrl+N", false, !busy)) { ScenarioSession.RequestNew(); }
            BusyTooltip(busy, "A folder, a name, an area and a time window; the .wui is written straight away.");
            //Gated like New: loading replaced the scenario a running data step was about to write its paths
            //into, and the one a running simulation was reading.
            if (ImGui.MenuItem("Open scenario...", "Ctrl+O", false, !busy)) { ScenarioSession.RequestOpen(); }
            BusyTooltip(busy);

            List<string> recent = global::WUInity.RecentScenario.Recent;
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
            Tooltip(busy ? ScenarioSession.BusyTooltip : recent.Count == 0 ? "Nothing opened yet." : null);

            ImGui.Separator();

            bool canSave = ScenarioSession.HasInput && !ScenarioSession.EditingLocked;
            if (ImGui.MenuItem("Save", "Ctrl+S", false, canSave && ScenarioSession.IsDirty)) { ScenarioSession.Save(); }
            Tooltip(!ScenarioSession.HasInput ? "No scenario is open."
                : ScenarioSession.EditingLocked ? ScenarioSession.BusyTooltip
                : ScenarioSession.IsDirty ? "Unsaved: " + ScenarioSession.DirtySummary + "."
                : "Nothing to save.");
            if (ImGui.MenuItem("Save as... (same folder)", string.Empty, false, canSave && !busy)) { FileBrowser.OpenSaveInput(); }
            Tooltip(!ScenarioSession.HasInput ? "No scenario is open." : busy ? ScenarioSession.BusyTooltip
                : "Another .wui beside this one, sharing its prepared data. To move it elsewhere, copy the scenario.");
            if (ImGui.MenuItem("Copy scenario to...", string.Empty, false, ScenarioSession.HasInput && !busy)) { FileBrowser.OpenCopyScenario(false); }
            Tooltip(!ScenarioSession.HasInput ? "No scenario is open." : busy ? ScenarioSession.BusyTooltip
                : "Copies the scenario's folder (without _output and campaign realizations) into another folder, "
                  + "writes the scenario as it is now into the copy, and opens it.");

            if (ImGui.MenuItem("Reveal scenario folder", string.Empty, false, ScenarioSession.HasInput)) { OpenInFileManager(ScenarioSession.RootFolder); }
            if (ImGui.MenuItem("Close scenario", string.Empty, false, ScenarioSession.HasInput && !busy)) { ScenarioSession.RequestClose(); }
            Tooltip(busy ? ScenarioSession.BusyTooltip : null);

            ImGui.Separator();
            if (ImGui.MenuItem("Quit")) { ScenarioSession.RequestQuit(); }
            Tooltip("Stops a running simulation, data step or campaign first and waits for it, then asks to save unsaved work.");

            ImGui.EndMenu();
        }

        // ------------------------------------------------------------------ Scenario

        private static void DrawScenarioMenu()
        {
            if (!ImGui.BeginMenu("Scenario"))
            {
                return;
            }

            if (ImGui.MenuItem("Workflow panel", string.Empty, ScenarioWorkflowWindow.IsOpen)) { ScenarioWorkflowWindow.Toggle(); }
            Tooltip("Every step from an empty folder to a campaign, what each still needs, and what to do next.");

            ImGui.Separator();
            Item("Place and time...", WorkflowAction.EditPlaceAndTime, WorkflowStepId.PlaceAndTime);
            Page("Terrain...", SettingsPage.Terrain);
            Page("Weather...", SettingsPage.Weather);

            ImGui.Separator();
            Item("Check scenario...", WorkflowAction.CheckScenario, WorkflowStepId.None,
                "Parses the scenario as it stands, unsaved edits included, and files what it finds under the workflow's steps.");
            if (ImGui.MenuItem("All settings...", string.Empty, ScenarioEditorWindow.IsOpen, ScenarioSession.HasInput)) { ScenarioEditorWindow.Open(); }
            Tooltip(ScenarioSession.HasInput ? "Every setting in one tabbed window." : "No scenario is open.");

            ImGui.EndMenu();
        }

        // ------------------------------------------------------------------ Data

        private static void DrawDataMenu()
        {
            if (!ImGui.BeginMenu("Data"))
            {
                return;
            }

            DataItem("Roads: OpenStreetMap to SUMO network...", WorkflowStepId.Roads, WorkflowAction.PrepareRoads);
            DataItem("Population: WorldPop to households...", WorkflowStepId.Population, WorkflowAction.PreparePopulation);
            Item("Fuels, canopy and buildings...", WorkflowAction.OpenSourceLayers, WorkflowStepId.Fuels);

            bool elmfire = WorkflowService.Model.IsElmfire;
            if (elmfire)
            {
                DataItem("Build fire case (ELMFIRE)...", WorkflowStepId.FireCase, WorkflowAction.BuildFireCase);
            }
            else
            {
                ImGui.MenuItem("Build fire case (ELMFIRE)...", string.Empty, false, false);
                Tooltip(ScenarioSession.HasInput ? "The fire model is not ELMFIRE (Fire > Fire model settings)." : "No scenario is open.");
            }

            ImGui.Separator();
            if (ImGui.BeginMenu("Advanced", ScenarioSession.HasInput))
            {
                string whyNotHere = elmfire
                    ? "An ELMFIRE case builds its own terrain and weather (Build fire case, step 5)."
                    : null;

                string why = whyNotHere ?? WorkflowService.WhyNot(WorkflowAction.DownloadDemOnly, WorkflowStepId.FireCase);
                if (ImGui.MenuItem("Download DEM only", string.Empty, false, string.IsNullOrEmpty(why)))
                {
                    WorkflowService.Perform(WorkflowAction.DownloadDemOnly);
                }
                Tooltip(string.IsNullOrEmpty(why)
                    ? "Downloads a DEM from OpenTopography, warps it into the simulation's UTM zone, and derives slope and aspect."
                    : why);

                why = whyNotHere ?? WeatherWhyNot();
                if (ImGui.MenuItem("Download Open-Meteo weather", string.Empty, false, string.IsNullOrEmpty(why)))
                {
                    ScenarioDataSteps.DownloadWeatherOnly();
                }
                Tooltip(string.IsNullOrEmpty(why)
                    ? "Hourly weather for the simulation window, as the weather CSV an imported fire's k-PERIL reads."
                    : why);

                Item("LANDFIRE fuels and canopy (US)", WorkflowAction.DownloadLandfire, WorkflowStepId.Fuels,
                    "Downloads LANDFIRE's fuel model, canopy cover, height, base height and bulk density for the area, "
                    + "and sets them as the fire case's source layers.");
                ImGui.EndMenu();
            }
            Tooltip(ScenarioSession.HasInput ? null : "No scenario is open.");

            ImGui.EndMenu();
        }

        private static string WeatherWhyNot()
        {
            if (!ScenarioSession.HasInput) return "No scenario is open.";
            if (ScenarioSession.EditingLocked) return ScenarioSession.BusyTooltip;
            WorkflowStep place = WorkflowService.Step(WorkflowStepId.PlaceAndTime);
            if (place != null && place.HasErrors) return "Blocked by step 1 (Place and time): " + FirstError(place);
            return string.Empty;
        }

        private static string FirstError(WorkflowStep step)
        {
            foreach (StepIssue i in step.Issues)
            {
                if (i.Level == IssueLevel.Error) return i.Text;
            }
            return string.Empty;
        }

        // ------------------------------------------------------------------ Fire

        private static void DrawFireMenu()
        {
            if (!ImGui.BeginMenu("Fire"))
            {
                return;
            }

            Item("Fire areas (WUI, ignition area)...", WorkflowAction.OpenFireAreas, WorkflowStepId.FireAreas);
            Item("Ignition points...", WorkflowAction.OpenFireAreas, WorkflowStepId.FireAreas,
                "Listed, added and moved in the Fire areas window, beside the painted ignition area.");
            ImGui.Separator();
            Item("Fire model settings...", WorkflowAction.OpenFireModelSettings, WorkflowStepId.FireCase);
            Item("Fire behaviour (namelist)...", WorkflowAction.OpenFireBehaviour, WorkflowStepId.FireCase);
            if (WorkflowService.Model.IsElmfire)
            {
                Item("Preview namelist...", WorkflowAction.PreviewNamelist, WorkflowStepId.FireCase,
                    "The elmfire.data a run would write from these settings.");
            }
            ImGui.Separator();
            Item("Trigger boundary (k-PERIL)...", WorkflowAction.OpenTriggerBoundary, WorkflowStepId.TriggerBoundary);
            if (ScenarioSession.HasInput && ScenarioSession.Input.SmokeModule.Enabled)
            {
                Page("Smoke...", SettingsPage.Smoke);
            }

            ImGui.EndMenu();
        }

        // ------------------------------------------------------------------ Evacuation

        private static void DrawEvacuationMenu()
        {
            if (!ImGui.BeginMenu("Evacuation"))
            {
                return;
            }

            Item("Modules (pedestrian, traffic)...", WorkflowAction.OpenEvacuationModules, WorkflowStepId.Roads,
                "Which evacuation models run, their settings, and the population.");
            ImGui.Separator();
            Item("Destinations...", WorkflowAction.OpenDestinations, WorkflowStepId.Destinations);
            Item("Response curves...", WorkflowAction.OpenCurves, WorkflowStepId.CurvesAndDemographics);
            Item("Demographics...", WorkflowAction.OpenDemographics, WorkflowStepId.CurvesAndDemographics);
            Item("Evacuation groups...", WorkflowAction.OpenGroups, WorkflowStepId.EvacuationGroups);
            Item("Paint group areas...", WorkflowAction.PaintGroups, WorkflowStepId.EvacuationGroups);

            ImGui.EndMenu();
        }

        // ------------------------------------------------------------------ Run

        private static void DrawRunMenu()
        {
            if (!ImGui.BeginMenu("Run"))
            {
                return;
            }

            Item("Run simulation...", WorkflowAction.OpenRun, WorkflowStepId.RunSimulation, "Checks, saves, runs, and shows how far along it is.", "F5");

            bool canPause = RunSimulationWindow.CanPause;
            if (ImGui.MenuItem(RunSimulationWindow.IsPaused ? "Resume" : "Pause", string.Empty, false, canPause))
            {
                RunSimulationWindow.TogglePause();
            }
            Tooltip(canPause ? null : ScenarioSession.SimulationActive ? "Not until the run is past initialisation (the fire is computed first)."
                : "No simulation is running.");

            if (ImGui.MenuItem("Stop", string.Empty, false, ScenarioSession.SimulationActive)) { RunSimulationWindow.Stop(); }
            Tooltip(ScenarioSession.SimulationActive ? "Stops the run." : "No simulation is running.");

            ImGui.Separator();
            Item("Trigger campaign...", WorkflowAction.OpenCampaign, WorkflowStepId.Campaign,
                "One fire, evacuation and trigger boundary per realization, aggregated into a probability raster.");

            ImGui.EndMenu();
        }

        // ------------------------------------------------------------------ Results

        private static void DrawResultsMenu()
        {
            if (!ImGui.BeginMenu("Results"))
            {
                return;
            }

            bool haveOutput = HasOutput;
            if (ImGui.MenuItem("Live output", string.Empty, LiveOutputWindow.IsOpen, haveOutput)) { LiveOutputWindow.Open(); }
            Tooltip(haveOutput ? "Evacuees, vehicles and the fire as the run goes." : "Available once a run has started; opens by itself when one does.");

            if (ImGui.BeginMenu("Show on map", ScenarioSession.HasInput))
            {
                Show("Fire arrival (last run)", ResultsWindow.Kind.FireArrival);
                Show("Trigger boundary", ResultsWindow.Kind.TriggerBoundary);
                ImGui.Separator();
                Show("Trigger probability", ResultsWindow.Kind.TriggerProbability);
                Show("Burn probability", ResultsWindow.Kind.BurnProbability);
                Show("Arrival p10", ResultsWindow.Kind.ArrivalStatistic, "p10");
                Show("Arrival p50", ResultsWindow.Kind.ArrivalStatistic, "p50");
                Show("Arrival p90", ResultsWindow.Kind.ArrivalStatistic, "p90");
                ImGui.Separator();
                bool groups = PreactGUI.WUInity.Painter != null && PreactGUI.WUInity.Painter.HasEvacGroupCells;
                if (ImGui.MenuItem("Evacuation groups", string.Empty, false, groups)) { PreactGUI.WUInity.ShowEvacGroupOverlay(); }
                Tooltip(groups ? "The painted group areas, dimmed." : "No group areas are loaded (Evacuation > Paint group areas).");
                Show("WUI area (case)", ResultsWindow.Kind.WuiArea);
                ImGui.Separator();
                if (ImGui.MenuItem("Hide result", string.Empty, false, ResultsWindow.IsShowing)) { ResultsWindow.Hide(); }
                ImGui.EndMenu();
            }
            Tooltip(ScenarioSession.HasInput ? null : "No scenario is open.");

            Item("Results of the last run and campaign...", WorkflowAction.OpenResults, WorkflowStepId.Results);

            string output = ScenarioSession.HasInput ? Path.Combine(ScenarioSession.RootFolder, "_output") : null;
            bool haveFolder = output != null && GuiFiles.Probe.DirectoryExists(output);
            if (ImGui.MenuItem("Open output folder", string.Empty, false, haveFolder)) { OpenInFileManager(output); }
            Tooltip(haveFolder ? output : ScenarioSession.HasInput ? "There is no _output folder yet: nothing has been run." : "No scenario is open.");

            ImGui.EndMenu();
        }

        private static void Show(string label, ResultsWindow.Kind kind, string nameContains = null)
        {
            bool any = ResultsWindow.HasAny(kind, nameContains);
            if (ImGui.MenuItem(label + "###show" + kind + nameContains, string.Empty, false, any))
            {
                ResultsWindow.ShowNewest(kind, nameContains);
            }
            Tooltip(any ? null : kind == ResultsWindow.Kind.FireArrival || kind == ResultsWindow.Kind.TriggerBoundary
                ? "Not written yet: run the simulation (Run > Run simulation, step 11)."
                : kind == ResultsWindow.Kind.WuiArea
                ? "The case has no wui_area.tif yet (steps 5 and 6)."
                : "Not written yet: run a campaign (Run > Trigger campaign, step 13).");
        }

        // ------------------------------------------------------------------ View

        /// <summary>Anything that only changes what is on screen.</summary>
        private static void DrawViewMenu()
        {
            if (!ImGui.BeginMenu("View"))
            {
                return;
            }

            if (ImGui.BeginMenu("Map layers", ScenarioSession.HasInput))
            {
                var visualizer = PreactGUI.WUInity.SimulationDomainVisualizer;
                bool density = visualizer != null && visualizer.IsDataPlaneActive();
                if (ImGui.MenuItem("Population density", string.Empty, density))
                {
                    if (density) visualizer.SetVisibility(false);
                    else PreactGUI.WUInity.DisplayPopulationDensityMap();
                }
                Tooltip("People per cell, from the scenario's population file.");

                //A checkable item rather than two: the roads are a background layer, either on or off.
                bool roads = PreactGUI.WUInity.IsRoadNetworkVisible;
                if (ImGui.MenuItem("Road network", string.Empty, roads)) { PreactGUI.WUInity.ShowRoadNetwork(!roads); }
                Tooltip("The lanes of the scenario's SUMO network - the roads traffic is actually routed on. Needs the "
                    + "SUMO network to have been built (step 2).");

                bool fireGrid = PreactGUI.WUInity.IsFireGridOutlineVisible;
                if (ImGui.MenuItem("Fire grid outline", string.Empty, fireGrid)) { PreactGUI.WUInity.ShowFireGridOutline(!fireGrid); }
                Tooltip("The edge of the grid the fire, the painted areas and k-PERIL share (orange) - for an ELMFIRE scenario the "
                    + "case's dem.tif, which reaches past the domain (white) by the case's padding.");

                bool markers = visualizer == null || visualizer.MarkersVisible;
                if (ImGui.MenuItem("Markers", string.Empty, markers, visualizer != null)) { visualizer.SetMarkersVisible(!markers); }
                Tooltip("The destinations (in their colours) and the ignition points (white).");

                bool result = ResultsWindow.IsShowing;
                if (ImGui.MenuItem("Result overlay", string.Empty, result, result)) { ResultsWindow.Hide(); }
                Tooltip(result ? "Hides the result raster (Results > Show on map puts one up)." : "No result is on the map (Results > Show on map).");

                ImGui.EndMenu();
            }
            Tooltip(ScenarioSession.HasInput ? null : "No scenario is open.");

            ImGui.Separator();
            if (ImGui.MenuItem("Console", string.Empty, ConsoleWindow.IsOpen)) { ConsoleWindow.Toggle(); }
            if (ImGui.MenuItem("Clear console")) { PreactGUI.ClearMessages(); }

            if (ImGui.BeginMenu("Theme"))
            {
                bool dark = PreactGUI.IsDarkTheme;
                if (ImGui.MenuItem("Dark", string.Empty, dark)) { PreactGUI.SetTheme(true); }
                if (ImGui.MenuItem("Light", string.Empty, !dark)) { PreactGUI.SetTheme(false); }
                ImGui.EndMenu();
            }

            ImGui.Separator();
            if (ImGui.MenuItem("Reset window layout")) { DockLayout.RequestReset(); }
            Tooltip("The workflow panel back on the left and the console along the bottom.");

            ImGui.EndMenu();
        }

        // ------------------------------------------------------------------ Help

        private static void DrawHelpMenu()
        {
            if (!ImGui.BeginMenu("Help"))
            {
                return;
            }

            if (ImGui.MenuItem("Getting started (docs)")) { OpenDoc("getting-started.md"); }
            if (ImGui.MenuItem("Troubleshooting (docs)")) { OpenDoc("troubleshooting.md"); }
            if (ImGui.MenuItem("External tools and keys...")) { ExternalToolsWindow.Open(); }
            Tooltip("SUMO, GDAL, PROJ, ELMFIRE, WindNinja, and the OpenTopography and Mapbox keys: what was found, and what is lost without each.");
            ImGui.Separator();
            if (ImGui.MenuItem("About")) { _openAbout = true; }

            ImGui.EndMenu();
        }

        private static void OpenDoc(string name)
        {
            //The docs folder sits beside the Unity project in the repository; a player build has none.
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "docs", name));
            if (File.Exists(path))
            {
                OpenInFileManager(path);
            }
            else
            {
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning, "docs/" + name + " is in the repository's docs folder; "
                    + "it was not found beside this build (looked for " + path + ").");
            }
        }

        private static void DrawAbout()
        {
            if (!_aboutOpen)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(460f, 0f), ImGuiCond.Appearing);
            if (ImGui.BeginPopupModal("About###About", ref _aboutOpen, ImGuiWindowFlags.NoResize))
            {
                ImGui.TextUnformatted("WUInity / PREACT");
                ImGui.TextWrapped("A platform for wildland-urban interface evacuation: a fire (computed by ELMFIRE or imported), "
                    + "the people and vehicles leaving ahead of it, and the trigger boundary at which an order has to be given.");
                ImGui.Spacing();
                ImGui.TextWrapped("Research software. Its results depend on the data prepared for a scenario and on the models' "
                    + "assumptions; they are not a substitute for the judgement of those responsible for an evacuation.");
                ImGui.Spacing();
                ImGui.TextDisabled("Unity " + Application.unityVersion);
                if (ImGui.Button("Close###AboutClose"))
                {
                    _aboutOpen = false;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }
        }

        // ------------------------------------------------------------------ the right-hand end

        /// <summary>Unsaved changes and what is running, at the right of the menu bar.</summary>
        private static void DrawStatus()
        {
            string text;
            Vector4 colour;
            //Read once: the campaign's state is written from its output thread, so the reason can go between two reads.
            string busy = ScenarioSession.BusyReason;
            if (!string.IsNullOrEmpty(busy))
            {
                text = char.ToUpperInvariant(busy[0]) + busy.Substring(1);
                colour = new Vector4(0.35f, 0.6f, 0.95f, 1f);
            }
            else if (ScenarioSession.HasInput && ScenarioSession.IsDirty)
            {
                text = ScenarioSession.DisplayName + " - unsaved changes";
                colour = Fields.Warning;
            }
            else if (ScenarioSession.HasInput)
            {
                text = ScenarioSession.DisplayName;
                colour = ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
            }
            else
            {
                return;
            }

            float width = ImGui.CalcTextSize(text).x + 2f * ImGui.GetStyle().ItemSpacing.x;
            float x = ImGui.GetWindowWidth() - width;
            if (x > ImGui.GetCursorPosX())
            {
                ImGui.SetCursorPosX(x);
                ImGui.TextColored(colour, text);
                if (ScenarioSession.IsDirty && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Unsaved: " + ScenarioSession.DirtySummary + ". Ctrl+S saves.");
                }
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>A menu item that performs a workflow action, disabled with the workflow's reason when it cannot.</summary>
        private static void Item(string label, WorkflowAction action, WorkflowStepId step, string what = null, string shortcut = null)
        {
            string why = WorkflowService.WhyNot(action, step);
            bool enabled = string.IsNullOrEmpty(why);
            if (ImGui.MenuItem(label, shortcut ?? string.Empty, false, enabled))
            {
                WorkflowService.Perform(action);
            }
            if (enabled)
            {
                string described = WorkflowService.Describe(action, step);
                Tooltip(!string.IsNullOrEmpty(what) ? what : described);
            }
            else
            {
                Tooltip(why);
            }
        }

        /// <summary>
        /// A data step: runs what is missing and shows the step's row, where its progress and its Rebuild are.
        /// Ticked when the step is done; clicking it then only shows the row.
        /// </summary>
        private static void DataItem(string label, WorkflowStepId step, WorkflowAction prepare)
        {
            WorkflowStep s = WorkflowService.Step(step);
            bool done = s != null && s.Status == StepStatus.Done;
            string why = done ? (ScenarioSession.HasInput ? string.Empty : "No scenario is open.") : WorkflowService.WhyNot(prepare, step);
            bool enabled = string.IsNullOrEmpty(why);

            if (ImGui.MenuItem(label, string.Empty, done, enabled))
            {
                if (!done)
                {
                    WorkflowService.Perform(prepare);
                }
                ScenarioWorkflowWindow.Focus(step);
            }

            if (!enabled)
            {
                Tooltip(why);
            }
            else if (done)
            {
                Tooltip("Done: " + s.Summary + ". Rebuilding is in the workflow row.");
            }
            else
            {
                Tooltip(WorkflowService.Describe(prepare, step));
            }
        }

        private static void Page(string label, SettingsPage page)
        {
            bool has = ScenarioSession.HasInput;
            if (ImGui.MenuItem(label, string.Empty, SettingsPageWindow.IsOpen(page), has)) { SettingsPageWindow.Open(page); }
            Tooltip(has ? null : "No scenario is open.");
        }

        private static void Tooltip(string text)
        {
            if (!string.IsNullOrEmpty(text) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(text);
            }
        }

        /// <summary>Says why the item just drawn is disabled when it is disabled for being busy, else what it does.</summary>
        private static void BusyTooltip(bool disabledForBusy, string otherwise = null)
        {
            Tooltip(disabledForBusy ? ScenarioSession.BusyTooltip : otherwise);
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

        private static bool HasOutput
        {
            get
            {
                if (PreactGUI.Engine?.Simulation == null)
                {
                    return false;
                }

                return PreactGUI.Engine.Simulation.State == PREACT.Simulation.SimulationState.Running
                       || PreactGUI.Engine.Simulation.State == PREACT.Simulation.SimulationState.Completed
                       || ScenarioSession.SimulationActive;
            }
        }
    }
}
