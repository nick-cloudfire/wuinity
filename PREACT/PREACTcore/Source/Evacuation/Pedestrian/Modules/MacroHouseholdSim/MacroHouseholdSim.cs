//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using PREACT.Evacuation;
using System.Numerics;
using PREACT.Visualization;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Pedestrian
{
    /// <summary>
    /// Simple human evacuation simulator that lumps households of people into one unit.
    /// Household position is randomized within a cell that has been discretized from the world plane.
    /// </summary>
    [System.Serializable]
    public class MacroHouseholdSim : PedestrianModule
    {
        List<MacroHousehold> _macroHouseholds;
        int totalPopulation;
        int totalCars;
        int totalCarsReached;
        int totalPeopleWhoWillNotEvacuate;
        private bool evacuationDone = false;
        int peopleLeft;
        List<string> output;

        int totalHouseholds;
        Vector4[] householdPositions;
        int totalHouseholdsResponded = 0, totalHouseholdsReachedCar = 0;
        int totalPeopleReachedCar = 0;
        int totalPeopleResponded = 0;



        public MacroHouseholdSim(Simulation simulation) : base(simulation)
        {
            PopulateSimulation(simulation.Input.Population.Data.Households);
            output = new List<string>();
            output.Add("Time(s),Households left,People left,Total households responded, Total people responded,Total households reached car,Total people reached car,Total cars activated,Avg. walking dist.");
        }

        public override int GetTotalPopulation()
        {
            return totalPopulation;
        }

        public override int GetTotalHouseHolds()
        {
            return totalHouseholds; 
        }

        /// <summary>
        /// Advances the macro human simulation.
        /// Loops through all cells and all households per cell and check if they have reached their goal (car)
        /// </summary>
        /// <param name="deltaTime"></param>
        /// <param name="currentTime"></param>
        public override void Step(double currentTime, double deltaTime)
        {            
            if (!evacuationDone)
            {                
                float walkingDistance = 0f;
                int householdsDone = 0;
                int householdIndex = 0;
                int totalHouseholdsLeft = 0; 

                for (int i = 0; i < _macroHouseholds.Count; ++i)
                {
                    MacroHousehold household = _macroHouseholds[i];

                    //update position array for visualization
                    householdPositions[householdIndex] = household.GetPositionAndState(currentTime);
                    ++householdIndex;

                    //if evac time is float.MaxValue they have decided to stay forever
                    if (household.evacuationTime < float.MaxValue)
                    {
                        if(!household.reachedCar)
                        {                                                                      
                            //see if the household has reacted yet, if so, set them in motion
                            if (!household.isMoving && household.ResponseTime <= currentTime)
                            {
                                household.isMoving = true;
                                ++totalHouseholdsResponded;
                                totalPeopleResponded += household.peopleInHousehold;
                            }

                            //if we yet have not reached our car, check if we have
                            if (household.evacuationTime <= currentTime)
                            {
                                ReachedCar(household);
                                totalPeopleReachedCar += household.peopleInHousehold;
                                ++totalHouseholdsReachedCar;
                                totalCarsReached += household.cars;
                            }
                            else
                            {
                                ++totalHouseholdsLeft;
                            }
                        } 
                        else
                        {
                            walkingDistance += household.walkingDistance;
                            ++householdsDone;
                        }
                    }
                    else
                    {
                        totalHouseholdsLeft++;                                
                    }
                }

                peopleLeft = totalPopulation - totalPeopleReachedCar;
                if (peopleLeft - totalPeopleWhoWillNotEvacuate == 0)
                {
                    evacuationDone = true;
                }

                float avgWalkDist = 0f;
                if (householdsDone > 0)
                {
                    avgWalkDist = walkingDistance / householdsDone;
                }

                //Time(s),Households left,People left,Total households responded, Total people responded,Total households reached car,Total people reached car,Total cars activated,Avg. walking dist.
                output.Add(System.FormattableString.Invariant($"{currentTime},{totalHouseholdsLeft},{peopleLeft},{totalHouseholdsResponded},{totalPeopleResponded},{totalHouseholdsReachedCar},{totalPeopleReachedCar},{totalCarsReached},{avgWalkDist}"));
                //string output = currentTime + "," +  peopleWhoReachedCar";
                //SaveToFile(output, false);
            }
        }

        public override bool IsSimulationDone()
        {
            return evacuationDone;
        }

        public Vector4[] GetHouseholdPositions()
        {
            return householdPositions;
        }

        public override int GetPeopleLeft()
        {
            return peopleLeft;
        }

        public override int GetPeopleStaying()
        {
            return totalPeopleWhoWillNotEvacuate;
        }

        public override int GetTotalCars()
        {
            return totalCars;
        }

        public override int GetCarsReached()
        {
            return totalCarsReached;
        }

        private void ReachedCar(MacroHousehold household)
        {
            if(_simulation.Input.TrafficModule.Enabled)
            {
                //assume all cars in household goes to the same goal, else we have to make a new call to select goal for every car
                EvacuationDestination evacDest = _simulation.Evacuation.GetEvacuationDestination(household.GetVehicleLatLon(), household.EvacuationGroup);

                if (evacDest != null && evacDest.Blocked)
                {
                    //chosen destination is blocked — redirect to the best available one
                    EvacuationDestination replacement = _simulation.Evacuation.GetBestAvailableDestination(household.EvacuationGroup, household.GetVehicleLatLon());
                    if (replacement != null)
                    {
                        evacDest = replacement;
                    }
                }

                Vector2d vehicleLatLon = household.GetVehicleLatLon();
                if (household.cars > 1)
                {
                    int peopleLeftInHousehold = household.peopleInHousehold;
                    int carIndex = 0;
                    int[] peopleInCar = new int[household.cars];

                    while(peopleLeftInHousehold > 0)
                    {
                        ++peopleInCar[carIndex];
                        ++carIndex;
                        if(carIndex > household.cars - 1)
                        {
                            carIndex = 0;
                        }
                        --peopleLeftInHousehold;
                    }

                    for (int i = 0; i < household.cars; i++)
                    {
                        _simulation.Evacuation.InsertNewCar(vehicleLatLon, evacDest, (uint)peopleInCar[i]);
                    }
                }
                else
                {
                    _simulation.Evacuation.InsertNewCar(vehicleLatLon, evacDest, (uint)household.peopleInHousehold);
                }
            }
            
            household.reachedCar = true;
        }

        public void SaveToFile(string file)
        {            
            System.IO.File.WriteAllLines(file, output);
        }

        public void PopulateSimulation(PopulationData.HouseholdData[] householdData)
        {       
            totalPopulation = 0;
            totalHouseholds = householdData.Length;

            for (int i = 0; i < householdData.Length; i++)
            {
                totalPopulation += householdData[i].peopleCount;
            }    

            _macroHouseholds = new List<MacroHousehold>();
            int culledHouseholdsOutsideDomain = 0;
            int culledPeopleOutsideDomain = 0;

            int culledHouseholdsOutsideGroups = 0;           
            int culledPeopleOutsideGroups = 0;
            for (int i = 0; i < householdData.Length; ++i)
            {
                Vector2d pos = _simulation.Spatial.GetSimulationPosition(householdData[i].originLatLon);

                //check that we are inside
                if(pos.x >= 0.0 && pos.x <= _simulation.Input.Simulation.DomainSize.x && pos.y >= 0.0 && pos.y <= _simulation.Input.Simulation.DomainSize.y)
                {
                    bool insideGroup;
                    EvacuationGroup eG = _simulation.Evacuation.GetEvacuationGroup(householdData[i].originLatLon, out insideGroup);
                    if(!insideGroup && _simulation.Input.Population.CullOutsideGroups)
                    {
                        totalPopulation -= householdData[i].peopleCount;
                        --totalHouseholds;
                        ++culledHouseholdsOutsideGroups;
                        culledPeopleOutsideGroups += householdData[i].peopleCount;
                    }
                    else
                    {
                        MacroHousehold mH = new MacroHousehold(householdData[i], GetRandomWalkingSpeed(), eG, _simulation);
                        _macroHouseholds.Add(mH);
                    }                        
                }
                else
                {
                    totalPopulation -= householdData[i].peopleCount;
                    --totalHouseholds;
                    ++culledHouseholdsOutsideDomain;
                    culledPeopleOutsideDomain += householdData[i].peopleCount;
                }
            }

            //A warning only when someone was actually left out: "culled ... 0/0" as a WARNING on every run of a scenario
            //whose population all lies inside (Lytton) reads as a problem that is not there (e2e N6).
            Engine.Message(_simulation, culledHouseholdsOutsideGroups > 0 ? Engine.LogType.Warning : Engine.LogType.Log,
                $"Number of households/people culled outside of defined groups: {culledHouseholdsOutsideGroups}/{culledPeopleOutsideGroups}");
            Engine.Message(_simulation, culledHouseholdsOutsideDomain > 0 ? Engine.LogType.Warning : Engine.LogType.Log,
                $"Number of households/people culled outside of defined simulation domain: {culledHouseholdsOutsideDomain}/{culledPeopleOutsideDomain}");

            //sum up the number of people which will not evacuate and total cars
            totalPeopleWhoWillNotEvacuate = 0;
            totalCars = 0;
            for (int i = 0; i < _macroHouseholds.Count; ++i)
            {
                if (_macroHouseholds[i].ResponseTime < float.MaxValue)
                {
                    totalCars += _macroHouseholds[i].cars;
                }
                else
                {
                    totalPeopleWhoWillNotEvacuate += _macroHouseholds[i].peopleInHousehold;
                }
            }

            householdPositions = new Vector4[totalHouseholds];
            peopleLeft = totalPopulation;

            Engine.Message(_simulation, Engine.LogType.Log, " Total active population: " + totalPopulation);
            Engine.Message(_simulation, Engine.LogType.Log, " Total active households: " + totalHouseholds);
            Engine.Message(_simulation, Engine.LogType.Log, " Total active cars: " + totalCars);
            Engine.Message(_simulation, Engine.LogType.Log, " Total people who will not evacuate: " + totalPeopleWhoWillNotEvacuate);
        }

        /// <summary>
        /// Gets random walking speed based on user input range
        /// </summary>
        /// <returns></returns>
        public float GetRandomWalkingSpeed()
        {
            MacroHouseholdSimInput eO = _simulation.Input.PedestrianModule.MacroHouseholdSimInput;
            return Random.Range(eO.WalkingSpeedMinMax.X, eO.WalkingSpeedMinMax.Y) * eO.WalkingSpeedModifier;
        }

        int _householdsStartedByFire;
        int _peopleStartedByFire;
        int _frontVersionSeen = -1;

        /// <summary>Households that left because the fire came within the reaction distance, not on their own time.</summary>
        public int HouseholdsStartedByFire { get => _householdsStartedByFire; }

        /// <summary>
        /// Starts every household that has not left yet and whose home is within
        /// <see cref="MacroHouseholdSimInput.FireReactionDistance"/> of the fire as it is now - the cells the fire
        /// has reached by <paramref name="simulationTime"/>.
        /// </summary>
        /// <remarks>
        /// Three things made this inert before, so every departure came from the response curve alone:
        /// <list type="bullet">
        /// <item>it looked up the distance at the home's <b>lat/lon in degrees</b> as if they were simulation
        /// metres, which is one cell near the grid's south-west corner for every household;</item>
        /// <item><see cref="MacroHousehold.StartEvacuation"/> postponed the household instead of starting it;</item>
        /// <item>the distance field was built from the fire's final rate-of-spread raster - everywhere it would
        /// ever burn - rather than from where it had burned so far.</item>
        /// </list>
        /// A household whose drawn response is "never" (the part of the curve above its last probability) is left
        /// alone: that share is the modelled population that stays. Nothing is done when the fire field has not
        /// changed since the last call.
        /// </remarks>
        public override void ReactToWildfire(double simulationTime)
        {
            MacroHouseholdSimInput settings = _simulation.Input.PedestrianModule.MacroHouseholdSimInput;
            if (!settings.ReactToFire || evacuationDone)
            {
                return;
            }

            int version = _simulation.Hazards.WildfireFrontVersion;
            if (version == _frontVersionSeen || version == 0)
            {
                return;
            }
            _frontVersionSeen = version;

            int started = 0;
            for (int i = 0; i < _macroHouseholds.Count; ++i)
            {
                MacroHousehold household = _macroHouseholds[i];
                if (household.isMoving || household.reachedCar || household.ResponseTime == float.MaxValue
                    || household.ResponseTime <= simulationTime)
                {
                    continue;
                }

                float distance = _simulation.Hazards.DistanceToWildfire(household.SimulationHomePosition);
                if (distance <= settings.FireReactionDistance && household.StartEvacuation(simulationTime))
                {
                    ++totalHouseholdsResponded;
                    totalPeopleResponded += household.peopleInHousehold;
                    ++_householdsStartedByFire;
                    _peopleStartedByFire += household.peopleInHousehold;
                    ++started;
                }
            }

            if (started > 0)
            {
                Engine.Message(_simulation, Engine.LogType.Log, $"The fire came within {settings.FireReactionDistance} m of {started} household(s), which left ahead of their response time ({_householdsStartedByFire} so far).");
            }
        }

        public override void Stop()
        {
            Engine.Message(_simulation, Engine.LogType.Log, $"Households that left because of the fire's proximity: {_householdsStartedByFire} ({_peopleStartedByFire} people).");
        }
    }
}