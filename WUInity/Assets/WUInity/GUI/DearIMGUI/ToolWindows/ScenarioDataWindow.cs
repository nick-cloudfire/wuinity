using ImGuiNET;
using PREACT.Input;
using System.IO;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Prepares the data files a scenario that is already loaded needs, in place.
    ///
    /// The new-scenario creator has the same steps, but it cannot be used for this: opening it clears
    /// the loaded scenario and starts a fresh input, so reaching those buttons meant throwing away the
    /// scenario they were wanted for. This window runs the same steps against the loaded input.
    ///
    /// Every step is offered whether or not its output already exists, because the reasons to re-run
    /// one are ordinary - the area of interest changed, a download was truncated, the file came out
    /// empty. What exists is marked as done with its size and date, and re-running overwrites.
    /// </summary>
    public static class ScenarioDataWindow
    {
        private static bool _isOpen;

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

            ScenarioDataSteps.FlushStepLog();

            ImGui.Begin("Prepare scenario data", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            PREACTInput input = ScenarioEditorWindow.Input;
            if (input == null)
            {
                ImGui.TextWrapped("Load a scenario first: these steps name their output after it and write into its folder.");
                ImGui.End();
                if (!_isOpen) { PreactGUI.CloseWindow(Draw); }
                return;
            }

            //Pointed at the loaded scenario every frame rather than once on opening, so loading a
            //different scenario while this is open cannot leave the steps writing into the old one.
            ScenarioDataSteps.Input = input;

            ImGui.Text("Scenario: " + input.Simulation.Name);
            ImGui.Text("Folder: " + input.RootFolder);
            ImGui.Text($"Area: lower-left {input.Simulation.LowerLeftLatLon.x:F5}, {input.Simulation.LowerLeftLatLon.y:F5}, "
                + $"domain {input.Simulation.DomainSize.x:F0} x {input.Simulation.DomainSize.y:F0} m");

            if (!ScenarioDataSteps.ValidateAio(out string problem))
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), problem);
                ImGui.End();
                if (!_isOpen) { PreactGUI.CloseWindow(Draw); }
                return;
            }

            ImGui.BeginDisabled(ScenarioDataSteps.Busy);

            ImGui.SeparatorText("Road network");
            //Both the RouterDb and the SUMO network are built from the same OSM extract, which is why
            //it is a step of its own here rather than being repeated under each.
            if (ScenarioDataSteps.StepButton("Download OSM roads", ScenarioDataSteps.OsmFile)) { ScenarioDataSteps.DownloadOsm(); }
            ImGui.TextDisabled("Roads only. Both the RouterDb and the SUMO network are built from this.");

            if (ScenarioDataSteps.StepButton("Build RouterDb", ScenarioDataSteps.RouterDbFile)) { ScenarioDataSteps.BuildRouterDb(); }
            ImGui.TextDisabled("Itinero routing graph. Used to snap households onto a road they can leave by.");

            if (ScenarioDataSteps.StepButton("Build SUMO network", ScenarioDataSteps.SumoConfigFile)) { ScenarioDataSteps.BuildSumoNetwork(); }
            ImGui.TextDisabled("Runs SUMO's netconvert and points the scenario at the configuration. Needs SUMO installed.");

            ImGui.SeparatorText("Population");
            ImGui.InputInt("Min household size", ref ScenarioDataSteps.MinHouseholdSize);
            ImGui.InputInt("Max household size", ref ScenarioDataSteps.MaxHouseholdSize);
            if (ScenarioDataSteps.StepButton("Download WorldPop", ScenarioDataSteps.WorldPopFile)) { ScenarioDataSteps.DownloadWorldPop(); }
            if (ScenarioDataSteps.StepButton("Generate population", ScenarioDataSteps.PopulationFile)) { ScenarioDataSteps.GeneratePopulation(); }
            ImGui.TextDisabled("Needs WorldPop and the RouterDb. Sets PopulationFile on the scenario.");

            ImGui.SeparatorText("Weather and landscape");
            if (ScenarioDataSteps.StepButton("Download weather", ScenarioDataSteps.WeatherFile)) { ScenarioDataSteps.DownloadWeather(); }
            ImGui.Checkbox("Anderson 13 fuel models (otherwise Scott & Burgan 40)", ref ScenarioDataSteps.UseAnderson13);
            if (ImGui.Button("Download LANDFIRE landscape")) { ScenarioDataSteps.DownloadLandfire(); }
            ImGui.TextDisabled("LANDFIRE covers the United States only. Elsewhere, supply the landscape file yourself.");

            ImGui.EndDisabled();

            ImGui.SeparatorText("Progress");
            ScenarioDataSteps.DrawStatus();

            ImGui.SeparatorText("Keeping the result");
            //The steps set paths on the loaded input - PopulationFile, the SUMO configuration, the
            //weather file - and those live only in memory until the scenario is written back out.
            ImGui.TextWrapped("Steps that produce a file the scenario refers to also set the path on it. "
                + "Save the scenario to keep those, or they are lost when it is closed.");
            ImGui.BeginDisabled(ScenarioDataSteps.Busy || !ScenarioEditorWindow.HasInput);
            if (ImGui.Button("Save scenario"))
            {
                ScenarioEditorWindow.SaveInput();
            }
            ImGui.EndDisabled();

            ImGui.End();

            //A sibling window rather than one nested inside this, and drawn after End for that reason.
            ScenarioDataSteps.DrawProgressWindow();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }
    }
}
