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
        private FuelModelInput _fuelModelsData;
        private List<IgnitionPointInput> _ignitionPoints = new List<IgnitionPointInput>();
        private InitialFuelMoistureLibrary _initialFuelMoistureData;
        private CanadianFBPLookupTable _canadianFBPLookupTable = new CanadianFBPLookupTable();
        private LookupROSTable _lookupROSTable = new LookupROSTable();

        public bool[] WuiArea;
        public bool[] RandomIgnition;
        public bool[] InitialIgnition;
        public bool[] ManualTriggerBuffer;

               
        public LandscapeData LandscapeData { get => _lcpData; }        
        public FuelModelInput FuelModelsData { get => _fuelModelsData; }        
        public List<IgnitionPointInput> IgnitionPoints { get => _ignitionPoints; }       
        public InitialFuelMoistureLibrary InitialFuelMoistureData { get => _initialFuelMoistureData; }   
        public CanadianFBPLookupTable CanadianFBPLookupTable { get => _canadianFBPLookupTable; }
        public LookupROSTable LookupROSTable { get => _lookupROSTable; }

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

            if(!wildfireInput.Enabled)
            {
                Engine.Message(null, Engine.LogType.Log, "Skipping loading fire data as user has specified not running fire module.");
                success = true;
                return;
            }
            Engine.Message(null, Engine.LogType.Log, "Loading wildfire data...");

            //An imported fire brings its own arrival times, rates of spread and directions, so it needs
            //nothing further from the landscape - whatever was loaded above is a bonus rather than a
            //requirement.
            if (wildfireInput.Module == WildfireModuleInput.WildfireModules.AscImport)
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

            if (wildfireInput.Module == WildfireModuleInput.WildfireModules.ElmClone)
            {
                int issues = 0;
                string filePath;

                //common 
                filePath = Path.Combine(rootFolder, wildfireInput.FireCellInput.IgnitionPointsFile);
                LoadIgnitionPoints(wildfireInput, simulationInput, filePath, false, out success);
                issues += success ? 0 : 1;

                //spread model dependent
                if (wildfireInput.FireCellInput.SpreadRateModel == FireCellInput.SpreadRateModels.Behave)
                {
                    filePath = Path.Combine(rootFolder, wildfireInput.FireCellInput.FuelModelsFile);
                    LoadFuelModelsInput(wildfireInput, filePath, false, out success);
                    issues += success ? 0 : 1;

                    filePath = Path.Combine(rootFolder, wildfireInput.FireCellInput.InitialFuelMoistureFile);
                    LoadInitialFuelMoistureData(wildfireInput, filePath, false, out success);
                    issues += success ? 0 : 1;
                }
                else if (wildfireInput.FireCellInput.SpreadRateModel == FireCellInput.SpreadRateModels.CanadianFBP)
                {
                    filePath = Path.Combine(rootFolder, wildfireInput.FireCellInput.FBPLookupTableFile);
                    _canadianFBPLookupTable.Parse(Path.Combine(rootFolder, wildfireInput.FireCellInput.FBPLookupTableFile), out success);
                    issues += success ? 0 : 1;
                }
                else if (wildfireInput.FireCellInput.SpreadRateModel == FireCellInput.SpreadRateModels.LookupROS)
                {
                    filePath = Path.Combine(rootFolder, wildfireInput.FireCellInput.LookUpTableFile);
                    _lookupROSTable.Parse(Path.Combine(rootFolder, wildfireInput.FireCellInput.LookUpTableFile), out success);
                    issues += success ? 0 : 1;
                }

                if (issues > 0)
                {
                    return;
                }
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
            //The wildfire module's own LandscapeFile still works, and wins if it is set, so no existing
            //scenario changes behaviour.
            string moduleLandscape = wildfireInput.FireCellInput.LandscapeFile;
            if (!string.IsNullOrEmpty(moduleLandscape))
            {
                LoadLCPFile(wildfireInput, Path.Combine(rootFolder, moduleLandscape), simulationInput.Data.UTMOrigin, false, out bool _);
                return;
            }

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

            if (success && updateInput)
            {
                fireInput.FireCellInput.LandscapeFile = Path.GetFileName(filePath);
            }
        }

        public void LoadFuelModelsInput(WildfireModuleInput fireInput, string filePath, bool updateInput, out bool success)
        {
            _fuelModelsData = FuelModelInput.LoadFromFile(filePath, out success);
            if (success && updateInput)
            {
                fireInput.FireCellInput.FuelModelsFile = Path.GetFileName(filePath);
            }
        }

        public void LoadIgnitionPoints(WildfireModuleInput fireInput, SimulationInput simulationInput, string filePath, bool updateInput, out bool success)
        {
            IgnitionPointInput.LoadIgnitionPointsFile(_ignitionPoints, filePath, simulationInput, out success);
            if (success && updateInput)
            {
                fireInput.FireCellInput.IgnitionPointsFile = Path.GetFileName(filePath);
            }
        }

        public void LoadInitialFuelMoistureData(WildfireModuleInput fireInput, string filePath, bool updateInput, out bool success)
        {
            _initialFuelMoistureData = InitialFuelMoistureLibrary.LoadInitialFuelMoistureDataFile(filePath, out success);
            if (success && updateInput)
            {
                fireInput.FireCellInput.InitialFuelMoistureFile = Path.GetFileName(filePath);
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