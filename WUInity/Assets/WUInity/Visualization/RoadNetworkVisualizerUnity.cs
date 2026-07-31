//This file is part of WUIPlatform Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using UnityEngine;
using PREACT.Math;
using PREACT.Utility;

namespace WUInity.Visualization
{
    /// <summary>
    /// Draws the road network vehicles actually drive on, as lines over the map.
    ///
    /// The lanes come from the SUMO network rather than from the OSM file. They are the same roads, but the
    /// SUMO network is what the traffic simulation routes on: netconvert drops what cars cannot use, joins
    /// junctions and splits carriageways, so an OSM way and the lanes it became are not the same geometry.
    /// Showing the OSM ways would mean showing something the simulation does not have, which is the opposite
    /// of useful when the reason to look is to place a destination on a road.
    ///
    /// One mesh of lines rather than a LineRenderer per lane: a town is tens of thousands of segments, and
    /// that many renderers costs more to cull than to draw.
    /// </summary>
    public class RoadNetworkVisualizerUnity
    {
        private readonly GameObject _networkObject;
        private MeshRenderer _meshRenderer;
        private MeshFilter _meshFilter;

        //Just above the map planes, which sit at y = 1, so the roads are not buried in whatever raster is
        //being shown underneath them.
        private const float Height = 1.5f;

        public RoadNetworkVisualizerUnity(Transform parent)
        {
            _networkObject = new GameObject("RoadNetwork");
            _networkObject.transform.parent = parent;
            _networkObject.SetActive(false);
        }

        public bool IsVisible { get => _networkObject.activeSelf; }

        /// <summary>
        /// Builds the line mesh for the given lanes. Returns false when there is nothing to draw.
        /// </summary>
        public bool Build(SumoNetworkGeometry geometry, Color color)
        {
            if (geometry == null || geometry.LaneCount == 0)
            {
                return false;
            }

            int segments = 0;
            foreach (SumoNetworkGeometry.Lane lane in geometry.Lanes)
            {
                segments += lane.Points.Length - 1;
            }

            if (segments == 0)
            {
                return false;
            }

            Vector3[] vertices = new Vector3[segments * 2];
            int[] indices = new int[segments * 2];

            int v = 0;
            foreach (SumoNetworkGeometry.Lane lane in geometry.Lanes)
            {
                for (int i = 0; i < lane.Points.Length - 1; ++i)
                {
                    Vector2d a = lane.Points[i];
                    Vector2d b = lane.Points[i + 1];
                    vertices[v] = new Vector3((float)a.x, Height, (float)a.y);
                    vertices[v + 1] = new Vector3((float)b.x, Height, (float)b.y);
                    indices[v] = v;
                    indices[v + 1] = v + 1;
                    v += 2;
                }
            }

            if (_meshFilter == null)
            {
                _meshFilter = _networkObject.AddComponent<MeshFilter>();
                _meshRenderer = _networkObject.AddComponent<MeshRenderer>();
                _meshRenderer.receiveShadows = false;
                _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                _meshRenderer.material = new Material(Shader.Find("Unlit/Color"));
            }

            Mesh mesh = _meshFilter.mesh;
            if (mesh == null)
            {
                mesh = new Mesh();
                _meshFilter.mesh = mesh;
            }
            mesh.Clear();
            //32-bit indices: two vertices per segment passes 65535 in any real network, and the default
            //16-bit format would wrap silently into a mesh full of lines between unrelated roads.
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = vertices;
            mesh.SetIndices(indices, MeshTopology.Lines, 0);
            mesh.RecalculateBounds();

            _meshRenderer.material.color = color;

            return true;
        }

        public void SetVisibility(bool visible)
        {
            _networkObject.SetActive(visible);
        }
    }
}
