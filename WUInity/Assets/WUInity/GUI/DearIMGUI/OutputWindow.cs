
using ImGuiNET;
using PREACT;
using System.Globalization;
using UnityEngine;
using WUInity;
using WUInity.Visualization;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class OutputWindow
    {
        private static bool _isOpen;

        static OutputWindow()
        {

        }

        public static void Open()
        {
            if(!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }            
            
            if (PreactGUI.Engine.Simulation == null || PreactGUI.Engine.Simulation.State == Simulation.SimulationState.Error || PreactGUI.Engine.Simulation.State == Simulation.SimulationState.Initializing)
            {
                return;
            }

            ImGui.Begin("Output", ref _isOpen, ImGuiWindowFlags.NoDocking);
            Simulation sim = PreactGUI.Engine.Simulation;

            ImGui.SeparatorText("General");
            ImGui.BulletText($"{nameof(sim.Time.SimulationTime)}: {(int)sim.Time.SimulationTime} s");
            ImGui.BulletText($"{nameof(sim.Time.CurrentDateTime)}: {sim.Time.CurrentDateTime.ToString(CultureInfo.CurrentCulture)}");
            ImGui.BulletText($"{nameof(sim.Input.Population.Data.TotalPopulation)}: {sim.Input.Population.Data.TotalPopulation}");                        

            if (sim.Evacuation.PedestrianModule != null)
            {
                ImGui.BulletText($"People staying: {sim.Evacuation.PedestrianModule.GetPeopleStaying()}");
                ImGui.BulletText($"Total vehicles: {sim.Evacuation.PedestrianModule.GetTotalCars()}");
            }

            ImGui.SeparatorText("Simulation state");
            if (sim.State == Simulation.SimulationState.Running)
            {

                string pauseState = "Simulation running";
                string pauseButton = "Pause simulation";
                if (sim.IsPaused)
                {
                    pauseState = "Simulation paused";
                    pauseButton = "Cont. simulation";
                }

                ImGui.BulletText(pauseState);

                if (ImGui.Button(pauseButton)) { sim.TogglePause(); }

                if (ImGui.Button("Stop simulation")) { PreactGUI.WUInity.StopSimulations(); }

                if (ImGui.Button("Toggle realtime")) { sim.ToggleRealtime(); }

                ImGui.BulletText("Step execution time [ms]: " + sim.StepExecutionTime.ToString("F1", CultureInfo.InvariantCulture));
            }

            ImGui.SeparatorText("Weather");

            //Where in the record these come from. Worth stating, because with an ELMFIRE fire it is not the
            //scenario's own date: the case's weather is a historical peak fire-weather day drawn out of ERA5,
            //and the run reads that day's hours. Without saying so, a July scenario reporting August weather
            //looks like a bug.
            if (sim.Input.Weather != null && sim.Input.Weather.HasWeatherAnchor)
            {
                ImGui.TextDisabled($"From {sim.Input.Weather.WeatherAnchorDateTime:yyyy-MM-dd} in the record - "
                    + "the day the fire was computed against.");
            }

            ImGui.BulletText($"Temp.: {sim.Weather.GetTemperature():F1} C");
            ImGui.BulletText($"RH: {sim.Weather.GetRelativeHumidity():F0} %");
            ImGui.BulletText($"Wind speed: {sim.Weather.WindSpeed}");
            ImGui.BulletText($"Wind dir.: {sim.Weather.WindDirection}");

            //The fire-danger indices the weather manager has always computed and nothing ever displayed. They
            //are the reason the [Weather] seeds exist, so leaving them invisible made those keys look inert.
            ImGui.BulletText($"FFMC (hourly): {sim.Weather.FFMCHourly:F1}");
            ImGui.BulletText($"KBDI: {sim.Weather.KBDI:F0}");
            if (sim.Weather.FWI != null)
            {
                ImGui.BulletText($"FWI: {sim.Weather.FWI.FWI:F1}   (FFMC {sim.Weather.FWI.FFMC:F1}, "
                    + $"DMC {sim.Weather.FWI.DMC:F1}, DC {sim.Weather.FWI.DC:F0})");
            }
            ImGui.TextDisabled("Fire danger is reported, not used: ELMFIRE computes spread from the case's own");
            ImGui.TextDisabled("moisture rasters. DMC and DC start from the [Weather] seeds - there is no");
            ImGui.TextDisabled("antecedent marching over the record before the run begins.");

            ImGui.SeparatorText("Display controls");

            //Each toggle is disabled when the module behind it is off, rather than left clickable and
            //silently doing nothing: ToggleFire and ToggleSoot both return false and change nothing without
            //their module, which is indistinguishable from a button that does not work.
            ImGui.BeginDisabled(!sim.Input.PedestrianModule.Enabled);
            if (ImGui.Button("Toggle household rendering")) { PreactGUI.WUInity.ToggleHouseholdRendering(); }
            ImGui.EndDisabled();

            ImGui.BeginDisabled(!sim.Input.TrafficModule.Enabled);
            if (ImGui.Button("Toggle traffic rendering")) { PreactGUI.WUInity.ToggleTrafficRendering(); }
            ImGui.EndDisabled();

            ImGui.BeginDisabled(!sim.Input.WildfireModule.Enabled);
            if (ImGui.Button("Toggle wildfire spread rendering"))
            {
                PreactGUI.WUInity.ToggleFireSpreadRendering();
                PreactGUI.WUInity.SetSampleMode(DataSampleMode.None);
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(sim.Input.WildfireModule.Enabled
                    ? "The fire is drawn from the start of the run, so the first press hides it."
                    : "No wildfire module in this scenario.");
            }

            ImGui.BeginDisabled(!sim.Input.SmokeModule.Enabled);
            if (ImGui.Button("Toggle wildfire smoke rendering"))
            {
                PreactGUI.WUInity.ToggleSootRendering();
                PreactGUI.WUInity.SetSampleMode(DataSampleMode.None);
            }
            ImGui.EndDisabled();
            if (!sim.Input.SmokeModule.Enabled && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("No smoke module in this scenario.");
            }

            //Named for what it does. It was "Disable rendering", which it is not: the fire spread plane,
            //the households and the traffic are separate objects and it never touched any of them, so the
            //button appeared not to work on everything the eye was actually on.
            if (ImGui.Button("Hide domain overlays"))
            {
                PreactGUI.WUInity.SimulationDomainVisualizer.SetVisibility(false);
                PreactGUI.WUInity.SimulationDomainVisualizer.SetGPWVisibility(false);
                PreactGUI.WUInity.FireDomainVisualizer.SetVisibility(false);
            }
            ImGui.SameLine();
            //There was no way back from the old button short of restarting the run.
            if (ImGui.Button("Show domain overlays"))
            {
                PreactGUI.WUInity.SimulationDomainVisualizer.SetVisibility(true);
                PreactGUI.WUInity.FireDomainVisualizer.SetVisibility(true);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The domain and fire-domain planes underneath. Not the fire itself, the "
                    + "households or the traffic - those are the toggles above.");
            }

            ImGui.SeparatorText("Evacuation");

            if (sim.Input.PedestrianModule.Enabled && sim.Evacuation.PedestrianModule != null)
            {
                ImGui.BulletText($"People left: {sim.Evacuation.PedestrianModule.GetPeopleLeft()} /  {sim.Evacuation.PedestrianModule.GetTotalPopulation()}");
                ImGui.BulletText($"Vehicles reached: {sim.Evacuation.PedestrianModule.GetCarsReached()}");                
            }

            //vehicles still left
            if (sim.Input.TrafficModule.Enabled && sim.Evacuation.TrafficModule != null)
            {
                ImGui.BulletText($"Vehicles left: {sim.Evacuation.TrafficModule.GetNumberOfCarsInSystem()} / {sim.Evacuation.TrafficModule.GetTotalCarsSimulated()}");                
            }

            //The module, not just the scenario's intention to have one. A window that redraws every frame
            //turns one null dereference into a wall of identical exceptions, which buries whatever the
            //console was trying to say about why the module is missing - and it is missing for a reason that
            //is always logged.
            if (sim.Input.PedestrianModule.Enabled && sim.Evacuation.PedestrianModule != null)
            {
                for (int i = 0; i < sim.Evacuation.Destinations.Count; i++)
                {
                    string name = sim.Evacuation.Destinations[i].Name;
                    ImGui.BulletText($"{name}: {sim.Evacuation.Destinations[i].CurrentPeople} ({ sim.Evacuation.Destinations[i].Vehicles.Count})");

                }
                ImGui.BulletText($"Total evacuated: {sim.Evacuation.GetTotalEvacuated()} / {sim.Evacuation.PedestrianModule.GetTotalPopulation() - sim.Evacuation.PedestrianModule.GetPeopleStaying()}");
            }
            else if (sim.Input.PedestrianModule.Enabled)
            {
                ImGui.TextDisabled("The pedestrian module was not created; see the console.");
            }

            ImGui.SeparatorText("Wildfire spread");

            //Not gated on Running any more. The fire stays on screen after the run ends - which is when it
            //is most worth looking at - and the cell count and display mode were both unreachable then.
            if (sim.Input.WildfireModule.Enabled && sim.Hazards.Wildfire != null)
            {
                ImGui.BulletText($"Cells burned so far: {sim.Hazards.Wildfire.GetActiveCellCount()}");
                ImGui.TextDisabled("The fire is drawn as it arrives, cell by cell, against the same clock as");
                ImGui.TextDisabled("the evacuation. Cells stay lit once burned, so this is the burned area");
                ImGui.TextDisabled("growing rather than an instantaneous fire front.");

                ImGui.SeparatorText("Fire display mode");
                if (ImGui.Button("Fireline intensity"))
                {
                    PreactGUI.WUInity.FireRenderer.SetFireDisplayMode(FireRenderer.FireDisplayMode.FirelineIntensity);
                }

                //Disabled rather than removed: it is a real ELMFIRE raster, just not one this module reads.
                //Clickable, it set a display mode whose data source returns nothing, which froze the fire on
                //screen with no indication why.
                ImGui.SameLine();
                ImGui.BeginDisabled(true);
                ImGui.Button("Fuel model");
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("An imported fire carries arrival time, spread rate, spread direction "
                        + "and intensity - the fuel model was an input to the fire, not an output of it, so "
                        + "there is nothing here to draw.");
                }
            }
            else if (sim.Input.WildfireModule.Enabled)
            {
                ImGui.TextDisabled("The wildfire module was not created; see the console.");
            }

            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }
    }
}
