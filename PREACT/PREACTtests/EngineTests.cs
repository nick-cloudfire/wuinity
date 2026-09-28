using System.Globalization;
using PREACT.Evacuation;
using PREACT.Input;
using PREACT.Pedestrian;
using PREACT.Traffic;

namespace PREACT.Tests
{
    /// <summary>
    /// Engine behaviour that can be checked without SUMO or real case data, on the synthetic scenario.
    /// </summary>
    internal static class EngineTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("engine: MaxFlow holds a stream to its rate and recovers after a lull", DestinationFlow);
            runner.Add("engine: a CDF ending below 1 sends the rest to the last destination", CdfFallback);
            runner.Add("engine: households leave when the fire comes within the reaction distance", FireReaction);
            runner.Add("engine: the same RandomSeed reproduces a run, another seed does not", SeedReproducible);
            runner.Add("engine: the clock is exact after a day of 0.1 s steps", ExactClock);
            runner.Add("engine: no weather file means no weather and no download", NoWeather);
            runner.Add("engine: a run on an incomplete scenario is refused and still finishes", RunGatedOnChecklist);
        }

        private sealed class TestVehicle : TrafficModuleVehicle
        {
            public TestVehicle(uint id, EvacuationDestination destination) : base(id, 1, destination) { }
            public override bool TryToArrive(double deltaTime, double currentTime) => _destination.TryToArrive(this, currentTime + deltaTime, deltaTime);
        }

        private static Engine TheEngine => Program.Engine;

        private static PREACTInput Load(FormatTests.Scenario scenario, IEnumerable<string> lines)
        {
            PREACTInput input = scenario.Load(lines, out bool runnable);
            Assert.True(runnable, "scenario runnable; critical: " + string.Join("; ", PREACTInput.Requirements.Where(r => r.Critical)));
            return input;
        }

        private static void DestinationFlow()
        {
            using var s = new FormatTests.Scenario();
            PREACTInput input = Load(s, FormatTests.Replace("MaxFlow", "360")); //one car every 10 s
            var simulation = new Simulation(TheEngine, input, 0);
            EvacuationDestination north = simulation.Evacuation.Destinations.First(d => d.Name == "north");

            //A car arrives every second for ten minutes.
            int accepted = 0;
            uint id = 0;
            for (int t = 0; t < 600; ++t)
            {
                if (new TestVehicle(++id, north).TryToArrive(1.0, t)) ++accepted;
            }
            //burst of 1 (one minute's worth = 6 cars) then one per 10 s
            Assert.True(accepted >= 60 && accepted <= 70, $"a 360 veh/h exit accepted {accepted} cars in 600 s (expected about 60-66)");
            double flow = north.GetVehicleFlow(600);
            Assert.True(flow >= 300 && flow <= 440, $"reported flow {flow} veh/h over the last 5 minutes (limit 360)");

            //After a 10 minute lull, a car is let straight in again (it used to be refused for good).
            Assert.True(new TestVehicle(++id, north).TryToArrive(1.0, 1200), "a car after a lull is accepted");
            Assert.True(north.GetVehicleFlow(3000) == 0.0, "flow falls to zero once arrivals stop");

            //Unlimited exit: every car, and a sane flow (it used to be arrivals x 3600).
            EvacuationDestination south = simulation.Evacuation.Destinations.First(d => d.Name == "south");
            for (int t = 0; t < 300; ++t)
            {
                Assert.True(new TestVehicle(++id, south).TryToArrive(1.0, t), "unlimited exit accepts every car");
            }
            Assert.Near(3600.0, south.GetVehicleFlow(300), 1e-6, "300 cars in 300 s is 3600 veh/h");
        }

        private static void CdfFallback()
        {
            using var s = new FormatTests.Scenario();
            PREACTInput input = s.Load(FormatTests.Replace("DestinationsCDF", "0.5,0.5"), out bool _);
            var simulation = new Simulation(TheEngine, input, 0);
            EvacuationGroup group = simulation.Evacuation.GetEvacuationGroup(new Math.Vector2d(38.013, 23.901), out bool inside);
            Assert.True(inside, "the test household is inside the group");
            Math.Random.SeedForSimulation(7, 0);
            int last = 0;
            for (int i = 0; i < 2000; ++i)
            {
                if (group.GetWeightedRandomDestination().Name == "south") ++last;
            }
            Assert.True(last > 850 && last < 1150, $"about half the draws go to the last destination; got {last} of 2000 (the fallback used to be the first)");
        }

        /// <summary>
        /// A fire spreading from (1500, 1500) m at 0.5 m/s, the homes about 1.2 km from it and ordered to leave only
        /// after an hour: the front is within 500 m of them after roughly 40 minutes.
        /// </summary>
        private static List<string> FireLines(FormatTests.Scenario s, PREACTInput probe, bool react)
        {
            Math.Vector2d origin = probe.Simulation.Data.UTMOrigin;
            int n = 20;
            double cell = 100.0;
            string Header() => string.Join("\n", new[]
            {
                "ncols " + n, "nrows " + n,
                "xllcorner " + origin.x.ToString("R", CultureInfo.InvariantCulture),
                "yllcorner " + origin.y.ToString("R", CultureInfo.InvariantCulture),
                "cellsize " + cell.ToString(CultureInfo.InvariantCulture), "NODATA_value -9999",
            });
            string Grid(Func<double, double, double> value)
            {
                var rows = new List<string>();
                for (int row = 0; row < n; ++row)
                {
                    double y = (n - row - 0.5) * cell; //first row is the northern one
                    var cells = new List<string>();
                    for (int col = 0; col < n; ++col)
                    {
                        double x = (col + 0.5) * cell;
                        cells.Add(value(x, y).ToString("R", CultureInfo.InvariantCulture));
                    }
                    rows.Add(string.Join(" ", cells));
                }
                return Header() + "\n" + string.Join("\n", rows) + "\n";
            }
            File.WriteAllText(Path.Combine(s.Folder, "toa.asc"), Grid((x, y) => System.Math.Sqrt((x - 1500) * (x - 1500) + (y - 1500) * (y - 1500)) / 0.5));
            File.WriteAllText(Path.Combine(s.Folder, "ros.asc"), Grid((x, y) => 0.5));
            File.WriteAllText(Path.Combine(s.Folder, "sd.asc"), Grid((x, y) => 225));

            var lines = new List<string>(FormatTests.Scenario.Lines);
            lines[lines.IndexOf("EvacuationOrderDateTime=2026-06-28T12:00:00")] = "EvacuationOrderDateTime=2026-06-28T13:00:00";
            lines.Insert(lines.IndexOf("WalkingSpeedMinMax=0.7,1") + 1, "ReactToFire=" + (react ? "true" : "false"));
            lines.Insert(lines.IndexOf("StopWhenEvacuated=true") + 1, "RandomSeed=11");
            lines.AddRange(new[]
            {
                "", "[WildfireModule]", "Enabled=true", "Module=AscImport",
                "", "[AscImport]", "StartDateTime=2026-06-28T12:00:00", "TimeOfArrivalFile=toa.asc", "TimeOfArrivalUnits=Seconds",
                "RateOfSpreadFile=ros.asc", "SpreadDirectionFile=sd.asc",
            });
            return lines;
        }

        private static int RunFire(bool react)
        {
            using var s = new FormatTests.Scenario();
            PREACTInput probe = s.Load(FormatTests.Scenario.Lines, out bool _);
            PREACTInput input = Load(s, FireLines(s, probe, react));
            TheEngine.SetInput(input, Path.Combine(s.Folder, "scenario.wui"));
            TheEngine.RunSimulations(new EngineTask(EngineTask.ExecutionMode.Serial, 1)).GetAwaiter().GetResult();
            Simulation simulation = TheEngine.Simulation;
            Assert.True(simulation.State == Simulation.SimulationState.Completed, "run completed, state " + simulation.State);
            Assert.True(!simulation.IsRunning, "not running after the run");
            var pedestrians = (MacroHouseholdSim)simulation.Evacuation.PedestrianModule;
            return pedestrians.HouseholdsStartedByFire;
        }

        private static void FireReaction()
        {
            int earlyOn = RunFire(true);
            int earlyOff = RunFire(false);
            Assert.Equal(0, earlyOff, "households started by the fire with ReactToFire=false");
            Assert.Equal(3, earlyOn, "households started by the fire with ReactToFire=true (all three homes are reached before their order)");
        }

        private static string RunSeeded(int seed)
        {
            using var s = new FormatTests.Scenario();
            var lines = new List<string>(FormatTests.Scenario.Lines);
            lines.Insert(lines.IndexOf("StopWhenEvacuated=true") + 1, "RandomSeed=" + seed);
            PREACTInput input = Load(s, lines);
            TheEngine.SetInput(input, Path.Combine(s.Folder, "scenario.wui"));
            TheEngine.RunSimulations(new EngineTask(EngineTask.ExecutionMode.Serial, 1)).GetAwaiter().GetResult();
            return string.Join("\n", File.ReadAllLines(Path.Combine(s.Folder, "_output", "synthetic_pedestrian_output_0.csv")));
        }

        private static void SeedReproducible()
        {
            string a = RunSeeded(42), b = RunSeeded(42), c = RunSeeded(42), d = RunSeeded(43);
            Assert.True(a == b && b == c, "three runs with RandomSeed=42 give identical pedestrian output");
            Assert.True(a != d, "RandomSeed=43 gives a different run");
        }

        private static void ExactClock()
        {
            using var s = new FormatTests.Scenario();
            PREACTInput input = s.Load(FormatTests.Replace("EndDateTime", "2026-06-29T12:00:00"), out bool _);
            var time = new TimeManager(input, new Simulation(TheEngine, input, 0));
            for (int i = 0; i < 864000; ++i)
            {
                time.Step(0.1);
            }
            Assert.Near(86400.0, time.SimulationTime, 1e-6, "SimulationTime after 864000 steps of 0.1 s");
            Assert.Equal(time.StartDateTime.AddDays(1), time.CurrentDateTime, "CurrentDateTime a day later");
        }

        private static void NoWeather()
        {
            using var s = new FormatTests.Scenario();
            PREACTInput input = Load(s, FormatTests.Scenario.Lines);
            var simulation = new Simulation(TheEngine, input, 0);
            Assert.True(!simulation.Weather.HasWeather, "no weather loaded");
            Assert.True(Directory.GetFiles(s.Folder, "weather_*").Length == 0, "nothing downloaded into the scenario folder");
            simulation.Weather.Update(input.Simulation.StartDateTime.AddHours(1));
        }

        private static void RunGatedOnChecklist()
        {
            using var s = new FormatTests.Scenario();
            PREACTInput input = s.Load(FormatTests.Replace("PopulationFile", "missing.csv"), out bool runnable);
            Assert.True(!runnable, "a missing population file is critical with pedestrians on");
            TheEngine.SetInput(input, Path.Combine(s.Folder, "scenario.wui"));
            var log = Program.Log;
            log.Take();
            TheEngine.RunSimulations(new EngineTask(EngineTask.ExecutionMode.Serial, 1)).GetAwaiter().GetResult();
            Assert.True(!TheEngine.LastRunSucceeded, "the run did not succeed");
            Assert.True(log.Take().Any(m => m.Contains("not complete enough to run")), "and said why");
            Assert.True(log.FinishedCount > 0, "SimulationsFinished was still called");
        }
    }
}
