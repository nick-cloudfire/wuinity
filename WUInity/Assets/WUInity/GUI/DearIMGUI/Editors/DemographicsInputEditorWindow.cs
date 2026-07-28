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
        private static DemographicsInput _input;
        private static Dictionary<string, DemographicsInput> _inputs;
        static string _oldKey = string.Empty;

        static DemographicsInputEditorWindow()
        {

        }

        public static void Open(Dictionary<string, DemographicsInput> inputs, DemographicsInput input)
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;

            _inputs = inputs;
            if (input == null)
            {
                _input = new DemographicsInput();
                _oldKey = string.Empty;
            }
            else
            {
                _input = input;
                _oldKey = _input.Name;
            }
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.Begin("Demographics editor", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            //Only clears the others when this one is being turned ON. It used to force Default back
            //to true on every toggle, so the box could never be unticked.
            if(ImGui.Checkbox(nameof(_input.Default), ref _input.Default))
            {
                if (_input.Default)
                {
                    foreach(KeyValuePair<string, DemographicsInput> kV in _inputs)
                    {
                        kV.Value.Default = false;
                    }
                    _input.Default = true;
                }
            }

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
                if (_input.Name != _oldKey)
                {
                    _inputs.Remove(_oldKey);
                }
                _inputs[_input.Name] = _input;
                _isOpen = false;
            }
            ImGui.EndDisabled();
            if (!nameIsFree)
            {
                ImGui.SameLine();
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(_input.Name) ? "Needs a name." : "That name is already used.");
            }

            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }


    }
}
