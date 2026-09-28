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
                
        /// <summary>When the distance-to-fire field was last recomputed. The transform is O(cells) and the
        /// front moves slowly next to the timestep; the interval is [MacroHouseholdSim] FireReactionUpdateInterval.</summary>
        private float _lastDistanceTransformTime = float.NegativeInfinity;
        private float[,] _burnedSoFar;
        private int _frontVersion;

        /// <summary>
        /// Increases every time the distance-to-fire field is recomputed; 0 until the fire has burned anything.
        /// Lets a consumer skip work when the field has not changed.
        /// </summary>
        public int WildfireFrontVersion { get => _frontVersion; }

        /// <summary>
        /// Distance from every fire cell to the fire as it is <b>now</b>: the cells whose arrival time is at or
        /// before <paramref name="simulationTime"/>.
        /// </summary>
        /// <remarks>
        /// This used to feed the transform the final rate-of-spread raster - every cell the fire would reach by
        /// the end of the run - and recompute that same static field every five minutes, so from the first burning
        /// sweep every household within the reaction distance of anywhere the fire would ever go counted as
        /// threatened.
        /// </remarks>
        private void CalculateWildfireDistanceTransform(float simulationTime)
        {
            //_wildfire is null when no wildfire module is enabled (e.g. a
            //traffic- or pedestrian-only run); PostStep still runs every step.
            if (_wildfire == null || !_wildfire.Ignited())
            {
                return;
            }

            //Elapsed time, not (int)simulationTime % 300 == 0. That only fires when the clock lands exactly
            //on a multiple of 300, so it depended on the timestep dividing 300.
            float interval = _simulation.Input.PedestrianModule.MacroHouseholdSimInput.FireReactionUpdateInterval;
            if (simulationTime - _lastDistanceTransformTime < interval)
            {
                return;
            }
            _lastDistanceTransformTime = simulationTime;

            int xDim = _wildfire.GetCellCountX();
            int yDim = _wildfire.GetCellCountY();
            if (_wildfireFrontDistance == null)
            {
                _wildfireFrontDistance = new float[xDim, yDim];
                _burnedSoFar = new float[xDim, yDim];
            }

            int burned = 0;
            for (int x = 0; x < xDim; ++x)
            {
                for (int y = 0; y < yDim; ++y)
                {
                    float arrival = _wildfire.GetTimeOfArrival(x, y);
                    bool hasBurned = arrival != float.MaxValue && arrival <= simulationTime;
                    _burnedSoFar[x, y] = hasBurned ? 1f : 0f;
                    burned += hasBurned ? 1 : 0;
                }
            }

            if (burned == 0)
            {
                return;
            }

            Utility.Analysis.EuclideanDistanceTransform.ComputeEDT(_burnedSoFar, _wildfireFrontDistance, _wildfire.GetCellSizeX(), _wildfire.GetCellSizeY(), 0f);
            ++_frontVersion;
        }

        /// <summary>
        /// Distance in metres from a simulation position to the nearest cell the fire has reached so far, as of
        /// the last update; <see cref="float.MaxValue"/> outside the fire grid or before anything has burned.
        /// </summary>
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

        /// <summary>
        /// Creates the fire and smoke modules, each on its own: either may be enabled without the other.
        /// </summary>
        /// <remarks>
        /// This used to return as soon as there was no fire module, before the smoke module was even looked at -
        /// so a smoke-only scenario (GlobalSmoke needs no fire) silently ran without smoke.
        /// </remarks>
        public List<SimulationModule> CreateModules(WeatherManager weather, TimeManager time, out bool success)
        {
            List<SimulationModule> createdModules = new List<SimulationModule>();

            CreateWildfireModule(_simulation, _simulation.Input, weather, time, out bool fireOk);
            if (fireOk && _wildfire != null)
            {
                createdModules.Add(_wildfire);
            }

            bool smokeOk = false;
            if (fireOk)
            {
                CreateSmokeModule(_simulation, _simulation.Input, weather, time, out smokeOk);
                if (smokeOk && _smoke != null)
                {
                    createdModules.Add(_smoke);
                }
            }

            success = fireOk && smokeOk;
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

            //ELMFIRE writes the simulation clock straight into time_of_arrival, in seconds. The reader's
            //other sources - FARSITE, FlamMap, Prometheus - use minutes, which is its default, so this has to
            //be said or the fire arrives 60 times too late.
            asc.TimeOfArrivalUnits = AscImportInput.TimeUnits.Seconds;

            asc.TimeOfArrivalFile = fire.TimeOfArrivalFile;
            asc.RateOfSpreadFile = fire.RateOfSpreadFile;
            asc.SpreadDirectionFile = fire.SpreadDirectionFile;
            asc.FirelineIntensityFile = fire.FirelineIntensityFile;

            //Display only, and the reason the output window's fuel model mode showed nothing: the reader had no
            //fuel raster to hand it, since an imported fire needs none to spread.
            asc.FuelModelFile = fire.FuelModelFile;

            //The weather the fire was actually computed against. ELMFIRE's weather comes from a historical peak
            //fire-weather day drawn out of the ERA5 record, while the scenario is dated whenever the evacuation
            //is being modelled - so the temperature, humidity and fire-danger indices the platform reported were
            //from a date the fire knew nothing about. Rebasing here rather than at construction because the
            //weather manager exists before this module does, and building the case is what draws the day.
            if (fire.WeatherAnchor != default)
            {
                string archive = string.IsNullOrEmpty(fire.WeatherArchiveFile) ? null : fire.WeatherArchiveFile;
                simulation.Weather.Rebase(fire.WeatherAnchor, archive, simulation.Time);
            }

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
                if (input.SmokeModule.Module == SmokeInput.SmokeModules.GlobalSmoke && input.SmokeModule.Data.ExtinctionRamp != null)
                {
                    _smoke = new GlobalSmoke(simulation, input.SmokeModule.Data.ExtinctionRamp);
                    Engine.Message(simulation, Engine.LogType.Log, "Smoke module GlobalSmoke initiated.");
                }
                else
                {
                    Engine.Message(simulation, Engine.LogType.SimulationError, "The smoke module is enabled but could not be created: "
                        + (input.SmokeModule.Module == SmokeInput.SmokeModules.GlobalSmoke ? "the extinction ramp was not loaded." : "no smoke module is chosen."));
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
        /// Light extinction coefficient (1/m) at ground level at a position in simulation space; 0 without smoke.
        /// </summary>
        public float GetExtinctionCoefficientAtPos(Vector2d pos)
        {
            return _smoke != null ? _smoke.GetExtinctionCoefficientAtPos(pos) : 0f;
        }
    }
}
