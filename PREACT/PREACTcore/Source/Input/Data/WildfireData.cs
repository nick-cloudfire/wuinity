//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.IO;
using PREACT.Wildfire;
using PREACT.Math;
using System.Collections.Generic;

namespace PREACT.Input
{
    public class WildfireData
    {
        private LandscapeData _lcpData;
        private List<IgnitionPointInput> _ignitionPoints = new List<IgnitionPointInput>();

        /// <summary>The painted ignition area, where a campaign draws its ignitions (the case's ignition_mask.tif).</summary>
        public bool[] RandomIgnition;
        public bool[] ManualTriggerBuffer;

        /// <summary>
        /// What a painting from before round 2 holds that is no longer used, counted for the note its load gives: a
        /// painted WUI area (the WUI area is the evacuation groups') and a painted initial ignition (an ignition point,
        /// which the load adds in its place - see <see cref="LoadAll(SimulationInput, WildfireModuleInput, LandscapeInput, string, out bool)"/>).
        /// </summary>
        public int LegacyWuiCells, LegacyInitialIgnitionCells;

        //The legacy initial ignition, between reading the painting and turning it into an ignition point.
        private bool[] _legacyInitialIgnition;

        /// <summary>
        /// The grid the loaded masks were painted on, or (0,0) when none were loaded.
        ///
        /// Kept because the masks are the one thing in a scenario whose grid is not implied by anything
        /// else in it: they are painted on whichever raster the painter resolved, so a scenario that
        /// since gained a landscape, or had its imported fire replaced, can be holding masks measured in
        /// cells that no longer exist. Whoever uses them can then say so instead of indexing into them.
        /// </summary>
        public Vector2int PaintedCellCount;

        /// <summary>
        /// Where the grid the masks are on lies, when that is known: the record in the painting's file, or the grid
        /// the GUI's painter painted them on. Null for a file written before paintings recorded it. With it a mask
        /// of the fire grid's size is still refused when it was painted somewhere else (review MI-3).
        /// </summary>
        public GraphicalFireInput.PaintedGrid PaintedGrid;


        public LandscapeData LandscapeData { get => _lcpData; }
        public List<IgnitionPointInput> IgnitionPoints { get => _ignitionPoints; }

        public WildfireData()
        {

        }

        public void LoadAll(SimulationInput simulationInput, WildfireModuleInput wildfireInput, string rootFolder, out bool success)
        {
            LoadAll(simulationInput, wildfireInput, null, rootFolder, out success);
        }

        public void LoadAll(SimulationInput simulationInput, WildfireModuleInput wildfireInput, LandscapeInput landscapeInput, string rootFolder, out bool success)
        {
            success = false;

            //The landscape is loaded whether or not a fire is being modelled. It is terrain, and the
            //rest of the scenario has uses for it that have nothing to do with fire spread - somewhere
            //to paint, a surface to draw the map on, a slope to correct a trigger boundary by. This used
            //to be skipped entirely for a disabled fire module and for AscImport, which is why a
            //scenario with an imported fire had no terrain at all.
            LoadLandscape(simulationInput, wildfireInput, landscapeInput, rootFolder);

            //Loaded whatever the module is, and whether or not a fire is being modelled: the painter needs what was
            //painted last time in order to carry on painting it. Nothing called this at all before, which is why
            //painting and reopening the scenario showed nothing.
            if (!string.IsNullOrEmpty(wildfireInput.GraphicalFireInputFile))
            {
                string painting = PREACTInput.ResolvePath(rootFolder, wildfireInput.GraphicalFireInputFile);
                LoadGraphicalFireInput(wildfireInput, painting, false, out bool loaded);
                if (loaded) TakeLegacyPainting(painting, simulationInput, wildfireInput, landscapeInput, rootFolder);
            }

            if(!wildfireInput.Enabled)
            {
                Engine.Message(null, Engine.LogType.Log, "Skipping loading fire data as user has specified not running fire module.");
                success = true;
                return;
            }
            Engine.Message(null, Engine.LogType.Log, "Loading wildfire data...");

            //Neither of these needs anything further from the landscape. An imported fire brings its own
            //arrival times, rates of spread and directions; ELMFIRE computes them from its own case, whose
            //rasters are on its own grid and are not WUInity's to assemble. Whatever was loaded above is a
            //bonus for both - something to paint on, a surface for the map - rather than a requirement. With no
            //module chosen there is no fire to load; the wildfire section reports that.
            success = wildfireInput.Module == WildfireModuleInput.WildfireModules.AscImport
                      || wildfireInput.Module == WildfireModuleInput.WildfireModules.ELMFIRE;
        }

        /// <summary>
        /// Reads the landscape again after its bands were re-pointed in memory - a fire case build points
        /// <c>[Landscape]</c> at the case's own terrain (contract C1). What was loaded from the old bands is
        /// dropped first, so a new set that fails to load leaves no landscape rather than the previous grid.
        /// Painted masks and ignition points are left as they are.
        /// </summary>
        /// <remarks>
        /// The loaded landscape is what k-PERIL samples its slope from and what the GUI paints and draws on; it
        /// is read when the scenario is parsed, so without this a run straight after a build used the terrain
        /// the scenario named before it.
        /// </remarks>
        public void ReloadLandscape(SimulationInput simulationInput, WildfireModuleInput wildfireInput,
            LandscapeInput landscapeInput, string rootFolder)
        {
            _lcpData = null;
            LoadLandscape(simulationInput, wildfireInput, landscapeInput, rootFolder);
        }

        /// <summary>
        /// Loads the landscape from whichever of the three ways of describing one the scenario used:
        /// separate GeoTIFF bands, a single multiband GeoTIFF, or a FARSITE .lcp.
        ///
        /// Separate bands come first because they are the form that can actually be assembled outside the
        /// United States. A landscape needs elevation, slope, aspect, fuel model and canopy cover on one
        /// grid, and only LANDFIRE distributes all five as a package; anywhere else the elevation comes
        /// from a DEM and the fuels, if they exist at all, from somewhere unrelated. Requiring them
        /// pre-merged into one file made that a GIS exercise to be completed before the scenario could be
        /// opened. Slope and aspect are computed from the elevation, so in practice a DEM alone is enough
        /// to have terrain.
        /// </summary>
        private void LoadLandscape(SimulationInput simulationInput, WildfireModuleInput wildfireInput,
            LandscapeInput landscapeInput, string rootFolder)
        {
            //Only the [Landscape] section now. The fire module used to be able to name a .lcp of its own and
            //that took precedence, but the module that read it is gone - and a landscape is terrain, not a
            //fire module's property, which is why the section exists.
            if (landscapeInput == null || !landscapeInput.HaveAnything())
            {
                return;
            }

            //A named file that is not there is left out rather than opened: GDAL throws on a missing file, and that
            //exception used to abort the whole [WildfireModule] section before its [IgnitionPoint]s were read - so
            //opening a scenario whose case folder had been emptied or rebuilt, and saving it, deleted its ignition
            //points (e2e F3). [Landscape] has already put the missing file on the checklist, without making it
            //critical: the bands are optional, and the scenario loses its terrain, not its fire.
            try
            {
                if (!string.IsNullOrEmpty(landscapeInput.LandscapeFile))
                {
                    string lcp = PREACTInput.ResolvePath(rootFolder, landscapeInput.LandscapeFile);
                    if (!File.Exists(lcp))
                    {
                        Engine.Message(null, Engine.LogType.Warning,
                            "The landscape file " + landscapeInput.LandscapeFile + " is not there, so the scenario has no "
                            + "terrain until it is.");
                        return;
                    }
                    LoadLCPFile(wildfireInput, lcp, simulationInput.Data.UTMOrigin, false, out bool _);
                    return;
                }

                string[] bands = landscapeInput.GetOrderedBandFiles();
                var missing = new List<string>();
                for (int i = 0; i < bands.Length; ++i)
                {
                    if (string.IsNullOrEmpty(bands[i])) continue;

                    string resolved = PREACTInput.ResolvePath(rootFolder, bands[i]);
                    if (File.Exists(resolved))
                    {
                        bands[i] = resolved;
                    }
                    else
                    {
                        missing.Add(bands[i]);
                        bands[i] = string.Empty;
                    }
                }

                if (string.IsNullOrEmpty(bands[(int)LandscapeData.Band.Elevation]))
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        "The landscape's elevation raster is not there (" + string.Join(", ", missing) + "), so the scenario "
                        + "has no terrain to paint on or to give k-PERIL its slope until it is. Everything else was read.");
                    return;
                }

                if (missing.Count > 0)
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        "Landscape bands that are not there are left out (" + string.Join(", ", missing) + "); slope and "
                        + "aspect are computed from the elevation instead.");
                }

                Wildfire.LandscapeData landscape = new Wildfire.LandscapeData(bands, simulationInput.Data.UTMOrigin);
                if (!landscape.CantAllocLCP)
                {
                    _lcpData = landscape;
                }
            }
            catch (System.Exception e)
            {
                //A file that exists and cannot be read. The terrain is lost, not the rest of the section.
                PREACTInput.InputWarning("Landscape", "could not be read (" + e.Message + "); the scenario has no terrain.");
            }
        }

        public void LoadLCPFile(WildfireModuleInput fireInput, string filePath, Vector2d simulationUtmOrigin, bool updateInput, out bool success)
        {
            LandscapeData lcpData = new LandscapeData(filePath, simulationUtmOrigin);
            success = !lcpData.CantAllocLCP;

            if (success)
            {
                _lcpData = lcpData;
                HashSet<int> fuelNrs = _lcpData.GetExisitingFuelModelNumbers();
                string message = "Present fuel model numbers are ";
                int index = 0;
                foreach (int i in fuelNrs)
                {
                    message += i;
                    if (index < fuelNrs.Count - 1)
                    {
                        message += ", ";
                    }
                    else
                    {
                        message += ".";
                    }

                    ++index;
                }

                Engine.Message(null, Engine.LogType.Log, message);
            }

        }

        /*public void LoadWeatherInput(WildfireModuleInput fireInput, string filePath, bool updateInput, out bool success)
        {
            _weatherInput = WeatherInput.LoadWeatherInputFile(filePath, out success);
            if (success && updateInput)
            {
                fireInput.FireCellInput.WeatherFile = Path.GetFileName(filePath);
            }
        }*/

        /*public void LoadWindInput(WildfireModuleInput fireInput, string filePath, bool updateInput, out bool success)
        {
            _windInput = WindInput.LoadWindInputFile(filePath, out success);
            if (success && updateInput)
            {
                fireInput.FireCellInput.WindFile = Path.GetFileName(filePath);
            }
        }*/

        /// <summary>
        /// Loads painted masks on the grid the file itself declares, which is the only grid that can be
        /// known here - see <see cref="PaintedCellCount"/>. A painted WUI area or initial ignition in it (a painting made
        /// before round 2) is counted, not kept.
        /// </summary>
        public void LoadGraphicalFireInput(WildfireModuleInput fireInput, string filePath, bool updateInput, out bool success)
        {
            GraphicalFireInput.LoadGraphicalFireInput(filePath, out int ncols, out int nrows,
                out bool[] wuiArea, out RandomIgnition, out bool[] initialIgnition, out ManualTriggerBuffer,
                out GraphicalFireInput.PaintedGrid grid, out success);
            PaintedGrid = success ? grid : null;
            LegacyWuiCells = LegacyInitialIgnitionCells = 0;
            _legacyInitialIgnition = null;

            if (!success)
            {
                return;
            }

            PaintedCellCount = new Vector2int(ncols, nrows);
            LegacyWuiCells = Count(wuiArea);
            LegacyInitialIgnitionCells = Count(initialIgnition);
            if (LegacyInitialIgnitionCells > 0) _legacyInitialIgnition = initialIgnition;
            Engine.Message(null, Engine.LogType.Log,
                $"Loaded painted fire areas on a {ncols} x {nrows} grid: {Count(RandomIgnition)} ignition area cells.");

            if (updateInput)
            {
                fireInput.GraphicalFireInputFile = Path.GetFileName(filePath);
            }
        }

        //Paintings whose retired layers have been noted this session, by path and time, so the note is said once.
        private static readonly HashSet<string> NotedPaintings = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// What a painting from before round 2 holds that is no longer used. Its WUI area is noted and dropped: the WUI area
        /// is the evacuation groups'. Its initial ignition becomes one <c>[IgnitionPoint]</c> at the centroid of the painted
        /// cells - what the case build used to reduce it to - unless the scenario has ignition points already, which won over
        /// it then too; one at the centroid (a cell and a half) is that conversion, saved, and nothing is said.
        /// </summary>
        /// <remarks>
        /// The centroid is placed through the grid the painting records, else the case's dem.tif or the landscape raster
        /// when it is the painting's size; a painting on no known grid is noted and left. Said once per painting per
        /// session: the scenario is parsed again on every reload, and the point is made again each time until it is saved.
        /// </remarks>
        private void TakeLegacyPainting(string file, SimulationInput simulation, WildfireModuleInput fire,
            LandscapeInput landscape, string rootFolder)
        {
            if (LegacyWuiCells == 0 && _legacyInitialIgnition == null) return;

            bool first;
            lock (NotedPaintings)
            {
                first = NotedPaintings.Add(file + "|" + File.GetLastWriteTimeUtc(file).Ticks);
            }
            string name = Path.GetFileName(file);
            void Note(string message)
            {
                if (first) Engine.Message(null, Engine.LogType.Warning, message);
            }

            if (LegacyWuiCells > 0)
            {
                Note($"{name} holds a painted WUI area ({LegacyWuiCells} cells), which is no longer used: the WUI area is the "
                     + "evacuation groups' (workflow step 9). Saving the painted areas again leaves it out.");
            }

            bool[] initial = _legacyInitialIgnition;
            _legacyInitialIgnition = null;
            if (initial == null) return;

            int cells = LegacyInitialIgnitionCells;
            double sumX = 0.0, sumY = 0.0;
            int ncols = PaintedCellCount.x;
            for (int i = 0; i < initial.Length; ++i)
            {
                if (!initial[i]) continue;
                sumX += i % ncols;
                sumY += i / ncols;
            }

            GraphicalFireInput.PaintedGrid where = PaintedGrid
                ?? GridOfSize(Utility.ElmfireStems.Tif(Path.Combine(Utility.ElmfireCoupling.CaseDirectoryPath(rootFolder,
                       fire.ElmfireInput), "inputs"), Utility.ElmfireStems.Dem))
                ?? GridOfSize(string.IsNullOrEmpty(landscape?.GetReferenceFile()) ? null : PREACTInput.ResolvePath(rootFolder, landscape.GetReferenceFile()));
            double x = 0.0, y = 0.0, lat = 0.0, lon = 0.0;
            bool placed = where != null && where.EpsgCode > 0;
            if (placed)
            {
                //Cell centres, rows from the south, as every mask is held.
                x = where.XllCorner + (sumX / cells + 0.5) * where.CellSize;
                y = where.YllCorner + (sumY / cells + 0.5) * where.CellSize;
                placed = Utility.CrsTransform.TryToWgs84("EPSG:" + where.EpsgCode, x, y, out lat, out lon);
            }

            if (!placed)
            {
                Note($"{name} holds a painted initial ignition ({cells} cells), which is no longer used, and it does not say "
                     + "where its grid lies, so it could not be made an ignition point: place one in Fire areas and "
                     + "ignition (workflow step 6).");
                return;
            }

            var latLon = new Vector2d(lat, lon);
            Vector2d centroid = simulation.Data.GetSimulationPosition(latLon);
            foreach (Wildfire.IgnitionPointInput point in _ignitionPoints)
            {
                if (Vector2d.Distance(simulation.Data.GetSimulationPosition(point.LatLon), centroid) <= 1.5 * where.CellSize)
                {
                    return;
                }
            }

            string at = $"{lat.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}, "
                        + lon.ToString("F5", System.Globalization.CultureInfo.InvariantCulture);
            if (_ignitionPoints.Count > 0)
            {
                Note($"{name} holds a painted initial ignition ({cells} cells, centred on {at}), which is no longer used; "
                     + $"the scenario's {_ignitionPoints.Count} ignition point(s) are, as they were before.");
                return;
            }

            _ignitionPoints.Add(new Wildfire.IgnitionPointInput(latLon, false, 0f, simulation.StartDateTime));
            Note($"{name} holds a painted initial ignition ({cells} cells); painted initial ignitions are no longer used, "
                 + $"so it is now an ignition point at its centre, {at} - the point the case build used to ignite. Save the "
                 + "scenario to keep it.");
        }

        /// <summary>A raster's grid as a painting records one, when it is the painting's size; null otherwise.</summary>
        private GraphicalFireInput.PaintedGrid GridOfSize(string raster)
        {
            if (string.IsNullOrEmpty(raster) || !File.Exists(raster)) return null;
            try
            {
                Utility.PaintedMaskResampler.Grid grid = Utility.PaintedMaskResampler.Grid.FromRaster(raster);
                return grid.Ncols == PaintedCellCount.x && grid.Nrows == PaintedCellCount.y ? grid.ToPaintedGrid() : null;
            }
            catch (System.Exception)
            {
                return null;
            }
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

        public void UpdateRandomIgnitionIndices(bool[] randomIgnitionIndices, int xCount, int yCount)
        {
            if (randomIgnitionIndices == null)
            {
                randomIgnitionIndices = new bool[xCount * yCount];
            }
            RandomIgnition = randomIgnitionIndices;
        }

        //for painting trigger buffer manually
        public void UpdateTriggerBufferIndices(bool[] triggerBufferIndices, int xCount, int yCount)
        {
            if (triggerBufferIndices == null)
            {
                triggerBufferIndices = new bool[xCount * yCount];
            }
            ManualTriggerBuffer = triggerBufferIndices;
        }
    }
}