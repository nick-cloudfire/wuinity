//This file is part of WUIPlatform Copyright (C) 2024 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using UnityEngine;
using PREACT;
using PREACT.Math;
using ImGuiNET;

namespace WUInity
{
    public class Painter : MonoBehaviour
    {
        public enum PaintMode { WUIArea, RandomIgnitionArea, InitialIgnition, EvacGroup };
        PaintMode paintMode = PaintMode.WUIArea;

        Color currentColor = Color.red;
        const float transparency = 0.5f;
        Color activeAreaColor = new Color(1f, 0f, 0f, transparency);
        Color inactiveAreaColor = new Color(1f, 1f, 1f, 0.1f);
        Vector2int activeCellCount;
        Vector2d activeRealSize;
        Texture2D activeTexture;
        Color[] activeColorArray;
        private int _brushSize;

        //general evac stuff
        Vector2d evacDataRealSize;
        Vector2int evacDataCellCount;
        //evac groups
        Texture2D evacGroupTex;
        int evacGroupIndex;
        Color[] evacGroupColorArray;

        //Which group owns each cell, -1 for none. Painting a group writes its index here, so an area
        //can be reassigned from one group to another and every group's extent stays recoverable from
        //a single array rather than one mask per group held open at once.
        int[] _evacGroupCells;
        string[] _evacGroupNames = new string[0];
        Color[] _evacGroupColors = new Color[0];

        //general fire stuff
        Vector2d fireDataRealSize;
        Vector2int fireDataCellCount;
        PREACT.Wildfire.LandscapeData _lcpData;
        bool addingArea;

        //wui area stuff
        Texture2D wuiAreaTex;
        Color[] wuiAreaColorArray;   
        
        //random ignition area stuff
        Texture2D randomIgnitionTex;
        Color[] randomIgnitionColorArray;

        //initial ignition
        Texture2D initialIgnitionTex;
        Color[] initialIgnitionColorArray;

        //population mask painter
        Texture2D populationMaskTex;
        Color[] populationMaskColorArray;

        private Vector3 _offset;

        WUInityManager _manager;
        public void SetManager(WUInityManager manager)
        {
            _manager = manager;
        }

        public void SetLCPData(PREACT.Wildfire.LandscapeData lcpData)
        {
            _lcpData = lcpData;
        }

        public PaintMode GetPaintMode()
        {
            return paintMode;
        }

        public Texture2D GetEvacGroupTexture()
        {
            if (evacGroupTex == null)
            {
                CheckDataResources(evacGroupTex, evacGroupColorArray);
            }
            return evacGroupTex;
        }

        public Texture2D GetPopulationMaskTexture()
        {
            if (populationMaskTex == null)
            {
                Texture2D tex = (Texture2D)_manager.SimulationDomainVisualizer.GetPopulationMaskTexture();
                if (tex != null)
                {
                    populationMaskTex = tex;
                }
                else
                {
                    CheckDataResources(populationMaskTex, populationMaskColorArray);
                }                
            }
            return populationMaskTex;
        }

        public Texture2D GetWUIAreaTexture()
        {
            if (wuiAreaTex == null)
            {
                CheckDataResources(wuiAreaTex, wuiAreaColorArray);
            }
            return wuiAreaTex;
        }

        public Texture2D GetRandomIgnitionTexture()
        {
            if (randomIgnitionTex == null)
            {
                CheckDataResources(randomIgnitionTex, randomIgnitionColorArray);
            }
            return randomIgnitionTex;
        }

        public Texture2D GetInitialIgnitionTexture()
        {
            if (initialIgnitionTex == null)
            {
                CheckDataResources(initialIgnitionTex, initialIgnitionColorArray);
            }
            return initialIgnitionTex;
        }

        public void SetEvacGroupColor(int groupIndex)
        {
            SetColor(groupIndex);
        }

        public void SetWUIAreaColor(bool addArea)
        {
            SetColor(addArea ? 1 : 0);
        }

        public void SetRandomIgnitionAreaColor(bool addArea)
        {
            SetColor(addArea ? 1 : 0);
        }

        public void SetInitialIgnitionAreaColor(bool addArea)
        {
            SetColor(addArea ? 1 : 0);
        }

        public void SetTriggerBufferColor(bool addArea)
        {
            SetColor(addArea ? 1 : 0);
        }

        public void SetMaskGPWColor(bool addArea)
        {
            SetColor(addArea ? 1 : 0);
        }

        private void SetColor(int arrayIndex = 0)
        {
            if(paintMode == PaintMode.WUIArea || paintMode == PaintMode.RandomIgnitionArea || paintMode == PaintMode.InitialIgnition)
            {
                currentColor = arrayIndex == 1 ? activeAreaColor : inactiveAreaColor;
                addingArea = arrayIndex == 1;
            }
            else if (paintMode == PaintMode.EvacGroup)
            {
                //An index past the end means "erase", which is how a cell is taken out of every
                //group - there is no other way to unassign one.
                evacGroupIndex = arrayIndex;
                if (arrayIndex >= 0 && arrayIndex < _evacGroupColors.Length)
                {
                    currentColor = _evacGroupColors[arrayIndex];
                    currentColor.a = transparency;
                    addingArea = true;
                }
                else
                {
                    evacGroupIndex = -1;
                    currentColor = inactiveAreaColor;
                    addingArea = false;
                }
            }
        }

        public void SetPainterMode(PaintMode mode)
        {
            if (mode == PaintMode.WUIArea)
            {
                SetPainterWUIArea();
            }
            else if (mode == PaintMode.RandomIgnitionArea)
            {
                SetPainterRandomIgnition();
            }
            else if (mode == PaintMode.InitialIgnition)
            {
                SetPainterInitialIgnition();
            }
            else if (mode == PaintMode.EvacGroup)
            {
                SetPainterEvacGroup(evacGroupIndex);
            }
            else
            {
                Engine.Message(null, Engine.LogType.SimulationError, "Desired paint mode not yet implemented.");
            }
        }

        /// <summary>
        /// Tells the painter which groups exist, in the order their indices refer to. Called before
        /// painting so the painter can colour each group as the scenario defines it rather than
        /// inventing its own palette.
        /// </summary>
        public void SetEvacGroups(string[] names, Color[] colors)
        {
            _evacGroupNames = names ?? new string[0];
            _evacGroupColors = colors ?? new Color[0];
        }

        public string[] GetEvacGroupNames()
        {
            return _evacGroupNames;
        }

        /// <summary>
        /// Cell ownership as painted, indexed x + y*width with y running north, matching the WUI mask
        /// and k-PERIL. -1 means no group owns the cell.
        /// </summary>
        public int[] GetEvacGroupCells(out Vector2int cellCount)
        {
            cellCount = fireDataCellCount;
            return _evacGroupCells;
        }

        /// <summary>
        /// Writes one mask per painted group and returns their file names, or null if nothing has
        /// been painted.
        ///
        /// The header carries the fire grid's SIMULATION-space origin and cell size rather than a
        /// projected corner, which is the frame EvacuationGroup.LoadMask reads them back in. The two
        /// have to agree: a mask written in one frame and read in another lands the group somewhere
        /// else entirely, and nothing downstream would flag it.
        /// </summary>
        public string[] ExportEvacGroupMasks(string folder)
        {
            if (_evacGroupCells == null || _lcpData == null)
            {
                Engine.Message(null, Engine.LogType.Warning, "No painted evacuation groups to export.");
                return null;
            }

            int xCount = fireDataCellCount.x;
            int yCount = fireDataCellCount.y;

            var header = new PREACT.Utility.AscRaster.Header
            {
                Ncols = xCount,
                Nrows = yCount,
                XllCorner = _lcpData.OriginOffset.x,
                YllCorner = _lcpData.OriginOffset.y,
                CellSize = fireDataRealSize.x / xCount,
                NoDataValue = -9999.0
            };

            var written = new System.Collections.Generic.List<string>();

            for (int g = 0; g < _evacGroupNames.Length; ++g)
            {
                float[,] data = new float[xCount, yCount];
                int cells = 0;
                for (int y = 0; y < yCount; ++y)
                {
                    for (int x = 0; x < xCount; ++x)
                    {
                        if (_evacGroupCells[x + y * xCount] == g)
                        {
                            data[x, y] = 1f;
                            ++cells;
                        }
                    }
                }

                //A group with nothing painted gets no file, rather than one marking no cells - which
                //would load as a group nobody belongs to.
                if (cells == 0)
                {
                    Engine.Message(null, Engine.LogType.Warning, $"Evacuation group {_evacGroupNames[g]} has no painted cells; no mask written.");
                    written.Add(string.Empty);
                    continue;
                }

                string fileName = "evac_group_" + _evacGroupNames[g] + ".asc";
                PREACT.Utility.AscRaster.Write(data, header, System.IO.Path.Combine(folder, fileName));
                Engine.Message(null, Engine.LogType.Log, $"Evacuation group {_evacGroupNames[g]}: wrote {fileName} with {cells} cells.");
                written.Add(fileName);
            }

            return written.ToArray();
        }

        void SetPainterEvacGroup(int groupIndex)
        {
            if (_lcpData == null)
            {
                Engine.Message(null, Engine.LogType.Warning, "Painting an evacuation group needs the landscape loaded, since groups are painted on the fire grid.");
                return;
            }

            paintMode = PaintMode.EvacGroup;
            evacGroupIndex = groupIndex;
            CheckDataResources(evacGroupTex, evacGroupColorArray);
            SetColor(groupIndex);
            _brushSize = 5;
            _offset = new Vector3((float)_lcpData.OriginOffset.x, 0f, (float)_lcpData.OriginOffset.y);
        }

        void SetPainterWUIArea()
        {
            if(_lcpData == null)
            {
                return;
            }

            paintMode = PaintMode.WUIArea;
            CheckDataResources(wuiAreaTex, wuiAreaColorArray);
            SetWUIAreaColor(true);
            _brushSize = 5;
            _offset = new Vector3((float)_lcpData.OriginOffset.x, 0f, (float)_lcpData.OriginOffset.y);
        }

        void SetPainterRandomIgnition()
        {
            if (_lcpData == null)
            {
                return;
            }

            paintMode = PaintMode.RandomIgnitionArea;
            CheckDataResources(randomIgnitionTex, randomIgnitionColorArray);
            SetRandomIgnitionAreaColor(true);
            _brushSize = 5;
            _offset = new Vector3((float)_lcpData.OriginOffset.x, 0f, (float)_lcpData.OriginOffset.y);
        }
        void SetPainterInitialIgnition()
        {
            if (_lcpData == null)
            {
                return;
            }

            paintMode = PaintMode.InitialIgnition;
            CheckDataResources(initialIgnitionTex, initialIgnitionColorArray);
            SetInitialIgnitionAreaColor(true);
            _brushSize = 3;
            _offset = new Vector3((float)_lcpData.OriginOffset.x, 0f, (float)_lcpData.OriginOffset.y);
        }

        void CheckDataResources(Texture2D requestedTexture, Color[] requestedColorArray)
        {
            //Reported rather than dereferenced blindly: this used to throw a NullReferenceException
            //from the line below whenever it ran before a scenario was loaded, which says nothing
            //about what is actually missing.
            if (_manager == null || _manager.PREACTInput == null)
            {
                Engine.Message(null, Engine.LogType.Warning, "Painter has no scenario to paint on; load one first.");
                return;
            }

            if (requestedTexture == null)
            {
                Vector2int cellCount;
                //get correct size, fire mesh or evac mesh
                if(_manager.PREACTInput.WildfireModule.Data.LandscapeData != null)
                {
                    fireDataCellCount = _manager.PREACTInput.WildfireModule.Data.LandscapeData.GetCellCount();
                    cellCount = fireDataCellCount;
                    fireDataRealSize = _manager.PREACTInput.WildfireModule.Data.LandscapeData.GetSize();
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, "Painter is trying to access LCP data but it is not loaded.");
                    return;
                }
                //painter
                requestedColorArray = new Color[cellCount.x * cellCount.y];
                requestedTexture = new Texture2D(cellCount.x, cellCount.y);
                requestedTexture.filterMode = FilterMode.Point;
                for (int y = 0; y < cellCount.y; y++)
                {
                    for (int x = 0; x < cellCount.x; x++)
                    {
                        Color c = Color.white;
                        if (paintMode == PaintMode.WUIArea)
                        {
                            c = _manager.PREACTInput.WildfireModule.Data.WuiArea[x + y * fireDataCellCount.x] == false ? inactiveAreaColor : activeAreaColor;
                        }
                        else if (paintMode == PaintMode.RandomIgnitionArea)
                        {
                            c = _manager.PREACTInput.WildfireModule.Data.RandomIgnition[x + y * fireDataCellCount.x] == false ? inactiveAreaColor : activeAreaColor;
                        }
                        else if (paintMode == PaintMode.InitialIgnition)
                        {
                            c = _manager.PREACTInput.WildfireModule.Data.InitialIgnition[x + y * fireDataCellCount.x] == false ? inactiveAreaColor : activeAreaColor;
                        }
                        else if (paintMode == PaintMode.EvacGroup)
                        {
                            //Allocated on first use and kept, so switching between groups does not
                            //discard what has already been painted.
                            if (_evacGroupCells == null || _evacGroupCells.Length != cellCount.x * cellCount.y)
                            {
                                _evacGroupCells = new int[cellCount.x * cellCount.y];
                                for (int i = 0; i < _evacGroupCells.Length; ++i)
                                {
                                    _evacGroupCells[i] = -1;
                                }
                            }

                            int owner = _evacGroupCells[x + y * cellCount.x];
                            if (owner >= 0 && owner < _evacGroupColors.Length)
                            {
                                c = _evacGroupColors[owner];
                                c.a = transparency;
                            }
                            else
                            {
                                c = inactiveAreaColor;
                            }
                        }
                        requestedColorArray[x + y * cellCount.x] = c;
                        requestedTexture.SetPixel(x, y, c);
                    }
                }
                requestedTexture.Apply();              

                //fix references after created
                if (paintMode == PaintMode.WUIArea)
                {
                    wuiAreaTex = requestedTexture;
                    wuiAreaColorArray = requestedColorArray;
                }
                else if(paintMode == PaintMode.RandomIgnitionArea)
                {
                    randomIgnitionTex = requestedTexture;
                    randomIgnitionColorArray = requestedColorArray;
                }
                else if (paintMode == PaintMode.InitialIgnition)
                {
                    initialIgnitionTex = requestedTexture;
                    initialIgnitionColorArray = requestedColorArray;
                }
                else if (paintMode == PaintMode.EvacGroup)
                {
                    evacGroupTex = requestedTexture;
                    evacGroupColorArray = requestedColorArray;
                }
            }

            //EvacGroup belongs with the others: groups are painted on the fire grid, which is the
            //grid k-PERIL and the WUI mask use, so a painted group lines up with them cell for cell.
            //Falling through to the evac branch would have used a cell count of zero.
            if (paintMode == PaintMode.WUIArea || paintMode == PaintMode.RandomIgnitionArea
                || paintMode == PaintMode.InitialIgnition || paintMode == PaintMode.EvacGroup)
            {
                activeCellCount = fireDataCellCount;
                activeRealSize = fireDataRealSize;
            }
            else
            {
                activeCellCount = evacDataCellCount;
                activeRealSize = evacDataRealSize;
            }
            activeTexture = requestedTexture;
            activeColorArray = requestedColorArray;
        }      

        // Update is called once per frame
        void Update()
        {
            UpdatePainter();
        }

        void UpdatePainter()
        {
            //The brush follows the pointer wherever it is, so without this it paints through the
            //windows on top of the map - including through the button that ends the painting.
            if (ImGui.GetIO().WantCaptureMouse)
            {
                return;
            }

            if(Input.GetKeyDown(KeyCode.KeypadPlus))
            {
                ++_brushSize;
            }
            if (Input.GetKeyDown(KeyCode.KeypadMinus))
            {
                --_brushSize;
                if (_brushSize < 1)
                {
                    _brushSize = 1;
                }
            }
            if (Input.GetMouseButton(0) || Input.GetMouseButtonDown(1))
            {
                Plane _yPlane = new Plane(Vector3.up, 0f);
                Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                float enter = 0.0f;
                if (_yPlane.Raycast(ray, out enter))
                {
                    Vector3 hitPoint = ray.GetPoint(enter);
                    hitPoint -= _offset;
                    Vector2 pixelUV = new Vector2(activeTexture.width * hitPoint.x / (float)activeRealSize.x, activeTexture.height * hitPoint.z / (float)activeRealSize.y);

                    int x = (int)pixelUV.x;
                    int y = (int)pixelUV.y;
                    if (x < 0 || x > activeCellCount.x - 1 || y < 0 || y > activeCellCount.y - 1)
                    {
                        return;
                    }

                    //left click
                    if(Input.GetMouseButton(0))
                    {
                        PaintPixels(x, y);
                    }
                    //right click
                    else
                    {
                        Color colorToOverwrite = activeTexture.GetPixel(x, y);
                        FloodFill(new Vector2int(x, y), currentColor, colorToOverwrite, activeColorArray);
                        activeTexture.SetPixels(activeColorArray);
                        activeTexture.Apply();
                    }

                }
            }
        }    
        
        void PaintPixels(int x, int y)
        {
            if(_brushSize == 1)
            {
                activeTexture.SetPixel(x, y, currentColor);
                SetArrayPixel(x, y, currentColor, activeColorArray);
            }
            else
            {
                int minX = UnityEngine.Mathf.Max(0, x - _brushSize - 1);
                int maxX = UnityEngine.Mathf.Min(activeCellCount.x - 1, x + _brushSize - 1);
                int minY = UnityEngine.Mathf.Max(0, y - _brushSize - 1);
                int maxY = UnityEngine.Mathf.Min(activeCellCount.y - 1, y + _brushSize - 1);

                Vector2int center = new Vector2int(x, y);

                for (int j = minY; j <= maxY; j++)
                {
                    for (int i = minX; i <= maxX; i++)
                    {
                        float dist = Vector2int.Distance(center, new Vector2int(i, j));
                        if (dist <= _brushSize)
                        {
                            activeTexture.SetPixel(i, j, currentColor);
                            SetArrayPixel(i, j, currentColor, activeColorArray);
                        }
                    }
                }
            }            
            
            activeTexture.Apply();
        }

        Color GetArrayPixel(int x, int y, Color[] colorArray)
        {
            return colorArray[x + y * activeTexture.width];
        }

        void SetArrayPixel(int x, int y, Color c, Color[] colorArray)
        {
            if (x < 0 || x > activeCellCount.x || y < 0 || y > activeCellCount.y)
            {
                return;
            }
            colorArray[x + y * activeTexture.width] = c;

            if(paintMode == PaintMode.WUIArea)
            {
                _manager.PREACTInput.WildfireModule.Data.WuiArea[x + y * activeCellCount.x] = addingArea;
            }
            else if (paintMode == PaintMode.RandomIgnitionArea)
            {
                _manager.PREACTInput.WildfireModule.Data.RandomIgnition[x + y * activeCellCount.x] = addingArea;
            }
            else if (paintMode == PaintMode.InitialIgnition)
            {
                _manager.PREACTInput.WildfireModule.Data.InitialIgnition[x + y * activeCellCount.x] = addingArea;
            }
            else if (paintMode == PaintMode.EvacGroup && _evacGroupCells != null)
            {
                //Ownership is exclusive: painting a cell for one group takes it from whichever group
                //held it, so no cell can end up in two groups.
                _evacGroupCells[x + y * activeCellCount.x] = evacGroupIndex;
            }
        }

        private void FloodFill(Vector2int startPixel, Color wantedColor, Color colorToOverwrite, Color[] colorArray)
        {   
            System.Collections.Generic.Stack<Vector2int> queue = new System.Collections.Generic.Stack<Vector2int>();
            queue.Push(startPixel);
            int maxPixels = colorArray.Length;
            while (queue.Count > 0)
            {
                Vector2int pixel = queue.Pop();
                SetArrayPixel(pixel.x, pixel.y, wantedColor, colorArray);

                Vector2int right = pixel + Vector2int.right;
                Vector2int left = pixel + Vector2int.left;
                Vector2int up = pixel + Vector2int.up;
                Vector2int down = pixel + Vector2int.down;

                // then we can either go east
                if (IncludePixel(right, wantedColor, colorToOverwrite, colorArray))
                {
                    queue.Push(right);
                }
                // west
                if (IncludePixel(left, wantedColor, colorToOverwrite, colorArray))
                {
                    queue.Push(left);
                }
                //north
                if (IncludePixel(up, wantedColor, colorToOverwrite, colorArray))
                {
                    queue.Push(up);
                }
                //south
                if (IncludePixel(down, wantedColor, colorToOverwrite, colorArray))
                {
                    queue.Push(down);
                }

                --maxPixels;
                if(maxPixels < 0)
                {
                    break;
                }
            }            
        }

        private bool IncludePixel(Vector2int pixelIndex, Color wantedColor, Color colorToOverwrite, Color[] colorArray)
        {
            //outside of texture
            if (pixelIndex.x < 0 || pixelIndex.x > (activeTexture.width - 1) || pixelIndex.y < 0 || pixelIndex.y > (activeTexture.height - 1))
            {
                return false;
            }

            Color currentColor = GetArrayPixel(pixelIndex.x, pixelIndex.y, colorArray);

            //already the same color in pixel
            if (UnityEngine.Mathf.Approximately(currentColor.r, wantedColor.r) && UnityEngine.Mathf.Approximately(currentColor.g, wantedColor.g)
                && UnityEngine.Mathf.Approximately(currentColor.b, wantedColor.b))
            {
                return false;
            }

            //not color we want to overwrite
            if (!UnityEngine.Mathf.Approximately(currentColor.r, colorToOverwrite.r) && !UnityEngine.Mathf.Approximately(currentColor.g, colorToOverwrite.g)
                && !UnityEngine.Mathf.Approximately(currentColor.b, colorToOverwrite.b))
            {
                return false;
            }

            return true;
        }
    }
}