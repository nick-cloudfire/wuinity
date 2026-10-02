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
        /// <summary>
        /// What the brush paints: the ignition area a campaign draws its fires from, or the evacuation groups - whose union
        /// is the WUI area k-PERIL protects. A separately painted WUI area and a painted initial ignition are gone: the
        /// first duplicated the groups, the second an ignition point.
        /// </summary>
        public enum PaintMode { RandomIgnitionArea, EvacGroup };
        PaintMode paintMode = PaintMode.RandomIgnitionArea;

        Color currentColor = Color.red;
        const float transparency = 0.5f;
        Color activeAreaColor = new Color(1f, 0f, 0f, transparency);
        Color inactiveAreaColor = new Color(1f, 1f, 1f, 0.1f);
        Vector2int activeCellCount;
        Vector2d activeRealSize;
        Texture2D activeTexture;
        Color[] activeColorArray;
        private int _brushSize;

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

        //Where that grid lies in its own CRS, as every saved painting records it (GraphicalFireInput's trailer), read
        //from the raster the grid came from the way the case builder and the resampler read theirs, so all three
        //compare the same numbers. Null when the grid's raster could not be read that way; the painting is then
        //saved without the record and matched by its size, as every painting was before.
        GraphicalFireInput.PaintedGrid _fireGridRecord;

        //random ignition area stuff
        Texture2D randomIgnitionTex;
        Color[] randomIgnitionColorArray;

        //Which raster the grid above was read from (scenario-relative), what to call it in messages, and -
        //when there is none - why not. Kept so a change of fire module, case folder or imported arrival
        //times is noticed: the grid used to be resolved once per session and never again, so after loading
        //a second scenario the painter wrote scenario B's masks with scenario A's cell count.
        string _gridReference;
        string _gridDescription;
        string _gridProblem;

        //The grid problem last said in the console. Every way into the painter resolves the grid - a mode, a texture, the
        //plane it is shown on - so one click said "Build the fire case first" three or four times.
        string _gridProblemSaid;

        //Group masks already read from the scenario's MaskFiles for the current grid.
        bool _evacGroupMasksLoaded;

        /// <summary>Fire-area strokes made since the areas were last saved or loaded.</summary>
        public bool UnsavedFireStrokes { get; private set; }

        /// <summary>Evacuation-group strokes made since the group masks were last saved or loaded.</summary>
        public bool UnsavedGroupStrokes { get; private set; }

        /// <summary>A one-line description of the paint grid, or of why there is none.</summary>
        public string GridDescription
        {
            get
            {
                if (_haveFireGrid)
                {
                    return $"{_gridDescription}: {fireDataCellCount.x} x {fireDataCellCount.y} cells of {_fireGridCellSize:F1} m";
                }
                return string.IsNullOrEmpty(_gridProblem) ? "No paint grid yet." : _gridProblem;
            }
        }

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

        public Texture2D GetRandomIgnitionTexture()
        {
            if (randomIgnitionTex == null)
            {
                CheckDataResources(randomIgnitionTex, randomIgnitionColorArray);
            }
            return randomIgnitionTex;
        }

        public void SetEvacGroupColor(int groupIndex)
        {
            SetColor(groupIndex);
        }

        public void SetRandomIgnitionAreaColor(bool addArea)
        {
            SetColor(addArea ? 1 : 0);
        }

        private void SetColor(int arrayIndex = 0)
        {
            if (paintMode == PaintMode.RandomIgnitionArea)
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
        /// The ELMFIRE case's DEM, relative to the scenario folder: the grid of record for an ELMFIRE scenario.
        /// </summary>
        public static string ElmfireGridReference(PREACT.Input.PREACTInput input)
        {
            //The workflow's own spelling of it, so the grid the painter names is the grid the workflow compares.
            return global::WUInity.Workflow.ScenarioFiles.CaseInput(input, "dem.tif");
        }

        /// <summary>
        /// The raster the paint grid has to come from for this scenario as it stands, or null when the
        /// scenario names none. Only a path - nothing is read.
        /// </summary>
        /// <remarks>
        /// One grid of record per fire module, and no fallbacks between them:
        ///
        ///   ELMFIRE   - the case's <c>inputs/dem.tif</c>. ELMFIRE reads its domain, CRS and cell size from
        ///               that file, so the fire, its outputs, the masks the case builder writes and k-PERIL are
        ///               all on it. Nothing else will do: a mask painted on the scenario's own DEM (Mati:
        ///               616 x 590 at 27.6 m, against the case's 566 x 541 at 30 m) cannot be applied to the
        ///               case, and k-PERIL refuses it at run time.
        ///   AscImport - the imported arrival times, which is the grid the fire is read on.
        ///   none      - the landscape, for a scenario with no fire that still wants areas painted.
        ///
        /// The old order put <c>AscImportInput.TimeOfArrivalFile</c> first whatever the module - and a run
        /// used to fill that in for an ELMFIRE fire, so what the painter used depended on whether a run had
        /// happened yet this session.
        /// </remarks>
        public static string ExpectedGridReference(PREACT.Input.PREACTInput input)
        {
            if (input == null || input.WildfireModule == null)
            {
                return null;
            }

            switch (input.WildfireModule.Module)
            {
                case PREACT.Input.WildfireModuleInput.WildfireModules.ELMFIRE:
                    //Contract C2: the case build converts painted masks against the case grid and fails otherwise;
                    //painting only ever on dem.tif is the GUI's half of that.
                    return ElmfireGridReference(input);

                case PREACT.Input.WildfireModuleInput.WildfireModules.AscImport:
                    string toa = input.WildfireModule.AscImportInput?.TimeOfArrivalFile;
                    return string.IsNullOrEmpty(toa) ? null : toa.Replace('\\', '/');

                default:
                    return input.Landscape != null && !string.IsNullOrEmpty(input.Landscape.GetReferenceFile())
                        ? input.Landscape.GetReferenceFile().Replace('\\', '/')
                        : null;
            }
        }

        /// <summary>
        /// Forgets the grid when the scenario now names a different raster than the one it came from - a
        /// module switched, a case folder renamed, an imported fire replaced.
        /// </summary>
        private void DropGridIfStale()
        {
            if (!_haveFireGrid || _manager == null || _manager.PREACTInput == null)
            {
                return;
            }

            string expected = ExpectedGridReference(_manager.PREACTInput) ?? "(landscape)";
            if (expected != _gridReference)
            {
                Engine.Message(null, Engine.LogType.Log, "The paint grid is now " + expected + " rather than "
                    + _gridReference + "; painting goes on on the new grid.");
                ResetGrid();
            }
        }

        private bool ResolveFireGrid()
        {
            if (_manager == null || _manager.PREACTInput == null)
            {
                ReportGridProblem("Load a scenario first: the painter has no scenario to paint on.");
                return false;
            }

            PREACT.Input.PREACTInput input = _manager.PREACTInput;
            PREACT.Input.WildfireModuleInput.WildfireModules module = input.WildfireModule.Module;

            //Nothing is invented when the raster is not there. A grid made up from the domain and an
            //arbitrary cell size would let painting proceed and produce masks that line up with nothing,
            //which is worse than not painting: the misalignment would only surface as a trigger boundary in
            //the wrong place, with no error anywhere.
            string reference = ExpectedGridReference(input);
            string what;

            if (module == PREACT.Input.WildfireModuleInput.WildfireModules.ELMFIRE)
            {
                what = "the fire case's DEM";
                if (!System.IO.File.Exists(System.IO.Path.Combine(input.RootFolder, reference)))
                {
                    ReportGridProblem("Build the fire case first (workflow step 5): an ELMFIRE scenario is painted on "
                        + reference + ", the grid the fire, the case's masks and k-PERIL share, and it does not "
                        + "exist yet.");
                    return false;
                }
            }
            else if (module == PREACT.Input.WildfireModuleInput.WildfireModules.AscImport)
            {
                what = "the imported fire's arrival times";
                if (string.IsNullOrEmpty(reference))
                {
                    ReportGridProblem("Set the imported fire's TimeOfArrivalFile first: an imported fire is painted on its grid.");
                    return false;
                }
            }
            else if (_lcpData != null)
            {
                fireDataCellCount = _lcpData.GetCellCount();
                fireDataRealSize = _lcpData.GetSize();
                _fireGridOrigin = _lcpData.OriginOffset;
                _fireGridCellSize = fireDataCellCount.x > 0 ? fireDataRealSize.x / fireDataCellCount.x : 0.0;
                _haveFireGrid = _fireGridCellSize > 0.0;
                _fireGridRecord = RecordOfLandscape(input);
                _gridReference = "(landscape)";
                _gridDescription = "the loaded landscape";
                _evacGroupMasksLoaded = false;
                if (_haveFireGrid) _gridProblemSaid = null;
                return _haveFireGrid;
            }
            else
            {
                what = "the landscape";
                if (string.IsNullOrEmpty(reference))
                {
                    ReportGridProblem("There is nothing to paint on. With no fire module, painting needs the scenario's "
                        + "landscape or elevation raster to define the cells.");
                    return false;
                }
            }

            //A .lcp holds no georeferencing this can read, but LandscapeData does read one - so it would
            //have been caught by the branch above if it had loaded.
            if (reference.ToLowerInvariant().EndsWith(".lcp"))
            {
                ReportGridProblem("The landscape file could not be loaded, so there is no grid to paint on.");
                return false;
            }

            string path = System.IO.Path.IsPathRooted(reference) ? reference : System.IO.Path.Combine(input.RootFolder, reference);
            PREACT.Utility.AscRaster.Header header = PREACT.Utility.AscRaster.ReadHeader(path, out bool ok);
            if (!ok)
            {
                ReportGridProblem("Could not read a cell grid from " + path + ".");
                return false;
            }

            fireDataCellCount = new Vector2int(header.Ncols, header.Nrows);
            fireDataRealSize = new Vector2d(header.Ncols * header.CellSize, header.Nrows * header.CellSize);
            //Simulation coordinates, as OriginOffset is: the raster's corner measured from the
            //simulation's own UTM origin. Mixing the two frames puts everything painted somewhere else.
            _fireGridOrigin = new Vector2d(header.XllCorner, header.YllCorner) - input.Simulation.Data.UTMOrigin;
            _fireGridCellSize = header.CellSize;
            _fireGridRecord = RecordOf(path, header.Ncols, header.Nrows);
            _haveFireGrid = true;
            _gridReference = reference;
            _gridDescription = what + " (" + reference + ")";
            _gridProblem = null;
            _gridProblemSaid = null;
            _evacGroupMasksLoaded = false;

            Engine.Message(null, Engine.LogType.Log,
                $"Painting on the grid of {what}: {header.Ncols} x {header.Nrows} cells of {header.CellSize:F1} m.");

            //A fire grid that does not reach the domain at all cannot be painted on usefully - the
            //brush would be somewhere off-screen - and the cause is always the same: the raster and the
            //simulation origin are in different UTM zones, so subtracting their eastings is meaningless.
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

        /// <summary>Why there is no paint grid, said in the console once until it changes or a grid is found.</summary>
        private void ReportGridProblem(string problem)
        {
            _gridProblem = problem;
            if (problem == _gridProblemSaid) return;
            _gridProblemSaid = problem;
            Engine.Message(null, Engine.LogType.Warning, problem);
        }

        /// <summary>True when the last attempt to find the paint grid failed, and the console has said why.</summary>
        public bool HasGridProblem { get => !_haveFireGrid && !string.IsNullOrEmpty(_gridProblem); }

        /// <summary>
        /// The record of a grid read from <paramref name="rasterPath"/>, if it has the paint grid's size; null when GDAL
        /// cannot read it as a north-up grid.
        /// </summary>
        private static GraphicalFireInput.PaintedGrid RecordOf(string rasterPath, int ncols, int nrows)
        {
            try
            {
                var grid = PREACT.Utility.PaintedMaskResampler.Grid.FromRaster(rasterPath);
                return grid.Ncols == ncols && grid.Nrows == nrows ? grid.ToPaintedGrid() : null;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The record of a loaded landscape's grid: from the raster it was read from, when that raster is where the
        /// landscape says it is (its corner within half a cell); null otherwise (an .lcp GDAL cannot place).
        /// </summary>
        private GraphicalFireInput.PaintedGrid RecordOfLandscape(PREACT.Input.PREACTInput input)
        {
            string reference = input?.Landscape?.GetReferenceFile();
            if (string.IsNullOrEmpty(reference) || _fireGridCellSize <= 0.0)
            {
                return null;
            }

            string path = System.IO.Path.IsPathRooted(reference) ? reference : System.IO.Path.Combine(input.RootFolder, reference);
            GraphicalFireInput.PaintedGrid record = System.IO.File.Exists(path) ? RecordOf(path, fireDataCellCount.x, fireDataCellCount.y) : null;
            if (record == null)
            {
                return null;
            }

            Vector2d corner = _fireGridOrigin + input.Simulation.Data.UTMOrigin;
            return record.DescribeMismatch(corner.x, corner.y, _fireGridCellSize, record.EpsgCode) == null ? record : null;
        }

        public void SetPainterMode(PaintMode mode)
        {
            if (mode == PaintMode.RandomIgnitionArea)
            {
                SetPainterRandomIgnition();
            }
            else if (mode == PaintMode.EvacGroup)
            {
                SetPainterEvacGroup(evacGroupIndex);
            }
            else
            {
                //A warning, not SimulationError: that type also stops any running simulation.
                Engine.Message(null, Engine.LogType.Warning, "Desired paint mode not yet implemented.");
            }
        }

        /// <summary>
        /// Tells the painter which groups exist, in the order their indices refer to. Called before
        /// painting so the painter can colour each group as the scenario defines it rather than
        /// inventing its own palette.
        /// </summary>
        public void SetEvacGroups(string[] names, Color[] colors)
        {
            names = names ?? new string[0];

            //Cells are owned by index, and the index is a position in this list - so a group added, removed
            //or renamed since the last call shifts every index after it. Ownership is carried across by name,
            //and a group that is gone leaves its cells unowned rather than handing them to its neighbour.
            if (_evacGroupCells != null && !SameNames(_evacGroupNames, names))
            {
                int[] remap = new int[_evacGroupNames.Length];
                for (int i = 0; i < _evacGroupNames.Length; ++i)
                {
                    remap[i] = System.Array.IndexOf(names, _evacGroupNames[i]);
                }

                for (int c = 0; c < _evacGroupCells.Length; ++c)
                {
                    int owner = _evacGroupCells[c];
                    _evacGroupCells[c] = owner >= 0 && owner < remap.Length ? remap[owner] : -1;
                }

                DestroyTexture(ref evacGroupTex);
                evacGroupColorArray = null;
            }

            _evacGroupNames = names;
            _evacGroupColors = colors ?? new Color[0];
        }

        /// <summary>
        /// A group was renamed: its cells stay its own. Ownership is carried across <see cref="SetEvacGroups"/> by name,
        /// so a rename used to read as the group removed and another added - its painted, unsaved cells became
        /// nobody's. Nothing happens when the painter does not know the old name, or already knows the new one.
        /// </summary>
        public void RenameEvacGroup(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || oldName == newName)
            {
                return;
            }

            int index = System.Array.IndexOf(_evacGroupNames, oldName);
            if (index < 0 || System.Array.IndexOf(_evacGroupNames, newName) >= 0)
            {
                return;
            }

            _evacGroupNames = (string[])_evacGroupNames.Clone();
            _evacGroupNames[index] = newName;
        }

        private static bool SameNames(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; ++i)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>Whether any group owns any cell, i.e. whether there is anything to save.</summary>
        public bool HasEvacGroupCells
        {
            get
            {
                if (_evacGroupCells == null) return false;
                for (int i = 0; i < _evacGroupCells.Length; ++i)
                {
                    if (_evacGroupCells[i] >= 0) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Reads the groups' existing masks into the painter, so painting carries on from what the scenario
        /// has instead of starting empty - and then overwriting every group that got a stroke on save.
        /// </summary>
        /// <remarks>
        /// A mask is taken only when it is on the paint grid: same cell count, same corner to within half a
        /// cell, same cell size. The masks are written with the grid's simulation-space corner, which is the
        /// frame EvacuationGroup.LoadMask reads them in, so that is the frame they are compared in. One on
        /// another grid is reported and left alone; painting that group again replaces it.
        /// </remarks>
        /// <returns>How many groups' masks were read; problems are added to <paramref name="problems"/>.</returns>
        public int LoadEvacGroupMasks(System.Collections.Generic.IList<PREACT.Evacuation.EvacuationGroupInput> groups,
            string rootFolder, System.Collections.Generic.List<string> problems)
        {
            if (groups == null || (!_haveFireGrid && !ResolveFireGrid()))
            {
                return 0;
            }

            int cells = fireDataCellCount.x * fireDataCellCount.y;
            if (_evacGroupCells == null || _evacGroupCells.Length != cells)
            {
                DropGroupStrokesFromOtherGrid(fireDataCellCount);
                _evacGroupCells = new int[cells];
                for (int i = 0; i < cells; ++i) _evacGroupCells[i] = -1;
            }

            int loaded = 0;
            for (int g = 0; g < groups.Count; ++g)
            {
                string mask = groups[g].MaskFile;
                if (string.IsNullOrEmpty(mask))
                {
                    continue;
                }

                int index = System.Array.IndexOf(_evacGroupNames, groups[g].Name);
                if (index < 0)
                {
                    continue;
                }

                string path = System.IO.Path.IsPathRooted(mask) ? mask
                    : System.IO.Path.Combine(rootFolder, mask.Replace('\\', '/'));
                if (!System.IO.File.Exists(path))
                {
                    problems?.Add($"Group {groups[g].Name}: its mask {mask} does not exist.");
                    continue;
                }

                float[,] data = PREACT.Utility.AscRaster.Read(path, out PREACT.Utility.AscRaster.Header header, out bool ok);
                if (!ok || data == null)
                {
                    problems?.Add($"Group {groups[g].Name}: could not read {mask}.");
                    continue;
                }

                double tolerance = 0.5 * _fireGridCellSize;
                bool sameGrid = header.Ncols == fireDataCellCount.x && header.Nrows == fireDataCellCount.y
                                && System.Math.Abs(header.CellSize - _fireGridCellSize) < 1e-3
                                && System.Math.Abs(header.XllCorner - _fireGridOrigin.x) < tolerance
                                && System.Math.Abs(header.YllCorner - _fireGridOrigin.y) < tolerance;
                if (!sameGrid)
                {
                    problems?.Add($"Group {groups[g].Name}: {mask} is {header.Ncols} x {header.Nrows} cells of "
                        + $"{header.CellSize:F1} m at {header.XllCorner:F0}, {header.YllCorner:F0}, not on the paint grid "
                        + $"({fireDataCellCount.x} x {fireDataCellCount.y} of {_fireGridCellSize:F1} m at "
                        + $"{_fireGridOrigin.x:F0}, {_fireGridOrigin.y:F0}). Paint the group again to replace it.");
                    continue;
                }

                int taken = 0;
                for (int y = 0; y < header.Nrows; ++y)
                {
                    for (int x = 0; x < header.Ncols; ++x)
                    {
                        float v = data[x, y];
                        if (v > 0f && v != (float)header.NoDataValue)
                        {
                            _evacGroupCells[x + y * header.Ncols] = index;
                            ++taken;
                        }
                    }
                }

                ++loaded;
                Engine.Message(null, Engine.LogType.Log, $"Evacuation group {groups[g].Name}: read {taken} painted cells from {mask}.");
            }

            _evacGroupMasksLoaded = true;
            UnsavedGroupStrokes = false;
            //Rebuilt from the ownership just read the next time it is shown.
            DestroyTexture(ref evacGroupTex);
            evacGroupColorArray = null;
            return loaded;
        }

        /// <summary>Group strokes on a grid of another size cannot be kept; said, and no longer counted as unsaved.</summary>
        private void DropGroupStrokesFromOtherGrid(Vector2int cellCount)
        {
            if (UnsavedGroupStrokes && _evacGroupCells != null && _evacGroupCells.Length != cellCount.x * cellCount.y)
            {
                Engine.Message(null, Engine.LogType.Warning, "Evacuation group areas painted on the previous grid and not "
                    + $"saved could not be carried onto this {cellCount.x} x {cellCount.y} grid, and are dropped. The groups' "
                    + "saved masks are unchanged.");
                UnsavedGroupStrokes = false;
            }
        }

        /// <summary>The paint grid's size in cells as last resolved, or (0, 0) when there is none. Resolves nothing.</summary>
        public Vector2int PaintGridSize { get => _haveFireGrid ? fireDataCellCount : new Vector2int(0, 0); }

        /// <summary>The group names the painter's cell ownership indices refer to, in index order.</summary>
        public string[] EvacGroupNames { get => (string[])_evacGroupNames.Clone(); }

        /// <summary>
        /// Writes the painted fire areas to <paramref name="path"/> without changing anything else: the scenario still
        /// names its own file and the strokes still count as unsaved in it (Copy scenario to writes them into the
        /// copy this way). False, with a warning, when there is no grid or nothing painted.
        /// </summary>
        public bool WritePaintedFireAreasTo(string path)
        {
            if (_manager == null || _manager.PREACTInput == null || (!_haveFireGrid && !ResolveFireGrid()))
            {
                return false;
            }

            PREACT.Input.WildfireData fireData = _manager.PREACTInput.WildfireModule.Data;
            int cells = fireDataCellCount.x * fireDataCellCount.y;
            if (fireData.RandomIgnition == null || fireData.RandomIgnition.Length != cells)
            {
                return false;
            }

            GraphicalFireInput.SaveGraphicalFireInput(path, fireData, fireDataCellCount.x, fireDataCellCount.y, _fireGridRecord);
            return true;
        }

        /// <summary>True once the scenario's group masks have been read for the current grid.</summary>
        public bool EvacGroupMasksLoaded { get => _evacGroupMasksLoaded; }

        /// <summary>
        /// Forgets the paint grid and everything built on it, so the next use resolves the grid again from
        /// the scenario as it now stands.
        /// </summary>
        /// <remarks>
        /// Called when a scenario is opened, when the fire case has been (re)built, and whenever the raster
        /// the grid should come from is no longer the one it came from. The painted masks themselves live on
        /// the scenario (WildfireData) and in its files, not here, so nothing painted and saved is lost.
        /// </remarks>
        public void ResetForScenario()
        {
            if (_manager != null && gameObject.activeSelf)
            {
                _manager.StopPainter();
            }

            DestroyTexture(ref randomIgnitionTex);
            DestroyTexture(ref evacGroupTex);
            DestroyTexture(ref _evacGroupOverlayTex);
            randomIgnitionColorArray = evacGroupColorArray = null;

            activeTexture = null;
            activeColorArray = null;
            activeCellCount = new Vector2int(0, 0);
            activeRealSize = Vector2d.zero;

            _haveFireGrid = false;
            fireDataCellCount = new Vector2int(0, 0);
            fireDataRealSize = Vector2d.zero;
            _fireGridOrigin = Vector2d.zero;
            _fireGridCellSize = 0.0;
            _fireGridRecord = null;
            _gridReference = null;
            _gridDescription = null;
            _gridProblem = null;
            _gridProblemSaid = null;

            _evacGroupCells = null;
            _evacGroupMasksLoaded = false;
            UnsavedFireStrokes = false;
            UnsavedGroupStrokes = false;

            _lcpData = _manager?.PREACTInput?.WildfireModule?.Data?.LandscapeData;

            if (_manager != null && _manager.FireDomainVisualizer != null)
            {
                _manager.FireDomainVisualizer.SetVisibility(false);
            }
        }

        /// <summary>
        /// Forgets the grid and the textures built on it, as <see cref="ResetForScenario"/> does, but keeps what was
        /// painted and not saved - for the same scenario whose terrain or case was re-read (a case build, "Use the
        /// case terrain", a changed fire module). The strokes live in the scenario's masks (and, for groups, here),
        /// and they still count as unsaved: a later close or quit asks about them.
        /// </summary>
        /// <remarks>
        /// Clearing the flags here is how strokes "stopped counting as unsaved" after a build: the build reads the
        /// painted areas from their file, so with "Don't save" the strokes were neither built nor saved nor asked
        /// about again. When the new grid has another size they cannot be kept, and the painter says so when it
        /// next builds a texture (see <see cref="CheckDataResources"/>) - unless they were carried onto it first.
        /// </remarks>
        public void ResetGrid()
        {
            bool fire = UnsavedFireStrokes;
            bool groups = UnsavedGroupStrokes;
            int[] groupCells = _evacGroupCells;
            bool groupMasksLoaded = _evacGroupMasksLoaded;

            ResetForScenario();

            UnsavedFireStrokes = fire;
            if (groups)
            {
                //Kept, and not read again from the masks on disk, which would replace them.
                UnsavedGroupStrokes = true;
                _evacGroupCells = groupCells;
                _evacGroupMasksLoaded = groupMasksLoaded;
            }
        }

        /// <summary>
        /// The scenario's painted fire areas were replaced from a file (a painting moved onto the fire grid): the
        /// fire textures are dropped so they are drawn from the new masks, and nothing painted here is unsaved.
        /// </summary>
        public void ReloadFireAreas()
        {
            if (_manager != null && gameObject.activeSelf && GetPaintMode() != PaintMode.EvacGroup)
            {
                _manager.StopPainter();
            }

            DestroyTexture(ref randomIgnitionTex);
            randomIgnitionColorArray = null;
            if (paintMode != PaintMode.EvacGroup)
            {
                activeTexture = null;
                activeColorArray = null;
            }
            UnsavedFireStrokes = false;
        }

        private static void DestroyTexture(ref Texture2D texture)
        {
            if (texture != null)
            {
                Destroy(texture);
                texture = null;
            }
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

            UnsavedGroupStrokes = false;
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
            DropGridIfStale();
            if (!_haveFireGrid && !ResolveFireGrid())
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

        void SetPainterRandomIgnition()
        {
            DropGridIfStale();
            if (!_haveFireGrid && !ResolveFireGrid())
            {
                return;
            }

            paintMode = PaintMode.RandomIgnitionArea;
            CheckDataResources(randomIgnitionTex, randomIgnitionColorArray);
            SetRandomIgnitionAreaColor(true);
            _brushSize = 5;
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
            DropGridIfStale();
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

            DropGridIfStale();
            if (!_haveFireGrid && !ResolveFireGrid())
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "The painted fire areas cannot be saved without knowing the grid they are on.");
                return null;
            }

            PREACT.Input.WildfireData fireData = _manager.PREACTInput.WildfireModule.Data;
            int cells = fireDataCellCount.x * fireDataCellCount.y;

            if (!AnyPainted(fireData.RandomIgnition, cells) && !AnyPainted(fireData.ManualTriggerBuffer, cells))
            {
                //Refused rather than written empty, because an empty file is not the same as no file: the
                //case builder reads an all-false ignition area as "not painted" and falls back to
                //ignite-anywhere, so a file saying nothing looks exactly like a file that was never saved
                //while making the checklist claim the scenario has painted areas.
                Engine.Message(null, Engine.LogType.Warning,
                    "Nothing has been painted, so there is nothing to save. Paint an ignition area first.");
                return null;
            }

            string name = FileNameOnThisGrid(folder, string.IsNullOrEmpty(fileName) ? GraphicalFireInput.DefaultFileName : fileName);
            string path = System.IO.Path.Combine(folder, name);

            //With the grid's record (review MI-3): the case builder places the painting only on the grid it was made on,
            //not on any grid of its size - a domain moved by whole cells used to take the old painting as its own.
            GraphicalFireInput.SaveGraphicalFireInput(path, fireData, fireDataCellCount.x, fireDataCellCount.y, _fireGridRecord);
            fireData.PaintedCellCount = fireDataCellCount;
            fireData.PaintedGrid = _fireGridRecord;
            UnsavedFireStrokes = false;

            Engine.Message(null, Engine.LogType.Log,
                $"Wrote {name}: {Count(fireData.RandomIgnition)} ignition area cells on a "
                + $"{fireDataCellCount.x} x {fireDataCellCount.y} grid of {_fireGridCellSize:F1} m.");
            //What an older file held that is not painted any more is not written again.
            fireData.LegacyWuiCells = fireData.LegacyInitialIgnitionCells = 0;

            return name;
        }

        /// <summary>
        /// <paramref name="name"/>, unless a painting of another grid is saved under it: that one is the only record
        /// of where those areas were (Mati's 616 x 590 painting, before it was moved onto the case grid), so it is
        /// kept, and this grid's areas get a name of their own beside it (painted_fire_areas_566x541.gfi).
        /// </summary>
        private string FileNameOnThisGrid(string folder, string name)
        {
            name = name.Replace('\\', '/');
            string path = System.IO.Path.Combine(folder, name);
            if (!System.IO.File.Exists(path))
            {
                return name;
            }

            //Unreadable (no size): not a painting anything can be recovered from, so it is replaced as before.
            GraphicalFireInput.PaintedGrid recorded = GraphicalFireInput.ReadGrid(path, out int width, out int height);
            if (width <= 0)
            {
                return name;
            }

            //Another grid is another size, or the same size somewhere else by the records of both.
            string elsewhere = recorded != null && _fireGridRecord != null
                ? recorded.DescribeMismatch(_fireGridRecord.XllCorner, _fireGridRecord.YllCorner, _fireGridRecord.CellSize, _fireGridRecord.EpsgCode)
                : null;
            if (width == fireDataCellCount.x && height == fireDataCellCount.y && elsewhere == null)
            {
                return name;
            }

            string fresh = PREACT.Utility.PaintedMaskResampler.NewFileName(path, fireDataCellCount.x, fireDataCellCount.y);
            string relativeFolder = System.IO.Path.GetDirectoryName(name)?.Replace('\\', '/');
            string freshName = string.IsNullOrEmpty(relativeFolder)
                ? System.IO.Path.GetFileName(fresh)
                : relativeFolder + "/" + System.IO.Path.GetFileName(fresh);
            Engine.Message(null, Engine.LogType.Warning, $"{name} holds a painting on a {width} x {height} grid"
                + (elsewhere != null ? $" of the same size elsewhere (this grid {elsewhere})" : string.Empty)
                + $", so it is kept; the areas painted on this {fireDataCellCount.x} x {fireDataCellCount.y} grid are saved as {freshName}.");
            return freshName;
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
                //Strokes painted on another grid and never saved cannot be carried onto this one either, and until now
                //still counted as unsaved; they are said to be gone, and stop counting.
                if (UnsavedFireStrokes && fireData.RandomIgnition != null && fireData.RandomIgnition.Length != cells)
                {
                    Engine.Message(null, Engine.LogType.Warning, "Fire areas painted on the previous grid and not saved "
                        + $"could not be carried onto this {cellCount.x} x {cellCount.y} grid, and are dropped. What was saved is "
                        + "still in " + (_manager.PREACTInput.WildfireModule.GraphicalFireInputFile ?? "its file") + ".");
                    UnsavedFireStrokes = false;
                }

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

                //The same size is still another grid when the masks record a place that is not this grid's (review MI-3):
                //a domain moved by whole cells. Shown here they would sit on the wrong ground, and strokes added to them
                //would be saved as this grid's. The saved file is left alone; the case build refuses it for the same reason.
                string elsewhere = fireData.PaintedGrid != null && _fireGridRecord != null && fireData.RandomIgnition != null
                                   && fireData.RandomIgnition.Length == cells
                    ? fireData.PaintedGrid.DescribeMismatch(_fireGridRecord.XllCorner, _fireGridRecord.YllCorner,
                        _fireGridRecord.CellSize, _fireGridRecord.EpsgCode)
                    : null;
                if (elsewhere != null)
                {
                    Engine.Message(null, Engine.LogType.Warning, (UnsavedFireStrokes
                            ? "Fire areas painted on the previous grid and not saved could not be carried onto this one"
                            : "The saved fire areas cannot be shown or added to here")
                        + $": they are on {fireData.PaintedGrid.Describe()}, and this {cellCount.x} x {cellCount.y} grid {elsewhere}. "
                        + "What was saved is still in " + (_manager.PREACTInput.WildfireModule.GraphicalFireInputFile ?? "its file")
                        + "; painting and saving again keeps it and writes a new file.");
                    UnsavedFireStrokes = false;
                    fireData.RandomIgnition = fireData.ManualTriggerBuffer = null;
                    fireData.PaintedCellCount = new Vector2int(0, 0);
                }

                if (fireData.RandomIgnition == null || fireData.RandomIgnition.Length != cells)
                {
                    fireData.UpdateRandomIgnitionIndices(null, cellCount.x, cellCount.y);
                }

                //From here the masks are this grid's: strokes go into them on it, and a run checks them against it.
                fireData.PaintedGrid = _fireGridRecord;

                //painter
                requestedColorArray = new Color[cellCount.x * cellCount.y];
                requestedTexture = new Texture2D(cellCount.x, cellCount.y);
                requestedTexture.filterMode = FilterMode.Point;
                for (int y = 0; y < cellCount.y; y++)
                {
                    for (int x = 0; x < cellCount.x; x++)
                    {
                        Color c = Color.white;
                        if (paintMode == PaintMode.RandomIgnitionArea)
                        {
                            c = _manager.PREACTInput.WildfireModule.Data.RandomIgnition[x + y * fireDataCellCount.x] == false ? inactiveAreaColor : activeAreaColor;
                        }
                        else if (paintMode == PaintMode.EvacGroup)
                        {
                            //Allocated on first use and kept, so switching between groups does not
                            //discard what has already been painted.
                            if (_evacGroupCells == null || _evacGroupCells.Length != cellCount.x * cellCount.y)
                            {
                                DropGroupStrokesFromOtherGrid(cellCount);
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
                if (paintMode == PaintMode.RandomIgnitionArea)
                {
                    randomIgnitionTex = requestedTexture;
                    randomIgnitionColorArray = requestedColorArray;
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
            activeCellCount = fireDataCellCount;
            activeRealSize = fireDataRealSize;
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

            //Not while a data step, a run or a campaign is at work: a build replaces the grid and the masks under the
            //brush, and a run reads them from another thread. The session puts the brush down when that starts.
            if (Assets.WUInity.GUI.DearIMGUI.ScenarioSession.IsBusy)
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

            if (paintMode == PaintMode.EvacGroup)
            {
                UnsavedGroupStrokes = true;
            }
            else
            {
                UnsavedFireStrokes = true;
            }

            if (paintMode == PaintMode.RandomIgnitionArea)
            {
                _manager.PREACTInput.WildfireModule.Data.RandomIgnition[x + y * activeCellCount.x] = addingArea;
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