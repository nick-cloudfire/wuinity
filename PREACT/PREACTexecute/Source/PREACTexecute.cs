using PREACT.Evacuation;
using PREACT.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PREACT
{
    internal class PREACTexecute :IExternalManager
    {
        Engine _engine;
        PREACTInput? _input;
        bool _isDone;

        public Engine Engine { get => _engine; }
        public bool IsDone { get => _isDone; }


        public PREACTexecute()
        {
            _engine = new Engine(this, true);
            _isDone = true;
        }

        /// <summary>Process exit codes.</summary>
        public const int ExitSuccess = 0, ExitNotRun = 1, ExitRunFailed = 2;

        /// <summary>
        /// Loads the scenario and runs it. Returns <see cref="ExitSuccess"/> when every simulation ran to the end
        /// and nothing reported an error, <see cref="ExitNotRun"/> when nothing was simulated (bad arguments, a
        /// scenario that does not load or is not complete), and <see cref="ExitRunFailed"/> when a simulation
        /// stopped on an error or an error was reported during the run - such as k-PERIL refusing to compute a
        /// boundary. The run used to count as a success in that last case.
        /// </summary>
        public async Task<int> Execute(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: PREACT <file.wui> [<numberOfRuns> [<ignored batchSize> [<indexOffset>]]]");
                return ExitNotRun;
            }
            else if(!File.Exists(args[0]))
            {
                Console.WriteLine("Specified file does not exist: " + args[0]);
                return ExitNotRun;
            }

            _engine.LoadInputFromFile(args[0], out bool success, false);
            if (!success)
            {
                Console.WriteLine("The scenario did not load as runnable; see the items listed above.");
                return ExitNotRun;
            }

            //Serial always: PREACT <file> [<numberOfRuns> [<batchSize> [<offset>]]]. The batch size is accepted for
            //compatibility and ignored - several runs in one process run one after another (SUMO allows one
            //instance per process); run several processes for parallelism, as the campaign tools do.
            int numberOfRuns = 1, simulationIndexOffset = 0;
            if (args.Length >= 2 && (!int.TryParse(args[1], out numberOfRuns) || numberOfRuns < 1))
            {
                Console.WriteLine("Number of runs must be a positive integer: " + args[1]);
                return ExitNotRun;
            }
            if (args.Length >= 4 && !int.TryParse(args[3], out simulationIndexOffset))
            {
                Console.WriteLine("Simulation index offset must be an integer: " + args[3]);
                return ExitNotRun;
            }

            EngineTask engineTask = new EngineTask(EngineTask.ExecutionMode.Serial, numberOfRuns, simulationIndexOffset);
            _isDone = false;
            await _engine.RunSimulations(engineTask);
            //belt and braces: SimulationsFinished() normally sets this, but it only fires
            //when an external manager is registered, and the process must exit regardless.
            _isDone = true;

            if (!_engine.LastRunSucceeded)
            {
                Console.WriteLine($"The run reported {_engine.LastRunErrorCount} error(s); see the log above.");
                return ExitRunFailed;
            }
            return ExitSuccess;
        }

        public void NewLogMessage(string message)
        {
            Console.WriteLine(message);
        }

        public void PauseSimulations()
        {
        }

        public void SimulationStarted()
        {
        }

        public void SimulationsFinished()
        {
            _isDone = true;
        }

        public void StopSimulations()
        {
        }

        public void UpdateInput(PREACTInput input)
        {
            _input = input;
        }

        public void UpdateDestinations(List<EvacuationDestination> destinations)
        {
        }
    }
}
