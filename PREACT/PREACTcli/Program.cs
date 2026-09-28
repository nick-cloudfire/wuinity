using PREACT.Tools;

namespace PREACTcli
{
    internal class Program
    {
        static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            //What the engine's constructor does for PREACT.exe and the GUI: the CLI builds no Engine, so without
            //this its GDAL wrappers were only found on Linux with Runtimes/Native/GDAL/x64 on LD_LIBRARY_PATH.
            PREACT.Runtime.NativeLibraries.SetUpForProcess();

            switch (args[0])
            {
                case "global-gpw-to-pop":
                    return Guarded(args[0], () => RunGpwToPop(args[1..]));
                case "converge-trigger":
                    return Guarded(args[0], () => ConvergeTrigger.Run(args[1..]));
                case "build-case":
                    return Guarded(args[0], () => BuildCase.Run(args[1..]));
                case "probabilistic-trigger":
                    Console.Error.WriteLine("probabilistic-trigger is gone: converge-trigger generates the realizations "
                                            + "with ELMFIRE and runs until the probability raster is stable.");
                    return 2;
                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]}");
                    PrintUsage();
                    return 2;
            }
        }

        /// <summary>
        /// Runs a command so that an exception it did not handle ends the CLI with a message and exit code 1, after
        /// killing every child it started - rather than .NET's unhandled-exception crash.
        /// </summary>
        /// <remarks>
        /// Moving a same-settings campaign folder aside throws on Windows while any file in it is open (the GUI
        /// showing the live raster, an Explorer preview), and hashing an input that is being written throws too; the
        /// CLI then died with 0xE0434352, which the GUI reads as neither success nor failure, and the ProcessExit kill
        /// of its children did not run (review MI-8).
        /// </remarks>
        internal static int Guarded(string command, Func<int> run)
        {
            try
            {
                return run();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"ERROR: {command} stopped on an unexpected error: {Describe(e)}");
                Console.Error.WriteLine("       (" + e.GetType().Name + (e.StackTrace != null
                    ? " at " + e.StackTrace.Split('\n')[0].Trim() : "") + ")");
                int killed = PREACT.Utility.ElmfireProcesses.KillAll();
                if (killed > 0) Console.Error.WriteLine($"       stopped {killed} process tree(s) it had started.");
                return 1;
            }
        }

        /// <summary>
        /// An exception's message with every inner exception's after it. A type initializer or a reflection call only
        /// says that something failed; which native library would not load is two levels down.
        /// </summary>
        internal static string Describe(Exception e)
        {
            var parts = new List<string>();
            for (Exception x = e; x != null && parts.Count < 6; x = x.InnerException)
            {
                string m = x.Message?.Trim();
                if (!string.IsNullOrEmpty(m) && !parts.Contains(m)) parts.Add(m);
            }
            return string.Join(" <- ", parts);
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  PREACTcli global-gpw-to-pop --gpw <dir> --osm <file> --out <file> [--minhh <n>] [--maxhh <n>]");
            BuildCase.PrintUsage();
            ConvergeTrigger.PrintUsage();
        }

        static int RunGpwToPop(string[] args)
        {
            string gpwDir = null, osmFile = null, outFile = null;
            int minHH = 1, maxHH = 5;

            try
            {
                new CliArgs()
                    .Value("--gpw", v => gpwDir = v)
                    .Value("--osm", v => osmFile = v)
                    .Value("--out", v => outFile = v)
                    .Int("--minhh", v => minHH = v, 1, 100)
                    .Int("--maxhh", v => maxHH = v, 1, 100)
                    .Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                return 2;
            }

            if (gpwDir == null || osmFile == null || outFile == null)
            {
                Console.Error.WriteLine("ERROR: --gpw, --osm and --out are required.");
                PrintUsage();
                return 2;
            }

            if (!Directory.Exists(gpwDir))
            {
                Console.Error.WriteLine($"ERROR: GPW directory not found: {gpwDir}");
                return 1;
            }

            if (!File.Exists(osmFile))
            {
                Console.Error.WriteLine($"ERROR: OSM file not found: {osmFile}");
                return 1;
            }

            Console.WriteLine("Generating population CSV...");
            Console.WriteLine($"  GPW folder : {gpwDir}");
            Console.WriteLine($"  OSM file   : {osmFile}");
            Console.WriteLine($"  Output     : {outFile}");
            Console.WriteLine($"  Household  : {minHH}-{maxHH} people");

            PopulationTools.CreatePopulationFromGPW(gpwDir, osmFile, outFile, minHH, maxHH, out bool success);

            if (!success)
            {
                Console.Error.WriteLine("ERROR: population generation failed (check the GPW folder, OSM file and that the domain overlaps the GPW data).");
                return 1;
            }

            Console.WriteLine("Done: " + outFile);
            return 0;
        }
    }
}
