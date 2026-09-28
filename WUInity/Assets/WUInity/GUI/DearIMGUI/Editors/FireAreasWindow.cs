using System.Collections.Generic;
using ImGuiNET;
using PREACT;
using PREACT.Input;
using UnityEngine;
using IgnitionPointInput = PREACT.Wildfire.IgnitionPointInput;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI.Editors
{
    /// <summary>
    /// Where the fire starts and what it threatens: the painted WUI area, ignition area and initial ignition,
    /// and the ignition points. Workflow step 6.
    /// </summary>
    /// <remarks>
    /// These were two places - a paint window reached from a button in the "Ignitions and areas" tab, and the
    /// ignition list in that tab - and neither said which grid it was on. For an ELMFIRE scenario that grid is
    /// now always the fire case's dem.tif, and until the case exists this window says so and offers to build
    /// it instead of painting on whatever raster happened to be found first.
    /// </remarks>
    public static class FireAreasWindow
    {
        private static bool _isOpen;
        private static global::WUInity.Painter.PaintMode _mode = global::WUInity.Painter.PaintMode.WUIArea;
        private static bool _adding = true;
        private static bool _subscribed;

        private static bool Painting
        {
            get { return PreactGUI.WUInity != null && PreactGUI.WUInity.IsPaintingFireArea; }
        }

        private static readonly global::WUInity.Painter.PaintMode[] Modes =
        {
            global::WUInity.Painter.PaintMode.WUIArea,
            global::WUInity.Painter.PaintMode.RandomIgnitionArea,
            global::WUInity.Painter.PaintMode.InitialIgnition,
        };

        private static readonly string[] Labels =
        {
            "WUI area (what the trigger boundary protects)",
            "Ignition area (where a fire may start)",
            "Initial ignition (where this one starts)",
        };

        private static readonly string[] Explanations =
        {
            "k-PERIL back-propagates from the fire to this area, and the case build writes it as wui_area.tif. "
            + "Without it the trigger boundary has nothing to protect.",
            "The case build writes this as ignition_mask.tif, which is where a campaign draws its ignitions from. "
            + "Unpainted means anywhere burnable in the domain.",
            "One fire, at the middle of what is painted here. For a single named point, an ignition point below "
            + "is exact.",
        };

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;

            if (!_subscribed)
            {
                _subscribed = true;
                ScenarioSession.ScenarioChanged += () =>
                {
                    if (Painting) PreactGUI.WUInity.StopPainter();
                };
            }
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(470f, 600f));
            if (ImGui.Begin("Fire areas and ignition###FireAreas", ref _isOpen, PreactGUI.ToolWindowFlags))
            {
                PREACTInput input = ScenarioSession.Input;
                if (input == null)
                {
                    ImGui.TextWrapped("Open a scenario first: the areas are painted on its fire grid.");
                }
                else
                {
                    ImGui.BeginDisabled(ScenarioSession.EditingLocked);
                    DrawPainting(input);
                    ImGui.SeparatorText("Ignition points");
                    DrawIgnitionPoints(input);
                    ImGui.EndDisabled();
                }
            }
            ImGui.End();

            if (!_isOpen)
            {
                //Closing the window by its title bar has to stop the brush too, or it keeps painting with no
                //way left to turn it off.
                if (Painting)
                {
                    PreactGUI.WUInity.StopPainter();
                }
                PreactGUI.CloseWindow(Draw);
            }
        }

        /// <summary>What All settings &gt; Fire &gt; Fire areas shows: where things stand, and the way here.</summary>
        public static void DrawSummary(PREACTInput input)
        {
            WorkflowStep step = WorkflowService.Step(WorkflowStepId.FireAreas);
            if (step != null)
            {
                ImGui.TextWrapped(step.Summary);
            }
            if (ImGui.Button("Open Fire areas and ignition"))
            {
                Open();
            }
        }

        private static void DrawPainting(PREACTInput input)
        {
            global::WUInity.Painter painter = PreactGUI.WUInity.Painter;
            ScenarioWorkflow model = WorkflowService.Model;
            bool isElmfire = input.WildfireModule.Module == WildfireModuleInput.WildfireModules.ELMFIRE;

            ImGui.SeparatorText("Grid");
            if (model.PaintGrid == null)
            {
                //The one grid of record is missing; there is nothing else to paint on.
                Fields.Warn(isElmfire
                    ? "The fire case has not been built, and an ELMFIRE scenario is painted on its dem.tif."
                    : painter.GridDescription);
                if (isElmfire)
                {
                    ImGui.BeginDisabled(ScenarioSession.IsBusy);
                    if (ImGui.Button("Build the fire case (step 5)"))
                    {
                        WorkflowService.Perform(WorkflowAction.BuildFireCase);
                    }
                    ImGui.EndDisabled();
                }
                return;
            }

            ImGui.TextDisabled($"{model.PaintGridReference}: {model.PaintGrid.Describe()}");
            Fields.Hint("Everything here is painted on this grid: the fire's own, which the case's masks,",
                        "the evacuation groups and k-PERIL all share.");

            ImGui.SeparatorText("Area being painted");
            for (int i = 0; i < Modes.Length; ++i)
            {
                if (ImGui.RadioButton(Labels[i] + "###mode" + i, _mode == Modes[i]))
                {
                    _mode = Modes[i];
                    if (Painting)
                    {
                        PreactGUI.WUInity.StartPainter(_mode);
                        ApplyBrushColour();
                    }
                }
            }

            int selected = System.Array.IndexOf(Modes, _mode);
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled(Explanations[selected < 0 ? 0 : selected]);
            ImGui.PopTextWrapPos();

            ImGui.SeparatorText("Brush");
            //Erasing is the same brush in the inactive colour, which is how the painter models it - so it is
            //offered as what the brush does rather than as a separate tool.
            if (ImGui.RadioButton("Add###brushAdd", _adding))
            {
                _adding = true;
                ApplyBrushColour();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Erase###brushErase", !_adding))
            {
                _adding = false;
                ApplyBrushColour();
            }
            ImGui.SameLine();
            ImGui.TextDisabled("left drag paints, right click fills, keypad +/- sizes");

            if (!Painting)
            {
                if (ImGui.Button("Start painting"))
                {
                    StartPainting();
                }
            }
            else if (ImGui.Button("Stop painting"))
            {
                PreactGUI.WUInity.StopPainter();
            }

            //One save: the file, the scenario's reference to it, and the scenario marked changed. Offered with
            //the brush down too - the areas survive it, and saving only while painting was a way to lose them.
            ImGui.SameLine();
            if (ImGui.Button("Save painted areas"))
            {
                Save(input);
            }

            if (isElmfire)
            {
                ImGui.SameLine();
                WorkflowStep step = WorkflowService.Step(WorkflowStepId.FireAreas);
                StepAction apply = step?.Secondary.Find(a => a.Id == WorkflowAction.ApplyFireAreasToCase);
                bool canApply = apply != null && apply.Enabled && !ScenarioSession.IsBusy;
                ImGui.BeginDisabled(!canApply);
                if (ImGui.Button("Apply to case"))
                {
                    WorkflowService.Perform(WorkflowAction.ApplyFireAreasToCase);
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(apply == null ? "" : !apply.Enabled ? apply.Tooltip
                        : "Rebuilds the case keeping its layers, so these areas become its ignition_mask.tif and wui_area.tif.");
                }
            }

            string current = input.WildfireModule.GraphicalFireInputFile;
            ImGui.TextDisabled(string.IsNullOrEmpty(current) ? "Nothing saved yet." : "Saved in " + current);
            if (painter.UnsavedFireStrokes)
            {
                Fields.Warn("Unsaved strokes - Save painted areas, or File > Save.");
            }

            //The workflow's verdict on what is painted, so the problems are said where they are fixed.
            WorkflowStep areas = WorkflowService.Step(WorkflowStepId.FireAreas);
            if (areas != null)
            {
                foreach (StepIssue issue in areas.Issues)
                {
                    if (issue.Level == IssueLevel.Error) Fields.Caution(issue.Text);
                }
            }
        }

        private static void DrawIgnitionPoints(PREACTInput input)
        {
            List<IgnitionPointInput> ignitions = input.WildfireModule.Data.IgnitionPoints;

            if (ImGui.Button("New ignition point"))
            {
                IgnitionPointEditWindow.Open(ignitions, -1);
            }

            if (ignitions.Count == 0)
            {
                Fields.Hint("None. ELMFIRE then starts from the painted initial ignition, or draws from the",
                            "ignition area.");
            }

            int remove = -1;
            for (int i = 0; i < ignitions.Count; ++i)
            {
                IgnitionPointInput point = ignitions[i];
                ImGui.PushID("ign" + i);

                //Its position as the label, since that is the only thing distinguishing one from another - an
                //ignition point has no name.
                ImGui.Text($"{i + 1}. {point.LatLon.x:F5}, {point.LatLon.y:F5}  "
                    + (point.AbsoluteTime ? $"at {point.IgnitionDateTime:yyyy-MM-dd HH:mm}" : $"{point.IgnitionTime:F0} s in"));
                ImGui.SameLine();
                if (ImGui.SmallButton("Edit")) { IgnitionPointEditWindow.Open(ignitions, i); }
                ImGui.SameLine();
                if (ImGui.SmallButton("Remove")) { remove = i; }

                ImGui.PopID();
            }

            if (remove >= 0)
            {
                ignitions.RemoveAt(remove);
                //Or its marker stays on the map, marking an ignition the scenario no longer has.
                PreactGUI.WUInity.RefreshWildfireIgnitionMarkers();
                ScenarioSession.NotifyEdited("ignition point removed");
            }
        }

        private static void StartPainting()
        {
            PreactGUI.WUInity.ShowUTMMap();
            //Through the manager, so the painter object is switched on, the right map plane is shown and a
            //left-drag paints instead of panning the map.
            PreactGUI.WUInity.StartPainter(_mode);

            //Setting a mode can fail for want of a grid, which the painter reports rather than throws.
            if (!PreactGUI.WUInity.Painter.CanPaint)
            {
                PreactGUI.WUInity.StopPainter();
                return;
            }

            ApplyBrushColour();
        }

        /// <summary>
        /// Both halves of the brush: which mask is being written, and whether the stroke adds or takes away.
        /// The painter derives the second from the colour, so it is set again after every mode change.
        /// </summary>
        private static void ApplyBrushColour()
        {
            if (!Painting)
            {
                return;
            }

            if (_mode == global::WUInity.Painter.PaintMode.WUIArea)
            {
                PreactGUI.WUInity.Painter.SetWUIAreaColor(_adding);
            }
            else if (_mode == global::WUInity.Painter.PaintMode.RandomIgnitionArea)
            {
                PreactGUI.WUInity.Painter.SetRandomIgnitionAreaColor(_adding);
            }
            else
            {
                PreactGUI.WUInity.Painter.SetInitialIgnitionAreaColor(_adding);
            }
        }

        private static void Save(PREACTInput input)
        {
            string written = PreactGUI.WUInity.Painter.SavePaintedFireAreas(input.RootFolder);
            if (string.IsNullOrEmpty(written))
            {
                return;
            }

            input.WildfireModule.GraphicalFireInputFile = written;
            ScenarioSession.NotifyEdited("painted fire areas");
            Engine.Message(null, Engine.LogType.Log, "Painted fire areas saved in " + written
                + (ScenarioSession.IsDirty ? ". File > Save keeps the scenario's reference to them." : "."));
        }
    }
}
