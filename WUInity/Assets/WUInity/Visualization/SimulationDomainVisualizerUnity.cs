//This file is part of WUIPlatform Copyright (C) 2024 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using UnityEngine;
using PREACT.Population;
using PREACT;
using PREACT.Runtime;
using PREACT.Math;
using PREACT.Evacuation;
using PREACT.Input;

namespace WUInity.Visualization
{
    public class SimulationDomainVisualizerUnity : SimulationDomainVisualizer
    {
        private GameObject _simulationDomainPlane;
        MeshRenderer _simulationDomainMeshRenderer;
        //textures
        private Texture2D _populationMapTexture;

        private Vector2d _simulationDomainSize, _simulationDomainLatLon;

        //markers
        GameObject[] _goalMarkers;
        GameObject[] _ignitionMarkers;
        bool _markersVisible = true;

        /// <summary>Whether the destination and ignition markers are shown (View &gt; Map layers &gt; Markers).</summary>
        public bool MarkersVisible { get => _markersVisible; }

        /// <summary>Shows or hides every destination and ignition marker, now and for markers spawned later.</summary>
        public void SetMarkersVisible(bool visible)
        {
            _markersVisible = visible;
            SetActive(_goalMarkers, visible);
            SetActive(_ignitionMarkers, visible);
        }

        private static void SetActive(GameObject[] markers, bool visible)
        {
            if (markers == null) return;
            foreach (GameObject marker in markers)
            {
                if (marker != null) marker.SetActive(visible);
            }
        }

        public SimulationDomainVisualizerUnity(Transform parent)
        {
            _simulationDomainPlane = new GameObject("SimulationDomain");            
            _simulationDomainPlane.transform.parent = parent;
            _simulationDomainPlane.transform.position += Vector3.up;
            _simulationDomainPlane.isStatic = true;
        }

        private void CheckIfNeedNewSimulationDomainPlane(PopulationMap data)
        {
            if (DomainVisualizerUnity.NeedNewPlane(_simulationDomainSize, data._size, _simulationDomainLatLon, data._lowerLeftLatLong) || _simulationDomainMeshRenderer == null)
            {
                _simulationDomainMeshRenderer = DomainVisualizerUnity.CreateDomainPlane(_simulationDomainPlane, _simulationDomainMeshRenderer, data._size, Vector2d.zero);
            }
            _simulationDomainSize = data._size;
            _simulationDomainLatLon = data._lowerLeftLatLong;
        }

        public override void SetAndDisplayPopulationMapTexture(PopulationMap data, WorkingData workingData)
        {
            if (DomainVisualizerUnity.NeedNewTexture(data._cells, _populationMapTexture))
            {
                _populationMapTexture = new Texture2D(data._cells.x, data._cells.y);
                _populationMapTexture.filterMode = FilterMode.Point;
            }

            //update cached data
            workingData.PopulationMap = data;

            CheckIfNeedNewSimulationDomainPlane(data);

            for (int y = 0; y < data._cells.y; y++)
            {
                for (int x = 0; x < data._cells.x; x++)
                {
                    double density = data.GetPeopleCount(x, y) / data._cellArea;
                    PREACTColor color = GetGPWColor((float)density);
                    if (density == 0)
                    {
                        color.a = 0f;
                    }

                    _populationMapTexture.SetPixel(x, y, color.UnityColor());
                }
            }
            _populationMapTexture.Apply();
            _simulationDomainMeshRenderer.material.mainTexture = _populationMapTexture;
            SetVisibility(true);
        }

        public override bool IsDataPlaneActive()
        {
            return _simulationDomainPlane.activeSelf;
        }

        //The simulation domain plane, which is what this class's other members are about. These used to act
        //on a separate GPW plane instead, in a separate frame, so showing the population density switched
        //something else on and left the plane holding the texture untouched, while hiding "the domain data"
        //before painting hid nothing. The GPW plane itself is gone: nothing drew on it any more.
        public override void SetVisibility(bool activeSelf)
        {
            _simulationDomainPlane.SetActive(activeSelf);
        }

        public override bool ToggleVisibility()
        {
            _simulationDomainPlane.SetActive(!_simulationDomainPlane.activeSelf);

            return _simulationDomainPlane.activeSelf;
        }

        public void SpawnEvacuationGoalMarkers(PREACTInput input, GameObject markerPrefab)
        {
            ClearDestinationMarkers();

            if (input.Evacuation.EvacuationDestinationInputs.Count == 0)
            {
                return;
            }            

            _goalMarkers = new GameObject[input.Evacuation.EvacuationDestinationInputs.Count];

            int index = 0;
            foreach (EvacuationDestinationInput eDI in input.Evacuation.EvacuationDestinationInputs.Values)
            {
                _goalMarkers[index] = MonoBehaviour.Instantiate<GameObject>(markerPrefab);
                _goalMarkers[index].name = $"Destination {eDI.Name}";
                Vector2d pos = input.Simulation.Data.GetSimulationPosition(eDI.LatLon);

                float scale = 0.02f * (float)Mathd.Max(input.Simulation.DomainSize.x, input.Simulation.DomainSize.y);
                _goalMarkers[index].transform.localScale = new Vector3(scale, 100f, scale);
                _goalMarkers[index].transform.position = new Vector3((float)pos.x, 0f, (float)pos.y);
                MeshRenderer mR = _goalMarkers[index].GetComponentInChildren<MeshRenderer>();
                mR.material.color = eDI.Color.UnityColor();

                ++index;
            }
            SetActive(_goalMarkers, _markersVisible);
        }

        public void SpawnEvacuationGoalMarkers(PREACTInput input, System.Collections.Generic.List<EvacuationDestination> destinations, GameObject markerPrefab)
        {
            ClearDestinationMarkers();

            if (destinations.Count == 0)
            {
                return;
            }

            _goalMarkers = new GameObject[destinations.Count];
            int index = 0;
            foreach (EvacuationDestination eDI in destinations)
            {
                _goalMarkers[index] = MonoBehaviour.Instantiate<GameObject>(markerPrefab);
                _goalMarkers[index].name = $"Destination {eDI.Name}";
                Vector2d pos = input.Simulation.Data.GetSimulationPosition(eDI.LatLon);

                float scale = 0.02f * (float)Mathd.Max(input.Simulation.DomainSize.x, input.Simulation.DomainSize.y);
                _goalMarkers[index].transform.localScale = new Vector3(scale, 100f, scale);
                _goalMarkers[index].transform.position = new Vector3((float)pos.x, 0f, (float)pos.y);
                MeshRenderer mR = _goalMarkers[index].GetComponentInChildren<MeshRenderer>();
                mR.material.color = eDI.Color.UnityColor();

                ++index;
            }
            SetActive(_goalMarkers, _markersVisible);
        }

        public void SpawnWildfireIgnitionMarkers(PREACTInput input, GameObject markerPrefab)
        {
            ClearIgnitionMarkers();

            _ignitionMarkers = new GameObject[input.WildfireModule.Data.IgnitionPoints.Count];

            int index = 0;
            foreach (PREACT.Wildfire.IgnitionPointInput point in input.WildfireModule.Data.IgnitionPoints)
            {
                _ignitionMarkers[index] = MonoBehaviour.Instantiate<GameObject>(markerPrefab);
                _ignitionMarkers[index].name = $"Ignition [{point.LatLon.x}, {point.LatLon.y}]";
                Vector2d pos = input.Simulation.Data.GetSimulationPosition(point.LatLon);

                float scale = 0.02f * (float)Mathd.Max(input.Simulation.DomainSize.x, input.Simulation.DomainSize.y);
                _ignitionMarkers[index].transform.localScale = new Vector3(scale, 100f, scale);
                _ignitionMarkers[index].transform.position = new Vector3((float)pos.x, 0f, (float)pos.y);
                MeshRenderer mR = _ignitionMarkers[index].GetComponentInChildren<MeshRenderer>();
                mR.material.color = Color.white;

                ++index;
            }
            SetActive(_ignitionMarkers, _markersVisible);
        }

        private void ClearDestinationMarkers()
        {
            if (_goalMarkers != null)
            {
                for (int i = 0; i < _goalMarkers.Length; i++)
                {
                    if (_goalMarkers[i] != null)
                    {
                        MonoBehaviour.Destroy(_goalMarkers[i]);
                    }
                }
            }
        }

        private void ClearIgnitionMarkers()
        {
            if (_ignitionMarkers != null)
            {
                for (int i = 0; i < _ignitionMarkers.Length; i++)
                {
                    if (_ignitionMarkers[i] != null)
                    {
                        MonoBehaviour.Destroy(_ignitionMarkers[i]);
                    }
                }
            }
        }
    }
}