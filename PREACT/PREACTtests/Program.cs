using PREACT.Input;

namespace PREACT.Tests
{
    /// <summary>
    /// Console test runner for the engine.
    /// </summary>
    /// <remarks>
    /// Usage: <c>PREACTtests [--no-examples] [--out &lt;dir&gt;] [--filter &lt;text&gt;] [--verbose] [file.wui ...]</c>
    /// <list type="bullet">
    /// <item>Always runs the self-contained format and engine tests (synthetic scenarios in a temp folder).</item>
    /// <item>Round-trips (load, write, reload, write) every example in the repository's <c>Examples/</c> and
    /// every <c>.wui</c> given on the command line: no new or more critical requirement, idempotent writer, and
    /// every key of the original survives (retired keys excepted). Nothing is written into a scenario's folder.</item>
    /// <item><c>--out</c> keeps the written files for inspection.</item>
    /// </list>
    /// Exit code 0 when everything passed, 1 otherwise. Differences in sections owned by the fire/k-PERIL
    /// parsers are reported as WARN rather than failures.
    /// </remarks>
    internal static class Program
    {
        internal static readonly TestLog Log = new TestLog();
        internal static Engine Engine;

        private static int Main(string[] args)
        {
            bool examples = true;
            string outDir = null;
            string filter = null;
            var files = new List<string>();
            for (int i = 0; i < args.Length; ++i)
            {
                switch (args[i])
                {
                    case "--no-examples": examples = false; break;
                    case "--out": outDir = args[++i]; break;
                    case "--filter": filter = args[++i]; break;
                    case "--verbose": Log.Echo = true; break;
                    case "-h":
                    case "--help":
                        Console.WriteLine("PREACTtests [--no-examples] [--out <dir>] [--filter <text>] [--verbose] [file.wui ...]");
                        return 0;
                    default: files.Add(args[i]); break;
                }
            }

            //The engine registers GDAL and the native resolver; its messages are collected per test.
            Engine = new Engine(Log, true);

            var runner = new Runner(filter);
            FormatTests.Register(runner);
            EngineTests.Register(runner);
            CliTests.Register(runner);
            IntegrationTests.Register(runner);
            TriggerTests.Register(runner);
            PipelineTests.Register(runner);

            if (examples)
            {
                string repo = FindRepositoryRoot();
                if (repo == null)
                {
                    Console.WriteLine("WARN could not find the repository's Examples folder; example round trips skipped.");
                }
                else
                {
                    files.InsertRange(0, RoundTrip.Examples(repo));
                }
            }

            foreach (string file in files)
            {
                string captured = file;
                runner.Add("roundtrip " + Path.GetFileName(file), () => RoundTrip.Run(captured, outDir));
            }

            return runner.RunAll() ? 0 : 1;
        }

        private static string FindRepositoryRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (Directory.Exists(Path.Combine(dir, "Examples")) && Directory.Exists(Path.Combine(dir, "PREACT")))
                {
                    return dir;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }

    /// <summary>Collects engine messages so a failing test can show what the engine said.</summary>
    internal sealed class TestLog : IExternalManager
    {
        private readonly List<string> _messages = new List<string>();
        public bool Echo;

        public List<string> Take()
        {
            lock (_messages)
            {
                var copy = new List<string>(_messages);
                _messages.Clear();
                return copy;
            }
        }

        public void NewLogMessage(string message)
        {
            lock (_messages) _messages.Add(message);
            if (Echo) Console.WriteLine("    | " + message);
        }

        public void UpdateInput(PREACTInput input) { }
        public int FinishedCount;
        public void SimulationsFinished() { ++FinishedCount; }
        public void UpdateDestinations(List<Evacuation.EvacuationDestination> destinations) { }
    }

    internal sealed class TestFailure : Exception
    {
        public TestFailure(string message) : base(message) { }
    }

    internal static class Assert
    {
        public static void True(bool condition, string what)
        {
            if (!condition) throw new TestFailure(what);
        }

        public static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new TestFailure($"{what}: expected {expected}, got {actual}");
            }
        }

        public static void Near(double expected, double actual, double tolerance, string what)
        {
            if (System.Math.Abs(expected - actual) > tolerance)
            {
                throw new TestFailure($"{what}: expected {expected}, got {actual}");
            }
        }
    }

    internal sealed class Runner
    {
        private readonly List<(string name, Func<List<string>> test)> _tests = new List<(string, Func<List<string>>)>();
        private readonly string _filter;

        public Runner(string filter)
        {
            _filter = filter;
        }

        /// <summary>A test that either passes or throws.</summary>
        public void Add(string name, Action test)
        {
            _tests.Add((name, () => { test(); return new List<string>(); }));
        }

        /// <summary>A test that returns warnings (non-fatal findings) or throws.</summary>
        public void Add(string name, Func<List<string>> test)
        {
            _tests.Add((name, test));
        }

        public bool RunAll()
        {
            int passed = 0, failed = 0, warned = 0;
            foreach ((string name, Func<List<string>> test) in _tests)
            {
                if (_filter != null && name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                Program.Log.Take();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    List<string> warnings = test();
                    Console.WriteLine($"PASS {name} ({watch.Elapsed.TotalSeconds:F1} s)");
                    foreach (string warning in warnings)
                    {
                        Console.WriteLine("  WARN " + warning);
                        ++warned;
                    }
                    ++passed;
                }
                catch (Exception e)
                {
                    ++failed;
                    Console.WriteLine($"FAIL {name}: {(e is TestFailure ? e.Message : e.ToString())}");
                    List<string> messages = Program.Log.Take();
                    foreach (string message in messages.Skip(System.Math.Max(0, messages.Count - 15)))
                    {
                        Console.WriteLine("    | " + message);
                    }
                }
            }

            Console.WriteLine($"{passed} passed, {failed} failed, {warned} warning(s).");
            return failed == 0;
        }
    }
}
