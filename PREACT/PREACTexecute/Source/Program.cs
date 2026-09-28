namespace PREACT
{
    internal class Program
    {
        /// <summary>
        /// 0 when the run happened and succeeded, 1 when nothing was run, 2 when the run reported errors.
        /// </summary>
        /// <remarks>
        /// This used to return 0 whatever happened, and then 0 whenever a run merely took place - so a realization
        /// whose k-PERIL step refused to compute a boundary ("no evacuation arrivals") still exited 0.
        /// </remarks>
        static async Task<int> Main(string[] args)
        {
            PREACTexecute preact = new PREACTexecute();

            // Awaited rather than polled (a bare `while (!preact.IsDone) { }` used to burn a core for the whole
            // run and never ended if the finished callback did not fire).
            int exitCode = await preact.Execute(args);

            //Only after a run: after a usage line or a scenario that did not load it read as though something had run.
            if (preact.Ran)
            {
                Console.WriteLine("Simulation run executed, shutting down (exit code " + exitCode + ").");
            }
            return exitCode;
        }
    }
}
