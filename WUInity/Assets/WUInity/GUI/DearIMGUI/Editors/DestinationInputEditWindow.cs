using ImGuiNET;
using PREACT;
using PREACT.Input;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI.Editors
{ 
    public static class DestinationInputEditWindow
    {
        private static bool _isOpen;
        private static EvacuationDestinationInput _input;
        private static Dictionary<string, EvacuationDestinationInput> _inputs;
        public static string[] DestinationTypesStrings;
        static int _destinationTypeIndex;
        static string _oldKey = string.Empty;

        static DestinationInputEditWindow()
        {
            DestinationTypesStrings = Enum.GetNames(typeof(DestinationTypes));
        }

        public static void Open(Dictionary<string, EvacuationDestinationInput> inputs, EvacuationDestinationInput input)
        {
            if(!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;

            _inputs = inputs;
            if(input == null)
            {
                _input = new EvacuationDestinationInput();
                _oldKey = string.Empty;
            }
            else
            {
                _input = input;
                _oldKey = _input.Name;
            }           
        }

        public static void Close()
        {
            if (_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
            _isOpen = false;
        }

        public static void Draw()
        {
            if(!_isOpen)
            {
                return;
            }

            ImGui.Begin("Evacuation destination editor", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            ImGui.InputText(nameof(_input.Name), ref _input.Name, 64);

            if(ImGui.Button("Set on map")) 
            {
                Close();
                PreactGUI.WUInity.PickPosOnMap(SetDestinationPos); 
            }
            ImGui.SameLine();
            Vector2 latLon = new Vector2((float)_input.LatLon.x, (float)_input.LatLon.y);
            if(ImGui.InputFloat2(nameof(_input.LatLon), ref latLon))
            {
                _input.LatLon.x = latLon.x;
                _input.LatLon.y = latLon.y;
            }            

            _destinationTypeIndex = (int)_input.Type;
            ImGui.Combo(nameof(_input.Type), ref _destinationTypeIndex, DestinationTypesStrings, DestinationTypesStrings.Length);
            _input.Type = (DestinationTypes)_destinationTypeIndex;

            //Capacity is a shelter's property: an Exit is a way out of the domain and holds everyone
            //who reaches it. Flow is not - it throttles arrivals at either kind - so it is asked for
            //in both cases. Every one of the three is off when negative, which the labels now say,
            //because a blank-looking "-1" reads as unset rather than as unlimited.
            ImGui.InputFloat("Max arrival flow (cars/hour, -1 for no limit)", ref _input.MaxFlow);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Vehicles beyond this rate wait rather than arriving. It does not turn them away.");
            }

            if(_input.Type == DestinationTypes.Shelter)
            {
                ImGui.InputInt("Max vehicles (-1 for no limit)", ref _input.MaxVehicles);
                ImGui.InputInt("Max people (-1 for no limit)", ref _input.MaxPeople);
                ImGui.TextWrapped("Whichever limit is reached first closes the shelter for good, and vehicles "
                    + "still heading for it are re-routed to another destination. Capacity is never freed again.");
            }
            else
            {
                ImGui.TextDisabled("Exits have no capacity: everyone who reaches one leaves the domain.");
            }

            Vector3 color = new Vector3((float)_input.Color.r, (float)_input.Color.g, (float)_input.Color.b);
            ImGui.ColorEdit3(nameof(_input.Color), ref color);
            _input.Color.r = color.x;
            _input.Color.g = color.y;
            _input.Color.b = color.z;

            //A name is the dictionary key, so an empty or already-taken one cannot be committed.
            //This used to Remove the old key then Add unconditionally, which threw an unhandled
            //ArgumentException whenever a new destination reused an existing name, and silently
            //filed a nameless one under the empty string.
            bool nameIsFree = !string.IsNullOrWhiteSpace(_input.Name)
                              && (_input.Name == _oldKey || !_inputs.ContainsKey(_input.Name));

            ImGui.BeginDisabled(!nameIsFree);
            if (ImGui.Button("OK"))
            {
                _inputs.Remove(_oldKey);
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
            if(!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }    
        
        private static void SetDestinationPos(PREACT.Math.Vector2d simulationPos)
        {
            _input.LatLon = ScenarioEditorWindow.Input.Simulation.Data.GetWGS84FromSimulationPosition(simulationPos);
            Open(_inputs, _input);
        }
    }
}
