namespace PREACT
{
    public class EngineTask
    {
        /// <summary>
        /// How the runs are executed. Only Serial remains: the in-process Parallel mode could never work (SUMO
        /// allows one instance per process) and ParallelProcess mis-counted its batches; the campaign tools run
        /// realizations in parallel as separate PREACT processes instead.
        /// </summary>
        public enum ExecutionMode { Serial };

        public ExecutionMode Execution = ExecutionMode.Serial;
        public int NumberOfRuns = 1;
        /// <summary>Unused since the parallel modes were removed; kept for the GUI's run tab.</summary>
        public int BatchSize = 4;
        /// <summary>Unused; kept for the GUI's run tab.</summary>
        public bool Visualize = true;
        public bool StopAfterConverging = true;        
        public int ConvergenceMinSequence = 10;
        public float ConvergenceMaxDifference = 0.02f;
        public int SimulationIndexOffset = 0;

        public EngineTask()
        {

        }

        public EngineTask(ExecutionMode Execution, int numberOfRuns, int simulationIndexOffset = 0, int batchSize = 4, bool visualize = true, bool stopAfterConverging = true, int convergenceMinSequence = 10, float convergenceMaxDifference = 0.02f)
        {
            this.Execution = Execution;
            NumberOfRuns = numberOfRuns;
            SimulationIndexOffset = simulationIndexOffset;
            BatchSize = batchSize;
            Visualize = visualize;
            StopAfterConverging = stopAfterConverging;
            ConvergenceMinSequence = convergenceMinSequence;
            ConvergenceMaxDifference = convergenceMaxDifference;
        }
    }
}