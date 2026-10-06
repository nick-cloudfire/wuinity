using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PREACT.Utility
{
    /// <summary>
    /// What a trigger campaign is doing now: its phase, its counts, and where every realization in flight is - which
    /// weather stage, which ELMFIRE timestep, how far its evacuation has got. <c>PREACTcli converge-trigger</c> writes it
    /// to <see cref="CampaignLayout.StatusFile"/> in the campaign folder about once a second; the GUI's campaign monitor
    /// reads it at the same rate, whether it started the campaign or found it running (or finished) on disk.
    /// </summary>
    /// <remarks>
    /// A file rather than more tagged stdout lines: a window opened after the campaign started - or after the GUI was
    /// restarted - has no stdout to read, and a per-timestep line from every ELMFIRE in flight would be thousands of
    /// lines a minute. The file is replaced whole (written beside it, then swapped in), so a reader never sees half of it.
    /// </remarks>
    public sealed class CampaignStatus
    {
        public const int FormatVersion = 1;

        // ---- phases
        public const string PhaseSetup = "setup";
        public const string PhaseRunning = "running";
        public const string PhaseAggregating = "aggregating";
        public const string PhaseDone = "done";
        public const string PhaseFailed = "failed";
        public const string PhaseCancelled = "cancelled";

        /// <summary>setup, running, aggregating, done, failed or cancelled.</summary>
        public string Phase = PhaseSetup;

        /// <summary>What ended the campaign, for failed and cancelled (and done without converging).</summary>
        public string Message = string.Empty;

        /// <summary>The CLI's process id, so a reader can tell a campaign that died from one that is still going.</summary>
        public int ProcessId;

        public DateTime StartedUtc;
        public DateTime UpdatedUtc;

        /// <summary>Hours of fire per realization, or <see cref="CampaignLayout.UntilStoppedHours"/>.</summary>
        public double FireHours;
        public double MaxRuntimeSeconds;
        public int Parallel;

        public int Max;
        public int Launched;
        public int Done;
        public int Ok;
        public int NotThreatened;
        public int Failed;
        public int Reused;
        public int Streak;
        public int StreakTarget;
        public bool Converged;

        /// <summary>The realizations in flight, by index.</summary>
        public List<RealizationProgress> Running = new List<RealizationProgress>();

        public bool IsFinished => Phase == PhaseDone || Phase == PhaseFailed || Phase == PhaseCancelled;

        public bool UntilStopped => CampaignLayout.IsUntilStopped(FireHours);

        // ------------------------------------------------------------------ writing

        public string ToJson()
        {
            using (var stream = new MemoryStream())
            {
                //Relaxed escaping: the file is read by people too, and "it's" is clearer than "it\u0027s". It is never embedded in HTML.
                var options = new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                using (var w = new Utf8JsonWriter(stream, options))
                {
                    w.WriteStartObject();
                    w.WriteNumber("version", FormatVersion);
                    w.WriteString("phase", Phase ?? PhaseSetup);
                    w.WriteString("message", Message ?? string.Empty);
                    w.WriteNumber("pid", ProcessId);
                    w.WriteString("started", Iso(StartedUtc));
                    w.WriteString("updated", Iso(UpdatedUtc));
                    w.WriteNumber("fireHours", FireHours);
                    w.WriteNumber("maxRuntimeSeconds", MaxRuntimeSeconds);
                    w.WriteNumber("parallel", Parallel);
                    w.WriteNumber("max", Max);
                    w.WriteNumber("launched", Launched);
                    w.WriteNumber("done", Done);
                    w.WriteNumber("ok", Ok);
                    w.WriteNumber("notThreatened", NotThreatened);
                    w.WriteNumber("failed", Failed);
                    w.WriteNumber("reused", Reused);
                    w.WriteNumber("streak", Streak);
                    w.WriteNumber("streakTarget", StreakTarget);
                    w.WriteBoolean("converged", Converged);

                    w.WriteStartArray("running");
                    foreach (RealizationProgress r in Running)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("index", r.Index);
                        w.WriteString("id", r.Id ?? string.Empty);
                        w.WriteString("stage", r.Stage ?? string.Empty);
                        w.WriteString("detail", r.Detail ?? string.Empty);
                        w.WriteString("started", Iso(r.StartedUtc));
                        w.WriteString("stageStarted", Iso(r.StageStartedUtc));
                        w.WriteNumber("elmfireSeconds", r.ElmfireSeconds);
                        w.WriteNumber("elmfireStopSeconds", r.ElmfireStopSeconds);
                        w.WriteNumber("trackedNodes", r.TrackedNodes);
                        w.WriteNumber("evacuationSeconds", r.EvacuationSeconds);
                        w.WriteNumber("evacuationEndSeconds", r.EvacuationEndSeconds);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        /// <summary>
        /// Writes the status into <paramref name="campaignFolder"/>: to a temporary file beside it, then swapped in, so a
        /// reader sees the previous status or this one and never a half-written file. False when it could not be written
        /// this time (a reader holding the file without delete sharing, on Windows); the next write tries again.
        /// </summary>
        public static bool Write(string campaignFolder, CampaignStatus status)
        {
            string path = Path.Combine(campaignFolder, CampaignLayout.StatusFile);
            string temp = path + ".tmp";
            try
            {
                File.WriteAllText(temp, status.ToJson());
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            for (int attempt = 0; attempt < 3; ++attempt)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Replace(temp, path, null);
                    }
                    else
                    {
                        File.Move(temp, path);
                    }
                    return true;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                System.Threading.Thread.Sleep(20);
            }

            try { File.Delete(temp); } catch { }
            return false;
        }

        // ------------------------------------------------------------------ reading

        /// <summary>
        /// The status in <paramref name="campaignFolder"/>, or null when there is none or it cannot be read now. Opened
        /// with every kind of sharing, so the writer can replace it while it is being read.
        /// </summary>
        public static CampaignStatus Read(string campaignFolder)
        {
            if (string.IsNullOrEmpty(campaignFolder)) return null;
            string path = Path.Combine(campaignFolder, CampaignLayout.StatusFile);
            try
            {
                if (!File.Exists(path)) return null;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                           FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return Parse(reader.ReadToEnd());
                }
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>A status from its JSON, or null when it is not one. Missing members keep their defaults.</summary>
        public static CampaignStatus Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    JsonElement root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return null;

                    var s = new CampaignStatus
                    {
                        Phase = Str(root, "phase", PhaseSetup),
                        Message = Str(root, "message", string.Empty),
                        ProcessId = Int(root, "pid"),
                        StartedUtc = Time(root, "started"),
                        UpdatedUtc = Time(root, "updated"),
                        FireHours = Num(root, "fireHours", 0.0),
                        MaxRuntimeSeconds = Num(root, "maxRuntimeSeconds", 0.0),
                        Parallel = Int(root, "parallel"),
                        Max = Int(root, "max"),
                        Launched = Int(root, "launched"),
                        Done = Int(root, "done"),
                        Ok = Int(root, "ok"),
                        NotThreatened = Int(root, "notThreatened"),
                        Failed = Int(root, "failed"),
                        Reused = Int(root, "reused"),
                        Streak = Int(root, "streak"),
                        StreakTarget = Int(root, "streakTarget"),
                        Converged = root.TryGetProperty("converged", out JsonElement c) && c.ValueKind == JsonValueKind.True,
                    };

                    if (root.TryGetProperty("running", out JsonElement running) && running.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement e in running.EnumerateArray())
                        {
                            if (e.ValueKind != JsonValueKind.Object) continue;
                            s.Running.Add(new RealizationProgress
                            {
                                Index = Int(e, "index"),
                                Id = Str(e, "id", string.Empty),
                                Stage = Str(e, "stage", string.Empty),
                                Detail = Str(e, "detail", string.Empty),
                                StartedUtc = Time(e, "started"),
                                StageStartedUtc = Time(e, "stageStarted"),
                                ElmfireSeconds = Num(e, "elmfireSeconds", -1.0),
                                ElmfireStopSeconds = Num(e, "elmfireStopSeconds", -1.0),
                                TrackedNodes = (long)Num(e, "trackedNodes", -1.0),
                                EvacuationSeconds = Num(e, "evacuationSeconds", -1.0),
                                EvacuationEndSeconds = Num(e, "evacuationEndSeconds", -1.0),
                            });
                        }
                    }
                    return s;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Iso(DateTime utc) => utc == default
            ? string.Empty
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        private static string Str(JsonElement e, string name, string fallback) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;

        private static double Num(JsonElement e, string name, double fallback) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? d : fallback;

        private static int Int(JsonElement e, string name) => (int)Num(e, name, 0.0);

        private static DateTime Time(JsonElement e, string name)
        {
            string text = Str(e, name, null);
            return !string.IsNullOrEmpty(text) && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)
                ? DateTime.SpecifyKind(t, DateTimeKind.Utc)
                : default;
        }

        // ------------------------------------------------------------------ for people

        /// <summary>"12 of at most 200 done: 7 boundaries, 3 not threatened, 2 failed".</summary>
        public string DescribeCounts()
        {
            return $"{Done} of at most {Max} done: {Ok} boundar{(Ok == 1 ? "y" : "ies")}, {NotThreatened} not threatened, {Failed} failed"
                   + (Reused > 0 ? $" ({Reused} reused)" : string.Empty);
        }

        /// <summary>A duration as "42 s", "7 min 05 s" or "3 h 12 min".</summary>
        public static string DescribeDuration(TimeSpan span)
        {
            if (span < TimeSpan.Zero) span = TimeSpan.Zero;
            if (span.TotalSeconds < 60) return ((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
            if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min {span.Seconds:00} s";
            return $"{(int)span.TotalHours} h {span.Minutes:00} min";
        }
    }

    /// <summary>One realization in flight: where it is in weather, fire and evacuation.</summary>
    public sealed class RealizationProgress
    {
        // ---- stages, in order
        public const string StageStarting = "starting";
        public const string StageWeather = "weather";
        public const string StageElmfire = "elmfire";
        public const string StageEvacuation = "evacuation";

        public int Index;
        public string Id;

        /// <summary>starting, weather, elmfire or evacuation.</summary>
        public string Stage = StageStarting;

        /// <summary>What within the stage: "drawing", "WindNinja", "fuel moisture", "loading the road network", ...</summary>
        public string Detail = string.Empty;

        public DateTime StartedUtc;
        public DateTime StageStartedUtc;

        /// <summary>ELMFIRE's simulated time from its last "Current Timestep" line, seconds; -1 before the first.</summary>
        public double ElmfireSeconds = -1.0;

        /// <summary>ELMFIRE's SIMULATION_TSTOP as it reports it, seconds; -1 before the first timestep.</summary>
        public double ElmfireStopSeconds = -1.0;

        /// <summary>The fire front's tracked nodes at that timestep; -1 when not known.</summary>
        public long TrackedNodes = -1;

        /// <summary>The evacuation's simulated time, seconds, from PREACT.exe's progress lines; -1 when not known.</summary>
        public double EvacuationSeconds = -1.0;

        /// <summary>The evacuation's end time, seconds; -1 when not known.</summary>
        public double EvacuationEndSeconds = -1.0;

        public RealizationProgress Copy() => (RealizationProgress)MemberwiseClone();

        /// <summary>
        /// Where the realization is, for a table cell: "weather: WindNinja", "ELMFIRE 37.5 h, 12,804 nodes (until it
        /// stops)", "ELMFIRE 37.5 of 72 h, 12,804 nodes", "evacuation 3.2 of 24 h".
        /// </summary>
        /// <param name="untilStopped">
        /// The campaign runs each fire until it stops: its SIMULATION_TSTOP is a year that is never reached, so "of 8760 h"
        /// would read as a percentage that never moves.
        /// </param>
        public string Describe(bool untilStopped)
        {
            switch (Stage)
            {
                case StageWeather:
                    return "weather" + (string.IsNullOrEmpty(Detail) ? string.Empty : ": " + Detail);
                case StageElmfire:
                    return "ELMFIRE " + DescribeElmfire(untilStopped);
                case StageEvacuation:
                    if (EvacuationSeconds >= 0)
                    {
                        string clock = EvacuationEndSeconds > 0
                            ? (EvacuationSeconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture) + " of " + Hours(EvacuationEndSeconds)
                            : Hours(EvacuationSeconds);
                        return "evacuation " + clock
                               + (string.IsNullOrEmpty(Detail) ? string.Empty : " (" + Detail + ")");
                    }
                    return "evacuation" + (string.IsNullOrEmpty(Detail) ? string.Empty : ": " + Detail);
                default:
                    return string.IsNullOrEmpty(Detail) ? Stage ?? string.Empty : Stage + ": " + Detail;
            }
        }

        /// <summary>"37.5 h, 12,804 nodes", "37.5 of 72 h, 12,804 nodes", or "starting" before the first timestep.</summary>
        public string DescribeElmfire(bool untilStopped)
        {
            if (ElmfireSeconds < 0) return string.IsNullOrEmpty(Detail) ? "starting" : Detail;

            string time = untilStopped || ElmfireStopSeconds <= 0
                ? Hours(ElmfireSeconds)
                : $"{(ElmfireSeconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture)} of {Hours(ElmfireStopSeconds)}";
            string nodes = TrackedNodes >= 0
                ? ", " + TrackedNodes.ToString("#,0", CultureInfo.InvariantCulture) + " nodes"
                : string.Empty;
            return time + nodes + (untilStopped ? " (until it stops)" : string.Empty);
        }

        private static string Hours(double seconds) =>
            (seconds / 3600.0).ToString("0.0", CultureInfo.InvariantCulture) + " h";
    }

    /// <summary>
    /// ELMFIRE's per-timestep progress line, <c>[1] Current Timestep: 102449.0 of 7200000.0, tracked nodes: 26822.
    /// Weather bands 1 to 1</c>, which it prints once per timestep (with a carriage return, so a console overwrites it).
    /// </summary>
    public struct ElmfireTimestep
    {
        public double Seconds;
        public double StopSeconds;

        /// <summary>-1 when the line does not say (or Fortran printed asterisks for a number too wide).</summary>
        public long TrackedNodes;

        private const string Marker = "Current Timestep:";

        /// <summary>
        /// Reads one line. Carriage returns, a rank prefix and padding are tolerated; anything that is not a timestep
        /// line - or one whose time does not parse - is false.
        /// </summary>
        public static bool TryParse(string line, out ElmfireTimestep step)
        {
            step = new ElmfireTimestep { Seconds = -1.0, StopSeconds = -1.0, TrackedNodes = -1 };
            if (string.IsNullOrEmpty(line)) return false;

            //With a carriage return in the middle, the last complete one counts.
            int at = line.LastIndexOf(Marker, StringComparison.Ordinal);
            if (at < 0) return false;

            int i = at + Marker.Length;
            if (!ReadNumber(line, ref i, out double seconds)) return false;
            step.Seconds = seconds;

            int of = line.IndexOf(" of ", i, StringComparison.Ordinal);
            if (of >= 0 && of - i < 4)
            {
                int j = of + 4;
                if (ReadNumber(line, ref j, out double stop))
                {
                    step.StopSeconds = stop;
                    i = j;
                }
            }

            int nodesAt = line.IndexOf("tracked nodes:", i, StringComparison.OrdinalIgnoreCase);
            if (nodesAt >= 0)
            {
                int k = nodesAt + "tracked nodes:".Length;
                if (ReadNumber(line, ref k, out double nodes) && nodes >= 0) step.TrackedNodes = (long)nodes;
            }
            return true;
        }

        private static bool ReadNumber(string line, ref int i, out double value)
        {
            value = 0;
            while (i < line.Length && line[i] == ' ') ++i;
            int start = i;
            while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.' || line[i] == '-' || line[i] == '+'
                                       || line[i] == 'E' || line[i] == 'e'))
            {
                ++i;
            }
            //"26822." ends a sentence: the trailing dot belongs to it, not to the number.
            int end = i;
            if (end > start && line[end - 1] == '.') --end;
            return end > start && double.TryParse(line.Substring(start, end - start), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value);
        }
    }
    /// <summary>A finished realization, as a row of <see cref="CampaignLayout.RealizationsCsv"/>.</summary>
    public sealed class FinishedRealization
    {
        public string Id;
        public string Status;
        public bool Reused;
        public string Message;

        /// <summary>Acres; NaN when the row has none.</summary>
        public double FireAreaAcres = double.NaN;

        /// <summary>Minutes; NaN when the row has none (a reused fire, or one that never ran).</summary>
        public double ElmfireMinutes = double.NaN;
    }

    /// <summary>The convergence state after the latest boundary, the last row of <see cref="CampaignLayout.ConvergenceCsv"/>.</summary>
    public sealed class ConvergenceSnapshot
    {
        public int Boundaries;
        public string RealizationId;
        public int Streak;
        public double[] Deciles;
        public double[] Area;

        /// <summary>Null where a decile had no baseline yet.</summary>
        public double?[] Delta;
    }

    /// <summary>Readers for the campaign's tables, for windows that watch a campaign another process is writing.</summary>
    public static class CampaignFiles
    {
        /// <summary>
        /// The rows of a campaign's realizations.csv in file order (completion order), or an empty list when it has none.
        /// Read with full sharing: the campaign keeps the file open and appends a row per realization.
        /// </summary>
        public static List<FinishedRealization> ReadRealizations(string campaignFolder)
        {
            var rows = new List<FinishedRealization>();
            List<string> lines = ReadLines(Path.Combine(campaignFolder ?? string.Empty, CampaignLayout.RealizationsCsv));
            if (lines.Count == 0) return rows;

            List<string> header = SplitCsv(lines[0]);
            int id = header.IndexOf("realization"), status = header.IndexOf("status"), reused = header.IndexOf("reused"),
                message = header.IndexOf("message"), area = header.IndexOf("fire_area_acres"), minutes = header.IndexOf("elmfire_minutes");
            if (id < 0 || status < 0) return rows;

            for (int i = 1; i < lines.Count; ++i)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                List<string> f = SplitCsv(lines[i]);
                string Get(int column) => column >= 0 && column < f.Count ? f[column] : string.Empty;
                rows.Add(new FinishedRealization
                {
                    Id = Get(id),
                    Status = Get(status),
                    Reused = Get(reused) == "true",
                    Message = Get(message),
                    FireAreaAcres = Number(Get(area)),
                    ElmfireMinutes = Number(Get(minutes)),
                });
            }
            return rows;
        }

        /// <summary>The last row of a campaign's trigger_convergence.csv, or null when it has no boundary yet.</summary>
        public static ConvergenceSnapshot ReadLatestConvergence(string campaignFolder)
        {
            List<string> lines = ReadLines(Path.Combine(campaignFolder ?? string.Empty, CampaignLayout.ConvergenceCsv));
            if (lines.Count < 2) return null;

            string last = null;
            for (int i = lines.Count - 1; i >= 1 && last == null; --i)
            {
                if (!string.IsNullOrWhiteSpace(lines[i])) last = lines[i];
            }
            if (last == null) return null;

            List<string> header = SplitCsv(lines[0]);
            List<string> f = SplitCsv(last);
            var deciles = new List<double>();
            var area = new List<double>();
            var delta = new List<double?>();
            var snapshot = new ConvergenceSnapshot();
            for (int c = 0; c < header.Count && c < f.Count; ++c)
            {
                string name = header[c];
                if (name == "boundaries") int.TryParse(f[c], NumberStyles.Integer, CultureInfo.InvariantCulture, out snapshot.Boundaries);
                else if (name == "realization_id") snapshot.RealizationId = f[c];
                else if (name == "streak") int.TryParse(f[c], NumberStyles.Integer, CultureInfo.InvariantCulture, out snapshot.Streak);
                else if (name.StartsWith("area_p", StringComparison.Ordinal))
                {
                    deciles.Add(Number(name.Substring("area_p".Length)) / 100.0);
                    area.Add(Number(f[c]));
                }
                else if (name.StartsWith("delta_p", StringComparison.Ordinal))
                {
                    double d = Number(f[c]);
                    delta.Add(double.IsNaN(d) ? (double?)null : d);
                }
            }
            snapshot.Deciles = deciles.ToArray();
            snapshot.Area = area.ToArray();
            snapshot.Delta = delta.ToArray();
            return snapshot;
        }

        /// <summary>
        /// The last <paramref name="maxLines"/> lines of a text file another process may be appending to, reading no more
        /// than its last <paramref name="maxBytes"/>. Empty when there is no file.
        /// </summary>
        public static List<string> ReadTail(string path, int maxLines, long maxBytes = 512 * 1024)
        {
            var lines = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return lines;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bool cut = stream.Length > maxBytes;
                    if (cut) stream.Seek(-maxBytes, SeekOrigin.End);
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        if (cut) reader.ReadLine(); //most likely a partial line
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            lines.Add(line);
                            if (lines.Count > maxLines) lines.RemoveAt(0);
                        }
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return lines;
        }

        private static List<string> ReadLines(string path)
        {
            var lines = new List<string>();
            try
            {
                if (!File.Exists(path)) return lines;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null) lines.Add(line);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            return lines;
        }

        /// <summary>One CSV line's fields; a quoted field may hold commas ("" is a quote).</summary>
        internal static List<string> SplitCsv(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; ++i)
            {
                char ch = line[i];
                if (quoted)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); ++i; }
                    else if (ch == '"') quoted = false;
                    else sb.Append(ch);
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(ch);
            }
            fields.Add(sb.ToString());
            return fields;
        }

        private static double Number(string text) =>
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }
}
