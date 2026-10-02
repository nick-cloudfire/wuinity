//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using PREACT.Math;

namespace PREACT.Evacuation
{
    public class EvacuationGroup
    {
        private string _name;
        private System.DateTime _evacuationOrderDateTime;
        private DestinationChoices _destinationChoice;
        private PREACTColor _color;
        private bool _default;
        private List<EvacuationDestination> _destinations;
        private List<double> _destinationsCDF;
        private List<ResponseCurve> _responseCurves;
        private List<double> _responseCurvesCDF;
        private DemographicsInput _demographics;

        //The group's ground, from its painted mask or its shapefile: the same area the case build writes into the
        //WUI area and k-PERIL protects (EvacuationGroupArea).
        private EvacuationGroupArea _area;

        public string Name { get => _name; }
        public System.DateTime EvacuationOrderDateTime { get => _evacuationOrderDateTime; }
        public PREACTColor Color { get => _color; }
        public bool Default { get => _default; }
        public List<EvacuationDestination> Destinations { get => _destinations; }
        public DestinationChoices DestinationChoice { get => _destinationChoice; }
        public DemographicsInput Demographics { get => _demographics; }


        /// <param name="defaultDemographics">What a group gets whose <c>Demographics</c> names none that exists - the
        /// evacuation manager's default, handed in because the manager is still being constructed (verification D4: the
        /// group read <c>simulation.Evacuation</c>, still null then, and the run ended in a NullReferenceException).</param>
        public EvacuationGroup(EvacuationGroupInput groupInput, Dictionary<string, EvacuationDestination> allDestinations, Dictionary<string, ResponseCurve> allResponseCurves, Dictionary<string, DemographicsInput> allDemographics, DemographicsInput defaultDemographics, Simulation simulation)
        {
            _name = groupInput.Name;
            _evacuationOrderDateTime = groupInput.EvacuationOrderDateTime;
            _destinationChoice = groupInput.DestinationChoice;
            _color = groupInput.Color;
            _default = groupInput.Default;

            //The checklist has said so already ("default value the default demographics has been used").
            if (groupInput.Demographics == null || !allDemographics.TryGetValue(groupInput.Demographics, out _demographics))
            {
                _demographics = defaultDemographics;
            }

            //this is where we need to "re-build" the information from input. A name that refers to nothing is
            //skipped together with its CDF entry, so the two lists stay aligned; the checklist has already said
            //so (critical when the module that needs it is on). It used to log a SimulationError here, which
            //stopped the run even when the traffic module that needs destinations was switched off.
            _destinations = new List<EvacuationDestination>(groupInput.Destinations.Count);
            _destinationsCDF = new List<double>(groupInput.Destinations.Count);
            for(int i = 0; i < groupInput.Destinations.Count; ++i)
            {
                if (allDestinations.TryGetValue(groupInput.Destinations[i], out EvacuationDestination eD))
                {
                    _destinations.Add(eD);
                    _destinationsCDF.Add(i < groupInput.DestinationsCDF.Count ? groupInput.DestinationsCDF[i] : 1.0);
                }
                else
                {
                    Engine.Message(simulation, Engine.LogType.Warning, $"Evacuation group {_name}: destination {groupInput.Destinations[i]} does not exist and is ignored.");
                }
            }

            _responseCurves = new List<ResponseCurve>(groupInput.ResponseCurves.Count);
            _responseCurvesCDF = new List<double>(groupInput.ResponseCurves.Count);
            for (int i = 0; i < groupInput.ResponseCurves.Count; ++i)
            {
                if (allResponseCurves.TryGetValue(groupInput.ResponseCurves[i], out ResponseCurve rC))
                {
                    _responseCurves.Add(rC);
                    _responseCurvesCDF.Add(i < groupInput.ResponseCurvesCDF.Count ? groupInput.ResponseCurvesCDF[i] : 1.0);
                }
                else
                {
                    Engine.Message(simulation, Engine.LogType.Warning, $"Evacuation group {_name}: response curve {groupInput.ResponseCurves[i]} does not exist and is ignored.");
                }
            }

            //A painted mask wins over a shapefile, as the parser says; a file that cannot be read stops the run, since
            //nobody could then be placed in the group it describes.
            _area = EvacuationGroupArea.Load(groupInput, simulation.Input.RootFolder, simulation.Input.Simulation.Data,
                out string problem, out bool fatal);
            if (problem != null)
            {
                Engine.Message(simulation, fatal ? Engine.LogType.SimulationError : Engine.LogType.Warning, problem);
            }
        }

        public static EvacuationGroup[] CreateGroupsFromInput(Dictionary<string, EvacuationGroupInput> groupsInput, Dictionary<string, 
            EvacuationDestination> allDestinations, Dictionary<string, ResponseCurve> allResponseCurves, Dictionary<string, DemographicsInput> allDemographics,
            DemographicsInput defaultDemographics, Simulation simulation)
        {
            EvacuationGroup[] groups = new EvacuationGroup[groupsInput.Count];
            int index = 0;
            foreach(EvacuationGroupInput eGI in groupsInput.Values)
            {
                groups[index] = new EvacuationGroup(eGI, allDestinations, allResponseCurves, allDemographics, defaultDemographics, simulation);
                ++index;
            }

            return groups;
        }

        //https://en.wikipedia.org/wiki/Point_in_polygon
        //https://stackoverflow.com/questions/4243042/c-sharp-point-in-polygon
        public bool LatLonBelongsToGroup(Vector2d latLon, Simulation simulation)
        {
            return SimulationPositionBelongsToGroup(simulation.Input.Simulation.Data.GetSimulationPosition(latLon));
        }

        /// <summary>
        /// The same test against a point already in simulation coordinates, which is what the
        /// polygon is stored in. Rasterising a group onto the fire grid works in these coordinates
        /// throughout, so going out to lat/lon and straight back for every cell would only add
        /// conversion error and cost.
        /// </summary>
        public bool SimulationPositionBelongsToGroup(Vector2d testedPoint)
        {
            return _area != null && _area.Contains(testedPoint);
        }

        /// <summary>The group's area, as the WUI area is made from it.</summary>
        public EvacuationGroupArea Area { get => _area; }

        /// <summary>
        /// A destination drawn from the group's cumulative distribution.
        /// </summary>
        /// <remarks>
        /// A draw above the last CDF value (a CDF that ends below 1, which the load reports) goes to the <b>last</b>
        /// destination, as the message always said; it used to go to the first. Null only for a group with no
        /// destination at all, which the checklist makes critical whenever traffic is on.
        /// </remarks>
        public EvacuationDestination GetWeightedRandomDestination()
        {
            if (_destinations.Count == 0)
            {
                return null;
            }

            float randomChoice = Random.valueF;
            for (int i = 0; i < _destinationsCDF.Count && i < _destinations.Count; i++)
            {
                if (randomChoice <= _destinationsCDF[i])
                {
                    return _destinations[i];
                }
            }

            return _destinations[_destinations.Count - 1];
        }

        public EvacuationDestination GetClosestEuclideanDestination(Vector2d startLatLon, Simulation simulation)
        {
            Vector2d householdPos = simulation.Input.Simulation.Data.GetSimulationPosition(startLatLon);
            double closestDistance = double.MaxValue;
            EvacuationDestination closestDestination = null;

            for (int i = 0; i < Destinations.Count; ++i)
            {
                Vector2d destPos = simulation.Input.Simulation.Data.GetSimulationPosition(Destinations[i].LatLon);
                double distance = Vector2d.SqrMagnitude(destPos - householdPos);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestDestination = Destinations[i];
                }                         
            }
            
            return closestDestination;
        }

        /// <summary>
        /// A household's response time in seconds after the simulation start, drawn from one of the group's
        /// curves; <see cref="float.MaxValue"/> for a household that does not evacuate (a draw above the curve's
        /// final probability).
        /// </summary>
        /// <param name="evacuationOrderStart">Seconds from the simulation start to this group's evacuation order.
        /// Added to a Relative curve; an Absolute curve is already measured from the simulation start.</param>
        public float GetWeightedRandomResponseTime(float evacuationOrderStart)
        {
            float responseTime = float.MaxValue;
            if (_responseCurves.Count == 0)
            {
                return responseTime;
            }

            float r = Random.valueF;
            //get curve index from evac group; a draw above the last CDF value takes the last curve
            ResponseCurve pickedCurve = _responseCurves[_responseCurves.Count - 1];
            for (int i = 0; i < _responseCurves.Count && i < _responseCurvesCDF.Count; i++)
            {
                if (r <= _responseCurvesCDF[i])
                {
                    pickedCurve = _responseCurves[i];
                    break;
                }
            }

            float offset = pickedCurve.TimeInput == TimeInputs.Absolute ? 0f : evacuationOrderStart;

            //need new random
            r = Random.valueF;            
            for (int i = 1; i < pickedCurve.DataPoints.Length; i++) //skip first as that is always zero probability
            {
                if (r <= pickedCurve.DataPoints[i].Probability)
                {
                    responseTime = Random.Range(pickedCurve.DataPoints[i - 1].Time, pickedCurve.DataPoints[i].Time) + offset;
                    break;
                }
            }

            return responseTime;
        }
    }
}
