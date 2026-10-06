using System.Collections.Generic;
using ImGuiNET;
using UnityEngine;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The scenario workflow panel: every step from an empty folder to a probabilistic campaign, in order,
    /// each with where it stands, what is in the way, and the one thing to do next.
    /// </summary>
    /// <remarks>
    /// Docked on the left by default and open from the start. It draws the model <see cref="WorkflowService"/>
    /// keeps - nothing is probed or computed here, so it costs the same every frame whatever the scenario
    /// holds. Every button goes through <see cref="WorkflowService.Perform"/>, the same path as the menus,
    /// so a row and its menu item cannot do different things.
    /// </remarks>
    public static class ScenarioWorkflowWindow
    {
        /// <summary>The window's ID, for the default dock layout.</summary>
        public const string WindowId = "###Workflow";

        private static bool _isOpen = true;
        private static bool _registered;

        private static readonly HashSet<WorkflowStepId> _expanded = new HashSet<WorkflowStepId>();

        //A row to scroll to and open on the next frame ("Go to step N", the scenario check's links).
        private static WorkflowStepId _focus = WorkflowStepId.None;
        private static bool _focusWindow;

        private static readonly Vector4 BlockedColour = new Vector4(0.55f, 0.55f, 0.58f, 1f);
        private static readonly Vector4 RunningColour = new Vector4(0.35f, 0.6f, 0.95f, 1f);
        private static readonly Vector4 BadgeText = new Vector4(0.08f, 0.08f, 0.08f, 1f);

        public static bool IsOpen { get => _isOpen; }

        /// <summary>Registers the panel; called once at start-up so it is there from the first frame.</summary>
        public static void Register()
        {
            if (!_registered)
            {
                PreactGUI.DrawWindow(Draw);
                _registered = true;
            }
        }

        public static void Open()
        {
            Register();
            _isOpen = true;
        }

        public static void Toggle()
        {
            if (_isOpen) _isOpen = false;
            else Open();
        }

        /// <summary>Opens the panel with <paramref name="id"/>'s row expanded and scrolled into view.</summary>
        public static void Focus(WorkflowStepId id)
        {
            Open();
            if (id != WorkflowStepId.None)
            {
                _expanded.Add(id);
            }
            _focus = id;
            _focusWindow = true;
        }

        private static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            if (_focusWindow)
            {
                ImGui.SetNextWindowFocus();
                _focusWindow = false;
            }

            ImGui.SetNextWindowSize(new Vector2(380f, 720f), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Scenario workflow" + WindowId, ref _isOpen, PreactGUI.ToolWindowFlags))
            {
                if (ScenarioSession.HasInput)
                {
                    DrawHeader();
                    DrawSteps();
                }
                else
                {
                    DrawNoScenario();
                }
            }
            ImGui.End();
        }

        // ------------------------------------------------------------------ no scenario

        private static void DrawNoScenario()
        {
            ImGui.TextWrapped("No scenario is open.");
            ImGui.Spacing();

            bool busy = ScenarioSession.IsBusy;
            ImGui.BeginDisabled(busy);
            if (ImGui.Button("New scenario...###WfNew"))
            {
                ScenarioSession.RequestNew();
            }
            ImGui.SameLine();
            if (ImGui.Button("Open scenario...###WfOpen"))
            {
                ScenarioSession.RequestOpen();
            }
            ImGui.EndDisabled();
            if (busy)
            {
                ImGui.TextDisabled(ScenarioSession.BusyTooltip);
            }

            List<string> recent = global::WUInity.RecentScenario.Recent;
            if (recent.Count > 0)
            {
                ImGui.SeparatorText("Recent");
                for (int i = 0; i < recent.Count && i < 5; ++i)
                {
                    string path = recent[i];
                    if (ImGui.Selectable(System.IO.Path.GetFileName(path) + "###wfrecent" + i, false,
                        busy ? ImGuiSelectableFlags.Disabled : ImGuiSelectableFlags.None))
                    {
                        ScenarioSession.RequestOpen(path);
                    }
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip(path);
                    }
                }
            }

            ImGui.SeparatorText("How this works");
            ImGui.TextWrapped("A new scenario is a folder, a name, an area on the map and a time window. Everything else "
                + "- roads, population, fuels, the fire case, the areas to protect - is prepared here afterwards, one "
                + "step at a time, and each step says what it still needs.");

            ExternalToolsSnapshot tools = ToolsService.Current;
            if (tools.Probed && (string.IsNullOrEmpty(tools.ElmfireExe) || string.IsNullOrEmpty(tools.GdalBin)
                || string.IsNullOrEmpty(tools.SumoBin)))
            {
                ImGui.Spacing();
                Fields.Warn("Some external tools were not found; the steps that need them will say so.");
                if (ImGui.SmallButton("External tools and keys...###WfTools"))
                {
                    ExternalToolsWindow.Open();
                }
            }
        }

        // ------------------------------------------------------------------ header

        private static void DrawHeader()
        {
            ImGui.TextUnformatted(ScenarioSession.DisplayName);
            string folder = ScenarioSession.RootFolder;
            if (!string.IsNullOrEmpty(folder))
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextDisabled(folder);
                ImGui.PopTextWrapPos();
            }

            if (ScenarioSession.IsDirty)
            {
                ImGui.TextColored(Fields.Warning, "Unsaved changes");
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(ScenarioSession.DirtySummary);
                }
                ImGui.SameLine();
                ImGui.BeginDisabled(ScenarioSession.EditingLocked);
                if (ImGui.SmallButton("Save###WfSave"))
                {
                    ScenarioSession.Save();
                }
                ImGui.EndDisabled();
                if (ScenarioSession.EditingLocked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(ScenarioSession.BusyTooltip);
                }
            }
            else
            {
                ImGui.TextDisabled("Saved");
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Check scenario###WfCheck"))
            {
                WorkflowService.Perform(WorkflowAction.CheckScenario);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Parses the scenario as it stands, unsaved edits included, and files what it finds under "
                    + "the steps below.");
            }
            ImGui.SameLine();
            if (ImGui.SmallButton("All settings###WfAll"))
            {
                ScenarioEditorWindow.Open();
            }

            DrawBusyLine();
            ImGui.Separator();
        }

        private static void DrawBusyLine()
        {
            if (ScenarioSession.StepActive)
            {
                string title = ScenarioDataSteps.CurrentTitle;
                ImGui.TextColored(RunningColour, string.IsNullOrEmpty(title) ? "Preparing data..." : title + "...");
                float f = ScenarioDataSteps.ProgressFraction;
                if (f > 0f)
                {
                    ImGui.ProgressBar(f, new Vector2(-1f, 0f));
                }
                if (ImGui.SmallButton("Show progress###WfProgress"))
                {
                    ScenarioDataSteps.ProgressWindowOpen = true;
                }
            }
            else if (ScenarioSession.SimulationActive)
            {
                ImGui.TextColored(RunningColour, "A simulation is running.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Show###WfRunShow"))
                {
                    RunSimulationWindow.Open();
                }
            }
            else if (ScenarioSession.CampaignActive)
            {
                ImGui.TextColored(RunningColour, "A campaign is running.");
                ImGui.SameLine();
                if (ImGui.SmallButton("Show###WfCampaignShow"))
                {
                    CampaignMonitorWindow.Open();
                }
            }
            else if (ScenarioCheckWindow.Checking)
            {
                ImGui.TextColored(RunningColour, "Checking the scenario...");
            }
        }

        // ------------------------------------------------------------------ steps

        private static void DrawSteps()
        {
            IReadOnlyList<WorkflowStep> steps = WorkflowService.Model.Steps;
            if (steps == null || steps.Count == 0)
            {
                ImGui.TextDisabled("Working out where the scenario stands...");
                return;
            }

            ImGui.BeginChild("wfsteps", Vector2.zero);
            float badgeWidth = ImGui.CalcTextSize("Needs attention").x + 2f * ImGui.GetStyle().FramePadding.x;

            for (int i = 0; i < steps.Count; ++i)
            {
                WorkflowStep step = steps[i];
                if (step == null) continue;

                ImGui.PushID((int)step.Id);
                DrawStep(step, badgeWidth);
                ImGui.PopID();

                if (i < steps.Count - 1)
                {
                    ImGui.Separator();
                }
            }

            if (_focus != WorkflowStepId.None)
            {
                //The row was not drawn (the model has no such step); stop trying.
                _focus = WorkflowStepId.None;
            }

            ImGui.EndChild();
        }

        private static void DrawStep(WorkflowStep step, float badgeWidth)
        {
            if (_focus == step.Id)
            {
                ImGui.SetScrollHereY(0.1f);
                _focus = WorkflowStepId.None;
            }

            bool expanded = _expanded.Contains(step.Id);
            bool hasDetail = step.Issues.Count > 0 || step.Secondary.Count > 0 || step.Status == StepStatus.Blocked;

            //Line 1: expand arrow, status, number and title.
            ImGui.BeginDisabled(!hasDetail);
            if (ImGui.ArrowButton("###expand", expanded && hasDetail ? ImGuiDir.Down : ImGuiDir.Right))
            {
                if (expanded) _expanded.Remove(step.Id);
                else _expanded.Add(step.Id);
                expanded = !expanded;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            Badge(step, badgeWidth);
            ImGui.SameLine();
            Vector4 titleColour = step.Status == StepStatus.Optional || step.Status == StepStatus.Blocked
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]
                : ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
            ImGui.TextColored(titleColour, step.Number + ". " + step.Title);

            //Line 2: the one-line summary.
            if (!string.IsNullOrEmpty(step.Summary))
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextDisabled(step.Summary);
                ImGui.PopTextWrapPos();
            }

            if (step.Status == StepStatus.Running && ScenarioDataSteps.Owner == step.Id)
            {
                float f = ScenarioDataSteps.ProgressFraction;
                ImGui.ProgressBar(f > 0f ? f : 0f, new Vector2(-1f, 0f),
                    string.IsNullOrEmpty(ScenarioDataSteps.CurrentTitle) ? string.Empty : ScenarioDataSteps.CurrentTitle);
            }

            //Line 3: the primary action, and a count of what is inside when collapsed.
            if (step.Primary != null)
            {
                ActionButton(step.Primary, false);
            }
            if (!expanded)
            {
                int errors = 0, warnings = 0;
                foreach (StepIssue issue in step.Issues)
                {
                    if (issue.Level == IssueLevel.Error) ++errors;
                    else if (issue.Level == IssueLevel.Warning) ++warnings;
                }
                if (errors + warnings > 0)
                {
                    if (step.Primary != null) ImGui.SameLine();
                    string label = errors > 0 && warnings > 0 ? $"{errors} error(s), {warnings} warning(s)"
                        : errors > 0 ? $"{errors} error(s)" : $"{warnings} warning(s)";
                    if (ImGui.SmallButton(label + "###showissues"))
                    {
                        _expanded.Add(step.Id);
                    }
                }
            }

            if (expanded && hasDetail)
            {
                ImGui.Indent();
                DrawDetail(step);
                ImGui.Unindent();
            }
        }

        private static void DrawDetail(WorkflowStep step)
        {
            if (step.Status == StepStatus.Blocked && !string.IsNullOrEmpty(step.BlockedBy))
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextColored(BlockedColour, "Blocked: " + step.BlockedBy);
                ImGui.PopTextWrapPos();
                if (step.BlockedByStep != WorkflowStepId.None && step.BlockedByStep != step.Id)
                {
                    if (ImGui.SmallButton("Go to step " + (int)step.BlockedByStep + "###gotoblocker"))
                    {
                        Focus(step.BlockedByStep);
                    }
                }
            }

            for (int i = 0; i < step.Issues.Count; ++i)
            {
                StepIssue issue = step.Issues[i];
                ImGui.PushID(i);

                Vector4 colour = issue.Level == IssueLevel.Error ? Fields.Alert
                    : issue.Level == IssueLevel.Warning ? Fields.Warning
                    : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled];
                string prefix = issue.Level == IssueLevel.Error ? "Error: " : issue.Level == IssueLevel.Warning ? "Warning: " : string.Empty;

                ImGui.PushStyleColor(ImGuiCol.Text, colour);
                ImGui.PushTextWrapPos(0f);
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextUnformatted(prefix + issue.Text);
                ImGui.PopTextWrapPos();
                ImGui.PopStyleColor();

                if (issue.Fix != WorkflowAction.None)
                {
                    bool locked = ScenarioSession.EditingLocked && WorkflowService.ChangesScenario(issue.Fix);
                    ImGui.BeginDisabled(locked);
                    if (ImGui.SmallButton((string.IsNullOrEmpty(issue.FixLabel) ? "Fix" : issue.FixLabel) + "###fix"))
                    {
                        WorkflowService.Perform(issue.Fix);
                    }
                    ImGui.EndDisabled();
                    if (locked && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip(ScenarioSession.BusyTooltip);
                    }
                }

                ImGui.PopID();
            }

            if (step.Secondary.Count > 0)
            {
                ImGui.Spacing();
                for (int i = 0; i < step.Secondary.Count; ++i)
                {
                    ImGui.PushID(1000 + i);
                    if (i > 0) ImGui.SameLine();
                    //Wraps to a new line rather than running off the panel.
                    float width = ImGui.CalcTextSize(step.Secondary[i].Label).x + 2f * ImGui.GetStyle().FramePadding.x;
                    if (i > 0 && ImGui.GetContentRegionAvail().x < width)
                    {
                        ImGui.NewLine();
                    }
                    ActionButton(step.Secondary[i], true);
                    ImGui.PopID();
                }
            }
        }

        /// <summary>A step's action as a button, disabled with its reason when it cannot be pressed.</summary>
        private static void ActionButton(StepAction action, bool small)
        {
            bool enabled = action.Enabled && !(ScenarioSession.EditingLocked && WorkflowService.ChangesScenario(action.Id));
            ImGui.BeginDisabled(!enabled);
            bool pressed;
            if (action.Checked.HasValue)
            {
                bool ticked = action.Checked.Value;
                pressed = ImGui.Checkbox(action.Label + "###act", ref ticked);
            }
            else
            {
                pressed = small ? ImGui.SmallButton(action.Label + "###act") : ImGui.Button(action.Label + "###act");
            }
            ImGui.EndDisabled();

            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                string tip = !action.Enabled ? action.Tooltip
                    : !enabled ? ScenarioSession.BusyTooltip
                    : action.Tooltip;
                if (!string.IsNullOrEmpty(tip))
                {
                    ImGui.SetTooltip(tip);
                }
            }

            if (pressed)
            {
                WorkflowService.Perform(action.Id);
            }
        }

        /// <summary>The status as a filled label of fixed width, so the titles line up.</summary>
        private static void Badge(WorkflowStep step, float width)
        {
            Vector4 fill = StatusColour(step.Status);
            string text = step.StatusLabel;

            float height = ImGui.GetFrameHeight();
            Vector2 min = ImGui.GetCursorScreenPos();
            Vector2 max = new Vector2(min.x + width, min.y + height);
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(min, max, ImGui.GetColorU32(fill), 3f);

            Vector2 size = ImGui.CalcTextSize(text);
            Vector2 at = new Vector2(min.x + 0.5f * (width - size.x), min.y + 0.5f * (height - size.y));
            draw.AddText(at, ImGui.GetColorU32(step.Status == StepStatus.Optional || step.Status == StepStatus.ToDo
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : BadgeText), text);

            ImGui.Dummy(new Vector2(width, height));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(StatusMeaning(step.Status));
            }
        }

        private static Vector4 StatusColour(StepStatus status)
        {
            switch (status)
            {
                case StepStatus.Done: return Fields.Good;
                case StepStatus.NeedsAttention: return Fields.Warning;
                case StepStatus.Blocked: return BlockedColour;
                case StepStatus.Running: return RunningColour;
                case StepStatus.Optional:
                    {
                        Vector4 c = ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg];
                        return c;
                    }
                default:
                    {
                        Vector4 c = ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered];
                        return c;
                    }
            }
        }

        private static string StatusMeaning(StepStatus status)
        {
            switch (status)
            {
                case StepStatus.Done: return "Done: everything this step is for exists and checks out.";
                case StepStatus.NeedsAttention: return "Needs attention: it will run, but something is stale, suboptimal or wrong.";
                case StepStatus.Blocked: return "Blocked: something an earlier step makes is missing.";
                case StepStatus.ToDo: return "To do: nothing is in the way; it has not been done yet.";
                case StepStatus.Optional: return "Optional: not needed for this scenario as it is set up.";
                case StepStatus.Running: return "Running: being worked on now.";
                default: return string.Empty;
            }
        }
    }
}
