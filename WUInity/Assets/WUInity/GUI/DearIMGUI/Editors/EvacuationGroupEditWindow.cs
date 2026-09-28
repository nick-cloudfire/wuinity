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
        //The copy being edited and the scenario's group it came from (null for a new one). This used to edit
        //the scenario's group directly, so Cancel kept every change and a rename left the dictionary keyed by
        //the old name - two list rows could then show the same name and share ImGui IDs.
        private static EvacuationGroupInput _input;
        private static EvacuationGroupInput _original;
        private static string _oldKey = string.Empty;
        private static bool _subscribed;

        private static string[] DestinationChoiceStrings = Enum.GetNames(typeof(DestinationChoices));
        private static int _destinationChoiceIndex;

        public static void Open(Dictionary<string, EvacuationGroupInput> inputs, EvacuationGroupInput input)
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;

            if (!_subscribed)
            {
                _subscribed = true;
                ScenarioSession.ScenarioChanged += () => { if (_isOpen) { _isOpen = false; PreactGUI.CloseWindow(Draw); } };
            }

            _inputs = inputs;
            _original = input;
            if (input == null)
            {
                _input = new EvacuationGroupInput();
                _oldKey = string.Empty;
            }
            else
            {
                _input = Copy(input);
                _oldKey = input.Name;
            }
            _destinationChoiceIndex = (int)_input.DestinationChoice;

            //An unset DateTime is 0001-01-01, which is not a plausible evacuation order and reads as a
            //broken field rather than an empty one. The scenario's own start is the only sensible
            //starting point: an order before it never fires, and the picker would otherwise have to be
            //walked forward two thousand years to reach the simulated day.
            if (_input.EvacuationOrderDateTime == default(DateTime))
            {
                PREACT.Input.PREACTInput scenario = ScenarioSession.Input;
                if (scenario != null)
                {
                    _input.EvacuationOrderDateTime = scenario.Simulation.StartDateTime;
                }
            }
        }

        private static EvacuationGroupInput Copy(EvacuationGroupInput g)
        {
            return new EvacuationGroupInput
            {
                Name = g.Name,
                EvacuationOrderDateTime = g.EvacuationOrderDateTime,
                Color = g.Color,
                DestinationChoice = g.DestinationChoice,
                Destinations = new List<string>(g.Destinations),
                DestinationsCDF = new List<double>(g.DestinationsCDF),
                ResponseCurves = new List<string>(g.ResponseCurves),
                ResponseCurvesCDF = new List<double>(g.ResponseCurvesCDF),
                Demographics = g.Demographics,
                ShapeFile = g.ShapeFile,
                MaskFile = g.MaskFile,
                Default = g.Default,
            };
        }

        private static void CopyInto(EvacuationGroupInput from, EvacuationGroupInput to)
        {
            to.Name = from.Name;
            to.EvacuationOrderDateTime = from.EvacuationOrderDateTime;
            to.Color = from.Color;
            to.DestinationChoice = from.DestinationChoice;
            to.Destinations = new List<string>(from.Destinations);
            to.DestinationsCDF = new List<double>(from.DestinationsCDF);
            to.ResponseCurves = new List<string>(from.ResponseCurves);
            to.ResponseCurvesCDF = new List<double>(from.ResponseCurvesCDF);
            to.Demographics = from.Demographics;
            to.ShapeFile = from.ShapeFile;
            to.MaskFile = from.MaskFile;
            to.Default = from.Default;
        }

        /// <summary>Writes the edited copy into the scenario under its (possibly new) name.</summary>
        private static void Commit()
        {
            if (_original != null)
            {
                _inputs.Remove(_oldKey);
                CopyInto(_input, _original);
                _inputs[_original.Name] = _original;
            }
            else
            {
                _original = Copy(_input);
                _inputs[_original.Name] = _original;
            }
            _oldKey = _original.Name;
            ScenarioSession.NotifyEdited("evacuation group " + _original.Name);
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(520f, 560f));
            ImGui.Begin("Evacuation group###GroupEditor", ref _isOpen, PreactGUI.ToolWindowFlags);
            ImGui.BeginDisabled(ScenarioSession.EditingLocked);

            ImGui.InputText(nameof(_input.Name), ref _input.Name, 64);

            Vector3 color = new Vector3(_input.Color.r, _input.Color.g, _input.Color.b);
            if (ImGui.ColorEdit3(nameof(_input.Color), ref color))
            {
                _input.Color.r = color.x;
                _input.Color.g = color.y;
                _input.Color.b = color.z;
            }

            CustomTypes.InputDateTimePopup(nameof(_input.EvacuationOrderDateTime), ref _input.EvacuationOrderDateTime);
            DrawOrderTimeNote();

            _destinationChoiceIndex = (int)_input.DestinationChoice;
            ImGui.Combo(nameof(_input.DestinationChoice), ref _destinationChoiceIndex, DestinationChoiceStrings, DestinationChoiceStrings.Length);
            _input.DestinationChoice = (DestinationChoices)_destinationChoiceIndex;

            //Chosen from what the scenario defines rather than typed: the parser matches these against
            //the scenario's own dictionaries and drops a group's reference when the name is not there,
            //so a typo is silent - the group simply loses that demographic, destination or curve.
            DrawNameChoiceRow(nameof(_input.Demographics), ref _input.Demographics, DemographicNames(),
                "No demographics are defined in this scenario.");

            ImGui.Checkbox(nameof(_input.Default), ref _input.Default);

            ImGui.SeparatorText("Area");
            DrawArea();

            ImGui.SeparatorText("Destinations");
            DrawList(nameof(_input.Destinations), _input.Destinations, _input.DestinationsCDF,
                DestinationNames(), "No destinations are defined in this scenario.");

            ImGui.SeparatorText("Response curves");
            DrawList(nameof(_input.ResponseCurves), _input.ResponseCurves, _input.ResponseCurvesCDF,
                ResponseCurveNames(), "No response curves are defined in this scenario.");

            ImGui.Separator();

            bool nameIsFree = !string.IsNullOrWhiteSpace(_input.Name)
                              && (_input.Name == _oldKey || _inputs == null || !_inputs.ContainsKey(_input.Name));

            ImGui.BeginDisabled(!nameIsFree);
            if (ImGui.Button("OK"))
            {
                Commit();
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

            ImGui.EndDisabled();
            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        /// <summary>
        /// Places the order time against the simulated window, since an order outside it either fires
        /// immediately or never, and neither is obvious from the timestamp alone.
        /// </summary>
        private static void DrawOrderTimeNote()
        {
            PREACT.Input.PREACTInput scenario = ScenarioSession.Input;
            if (scenario == null)
            {
                return;
            }

            DateTime start = scenario.Simulation.StartDateTime;
            DateTime end = scenario.Simulation.EndDateTime;
            DateTime order = _input.EvacuationOrderDateTime;

            if (order < start)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                    $"Before the scenario starts ({start:yyyy-MM-dd HH:mm}), so the order is already given at t = 0.");
            }
            else if (order > end)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                    $"After the scenario ends ({end:yyyy-MM-dd HH:mm}), so this group is never ordered to leave.");
            }
            else
            {
                ImGui.TextDisabled($"{(order - start).TotalMinutes:F0} minutes into the simulation.");
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
                Fields.Path(nameof(_input.ShapeFile), () => _input.ShapeFile, v => _input.ShapeFile = v,
                    filter: new[] { ".shp" });
            }

            //A group that is not in the dictionary yet cannot be painted: the paint window works from
            //the dictionary, since every group is painted against the others. Rather than a button that
            //silently leaves the new group out, this commits it first - which is what pressing OK would
            //have done anyway, and the editor stays open on it.
            //Painting works from the scenario's groups, all at once, so the group being edited is written to the
            //scenario first - what OK would have done anyway - and the editor stays open on it.
            bool nameUsable = !string.IsNullOrWhiteSpace(_input.Name)
                              && (_input.Name == _oldKey || _inputs == null || !_inputs.ContainsKey(_input.Name));

            ImGui.BeginDisabled(!nameUsable);
            if (ImGui.Button(_original == null ? "Add this group and paint the areas" : "Apply and paint group areas on the map"))
            {
                Commit();
                _input = Copy(_original);
                EvacuationGroupPaintWindow.Open(_inputs);
            }
            ImGui.EndDisabled();

            if (!nameUsable)
            {
                ImGui.TextDisabled("Name the group first: painting works on all the groups at once, and they are identified by name.");
            }
            ImGui.TextWrapped("Painting covers every group at once, since a cell belongs to only one group.");
        }

        /// <summary>
        /// The names the scenario defines, for the three fields that reference one. Read fresh each
        /// frame rather than cached, since a destination or curve can be added while this is open.
        /// </summary>
        private static string[] DestinationNames()
        {
            PREACT.Input.PREACTInput scenario = ScenarioSession.Input;
            if (scenario == null) return new string[0];
            return new List<string>(scenario.Evacuation.EvacuationDestinationInputs.Keys).ToArray();
        }

        private static string[] ResponseCurveNames()
        {
            PREACT.Input.PREACTInput scenario = ScenarioSession.Input;
            if (scenario == null) return new string[0];
            return new List<string>(scenario.Evacuation.ResponseCurves.Keys).ToArray();
        }

        private static string[] DemographicNames()
        {
            PREACT.Input.PREACTInput scenario = ScenarioSession.Input;
            if (scenario == null) return new string[0];
            return new List<string>(scenario.Population.Demographics.Keys).ToArray();
        }

        /// <summary>
        /// A combo over the names the scenario defines, with an empty first entry so a field can be
        /// cleared. A value that is not among them is kept and shown as missing rather than being
        /// silently replaced with the first choice - it is usually a renamed destination, and losing
        /// which one it was makes that unrecoverable.
        /// </summary>
        /// <param name="missing">
        /// True when the current value is not among the choices. Reported back rather than drawn here,
        /// so a caller laying a row out with SameLine decides where the warning goes.
        /// </param>
        private static bool DrawNameChoice(string label, ref string value, string[] choices, out bool missing)
        {
            //A name the scenario does not define is offered as a choice of its own rather than being
            //replaced by the first entry, because that value is usually a renamed destination and
            //quietly overwriting it loses which one it was.
            missing = !string.IsNullOrEmpty(value) && System.Array.IndexOf(choices, value) < 0;

            int extra = missing ? 2 : 1;
            string[] entries = new string[choices.Length + extra];
            entries[0] = "(none)";
            if (missing)
            {
                entries[1] = value + "  <- not defined";
            }
            System.Array.Copy(choices, 0, entries, extra, choices.Length);

            int index = 0;
            if (missing)
            {
                index = 1;
            }
            else if (!string.IsNullOrEmpty(value))
            {
                index = System.Array.IndexOf(choices, value) + extra;
            }

            bool changed = ImGui.Combo(label, ref index, entries, entries.Length);
            if (changed)
            {
                if (index == 0)
                {
                    value = string.Empty;
                }
                else if (index >= extra)
                {
                    value = choices[index - extra];
                }
                //index 1 with a missing value is the value itself; nothing to change.
            }

            return changed;
        }

        /// <summary>
        /// Draws a name choice on its own line, with the "not defined" warning underneath it.
        /// </summary>
        private static void DrawNameChoiceRow(string label, ref string value, string[] choices, string emptyMessage)
        {
            if (choices.Length == 0 && string.IsNullOrEmpty(value))
            {
                ImGui.TextDisabled(label + ": " + emptyMessage);
                return;
            }

            DrawNameChoice(label, ref value, choices, out bool missing);
            if (missing)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                    $"\"{value}\" is not defined in this scenario, and is dropped when it is loaded.");
            }
        }

        /// <summary>
        /// A group's destinations and response curves are each a list of names with a parallel
        /// cumulative distribution, so the two are edited together - a name added without a
        /// corresponding CDF entry is read as an unreachable choice.
        /// </summary>
        private static void DrawList(string label, List<string> names, List<double> cdf, string[] choices, string emptyMessage)
        {
            if (names == null)
            {
                return;
            }

            if (choices.Length == 0)
            {
                ImGui.TextDisabled(emptyMessage);
            }

            int removeAt = -1;
            bool anyMissing = false;
            for (int i = 0; i < names.Count; ++i)
            {
                ImGui.PushID(label + i);

                string name = names[i];
                ImGui.SetNextItemWidth(220);
                if (DrawNameChoice("###name", ref name, choices, out bool missing))
                {
                    names[i] = name;
                }
                anyMissing |= missing;

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

            //Once for the list rather than per row, so the rows stay on one line each.
            if (anyMissing)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                    "Entries marked \"not defined\" do not exist in this scenario and are dropped when it is loaded.");
            }

            if (removeAt >= 0)
            {
                names.RemoveAt(removeAt);
                if (removeAt < cdf.Count)
                {
                    cdf.RemoveAt(removeAt);
                }
            }

            ImGui.BeginDisabled(choices.Length == 0);
            if (ImGui.Button("Add###" + label))
            {
                //The first choice not already listed, since duplicating one is never what was wanted
                //and an empty row is a choice that can never be drawn.
                string toAdd = choices.Length > 0 ? choices[0] : string.Empty;
                for (int i = 0; i < choices.Length; ++i)
                {
                    if (!names.Contains(choices[i]))
                    {
                        toAdd = choices[i];
                        break;
                    }
                }
                names.Add(toAdd);
                cdf.Add(1.0);
            }
            ImGui.EndDisabled();

            //The last entry has to reach 1 or the tail of the distribution is unreachable.
            if (cdf.Count > 0 && cdf[cdf.Count - 1] < 0.999)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), $"The last cumulative value is {cdf[cdf.Count - 1]:F3}; it should reach 1.");
            }
        }
    }
}
