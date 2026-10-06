using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>
    /// Keeps the campaign's <see cref="CampaignLayout.StatusFile"/> current: the phase and counts the aggregation loop
    /// reports, and the stage of every realization in flight as its threads report it. Written once a second by a
    /// timer, never by the threads that report, so a realization's ELMFIRE printing a timestep a millisecond costs a
    /// few field writes and nothing on disk.
    /// </summary>
    internal sealed class CampaignMonitor : IDisposable
    {
        /// <summary>How often the status file is rewritten while the campaign runs.</summary>
        public const int PeriodMs = 1000;

        private readonly string _folder;
        private readonly object _sync = new object();
        private readonly CampaignStatus _status;
        private readonly Dictionary<int, RealizationProgress> _running = new Dictionary<int, RealizationProgress>();
        private Timer _timer;
        private bool _finished;

        public CampaignMonitor(Campaign c)
        {
            _folder = c.Folder;
            CampaignOptions o = c.Options;
            _status = new CampaignStatus
            {
                Phase = CampaignStatus.PhaseSetup,
                ProcessId = Process.GetCurrentProcess().Id,
                StartedUtc = DateTime.UtcNow,
                FireHours = o.Hours,
                MaxRuntimeSeconds = o.MaxRuntimeSeconds,
                Parallel = o.Parallelism,
                Max = o.MaxRealizations,
                StreakTarget = o.Streak,
            };
            WriteNow();
            _timer = new Timer(_ => WriteNow(), null, PeriodMs, PeriodMs);
        }

        public void SetPhase(string phase)
        {
            lock (_sync) _status.Phase = phase;
        }

        /// <summary>The counts after a realization has been folded in (or the loop has launched another).</summary>
        public void SetCounts(int launched, int done, int ok, int notThreatened, int failed, int reused, int streak, bool converged)
        {
            lock (_sync)
            {
                _status.Launched = launched;
                _status.Done = done;
                _status.Ok = ok;
                _status.NotThreatened = notThreatened;
                _status.Failed = failed;
                _status.Reused = reused;
                _status.Streak = streak;
                _status.Converged = converged;
            }
        }

        /// <summary>What the campaign's end says (a failure's reason, or that it did not converge), for the final status.</summary>
        public string Message { get; set; }

        public void SetLaunched(int launched)
        {
            lock (_sync) _status.Launched = launched;
        }

        // ------------------------------------------------------------------ realizations in flight

        public void Begin(int index)
        {
            DateTime now = DateTime.UtcNow;
            lock (_sync)
            {
                _running[index] = new RealizationProgress
                {
                    Index = index,
                    Id = CampaignLayout.RealizationId(index),
                    Stage = RealizationProgress.StageStarting,
                    StartedUtc = now,
                    StageStartedUtc = now,
                };
            }
        }

        /// <summary>A realization moves to <paramref name="stage"/>; the stage's own progress starts again.</summary>
        public void Stage(int index, string stage, string detail = "")
        {
            lock (_sync)
            {
                if (!_running.TryGetValue(index, out RealizationProgress r)) return;
                if (r.Stage != stage)
                {
                    r.Stage = stage;
                    r.StageStartedUtc = DateTime.UtcNow;
                    r.ElmfireSeconds = r.ElmfireStopSeconds = -1.0;
                    r.TrackedNodes = -1;
                    r.EvacuationSeconds = r.EvacuationEndSeconds = -1.0;
                }
                r.Detail = detail ?? string.Empty;
            }
        }

        /// <summary>Within the current stage: what it is doing now.</summary>
        public void Detail(int index, string detail)
        {
            lock (_sync)
            {
                if (_running.TryGetValue(index, out RealizationProgress r)) r.Detail = detail ?? string.Empty;
            }
        }

        public void ElmfireTimestep(int index, ElmfireTimestep step)
        {
            lock (_sync)
            {
                if (!_running.TryGetValue(index, out RealizationProgress r)) return;
                r.ElmfireSeconds = step.Seconds;
                r.ElmfireStopSeconds = step.StopSeconds;
                r.TrackedNodes = step.TrackedNodes;
                r.Detail = string.Empty;
            }
        }

        public void EvacuationTime(int index, double seconds, double endSeconds)
        {
            lock (_sync)
            {
                if (!_running.TryGetValue(index, out RealizationProgress r)) return;
                r.EvacuationSeconds = seconds;
                r.EvacuationEndSeconds = endSeconds;
            }
        }

        public void End(int index)
        {
            lock (_sync) _running.Remove(index);
        }

        // ------------------------------------------------------------------ the end

        /// <summary>
        /// The campaign's last status, from its exit code: 0 done, 3 cancelled, anything else failed. Written at once;
        /// nothing is written after it.
        /// </summary>
        public void Finish(int exitCode, string message)
        {
            Timer timer = Interlocked.Exchange(ref _timer, null);
            timer?.Dispose();

            lock (_sync)
            {
                _status.Phase = exitCode == 0 ? CampaignStatus.PhaseDone
                    : exitCode == 3 ? CampaignStatus.PhaseCancelled
                    : CampaignStatus.PhaseFailed;
                _status.Message = message ?? string.Empty;
                _running.Clear();
                WriteLocked();
                _finished = true;
            }
        }

        public void Dispose()
        {
            Timer timer = Interlocked.Exchange(ref _timer, null);
            timer?.Dispose();
        }

        private void WriteNow()
        {
            lock (_sync)
            {
                if (_finished) return;
                WriteLocked();
            }
        }

        private void WriteLocked()
        {
            _status.UpdatedUtc = DateTime.UtcNow;
            _status.Running = _running.Values.OrderBy(r => r.Index).Select(r => r.Copy()).ToList();
            try
            {
                CampaignStatus.Write(_folder, _status);
            }
            catch (Exception)
            {
                //The status is for watching; a campaign never stops over it.
            }
        }
    }

    /// <summary>
    /// Copies everything the campaign prints, stdout and stderr, into <see cref="CampaignLayout.CampaignLogFile"/> in its
    /// folder, so the log can be read by a window that did not start the campaign. What was printed before the folder
    /// was known is kept and written first.
    /// </summary>
    internal static class CampaignLog
    {
        private static TeeWriter _out, _err;
        private static readonly object _sync = new object();
        private static StringWriter _early = new StringWriter();
        private static StreamWriter _file;

        public static void Install()
        {
            if (_out != null) return;
            _out = new TeeWriter(Console.Out);
            _err = new TeeWriter(Console.Error);
            Console.SetOut(_out);
            Console.SetError(_err);
        }

        /// <summary>From here on the copy goes to the campaign folder's log, after a line saying a run started.</summary>
        public static void Attach(string folder)
        {
            if (_out == null) return;
            lock (_sync)
            {
                if (_file != null) return;
                try
                {
                    var stream = new FileStream(Path.Combine(folder, CampaignLayout.CampaignLogFile), FileMode.Append,
                        FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _file = new StreamWriter(stream) { AutoFlush = true };
                    _file.WriteLine($"==== converge-trigger started {DateTime.Now:yyyy-MM-dd HH:mm:ss} (pid {Environment.ProcessId}) ====");
                    _file.Write(_early.ToString());
                }
                catch (Exception e)
                {
                    _file = null;
                    _out.Inner.WriteLine("WARNING: could not open the campaign log: " + e.Message);
                }
                _early = null;
            }
        }

        public static void Close()
        {
            lock (_sync)
            {
                _file?.Dispose();
                _file = null;
            }
        }

        private static void Copy(string text)
        {
            lock (_sync)
            {
                if (_file != null)
                {
                    try { _file.Write(text); } catch { }
                }
                else
                {
                    _early?.Write(text);
                }
            }
        }

        private sealed class TeeWriter : TextWriter
        {
            public readonly TextWriter Inner;

            public TeeWriter(TextWriter inner)
            {
                Inner = inner;
            }

            public override System.Text.Encoding Encoding => Inner.Encoding;

            public override void Write(char value)
            {
                lock (_sync)
                {
                    Inner.Write(value);
                    Copy(value.ToString());
                }
            }

            public override void Write(string value)
            {
                lock (_sync)
                {
                    Inner.Write(value);
                    Copy(value);
                }
            }

            public override void WriteLine(string value)
            {
                lock (_sync)
                {
                    Inner.WriteLine(value);
                    Copy(value + Environment.NewLine);
                }
            }

            public override void Flush() => Inner.Flush();
        }
    }
}
