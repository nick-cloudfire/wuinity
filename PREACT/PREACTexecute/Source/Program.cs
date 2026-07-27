namespace PREACT
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            PREACTexecute preact = new PREACTexecute();

            // Awaited rather than polled. This used to be a bare `while (!preact.IsDone) { }`
            // spin, which had two problems: it burned a full core for the whole run (multiplied by
            // every concurrent realization the probabilistic-trigger drivers spawn), and it never
            // terminated unless the SimulationsFinished callback happened to fire — so a finished
            // or failed run could leave the process alive indefinitely with its output already
            // written, which is exactly what stalled the converge-trigger pipeline.
            await preact.Execute(args);

            Console.WriteLine("Simulation run executed, shutting down.");
        }
    }
}
