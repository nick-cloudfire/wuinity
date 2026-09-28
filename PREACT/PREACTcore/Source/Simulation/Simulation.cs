//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using PREACT.Evacuation;
using PREACT.Input;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
using PREACT.Output;

namespace PREACT
{
    [System.Serializable]
    public class Simulation
    {
        public enum SimulationState { Initializing, Running, Completed, Error };

        //References
        private Engine _engine;
        private volatile SimulationState _state;
        private PREACTInput _input;
        private SimulationOutput _output;
        private TimeManager _time;
        private WeatherManager _weather;
        private SpatialManager _spatial;
        private EvacuationManager _evacuation;
        private HazardManager _hazards;

        private List<SimulationModule> _simulationModules = new List<SimulationModule>();
        private Stopwatch[] _moduleStopwatches;

        private Stopwatch _simulationStopwatch = new Stopwatch();
        private Stopwatch _modulesStopwatch = new Stopwatch();
        private Stopwatch _weatherStopwatch = new Stopwatch();

        //Data
        private int _simulationIndex;
        private volatile bool _isRunning;
        private volatile bool _isPaused = false;
        private volatile bool _stopRun = false;
        private bool _haveResults = false;
        private float _stepExecutionTime;

        [System.ThreadStatic] private static Simulation _runningOnThisThread;

        /// <summary>
        /// The simulation whose <see cref="Run"/> is executing on the calling thread, or null. Lets a message
        /// reported from deep inside a run (with no simulation to hand) be attributed to - and stop - that run
        /// alone.
        /// </summary>
        public static Simulation RunningOnThisThread { get => _runningOnThisThread; }

        //References
        public Engine Engine { get => _engine; }
        public SimulationState State { get => _state; }
        public PREACTInput Input { get => _input; }
        public SimulationOutput Output { get => _output; }
        public TimeManager Time { get => _time; }
        public WeatherManager Weather { get => _weather; }
        public SpatialManager Spatial { get => _spatial; }
        public EvacuationManager Evacuation { get => _evacuation; }
        public HazardManager Hazards { get => _hazards; }

        //Data
        public int SimulationIndex { get => _simulationIndex; }
        public bool IsPaused { get => _isPaused; }
        public bool IsRunning { get => _isRunning; }
        public bool HaveResults { get => _haveResults; }
        public float StepExecutionTime { get => _stepExecutionTime; }


        public Simulation(Engine engine, PREACTInput input, int simulationIndex)
        {
            _engine = engine;
            _simulationIndex = simulationIndex;
            _input = input;
            _output = new SimulationOutput(this);
            _time = new TimeManager(_input, this);
            _spatial = new SpatialManager(this);
            _weather = new WeatherManager(this, _time);
            _hazards = new HazardManager(this);
            _evacuation = new EvacuationManager(this);
        }

        /// <summary>
        /// Starts and runs the simulation until completed or halted.
        /// </summary>
        /// <remarks>
        /// Always leaves the simulation not running, whatever happens: an exception from a module used to escape
        /// with <see cref="IsRunning"/> still true, which locked the GUI's menus until restart. An exception now
        /// stops the run at once (it used to be logged on every step, 86 400 stack traces for a day at 1 s) with
        /// the state set to <see cref="SimulationState.Error"/>, and the modules are stopped so SUMO is closed.
        /// </remarks>
        public void Run()
        {
            _isRunning = true;
            _state = SimulationState.Initializing;
            Simulation previous = _runningOnThisThread;
            _runningOnThisThread = this;

            try
            {
                //Before anything draws. Every draw of the run - departure times, walking speeds, household
                //sizes, destination choice, SUMO start positions - now happens on this thread, since the modules
                //are stepped here in a fixed order, so the scenario's RandomSeed reproduces the run. They used to
                //step on job-system worker threads whose generators were never seeded.
                Math.Random.SeedForSimulation(Input.Simulation.RandomSeed, _simulationIndex);

                PreRun();

                //A failed setup ends the run here, as an error: nothing below may read the modules it did not make.
                if (_stopRun)
                {
                    _state = SimulationState.Error;
                    _stoppedDueToError = true;
                    Engine.Message(this, Engine.LogType.SimulationError,
                        "Simulation " + _simulationIndex + " did not start: see the errors above. No results were produced.");
                    StopModules();
                    return;
                }

                //actual time step loop
                _state = SimulationState.Running;
                _haveResults = true;
                while (!_stopRun)
                {
                    if (_isPaused)
                    {
                        Thread.Sleep(100);
                    }
                    else
                    {
                        Step();
                    }
                }

                //Judged on the time loop: a failure in the post-processing (no arrivals for k-PERIL, say) is
                //reported, and counted against the run, but the simulated results exist and stay viewable.
                bool loopFailed = _stoppedDueToError;
                PostRun();
                _state = loopFailed ? SimulationState.Error : SimulationState.Completed;
            }
            catch (System.Exception e)
            {
                _stoppedDueToError = true;
                _stopRun = true;
                _state = SimulationState.Error;
                Engine.Message(this, Engine.LogType.Exception, "Simulation " + _simulationIndex + " stopped on an error: " + e.Message
                    + System.Environment.NewLine + e.StackTrace);
                StopModules();
            }
            finally
            {
                _isRunning = false;
                _runningOnThisThread = previous;
            }
        }

        /// <summary>
        /// Sets up all modules and timing of simulation.
        /// </summary>
        private void PreRun()
        {
            _simulationStopwatch.Restart();
            _stopRun = false;
            _stoppedDueToError = false;

            Engine.Message(this, Engine.LogType.Log, "Simulation  " + _simulationIndex + " started, please wait.");

            CreateSimulationModules();
        }

        bool _runRealtime = false;

        /// <summary>
        /// One time step: weather, then every module in creation order (fire, smoke, pedestrians, traffic), then
        /// the cross-module updates.
        /// </summary>
        /// <remarks>
        /// Sequential, on this thread. The modules used to run concurrently on a job system, which bought little -
        /// the pedestrian and fire steps are small next to SUMO's - and cost determinism (unseeded worker-thread
        /// random streams, a pedestrian step adding cars while SUMO stepped) and robustness (a throwing module was
        /// logged and skipped every step instead of stopping the run). Traffic is stepped even when it has no car
        /// in it, so SUMO's clock never lags the simulation's and a car added after a lull departs at the time
        /// it was added rather than at SUMO's stale "now".
        /// </remarks>
        private void Step()
        {
            long startTime = _simulationStopwatch.ElapsedMilliseconds;

            //update weather for current time step
            _weatherStopwatch.Start();
            _weather.Update(_time.CurrentDateTime);
            _weatherStopwatch.Stop();

            //step all modules forward in time
            double deltaTime = _input.Simulation.DeltaTime;
            double now = _time.SimulationTime;
            _modulesStopwatch.Start();
            for (int i = 0; i < _simulationModules.Count && !_stopRun; ++i)
            {
                SimulationModule module = _simulationModules[i];
                if (module.StepWhenDone || !module.IsSimulationDone())
                {
                    Stopwatch stopwatch = _moduleStopwatches[i];
                    stopwatch.Start();
                    module.Step(now, deltaTime);
                    stopwatch.Stop();
                }
            }
            _modulesStopwatch.Stop();

            //advance time
            _time.Step(deltaTime);

            //deal with what has happen during time step
            PostStep();

            //see if we are done or not
            CheckCompletion();
            UpdatePerformanceTimer(startTime, deltaTime);
        }

        private void PostStep()
        {
            _hazards.PostStep((float)_time.SimulationTime);
            _evacuation.PostStep();
        }

        private void CheckCompletion()
        {
            if (_stopRun)
            {
                return;
            }

            bool endTimeReached = _time.SimulationEndTime - _time.SimulationTime < 0.001;

            if (endTimeReached)
            {
                Stop("Simulation has reached specified end time.", false);
            }

            if (!_stopRun && _input.Simulation.StopWhenEvacuated)
            {
                bool pedestrianDone = true;
                if (_input.PedestrianModule.Enabled)
                {
                    pedestrianDone = _evacuation.PedestrianModule.IsSimulationDone();
                }
                bool trafficDone = true;
                if (_input.TrafficModule.Enabled)
                {
                    trafficDone = _evacuation.TrafficModule.IsSimulationDone();
                }

                if (pedestrianDone && trafficDone)
                {
                    Stop("Both pedestrian and traffic simulations are completed, stopping as per user settings.", false);
                }
            }
        }

        private void UpdatePerformanceTimer(long startTime, double deltaTime)
        {
            //just some stuff for controlling execution mode and timing performance
            long timeSpent = _simulationStopwatch.ElapsedMilliseconds - startTime;
            if (_runRealtime)
            {
                int sleepTime = (int)(deltaTime * 1000) - (int)timeSpent;
                if (sleepTime > 0)
                {
                    Thread.Sleep(sleepTime);
                }
            }

            if(timeSpent > 100) //for when one can actually see time taken
            {
                _stepExecutionTime = timeSpent;
            }
            else
            {
                _stepExecutionTime = 0.01f * timeSpent + 0.99f * _stepExecutionTime;
            }
        }

        private void PostRun()
        {
            StopModules();

            if (!_stoppedDueToError)
            {
                _output.SaveOutput();
                _haveResults = true;
                _evacuation.CreateAndRunTriggerBufferModule(this, _input, _weather, _time);
            }

            _simulationStopwatch.Stop();
            double total = System.Math.Max(1, _simulationStopwatch.ElapsedMilliseconds);
            Engine.Message(this, Engine.LogType.Log, "Total time spent [s]:" + _simulationStopwatch.ElapsedMilliseconds * 0.001);
            Engine.Message(this, Engine.LogType.Log, "Total time spent in weather manager [s]:" + _weatherStopwatch.ElapsedMilliseconds * 0.001);
            Engine.Message(this, Engine.LogType.Log, "Total time spent in modules [s]:" + _modulesStopwatch.ElapsedMilliseconds * 0.001);
            if (_moduleStopwatches != null)
            {
                for (int i = 0; i < _moduleStopwatches.Length; ++i)
                {
                    Engine.Message(this, Engine.LogType.Log, $"Total time spent in {_simulationModules[i].GetType().Name} [s]:" + _moduleStopwatches[i].ElapsedMilliseconds * 0.001 + string.Format(" [{0}%]", (int)(100.0 * _moduleStopwatches[i].ElapsedMilliseconds / total)));
                }
            }
            _evacuation.PostRun(_simulationStopwatch);

            Engine.Message(this, Engine.LogType.Log, " Simulation done.");

            //force garbage collection
            System.GC.Collect();
        }

        /// <summary>
        /// Creates the hazard and evacuation modules. Whatever was created is kept even when a later module fails,
        /// so the failure path can stop it (a SUMO instance left open blocks the next run in the same process).
        /// </summary>
        private void CreateSimulationModules()
        {
            _state = SimulationState.Initializing;

            //Hazards
            List<SimulationModule> createdModules = _hazards.CreateModules(_weather, _time, out bool success);
            _simulationModules.AddRange(createdModules);
            if (success)
            {
                Engine.Message(this, Engine.LogType.Log, "All requested hazard modules initiated successfully.");
            }
            else
            {
                _stopRun = true;
                Engine.Message(this, Engine.LogType.Log, "Failed to create all requested hazard modules, aborting.");
                return;
            }

            //Evacuation
            createdModules = _evacuation.CreateModules(_weather, _time, out success);
            _simulationModules.AddRange(createdModules);
            if (success)
            {
                Engine.Message(this, Engine.LogType.Log, "All requested evacuation modules initiated successfully.");
            }
            else
            {
                _stopRun = true;
                Engine.Message(this, Engine.LogType.Log, "Failed to create all requested evacuation modules, aborting.");
                return;
            }

            //stuff for timing
            _moduleStopwatches = new Stopwatch[_simulationModules.Count];
            for (int i = 0; i < _simulationModules.Count; ++i)
            {
                _moduleStopwatches[i] = new Stopwatch();
            }

            Engine.Message(this, Engine.LogType.Log, "All requested sub-modules initiated successfully.");
        }

        public void SetPause(bool pause)
        {
            _isPaused = pause;
        }

        public void TogglePause()
        {
            _isPaused = !_isPaused;
        }

        public void ToggleRealtime()
        {
            _runRealtime = !_runRealtime;
        }

        private bool _modulesStopped;

        /// <summary>Stops every created module once; one that throws does not keep the others open.</summary>
        private void StopModules()
        {
            if (_modulesStopped)
            {
                return;
            }
            _modulesStopped = true;

            foreach(SimulationModule module in _simulationModules)
            {
                try
                {
                    module.Stop();
                }
                catch (System.Exception e)
                {
                    Engine.Message(this, Engine.LogType.Warning, $"Stopping {module.GetType().Name} threw: {e.Message}");
                }
            }
        }

        private volatile bool _stoppedDueToError = false;
        public bool StoppedDueToError { get => _stoppedDueToError; }
        public void Stop(string stopMessage, bool stoppedDueToError)
        {
            _stoppedDueToError |= stoppedDueToError;
            if(!_stopRun)
            {
                _stopRun = true;
                Engine.Message(this, Engine.LogType.Log, stopMessage);
            }
        }
    }
}
