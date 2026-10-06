using System.Diagnostics;
using PREACT.Utility;
using PREACTcli.Campaigns;

namespace PREACT.Tests
{
    /// <summary>
    /// The trigger campaign's live status: ELMFIRE's timestep lines read as it runs, the status file the CLI writes and
    /// the GUI's monitor reads, and the campaign tables the monitor shows.
    /// </summary>
    internal static class CampaignMonitorTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("Campaign monitor: ELMFIRE's timestep line is read (time, stop time, tracked nodes)", TimestepLines);
            runner.Add("Campaign monitor: every timestep line of a real campaign's ELMFIRE logs parses (data permitting)", RealElmfireLogs);
            runner.Add("Campaign monitor: a realization's stage reads as hours and nodes, never a % of a year", DescribeStages);
            runner.Add("Campaign monitor: status.json round-trips, is replaced whole, and leaves no temporary file", StatusRoundTrip);
            runner.Add("Campaign monitor: an unreadable or missing status is null, not an exception", StatusUnreadable);
            runner.Add("Campaign monitor: ElmfireRunner reports ELMFIRE's timesteps as it runs and still logs them", RunnerReportsTimesteps);
            runner.Add("Campaign monitor: the CLI's monitor writes stages, timesteps and a final phase", CliMonitorWritesStatus);
            runner.Add("Campaign monitor: PREACT.exe's SIM_TIME lines and steps are read", EvacuationProgress);
            runner.Add("Campaign monitor: realizations.csv and the latest convergence row are read", CampaignTables);
        }

        private static void TimestepLines()
        {
            Assert.True(ElmfireTimestep.TryParse("[1] Current Timestep: 102449.0 of 7200000.0, tracked nodes: 26822. Weather bands 1 to 1",
                out ElmfireTimestep t), "the line from the brief parses");
            Assert.Near(102449.0, t.Seconds, 1e-9, "simulated seconds");
            Assert.Near(7200000.0, t.StopSeconds, 1e-9, "SIMULATION_TSTOP");
            Assert.Equal(26822L, t.TrackedNodes, "tracked nodes, without the sentence's full stop");

            Assert.True(ElmfireTimestep.TryParse("[1] Current Timestep: 13744.9 of 28800.0, tracked nodes:     372. Weather bands 1 to 8\r",
                out t), "padded nodes and a trailing carriage return");
            Assert.Equal(372L, t.TrackedNodes, "padded nodes");
            Assert.Near(13744.9, t.Seconds, 1e-9, "seconds");

            //Two lines run together by a console's carriage return: the last one is where it is now.
            Assert.True(ElmfireTimestep.TryParse("[1] Current Timestep: 10.0 of 20.0, tracked nodes: 5. Weather\r[1] Current Timestep: 11.5 of 20.0, tracked nodes: 7. Weather",
                out t), "two steps on one line");
            Assert.Near(11.5, t.Seconds, 1e-9, "the later step");
            Assert.Equal(7L, t.TrackedNodes, "its nodes");

            Assert.True(ElmfireTimestep.TryParse("[1] Current Timestep: 900.0 of 1800.0, tracked nodes: *******. Weather bands 1 to 1",
                out t), "a node count too wide for its Fortran format still gives the time");
            Assert.Equal(-1L, t.TrackedNodes, "and no node count");

            Assert.True(!ElmfireTimestep.TryParse("[1] Meteorology band 1: Case # 1 complete.  Fire area:   3094.0 acres.", out _), "another line is not a timestep");
            Assert.True(!ElmfireTimestep.TryParse("[1] Current Timestep: of", out _), "a line without a time is not one");
            Assert.True(!ElmfireTimestep.TryParse(null, out _), "null");
        }

        private static List<string> RealElmfireLogs()
        {
            var warnings = new List<string>();
            const string folder = "/home/claude/cases/logtails";
            if (!Directory.Exists(folder))
            {
                warnings.Add("no ELMFIRE logs at " + folder + "; skipped");
                return warnings;
            }

            int lines = 0;
            foreach (string file in Directory.GetFiles(folder, "elmfire_*.txt"))
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (!line.Contains("Current Timestep:")) continue;
                    //Some run into the next message ("... Weather bands 1 to 1[1] STOPPED: FIRE FRONT PROPAGATION STALLED").
                    Assert.True(ElmfireTimestep.TryParse(line, out ElmfireTimestep t), file + ": " + line);
                    Assert.True(t.Seconds >= 0 && t.StopSeconds >= t.Seconds && t.TrackedNodes >= 0, file + ": every field of " + line);
                    ++lines;
                }
            }
            Assert.True(lines > 1000, $"read {lines} timestep lines");
            return warnings;
        }

        private static void DescribeStages()
        {
            var r = new RealizationProgress
            {
                Stage = RealizationProgress.StageElmfire,
                ElmfireSeconds = 37.5 * 3600,
                ElmfireStopSeconds = CampaignLayout.UntilStoppedTstopHours * 3600,
                TrackedNodes = 12804,
            };
            Assert.Equal("ELMFIRE 37.5 h, 12,804 nodes (until it stops)", r.Describe(untilStopped: true), "until it stops");

            r.ElmfireStopSeconds = 72 * 3600;
            Assert.Equal("ELMFIRE 37.5 of 72.0 h, 12,804 nodes", r.Describe(untilStopped: false), "a fixed duration");

            r.ElmfireSeconds = -1;
            r.Detail = "starting";
            Assert.Equal("ELMFIRE starting", r.Describe(false), "before its first timestep");

            var w = new RealizationProgress { Stage = RealizationProgress.StageWeather, Detail = "WindNinja" };
            Assert.Equal("weather: WindNinja", w.Describe(true), "weather");

            var e = new RealizationProgress { Stage = RealizationProgress.StageEvacuation, Detail = "loading the road network" };
            Assert.Equal("evacuation: loading the road network", e.Describe(true), "evacuation before its clock runs");
            e.EvacuationSeconds = 3.2 * 3600;
            e.EvacuationEndSeconds = 24 * 3600;
            e.Detail = "evacuating";
            Assert.Equal("evacuation 3.2 of 24.0 h (evacuating)", e.Describe(true), "evacuation with its clock");

            Assert.Equal("42 s", CampaignStatus.DescribeDuration(TimeSpan.FromSeconds(42.7)), "seconds");
            Assert.Equal("7 min 05 s", CampaignStatus.DescribeDuration(TimeSpan.FromSeconds(425)), "minutes");
            Assert.Equal("3 h 12 min", CampaignStatus.DescribeDuration(TimeSpan.FromMinutes(192.5)), "hours");
        }

        private static void StatusRoundTrip()
        {
            string dir = Directory.CreateTempSubdirectory("preact-status-").FullName;
            try
            {
                var started = new DateTime(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);
                var s = new CampaignStatus
                {
                    Phase = CampaignStatus.PhaseRunning,
                    Message = "a \"quoted\" message, with a comma\nand a line",
                    ProcessId = 4242,
                    StartedUtc = started,
                    UpdatedUtc = started.AddMinutes(5),
                    FireHours = CampaignLayout.UntilStoppedHours,
                    MaxRuntimeSeconds = 14400,
                    Parallel = 4,
                    Max = 200, Launched = 9, Done = 5, Ok = 3, NotThreatened = 1, Failed = 1, Reused = 2,
                    Streak = 2, StreakTarget = 20, Converged = false,
                };
                s.Running.Add(new RealizationProgress
                {
                    Index = 7, Id = "0000007", Stage = RealizationProgress.StageElmfire,
                    StartedUtc = started.AddMinutes(1), StageStartedUtc = started.AddMinutes(2),
                    ElmfireSeconds = 135000.0, ElmfireStopSeconds = 31536000.0, TrackedNodes = 12804,
                });
                s.Running.Add(new RealizationProgress
                {
                    Index = 8, Id = "0000008", Stage = RealizationProgress.StageWeather, Detail = "WindNinja",
                    StartedUtc = started.AddMinutes(3), StageStartedUtc = started.AddMinutes(3),
                });

                Assert.True(CampaignStatus.Write(dir, s), "written");
                CampaignStatus r = CampaignStatus.Read(dir);
                Assert.True(r != null, "read back");
                Assert.Equal(s.Phase, r.Phase, "phase");
                Assert.Equal(s.Message, r.Message, "message, escaped and back");
                Assert.Equal(4242, r.ProcessId, "pid");
                Assert.Equal(started, r.StartedUtc, "start, UTC");
                Assert.Equal(DateTimeKind.Utc, r.StartedUtc.Kind, "kept as UTC");
                Assert.Equal(started.AddMinutes(5), r.UpdatedUtc, "update");
                Assert.True(r.UntilStopped, "until it stops");
                Assert.Equal(200, r.Max, "max");
                Assert.Equal(9, r.Launched, "launched");
                Assert.Equal(5, r.Done, "done");
                Assert.Equal(3, r.Ok, "ok");
                Assert.Equal(1, r.NotThreatened, "not threatened");
                Assert.Equal(1, r.Failed, "failed");
                Assert.Equal(2, r.Reused, "reused");
                Assert.Equal(2, r.Streak, "streak");
                Assert.Equal(20, r.StreakTarget, "streak target");
                Assert.Equal(2, r.Running.Count, "two in flight");
                Assert.Equal("0000007", r.Running[0].Id, "id");
                Assert.Near(135000.0, r.Running[0].ElmfireSeconds, 1e-9, "ELMFIRE time");
                Assert.Equal(12804L, r.Running[0].TrackedNodes, "nodes");
                Assert.Equal(started.AddMinutes(2), r.Running[0].StageStartedUtc, "stage start");
                Assert.Equal("WindNinja", r.Running[1].Detail, "detail");
                Assert.Near(-1.0, r.Running[1].ElmfireSeconds, 0.0, "no ELMFIRE time before ELMFIRE");

                //Replaced, while a reader holds the file the way the monitor opens it.
                s.Phase = CampaignStatus.PhaseDone;
                s.Converged = true;
                s.Running.Clear();
                using (new FileStream(Path.Combine(dir, CampaignLayout.StatusFile), FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                {
                    Assert.True(CampaignStatus.Write(dir, s), "replaced while open for reading");
                }
                r = CampaignStatus.Read(dir);
                Assert.Equal(CampaignStatus.PhaseDone, r.Phase, "the new phase");
                Assert.True(r.IsFinished && r.Converged && r.Running.Count == 0, "finished, converged, nothing in flight");
                Assert.True(!File.Exists(Path.Combine(dir, CampaignLayout.StatusFile + ".tmp")), "no temporary file left");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void StatusUnreadable()
        {
            string dir = Directory.CreateTempSubdirectory("preact-status-").FullName;
            try
            {
                Assert.True(CampaignStatus.Read(dir) == null, "no file");
                Assert.True(CampaignStatus.Read(null) == null, "no folder");
                File.WriteAllText(Path.Combine(dir, CampaignLayout.StatusFile), "{\"phase\":\"runn");
                Assert.True(CampaignStatus.Read(dir) == null, "half a file");
                File.WriteAllText(Path.Combine(dir, CampaignLayout.StatusFile), "[1,2]");
                Assert.True(CampaignStatus.Read(dir) == null, "not an object");
                File.WriteAllText(Path.Combine(dir, CampaignLayout.StatusFile), "{\"phase\":\"running\",\"done\":3}");
                CampaignStatus s = CampaignStatus.Read(dir);
                Assert.True(s != null && s.Done == 3 && s.Running.Count == 0, "missing members keep their defaults");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static List<string> RunnerReportsTimesteps()
        {
            var warnings = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                warnings.Add("the fake ELMFIRE is a shell script; skipped on Windows");
                return warnings;
            }

            string dir = Directory.CreateTempSubdirectory("preact-timestep-").FullName;
            try
            {
                //ELMFIRE's own way: a carriage return after each step, a newline only at the end.
                string sh = Path.Combine(dir, "elmfire");
                File.WriteAllText(sh, "#!/bin/sh\n"
                    + "printf '[1] Current Timestep: 30.0 of 7200.0, tracked nodes:      12. Weather bands 1 to 1\\r'\n"
                    + "sleep 1\n"
                    + "printf '[1] Current Timestep: 60.5 of 7200.0, tracked nodes:      40. Weather bands 1 to 1\\r'\n"
                    + "printf '[1] Current Timestep: 95.0 of 7200.0, tracked nodes:     118. Weather bands 1 to 1\\r\\n'\n"
                    + "echo 'Fire area: 0.0 acres.'\n");
                File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                var seen = new List<ElmfireTimestep>();
                var firstAt = new List<double>();
                var clock = Stopwatch.StartNew();
                ElmfireRunner.Result result = ElmfireRunner.Run(sh, dir, "test", new[] { "&MISC", "/" }, false, null,
                    timestep: step =>
                    {
                        lock (seen)
                        {
                            seen.Add(step);
                            firstAt.Add(clock.Elapsed.TotalSeconds);
                        }
                    });

                Assert.Equal(3, seen.Count, "every step reported");
                Assert.Near(30.0, seen[0].Seconds, 1e-9, "first step");
                Assert.Near(95.0, seen[2].Seconds, 1e-9, "last step");
                Assert.Equal(118L, seen[2].TrackedNodes, "its nodes");
                Assert.True(firstAt[0] < clock.Elapsed.TotalSeconds - 0.5, "the first step was reported while ELMFIRE was still running");

                string log = File.ReadAllText(Path.Combine(dir, "elmfire.log"));
                Assert.True(log.Contains("Current Timestep: 60.5") && log.Contains("Fire area"), "the lines still went to elmfire.log");
                Assert.True(!result.Ok, "a fake fire with no rasters is not a fire");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
            return warnings;
        }

        private static void CliMonitorWritesStatus()
        {
            string dir = Directory.CreateTempSubdirectory("preact-monitor-").FullName;
            try
            {
                var c = new Campaign
                {
                    Options = CampaignOptions.Parse(new[] { "--wui", "a.wui", "--max", "12", "--parallel", "2", "--streak", "4" }),
                    Folder = dir,
                };

                using (var monitor = new CampaignMonitor(c))
                {
                    CampaignStatus s = CampaignStatus.Read(dir);
                    Assert.True(s != null && s.Phase == CampaignStatus.PhaseSetup, "written at once, in setup");
                    Assert.Equal(12, s.Max, "max");
                    Assert.Equal(4, s.StreakTarget, "streak target");
                    Assert.Equal(Environment.ProcessId, s.ProcessId, "the process");

                    monitor.SetPhase(CampaignStatus.PhaseRunning);
                    monitor.Begin(3);
                    monitor.Stage(3, RealizationProgress.StageWeather, "drawing the ignition");
                    monitor.Detail(3, "WindNinja");
                    monitor.Begin(4);
                    monitor.Stage(4, RealizationProgress.StageElmfire, "starting");
                    monitor.ElmfireTimestep(4, new ElmfireTimestep { Seconds = 7200, StopSeconds = 31536000, TrackedNodes = 99 });
                    monitor.SetCounts(2, 1, 1, 0, 0, 0, 0, false);

                    s = WaitFor(dir, x => x.Running.Count == 2, 5000);
                    Assert.True(s != null, "the timer rewrote it within a few seconds");
                    Assert.Equal(CampaignStatus.PhaseRunning, s.Phase, "running");
                    Assert.Equal("weather: WindNinja", s.Running[0].Describe(true), "realization 3's stage");
                    Assert.Equal("ELMFIRE 2.0 h, 99 nodes (until it stops)", s.Running[1].Describe(true), "realization 4's timestep");
                    Assert.Equal(1, s.Ok, "counts");

                    monitor.Stage(4, RealizationProgress.StageEvacuation, "starting PREACT");
                    monitor.EvacuationTime(4, 3600, 86400);
                    monitor.End(3);
                    s = WaitFor(dir, x => x.Running.Count == 1, 5000);
                    Assert.True(s != null, "realization 3 left");
                    Assert.Equal("evacuation 1.0 of 24.0 h (starting PREACT)", s.Running[0].Describe(true), "a new stage starts clean");
                    Assert.Near(-1.0, s.Running[0].ElmfireSeconds, 0.0, "the ELMFIRE time went with its stage");

                    monitor.Message = "reached --max 12 realizations without converging";
                    monitor.Finish(0, monitor.Message);
                }

                CampaignStatus last = CampaignStatus.Read(dir);
                Assert.Equal(CampaignStatus.PhaseDone, last.Phase, "done");
                Assert.Equal(0, last.Running.Count, "nothing in flight once it ended");
                Assert.True(last.Message.Contains("without converging"), "with what the end said");
                Thread.Sleep(1500);
                Assert.Equal(CampaignStatus.PhaseDone, CampaignStatus.Read(dir).Phase, "and nothing overwrote it afterwards");

                var cancelled = new CampaignMonitor(c);
                cancelled.Finish(3, "cancelled");
                Assert.Equal(CampaignStatus.PhaseCancelled, CampaignStatus.Read(dir).Phase, "exit 3 is cancelled");
                var failed = new CampaignMonitor(c);
                failed.Finish(1, "no boundary");
                Assert.Equal(CampaignStatus.PhaseFailed, CampaignStatus.Read(dir).Phase, "exit 1 is failed");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static CampaignStatus WaitFor(string dir, Func<CampaignStatus, bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < milliseconds)
            {
                CampaignStatus s = CampaignStatus.Read(dir);
                if (s != null && condition(s)) return s;
                Thread.Sleep(100);
            }
            return null;
        }

        private static void EvacuationProgress()
        {
            Assert.True(RealizationRunner.TryParseSimulationTime(CampaignLayout.SimulationTimeTag + "3600 of 86400 s", out double t, out double end),
                "SIM_TIME parses");
            Assert.Near(3600, t, 0, "time");
            Assert.Near(86400, end, 0, "end");
            Assert.True(!RealizationRunner.TryParseSimulationTime("[09:26:31] [Simulation# 0, sim time 2026-06-28 12:35:53 (0 s in)] AscFire started.", out _, out _),
                "another line is not one");

            Assert.Equal("loading the road network",
                RealizationRunner.DescribeStep("Loading net-file from 'D:\\WUINITY\\cases\\mati_generated\\sumo\\osm.net.xml' ... done (15418ms)."),
                "the SUMO network");
            Assert.Equal("evacuating", RealizationRunner.DescribeStep("[09:26:31] [Simulation# 0] Traffic module SUMO initiated."), "SUMO started");
            Assert.Equal("k-PERIL boundary",
                RealizationRunner.DescribeStep("[09:31:07] Starting calculation of trigger buffer using k-PERIL. RSET = 180.31667 minutes."), "k-PERIL");
            Assert.True(RealizationRunner.DescribeStep("Warning: Teleporting vehicle '610'") == null, "SUMO's chatter is not a step");
        }

        private static void CampaignTables()
        {
            string dir = Directory.CreateTempSubdirectory("preact-tables-").FullName;
            try
            {
                File.WriteAllLines(Path.Combine(dir, CampaignLayout.RealizationsCsv), new[]
                {
                    "realization,status,reused,message,fire_area_acres,elmfire_minutes,ignition_x,ignition_y,wind_from_deg,mean_wind_10m_mph,dead_1h_pct,live_herbaceous_pct,live_woody_pct,evacuation_seed",
                    "0000001,ok,false,\"\",4210.5,6.25,1,2,3,4,5,6,7,2000013",
                    "0000002,not-threatened,true,\"the fire never reached the WUI area (7383 cells)\",12,,1,2,3,4,5,,,",
                    "0000003,failed,false,\"ELMFIRE failed: Error, opening 'x' | more\",,,,,,,,,,",
                });
                List<FinishedRealization> rows = CampaignFiles.ReadRealizations(dir);
                Assert.Equal(3, rows.Count, "three rows");
                Assert.Equal("0000001", rows[0].Id, "file order");
                Assert.Near(4210.5, rows[0].FireAreaAcres, 1e-9, "area");
                Assert.Near(6.25, rows[0].ElmfireMinutes, 1e-9, "minutes");
                Assert.True(rows[1].Reused && double.IsNaN(rows[1].ElmfireMinutes), "reused, no ELMFIRE time");
                Assert.Equal("the fire never reached the WUI area (7383 cells)", rows[1].Message, "message");
                Assert.Equal("ELMFIRE failed: Error, opening 'x' | more", rows[2].Message, "a message with a comma stays one field");
                Assert.Equal(CampaignLayout.StatusFailed, rows[2].Status, "status");

                Assert.True(CampaignFiles.ReadLatestConvergence(dir) == null, "no convergence CSV");
                File.WriteAllLines(Path.Combine(dir, CampaignLayout.ConvergenceCsv), new[]
                {
                    "boundaries,realization_id,streak,area_p10,area_p20,area_p30,area_p40,area_p50,area_p60,area_p70,area_p80,area_p90,area_p100,delta_p10,delta_p20,delta_p30,delta_p40,delta_p50,delta_p60,delta_p70,delta_p80,delta_p90,delta_p100",
                    "1,0000001,0,10,9,8,7,6,5,4,3,2,1,,,,,,,,,,",
                    "2,0000004,1,11,9,8,7,6,5,4,3,2,0,0.1,0,0,0,0,0,0,0,0,",
                });
                ConvergenceSnapshot c = CampaignFiles.ReadLatestConvergence(dir);
                Assert.True(c != null, "read");
                Assert.Equal(2, c.Boundaries, "the last row");
                Assert.Equal("0000004", c.RealizationId, "its realization");
                Assert.Equal(1, c.Streak, "streak");
                Assert.Equal(10, c.Area.Length, "ten deciles");
                Assert.Near(0.1, c.Deciles[0], 1e-12, "p10");
                Assert.Near(1.0, c.Deciles[9], 1e-12, "p100");
                Assert.Near(11, c.Area[0], 0, "area");
                Assert.Near(0.1, c.Delta[0].Value, 1e-12, "delta");
                Assert.True(!c.Delta[9].HasValue, "a decile with no baseline has no delta");

                string log = Path.Combine(dir, CampaignLayout.CampaignLogFile);
                File.WriteAllLines(log, Enumerable.Range(1, 5000).Select(i => "line " + i));
                List<string> tail = CampaignFiles.ReadTail(log, 2000, 20000);
                Assert.Equal("line 5000", tail[tail.Count - 1], "the tail ends at the end");
                Assert.True(tail.Count <= 2000 && tail.Count > 1000, $"bounded ({tail.Count} lines)");
                Assert.True(tail[0].StartsWith("line ", StringComparison.Ordinal) && tail[0].Length > 5, "no partial first line");
                Assert.Equal(2000, CampaignFiles.ReadTail(log, 2000).Count, "at most the lines asked for");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
