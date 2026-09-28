
using ImGuiNET;
using PREACT;
using System.Globalization;
using UnityEngine;
using WUInity.Visualization;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Results &gt; Live output: the running simulation's clock, people, vehicles, weather and fire, and the
    /// controls for what is drawn.
    /// </summary>
    /// <remarks>
    /// Was "Output", and drew nothing at all - not even its frame - while the simulation was initialising, which
    /// with ELMFIRE is where the fire is computed and can last hours, or after an error: the window was open and
    /// invisible exactly when someone was looking for it. It now says what phase the run is in, and Stop is
    /// always there while one is going.
    /// </remarks>
    public static class LiveOutputWindow
    {
        private static bool _isOpen;

        public static bool IsOpen { get => _isOpen; }

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
            
            PreactGUI.PlaceNextWindow(new Vector2(420f, 560f));
            if (!ImGui.Begin("Live output###LiveOutput", ref _isOpen, PreactGUI.ToolWindowFlags))
            {
                ImGui.End();
                CloseIfClosed();
                return;
            }

            Simulation sim = PreactGUI.Engine.Simulation;
            if (sim == null)
            {
                ImGui.TextDisabled("Nothing has run yet. Run > Run simulation starts one.");
                ImGui.End();
                CloseIfClosed();
                return;
            }

            if (sim.State == Simulation.SimulationState.Initializing)
            {
                ImGui.TextWrapped("Preparing the run: creating the modules. With an ELMFIRE fire, this is where the fire "
                    + "is computed - minutes to hours. The console shows its progress.");
                if (ImGui.Button("Stop")) { RunSimulationWindow.Stop(); }
                ImGui.End();
                CloseIfClosed();
                return;
            }

            if (sim.State == Simulation.SimulationState.Error)
            {
                Fields.Caution("The run stopped with an error; the console says why.");
                if (ImGui.Button("Open console")) { ConsoleWindow.Open(); }
                ImGui.End();
                CloseIfClosed();
                return;
            }

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
                ImGui.SameLine();
                if (ImGui.Button("Stop simulation")) { RunSimulationWindow.Stop(); }
                ImGui.SameLine();
                if (ImGui.Button("Real-time playback")) { sim.ToggleRealtime(); }

                ImGui.BulletText("Step execution time [ms]: " + sim.StepExecutionTime.ToString("F1", CultureInfo.InvariantCulture));
            }

            ImGui.SeparatorText("Weather");

            //Where in the record these come from. Worth stating, because with an ELMFIRE fire it is not the
            //scenario's own date: the case's weather is a historical peak fire-weather day drawn out of ERA5,
            //and the run reads that day's hours. Without saying so, a July scenario reporting August weather
            //looks like a bug.
            //The run's own weather manager, not the scenario: an ELMFIRE run anchors its weather to the fire's day
            //at run time without writing that into the scenario (contract C4).
            if (sim.Weather.HasWeatherAnchor)
            {
                ImGui.TextDisabled($"From {sim.Weather.WeatherAnchor:yyyy-MM-dd} in the record - "
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
            }
            ImGui.EndDisabled();
            if (!sim.Input.SmokeModule.Enabled && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("No smoke module in this scenario.");
            }

            ImGui.TextDisabled("Map overlays (population, roads, painted areas, results): View > Map layers.");

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

                ImGui.TextDisabled("Drawn as fireline intensity.");
            }
            else if (sim.Input.WildfireModule.Enabled)
            {
                ImGui.TextDisabled("The wildfire module was not created; see the console.");
            }

            ImGui.End();
            CloseIfClosed();
        }

        private static void CloseIfClosed()
        {
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }
    }
}
