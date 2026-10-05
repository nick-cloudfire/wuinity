//This file is part of WUIPlatform Copyright (C) 2024 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using UnityEngine;
using PREACT;
using PREACT.Wildfire;
using PREACT.Math;
using PREACT.Visualization;

namespace WUInity.Visualization
{
    public class FireDomainVisualizerUnity : FireDomainVisualizer
    {
        private GameObject _lcpDomainPlane;
        MeshRenderer _lcpDomainMeshRenderer;
        LandscapeData _lcpData;
        //The rectangle the plane currently covers, so a plane made for one grid is rebuilt rather than
        //reused when a texture on a different grid arrives.
        Vector2d _planeSize, _planeOffset;
        //textures
        Texture2D _fuelModelsTexture, _elevationTexture, _slopeTexture, _aspectTexture;
          

        //A plane of its own for results (trigger boundaries, probabilities, arrival times), above the painted
        //areas' plane, so a result can be looked at against what was painted.
        private GameObject _rasterPlane;
        private MeshRenderer _rasterRenderer;
        private Vector2d _rasterPlaneSize, _rasterPlaneOffset;
        private Texture2D _rasterTexture;

        /// <summary>How a displayed raster's values become colours.</summary>
        public enum RasterRamp
        {
            /// <summary>0..1, transparent at 0, yellow to dark red.</summary>
            Probability,
            /// <summary>Times: red for the earliest valid value to blue for the latest.</summary>
            Arrival,
            /// <summary>A trigger boundary: banded reds over the cells it covers.</summary>
            Boundary,
            /// <summary>A mask: any positive value, one colour.</summary>
            Mask,
        }

        //View > Map layers > Fire case inputs: one input raster (fuel, canopy, terrain, weather, a mask) on a plane of its
        //own, between the painted areas' plane (1 m) and the result overlay (2 m), so a result reads against its inputs.
        //Its pixels arrive coloured (PREACT.Visualization.MapLayers.LayerColoring, on a worker); this only uploads them.
        private GameObject _inputPlane;
        private MeshRenderer _inputRenderer;
        private Vector2d _inputPlaneSize, _inputPlaneOffset;
        private Texture2D _inputTexture;

        public FireDomainVisualizerUnity(Transform parent)
        {
            _lcpDomainPlane = new GameObject("WildfireDomain");
            _lcpDomainPlane.transform.parent = parent;
            _lcpDomainPlane.transform.position += Vector3.up;
            _lcpDomainPlane.isStatic = true;

            _rasterPlane = new GameObject("ResultOverlay");
            _rasterPlane.transform.parent = parent;
            _rasterPlane.transform.position += 2f * Vector3.up;
            _rasterPlane.isStatic = true;
            _rasterPlane.SetActive(false);

            _inputPlane = new GameObject("InputLayerOverlay");
            _inputPlane.transform.parent = parent;
            _inputPlane.transform.position += 1.5f * Vector3.up;
            _inputPlane.isStatic = true;
            _inputPlane.SetActive(false);
        }

        public bool IsInputLayerVisible { get => _inputPlane != null && _inputPlane.activeSelf; }

        /// <summary>
        /// Shows an input layer: <paramref name="rgba"/> is <paramref name="width"/> x <paramref name="height"/> RGBA32 bytes,
        /// row 0 at the south, stretched over <paramref name="size"/> metres from the south-west corner
        /// <paramref name="originOffset"/> in simulation coordinates. The texture is replaced (and the old one destroyed)
        /// only when its size changes; the same size is written into in place.
        /// </summary>
        public bool DisplayInputLayer(byte[] rgba, int width, int height, Vector2d size, Vector2d originOffset)
        {
            if (rgba == null || width <= 0 || height <= 0 || rgba.Length != width * height * 4)
            {
                return false;
            }

            if (_inputRenderer == null || DomainVisualizerUnity.NeedNewPlane(_inputPlaneSize, size, _inputPlaneOffset, originOffset))
            {
                _inputRenderer = DomainVisualizerUnity.CreateDomainPlane(_inputPlane, _inputRenderer, size, originOffset);
                _inputPlaneSize = size;
                _inputPlaneOffset = originOffset;
            }

            if (DomainVisualizerUnity.NeedNewTexture(new Vector2int(width, height), _inputTexture))
            {
                if (_inputTexture != null) Object.Destroy(_inputTexture);
                _inputTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                _inputTexture.filterMode = FilterMode.Point;
                _inputTexture.wrapMode = TextureWrapMode.Clamp;
            }

            _inputTexture.LoadRawTextureData(rgba);
            _inputTexture.Apply(false);
            _inputRenderer.material.mainTexture = _inputTexture;
            _inputPlane.SetActive(true);
            return true;
        }

        /// <summary>Takes the input layer off the map and frees its texture.</summary>
        public void HideInputLayer()
        {
            if (_inputPlane != null) _inputPlane.SetActive(false);
            if (_inputRenderer != null) _inputRenderer.material.mainTexture = null;
            if (_inputTexture != null)
            {
                Object.Destroy(_inputTexture);
                _inputTexture = null;
            }
        }

        public bool IsRasterVisible { get => _rasterPlane != null && _rasterPlane.activeSelf; }

        public void HideRaster()
        {
            if (_rasterPlane != null) _rasterPlane.SetActive(false);
        }

        /// <summary>
        /// Shows a raster over the map: <paramref name="data"/> is [columns, rows] with row 0 at the south, as
        /// AscRaster reads it, on a grid of <paramref name="size"/> metres whose south-west corner is at
        /// <paramref name="originOffset"/> in simulation coordinates. Cells at or below zero, or equal to
        /// <paramref name="noData"/>, are transparent. Reports the range of the values it coloured.
        /// </summary>
        public bool DisplayRaster(float[,] data, double noData, Vector2d size, Vector2d originOffset, RasterRamp ramp,
            out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            if (data == null)
            {
                return false;
            }

            int xPixels = data.GetLength(0);
            int yPixels = data.GetLength(1);

            for (int y = 0; y < yPixels; y++)
            {
                for (int x = 0; x < xPixels; x++)
                {
                    float v = data[x, y];
                    if (!Valid(v, noData, ramp)) continue;
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }

            if (min > max)
            {
                min = max = 0f;
            }

            if (_rasterRenderer == null || DomainVisualizerUnity.NeedNewPlane(_rasterPlaneSize, size, _rasterPlaneOffset, originOffset))
            {
                _rasterRenderer = DomainVisualizerUnity.CreateDomainPlane(_rasterPlane, _rasterRenderer, size, originOffset);
                _rasterPlaneSize = size;
                _rasterPlaneOffset = originOffset;
            }

            if (DomainVisualizerUnity.NeedNewTexture(new Vector2int(xPixels, yPixels), _rasterTexture))
            {
                if (_rasterTexture != null) Object.Destroy(_rasterTexture);
                _rasterTexture = new Texture2D(xPixels, yPixels, TextureFormat.RGBA32, false);
                _rasterTexture.filterMode = FilterMode.Point;
            }

            float range = max - min;
            var pixels = new Color32[xPixels * yPixels];
            for (int y = 0; y < yPixels; y++)
            {
                for (int x = 0; x < xPixels; x++)
                {
                    float v = data[x, y];
                    Color c = new Color(0f, 0f, 0f, 0f);
                    if (Valid(v, noData, ramp))
                    {
                        switch (ramp)
                        {
                            case RasterRamp.Probability:
                                float p = UnityEngine.Mathf.Clamp01(v);
                                c = Color.Lerp(new Color(1f, 0.9f, 0.2f), new Color(0.55f, 0f, 0f), p);
                                c.a = 0.35f + 0.5f * p;
                                break;
                            case RasterRamp.Arrival:
                                float t = range > 0f ? (v - min) / range : 0f;
                                c = Color.HSVToRGB(0.66f * t, 0.9f, 1f);
                                c.a = 0.7f;
                                break;
                            case RasterRamp.Boundary:
                                float band = (int)(UnityEngine.Mathf.Clamp01(range > 0f ? (v - min) / range : 1f) * 5f) / 5f;
                                c = Color.red * (0.8f * band + 0.2f);
                                c.a = 0.8f;
                                break;
                            default:
                                c = new Color(0.1f, 0.85f, 1f, 0.5f);
                                break;
                        }
                    }
                    pixels[x + y * xPixels] = c;
                }
            }

            _rasterTexture.SetPixels32(pixels);
            _rasterTexture.Apply();
            _rasterRenderer.material.mainTexture = _rasterTexture;
            _rasterPlane.SetActive(true);
            return true;
        }

        private static bool Valid(float v, double noData, RasterRamp ramp)
        {
            if (float.IsNaN(v) || float.IsInfinity(v) || UnityEngine.Mathf.Approximately(v, (float)noData) || v <= -9999f)
            {
                return false;
            }
            //Arrival times start at zero, which is a real value (ignition); everything else treats 0 as "none".
            return ramp == RasterRamp.Arrival ? v >= 0f : v > 0f;
        }

        public void SetLCPPlaneTexture(Texture2D tex)
        {
            if (tex == null)
            {
                return;
            }

            //No plane at all is the one thing that cannot be worked around, and it used to be reached by
            //returning silently above whenever no LCP had been loaded - so a scenario with only a DEM
            //painted correctly and showed nothing, with nothing said about why.
            if (_lcpDomainMeshRenderer == null)
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "There is no wildfire domain plane to show this on. Call EnsurePlane with the grid the "
                    + "texture is on first.");
                return;
            }

            //Checked against the LCP only when there is one. The painter's textures are on the grid it
            //resolved, which is the LCP's when a landscape is loaded and the DEM's or the imported arrival
            //times' when it is not, and in those cases there is no LCP size to compare against.
            if (_lcpData != null)
            {
                bool sameSize = _lcpData.GetCellCountX() == tex.width && _lcpData.GetCellCountY() == tex.height;
                if (!sameSize)
                {
                    Engine.Message(null, Engine.LogType.Warning, "Texture provided does not match the given LCP data size.");
                    return;
                }
            }

            _lcpDomainMeshRenderer.material.mainTexture = tex;
        }

        /// <summary>
        /// Makes sure there is a plane of the given extent, at the given simulation-space corner, to show a
        /// texture on. Used for painted textures, whose grid comes from the painter rather than from an
        /// LCP - the plane is otherwise only ever created when a landscape is loaded and displayed.
        /// </summary>
        public void EnsurePlane(Vector2d size, Vector2d originOffset)
        {
            if (_lcpDomainMeshRenderer == null || DomainVisualizerUnity.NeedNewPlane(_planeSize, size, _planeOffset, originOffset))
            {
                _lcpDomainMeshRenderer = DomainVisualizerUnity.CreateDomainPlane(_lcpDomainPlane, _lcpDomainMeshRenderer, size, originOffset);
                _planeSize = size;
                _planeOffset = originOffset;
            }
        }

        public override void SetLCPViewMode(LcpViewMode lcpViewMode)
        {
            if (_lcpData == null)
            {
                return;
            }

            if (lcpViewMode == LcpViewMode.FuelModel)
            {
                _lcpDomainMeshRenderer.material.mainTexture = _fuelModelsTexture;
            }
            else if (lcpViewMode == LcpViewMode.Elevation)
            {
                _lcpDomainMeshRenderer.material.mainTexture = _elevationTexture;
            }
            else if (lcpViewMode == LcpViewMode.Slope)
            {
                _lcpDomainMeshRenderer.material.mainTexture = _slopeTexture;
            }
            else if (lcpViewMode == LcpViewMode.Aspect)
            {
                _lcpDomainMeshRenderer.material.mainTexture = _aspectTexture;
            }
            else if (lcpViewMode == LcpViewMode.TriggerBuffer)
            {
                //Shown on the result overlay's own plane now (DisplayRaster).
                _rasterPlane.SetActive(true);
            }
        }
        private void CheckIfNeedNewFireDomainPlane(LandscapeData newLCPData)
        {
            if (_lcpData == null || DomainVisualizerUnity.NeedNewPlane(_lcpData.GetSize(), newLCPData.GetSize(), _lcpData.GetLowerLeftUTM(), newLCPData.GetLowerLeftUTM()))
            {
                _lcpDomainMeshRenderer = DomainVisualizerUnity.CreateDomainPlane(_lcpDomainPlane, _lcpDomainMeshRenderer, newLCPData.GetSize(), newLCPData.OriginOffset);
                //Recorded so EnsurePlane below can tell whether the plane already covers the grid a
                //painted texture is on, instead of rebuilding it on every paint.
                _planeSize = newLCPData.GetSize();
                _planeOffset = newLCPData.OriginOffset;
            }
        }

        public override void SetAndDisplayLCP(LandscapeData newLCPData, LcpViewMode lcpViewMode = LcpViewMode.FuelModel)
        {   
            int xDim = newLCPData.GetCellCountX();
            int yDim = newLCPData.GetCellCountY();

            if (DomainVisualizerUnity.NeedNewTexture(newLCPData.GetCellCount(), _fuelModelsTexture))
            {
                _fuelModelsTexture = new Texture2D(xDim, yDim, TextureFormat.RGBA32, false);
                _fuelModelsTexture.filterMode = FilterMode.Point;

                _elevationTexture = new Texture2D(xDim, yDim, TextureFormat.RGBA32, false);
                _elevationTexture.filterMode = FilterMode.Point;

                _slopeTexture = new Texture2D(xDim, yDim, TextureFormat.RGBA32, false);
                _slopeTexture.filterMode = FilterMode.Point;

                _aspectTexture = new Texture2D(xDim, yDim, TextureFormat.RGBA32, false);
                _aspectTexture.filterMode = FilterMode.Point;
            }

            CheckIfNeedNewFireDomainPlane(newLCPData);

            //set data
            _lcpData = newLCPData;           

            //update textures
            Vector2d elevationMinMax = _lcpData.GetElevationMinMax();
            Vector2d slopeMinMax = _lcpData.GetSlopeMinMax();
            Vector2d aspectMinMax = _lcpData.GetAspectMinMax();

            float elevationRange = (float)(elevationMinMax.y - elevationMinMax.x);
            float slopeRange = (float)(slopeMinMax.y - slopeMinMax.x);
            float aspectRange = (float)(aspectMinMax.y - aspectMinMax.x);
            float alpha = 0.85f;

            for (int y = 0; y < yDim; y++)
            {
                for (int x = 0; x < xDim; x++)
                {
                    LandscapeCellData l = _lcpData.GetCellDataSimulationIndex(x, y, false);

                    PREACTColor c = FuelModelColors.GetFuelColor((int)l.fuel_model);
                    c.a = alpha;
                    _fuelModelsTexture.SetPixel(x, y, c.UnityColor());

                    c = PREACTColor.white * ((l.elevation - (float)elevationMinMax.x) / elevationRange);
                    c.a = alpha;
                    _elevationTexture.SetPixel(x, y, c.UnityColor());

                    c = PREACTColor.white * ((l.slope - (float)slopeMinMax.x) / slopeRange);
                    c.a = alpha;
                    _slopeTexture.SetPixel(x, y, c.UnityColor());

                    c = PREACTColor.white * ((l.aspect - (float)aspectMinMax.x) / aspectRange);
                    c.a = alpha;
                    _aspectTexture.SetPixel(x, y, c.UnityColor());
                }
            }

            _fuelModelsTexture.Apply();
            _elevationTexture.Apply();
            _slopeTexture.Apply();
            _aspectTexture.Apply();
                        
            SetLCPViewMode(LcpViewMode.FuelModel);
            SetVisibility(true);
        }

        /// <summary>
        /// A trigger boundary on the loaded landscape's grid. What the Results window uses is
        /// <see cref="DisplayRaster"/>, which takes the grid explicitly; this keeps the engine's interface.
        /// </summary>
        public override void DisplayTriggerBuffer(float[,] data)
        {
            if(_lcpData == null)
            {
                Engine.Message(null, Engine.LogType.Warning, "Cannot display trigger buffer without specified Landscape data.");
                return;
            }

            bool sameSize = _lcpData.GetCellCountX() == data.GetLength(0) && _lcpData.GetCellCountY() == data.GetLength(1);
            if (!sameSize)
            {
                Engine.Message(null, Engine.LogType.Warning, "Trigger buffer provided does not match the given LCP data.");
                return;
            }

            DisplayRaster(data, -9999.0, _lcpData.GetSize(), _lcpData.OriginOffset, RasterRamp.Boundary, out float _, out float _);
        }

        public override void SetVisibility(bool visible)
        {
            _lcpDomainPlane.SetActive(visible);
        }

        public override bool ToggleVisibility()
        {
            _lcpDomainPlane.SetActive(!_lcpDomainPlane.activeSelf);
            return _lcpDomainPlane.activeSelf;
        }

        public override bool IsDataPlaneActive()
        {
            return _lcpDomainPlane.activeSelf;
        }
    }
}

