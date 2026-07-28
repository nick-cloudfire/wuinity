using ImGuiNET;
using PREACT;
using PREACT.Evacuation;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI.Editors
{
    /// <summary>
    /// Editor for one evacuation group, and the entry point for painting group areas on the map.
    ///
    /// A group's area comes from either a shapefile or a painted mask. Painting is done for all
    /// groups at once rather than per group, because ownership of a cell is exclusive - painting one
    /// group takes the cell from whichever group had it - so the groups have to be painted against
    /// each other to be meaningful.
    /// </summary>
    public static class EvacuationGroupEditWindow
    {
        private static bool _isOpen;
        private static Dictionary<string, EvacuationGroupInput> _inputs;
        private static EvacuationGroupInput _input;
        private static string _oldKey = string.Empty;

        private static string[] DestinationChoiceStrings = Enum.GetNames(typeof(DestinationChoices));
        private static int _destinationChoiceIndex;

        public static void Open(Dictionary<string, EvacuationGroupInput> inputs, EvacuationGroupInput input)
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;

            _inputs = inputs;
            if (input == null)
            {
                _input = new EvacuationGroupInput();
                _oldKey = string.Empty;
            }
            else
            {
                _input = input;
                _oldKey = _input.Name;
            }
            _destinationChoiceIndex = (int)_input.DestinationChoice;
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.Begin("Evacuation group editor", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            ImGui.InputText(nameof(_input.Name), ref _input.Name, 64);

            Vector3 color = new Vector3(_input.Color.r, _input.Color.g, _input.Color.b);
            if (ImGui.ColorEdit3(nameof(_input.Color), ref color))
            {
                _input.Color.r = color.x;
                _input.Color.g = color.y;
                _input.Color.b = color.z;
            }

            CustomTypes.InputDateTimePopup(nameof(_input.EvacuationOrderDateTime), ref _input.EvacuationOrderDateTime);

            _destinationChoiceIndex = (int)_input.DestinationChoice;
            ImGui.Combo(nameof(_input.DestinationChoice), ref _destinationChoiceIndex, DestinationChoiceStrings, DestinationChoiceStrings.Length);
            _input.DestinationChoice = (DestinationChoices)_destinationChoiceIndex;

            ImGui.InputText(nameof(_input.Demographics), ref _input.Demographics, 64);

            ImGui.Checkbox(nameof(_input.Default), ref _input.Default);

            ImGui.SeparatorText("Area");
            DrawArea();

            ImGui.SeparatorText("Destinations");
            DrawList(nameof(_input.Destinations), _input.Destinations, _input.DestinationsCDF);

            ImGui.SeparatorText("Response curves");
            DrawList(nameof(_input.ResponseCurves), _input.ResponseCurves, _input.ResponseCurvesCDF);

            ImGui.Separator();

            bool nameIsFree = !string.IsNullOrWhiteSpace(_input.Name)
                              && (_input.Name == _oldKey || _inputs == null || !_inputs.ContainsKey(_input.Name));

            ImGui.BeginDisabled(!nameIsFree);
            if (ImGui.Button("OK"))
            {
                if (_input.Name != _oldKey)
                {
                    _inputs.Remove(_oldKey);
                }
                _inputs[_input.Name] = _input;
                _isOpen = false;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _isOpen = false;
            }
            if (!nameIsFree)
            {
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(_input.Name) ? "Needs a name." : "That name is already used.");
            }

            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void DrawArea()
        {
            //The mask wins when both are set, which is what the parser does, so saying so here avoids
            //someone setting a shapefile and wondering why it has no effect.
            if (!string.IsNullOrEmpty(_input.MaskFile))
            {
                ImGui.Text($"{nameof(_input.MaskFile)}: {_input.MaskFile}");
                ImGui.TextDisabled("A painted mask defines this group's area; its shapefile is ignored.");
                if (ImGui.Button("Clear painted mask"))
                {
                    _input.MaskFile = string.Empty;
                }
            }
            else
            {
                if (ImGui.Button("Select shapefile"))
                {
                    FileBrowser.OpenSetFilePath(path => _input.ShapeFile = path, "Select evacuation group shapefile", true);
                }
                ImGui.SameLine();
                ImGui.Text($"{nameof(_input.ShapeFile)}: {_input.ShapeFile}");
            }

            if (ImGui.Button("Paint group areas on the map"))
            {
                EvacuationGroupPaintWindow.Open(_inputs);
            }
            ImGui.TextWrapped("Painting covers every group at once, since a cell belongs to only one group.");
        }

        /// <summary>
        /// A group's destinations and response curves are each a list of names with a parallel
        /// cumulative distribution, so the two are edited together - a name added without a
        /// corresponding CDF entry is read as an unreachable choice.
        /// </summary>
        private static void DrawList(string label, List<string> names, List<double> cdf)
        {
            if (names == null)
            {
                return;
            }

            int removeAt = -1;
            for (int i = 0; i < names.Count; ++i)
            {
                ImGui.PushID(label + i);

                string name = names[i];
                ImGui.SetNextItemWidth(180);
                if (ImGui.InputText("###name", ref name, 64))
                {
                    names[i] = name;
                }

                ImGui.SameLine();
                float weight = i < cdf.Count ? (float)cdf[i] : 1f;
                ImGui.SetNextItemWidth(140);
                if (ImGui.SliderFloat("cumulative", ref weight, 0f, 1f))
                {
                    while (cdf.Count <= i)
                    {
                        cdf.Add(1.0);
                    }
                    cdf[i] = weight;
                }

                ImGui.SameLine();
                if (ImGui.Button("X"))
                {
                    removeAt = i;
                }

                ImGui.PopID();
            }

            if (removeAt >= 0)
            {
                names.RemoveAt(removeAt);
                if (removeAt < cdf.Count)
                {
                    cdf.RemoveAt(removeAt);
                }
            }

            if (ImGui.Button("Add###" + label))
            {
                names.Add(string.Empty);
                cdf.Add(1.0);
            }

            //The last entry has to reach 1 or the tail of the distribution is unreachable.
            if (cdf.Count > 0 && cdf[cdf.Count - 1] < 0.999)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), $"The last cumulative value is {cdf[cdf.Count - 1]:F3}; it should reach 1.");
            }
        }
    }
}
