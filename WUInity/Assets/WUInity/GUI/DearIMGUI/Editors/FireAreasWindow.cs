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
    /// Where fires start: the painted ignition area a campaign draws from, and the ignition points a single run starts
    /// at. Workflow step 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These were two places - a paint window reached from a button in the "Ignitions and areas" tab, and the
    /// ignition list in that tab - and neither said which grid it was on. For an ELMFIRE scenario that grid is
    /// now always the fire case's dem.tif, and until the case exists this window says so and offers to build
    /// it instead of painting on whatever raster happened to be found first.
    /// </para>
    /// <para>
    /// It painted a WUI area and an initial ignition too. The WUI area duplicated the evacuation groups, which is what
    /// the trigger boundary protects, and the initial ignition was an ignition point drawn with a brush and reduced to
    /// its centroid; both are gone, and the window says where the WUI area comes from instead.
    /// </para>
    /// </remarks>
    public static class FireAreasWindow
    {
        private static bool _isOpen;
        private const global::WUInity.Painter.PaintMode Mode = global::WUInity.Painter.PaintMode.RandomIgnitionArea;
        private static bool _adding = true;
        private static bool _subscribed;

        private static bool Painting
        {
            get { return PreactGUI.WUInity != null && PreactGUI.WUInity.IsPaintingFireArea; }
        }


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
                    DrawWuiArea(input);
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

            //A painting saved on another grid cannot be shown or added to here; painting would start from nothing
            //and its first save would be the only one on this grid. Moving it is the one thing to do first.
            bool movable = model.PaintingOnOtherGrid && !string.IsNullOrEmpty(model.PaintedOnReference);
            if (model.PaintingOnOtherGrid)
            {
                Fields.Caution($"The saved painting ({model.PaintedAreasFile}) is on another {model.PaintedWidth} x {model.PaintedHeight} grid "
                    + $"(this one {model.PaintGridDifference})"
                    + (movable ? $": {model.PaintedOnReference}'s." : ", which none of the scenario's rasters is."));
                if (movable)
                {
                    ImGui.BeginDisabled(ScenarioSession.IsBusy);
                    if (ImGui.Button((isElmfire ? "Move painting onto the fire-case grid" : "Move painting onto the fire grid") + "###MovePainting"))
                    {
                        WorkflowService.Perform(WorkflowAction.MovePaintingToCaseGrid);
                    }
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip(ScenarioSession.IsBusy ? ScenarioSession.BusyTooltip
                            : "Each cell of " + model.PaintGridReference + " takes the painted value at its centre. Written as a new "
                              + "file beside " + model.PaintedAreasFile + ", which is kept; the scenario then names the new one.");
                    }
                }
                else
                {
                    Fields.Hint("Painting here starts from nothing; saving writes a new file and keeps the old one.");
                }
            }

            ImGui.SeparatorText("Ignition area (where a fire may start)");
            ImGui.PushTextWrapPos(0f);
            ImGui.TextDisabled("The case build writes this as ignition_mask.tif, which is where a campaign draws its ignitions "
                + "from. Unpainted means anywhere burnable in the domain.");
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
                //Not while a campaign runs either (editing is locked for a run or a data step already).
                bool busy = ScenarioSession.IsBusy;
                ImGui.BeginDisabled(movable || busy);
                if (ImGui.Button("Start painting"))
                {
                    StartPainting();
                }
                ImGui.EndDisabled();
                if ((movable || busy) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(busy ? ScenarioSession.BusyTooltip
                        : "Move the saved painting onto this grid first: until then it cannot be shown or added to.");
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
                        : "Builds the case again so the ignition area becomes its ignition_mask.tif (and the evacuation groups "
                          + "its wui_area.tif). Its other layers are kept unless its grid has to be re-cut, and the namelist is "
                          + "written again from the scenario.");
                }
            }

            string current = input.WildfireModule.GraphicalFireInputFile;
            ImGui.TextDisabled(string.IsNullOrEmpty(current) ? "Nothing saved yet." : "Saved in " + current);
            if (painter.UnsavedFireStrokes)
            {
                Fields.Warn("Unsaved strokes - Save painted areas, or File > Save.");
            }

            //The workflow's verdict on what is painted, so the problems are said where they are fixed - and what an older
            //painting holds that is no longer used.
            WorkflowStep areas = WorkflowService.Step(WorkflowStepId.FireAreas);
            if (areas != null)
            {
                foreach (StepIssue issue in areas.Issues)
                {
                    //The painting on another grid is said, with its button, under Grid above.
                    if (issue.Level == IssueLevel.Error && issue.Fix != WorkflowAction.MovePaintingToCaseGrid) Fields.Caution(issue.Text);
                    else if (issue.Level == IssueLevel.Info && issue.Text.Contains("no longer used")) Fields.Hint(issue.Text);
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
                Fields.Hint("None. A single run then starts no fire of its own unless the namelist draws one",
                            "(RANDOM_IGNITIONS, from the ignition area); a campaign always draws its own.");
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

        /// <summary>
        /// Where the WUI area comes from: the evacuation groups, not anything painted here. Said here because this is
        /// where it used to be painted.
        /// </summary>
        private static void DrawWuiArea(PREACTInput input)
        {
            ImGui.SeparatorText("WUI area (what the trigger boundary protects)");
            int groups = 0, withArea = 0;
            foreach (PREACT.Evacuation.EvacuationGroupInput g in input.Evacuation.EvacuationGroupInputs.Values)
            {
                ++groups;
                if (!string.IsNullOrEmpty(g.MaskFile) || !string.IsNullOrEmpty(g.ShapeFile)) ++withArea;
            }

            Fields.Hint("The WUI area is the evacuation groups' area: k-PERIL protects them (combined, or one",
                        "boundary per group), and the case build writes their union as wui_area.tif.");
            ImGui.TextDisabled(groups == 0 ? "The scenario has no evacuation group yet (step 9)."
                : $"{withArea} of {groups} evacuation group(s) have an area.");
            if (ImGui.Button("Evacuation groups (step 9)"))
            {
                WorkflowService.Perform(WorkflowModelHasGrid() ? WorkflowAction.PaintGroups : WorkflowAction.OpenGroups);
            }
        }

        private static bool WorkflowModelHasGrid() => WorkflowService.Model?.PaintGrid != null;

        private static void StartPainting()
        {
            PreactGUI.WUInity.ShowUTMMap();
            //Through the manager, so the painter object is switched on, the right map plane is shown and a
            //left-drag paints instead of panning the map.
            PreactGUI.WUInity.StartPainter(Mode);

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

            PreactGUI.WUInity.Painter.SetRandomIgnitionAreaColor(_adding);
        }

        private static void Save(PREACTInput input)
        {
            string written = PreactGUI.WUInity.Painter.SavePaintedFireAreas(input.RootFolder, input.WildfireModule.GraphicalFireInputFile);
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
