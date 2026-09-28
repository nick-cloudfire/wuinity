namespace PREACT
{
    /// <summary>
    /// What <see cref="Engine.RunSimulations"/> is asked to do: how many runs, one after another in this process,
    /// and when to stop early because the average evacuation time has converged.
    /// </summary>
    /// <remarks>
    /// Runs are always serial. The in-process Parallel mode could never work (SUMO allows one instance per
    /// process) and ParallelProcess mis-counted its batches; the campaign tools run realizations in parallel as
    /// separate PREACT processes instead. The execution mode, batch size and "visualize" flag that described
    /// those are gone with them.
    /// </remarks>
    public class EngineTask
    {
        public int NumberOfRuns = 1;
        public bool StopAfterConverging = true;        
        public int ConvergenceMinSequence = 10;
        public float ConvergenceMaxDifference = 0.02f;
        public int SimulationIndexOffset = 0;

        public EngineTask()
        {

        }

        public EngineTask(int numberOfRuns, int simulationIndexOffset = 0, bool stopAfterConverging = true, int convergenceMinSequence = 10, float convergenceMaxDifference = 0.02f)
        {
            NumberOfRuns = numberOfRuns;
            SimulationIndexOffset = simulationIndexOffset;
            StopAfterConverging = stopAfterConverging;
            ConvergenceMinSequence = convergenceMinSequence;
            ConvergenceMaxDifference = convergenceMaxDifference;
        }
    }
}
