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
        /// <remarks>See FirePaintWindow.Painting: two windows each remembering this separately meant
        /// neither agreed with the painter, nor with the other.</remarks>
        private static bool Painting
        {
            get { return PreactGUI.WUInity != null && PreactGUI.WUInity.IsPaintingMode(global::WUInity.Painter.PaintMode.EvacGroup); }
        }
        private static bool _startFailed;
        //Dimmed enough to read the map through, strong enough to tell two groups apart.
        private const float GroupOverlayOpacity = 0.3f;
        private static bool _keepGroupsVisible = true;

        public static void Open(Dictionary<string, EvacuationGroupInput> inputs)
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;

            _inputs = inputs;
            RebuildOrder();
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

            ImGui.Begin("Paint evacuation groups", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            if (_ordered.Count == 0)
            {
                ImGui.TextWrapped("This scenario has no evacuation groups to paint. Create one first.");
                End();
                return;
            }

            ImGui.TextWrapped("Left click paints, right click flood fills, keypad +/- changes the brush size.");

            ImGui.SeparatorText("Group being painted");
            for (int i = 0; i < _ordered.Count; ++i)
            {
                PREACTColor c = _ordered[i].Color;
                ImGui.TextColored(new Vector4(c.r, c.g, c.b, 1f), "@@@");
                ImGui.SameLine();
                if (ImGui.RadioButton(_ordered[i].Name, _selected == i))
                {
                    _selected = i;
                    if (Painting)
                    {
                        SelectForPainting();
                    }
                }
            }

            //Erasing needs its own selection: with exclusive ownership there is otherwise no way to
            //take a cell out of every group again.
            if (ImGui.RadioButton("Erase (no group)", _selected == _ordered.Count))
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

            if (!Painting)
            {
                if (ImGui.Button("Start painting"))
                {
                    StartPainting();
                }
                ImGui.TextDisabled("Groups are painted on the fire grid: the landscape's, or the imported "
                    + "time of arrival raster's when the fire comes from one.");
                if (_startFailed)
                {
                    ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                        "The fire grid could not be established - see the console for what is missing.");
                }
            }
            else
            {
                if (ImGui.Button("Stop painting"))
                {
                    StopPainting();
                }

                ImGui.SameLine();
                if (ImGui.Button("Save painted areas"))
                {
                    SaveMasks();
                }
                ImGui.TextWrapped("Saving writes one mask per group into the scenario folder and points each group at its own, replacing any shapefile.");
            }

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
            string folder = ScenarioEditorWindow.Input.RootFolder;
            string[] written = PreactGUI.WUInity.Painter.ExportEvacGroupMasks(folder);
            if (written == null)
            {
                return;
            }

            for (int i = 0; i < written.Length && i < _ordered.Count; ++i)
            {
                if (string.IsNullOrEmpty(written[i]))
                {
                    continue;
                }

                _ordered[i].MaskFile = written[i];
                //Cleared so there is no question which of the two defines the area. The parser
                //prefers the mask anyway, but leaving a stale shapefile behind invites the reader to
                //believe it still matters.
                _ordered[i].ShapeFile = string.Empty;
            }

            Engine.Message(null, Engine.LogType.Log, "Painted evacuation group areas saved. Save the scenario to keep them.");
        }
    }
}
