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
        private static bool _painting;

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
                ImGui.End();
                if (!_isOpen) { PreactGUI.CloseWindow(Draw); }
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
                    if (_painting)
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
                if (_painting)
                {
                    SelectForPainting();
                }
            }

            ImGui.SeparatorText("Painting");

            if (!_painting)
            {
                if (ImGui.Button("Start painting"))
                {
                    StartPainting();
                }
                ImGui.TextDisabled("Needs the landscape loaded, since groups are painted on the fire grid.");
            }
            else
            {
                if (ImGui.Button("Stop painting"))
                {
                    _painting = false;
                }

                ImGui.SameLine();
                if (ImGui.Button("Save painted areas"))
                {
                    SaveMasks();
                }
                ImGui.TextWrapped("Saving writes one mask per group into the scenario folder and points each group at its own, replacing any shapefile.");
            }

            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void StartPainting()
        {
            PushGroupsToPainter();
            _painting = true;
            SelectForPainting();
            PreactGUI.WUInity.ShowUTMMap();
            PreactGUI.WUInity.DisplayEvacGroupMap();
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
