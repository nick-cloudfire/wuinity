using ImGuiNET;
using PREACT;
using PREACT.Input;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI.Editors
{
    /// <summary>
    /// Paints the three fire areas a scenario can carry, and saves them.
    ///
    /// The brush for these has existed all along (<c>Painter.PaintMode.WUIArea</c> /
    /// <c>RandomIgnitionArea</c> / <c>InitialIgnition</c>) with nothing anywhere that opened it, and
    /// nothing that wrote what it painted to disk - so the areas could be painted, were never saved, and
    /// were gone on the next load. Which is why the ELMFIRE case builder's painted-mask path had never
    /// run: it reads a file nothing produced.
    ///
    /// The three are painted one at a time rather than together, unlike evacuation groups: they overlap
    /// freely - the WUI area being protected may well be inside the area a fire may start in - so each is
    /// its own mask and there is nothing to define them against each other.
    /// </summary>
    public static class FirePaintWindow
    {
        private static bool _isOpen;
        private static global::WUInity.Painter.PaintMode _mode = global::WUInity.Painter.PaintMode.WUIArea;
        private static bool _adding = true;
        private static bool _painting;
        private static bool _startFailed;
        private static string _savedAs = string.Empty;

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
            "k-PERIL back-propagates from the fire to this area, and the ELMFIRE case builder writes it "
            + "as wui_area.tif. Without it the trigger boundary has nothing to protect.",
            "The ELMFIRE case builder writes this as ignition_mask.tif, which is where an ensemble draws "
            + "its ignitions from. Unpainted means the whole domain, including the sea.",
            "One fire, at the middle of what is painted here, instead of a random draw. For a single "
            + "named point, use the ignition point editor - it is exact, and it is kept in the scenario.",
        };

        public static void Open()
        {
            if (!_isOpen)
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

            ImGui.Begin("Paint fire areas", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            if (!ScenarioEditorWindow.HasInput)
            {
                ImGui.TextWrapped("Load a scenario first: the areas are painted on its fire grid.");
                End();
                return;
            }

            ImGui.TextWrapped("Left click paints, right click flood fills, keypad +/- changes the brush size.");

            ImGui.SeparatorText("Area being painted");
            for (int i = 0; i < Modes.Length; ++i)
            {
                if (ImGui.RadioButton(Labels[i], _mode == Modes[i]))
                {
                    _mode = Modes[i];
                    if (_painting)
                    {
                        SelectForPainting();
                    }
                }
            }

            int selected = System.Array.IndexOf(Modes, _mode);
            ImGui.TextWrapped(Explanations[selected < 0 ? 0 : selected]);

            ImGui.SeparatorText("Brush");
            //Erasing is the same brush in the inactive colour, which is how the painter models it - so it
            //is offered here as what the brush does rather than as a separate tool.
            if (ImGui.RadioButton("Add", _adding))
            {
                _adding = true;
                ApplyBrushColour();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Erase", !_adding))
            {
                _adding = false;
                ApplyBrushColour();
            }

            ImGui.SeparatorText("Painting");

            if (!_painting)
            {
                if (ImGui.Button("Start painting"))
                {
                    StartPainting();
                }
                ImGui.TextDisabled("Painted on the fire grid: the landscape's, or the imported time of arrival");
                ImGui.TextDisabled("raster's when the fire comes from one, or a DEM's.");
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
            }

            //Outside the painting branch, unlike the evacuation group window's: the areas survive the
            //brush being put down, and finding that they can only be saved while it is up is a way to
            //lose them.
            ImGui.SameLine();
            if (ImGui.Button("Save painted areas"))
            {
                Save();
            }

            string current = ScenarioEditorWindow.Input.WildfireModule.GraphicalFireInputFile;
            if (!string.IsNullOrEmpty(current))
            {
                ImGui.Text("Scenario's painted areas: " + current);
            }
            else
            {
                ImGui.TextDisabled("This scenario has no painted areas saved yet.");
            }

            if (!string.IsNullOrEmpty(_savedAs))
            {
                ImGui.TextWrapped("Saved as " + _savedAs + ". Save the scenario (File > Save) to keep the reference to it.");
            }

            End();
        }

        private static void End()
        {
            ImGui.End();
            if (!_isOpen)
            {
                //Closing the window by its title bar has to stop the brush too, or it keeps painting with
                //no way left to turn it off.
                if (_painting)
                {
                    StopPainting();
                }
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void StartPainting()
        {
            PreactGUI.WUInity.ShowUTMMap();
            //Through the manager, so the painter object is switched on, the right map plane is shown and a
            //left-drag paints instead of panning the map.
            PreactGUI.WUInity.StartPainter(_mode);

            //Setting a mode can fail for want of a fire grid, and it says so in the log rather than
            //throwing - so without this the window would claim to be painting while the brush did nothing.
            if (!PreactGUI.WUInity.Painter.CanPaint)
            {
                _startFailed = true;
                PreactGUI.WUInity.StopPainter();
                return;
            }

            _startFailed = false;
            _painting = true;
            ApplyBrushColour();
        }

        private static void StopPainting()
        {
            _painting = false;
            PreactGUI.WUInity.StopPainter();
        }

        private static void SelectForPainting()
        {
            PreactGUI.WUInity.StartPainter(_mode);
            ApplyBrushColour();
        }

        /// <summary>
        /// Both halves of the brush at once: which mask is being written, and whether the stroke adds to
        /// it or takes away. The painter derives the second from the colour, so the colour has to be set
        /// again after every mode change - the mode's own setter always selects "add".
        /// </summary>
        private static void ApplyBrushColour()
        {
            if (!_painting)
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

        private static void Save()
        {
            PREACTInput input = ScenarioEditorWindow.Input;
            string written = PreactGUI.WUInity.Painter.SavePaintedFireAreas(input.RootFolder);
            if (string.IsNullOrEmpty(written))
            {
                return;
            }

            input.WildfireModule.GraphicalFireInputFile = written;
            _savedAs = written;
            Engine.Message(null, Engine.LogType.Log,
                "Painted fire areas saved. Save the scenario to keep the reference to them.");
        }
    }
}
