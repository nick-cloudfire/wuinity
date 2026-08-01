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

        public bool[] WuiArea;
        public bool[] RandomIgnition;
        public bool[] InitialIgnition;
        public bool[] ManualTriggerBuffer;

        /// <summary>
        /// The grid the loaded masks were painted on, or (0,0) when none were loaded.
        ///
        /// Kept because the masks are the one thing in a scenario whose grid is not implied by anything
        /// else in it: they are painted on whichever raster the painter resolved, so a scenario that
        /// since gained a landscape, or had its imported fire replaced, can be holding masks measured in
        /// cells that no longer exist. Whoever uses them can then say so instead of indexing into them.
        /// </summary>
        public Vector2int PaintedCellCount;


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

            //Loaded whatever the module is, and whether or not a fire is being modelled, because the
            //masks are not the fire module's alone: k-PERIL falls back to WuiArea for the area it
            //protects (EvacuationManager.BuildWuiAreaRuns), and the painter needs what was painted last
            //time in order to carry on painting it. Nothing called this at all before, which is why
            //painting a WUI area and reopening the scenario showed nothing.
            if (!string.IsNullOrEmpty(wildfireInput.GraphicalFireInputFile))
            {
                LoadGraphicalFireInput(wildfireInput, Path.Combine(rootFolder, wildfireInput.GraphicalFireInputFile),
                    false, out bool _);
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
            //bonus for both - something to paint on, a surface for the map - rather than a requirement.
            if (wildfireInput.Module == WildfireModuleInput.WildfireModules.AscImport
                || wildfireInput.Module == WildfireModuleInput.WildfireModules.ELMFIRE)
            {
                success = true;
                return;
            }

            if (_lcpData == null)
            {
                Engine.Message(null, Engine.LogType.InputError,
                    "This wildfire module spreads fire itself, so it needs a landscape: either a LandscapeFile, or "
                    + "at least an ElevationFile and a FuelModelFile in the Landscape section.");
                return;
            }

            if (!_lcpData.HaveFuel)
            {
                Engine.Message(null, Engine.LogType.InputError,
                    "The landscape has no fuel model band, so this wildfire module has nothing to spread fire through.");
                return;
            }

            success = true;
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

            if (!string.IsNullOrEmpty(landscapeInput.LandscapeFile))
            {
                LoadLCPFile(wildfireInput, Path.Combine(rootFolder, landscapeInput.LandscapeFile), simulationInput.Data.UTMOrigin, false, out bool _);
                return;
            }

            string[] bands = landscapeInput.GetOrderedBandFiles();
            for (int i = 0; i < bands.Length; ++i)
            {
                if (!string.IsNullOrEmpty(bands[i]))
                {
                    bands[i] = Path.Combine(rootFolder, bands[i]);
                }
            }

            Wildfire.LandscapeData landscape = new Wildfire.LandscapeData(bands, simulationInput.Data.UTMOrigin);
            if (!landscape.CantAllocLCP)
            {
                _lcpData = landscape;
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

        public void LoadGraphicalFireInput(WildfireModuleInput fireInput, string filePath, LandscapeData lcpData, bool updateInput, out bool success)
        {
            GraphicalFireInput.LoadGraphicalFireInput(filePath, lcpData, out WuiArea, out RandomIgnition, out InitialIgnition, out ManualTriggerBuffer, out success);
            if (success)
            {
                PaintedCellCount = new Vector2int(lcpData.GetCellCountX(), lcpData.GetCellCountY());
            }
        }

        /// <summary>
        /// Loads painted masks on the grid the file itself declares, which is the only grid that can be
        /// known here - see <see cref="PaintedCellCount"/>.
        /// </summary>
        public void LoadGraphicalFireInput(WildfireModuleInput fireInput, string filePath, bool updateInput, out bool success)
        {
            GraphicalFireInput.LoadGraphicalFireInput(filePath, out int ncols, out int nrows,
                out WuiArea, out RandomIgnition, out InitialIgnition, out ManualTriggerBuffer, out success);

            if (!success)
            {
                return;
            }

            PaintedCellCount = new Vector2int(ncols, nrows);
            Engine.Message(null, Engine.LogType.Log,
                $"Loaded painted fire areas on a {ncols} x {nrows} grid: "
                + $"{Count(WuiArea)} WUI cells, {Count(RandomIgnition)} ignition area cells, "
                + $"{Count(InitialIgnition)} initial ignition cells.");

            if (updateInput)
            {
                fireInput.GraphicalFireInputFile = Path.GetFileName(filePath);
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

        public void UpdateWUIArea(bool[] wuiAreaIndices, int xCount, int yCount)
        {
            if (wuiAreaIndices == null)
            {
                wuiAreaIndices = new bool[xCount * yCount];
            }
            WuiArea = wuiAreaIndices;
        }

        public void UpdateRandomIgnitionIndices(bool[] randomIgnitionIndices, int xCount, int yCount)
        {
            if (randomIgnitionIndices == null)
            {
                randomIgnitionIndices = new bool[xCount * yCount];
            }
            RandomIgnition = randomIgnitionIndices;
        }

        public void UpdateInitialIgnitionIndices(bool[] initialIgnitionIndices, int xCount, int yCount)
        {
            if (initialIgnitionIndices == null)
            {
                initialIgnitionIndices = new bool[xCount * yCount];
            }
            InitialIgnition = initialIgnitionIndices;
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