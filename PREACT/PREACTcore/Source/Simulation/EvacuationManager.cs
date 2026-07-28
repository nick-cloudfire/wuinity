using PREACT.Math;
using System.Collections.Generic;
using PREACT.Input;
using PREACT.Pedestrian;
using PREACT.Wildfire;
using PREACT.Traffic;
using System.Diagnostics;
using System.IO;

namespace PREACT.Evacuation
{
    public class EvacuationManager
    {
        Simulation _simulation;
        private TrafficModule _trafficModule;
        private PedestrianModule _pedestrianModule;
        private TriggerBufferModule _triggerBufferModule;


        private Stopwatch _pathfindingStopwatch = new Stopwatch();
        private Stopwatch _roadClosureStopwatch = new Stopwatch();


        PREACTInput _input;
        EvacuationGroup _defaultEvacuationGroup;        
        Dictionary<string, EvacuationDestination> _evacuationDestinationsDict;
        List<EvacuationDestination> _evacuationDestinations;
        List<EvacuationDestination> _availableEvacuationDestinations;
        EvacuationGroup[] _evacuationGroups;
        DemographicsInput _defaultDemographics;

        public PedestrianModule PedestrianModule { get => _pedestrianModule; }
        public TrafficModule TrafficModule { get => _trafficModule; }
        public TriggerBufferModule TriggerBufferModule { get => _triggerBufferModule; }

        //Data, move?
        public List<EvacuationDestination> Destinations { get => _evacuationDestinations; }
        public DemographicsInput DefaultDemographics { get => _defaultDemographics; }

        public EvacuationManager(Simulation simulation)
        {
            _simulation = simulation;
            _input = _simulation.Input;
            _evacuationDestinationsDict = EvacuationDestination.CreateEvacacuationDestinationsFromInput(_simulation, _input.Evacuation.EvacuationDestinationInputs);
            SetDefaulDemographics(_input.Population.Demographics);
            _evacuationGroups = EvacuationGroup.CreateGroupsFromInput(_input.Evacuation.EvacuationGroupInputs, _evacuationDestinationsDict, _input.Evacuation.ResponseCurves, _input.Population.Demographics, _simulation);
            SetDefaulEvacuationtGroup(); //just sets default group fallback
            BuildEvacuationDestinationList(); //duplicate of destination but in an array, needed for random pull of destination
            BuildAvailableEvacuationDestinations();
        }

        public void PostStep()
        {
            //check for distance to wildfire front, affect evacuees
            //(_pedestrianModule is null when no pedestrian module is enabled)
            if(_simulation.Hazards.Wildfire != null && _pedestrianModule != null)
            {
                _pedestrianModule.ReactToWildfire(_simulation.Time.SimulationTime);
            }

            //handle all damage/impact on road network
            AffectRoadNetwork();

            //check if any goal has been blocked by fire, this is done after everything has progressed the current time step
            UpdateDestinationsWildfireStatus();

            //inject vehicles from all sources
            HandleNewVehicles();
        }

        private void AffectRoadNetwork()
        {
            //handle any fire effects on road network
            if (_simulation.Hazards.Wildfire != null)
            {
                if (_trafficModule != null)
                {
                    _roadClosureStopwatch.Start();
                    _trafficModule.HandleIgnitedFireCells(_simulation.Hazards.Wildfire.GetIgnitedFireCells());
                    _roadClosureStopwatch.Stop();
                }
                _simulation.Hazards.Wildfire.ConsumeIgnitedFireCells();
            }
        }

        private void HandleNewVehicles()
        {
            //handle/inject cars that arrived this timestep
            if (_trafficModule != null)
            {
                _pathfindingStopwatch.Start();
                _trafficModule.HandleNewCars();
                _pathfindingStopwatch.Stop();
            }
        }


        public List<SimulationModule> CreateModules(WeatherManager weather, TimeManager time, out bool success)
        {
            List<SimulationModule> createdModules = new List<SimulationModule>();

            CreatePedestrianModule(_simulation, _input, weather, time, out success);
            if(success && _pedestrianModule != null)
            {
                createdModules.Add(_pedestrianModule);
            }
            else
            {
                return createdModules;
            }

            CreateTrafficModule(_simulation, _input, weather, time, out success);
            if (success && _trafficModule != null)
            {
                createdModules.Add(_trafficModule);
            }
            else
            {
                return createdModules;
            }

            return createdModules;
        }

        private void CreatePedestrianModule(Simulation simulation, PREACTInput input, WeatherManager weather, TimeManager time, out bool success)
        {
            success = false;

            if (_input.PedestrianModule.Enabled)
            {
                if (_input.PedestrianModule.Module == PedestrianModuleInput.PedestrianModules.MacroHouseholdSim)
                {
                    _pedestrianModule = new MacroHouseholdSim(simulation);
                    Engine.Message(simulation, Engine.LogType.Log, "Pedestrian module MacroHouseholdSim initiated.");
                }
            }
            else
            {
                success = true;
                Engine.Message(simulation, Engine.LogType.Log, "No pedestrian module was enabled.");
            }

            if(_pedestrianModule != null)
            {
                success = true;
            }
        }

        private void CreateTrafficModule(Simulation simulation, PREACTInput input, WeatherManager weather, TimeManager time, out bool success)
        {
            success = false;

            if (_input.TrafficModule.Enabled)
            {
                if (_input.TrafficModule.Module == TrafficModuleInput.TrafficModules.SUMO)
                {
                    _trafficModule = new SUMOModule(simulation, out success);
                    if (success)
                    {
                        Engine.Message(simulation, Engine.LogType.Log, "Traffic module SUMO initiated.");
                    }
                    else
                    {
                        _trafficModule = null;
                    }
                }
                else
                {
                    Engine.Message(simulation, Engine.LogType.SimulationError, "No valid traffic module was specified.");
                }
            }
            else
            {
                success = true;
                Engine.Message(simulation, Engine.LogType.Log, "No traffic module was enabled.");
            }

            if (_trafficModule != null)
            {
                success = true;
            }
        }

        /// <summary>
        /// WRSET = the last-arrival (100%) evacuation time for this run, in minutes:
        /// the moment the final vehicle reaches safety. Fed to k-PERIL as the RSET.
        /// </summary>
        private float CalculateWRSETMinutes(Simulation simulation)
        {
            System.Collections.Generic.List<double> arrivals = simulation.Output.GetTrafficArrivalData();
            double lastArrivalSeconds = 0.0;
            if (arrivals != null)
            {
                for (int i = 0; i < arrivals.Count; i++)
                {
                    if (arrivals[i] > lastArrivalSeconds)
                    {
                        lastArrivalSeconds = arrivals[i];
                    }
                }
            }
            return (float)(lastArrivalSeconds / 60.0);
        }

        /// <summary>
        /// Load a WUI-area mask (.asc or .tif, 1 = protected cell) into a bool[] flattened
        /// as index = x + y*xCount, aligned with the fire ROS grid. Returns null if no file
        /// was given, it could not be read, or its dimensions do not match the ROS grid.
        /// </summary>
        private bool[] LoadWuiAreaMask(string wuiAreaFile, string rootFolder, int xCount, int yCount)
        {
            if (string.IsNullOrEmpty(wuiAreaFile))
            {
                return null;
            }

            string path = System.IO.Path.Combine(rootFolder, wuiAreaFile);
            float[,] mask = Utility.AscRaster.Read(path, out Utility.AscRaster.Header header, out bool ok);
            if (!ok || mask == null)
            {
                return null;
            }

            if (header.Ncols != xCount || header.Nrows != yCount)
            {
                Engine.Message(null, Engine.LogType.Warning, $"WUI mask dimensions ({header.Ncols}x{header.Nrows}) do not match the fire grid ({xCount}x{yCount}); ignoring the mask.");
                return null;
            }

            //Any positive value marks a WUI cell. This accepts both a crisp 1/0 mask and a
            //fractional raster such as ELMFIRE's bldg_footprint_frac (0 = no buildings).
            bool[] wuiArea = new bool[xCount * yCount];
            for (int y = 0; y < yCount; y++)
            {
                for (int x = 0; x < xCount; x++)
                {
                    float v = mask[x, y];
                    wuiArea[x + y * xCount] = v > 0f && v != (float)header.NoDataValue;
                }
            }
            return wuiArea;
        }

        /// <summary>
        /// Reads a wind raster onto the fire grid, returning null if it is missing, unreadable or
        /// the wrong size. AscRaster handles both .asc and .tif and returns [x, y] with a lower-left
        /// origin, which is the same convention GetMaxROS() uses - k-PERIL takes totalX/totalY from
        /// the ROS raster and throws on a size mismatch, so a transposed grid would be caught, but
        /// only on a non-square domain. Checking here means a square domain cannot slip through
        /// silently transposed.
        /// </summary>
        private float[,] LoadWindRaster(string windFile, string rootFolder, int xCount, int yCount, string what, bool isDirection)
        {
            if (string.IsNullOrEmpty(windFile))
            {
                Engine.Message(null, Engine.LogType.InputError, what + " was not specified; k-PERIL needs a wind field.");
                return null;
            }

            string path = System.IO.Path.Combine(rootFolder, windFile);
            float[,] raster = Utility.AscRaster.Read(path, out Utility.AscRaster.Header header, out bool ok);
            if (!ok || raster == null)
            {
                Engine.Message(null, Engine.LogType.InputError, what + " could not be read: " + path);
                return null;
            }

            if (header.Ncols != xCount || header.Nrows != yCount)
            {
                Engine.Message(null, Engine.LogType.InputError, $"{what} dimensions ({header.Ncols}x{header.Nrows}) do not match the fire grid ({xCount}x{yCount}).");
                return null;
            }

            float min = float.MaxValue;
            float max = float.MinValue;
            for (int x = 0; x < xCount; ++x)
            {
                for (int y = 0; y < yCount; ++y)
                {
                    float v = raster[x, y];
                    if (v <= -9000f || float.IsNaN(v))
                    {
                        continue;
                    }
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }

            if (min > max)
            {
                Engine.Message(null, Engine.LogType.InputError, what + " contains no valid data: " + path);
                return null;
            }

            //An all-zero or otherwise flat wind field is almost always the wrong file rather than a
            //real calm. It is worth saying so, because it fails silently: zero wind gives a
            //length-to-breadth ratio of 1, so the spread template turns into a circle and the
            //trigger boundary comes out isotropic instead of wind-driven.
            if (min == max)
            {
                Engine.Message(null, Engine.LogType.Warning, $"{what} is constant at {min}; the spread ellipse will be circular. Check that the weather pipeline actually produced this raster.");
            }

            //Directions outside [0, 360] mean the field was resampled in angle space, which
            //interpolates across the 0/360 wrap: averaging 350 and 10 yields 180, the opposite
            //direction. Overshoot past the ends is the visible symptom of it. Wind rasters have to
            //be warped as u/v components, which is what WindNinjaRunner does.
            if (isDirection && (min < -0.001f || max > 360.001f))
            {
                Engine.Message(null, Engine.LogType.Warning, $"{what} spans {min} to {max} degrees, outside 0-360. It was most likely resampled as angles rather than as u/v components, so directions near the 0/360 wrap are wrong.");
            }

            return raster;
        }

        public void CreateAndRunTriggerBufferModule(Simulation simulation, PREACTInput input, WeatherManager weather, TimeManager time)
        {
            if (_input.TriggerBufferModule.Enabled)
            {
                if (_input.TriggerBufferModule.Module == TriggerBufferModuleInput.TriggerBufferModules.kPERIL)
                {
                    if (!_input.WildfireModule.Enabled && !_input.TriggerBufferModule.kPERILInput.CalculateROSFromBehave)
                    {
                        Engine.Message(simulation, Engine.LogType.Warning, "Can't run kPERIL without fire module (user set not to use BEHAVE).");
                        return;
                    }
                    else
                    {
                        //WRSET = the required safe egress time this run produced: the last-arrival
                        //(100%) evacuation time, in minutes. This is what k-PERIL back-propagates.
                        float wrsetMinutes = CalculateWRSETMinutes(simulation);
                        Engine.Message(simulation, Engine.LogType.Log, "WRSET (last-arrival evacuation time) = " + wrsetMinutes + " minutes.");

                        int xCount = simulation.Hazards.Wildfire.GetCellCountX();
                        int yCount = simulation.Hazards.Wildfire.GetCellCountY();

                        //k-PERIL takes a wind field, not a representative number. Both rasters are
                        //required: without them there is nothing to derive the spread ellipse's
                        //elongation from, and silently standing in a constant would discard the
                        //terrain-driven variation the WindNinja step exists to produce.
                        float[,] windSpeedMph = LoadWindRaster(_input.TriggerBufferModule.kPERILInput.WindSpeedFile,
                            _input.RootFolder, xCount, yCount, nameof(kPERILInput.WindSpeedFile), false);
                        float[,] windDirectionDegrees = LoadWindRaster(_input.TriggerBufferModule.kPERILInput.WindDirectionFile,
                            _input.RootFolder, xCount, yCount, nameof(kPERILInput.WindDirectionFile), true);

                        if (windSpeedMph == null || windDirectionDegrees == null)
                        {
                            Engine.Message(simulation, Engine.LogType.SimulationError, "Can't run kPERIL without wind speed and direction rasters matching the fire grid.");
                            return;
                        }

                        //Prefer an explicit WUI-area mask (.asc/.tif) when supplied; otherwise
                        //fall back to whatever WuiArea the wildfire data carried.
                        bool[] wuiArea = LoadWuiAreaMask(_input.TriggerBufferModule.kPERILInput.WuiAreaFile, _input.RootFolder, xCount, yCount)
                                         ?? _input.WildfireModule.Data.WuiArea;

                        if (_input.TriggerBufferModule.kPERILInput.CalculateROSFromBehave)
                        {
                            _triggerBufferModule = new kPERIL(_input.WildfireModule.Data.LandscapeData, wrsetMinutes, wuiArea, windSpeedMph, windDirectionDegrees, _input.WildfireModule.Data.InitialFuelMoistureData, _input.WildfireModule.Data.FuelModelsData);
                        }
                        else
                        {
                            _triggerBufferModule = new kPERIL(wrsetMinutes, wuiArea, windSpeedMph, windDirectionDegrees, simulation.Hazards.Wildfire.GetMaxROS(), simulation.Hazards.Wildfire.GetMaxROSAzimuth(), simulation.Hazards.Wildfire.GetCellSizeX());
                        }
                        _triggerBufferModule.Run();
                        string outputFilePath = Path.Combine(simulation.Engine.OutputFolder, simulation.SimulationIndex + "_" + _input.TriggerBufferModule.kPERILInput.OutputName);
                        kPERIL.SaveToFile(_triggerBufferModule.TriggerBufferOutput, simulation.Hazards.Wildfire.GetCellSizeX(), outputFilePath);
                    }
                }
                else
                {

                }

                if (_triggerBufferModule != null)
                {
                    simulation.Output.AddTriggerBufferOutput(_triggerBufferModule.TriggerBufferOutput, simulation.SimulationIndex);
                }
            }
            else
            {
                Engine.Message(simulation, Engine.LogType.Log, "No trigger buffer module was enabled.");
            }
        }

        public void InsertNewCar(Vector2d startLatLon, EvacuationDestination evacuationGoal, uint numberOfPeopleInCar)
        {
            if (_trafficModule != null)
            {
                _trafficModule.InsertNewCar(startLatLon, evacuationGoal, numberOfPeopleInCar);
            }
        }

        int _runtimeDestinationCount = 0;
        public EvacuationDestination AddRuntimeDestination(Vector2d latLon)
        {
            EvacuationDestination eD = new EvacuationDestination(_simulation, latLon, "RuntimeDestination" + _runtimeDestinationCount);
            _evacuationDestinations.Add(eD);
            ++_runtimeDestinationCount;
            _simulation.Engine.UpdateEvacuationDestinations(_simulation, _evacuationDestinations);

            return eD;
        }

        public uint GetTotalEvacuated()
        {
            uint result = 0;
            foreach (EvacuationDestination eD in _evacuationDestinations)
            {
                result += eD.CurrentPeople;
            }

            return result;
        }

        public void UpdateDestinationsWildfireStatus()
        {
            if (!_input.WildfireModule.Enabled)
            {
                return;
            }

            foreach (EvacuationDestination eD in _evacuationDestinations)
            {
                if (!eD.Blocked)
                {
                    FireCellState cellState = _simulation.Hazards.Wildfire.GetFireCellState(eD.SimulationPos);
                    if (cellState == FireCellState.Ignited)
                    {
                        Engine.Message(_simulation, Engine.LogType.Log, " Destination blocked by fire: " + eD.Name);
                        BlockDestination(eD);
                    }
                }
            }
        }

        public void TryBlockDestination(string destinationName)
        {
            EvacuationDestination eD;
            if(_evacuationDestinationsDict.TryGetValue(destinationName, out eD))
            {
                BlockDestination(eD);
                Engine.Message(_simulation, Engine.LogType.Event, "Goal blocked by user specified event: " + eD.Name);
            }
            else
            {
                Engine.Message(_simulation, Engine.LogType.Event, $"Could not block the destination {destinationName} as it does not exist.");
            }
        }

        public void BlockDestination(EvacuationDestination blockedEvacDest)
        {
            blockedEvacDest.BlockDestination();
            UpdateEvacuationDestinations(blockedEvacDest);
        }

        private void UpdateEvacuationDestinations(EvacuationDestination evacDestThatTriggeredUpdate)
        {
            //check that we have at least one goal left
            bool allBlocked = true;
            _availableEvacuationDestinations.Clear();
            foreach (EvacuationDestination eD in _evacuationDestinations)
            {
                if (!eD.Blocked)
                {
                    _availableEvacuationDestinations.Add(eD);
                    allBlocked = false;
                }
            }
            if (allBlocked)
            {
                _simulation.Stop("All destinations are unavailable, stopping simulation.", false);
                return;
            }

            //TODO: delay this as nobody can actually know that the destination is closed without arriving there, we need a signaling system and compliance system
            /*if (_trafficModule != null)
            {
                _trafficModule.UpdateDestinations();
            }*/

            if (evacDestThatTriggeredUpdate.GoalType == DestinationTypes.Shelter)
            {

            }
            else if (evacDestThatTriggeredUpdate.GoalType == DestinationTypes.Exit)
            {

            }       
        }

        private void BuildEvacuationDestinationList()
        {
            _evacuationDestinations = new List<EvacuationDestination>(_evacuationDestinationsDict.Count);
            foreach (EvacuationDestination eD in _evacuationDestinationsDict.Values)
            {
                _evacuationDestinations.Add(eD);
            }
        }

        private void BuildAvailableEvacuationDestinations()
        {
            _availableEvacuationDestinations = new List<EvacuationDestination>(_evacuationDestinations.Count);
            foreach(EvacuationDestination eD in _evacuationDestinations)
            {
                if(!eD.Blocked)
                {
                    _availableEvacuationDestinations.Add(eD);
                }
            }
        }

        private EvacuationDestination GetRandomEvacuationDestination()
        {
            int randomChoice = Random.Range(0, _evacuationDestinations.Count);
            return _evacuationDestinations[randomChoice];
        }

        private EvacuationDestination GetRandomAvailableEvacuationDestination()
        {
            int randomChoice = Random.Range(0, _availableEvacuationDestinations.Count);
            return _availableEvacuationDestinations[randomChoice];
        }

        private EvacuationDestination GetClosestEuclideanDestination(Vector2d currentLatLon, List<EvacuationDestination> destinationsToConsider)
        {
            double closestDistance = double.MaxValue;
            Vector2d simPos = _input.Simulation.Data.GetSimulationPosition(currentLatLon);
            EvacuationDestination pickedDestination = null;

            foreach (EvacuationDestination eD in destinationsToConsider)
            {
                Vector2d destPos = _input.Simulation.Data.GetSimulationPosition(eD.LatLon);
                double distance = Vector2d.SqrMagnitude(destPos - simPos);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    pickedDestination = eD;
                }
            }

            return pickedDestination;
        }

        private EvacuationDestination GetClosestEuclideanAvailableDestination(Vector2d currentLatLon)
        {
            EvacuationDestination pickedDestination = GetClosestEuclideanDestination(currentLatLon, _availableEvacuationDestinations);
            return pickedDestination;
        }

        private EvacuationDestination GetClosestEuclideanDestination(Vector2d currentLatLon)
        {
            EvacuationDestination pickedDestination = GetClosestEuclideanDestination(currentLatLon, _evacuationDestinations);
            return pickedDestination;
        }


        private void SetDefaulEvacuationtGroup()
        {
            for(int i = 0; i < _evacuationGroups.Length; ++i)
            {
                if (_evacuationGroups[i].Default)
                {
                    _defaultEvacuationGroup = _evacuationGroups[i];
                    break;
                }
            }
        }

        private void SetDefaulDemographics(Dictionary<string, DemographicsInput> demographics)
        {
            foreach(DemographicsInput d in demographics.Values)
            {
                if(d.Default)
                {
                    _defaultDemographics = d;
                    break;
                }
            }
        }

        public EvacuationDestination GetEvacuationDestination(Vector2d latLon, EvacuationGroup evacuationGroup)
        {
            EvacuationDestination goal = null;

            if (evacuationGroup.DestinationChoice == DestinationChoices.EvacGroupCDF)
            {
                goal = evacuationGroup.GetWeightedRandomDestination();
            }
            else if (evacuationGroup.DestinationChoice == DestinationChoices.EvacGroupClosestEuclidean)
            {
                goal = evacuationGroup.GetClosestEuclideanDestination(latLon, _simulation);
            }
            else if (evacuationGroup.DestinationChoice == DestinationChoices.Random)
            {
                goal = GetRandomEvacuationDestination();

            }
            else //default to closest
            {
                goal = GetClosestEuclideanDestination(latLon);
            }

            if (goal == null)
            {
                Engine.Message(_simulation, Engine.LogType.SimulationError, "Issue with assigning evacuation destination, traffic simulation will not run.");
            }

            return goal;
        }

        public EvacuationGroup GetEvacuationGroup(Vector2d latLon, out bool insideGroup)
        {
            EvacuationGroup pickedGroup = _defaultEvacuationGroup;
            insideGroup = false;

            for (int i = 0; i < _evacuationGroups.Length; ++i)
            {
                if (_evacuationGroups[i].LatLonBelongsToGroup(latLon, _simulation))
                {
                    pickedGroup = _evacuationGroups[i];
                    insideGroup = true;
                    break;
                }
            }

            return pickedGroup;
        }

        public EvacuationDestination GetBestAvailableDestination(EvacuationGroup evacuationGroup, Vector2d currentLatLon)
        {
            EvacuationDestination result = null;

            //TODO: actual priority pick based on random weight or proximity?
            for (int i = 0; i < evacuationGroup.Destinations.Count; ++i)
            {
                if (!evacuationGroup.Destinations[i].Blocked)
                {
                    result = evacuationGroup.Destinations[i];
                    break;
                }
            }     

            //all group choices are blocked, pick something else
            if(result == null)
            {
                result = GetClosestEuclideanAvailableDestination(currentLatLon);
            }

            return result;
        }

        public EvacuationDestination GetBestAvailableDestination(Vector2d currentSimulationPos)
        {
            Vector2d currentLatLon = _simulation.Spatial.GetWGS84FromSimulationPosition(currentSimulationPos);
            EvacuationDestination result = GetClosestEuclideanAvailableDestination(currentLatLon);
            return result;
        }

        public void PostRun(Stopwatch simStopwatch)
        {
            Engine.Message(_simulation, Engine.LogType.Log, "Total time spent on road closures [s]:" + _roadClosureStopwatch.ElapsedMilliseconds * 0.001 + string.Format(" [{0}%]", (int)(100.0 * _roadClosureStopwatch.ElapsedMilliseconds / simStopwatch.ElapsedMilliseconds)));
            Engine.Message(_simulation, Engine.LogType.Log, "Total time spent on initial traffic route pathfinding [s]:" + _pathfindingStopwatch.ElapsedMilliseconds * 0.001 + string.Format(" [{0}%]", (int)(100.0 * _pathfindingStopwatch.ElapsedMilliseconds / simStopwatch.ElapsedMilliseconds)));
        }
    }
}
