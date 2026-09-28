using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ImGuiNET;
using PREACT.Input;
using UnityEngine;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// What the scenario's own parser says it still needs, filed under the workflow step each item belongs to,
    /// with a way to that step.
    /// </summary>
    /// <remarks>
    /// The checklist this replaces opened itself after every load with anything outstanding, listed items that
    /// could not be clicked, and offered one "Open scenario editor" button for all of them. Its Re-check wrote a
    /// temporary .wui and parsed it - population, rasters and all - on the main thread. The parse now runs on a
    /// worker, in memory (as <see cref="PREACTInput.Revalidate"/> does), on a copy of the scenario serialised
    /// beforehand, and the findings feed the workflow panel, which is where they are normally read; this window
    /// is the full list. The parse also publishes the checklist the engine's run gate reads, so a check just
    /// before a run is what makes that gate judge the scenario as it is now.
    /// </remarks>
    public static class ScenarioCheckWindow
    {
        private static bool _isOpen;
        private static bool _onlyRequired = true;
        private static volatile bool _checking;
        private static string _note = string.Empty;
        private static string _scenario = string.Empty;

        /// <summary>A check is parsing a copy of the scenario on a worker.</summary>
        public static bool Checking { get => _checking; }

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
        }

        public static void OpenAndCheck()
        {
            Open();
            Check();
        }

        /// <summary>Called after a load; the findings the load produced are already in the workflow.</summary>
        public static void NoteLoaded(string scenarioName)
        {
            _scenario = scenarioName;
            _note = string.Empty;
        }

        /// <summary>
        /// Parses the scenario as it stands, unsaved edits included, and hands the findings to the workflow.
        /// <paramref name="done"/>, when given, is called on the main thread afterwards with whether nothing
        /// required is outstanding (false also when the check could not run, or was already running).
        /// </summary>
        public static void Check(Action<bool> done = null)
        {
            PREACTInput input = ScenarioSession.Input;
            if (input == null || _checking)
            {
                done?.Invoke(false);
                return;
            }

            //Serialised here, on the main thread, so the worker never reads the live scenario. Checked against
            //its own folder, because half the checks resolve paths relative to it - in memory, so nothing is
            //written there.
            string[] lines = PREACTInputWriter.Write(input);
            string root = input.RootFolder;
            int generation = ScenarioSession.EditGeneration;
            _checking = true;
            _note = "Checking...";

            Task.Run(() =>
            {
                List<PREACTInput.InputRequirement> found = null;
                string failure = null;
                try
                {
                    PREACTInput.LoadFromLines(lines, root, out bool _);
                    found = new List<PREACTInput.InputRequirement>(PREACTInput.Requirements);
                }
                catch (Exception e)
                {
                    failure = e.Message;
                }

                PreactGUI.Post(() =>
                {
                    _checking = false;
                    if (failure != null)
                    {
                        _note = "Could not check the scenario: " + failure;
                        done?.Invoke(false);
                        return;
                    }

                    WorkflowService.TakeRequirements(found, generation);
                    int required = found.FindAll(r => r.Critical).Count;
                    _note = $"Checked at {DateTime.Now:HH:mm:ss}: " + (required == 0 ? "nothing required is outstanding." : $"{required} required item(s).");
                    PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "Scenario check: " + _note);
                    done?.Invoke(required == 0);
                });
            });
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(560f, 420f));
            if (ImGui.Begin("Scenario check###ScenarioCheck", ref _isOpen, PreactGUI.ToolWindowFlags))
            {
                DrawContents();
            }
            ImGui.End();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void DrawContents()
        {
            if (!ScenarioSession.HasInput)
            {
                ImGui.TextDisabled("No scenario is open.");
                return;
            }

            IReadOnlyList<PREACTInput.InputRequirement> items = WorkflowService.Requirements;
            int required = 0, optional = 0;
            foreach (PREACTInput.InputRequirement r in items)
            {
                if (r.Critical) ++required; else ++optional;
            }

            ImGui.Text(ScenarioSession.DisplayName);
            if (required == 0)
            {
                Fields.Ok("The scenario file has everything its parser requires.");
            }
            else
            {
                Fields.Caution($"{required} item(s) the parser requires before this scenario can run.");
            }
            if (optional > 0)
            {
                ImGui.TextDisabled($"{optional} optional item(s) fell back to defaults.");
            }
            if (!WorkflowService.RequirementsFresh)
            {
                Fields.Warn("Found before your latest edits - check again to see where things stand now.");
            }
            ImGui.TextDisabled("The workflow panel shows the same findings under the step each belongs to, with the checks it makes of its own.");

            ImGui.Separator();
            ImGui.Checkbox("Only what is required", ref _onlyRequired);

            ImGui.BeginChild("checkitems", new Vector2(0, -ImGui.GetFrameHeightWithSpacing() * 2f), (ImGuiChildFlags)1);

            //Grouped by workflow step, in step order, so each group sits beside the way to fix it.
            var byStep = new SortedDictionary<int, List<PREACTInput.InputRequirement>>();
            foreach (PREACTInput.InputRequirement r in items)
            {
                if (_onlyRequired && !r.Critical) continue;
                int step = (int)ScenarioWorkflow.StepFor(r);
                if (!byStep.TryGetValue(step, out var list)) byStep[step] = list = new List<PREACTInput.InputRequirement>();
                list.Add(r);
            }

            foreach (KeyValuePair<int, List<PREACTInput.InputRequirement>> group in byStep)
            {
                WorkflowStepId id = (WorkflowStepId)group.Key;
                ImGui.PushID("check" + group.Key);
                ImGui.SeparatorText($"Step {group.Key}: {ScenarioWorkflow.TitleOf(id)}");

                WorkflowStep step = WorkflowService.Step(id);
                if (step?.Primary != null)
                {
                    ImGui.BeginDisabled(!step.Primary.Enabled);
                    if (ImGui.SmallButton("Go to: " + step.Primary.Label + "###goto"))
                    {
                        WorkflowService.Perform(step.Primary.Id);
                    }
                    ImGui.EndDisabled();
                    if (!step.Primary.Enabled && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip(step.Primary.Tooltip);
                    }
                    if (id != WorkflowStepId.None) ImGui.SameLine();
                }
                if (id != WorkflowStepId.None && ImGui.SmallButton("Show in the workflow###showstep"))
                {
                    ScenarioWorkflowWindow.Focus(id);
                }

                foreach (PREACTInput.InputRequirement item in group.Value)
                {
                    if (item.Critical) ImGui.TextColored(Fields.Alert, "[required]");
                    else ImGui.TextDisabled("[default]");
                    ImGui.SameLine();
                    ImGui.TextWrapped($"[{(string.IsNullOrEmpty(item.Section) ? "Scenario" : item.Section)}] {item.Key} - {item.Message}");
                }
                ImGui.PopID();
            }

            if (byStep.Count == 0)
            {
                ImGui.TextDisabled("Nothing outstanding.");
            }

            ImGui.EndChild();

            ImGui.BeginDisabled(_checking);
            if (ImGui.Button(_checking ? "Checking..." : "Check again"))
            {
                Check();
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Parses the scenario as it stands now, unsaved edits included, the way loading it would.");
            }
            if (!string.IsNullOrEmpty(_note))
            {
                ImGui.SameLine();
                ImGui.TextDisabled(_note);
            }
        }
    }
}
