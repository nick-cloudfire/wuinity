using System;
using System.Collections.Generic;
using ImGuiNET;
using PREACT;
using UnityEngine;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Run &gt; Run simulation: what stands in the way, the run's settings, and - once started - how far along
    /// it is, with Pause and Stop. Workflow step 11.
    /// </summary>
    /// <remarks>
    /// The Run tab this replaces started a run on whatever was in memory without saving it, checked nothing
    /// (<c>DataStatus.HaveInput</c> is set by the engine and read by no one), offered the Parallel mode the
    /// engine never allocates simulations for, and closed the editor it was in. While ELMFIRE computed - minutes
    /// to hours - the output window drew nothing at all and there was no Stop.
    /// </remarks>
    public static class RunSimulationWindow
    {
        private static bool _isOpen;
        private static bool _subscribed;
        private static bool _runAnyway;
        private static int _numberOfRuns = 1;
        private static bool _stopAfterConverging = true;
        private static int _convergenceMinSequence = 10;
        private static float _convergenceMaxDifference = 0.02f;

        //The scenario as saved just before the run, to tell afterwards whether the run changed it in memory.
        private static string _preRunSnapshot;
        private static string _preRunPath;
        private static DateTime _startedAt;
        private static string _lastResult = string.Empty;
        private static bool _stopRequested;

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
            Subscribe();

            //The findings are the parser's, and a run needs them current.
            if (ScenarioSession.HasInput && !WorkflowService.RequirementsFresh && !ScenarioSession.IsBusy)
            {
                ScenarioCheckWindow.Check();
            }
        }

        private static void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;
            if (PreactGUI.WUInity != null)
            {
                PreactGUI.WUInity.RunFinished += OnRunFinished;
            }
        }

        /// <summary>F5: opens this window, or starts the run when it is already open and nothing is in the way.</summary>
        public static void Shortcut()
        {
            if (_isOpen && CanStart(out string _))
            {
                Start();
            }
            else
            {
                Open();
            }
        }

        private static bool CanStart(out string why)
        {
            why = null;
            if (!ScenarioSession.HasInput) { why = "No scenario is open."; return false; }
            if (ScenarioSession.IsBusy) { why = ScenarioSession.BusyTooltip; return false; }
            if (WorkflowService.Model.RunBlockers.Count > 0 && !_runAnyway)
            {
                why = "Blocked: " + WorkflowService.Model.RunBlockers[0];
                return false;
            }
            return true;
        }

        private static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(560f, 460f));
            if (ImGui.Begin("Run simulation###RunSimulation", ref _isOpen, PreactGUI.ToolWindowFlags))
            {
                if (ScenarioSession.SimulationActive)
                {
                    DrawRunning();
                }
                else
                {
                    DrawPreflight();
                }
            }
            ImGui.End();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void DrawPreflight()
        {
            if (!ScenarioSession.HasInput)
            {
                ImGui.TextDisabled("No scenario is open.");
                return;
            }

            ScenarioWorkflow model = WorkflowService.Model;

            ImGui.SeparatorText("Before it can run");
            if (model.RunBlockers.Count == 0)
            {
                Fields.Ok("Nothing in the way.");
            }
            foreach (string blocker in model.RunBlockers)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Fields.Alert);
                ImGui.TextWrapped(blocker);
                ImGui.PopStyleColor();
            }
            foreach (string warning in model.RunWarnings)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Fields.Warning);
                ImGui.TextWrapped(warning);
                ImGui.PopStyleColor();
            }
            if (model.RunBlockers.Count + model.RunWarnings.Count > 0 && ImGui.SmallButton("Show the first in the workflow###RunShowWorkflow"))
            {
                //Each step's row has the button that deals with it.
                string first = model.RunBlockers.Count > 0 ? model.RunBlockers[0] : model.RunWarnings[0];
                ScenarioWorkflowWindow.Focus(StepNamedIn(first));
            }

            if (ScenarioCheckWindow.Checking)
            {
                ImGui.TextDisabled("Checking the scenario file...");
            }
            else if (!WorkflowService.RequirementsFresh)
            {
                if (ImGui.SmallButton("Check the scenario file###RunCheck")) ScenarioCheckWindow.Check();
                ImGui.SameLine();
                ImGui.TextDisabled("The parser has not seen your latest edits.");
            }

            ImGui.SeparatorText("Settings");
            ImGui.SetNextItemWidth(160f);
            ImGui.SliderInt("Number of runs", ref _numberOfRuns, 1, 100);
            if (_numberOfRuns > 1)
            {
                ImGui.Checkbox("Stop once evacuation time has converged", ref _stopAfterConverging);
                if (_stopAfterConverging)
                {
                    ImGui.SetNextItemWidth(160f);
                    ImGui.SliderInt("Runs in a row within the tolerance", ref _convergenceMinSequence, 1, 30);
                    ImGui.SetNextItemWidth(160f);
                    ImGui.SliderFloat("Tolerance (relative change)", ref _convergenceMaxDifference, 0.01f, 0.2f);
                }
            }
            //Serial only. Parallel is not implemented by the engine (it never allocates the simulations it would
            //run), and ParallelProcess starts preact.exe from PATH on the .wui on disk.
            ImGui.TextDisabled("Runs one after another in this process, drawn on the map as they go.");

            if (model.RunBlockers.Count > 0)
            {
                ImGui.Checkbox("Run anyway, knowing it may stop part-way###RunAnyway", ref _runAnyway);
            }

            ImGui.Separator();
            bool canStart = CanStart(out string why);
            ImGui.BeginDisabled(!canStart);
            if (ImGui.Button(ScenarioSession.IsDirty ? "Save and run (F5)" : "Run (F5)"))
            {
                Start();
            }
            ImGui.EndDisabled();
            if (!canStart && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(why);
            }
            if (ImGui.IsItemHovered() && canStart)
            {
                ImGui.SetTooltip("Saves the scenario first: what runs is what is on disk, so the run can be repeated from the file.");
            }

            if (!string.IsNullOrEmpty(_lastResult))
            {
                ImGui.SeparatorText("Last run");
                if (PreactGUI.WUInity.LastRunFailed == true) Fields.Caution(_lastResult);
                else ImGui.TextWrapped(_lastResult);
                if (ImGui.Button("Results")) ResultsWindow.Open();
                ImGui.SameLine();
                if (ImGui.Button("Live output")) LiveOutputWindow.Open();
                ImGui.SameLine();
                if (ImGui.Button("Console")) ConsoleWindow.Open();
            }
        }

        private static void DrawRunning()
        {
            Simulation sim = PreactGUI.Engine.Simulation;
            ImGui.Text(ScenarioSession.DisplayName + ", started " + _startedAt.ToString("HH:mm:ss"));

            if (sim == null || sim.State == Simulation.SimulationState.Initializing)
            {
                ImGui.ProgressBar(UnityEngine.Mathf.PingPong(UnityEngine.Time.realtimeSinceStartup * 0.5f, 1f),
                    new Vector2(-1, 0), "preparing");
                ImGui.TextWrapped("Creating the modules. With an ELMFIRE fire this is where the fire is computed, "
                    + "which can take minutes to hours; the console shows ELMFIRE's progress.");
            }
            else if (sim.State == Simulation.SimulationState.Running)
            {
                double total = (sim.Input.Simulation.EndDateTime - sim.Input.Simulation.StartDateTime).TotalSeconds;
                float fraction = total > 0 ? (float)Math.Min(1.0, sim.Time.SimulationTime / total) : 0f;
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{sim.Time.SimulationTime / 3600.0:0.0} of {total / 3600.0:0.0} h");
                ImGui.Text(sim.IsPaused ? "Paused." : "Running.");

                if (ImGui.Button(sim.IsPaused ? "Resume" : "Pause")) { sim.TogglePause(); }
                ImGui.SameLine();
                if (ImGui.Button("Real-time playback")) { sim.ToggleRealtime(); }
            }
            else
            {
                ImGui.TextDisabled("Finishing (" + sim.State + ")...");
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop"))
            {
                Stop();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Stops the run. While ELMFIRE is computing the fire, ELMFIRE is stopped too.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Live output")) LiveOutputWindow.Open();
        }

        //The model words each blocker "Step N (Title): ..."; anything else is the run's own row.
        private static WorkflowStepId StepNamedIn(string text)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(text ?? string.Empty, @"^Step (\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && Enum.IsDefined(typeof(WorkflowStepId), n))
            {
                return (WorkflowStepId)n;
            }
            return WorkflowStepId.RunSimulation;
        }

        /// <summary>The run is past initialisation and can be paused or resumed (Run &gt; Pause).</summary>
        public static bool CanPause
        {
            get
            {
                Simulation sim = PreactGUI.Engine?.Simulation;
                return ScenarioSession.SimulationActive && sim != null && sim.State == Simulation.SimulationState.Running;
            }
        }

        public static bool IsPaused { get => CanPause && PreactGUI.Engine.Simulation.IsPaused; }

        public static void TogglePause()
        {
            if (CanPause)
            {
                PreactGUI.Engine.Simulation.TogglePause();
            }
        }

        /// <summary>
        /// Stops the run. The engine's close also kills a running ELMFIRE process tree, so a stop while the fire is
        /// being computed takes effect at once instead of after ELMFIRE finishes.
        /// </summary>
        public static void Stop()
        {
            if (!ScenarioSession.SimulationActive)
            {
                return;
            }
            _stopRequested = true;
            PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "Stop requested from the GUI.");
            PreactGUI.WUInity.StopSimulations();
        }

        private static void Start()
        {
            if (!CanStart(out string why))
            {
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning, "Not started: " + why);
                return;
            }

            //What runs is what is on disk, so the run can be repeated from the file - and a campaign started
            //afterwards reads the same scenario.
            if (ScenarioSession.IsDirty || !GuiFiles.Exists(ScenarioSession.FilePath))
            {
                if (!ScenarioSession.Save())
                {
                    return;
                }
            }

            _preRunSnapshot = ScenarioSession.Serialise(ScenarioSession.Input);
            _preRunPath = ScenarioSession.FilePath;
            _startedAt = DateTime.Now;
            _lastResult = string.Empty;
            _stopRequested = false;

            var task = new EngineTask(EngineTask.ExecutionMode.Serial, _numberOfRuns, 0, 1, true,
                _stopAfterConverging, _convergenceMinSequence, _convergenceMaxDifference);

            ConsoleWindow.Open();
            LiveOutputWindow.Open();
            PreactGUI.WUInity.RunSimulation(task);
            WorkflowService.Invalidate();
        }

        /// <summary>On the main thread, once the run's task has finished however it finished.</summary>
        private static void OnRunFinished()
        {
            TimeSpan took = DateTime.Now - _startedAt;
            Simulation sim = PreactGUI.Engine.Simulation;
            bool failed = PreactGUI.WUInity.LastRunFailed == true;
            if (_stopRequested)
            {
                //A stop during the fire ends the run as "did not start" in the engine's terms; to the person who
                //pressed Stop it is simply stopped.
                _lastResult = $"Stopped after {took.TotalMinutes:0.#} min"
                    + (sim != null && sim.HaveResults ? "; what had been simulated is saved." : ", before the simulation started.");
            }
            else
            {
                _lastResult = failed
                    ? $"Ended in an error after {took.TotalMinutes:0.#} min - the console has what led up to it."
                    : $"Finished in {took.TotalMinutes:0.#} min" + (sim != null ? $" ({sim.State})." : ".");
            }
            _stopRequested = false;

            RestoreScenarioAfterRun();
            ResultsWindow.Rescan();
            WorkflowService.Invalidate();
        }

        /// <summary>
        /// Puts back the scenario the run was started with, when the run changed it in memory.
        /// </summary>
        /// <remarks>
        /// A run writes paths it derives into the scenario it runs - k-PERIL's wind rasters, the imported
        /// arrival times, the weather file and anchor - and a later Save then pins them into the .wui, after
        /// which "left empty, the case's own is used" is no longer true. The scenario was saved just before
        /// the run, so the file is exactly the pre-run scenario, and reading it back undoes those writes.
        /// </remarks>
        private static void RestoreScenarioAfterRun()
        {
            if (!ScenarioSession.HasInput || _preRunSnapshot == null || ScenarioSession.FilePath != _preRunPath)
            {
                return;
            }

            string now = ScenarioSession.Serialise(ScenarioSession.Input);
            if (now == _preRunSnapshot)
            {
                return;
            }

            // V1-INTEGRATION: C4 - once runs no longer mutate PREACTInput, the two are always equal and this never fires.
            var changed = new List<string>();
            string[] before = _preRunSnapshot.Split('\n');
            string[] after = now.Split('\n');
            var beforeSet = new HashSet<string>(before);
            foreach (string line in after)
            {
                if (!beforeSet.Contains(line) && line.Contains("=") && changed.Count < 6) changed.Add(line.Trim());
            }

            PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "The run wrote into the scenario it ran ("
                + string.Join("; ", changed) + "). Reading the scenario back from " + System.IO.Path.GetFileName(_preRunPath)
                + ", saved just before the run, so none of that ends up saved into it.");

            if (!ScenarioSession.IsBusy)
            {
                ScenarioSession.Load(_preRunPath);
            }
        }
    }
}
