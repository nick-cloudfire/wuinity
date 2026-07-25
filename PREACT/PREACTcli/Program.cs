using PREACT.Tools;

namespace PREACTcli
{
    internal class Program
    {
        static void Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            switch (args[0])
            {
                case "global-gpw-to-pop":
                    RunGpwToPop(args[1..]);
                    break;
                case "probabilistic-trigger":
                    Environment.Exit(ProbabilisticTrigger.Run(args[1..]));
                    break;
                case "converge-trigger":
                    Environment.Exit(ConvergeTrigger.Run(args[1..]));
                    break;
                default:
                    Console.Error.WriteLine($"Unknown command: {args[0]}");
                    PrintUsage();
                    Environment.Exit(1);
                    break;
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  PREACTcli global-gpw-to-pop --gpw <dir> --osm <file> --out <file> [--minhh <n>] [--maxhh <n>]");
            ProbabilisticTrigger.PrintUsage();
            ConvergeTrigger.PrintUsage();
        }

        static void RunGpwToPop(string[] args)
        {
            string? gpwDir    = null;
            string? osmFile   = null;
            string? outFile   = null;
            int     minHH     = 1;
            int     maxHH     = 5;

            for (int i = 0; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--gpw":   gpwDir  = args[++i]; break;
                    case "--osm":   osmFile = args[++i]; break;
                    case "--out":   outFile = args[++i]; break;
                    case "--minhh": int.TryParse(args[++i], out minHH); break;
                    case "--maxhh": int.TryParse(args[++i], out maxHH); break;
                }
            }

            if (gpwDir == null || osmFile == null || outFile == null)
            {
                Console.Error.WriteLine("ERROR: --gpw, --osm and --out are required.");
                PrintUsage();
                Environment.Exit(1);
                return;
            }

            if (!Directory.Exists(gpwDir))
            {
                Console.Error.WriteLine($"ERROR: GPW directory not found: {gpwDir}");
                Environment.Exit(1);
                return;
            }

            if (!File.Exists(osmFile))
            {
                Console.Error.WriteLine($"ERROR: OSM file not found: {osmFile}");
                Environment.Exit(1);
                return;
            }

            Console.WriteLine($"Generating population CSV...");
            Console.WriteLine($"  GPW folder : {gpwDir}");
            Console.WriteLine($"  OSM file   : {osmFile}");
            Console.WriteLine($"  Output     : {outFile}");
            Console.WriteLine($"  Household  : {minHH}-{maxHH} people");

            PopulationTools.CreatePopulationFromGPW(gpwDir, osmFile, outFile, minHH, maxHH, out bool success);

            if (success)
            {
                Console.WriteLine("Done: " + outFile);
            }
            else
            {
                Console.Error.WriteLine("ERROR: population generation failed (check the GPW folder, OSM file and that the domain overlaps the GPW data).");
                Environment.Exit(1);
            }
        }
    }
}
