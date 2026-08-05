namespace PREACT
{
    internal class Program
    {
        /// <summary>
        /// Returns 0 only when a run actually happened.
        /// </summary>
        /// <remarks>
        /// This used to return void, so the process exited 0 whatever happened — including the case that
        /// matters most to the campaign drivers: a scenario that fails to load prints "Failed to read loaded
        /// file", runs nothing, and reported success. The drivers only noticed because no boundary file
        /// appeared, which is a coincidence of what they check rather than the process telling them.
        /// </remarks>
        static async Task<int> Main(string[] args)
        {
            PREACTexecute preact = new PREACTexecute();

            // Awaited rather than polled. This used to be a bare `while (!preact.IsDone) { }`
            // spin, which had two problems: it burned a full core for the whole run (multiplied by
            // every concurrent realization the probabilistic-trigger drivers spawn), and it never
            // terminated unless the SimulationsFinished callback happened to fire — so a finished
            // or failed run could leave the process alive indefinitely with its output already
            // written, which is exactly what stalled the converge-trigger pipeline.
            bool ran = await preact.Execute(args);

            Console.WriteLine("Simulation run executed, shutting down.");
            return ran ? 0 : 1;
        }
    }
}
