using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>
    /// What happened to one realization, written into its folder as it goes, so a resumed campaign knows what it
    /// can reuse without redoing any of it - and without re-drawing the weather first, which is what used to
    /// overwrite a realization's weather before its old fire was reused.
    /// </summary>
    /// <remarks>
    /// Plain <c>key=value</c> lines. Written through a temporary file, so a record is either the previous one or
    /// the new one, never half of each.
    /// </remarks>
    internal sealed class RealizationRecord
    {
        /// <summary>How far the realization got: nothing yet, its fire, or everything.</summary>
        public const string StageNone = "none";
        public const string StageFire = "fire";
        public const string StageDone = "done";

        public string Stage = StageNone;

        /// <summary><see cref="CampaignLayout.StatusOk"/>, not-threatened or failed; empty while in progress.</summary>
        public string Status = string.Empty;

        public string Message = string.Empty;

        /// <summary>Relative to the realization folder.</summary>
        public string Toa, Ros, Sd, Fi, Mfws, Boundary;

        public double IgnitionX = double.NaN, IgnitionY = double.NaN, WindFromDeg = double.NaN;
        public double FireAreaAcres = -1.0;
        public double ElmfireSeconds = -1.0;

        /// <summary>The drawn weather, when it was drawn from fitted distributions.</summary>
        public bool HasDraw;
        public double Temperature, RelativeHumidity, WindSpeedMps, WindDirectionDeg;
        public bool DirectionResampled;
        public bool LiveDrawn;
        public double LiveHerbaceousPercent, LiveWoodyPercent;
        public double M1Percent = double.NaN, M10Percent = double.NaN, M100Percent = double.NaN;
        public string WeatherDay = string.Empty;
        public double MeanWindMph = double.NaN;

        public bool IsFinished => Stage == StageDone
                                  && (Status == CampaignLayout.StatusOk || Status == CampaignLayout.StatusNotThreatened);

        public static string PathIn(string realizationDir) => Path.Combine(realizationDir, CampaignLayout.RealizationRecord);

        public static RealizationRecord Load(string realizationDir)
        {
            string path = PathIn(realizationDir);
            if (!File.Exists(path)) return null;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    int eq = raw.IndexOf('=');
                    if (eq <= 0 || raw.StartsWith("#")) continue;
                    values[raw.Substring(0, eq)] = raw.Substring(eq + 1);
                }
            }
            catch
            {
                return null;
            }

            var r = new RealizationRecord();
            r.Stage = Str(values, "stage", StageNone);
            r.Status = Str(values, "status", string.Empty);
            r.Message = Str(values, "message", string.Empty);
            r.Toa = Str(values, "toa", null);
            r.Ros = Str(values, "ros", null);
            r.Sd = Str(values, "sd", null);
            r.Fi = Str(values, "fi", null);
            r.Mfws = Str(values, "mfws", null);
            r.Boundary = Str(values, "boundary", null);
            r.IgnitionX = Num(values, "ignition_x");
            r.IgnitionY = Num(values, "ignition_y");
            r.WindFromDeg = Num(values, "wind_from_deg");
            r.FireAreaAcres = Num(values, "fire_area_acres", -1.0);
            r.ElmfireSeconds = Num(values, "elmfire_seconds", -1.0);
            r.HasDraw = Str(values, "drawn", "false") == "true";
            r.Temperature = Num(values, "temperature_c");
            r.RelativeHumidity = Num(values, "relative_humidity_pct");
            r.WindSpeedMps = Num(values, "wind_speed_ms");
            r.WindDirectionDeg = Num(values, "wind_direction_deg");
            r.DirectionResampled = Str(values, "wind_direction_source", "aimed") == "resampled";
            r.LiveDrawn = Str(values, "live_drawn", "false") == "true";
            r.LiveHerbaceousPercent = Num(values, "live_herbaceous_pct");
            r.LiveWoodyPercent = Num(values, "live_woody_pct");
            r.M1Percent = Num(values, "dead_1h_pct");
            r.M10Percent = Num(values, "dead_10h_pct");
            r.M100Percent = Num(values, "dead_100h_pct");
            r.WeatherDay = Str(values, "weather_day", string.Empty);
            r.MeanWindMph = Num(values, "mean_wind_mph");
            return r;
        }

        public void Save(string realizationDir)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# One campaign realization. Paths are relative to this folder.");
            Line(sb, "stage", Stage);
            Line(sb, "status", Status);
            Line(sb, "message", (Message ?? string.Empty).Replace('\n', ' ').Replace('\r', ' '));
            Line(sb, "toa", Toa);
            Line(sb, "ros", Ros);
            Line(sb, "sd", Sd);
            Line(sb, "fi", Fi);
            Line(sb, "mfws", Mfws);
            Line(sb, "boundary", Boundary);
            Line(sb, "ignition_x", N(IgnitionX));
            Line(sb, "ignition_y", N(IgnitionY));
            Line(sb, "wind_from_deg", N(WindFromDeg));
            Line(sb, "fire_area_acres", N(FireAreaAcres));
            Line(sb, "elmfire_seconds", N(ElmfireSeconds));
            Line(sb, "drawn", HasDraw ? "true" : "false");
            Line(sb, "temperature_c", N(Temperature));
            Line(sb, "relative_humidity_pct", N(RelativeHumidity));
            Line(sb, "wind_speed_ms", N(WindSpeedMps));
            Line(sb, "wind_direction_deg", N(WindDirectionDeg));
            Line(sb, "wind_direction_source", DirectionResampled ? "resampled" : "aimed");
            Line(sb, "live_drawn", LiveDrawn ? "true" : "false");
            Line(sb, "live_herbaceous_pct", N(LiveHerbaceousPercent));
            Line(sb, "live_woody_pct", N(LiveWoodyPercent));
            Line(sb, "dead_1h_pct", N(M1Percent));
            Line(sb, "dead_10h_pct", N(M10Percent));
            Line(sb, "dead_100h_pct", N(M100Percent));
            Line(sb, "weather_day", WeatherDay);
            Line(sb, "mean_wind_mph", N(MeanWindMph));

            Directory.CreateDirectory(realizationDir);
            string path = PathIn(realizationDir);
            string temp = path + ".tmp";
            File.WriteAllText(temp, sb.ToString());
            File.Copy(temp, path, overwrite: true);
            File.Delete(temp);
        }

        /// <summary>A path of this realization's, made absolute; null when unset.</summary>
        public static string Resolve(string realizationDir, string relative)
        {
            return string.IsNullOrEmpty(relative) ? null : Path.GetFullPath(Path.Combine(realizationDir, relative));
        }

        private static void Line(StringBuilder sb, string key, string value)
        {
            sb.Append(key).Append('=').AppendLine(value ?? string.Empty);
        }

        private static string N(double v) => double.IsNaN(v) ? string.Empty : v.ToString("R", CultureInfo.InvariantCulture);

        private static string Str(Dictionary<string, string> values, string key, string fallback)
        {
            return values.TryGetValue(key, out string v) && v.Length > 0 ? v : fallback;
        }

        private static double Num(Dictionary<string, string> values, string key, double fallback = double.NaN)
        {
            return values.TryGetValue(key, out string v)
                   && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d
                : fallback;
        }
    }
}
