//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using PREACT.Evacuation;
using LIBSUMO = Eclipse.Sumo.Libsumo;
using PREACT.Math;

namespace PREACT.Traffic
{
    public class SUMOModule : TrafficModule
    {
        private Dictionary<string, SUMOVehicle> _sumoVehicles;        
        List<LIBSUMO.TraCIRoadPosition> _validStartPositions;

        //output
        private uint totalVehiclesArrived, totalPeopleArrived, totalSumoVehiclesArrived;
        private int currentVehiclessInSystem;
        private int totalVehiclesInjected, totalSumoVehiclesInjected;

        //Cars handed to SUMO and what became of them, for telling a broken SUMO from a few unroutable cars.
        private int _injectionAttempts, _injectionErrors, _injectionUnrouted;
        private string _firstInjectionError;
        private bool _injectionAbandoned;
        private List<string> output;

        private double[,] _usageMap;
        private uint[,] _carCount;
        private float[,] _accumulatedLevelOfService;
        private float[,] _accumulatedWatingTime;
        private bool _checkSmoke = false;

        private SumoConfig _sumoConfig;

        /// <summary>
        /// Starts SUMO for this simulation.
        /// </summary>
        /// <remarks>
        /// libsumo allows one instance per process, so any failure after the start closes it again; it used to stay
        /// open, and the next run in the same GUI session could not start SUMO at all. An instance left over from a
        /// run that did not close it is closed first.
        /// </remarks>
        public SUMOModule(Simulation simulation, out bool success) : base(simulation)
        {
            success = true;
            bool started = false;
            try
            {
                _sumoVehicles = new Dictionary<string, SUMOVehicle>();

                //Resolved rather than combined. The scenario's ConfigurationFile is a free path, and the one
                //thing that most often ends up in it is the OSM extract the network was built from - at which
                //point SUMO fails with "could not load configuration", a message about a file it was never
                //given the right kind of. The locator checks the path is a .sumocfg that exists, and finds
                //what the build step produced when it is not.
                string configFile = Utility.SumoConfigurationLocator.Resolve(
                    _simulation.Engine.WorkingFolder,
                    _simulation.Input.TrafficModule.SumoInput.ConfigurationFile,
                    true, out bool corrected, out string explanation);

                if (configFile == null)
                {
                    success = false;
                    Engine.Message(_simulation, Engine.LogType.SimulationError, "Could not start SUMO. " + explanation);
                    return;
                }

                if (corrected)
                {
                    //A warning, not a silent substitution: the run proceeds, but the scenario is still wrong
                    //and will be wrong again next time it is opened.
                    Engine.Message(_simulation, Engine.LogType.Warning, explanation);
                }
                CloseLeftoverInstance();

                //see here for options https://sumo.dlr.de/docs/sumo.html, setting input file, start and end time.
                //SUMO's own step is the simulation's, so Simulation.step(t) lands exactly on each PREACT step (SUMO's
                //default of 1 s would silently round a 0.5 s step), and every number is written culture-invariant.
                var invariant = System.Globalization.CultureInfo.InvariantCulture;
                LIBSUMO.Simulation.start(new LIBSUMO.StringVector(new String[] { "sumo", "-c", configFile, "-b", "0.0",
                    "-e", _simulation.Time.SimulationEndTime.ToString("R", invariant),
                    "--step-length", ((double)_simulation.Input.Simulation.DeltaTime).ToString("R", invariant) }));
                started = true;

                //check if destinations are valid, if not abort
                ValidateDestinations(simulation.Evacuation.Destinations, out bool allValid);
                if(!allValid)
                {
                    success = false;
                    return;
                }

                _sumoConfig = new SumoConfig(configFile, _simulation.Input.WildfireModule.Enabled);
                CheckNetworkZone();

                //need to use UTM projection in SUMO and WUInity to overlay data
                Vector2d sumoUTM = -_sumoConfig.Network.UTMOffset;// new Vector2d(-_simulation.Input.TrafficModule.SumoInput.UTMoffset.x, -_simulation.Input.TrafficModule.SumoInput.UTMoffset.y);
                _originOffset = sumoUTM - _simulation.Spatial.UTMOrigin;

                _validStartPositions = new List<LIBSUMO.TraCIRoadPosition>();

                output = new List<string>();
                string header = "Time(s),Total cars injected, Total cars arrived,Current cars in system,Exiting people,Total Sumo cars injected,Total Sumo cars arrived";
                for (int i = 0; i < _simulation.Evacuation.Destinations.Count; ++i)
                {
                    header += "," + _simulation.Evacuation.Destinations[i].Name + " people arrived";
                    header += "," + _simulation.Evacuation.Destinations[i].Name + " cars arrived";
                    header += "," + _simulation.Evacuation.Destinations[i].Name + " flow [veh./h]";
                }
                output.Add(header);

                int xDim = Mathd.CeilToInt(_simulation.Input.Simulation.DomainSize.x / _simulation.Input.TrafficModule.SumoInput.OutputRasterSize);
                int yDim = Mathd.CeilToInt(_simulation.Input.Simulation.DomainSize.y / _simulation.Input.TrafficModule.SumoInput.OutputRasterSize);

                _usageMap = new double[xDim, yDim];
                _carCount = new uint[xDim, yDim];
                _accumulatedLevelOfService = new float[xDim, yDim];
                _accumulatedWatingTime = new float[xDim, yDim];

                if(_simulation.Input.SmokeModule.Enabled && (simulation.Input.TrafficModule.SumoInput.SmokeAlpha != 0f || _simulation.Input.TrafficModule.SumoInput.SmokeBeta != 0f))
                {
                    _checkSmoke = true;
                }

                SortEdgesInFireCells();
            }
            catch(Exception e)
            {
                success = false;
                Engine.Message(_simulation, Engine.LogType.SimulationError, "Could not start SUMO, aborting. " + e.Message + ". " + e.InnerException);
            }
            finally
            {
                if (!success && started)
                {
                    Stop();
                }
            }
        }

        /// <summary>SUMO has its own clock; it is stepped every step even with no car in it (see Simulation.Step).</summary>
        public override bool StepWhenDone { get => true; }

        private void CloseLeftoverInstance()
        {
            try
            {
                if (LIBSUMO.Simulation.isLoaded())
                {
                    Engine.Message(_simulation, Engine.LogType.Warning, "A SUMO instance from an earlier run was still open; closing it first.");
                    LIBSUMO.Simulation.close();
                }
            }
            catch (Exception)
            {
                //isLoaded throws when nothing is loaded on some libsumo versions; nothing to close then.
            }
        }

        /// <summary>
        /// Says loudly when the network is projected into a different UTM zone than the simulation measures in.
        /// </summary>
        /// <remarks>
        /// Car injection converts through lat/lon and is unaffected, but everything planar - which road runs through
        /// which fire cell, redirecting a vehicle, the traffic heat map - assumes the network and the simulation
        /// share a zone. netconvert picks the zone of the extract's centre unless told (SumoNetworkBuilder now
        /// tells it), so a domain across a zone boundary could be off by some 500 km with nothing visibly wrong.
        /// Reported, not refused, so existing campaigns keep running; rebuild the network to fix it.
        /// </remarks>
        private void CheckNetworkZone()
        {
            int networkZone = _sumoConfig.Network.UtmEpsgCode;
            int simulationZone = _simulation.Input.Simulation.Data.UtmEpsgCode;
            if (networkZone == 0)
            {
                Engine.Message(_simulation, Engine.LogType.Warning, "The SUMO network does not record a UTM projection ("
                    + (string.IsNullOrEmpty(_sumoConfig.Network.ProjParameter) ? "no projParameter" : _sumoConfig.Network.ProjParameter)
                    + "); road positions are assumed to be in the simulation's zone, EPSG:" + simulationZone + ".");
            }
            else if (networkZone != simulationZone)
            {
                Engine.Message(_simulation, Engine.LogType.Warning, $"The SUMO network is projected in EPSG:{networkZone} but the simulation "
                    + $"measures in EPSG:{simulationZone}. Fire road closures, vehicle redirection and the traffic maps will be misplaced "
                    + "(car injection is not affected). Rebuild the network from the scenario (Prepare data > roads), which now "
                    + "projects it into the simulation's zone.");
            }
        }

        private void ValidateDestinations(List<EvacuationDestination> destinations, out bool allValid)
        {
            allValid = true;

            foreach (EvacuationDestination eD in destinations)
            {
                try
                {
                    //IMPORTANT!!! Longitude then latitude in SUMO
                    LIBSUMO.TraCIRoadPosition road = LIBSUMO.Simulation.convertRoad(eD.LatLon.y, eD.LatLon.x, true);
                }
                catch (Exception e)
                {
                    allValid = false;
                    Engine.Message(_simulation, Engine.LogType.SimulationError, $"Destination {eD.Name} returns error from SUMO: {e.Message}");
                }
                
            }
        }

        //might crash SUMO when running a new instance of SUMOModule while the old one is garbage collected
        /*~SUMOModule()
        {
            LIBSUMO.Simulation.close();
        }*/

        /// <summary>
        /// Speed multiplier in smoke, <c>1 - alpha * exp(beta / K)</c> with K the extinction coefficient in 1/m
        /// (alpha in [0, 1], beta &lt; 0 so the reduction grows with K), clamped to [0.05, 1].
        /// </summary>
        /// <remarks>
        /// K = 0 (clear air) is no reduction; the formula used to be evaluated there as well, where beta / 0 is
        /// infinite - NaN for beta = 0 and minus infinity for beta &gt; 0, both handed to SUMO as a speed factor.
        /// </remarks>
        float GetSmokeSpeedReductionFactor(Vector2d pos)
        {
            float extCoeff = _simulation.Hazards.GetExtinctionCoefficientAtPos(pos);
            float alpha = _simulation.Input.TrafficModule.SumoInput.SmokeAlpha;
            float beta = _simulation.Input.TrafficModule.SumoInput.SmokeBeta;
            if (!(extCoeff > 0f) || alpha == 0f)
            {
                return 1f;
            }

            float factor = 1f - alpha * Mathf.Exp(beta / extCoeff);
            if (float.IsNaN(factor))
            {
                return 1f;
            }
            return Mathf.Clamp(factor, 0.05f, 1f);
        }

        public override void Step(double currentTime, double deltaTime)
        {
            if(_checkSmoke)
            {
                foreach (KeyValuePair<string, SUMOVehicle> sV in _sumoVehicles)
                {
                    SUMOVehicle vehicle = sV.Value;
                    double speedFactor = vehicle.InitialSpeedFactor * GetSmokeSpeedReductionFactor(vehicle.SimulationPos);
                    LIBSUMO.Vehicle.setSpeedFactor(vehicle.GetSumoVehicleID(), speedFactor);
                }
            }           

            //https://sumo.dlr.de/doxygen/d0/d17/classlibsumo_1_1_simulation.html
            LIBSUMO.Simulation.step(currentTime + deltaTime); // advances sim up to given time

            //update positions
            LIBSUMO.StringVector activeVehicles = LIBSUMO.Vehicle.getIDList();
            currentVehiclessInSystem = 0;
            if(activeVehicles.Count > 0)
            {
                for (int i = 0; i < activeVehicles.Count; i++)
                {
                    string sumoID = activeVehicles[i];
                    SUMOVehicle vehicle;
                    _sumoVehicles.TryGetValue(sumoID, out vehicle);
                    if(vehicle != null)
                    {
                        LIBSUMO.TraCIPosition pos = LIBSUMO.Vehicle.getPosition(sumoID);
                        vehicle.SetWorldPosItionAndRotation(pos, LIBSUMO.Vehicle.getAngle(sumoID), _originOffset);
                        if(vehicle.NumberOfPeople > 0)
                        {
                            currentVehiclessInSystem++;
                        }
                    }
                    //this can happen since SUMO can have control of car injection as well, not only injected from WUInity
                    else
                    {
                        vehicle = new SUMOVehicle(GetNewCarID(), sumoID, LIBSUMO.Vehicle.getPosition(sumoID), LIBSUMO.Vehicle.getAngle(sumoID), 0, null, LIBSUMO.Vehicle.getSpeedFactor(sumoID));
                        _sumoVehicles.Add(sumoID, vehicle);
                        _activeVehicles.Add(vehicle.VehicleId, vehicle);
                        ++totalSumoVehiclesInjected;
                    }

                    UpdateOutputMaps(vehicle, deltaTime);
                }
            }     

            //check if any cars have arrived
            if (LIBSUMO.Simulation.getArrivedNumber() > 0)
            {
                LIBSUMO.StringVector arrivedVehicles = LIBSUMO.Simulation.getArrivedIDList();
                for (int i = 0; i < arrivedVehicles.Count; i++)
                {
                    SUMOVehicle vehicle;
                    _sumoVehicles.TryGetValue(arrivedVehicles[i], out vehicle);
                    if(vehicle != null)
                    {
                        bool couldArrive = vehicle.TryToArrive(deltaTime, currentTime);
                        if(couldArrive)
                        {
                            _sumoVehicles.Remove(arrivedVehicles[i]);
                            _activeVehicles.Remove(vehicle.VehicleId);

                            //if car is internal to SUMO they have 0 passengers from the point of view of the simulation
                            if (vehicle.NumberOfPeople > 0)
                            {
                                _arrivalData.Add(currentTime + deltaTime);
                                totalVehiclesArrived++;
                                totalPeopleArrived += vehicle.NumberOfPeople;
                            }
                            else
                            {
                                ++totalSumoVehiclesArrived;
                            }                            
                        }
                        else
                        {
                            RedirectVehicle(vehicle, true);
                        }
                    }
                }
            }

            //Time(s),Total cars injected, Total cars arrived,Current cars in system, Exiting people
            var dataLine = new System.Text.StringBuilder(System.FormattableString.Invariant(
                $"{currentTime},{totalVehiclesInjected},{totalVehiclesArrived},{currentVehiclessInSystem},{totalPeopleArrived},{totalSumoVehiclesInjected},{totalSumoVehiclesArrived}"));
            for (int i = 0; i < _simulation.Evacuation.Destinations.Count; ++i)
            {
                EvacuationDestination destination = _simulation.Evacuation.Destinations[i];
                dataLine.Append(System.FormattableString.Invariant($",{destination.CurrentPeople},{destination.Vehicles.Count},{destination.GetVehicleFlow(currentTime + deltaTime)}"));
            }
            output.Add(dataLine.ToString());
        }

        private void UpdateOutputMaps(SUMOVehicle vehicle, double deltaTime)
        {
            Vector2d pos = vehicle.SimulationPos;

            int xIndex = (int)(_usageMap.GetLength(0) * pos.x / _simulation.Input.Simulation.DomainSize.x);
            int yIndex = (int)(_usageMap.GetLength(1) * pos.y / _simulation.Input.Simulation.DomainSize.y);

            //we can be outside as sometimes roads reach beyond simulation domain
            if (xIndex >= 0 && xIndex < _usageMap.GetLength(0) && yIndex >= 0 && yIndex < _usageMap.GetLength(1))
            {
                _usageMap[xIndex, yIndex] += deltaTime;

                _accumulatedLevelOfService[xIndex, yIndex] += vehicle.SpeedRatio;
                _accumulatedWatingTime[xIndex, yIndex] += (float)LIBSUMO.Vehicle.getWaitingTime(vehicle.GetSumoVehicleID());
                _carCount[xIndex, yIndex] += 1;
            }
        }

        /// <summary>How many cars have to have been tried before a failure rate means anything.</summary>
        public const int InjectionCheckMinimumCars = 25;

        /// <summary>The share of cars that may fail to enter SUMO before the run is stopped as broken.</summary>
        public const double InjectionFailureLimit = 0.9;

        /// <summary>
        /// Why a run whose cars mostly do not reach SUMO cannot continue, or null while it can: after
        /// <see cref="InjectionCheckMinimumCars"/> cars, more than <see cref="InjectionFailureLimit"/> of them not
        /// injected.
        /// </summary>
        /// <remarks>
        /// Each failed car used to be one warning and the run carried on: on Linux with the Windows SWIG glue all
        /// 928 of Mati's cars failed ("Unable to find an entry point named '?' in shared library 'libsumocs'"), the
        /// evacuation finished with nobody in it, and only k-PERIL's "no arrivals" gave the run a non-zero exit - and a
        /// run without k-PERIL none at all (e2e F5). A few unroutable cars are normal; nearly all of them is a SUMO,
        /// binding or network problem, and nothing computed from the rest would mean anything.
        /// </remarks>
        public static string DescribeInjectionFailure(int attempted, int injected, int errors, int unrouted, string firstError)
        {
            if (attempted < InjectionCheckMinimumCars) return null;
            int failed = attempted - injected;
            if (failed <= InjectionFailureLimit * attempted) return null;

            string why = errors > 0
                ? $"{errors} were refused by SUMO itself (first: {firstError})"
                : "none was refused by SUMO";
            string hint = firstError != null && firstError.IndexOf("entry point", StringComparison.OrdinalIgnoreCase) >= 0
                ? " The C# bindings (Runtimes/Managed/Eclipse.Sumo.Libsumo) do not match this SUMO's libsumocs: they have "
                  + "to be the files SWIG generated for the same SUMO build (the committed ones match the Windows SUMO "
                  + "1.22; for another build, copy its build/src/libsumo/cs/*.cs over them)."
                : unrouted > errors
                    ? " Most had no route to their destination: check that the SUMO network covers the population and the "
                      + "destinations, and is in the simulation's UTM zone."
                    : string.Empty;

            return $"{failed} of the first {attempted} cars could not be put into SUMO ({why}; {unrouted} had no route), "
                   + "so the evacuation would run without them. Stopping the run." + hint;
        }

        public override void HandleNewCars()
        {
            if (_injectionAbandoned)
            {
                _carsToInject.Clear();
                return;
            }

            foreach (InjectedCar injectedCar in _carsToInject)
            {
                ++_injectionAttempts;
                EvacuationDestination evacuationGoal = injectedCar.evacuationDestination;
                uint numberOfPeopleInCar = injectedCar.numberOfPeopleInCar;
                Vector2d startLatLon = injectedCar.startLatLon;                
                Vector2d destinationLatLon = evacuationGoal.LatLon;

                try
                {
                    //IMPORTANT!!! Longitude then latitude in SUMO
                    LIBSUMO.TraCIRoadPosition startRoad = LIBSUMO.Simulation.convertRoad(startLatLon.y, startLatLon.x, true); //lon/lat
                    LIBSUMO.TraCIRoadPosition destinationRoad = LIBSUMO.Simulation.convertRoad(destinationLatLon.y, destinationLatLon.x, true); //lon/lat
                    //Routed on the empty network's speed limits, as it always was (the alternative, routing on the
                    //network's current state, was an unreachable else branch).
                    LIBSUMO.TraCIStage route = LIBSUMO.Simulation.findRoute(startRoad.edgeID, destinationRoad.edgeID);

                    bool foundRoute = false;
                    if (route.edges.Count > 0)
                    {
                        _validStartPositions.Add(startRoad);
                        foundRoute = true;
                    }
                    //if we reach here we need to teleport the car to a new location as no valid route could be found
                    else if (_validStartPositions.Count > 0)
                    {
                        int randomStart = Math.Random.Range(0, _validStartPositions.Count);   
                        //TODO: actually save start/goal pairs as we might try to generate route from a random start position to a non-reachable current goal of the car
                        route = LIBSUMO.Simulation.findRoute(_validStartPositions[randomStart].edgeID, destinationRoad.edgeID);    
                        if(route.edges.Count > 0)
                        {
                            foundRoute = true;
                            Engine.Message(null, Engine.LogType.Warning, $"No route could be found for the injected car, so it was teleported to a valid location. Origin (lat/lon) {startLatLon.x}, {startLatLon.y}, dest. (lat/lon) {destinationLatLon.x}, {destinationLatLon.y}.");
                        }
                        else
                        {
                            Engine.Message(null, Engine.LogType.Warning, $"No route could be found for the injected car, tried teleporting but no valid route could be found. Origin (lat/lon) {startLatLon.x}, {startLatLon.y}, dest. (lat/lon) {destinationLatLon.x}, {destinationLatLon.y}.");
                        }
                    }
                    else
                    {
                        Engine.Message(null, Engine.LogType.Warning, $"Car could not be injected as no valid route was found or cached. Origin (lat/lon) {startLatLon.x}, {startLatLon.y}, dest. (lat/lon) {destinationLatLon.x}, {destinationLatLon.y}.");
                    }

                    if (!foundRoute) ++_injectionUnrouted;

                    if(foundRoute)
                    {
                        uint carID = GetNewCarID();
                        string sumoID = carID.ToString();
                        string routeID = "preact_route_" + carID;
                        LIBSUMO.Route.add(routeID, route.edges);
                        LIBSUMO.Vehicle.add(sumoID, routeID);//, vehicleType);
                        LIBSUMO.TraCIPosition startPos = LIBSUMO.Vehicle.getPosition(sumoID);
                        SUMOVehicle car = new SUMOVehicle(carID, sumoID, startPos, 0, numberOfPeopleInCar, evacuationGoal, LIBSUMO.Vehicle.getSpeedFactor(sumoID));
                        _sumoVehicles.Add(sumoID, car);
                        _activeVehicles.Add(car.VehicleId, car);
                        ++totalVehiclesInjected;
                    }
                }
                catch (Exception e)
                {
                    ++_injectionErrors;
                    if (_firstInjectionError == null) _firstInjectionError = e.Message;
                    Engine.Message(null, Engine.LogType.Warning, "Issue injecting vehicle into SUMO: " + e.Message);
                }

                string failure = DescribeInjectionFailure(_injectionAttempts, totalVehiclesInjected, _injectionErrors,
                    _injectionUnrouted, _firstInjectionError);
                if (failure != null)
                {
                    _injectionAbandoned = true;
                    Engine.Message(_simulation, Engine.LogType.SimulationError, failure);
                    break;
                }
            } 
            
            _carsToInject.Clear();
        }

        public override bool IsSimulationDone()
        {
            if(totalVehiclesArrived == totalVehiclesInjected)
            {
                return true;
            }

            return false;
        }

        public override int GetNumberOfCarsInSystem()
        {
            return currentVehiclessInSystem;
        }

        public override int GetTotalCarsSimulated()
        {
            return totalVehiclesInjected;
        }        

        public override void SaveToFile(int simulationIndex)
        {
            string filePath;
            //arrival data to csv
            try
            {
                filePath = Path.Combine(_simulation.Engine.OutputFolder, _simulation.Input.Simulation.Name + "_traffic_output_" + simulationIndex + ".csv");
                File.WriteAllLines(filePath, output);
            }
            catch(Exception e)
            {
                Engine.Message(null, Engine.LogType.Warning, e.Message);
            }

            filePath = Path.Combine(_simulation.Engine.OutputFolder, _simulation.Input.Simulation.Name + "_trafficData_" + simulationIndex + ".tiff");
            SaveOutputMaps(filePath);
        }

        private void SaveOutputMaps(string filePath)
        {
            //usage map as geotiff
            try
            {
                int xDim = _usageMap.GetLength(0);
                int yDim = _usageMap.GetLength(1);

                using (OSGeo.GDAL.Driver driver = OSGeo.GDAL.Gdal.GetDriverByName("GTiff"))
                {
                    OSGeo.GDAL.Dataset output = driver.Create(filePath, xDim, yDim, 3, OSGeo.GDAL.DataType.GDT_Float32, null);

                    double leftX = _simulation.Spatial.UTMOrigin.x;
                    double lowerLeftY = _simulation.Spatial.UTMOrigin.y;
                    double[] geoTransform = new double[] { leftX, _simulation.Input.TrafficModule.SumoInput.OutputRasterSize, 0.0, lowerLeftY, 0.0, _simulation.Input.TrafficModule.SumoInput.OutputRasterSize };
                    output.SetGeoTransform(geoTransform);

                    OSGeo.OSR.SpatialReference reference = new OSGeo.OSR.SpatialReference("");
                    reference.SetProjCS("UTM " + _simulation.Spatial.UTMData.Zona + " (WGS84)");
                    reference.SetWellKnownGeogCS("WGS84");
                    reference.SetUTM(_simulation.Spatial.UTMData.ZoneNumber, _simulation.Input.Simulation.LowerLeftLatLon.x > 0 ? 1 : 0); ;
                    output.SetSpatialRef(reference);

                    //heat map
                    OSGeo.GDAL.Band band = output.GetRasterBand(1); //starts from 1, not zero                
                    band.SetNoDataValue(-9999f);
                    band.SetDescription("Heat map [s], accumulated time spent on a roads overlaying with the raster.");
                    double[] row = new double[xDim];
                    for (int y = 0; y < yDim; ++y)
                    {
                        for (int x = 0; x < xDim; ++x)
                        {
                            row[x] = _usageMap[x, y];
                            if (row[x] == 0f)
                            {
                                row[x] = -9999f;
                            }
                        }
                        band.WriteRaster(0, y, xDim, 1, row, xDim, 1, 0, 0);
                    }
                    band.FlushCache();

                    // average level of service
                    band = output.GetRasterBand(2);
                    band.SetNoDataValue(-9999f);
                    band.SetDescription("Average level of service (ratio of actual speed and speed limit.)");
                    for (int y = 0; y < yDim; ++y)
                    {
                        for (int x = 0; x < xDim; ++x)
                        {
                            //guard against divide-by-zero for untravelled cells (would produce NaN,
                            //which the == 0f check below does not catch, corrupting the raster).
                            if (_carCount[x, y] > 0)
                            {
                                row[x] = _accumulatedLevelOfService[x, y] / _carCount[x, y];
                                if (row[x] == 0f)
                                {
                                    row[x] = -9999f;
                                }
                            }
                            else
                            {
                                row[x] = -9999f;
                            }
                        }
                        band.WriteRaster(0, y, xDim, 1, row, xDim, 1, 0, 0);
                    }
                    band.FlushCache();

                    // average waiting time
                    band = output.GetRasterBand(3);
                    band.SetNoDataValue(-9999f);
                    band.SetDescription("Average waiting time [s].");
                    for (int y = 0; y < yDim; ++y)
                    {
                        for (int x = 0; x < xDim; ++x)
                        {
                            if (_carCount[x, y] > 0)
                            {
                                row[x] = _accumulatedWatingTime[x, y] / _carCount[x, y];
                                if (row[x] == 0f)
                                {
                                    row[x] = -9999f;
                                }
                            }
                            else
                            {
                                row[x] = -9999f;
                            }
                        }
                        band.WriteRaster(0, y, xDim, 1, row, xDim, 1, 0, 0);
                    }
                    band.FlushCache();
                    output.FlushCache();
                    //reminder, output.Close() crashes violently, do not use or investigate further why...
                }

            }
            catch (Exception e)
            {
                Engine.Message(null, Engine.LogType.Warning, e.Message);
            }
        }

        public override void UpdateDestinations()
        {
            foreach (SUMOVehicle vehicle in _sumoVehicles.Values)
            {
                if(vehicle.Destination.Blocked)
                {
                    RedirectVehicle(vehicle, false);                   
                }
            }
        }

        public void RedirectVehicle(SUMOVehicle vehicle, bool haveArrivedAtDestination)
        {
            EvacuationDestination newDestination;
            bool sameDestination = false;
            if (vehicle.Destination.Blocked)
            {
                newDestination = _simulation.Evacuation.GetBestAvailableDestination(vehicle.SimulationPos);
            }
            //this happens when a vehicle has arrived and the flow does not allow them to arrive, but the actual destination is not blocked
            else
            {
                sameDestination = true;
                newDestination = vehicle.Destination;
            }
            
            bool couldRedirect = false;
            if (newDestination != null)
            {
                try
                {
                    Vector2d sumoDestinationPos = newDestination.SimulationPos - _originOffset;
                    LIBSUMO.TraCIRoadPosition destinationEdge = LIBSUMO.Simulation.convertRoad(sumoDestinationPos.x, sumoDestinationPos.y);

                    //this means that from SUMOs point of view the vehicle is gone (it arrived in sumo as it is not aware of shelter capacity or blocked destinations), so we need to re-inject it
                    if (haveArrivedAtDestination)
                    {
                        Vector2d sumoVehiclePos = vehicle.SimulationPos - _originOffset;
                        LIBSUMO.TraCIRoadPosition startEdge = LIBSUMO.Simulation.convertRoad(sumoVehiclePos.x, sumoVehiclePos.y);

                        string routeID;
                        if (sameDestination)
                        {
                            routeID = $"preact_route_{vehicle.GetSumoVehicleID()}";
                        }
                        else
                        {
                            LIBSUMO.TraCIStage route = LIBSUMO.Simulation.findRoute(startEdge.edgeID, destinationEdge.edgeID);
                            routeID = $"preact_route_{vehicle.GetSumoVehicleID()}_{vehicle.ReinjectionCount}";
                            LIBSUMO.Route.add(routeID, route.edges);
                        }
                        
                        LIBSUMO.Vehicle.add(vehicle.GetSumoVehicleID(), routeID);//, vehicleType);
                    }
                    else
                    {
                        LIBSUMO.Vehicle.changeTarget(vehicle.GetSumoVehicleID(), destinationEdge.edgeID);
                    }                    
                    vehicle.UpdateDestination(newDestination);
                    couldRedirect = true; 
                }
                catch (Exception e)
                {
                    Engine.Message(_simulation, Engine.LogType.Warning, e.Message);
                }
            }

            if(!couldRedirect) //we are basically screwed, nowhere to go, sim hould stop on other end
            {

            }
        }

        //List<string>[,] fireCellEdges;
        Dictionary<CellIndex, HashSet<SumoEdge>> _cellsWithEdges;
        private void SortEdgesInFireCells()
        {
            if(!_simulation.Input.WildfireModule.Enabled)
            {
                Engine.Message(null, Engine.LogType.Log, "No wildfire module requested, won't sort SUMO network edges in fire cells.");
                return;
            }

            try
            {
                Vector2d wildfireOrigin = _simulation.Hazards.Wildfire.GetOriginOffset();
                double minXPos = wildfireOrigin.x - _originOffset.x; // now in sumo space
                double minYPos = wildfireOrigin.y - _originOffset.y;
                double cellSizeX = _simulation.Hazards.Wildfire.GetCellSizeX();
                double cellSizeY = _simulation.Hazards.Wildfire.GetCellSizeY();

                _cellsWithEdges = EdgeCellIntersection.SortEdgesIntoCells(_sumoConfig.Network.Edges, minXPos, minYPos, cellSizeX, cellSizeY, _simulation.Hazards.Wildfire.GetCellCountX(), _simulation.Hazards.Wildfire.GetCellCountY());
                Engine.Message(null, Engine.LogType.Log, "Number of fire cells that have road junctions and will affect traffic:" + _cellsWithEdges.Count);
            }
            catch (Exception e) 
            {
                Engine.Message(_simulation, Engine.LogType.SimulationError, e.Message);
            }            
        }

        

        public override void HandleIgnitedFireCells(List<Vector2int> cellIndices)
        {
            for (int i = 0; i < cellIndices.Count; i++)
            {
                FireCellIgnited(cellIndices[i].x, cellIndices[i].y);
            }
        }

        HashSet<SUMOVehicle> _carsToUpdate = new HashSet<SUMOVehicle>(100);
        private void FireCellIgnited(int x, int y)
        {
            _carsToUpdate.Clear();
            CellIndex ci = new CellIndex(x, y);

            if (_cellsWithEdges.TryGetValue(ci, out HashSet<SumoEdge> edges))
            {
                foreach(SumoEdge edge in edges)
                {
                    //https://sumo.dlr.de/docs/Simulation/Routing.html
                    //after testing this seems to be the best option
                    LIBSUMO.Edge.adaptTraveltime(edge.Id, double.MaxValue);

                    //collect cars in system that has the edge in their route
                    foreach (SUMOVehicle car in _sumoVehicles.Values)
                    {
                        try 
                        {
                            LIBSUMO.StringVector route = LIBSUMO.Vehicle.getRoute(car.GetSumoVehicleID());
                            if (route.Contains(edge.Id))
                            {
                                _carsToUpdate.Add(car);
                            }
                        }
                        catch (Exception e)
                        {
                            Engine.Message(_simulation, Engine.LogType.Warning, e.Message);
                        }                    
                    }
                }
            }

            if (_carsToUpdate.Count == 0)
            {
                //suppress for now, to much output to make sense of, better to save as damage output or something
                //Engine.Message(_simulation, Engine.LogType.Log, "Cell " + x + "," + y + " has been ignited and affects roads but did not affect any vehicles.");
            }
            else
            {
                Engine.Message(_simulation, Engine.LogType.Log, "Cell " + x + "," + y + " has been ignited and affects roads, notifying vehicles.");
            }

            //then do update for affected cars
            foreach (SUMOVehicle car in _carsToUpdate)
            {
                LIBSUMO.Vehicle.rerouteTraveltime(car.GetSumoVehicleID());
            }
        }    

        public override bool IsNetworkReachable(Vector2d pointLatLon)
        {
            throw new NotImplementedException();
        }

        private bool _closed;

        public override void Stop()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;
            try
            {
                LIBSUMO.Simulation.close();
            }
            catch (Exception e)
            {
                Engine.Message(_simulation, Engine.LogType.Log, "Could not stop SUMO. " + e.Message + ". " + e.InnerException);
            }
        }

        public override void SetManualDestination(List<TrafficModuleVehicle> vehicles, Vector2d simulationPos, EvacuationDestination evacuationDestination)
        {
            for (int i = 0; i < vehicles.Count; ++i)
            {
                try
                {
                    //IMPORTANT!!! Longitude then latitude in SUMO
                    Vector2d sumoPos = simulationPos - _originOffset;
                    LIBSUMO.TraCIRoadPosition destinationEdge = LIBSUMO.Simulation.convertRoad(sumoPos.x, sumoPos.y);
                    LIBSUMO.Vehicle.changeTarget(((SUMOVehicle)vehicles[i]).GetSumoVehicleID(), destinationEdge.edgeID);
                    vehicles[i].UpdateDestination(evacuationDestination);
                }
                catch (Exception e)
                {
                    Engine.Message(null, Engine.LogType.Warning, e.Message);
                }
            }
        }
    }
}
