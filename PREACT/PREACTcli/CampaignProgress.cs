using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PREACTcli
{
    /// <summary>
    /// Tracks what each parallel worker is doing, and says so on stdout often enough to be watched.
    /// </summary>
    /// <remarks>
    /// A campaign used to be observable only at realization boundaries: <c>PROGRESS</c> and
    /// <c>PROGRESS_JSON</c> are emitted when one finishes, so at --parallel width the whole run went quiet
    /// for as long as the slowest realization took. A multi-minute ELMFIRE run and a hung one produced the
    /// same output - nothing - which is the state this class exists to tell apart.
    ///
    /// Two things had to be introduced for that. The first is worker identity: the driver launches with
    /// <c>Task.Run</c> under a count gate, so the tasks are anonymous and there is no "core 3" to report
    /// against. A slot is claimed from a fixed pool before the work starts and returned in a finally, which
    /// makes the pool exactly as wide as --parallel and gives the display a stable set of rows rather than a
    /// list that reorders itself. The second is progress *inside* a stage, which for ELMFIRE means reading
    /// its own <c>dump_times_*.csv</c>: it rewrites that file as the fire advances, so the last row is the
    /// simulated time reached. That needs no change to ELMFIRE and distinguishes "advancing slowly" from
    /// "stopped", which is the question a stalled realization poses.
    ///
    /// Everything here is best-effort. A progress display that throws, or that blocks a worker while it
    /// reads a file ELMFIRE is writing, would be worse than no display at all, so every read is guarded and
    /// every file is opened shared.
    /// </remarks>
    internal sealed class CampaignProgress : IDisposable
    {
        /// <summary>Stage names. Strings rather than an enum because they are display text.</summary>
        public const string StageWeather = "weather";
        public const string StageNamelist = "namelist";
        public const string StageElmfire = "elmfire";
        public const string StageBoundary = "boundary";

        private sealed class Slot
        {
            public string Realization;          //null when idle
            public string Stage;
            public string Detail;
            public DateTime StageStarted;
            public DateTime RealizationStarted;
            public string RunDir;
        }

        private readonly Slot[] _slots;
        private readonly Stack<int> _free = new Stack<int>();
        private readonly object _sync = new object();
        private readonly double _tstopSeconds;
        private readonly Timer _timer;
        private bool _disposed;

        public CampaignProgress(int parallelism, double tstopSeconds)
        {
            _tstopSeconds = tstopSeconds;
            _slots = new Slot[Math.Max(1, parallelism)];

            //Pushed in reverse so the first realization claims slot 0, which makes a short campaign fill the
            //table from the top instead of from wherever the stack happened to start.
            for (int i = 0; i < _slots.Length; ++i) _slots[i] = new Slot();
            for (int i = _slots.Length - 1; i >= 0; --i) _free.Push(i);

            //Independent of stage changes: the point is to report an ELMFIRE run that is advancing without
            //changing stage, which by definition emits nothing of its own.
            _timer = new Timer(_ => { try { Emit(); } catch { } }, null,
                               TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Claims a slot for a realization, or -1 if the pool is somehow empty.
        /// </summary>
        /// <remarks>
        /// -1 rather than blocking or throwing: the pool is sized to --parallel and the driver never exceeds
        /// it, so an empty pool means a bookkeeping bug here. Losing a row from a progress table is the right
        /// consequence of that; stalling the campaign is not.
        /// </remarks>
        public int Acquire(string realization, string runDir)
        {
            lock (_sync)
            {
                if (_free.Count == 0) return -1;
                int slot = _free.Pop();
                _slots[slot].Realization = realization;
                _slots[slot].RunDir = runDir;
                _slots[slot].Stage = StageWeather;
                _slots[slot].Detail = null;
                _slots[slot].StageStarted = DateTime.UtcNow;
                _slots[slot].RealizationStarted = DateTime.UtcNow;
                return slot;
            }
        }

        public void Release(int slot)
        {
            if (slot < 0) return;
            lock (_sync)
            {
                if (_slots[slot].Realization == null) return;   //released twice
                _slots[slot].Realization = null;
                _slots[slot].Stage = null;
                _slots[slot].Detail = null;
                _slots[slot].RunDir = null;
                _free.Push(slot);
            }
            Emit();
        }

        /// <summary>Moves a slot to a new stage and reports immediately.</summary>
        public void Stage(int slot, string stage, string detail = null)
        {
            if (slot < 0) return;
            lock (_sync)
            {
                if (_slots[slot].Realization == null) return;
                _slots[slot].Stage = stage;
                _slots[slot].StageStarted = DateTime.UtcNow;
                if (detail != null) _slots[slot].Detail = detail;
            }
            Emit();
        }

        /// <summary>
        /// Sets the descriptive text a slot carries - what this realization drew - without changing stage.
        /// </summary>
        public void Detail(int slot, string detail)
        {
            if (slot < 0) return;
            lock (_sync)
            {
                if (_slots[slot].Realization == null) return;
                _slots[slot].Detail = detail;
            }
        }

        /// <summary>
        /// Writes the whole table as one line, so a reader never sees a half-updated set of rows.
        /// </summary>
        private void Emit()
        {
            var sb = new StringBuilder(256);
            sb.Append("PROGRESS_SLOTS {\"tstopHours\":");
            sb.Append((_tstopSeconds / 3600.0).ToString("0.##", CultureInfo.InvariantCulture));
            sb.Append(",\"slots\":[");

            //Snapshotted under the lock, then formatted outside it: reading dump_times touches the disk and
            //holding the lock across that would make every stage transition wait on it.
            var rows = new List<(int Slot, string Realization, string Stage, string Detail, double Stage_s, double Total_s, string RunDir)>();
            lock (_sync)
            {
                DateTime now = DateTime.UtcNow;
                for (int i = 0; i < _slots.Length; ++i)
                {
                    Slot s = _slots[i];
                    if (s.Realization == null) continue;
                    rows.Add((i, s.Realization, s.Stage, s.Detail,
                              (now - s.StageStarted).TotalSeconds,
                              (now - s.RealizationStarted).TotalSeconds,
                              s.RunDir));
                }
            }

            for (int i = 0; i < rows.Count; ++i)
            {
                var r = rows[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"slot\":").Append(r.Slot.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"realization\":\"").Append(Escape(r.Realization)).Append('"');
                sb.Append(",\"stage\":\"").Append(Escape(r.Stage ?? "")).Append('"');
                sb.Append(",\"stageSeconds\":").Append(r.Stage_s.ToString("0.#", CultureInfo.InvariantCulture));
                sb.Append(",\"totalSeconds\":").Append(r.Total_s.ToString("0.#", CultureInfo.InvariantCulture));

                //Only meaningful while ELMFIRE is the running stage; before that there is no file, and after
                //it the value would be the last fire's rather than this one's.
                if (r.Stage == StageElmfire && TryReadSimulatedSeconds(r.RunDir, out double fireSeconds))
                {
                    sb.Append(",\"fireHours\":")
                      .Append((fireSeconds / 3600.0).ToString("0.##", CultureInfo.InvariantCulture));
                }

                if (!string.IsNullOrEmpty(r.Detail))
                {
                    sb.Append(",\"detail\":\"").Append(Escape(r.Detail)).Append('"');
                }
                sb.Append('}');
            }

            sb.Append("]}");
            Console.WriteLine(sb.ToString());
        }

        /// <summary>
        /// How far the fire has got, from ELMFIRE's own dump-time log, or false when it has not written one.
        /// </summary>
        /// <remarks>
        /// <c>dump_times_&lt;case&gt;.csv</c> is <c>dump_index,time_seconds,is_final_dump</c> and grows a row per
        /// dump, so the last parseable row is the simulated time reached. Opened with
        /// <see cref="FileShare.ReadWrite"/> because ELMFIRE has it open for writing; without that this throws
        /// on every call and the fire time never appears.
        /// </remarks>
        private static bool TryReadSimulatedSeconds(string runDir, out double seconds)
        {
            seconds = 0;
            if (string.IsNullOrEmpty(runDir)) return false;

            try
            {
                string outputs = Path.Combine(runDir, "outputs");
                if (!Directory.Exists(outputs)) return false;

                string[] files = Directory.GetFiles(outputs, "dump_times_*.csv");
                if (files.Length == 0) return false;

                bool any = false;
                foreach (string file in files)
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);

                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] parts = line.Split(',');
                        if (parts.Length < 2) continue;
                        if (double.TryParse(parts[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture,
                                out double t))
                        {
                            //Max rather than last: with more than one case per run directory the files are
                            //independent, and the furthest-advanced one is what "this realization has reached"
                            //means.
                            if (!any || t > seconds) seconds = t;
                            any = true;
                        }
                    }
                }
                return any;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// What ELMFIRE's own statistics say this realization's fire did, as a phrase, or null.
        /// </summary>
        /// <remarks>
        /// Read so that a realization producing no trigger boundary can say why on the line that reports it.
        /// "no boundary" alone is indistinguishable between a fire that never reached the community and one
        /// whose boundary computation failed, and the two call for opposite responses - the first is a fuel or
        /// ignition problem, the second a bug. <c>fire_size_stats.csv</c> answers it directly: a fire that
        /// stalled at a fraction of an acre reached nothing.
        ///
        /// Columns are taken by header name, not position: the file carries twenty of them and upstream has
        /// added to it before.
        /// </remarks>
        public static string DescribeFire(string runDir)
        {
            if (string.IsNullOrEmpty(runDir)) return null;

            try
            {
                string path = Path.Combine(runDir, "outputs", "fire_size_stats.csv");
                if (!File.Exists(path)) return null;

                string[] lines = File.ReadAllLines(path);
                if (lines.Length < 2) return null;

                string[] header = lines[0].Split(',');
                int areaCol = -1, tstopCol = -1;
                for (int i = 0; i < header.Length; ++i)
                {
                    string h = header[i].Trim();
                    if (h.Equals("Total fire area (ac)", StringComparison.OrdinalIgnoreCase)) areaCol = i;
                    else if (h.Equals("tstop (h)", StringComparison.OrdinalIgnoreCase)) tstopCol = i;
                }
                if (areaCol < 0) return null;

                //The last data row: one per ensemble member, and this driver runs one member per realization.
                string[] row = lines[lines.Length - 1].Split(',');
                if (row.Length <= areaCol) return null;

                if (!double.TryParse(row[areaCol].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture,
                        out double acres))
                {
                    return null;
                }

                if (tstopCol >= 0 && tstopCol < row.Length
                    && double.TryParse(row[tstopCol].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture,
                        out double hours))
                {
                    return $"fire stalled at {acres:0.#} ac after {hours:0.#} h";
                }
                return $"fire reached {acres:0.#} ac";
            }
            catch
            {
                return null;
            }
        }

        private static string Escape(string s)
        {
            return s == null ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
    }
}
