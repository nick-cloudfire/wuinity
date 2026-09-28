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

            switch (args[0])
            {
                case "global-gpw-to-pop":
                    return RunGpwToPop(args[1..]);
                case "converge-trigger":
                    return ConvergeTrigger.Run(args[1..]);
                case "build-case":
                    return BuildCase.Run(args[1..]);
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
