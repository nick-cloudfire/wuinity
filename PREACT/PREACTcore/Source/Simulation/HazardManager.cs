using PREACT.Math;
using PREACT.Wildfire;
using PREACT.Dispersion;
using PREACT.Input;
using System.Collections.Generic;

namespace PREACT
{
    /// <summary>
    /// This class is supposed to collect all the communication between different sub-modules, 
    /// e.g. traffic simulation needing information from the smoke or fire simulation.
    /// This is done to not clutter up the simulation class itself.
    /// </summary>
    public class HazardManager
    {
        private WildfireModule _wildfire;
        private SmokeModule _smoke;
        private Simulation _simulation;

        //data products
        float[,] _wildfireFrontDistance;

        public WildfireModule Wildfire { get => _wildfire; }
        public SmokeModule Smoke { get => _smoke; }

        public HazardManager(Simulation simulation)
        {
            _simulation = simulation;
        }

        public void PostStep(float simulationTime)
        {
            CalculateWildfireDistanceTransform(simulationTime);
        }
                
        private void CalculateWildfireDistanceTransform(float simulationTime)
        {
            //_wildfire is null when no wildfire module is enabled (e.g. a
            //traffic- or pedestrian-only run); PostStep still runs every step.
            if(_wildfire == null || !_wildfire.Ignited() ||  (int)simulationTime % 300 != 0)
            {
                return;
            }

            float[,] front = _wildfire.GetMaxROS();
            if(_wildfireFrontDistance == null)
            {
                int xDim = front.GetLength(0);
                int yDim = front.GetLength(1);
                _wildfireFrontDistance = new float[xDim, yDim]; 
                for(int j = 0; j < yDim; ++j)
                {
                    for (int i = 0; i < xDim; ++i)
                    {
                        _wildfireFrontDistance[i, j] = float.MaxValue;
                    }
                }
                
            }
            Utility.Analysis.EuclideanDistanceTransform.ComputeEDT(front, _wildfireFrontDistance, _wildfire.GetCellSizeX(), _wildfire.GetCellSizeY(), 0f); 
        }

        public float DistanceToWildfire(Vector2d simulationPos)
        {
            float distance = float.MaxValue;

            Vector2int cellIndex = _simulation.Spatial.GetWildfireCellIndex(simulationPos, out bool inside);
            if(inside && _wildfireFrontDistance != null)
            {
                distance = _wildfireFrontDistance[cellIndex.x, cellIndex.y];
            }

            return distance;
        }

        public List<SimulationModule> CreateModules(WeatherManager weather, TimeManager time, out bool success)
        {
            List<SimulationModule> createdModules = new List<SimulationModule>();

            CreateWildfireModule(_simulation, _simulation.Input, weather, time, out success);
            if(success && _wildfire != null)
            {
                createdModules.Add(_wildfire);
            }
            else
            {
                return createdModules;
            }
            
            CreateSmokeModule(_simulation, _simulation.Input, weather, time, out success);
            if (success && _smoke != null)
            {
                createdModules.Add(_smoke);
            }
            else
            {
                return createdModules;
            }

            return createdModules;
        }

        /// <summary>
        /// Runs ELMFIRE for this scenario and reads the fire it produced.
        ///
        /// ELMFIRE is a batch program, so there is no stepping it alongside the evacuation: it computes the
        /// whole fire, writes rasters, and exits. Once it has, those rasters are a pre-computed fire - which
        /// is exactly what <see cref="AscFireImport"/> reads - so the paths are written into the AscImport
        /// settings and that reader is used, rather than a second implementation of the same thing. An
        /// ELMFIRE fire and an imported one are therefore the same code from here on.
        ///
        /// This blocks for as long as ELMFIRE takes, which is minutes on a real domain. Output for an
        /// unchanged case is reused, so the wait falls on the first run rather than on every one.
        /// </summary>
        private bool CreateElmfireModule(Simulation simulation, PREACTInput input)
        {
            Engine.Message(simulation, Engine.LogType.Log,
                "Running ELMFIRE for this scenario. The simulation starts once it has finished - this takes "
                + "minutes the first time, and reuses the result afterwards.");

            Utility.ElmfireCoupling.Result fire = Utility.ElmfireCoupling.Prepare(
                input, input.WildfireModule.ElmfireInput,
                message => Engine.Message(simulation, Engine.LogType.Log, message));

            if (!fire.Ok)
            {
                Engine.Message(simulation, Engine.LogType.SimulationError, "ELMFIRE did not produce a fire: " + fire.Message);
                return false;
            }

            //Written into the AscImport settings because that is where the reader looks. Done here rather
            //than in the scenario, so nothing is saved: these paths are an artefact of this run.
            AscImportInput asc = input.WildfireModule.AscImportInput;
            asc.StartDateTime = input.Simulation.StartDateTime;
            asc.TimeOfArrivalFile = fire.TimeOfArrivalFile;
            asc.RateOfSpreadFile = fire.RateOfSpreadFile;
            asc.SpreadDirectionFile = fire.SpreadDirectionFile;
            asc.FirelineIntensityFile = fire.FirelineIntensityFile;

            //k-PERIL gets the same wind the fire was computed with, unless the scenario names its own. Two
            //wind fields for one fire is a disagreement waiting to happen: k-PERIL derives how elongated
            //spread is from this, so a field belonging to another case, another hour or another grid changes
            //the boundary without changing anything visible.
            Input.kPERILInput peril = input.TriggerBufferModule.kPERILInput;
            if (!string.IsNullOrEmpty(fire.WindSpeedFile) && string.IsNullOrEmpty(peril.WindSpeedFile))
            {
                peril.WindSpeedFile = fire.WindSpeedFile;
                peril.WindDirectionFile = fire.WindDirectionFile;
                Engine.Message(simulation, Engine.LogType.Log,
                    "The trigger boundary will use the ELMFIRE case's own wind rasters (" + fire.WindSpeedFile
                    + ", " + fire.WindDirectionFile + ").");
            }

            _wildfire = new AscFireImport(simulation);
            Engine.Message(simulation, Engine.LogType.Log,
                fire.Reused
                    ? "Wildfire module ELMFIRE initiated from output already in the case folder."
                    : "Wildfire module ELMFIRE initiated from a fresh ELMFIRE run.");
            return true;
        }

        private void CreateWildfireModule(Simulation simulation, PREACTInput input, WeatherManager weather, TimeManager time, out bool success)
        {
            success = false;

            if (input.WildfireModule.Enabled)
            {
                if (input.WildfireModule.Module == WildfireModuleInput.WildfireModules.AscImport)
                {
                    _wildfire = new AscFireImport(simulation);
                    Engine.Message(simulation, Engine.LogType.Log, $"Wildfire module {nameof(AscFireImport)} initiated.");
                }
                else if (input.WildfireModule.Module == WildfireModuleInput.WildfireModules.ELMFIRE)
                {
                    if (!CreateElmfireModule(simulation, input))
                    {
                        return;
                    }
                }
                else
                {
                    Engine.Message(simulation, Engine.LogType.SimulationError, "Could not initiate wildfire module, aborting.");
                }
            }
            else
            {

                success = true;
                Engine.Message(simulation, Engine.LogType.Log, "No fire module was enabled.");
            }

            if(_wildfire != null)
            {
                success = true;
            }
        }

        private void CreateSmokeModule(Simulation simulation, PREACTInput input, WeatherManager weather, TimeManager time, out bool success)
        {
            success = false;

            if (input.SmokeModule.Enabled)
            {
                if (input.SmokeModule.Module == SmokeInput.SmokeModules.GlobalSmoke)
                {
                    _smoke = new GlobalSmoke(simulation, input.SmokeModule.Data.ExtinctionRamp);
                }
            }
            else
            {
                success = true;
                Engine.Message(simulation, Engine.LogType.Log, "No smoke module was enabled.");
            }

            if (_smoke != null)
            {
                success = true;
            }
        }

        /// <summary>
        /// Returns optical density at ground level and location in simulation space.
        /// </summary>
        /// <returns></returns>
        public float GetExtinctionCoefficientAtPos(Vector2d pos)
        {
            float result = 0f;
            if(_smoke != null)
            {
                result = _smoke.GetSootDensityAtPos(pos) * 8700f; //TODO: user specified mass specific extinction coefficient
            }

            return result;
        }
    }
}
