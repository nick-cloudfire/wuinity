using ImGuiNET;
using PREACT;
using PREACT.Evacuation;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI.Editors
{
    /// <summary>
    /// Paints evacuation group areas onto the map.
    ///
    /// All groups are painted together because a cell belongs to exactly one group: painting for one
    /// takes the cell away from whichever held it, so they can only be defined against each other.
    /// Groups are painted on the fire grid, which is also the grid k-PERIL and the WUI mask use, so a
    /// painted group lines up with them cell for cell and can be fed straight to a trigger boundary.
    /// </summary>
    public static class EvacuationGroupPaintWindow
    {
        private static bool _isOpen;
        private static Dictionary<string, EvacuationGroupInput> _inputs;
        private static readonly List<EvacuationGroupInput> _ordered = new List<EvacuationGroupInput>();
        private static int _selected;
        /// <summary>Whether this window's brush is live, asked of the painter rather than remembered.</summary>
        /// <remarks>See FireAreasWindow.Painting: two windows each remembering this separately meant
        /// neither agreed with the painter, nor with the other.</remarks>
        private static bool Painting
        {
            get { return PreactGUI.WUInity != null && PreactGUI.WUInity.IsPaintingMode(global::WUInity.Painter.PaintMode.EvacGroup); }
        }
        private static bool _startFailed;
        //Dimmed enough to read the map through, strong enough to tell two groups apart.
        private const float GroupOverlayOpacity = 0.3f;
        private static bool _keepGroupsVisible = true;

        //What reading the groups' existing masks found wrong, shown until the next read.
        private static readonly List<string> _maskProblems = new List<string>();
        private static bool _subscribed;

        public static void Open(Dictionary<string, EvacuationGroupInput> inputs)
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;

            if (!_subscribed)
            {
                _subscribed = true;
                //The dictionary this window holds belongs to the scenario it was opened for; after another is
                //loaded, carrying on would paint into, and save the masks of, one that is no longer open.
                //Unregistered here as well: Draw returns before its own close path once _isOpen is false, and
                //the next Open would then register a second copy that draws over the first.
                ScenarioSession.ScenarioChanged += () =>
                {
                    if (_isOpen)
                    {
                        _isOpen = false;
                        PreactGUI.CloseWindow(Draw);
                    }
                };
                //Groups added, removed or renamed while this is open: the order the painter's indices refer to is
                //taken again, or a mask would be saved for the wrong group.
                ScenarioSession.Edited += () =>
                {
                    if (_isOpen && !SameGroups()) RebuildOrder();
                };
            }

            _inputs = inputs;
            RebuildOrder();
            LoadExistingMasks(false);
        }

        /// <summary>
        /// Reads the groups' saved masks into the painter, once per grid, so painting continues from them.
        /// Painting used to start from nothing every time, and saving then overwrote every group that had
        /// received a stroke - so a group's area could be added to, but never edited.
        /// </summary>
        private static void LoadExistingMasks(bool force)
        {
            if (!ScenarioSession.HasInput || PreactGUI.WUInity == null)
            {
                return;
            }

            global::WUInity.Painter painter = PreactGUI.WUInity.Painter;
            if (!force && painter.EvacGroupMasksLoaded)
            {
                return;
            }

            _maskProblems.Clear();
            painter.LoadEvacGroupMasks(_ordered, ScenarioSession.RootFolder, _maskProblems);
        }

        /// <summary>
        /// The painter refers to groups by index, so the order used here and the order handed to it
        /// have to be the same one. A dictionary has no inherent order, hence this snapshot.
        /// </summary>
        private static void RebuildOrder()
        {
            _ordered.Clear();
            if (_inputs == null)
            {
                return;
            }

            foreach (EvacuationGroupInput g in _inputs.Values)
            {
                _ordered.Add(g);
            }

            if (_selected >= _ordered.Count)
            {
                _selected = 0;
            }

            PushGroupsToPainter();
        }

        /// <summary>Whether the snapshot still holds exactly the scenario's groups, under the same names.</summary>
        private static bool SameGroups()
        {
            if (_inputs == null) return _ordered.Count == 0;
            if (_inputs.Count != _ordered.Count) return false;
            foreach (EvacuationGroupInput g in _ordered)
            {
                if (!_inputs.TryGetValue(g.Name, out EvacuationGroupInput current) || current != g) return false;
            }
            return true;
        }

        private static void PushGroupsToPainter()
        {
            var names = new string[_ordered.Count];
            var colors = new Color[_ordered.Count];
            for (int i = 0; i < _ordered.Count; ++i)
            {
                names[i] = _ordered[i].Name;
                colors[i] = new Color(_ordered[i].Color.r, _ordered[i].Color.g, _ordered[i].Color.b, 1f);
            }
            PreactGUI.WUInity.Painter.SetEvacGroups(names, colors);
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(420f, 460f), ImGuiCond.FirstUseEver);
            ImGui.Begin("Paint evacuation groups###PaintGroups", ref _isOpen, PreactGUI.ToolWindowFlags);

            if (_ordered.Count == 0)
            {
                ImGui.TextWrapped("This scenario has no evacuation groups to paint. Create one first.");
                End();
                return;
            }

            ImGui.TextWrapped("Left click paints, right click flood fills, keypad +/- changes the brush size.");

            global::WUInity.Painter painter = PreactGUI.WUInity.Painter;
            ImGui.TextDisabled(painter.GridDescription);

            for (int i = 0; i < _maskProblems.Count; ++i)
            {
                ImGui.TextColored(Fields.Warning, _maskProblems[i]);
            }

            ImGui.SeparatorText("Group being painted");
            for (int i = 0; i < _ordered.Count; ++i)
            {
                //By index: a group's name is only a label, and one containing "##" or named like the Erase item
                //would otherwise share its ID.
                ImGui.PushID(i);
                PREACTColor c = _ordered[i].Color;
                ImGui.TextColored(new Vector4(c.r, c.g, c.b, 1f), "@@@");
                ImGui.SameLine();
                if (ImGui.RadioButton(_ordered[i].Name.Replace("#", " ") + "###group", _selected == i))
                {
                    _selected = i;
                    if (Painting)
                    {
                        SelectForPainting();
                    }
                }
                ImGui.PopID();
            }

            //Erasing needs its own selection: with exclusive ownership there is otherwise no way to
            //take a cell out of every group again.
            if (ImGui.RadioButton("Erase (no group)###erasegroup", _selected == _ordered.Count))
            {
                _selected = _ordered.Count;
                if (Painting)
                {
                    SelectForPainting();
                }
            }

            ImGui.SeparatorText("Painting");

            if (ImGui.Checkbox("Keep groups on the map when not painting", ref _keepGroupsVisible))
            {
                //Acted on at once rather than only at the next stop, so the checkbox shows what it does.
                if (!Painting)
                {
                    if (_keepGroupsVisible)
                    {
                        PreactGUI.WUInity.ShowEvacGroupOverlay(GroupOverlayOpacity);
                    }
                    else
                    {
                        PreactGUI.WUInity.HideEvacGroupOverlay();
                    }
                }
            }

            ImGui.BeginDisabled(ScenarioSession.EditingLocked);
            if (!Painting)
            {
                //Not while a campaign runs either; the brush is put down when anything starts.
                ImGui.BeginDisabled(ScenarioSession.IsBusy);
                if (ImGui.Button("Start painting"))
                {
                    StartPainting();
                }
                ImGui.EndDisabled();
                if (ScenarioSession.IsBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(ScenarioSession.BusyTooltip);
                }
                if (_startFailed)
                {
                    ImGui.TextColored(Fields.Warning, "No paint grid: " + painter.GridDescription);
                }
            }
            else
            {
                if (ImGui.Button("Stop painting"))
                {
                    StopPainting();
                }
            }

            //Offered whether or not the brush is down, like the fire areas: the areas survive the brush being
            //put away, and being able to save them only while it was up was a way to lose them.
            ImGui.SameLine();
            ImGui.BeginDisabled(!painter.HasEvacGroupCells);
            if (ImGui.Button("Save group areas"))
            {
                SaveMasks();
            }
            ImGui.EndDisabled();
            if (!painter.HasEvacGroupCells && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip("Nothing painted yet.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Reload saved areas"))
            {
                LoadExistingMasks(true);
                if (Painting)
                {
                    SelectForPainting();
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Discards unsaved strokes and reads each group's saved mask again.");
            }
            ImGui.EndDisabled();

            if (painter.UnsavedGroupStrokes)
            {
                Fields.Warn("Unsaved strokes - Save group areas to keep them.");
            }
            ImGui.TextWrapped("Saving writes one mask per group into the scenario folder and points each group at its own, replacing any shapefile.");

            End();
        }

        /// <summary>
        /// The one way this window closes.
        /// </summary>
        /// <remarks>
        /// Every exit from <see cref="Draw"/> goes through here, because closing the window has to put the
        /// brush down: the map keeps painting under the cursor otherwise, and with the window gone there is
        /// nothing left to switch it off with. This was handled at the end of Draw but not on the early
        /// return for a scenario with no groups, so deleting the last group while painting left the brush
        /// live and unreachable.
        /// </remarks>
        private static void End()
        {
            ImGui.End();

            if (_isOpen)
            {
                return;
            }

            if (Painting)
            {
                StopPainting();
            }
            PreactGUI.CloseWindow(Draw);
        }

        private static void StartPainting()
        {
            PushGroupsToPainter();
            LoadExistingMasks(false);
            PreactGUI.WUInity.ShowUTMMap();
            //Through the manager, so the painter object is actually switched on and the sample mode
            //says painting is happening. Setting the mode on the painter alone left it inert whenever
            //another painter had been stopped earlier, and left the map draggable out from under the
            //brush, since a left-drag pans unless something claims the button.
            PreactGUI.WUInity.StartPainter(global::WUInity.Painter.PaintMode.EvacGroup);

            //Setting a mode can fail for want of a fire grid, and it says so in the log rather than
            //throwing - so without this the window would claim to be painting while the brush did
            //nothing at all.
            if (!PreactGUI.WUInity.Painter.CanPaint)
            {
                _startFailed = true;
                PreactGUI.WUInity.StopPainter();
                return;
            }

            _startFailed = false;
            PreactGUI.WUInity.Painter.SetEvacGroupColor(_selected);
            PreactGUI.WUInity.DisplayEvacGroupMap();
        }

        private static void StopPainting()
        {
            //Hides both map planes along with switching the brush off, so the dimmed view of what was
            //painted goes back up afterwards rather than instead.
            PreactGUI.WUInity.StopPainter();

            if (_keepGroupsVisible)
            {
                PreactGUI.WUInity.ShowEvacGroupOverlay(GroupOverlayOpacity);
            }
        }

        private static void SelectForPainting()
        {
            //An index past the end is the painter's "erase", which is why Erase is selected as one
            //past the last group rather than as a separate flag.
            //Globally qualified: inside Assets.WUInity.*, a bare "WUInity.Painter" binds to
            //Assets.WUInity.Painter, which does not exist.
            PreactGUI.WUInity.Painter.SetPainterMode(global::WUInity.Painter.PaintMode.EvacGroup);
            PreactGUI.WUInity.Painter.SetEvacGroupColor(_selected);
            PreactGUI.WUInity.DisplayEvacGroupMap();
        }

        private static void SaveMasks()
        {
            SaveMasksFor(ScenarioSession.Input);
        }

        /// <summary>
        /// Writes one mask per painted group and points each group at its own. Also what File &gt; Save calls
        /// when there are unsaved group strokes, so Save keeps what is on screen.
        /// </summary>
        public static void SaveMasksFor(PREACT.Input.PREACTInput input)
        {
            if (input == null || PreactGUI.WUInity == null)
            {
                return;
            }

            //The painter's indices refer to the names it was last given, so each mask goes to the group of that
            //name - not to whichever group is at that position now, which after a group was added, removed or
            //renamed is another one.
            string[] names = PreactGUI.WUInity.Painter.EvacGroupNames;
            string[] written = PreactGUI.WUInity.Painter.ExportEvacGroupMasks(input.RootFolder);
            if (written == null)
            {
                return;
            }

            for (int i = 0; i < written.Length && i < names.Length; ++i)
            {
                if (string.IsNullOrEmpty(written[i]))
                {
                    continue;
                }

                if (!input.Evacuation.EvacuationGroupInputs.TryGetValue(names[i], out EvacuationGroupInput group))
                {
                    PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning, $"{written[i]} was painted for group {names[i]}, "
                        + "which the scenario no longer has; it is written but no group names it.");
                    continue;
                }

                group.MaskFile = written[i];
                //Cleared so there is no question which of the two defines the area. The parser
                //prefers the mask anyway, but leaving a stale shapefile behind invites the reader to
                //believe it still matters.
                group.ShapeFile = string.Empty;
            }

            ScenarioSession.NotifyEdited("group masks");
        }
    }
}
