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

        //A destination has to sit on a lane: SUMO resolves it with convertRoad, which returns the nearest
        //edge without ever saying how far away it was, so one dropped beside the road becomes whichever
        //road happened to be nearest and nothing reports it. Snapping on placement is on by default for
        //that reason, and what it did is shown below.
        static bool _snapToRoad = true;
        static string _snapDescription = string.Empty;

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
            //Float2 keeps six or seven significant digits, which at these magnitudes is about a metre of
            //latitude - enough to walk a snapped destination off its lane again. So the value is only
            //written back when the field was actually edited, leaving a snapped position at full precision.
            Vector2 latLon = new Vector2((float)_input.LatLon.x, (float)_input.LatLon.y);
            if(ImGui.InputFloat2(nameof(_input.LatLon), ref latLon))
            {
                _input.LatLon.x = latLon.x;
                _input.LatLon.y = latLon.y;
                _snapDescription = string.Empty;
            }

            ImGui.Checkbox("Snap to the nearest road lane when placed", ref _snapToRoad);
            ImGui.SameLine();
            if (ImGui.Button("Snap now"))
            {
                SnapToNearestLane(true);
            }

            if (!string.IsNullOrEmpty(_snapDescription))
            {
                ImGui.TextDisabled(_snapDescription);
            }

            if (ImGui.Button("Show road network"))
            {
                PreactGUI.WUInity.ShowRoadNetwork(true);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Draws the lanes traffic is routed on, so a destination can be placed against "
                    + "them rather than against the satellite image.");
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
                //The markers are spawned from the scenario, so they have to be respawned for an edit to
                //appear. Without this a moved destination stayed drawn where it was, which reads as an
                //edit that did not take.
                PreactGUI.WUInity.RefreshDestinationMarkers();
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
            _snapDescription = string.Empty;

            if (_snapToRoad)
            {
                //Quietly when it works: a click on a road that moves by a metre does not need announcing.
                //Reported when there is no network, since then the position stands as clicked.
                SnapToNearestLane(false);
            }

            Open(_inputs, _input);
        }

        /// <summary>
        /// Moves the destination onto the nearest point of the nearest lane, and says which lane and how
        /// far it moved.
        ///
        /// Onto the lane rather than onto the edge's centreline: an edge is a carriageway and its lanes are
        /// separate geometry, so on a dual carriageway the two directions are metres apart and only one of
        /// them is the side traffic arrives on.
        /// </summary>
        private static void SnapToNearestLane(bool reportWhenAlreadyOnRoad)
        {
            if (!ScenarioEditorWindow.HasInput)
            {
                _snapDescription = "No scenario is loaded, so there is no network to snap to.";
                return;
            }

            PREACT.Input.SimulationData data = ScenarioEditorWindow.Input.Simulation.Data;
            PREACT.Math.Vector2d simulationPos = data.GetSimulationPosition(_input.LatLon);

            if (!PreactGUI.WUInity.TrySnapToRoadNetwork(simulationPos, out PREACT.Utility.SumoNetworkGeometry.Snap snap))
            {
                //The reason is logged by the loader, which knows whether the network is missing, unbuilt or
                //unreadable. Said here as well, because this window is where it matters.
                _snapDescription = "No road network to snap to - see the console.";
                return;
            }

            _input.LatLon = data.GetWGS84FromSimulationPosition(snap.SimulationPos);
            _snapDescription = $"On lane {snap.LaneId} (edge {snap.EdgeId}), moved {snap.Distance:F1} m.";

            if (!reportWhenAlreadyOnRoad && snap.Distance < 1.0)
            {
                _snapDescription = $"On lane {snap.LaneId} (edge {snap.EdgeId}).";
            }
        }
    }
}
