using ImGuiNET;
using PREACT;
using PREACT.Input;
using UnityEngine;
using System.Collections.Generic;
using System.Text;
using PREACT.Evacuation;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class DemographicsInputEditorWindow
    {
        private static bool _isOpen;
        //The copy being edited, and the scenario's own entry (null for a new one). Ticking Default used to
        //clear every other demographics on the spot, before OK - and with no Cancel, there was no way back.
        private static DemographicsInput _input;
        private static DemographicsInput _original;
        private static Dictionary<string, DemographicsInput> _inputs;
        static string _oldKey = string.Empty;
        private static bool _subscribed;

        public static void Open(Dictionary<string, DemographicsInput> inputs, DemographicsInput input)
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
                //The first demographics of a scenario is its default, since a scenario needs one.
                _input = new DemographicsInput { Default = inputs.Count == 0 };
                _oldKey = string.Empty;
            }
            else
            {
                _input = new DemographicsInput
                {
                    Name = input.Name,
                    AllowMoreThanOneCar = input.AllowMoreThanOneCar,
                    MaxCars = input.MaxCars,
                    MaxCarsProbability = input.MaxCarsProbability,
                    Default = input.Default,
                };
                _oldKey = input.Name;
            }
        }

        private static void Commit()
        {
            //Only one can be the default, applied now that the edit is committed.
            if (_input.Default)
            {
                foreach (KeyValuePair<string, DemographicsInput> kV in _inputs)
                {
                    kV.Value.Default = false;
                }
            }

            DemographicsInput target = _original ?? new DemographicsInput();
            if (_original != null)
            {
                _inputs.Remove(_oldKey);
            }
            target.Name = _input.Name;
            target.AllowMoreThanOneCar = _input.AllowMoreThanOneCar;
            target.MaxCars = _input.MaxCars;
            target.MaxCarsProbability = _input.MaxCarsProbability;
            target.Default = _input.Default;
            _inputs[target.Name] = target;

            //Groups refer to demographics by name.
            if (_original != null && _oldKey != target.Name)
            {
                foreach (EvacuationGroupInput group in ScenarioSession.Input.Evacuation.EvacuationGroupInputs.Values)
                {
                    if (group.Demographics == _oldKey) group.Demographics = target.Name;
                }
            }

            ScenarioSession.NotifyEdited("demographics " + target.Name);
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(400f, 220f));
            ImGui.Begin("Demographics###DemographicsEditor", ref _isOpen, PreactGUI.ToolWindowFlags);
            ImGui.BeginDisabled(ScenarioSession.EditingLocked);

            ImGui.Checkbox(nameof(_input.Default), ref _input.Default);
            Fields.Hint("The demographics households get when their group names none. Only one can be the",
                        "default; ticking this one unticks the others when you press OK.");

            ImGui.InputText(nameof(_input.Name), ref _input.Name, 64);
            ImGui.Checkbox(nameof(_input.AllowMoreThanOneCar), ref _input.AllowMoreThanOneCar);
            if (_input.AllowMoreThanOneCar)
            {
                ImGui.InputInt(nameof(_input.MaxCars), ref _input.MaxCars);
                ImGui.InputFloat(nameof(_input.MaxCarsProbability), ref _input.MaxCarsProbability);
            }

            //Same guard as the destination editor: the name is the key, so it has to be present and
            //free. Renaming onto an existing entry previously threw an unhandled ArgumentException,
            //and a brand new entry was never added at all when its name happened to be unchanged
            //from the empty default.
            bool nameIsFree = !string.IsNullOrWhiteSpace(_input.Name)
                              && (_input.Name == _oldKey || !_inputs.ContainsKey(_input.Name));

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
                ImGui.SameLine();
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(_input.Name) ? "Needs a name." : "That name is already used.");
            }

            ImGui.EndDisabled();
            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }


    }
}
