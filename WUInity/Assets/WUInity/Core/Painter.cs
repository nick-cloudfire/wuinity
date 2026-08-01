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
        //Kept separately from the paint texture, since it is what stays on the map after painting stops.
        Texture2D _evacGroupOverlayTex;

        //general fire stuff
        Vector2d fireDataRealSize;
        Vector2int fireDataCellCount;
        PREACT.Wildfire.LandscapeData _lcpData;
        bool addingArea;

        //The fire grid's lower-left corner in simulation coordinates, and its cell size. Held here
        //rather than read from the LCP at each use, because an AscImport scenario has no LCP at all -
        //WildfireData skips loading one for it by design - and the grid then comes from the imported
        //arrival time raster instead.
        Vector2d _fireGridOrigin;
        double _fireGridCellSize;
        bool _haveFireGrid;

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

        /// <summary>
        /// Whether the painter actually has a grid and a texture to work on. Callers that offer a
        /// "start painting" control need this: setting a mode can fail - no scenario, no fire grid -
        /// and the failure is a logged warning, not an exception, so it is otherwise invisible and the
        /// brush simply does nothing.
        /// </summary>
        public bool CanPaint { get => activeTexture != null && activeColorArray != null && activeCellCount.x > 0; }

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

        /// <summary>
        /// Establishes the grid every paint mode works on: cell count, extent, the lower-left corner in
        /// simulation coordinates and the cell size.
        ///
        /// The landscape is used when there is one. When there is not, the imported arrival time raster
        /// is - and that is not a fallback but the normal case for the module the trigger pipeline uses:
        /// WildfireData deliberately skips loading an LCP for AscImport, so every paint mode used to
        /// refuse outright with "LCP data is not loaded" for exactly the scenarios that need a WUI area
        /// and evacuation groups painted. That raster is also the grid k-PERIL computes on, so a mask
        /// painted against it lines up with the boundary cell for cell, which is what matters.
        /// </summary>
        private bool ResolveFireGrid()
        {
            if (_manager == null || _manager.PREACTInput == null)
            {
                Engine.Message(null, Engine.LogType.Warning, "The painter has no scenario to paint on; load one first.");
                return false;
            }

            PREACT.Input.PREACTInput input = _manager.PREACTInput;

            if (_lcpData != null)
            {
                fireDataCellCount = _lcpData.GetCellCount();
                fireDataRealSize = _lcpData.GetSize();
                _fireGridOrigin = _lcpData.OriginOffset;
                _fireGridCellSize = fireDataCellCount.x > 0 ? fireDataRealSize.x / fireDataCellCount.x : 0.0;
                _haveFireGrid = _fireGridCellSize > 0.0;
                return _haveFireGrid;
            }

            //Any georeferenced raster on the domain will do, in this order of preference:
            //
            //  1. the imported fire's arrival times - the grid the fire is on and k-PERIL computes on,
            //     so a mask painted against it needs no reconciling at all;
            //  2. the landscape, whatever of it exists;
            //  3. the elevation on its own - a DEM, which can be had for anywhere on Earth and is
            //     therefore the one thing a scenario outside LANDFIRE coverage can always have.
            //
            //Nothing is invented when none of them is there. A grid made up from the domain and an
            //arbitrary cell size would let painting proceed and produce masks that line up with nothing,
            //which is worse than not painting: the misalignment would only surface as a trigger boundary
            //in the wrong place, with no error anywhere.
            string reference = string.Empty;
            string what = string.Empty;

            if (input.WildfireModule.Module == PREACT.Input.WildfireModuleInput.WildfireModules.AscImport
                && !string.IsNullOrEmpty(input.WildfireModule.AscImportInput.TimeOfArrivalFile))
            {
                reference = input.WildfireModule.AscImportInput.TimeOfArrivalFile;
                what = "the imported fire's arrival times";
            }
            else if (input.Landscape != null && !string.IsNullOrEmpty(input.Landscape.GetReferenceFile()))
            {
                reference = input.Landscape.GetReferenceFile();
                what = reference == input.Landscape.ElevationFile ? "the elevation raster" : "the landscape";
            }

            if (string.IsNullOrEmpty(reference))
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "There is nothing to paint on. Painting needs a raster that defines the cells and where they are: "
                    + "an imported fire's time of arrival, a landscape, or just an elevation raster - a DEM is enough, "
                    + "and one can be downloaded for anywhere. Add one under the Landscape section.");
                return false;
            }

            //A .lcp holds no georeferencing this can read, but LandscapeData does read one - so it would
            //have been caught by the branch above if it had loaded.
            if (reference.ToLowerInvariant().EndsWith(".lcp"))
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "The landscape file could not be loaded, so there is no grid to paint on.");
                return false;
            }

            string path = System.IO.Path.Combine(input.RootFolder, reference);
            PREACT.Utility.AscRaster.Header header = PREACT.Utility.AscRaster.ReadHeader(path, out bool ok);
            if (!ok)
            {
                Engine.Message(null, Engine.LogType.Warning, "Could not read a cell grid from " + path + ".");
                return false;
            }

            fireDataCellCount = new Vector2int(header.Ncols, header.Nrows);
            fireDataRealSize = new Vector2d(header.Ncols * header.CellSize, header.Nrows * header.CellSize);
            //Simulation coordinates, as OriginOffset is: the raster's corner measured from the
            //simulation's own UTM origin. Mixing the two frames puts everything painted somewhere else.
            _fireGridOrigin = new Vector2d(header.XllCorner, header.YllCorner) - input.Simulation.Data.UTMOrigin;
            _fireGridCellSize = header.CellSize;
            _haveFireGrid = true;

            Engine.Message(null, Engine.LogType.Log,
                $"Painting on the grid of {what}: {header.Ncols} x {header.Nrows} cells of {header.CellSize:F1} m.");

            //A fire grid that does not reach the domain at all cannot be painted on usefully - the
            //brush would be somewhere off-screen - and the cause is always the same: the raster and the
            //simulation origin are in different UTM zones, so subtracting their eastings is meaningless.
            //Said here because the offset is otherwise invisible, and the same subtraction is what
            //AscFireImport places the fire itself with, so the fire is displaced by just as much.
            Vector2d domain = input.Simulation.DomainSize;
            bool overlaps = _fireGridOrigin.x < domain.x && _fireGridOrigin.y < domain.y
                            && _fireGridOrigin.x + fireDataRealSize.x > 0.0
                            && _fireGridOrigin.y + fireDataRealSize.y > 0.0;
            if (!overlaps)
            {
                Engine.Message(null, Engine.LogType.Warning,
                    $"The fire grid does not overlap the simulation domain: its corner is {_fireGridOrigin.x:F0}, {_fireGridOrigin.y:F0} m "
                    + $"from the origin, for a domain of {domain.x:F0} x {domain.y:F0} m. The raster's easting "
                    + $"({header.XllCorner:F0}) and the simulation's UTM origin ({input.Simulation.Data.UTMOrigin.x:F0}) "
                    + "are almost certainly in different UTM zones, which makes the difference between them meaningless. "
                    + "The fire itself is placed the same way, so it is displaced by the same amount.");
            }

            return true;
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
            if (_evacGroupCells == null || !_haveFireGrid)
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
                XllCorner = _fireGridOrigin.x,
                YllCorner = _fireGridOrigin.y,
                CellSize = _fireGridCellSize,
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

        /// <summary>
        /// A view of the painted groups to leave on the map once painting has stopped: each group in its own
        /// colour at the given opacity, and every unowned cell fully transparent so the map shows through.
        ///
        /// Built from group ownership rather than handed out as the paint texture, which is a different
        /// thing: that one marks unpainted cells with a visible "nothing here" grey, which is right while
        /// painting - it shows the grid being painted on - and wrong for something meant to sit quietly over
        /// the map afterwards. Returns null when nothing has been painted.
        /// </summary>
        public Texture2D BuildEvacGroupOverlayTexture(float opacity)
        {
            if (_evacGroupCells == null || !_haveFireGrid || fireDataCellCount.x <= 0)
            {
                return null;
            }

            int xCount = fireDataCellCount.x;
            int yCount = fireDataCellCount.y;
            if (_evacGroupCells.Length != xCount * yCount)
            {
                return null;
            }

            if (Visualization.DomainVisualizerUnity.NeedNewTexture(fireDataCellCount, _evacGroupOverlayTex))
            {
                _evacGroupOverlayTex = new Texture2D(xCount, yCount);
                _evacGroupOverlayTex.filterMode = FilterMode.Point;
            }

            Color clear = new Color(0f, 0f, 0f, 0f);
            bool anyPainted = false;

            for (int y = 0; y < yCount; ++y)
            {
                for (int x = 0; x < xCount; ++x)
                {
                    int owner = _evacGroupCells[x + y * xCount];
                    Color c = clear;
                    if (owner >= 0 && owner < _evacGroupColors.Length)
                    {
                        c = _evacGroupColors[owner];
                        c.a = opacity;
                        anyPainted = true;
                    }
                    _evacGroupOverlayTex.SetPixel(x, y, c);
                }
            }
            _evacGroupOverlayTex.Apply();

            return anyPainted ? _evacGroupOverlayTex : null;
        }

        void SetPainterEvacGroup(int groupIndex)
        {
            if (!ResolveFireGrid())
            {
                return;
            }

            paintMode = PaintMode.EvacGroup;
            evacGroupIndex = groupIndex;
            CheckDataResources(evacGroupTex, evacGroupColorArray);
            SetColor(groupIndex);
            _brushSize = 5;
            _offset = FireGridOffset();
        }

        void SetPainterWUIArea()
        {
            if (!ResolveFireGrid())
            {
                return;
            }

            paintMode = PaintMode.WUIArea;
            CheckDataResources(wuiAreaTex, wuiAreaColorArray);
            SetWUIAreaColor(true);
            _brushSize = 5;
            _offset = FireGridOffset();
        }

        void SetPainterRandomIgnition()
        {
            if (!ResolveFireGrid())
            {
                return;
            }

            paintMode = PaintMode.RandomIgnitionArea;
            CheckDataResources(randomIgnitionTex, randomIgnitionColorArray);
            SetRandomIgnitionAreaColor(true);
            _brushSize = 5;
            _offset = FireGridOffset();
        }
        void SetPainterInitialIgnition()
        {
            if (!ResolveFireGrid())
            {
                return;
            }

            paintMode = PaintMode.InitialIgnition;
            CheckDataResources(initialIgnitionTex, initialIgnitionColorArray);
            SetInitialIgnitionAreaColor(true);
            _brushSize = 3;
            _offset = FireGridOffset();
        }

        /// <summary>
        /// The grid every painted texture is built on, in the frame the scene draws in: extent in metres
        /// and the lower-left corner in simulation coordinates.
        ///
        /// Needed by whatever is going to show a painted texture, because the plane it goes on has to be
        /// the same rectangle the texture describes. Resolves the grid if it has not been resolved yet, so
        /// asking for it is enough - the caller does not have to have started painting first.
        /// </summary>
        public bool TryGetPaintGrid(out Vector2d realSize, out Vector2d originOffset)
        {
            return TryGetPaintGrid(out realSize, out originOffset, out Vector2int _, out double _);
        }

        /// <summary>
        /// The paint grid in full: extent, corner, cell count and cell size, all in simulation
        /// coordinates. Wanted by anything that has to place a single point on the grid rather than draw
        /// the whole of it - which cell it falls in is a question only the cell size can answer.
        /// </summary>
        public bool TryGetPaintGrid(out Vector2d realSize, out Vector2d originOffset,
            out Vector2int cellCount, out double cellSize)
        {
            if (!_haveFireGrid && !ResolveFireGrid())
            {
                realSize = Vector2d.zero;
                originOffset = Vector2d.zero;
                cellCount = new Vector2int(0, 0);
                cellSize = 0.0;
                return false;
            }

            realSize = fireDataRealSize;
            originOffset = _fireGridOrigin;
            cellCount = fireDataCellCount;
            cellSize = _fireGridCellSize;
            return true;
        }

        /// <summary>
        /// Writes the painted fire areas beside the scenario and returns the file name, or null when
        /// there is nothing to write.
        ///
        /// The grid written into the file is the one the masks were painted on, taken from the painter
        /// rather than from the landscape: a scenario with an imported fire has no landscape at all, and
        /// its masks are painted on the arrival time raster's grid.
        /// </summary>
        public string SavePaintedFireAreas(string folder, string fileName = null)
        {
            if (_manager == null || _manager.PREACTInput == null)
            {
                Engine.Message(null, Engine.LogType.Warning, "No scenario to save painted fire areas for.");
                return null;
            }

            if (!_haveFireGrid && !ResolveFireGrid())
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "The painted fire areas cannot be saved without knowing the grid they are on.");
                return null;
            }

            PREACT.Input.WildfireData fireData = _manager.PREACTInput.WildfireModule.Data;
            int cells = fireDataCellCount.x * fireDataCellCount.y;

            if (!AnyPainted(fireData.WuiArea, cells) && !AnyPainted(fireData.RandomIgnition, cells)
                && !AnyPainted(fireData.InitialIgnition, cells) && !AnyPainted(fireData.ManualTriggerBuffer, cells))
            {
                //Refused rather than written empty, because an empty file is not the same as no file: the
                //case builder reads an all-false ignition area as "not painted" and falls back to
                //ignite-anywhere, so a file saying nothing looks exactly like a file that was never saved
                //while making the checklist claim the scenario has painted areas.
                Engine.Message(null, Engine.LogType.Warning,
                    "Nothing has been painted, so there is nothing to save. Paint a WUI area, an ignition area "
                    + "or an initial ignition first.");
                return null;
            }

            string name = string.IsNullOrEmpty(fileName) ? GraphicalFireInput.DefaultFileName : fileName;
            string path = System.IO.Path.Combine(folder, name);

            GraphicalFireInput.SaveGraphicalFireInput(path, fireData, fireDataCellCount.x, fireDataCellCount.y);
            fireData.PaintedCellCount = fireDataCellCount;

            Engine.Message(null, Engine.LogType.Log,
                $"Wrote {name}: {Count(fireData.WuiArea)} WUI cells, {Count(fireData.RandomIgnition)} ignition area "
                + $"cells, {Count(fireData.InitialIgnition)} initial ignition cells on a "
                + $"{fireDataCellCount.x} x {fireDataCellCount.y} grid of {_fireGridCellSize:F1} m.");

            return name;
        }

        private static bool AnyPainted(bool[] mask, int cells)
        {
            if (mask == null)
            {
                return false;
            }

            for (int i = 0; i < mask.Length && i < cells; ++i)
            {
                if (mask[i]) return true;
            }
            return false;
        }

        private static int Count(bool[] mask)
        {
            if (mask == null)
            {
                return 0;
            }

            int n = 0;
            for (int i = 0; i < mask.Length; ++i)
            {
                if (mask[i]) ++n;
            }
            return n;
        }

        /// <summary>Where the fire grid sits in the scene, which is its simulation-space corner.</summary>
        private Vector3 FireGridOffset()
        {
            return new Vector3((float)_fireGridOrigin.x, 0f, (float)_fireGridOrigin.y);
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
                //The grid comes from the landscape when there is one and from the imported arrival time
                //raster when there is not, which is the case for the module the trigger pipeline uses.
                if (!_haveFireGrid && !ResolveFireGrid())
                {
                    return;
                }
                Vector2int cellCount = fireDataCellCount;

                //Allocated here when absent rather than assumed: these hold what has been painted and
                //are only filled in by a graphical fire input file, which most scenarios do not have.
                PREACT.Input.WildfireData fireData = _manager.PREACTInput.WildfireModule.Data;
                int cells = cellCount.x * cellCount.y;

                //Masks that were painted on a different grid cannot be carried onto this one, and the
                //reallocation below silently discards them. Said out loud, because the user sees a blank
                //map for a scenario that does have painted areas, and the cause is not on screen: the
                //grid changed under them - a landscape was added, or the imported fire was replaced.
                if (fireData.PaintedCellCount.x > 0
                    && (fireData.PaintedCellCount.x != cellCount.x || fireData.PaintedCellCount.y != cellCount.y))
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        $"The saved fire areas were painted on a {fireData.PaintedCellCount.x} x "
                        + $"{fireData.PaintedCellCount.y} grid, but this scenario now paints on a "
                        + $"{cellCount.x} x {cellCount.y} one, so they cannot be shown or added to. They are still "
                        + "on disk; painting and saving again replaces them.");
                    fireData.PaintedCellCount = new Vector2int(0, 0);
                }

                if (fireData.WuiArea == null || fireData.WuiArea.Length != cells)
                {
                    fireData.UpdateWUIArea(null, cellCount.x, cellCount.y);
                }
                if (fireData.RandomIgnition == null || fireData.RandomIgnition.Length != cells)
                {
                    fireData.UpdateRandomIgnitionIndices(null, cellCount.x, cellCount.y);
                }
                if (fireData.InitialIgnition == null || fireData.InitialIgnition.Length != cells)
                {
                    fireData.UpdateInitialIgnitionIndices(null, cellCount.x, cellCount.y);
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

            //Nothing to paint on. CheckDataResources leaves these null whenever it could not work out
            //the grid, and this ran anyway on the next click - a NullReferenceException per frame the
            //button was held, with the actual reason logged once, far above, and easily missed.
            if (activeTexture == null || activeColorArray == null || activeCellCount.x <= 0)
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
                        //The colour to spread over is taken from the colour array, not from the texture.
                        //A Texture2D stores 8 bits per channel, so a colour read back out of it is the
                        //quantised version of the one that was written - 0.5 comes back as 0.50196 - and
                        //IncludePixel compares it against the array's exact floats with
                        //Mathf.Approximately, whose tolerance is far tighter than one 8-bit step. Every
                        //neighbour therefore failed the "is this the colour we are overwriting" test and
                        //the fill stopped on the cell that was clicked.
                        Color colorToOverwrite = GetArrayPixel(x, y, activeColorArray);
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
            //Bounds are exclusive: a cell at activeCellCount.x is not the last column, it is the first
            //column of the row above, and writing it there is what the index below would have done.
            if (x < 0 || x >= activeCellCount.x || y < 0 || y >= activeCellCount.y)
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
            //Counted down on cells actually filled, not on pops. A cell can be pushed by more than one of
            //its neighbours before it is reached, so popping is not the same as filling, and budgeting by
            //pops cut large fills off partway.
            int maxPixels = colorArray.Length;
            while (queue.Count > 0)
            {
                Vector2int pixel = queue.Pop();

                //Already done, having been queued twice.
                if (SameColor(GetArrayPixel(pixel.x, pixel.y, colorArray), wantedColor))
                {
                    continue;
                }

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

            //already the same color in pixel - and the test the fill terminates on, since the cell it
            //started from was painted before its neighbours were queued
            if (SameColor(currentColor, wantedColor))
            {
                return false;
            }

            //not color we want to overwrite
            return SameColor(currentColor, colorToOverwrite);
        }

        /// <summary>
        /// Whether two painted colours are the same one, to within an 8-bit channel step.
        ///
        /// Not Mathf.Approximately per channel: these colours make round trips through a Texture2D, which
        /// holds 8 bits per channel, so the same colour differs by up to 1/255 depending on which side it
        /// was read from. Alpha counts as well as RGB - erasing differs from an unpainted cell only by its
        /// alpha in some modes.
        /// </summary>
        private static bool SameColor(Color a, Color b)
        {
            const float step = 1.5f / 255f;
            return UnityEngine.Mathf.Abs(a.r - b.r) < step
                   && UnityEngine.Mathf.Abs(a.g - b.g) < step
                   && UnityEngine.Mathf.Abs(a.b - b.b) < step
                   && UnityEngine.Mathf.Abs(a.a - b.a) < step;
        }
    }
}