using System;
using System.Globalization;
using System.IO;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>What one realization came to.</summary>
    internal sealed class RealizationOutcome
    {
        public int Index;
        public string Id;

        /// <summary>ok, not-threatened or failed - or <see cref="Cancelled"/>, which is none of them.</summary>
        public string Status = CampaignLayout.StatusFailed;
        public string Message = string.Empty;
        public bool Cancelled;

        /// <summary>Reused from an earlier run of this campaign rather than computed now.</summary>
        public bool Reused;

        /// <summary>Failed because ELMFIRE hit its wall-clock limit (MAX_RUNTIME) and stopped the fire early.</summary>
        public bool Truncated;

        public float[,] Boundary;
        public AscRaster.Header Header;

        /// <summary>The fire's arrival raster when its ELMFIRE run completed, for the ensemble fire statistics.</summary>
        public string ToaPath;

        public RealizationRecord Record;
    }

    /// <summary>
    /// One realization, end to end: its ignition, its own weather, its ELMFIRE run, and - when the fire reaches the
    /// WUI area - its evacuation and trigger boundary.
    /// </summary>
    internal static class RealizationBuilder
    {
        public static RealizationOutcome Run(Campaign c, int index, Func<bool> stopping)
        {
            string id = CampaignLayout.RealizationId(index);
            var outcome = new RealizationOutcome { Index = index, Id = id };
            string dir = c.RealizationDir(id);

            try
            {
                return RunStages(c, index, id, dir, outcome, stopping);
            }
            catch (Exception e)
            {
                //One realization's exception is that realization's failure, not the campaign's: it used to propagate
                //through the aggregation loop, crash the CLI and orphan every child still running.
                if (stopping())
                {
                    outcome.Cancelled = true;
                    return outcome;
                }

                outcome.Status = CampaignLayout.StatusFailed;
                outcome.Message = e.GetType().Name + ": " + e.Message;
                Console.Error.WriteLine($"[{id}] failed: {outcome.Message}");
                TryRecordFailure(dir, outcome.Message);
                return outcome;
            }
        }

        private static RealizationOutcome RunStages(Campaign c, int index, string id, string dir,
            RealizationOutcome outcome, Func<bool> stopping)
        {
            CampaignOptions o = c.Options;

            //Reuse is decided here, before anything is drawn: a campaign folder holds only realizations computed
            //with these exact settings (its name is their hash), so a finished one stands as it is. Drawing the
            //weather first and then reusing the old fire is what made the record describe weather no fire used.
            RealizationRecord record = RealizationRecord.Load(dir);
            if (o.Resume && record != null && record.IsFinished && TryReuse(c, dir, record, outcome))
            {
                return outcome;
            }

            if (o.ResumeOnly)
            {
                outcome.Cancelled = true; //nothing computed, nothing to count
                outcome.Message = "not computed yet";
                return outcome;
            }

            bool haveFire = o.Resume && record != null && record.Stage == RealizationRecord.StageFire
                            && FilesExist(dir, record.Toa, record.Ros, record.Sd, record.Mfws);

            if (!haveFire)
            {
                if (stopping()) { outcome.Cancelled = true; return outcome; }

                EmptyDirectory(dir);
                record = new RealizationRecord();
                outcome.Record = record;

                if (!ComputeFire(c, index, id, dir, record, outcome, stopping))
                {
                    return outcome;
                }
            }
            else
            {
                outcome.Record = record;
                outcome.ToaPath = RealizationRecord.Resolve(dir, record.Toa);
                Console.WriteLine($"[{id}] reusing its fire; running the evacuation again.");
            }

            outcome.ToaPath = RealizationRecord.Resolve(dir, record.Toa);

            //A fire that never reaches the WUI area has no trigger boundary to give, and the evacuation it would
            //otherwise run - three to five minutes of SUMO - ends with "the fire never reached wui".
            if (c.WuiAreaFile != null && c.WuiAreaSource == "Raster"
                && !FireReachesWui(outcome.ToaPath, c.WuiAreaFile, out string reach))
            {
                record.Stage = RealizationRecord.StageDone;
                record.Status = CampaignLayout.StatusNotThreatened;
                record.Message = reach;
                record.Save(dir);
                outcome.Status = CampaignLayout.StatusNotThreatened;
                outcome.Message = reach;
                Console.WriteLine($"[{id}] {reach}; no evacuation run.");
                return outcome;
            }

            if (stopping()) { outcome.Cancelled = true; return outcome; }

            bool ok = RealizationRunner.Run(c, index, id, dir, record, out string message, out bool cancelled);
            if (cancelled || stopping())
            {
                outcome.Cancelled = true;
                return outcome;
            }

            if (!ok)
            {
                //Stage stays "fire": a resumed campaign re-runs only the evacuation.
                record.Status = CampaignLayout.StatusFailed;
                record.Message = message;
                record.Save(dir);
                outcome.Status = CampaignLayout.StatusFailed;
                outcome.Message = message;
                return outcome;
            }

            record.Stage = RealizationRecord.StageDone;
            record.Status = CampaignLayout.StatusOk;
            record.Message = string.Empty;
            record.Save(dir);

            outcome.Status = CampaignLayout.StatusOk;
            return LoadBoundary(dir, record, outcome) ? outcome : FailOutcome(outcome, dir, record, "its boundary could not be read");
        }

        /// <summary>Ignition, weather and ELMFIRE. False when the realization has already been settled (failed,
        /// not threatened or cancelled) and <paramref name="outcome"/> says how.</summary>
        private static bool ComputeFire(Campaign c, int index, string id, string dir, RealizationRecord record,
            RealizationOutcome outcome, Func<bool> stopping)
        {
            CampaignOptions o = c.Options;

            // ---- ignition, and the bearing from it to the WUI area
            double? windFrom = null;
            if (o.WindToWui)
            {
                MaskIgnitionSampler.Result draw = MaskIgnitionSampler.Sample(
                    ElmfireStems.Tif(c.InputsDir, c.IgnitionMaskStem), ElmfireStems.Tif(c.InputsDir, c.FuelStem),
                    c.EdgeBufferMetres, unchecked(o.Seed + 1_000_000 + index));
                if (!draw.Ok)
                {
                    return Settle(outcome, dir, record, "could not draw an ignition: " + draw.Message);
                }

                windFrom = MaskIgnitionSampler.WindDirectionFromBearing(draw.X, draw.Y, c.WuiCentreX, c.WuiCentreY);
                record.IgnitionX = draw.X;
                record.IgnitionY = draw.Y;
                record.WindFromDeg = windFrom.Value;

                double distance = System.Math.Sqrt((c.WuiCentreX - draw.X) * (c.WuiCentreX - draw.X)
                                                   + (c.WuiCentreY - draw.Y) * (c.WuiCentreY - draw.Y));
                Console.WriteLine($"[{id}] ignition {draw.X:F0}, {draw.Y:F0}, {distance / 1000.0:F1} km from the WUI area; "
                                  + $"wind aimed from {windFrom.Value:F0} deg.");
            }

            // ---- this realization's own weather
            string weatherDir = Path.Combine(dir, CampaignLayout.RealizationWeatherFolder);
            Directory.CreateDirectory(weatherDir);

            WeatherRasterPipeline.Options per = c.Weather.Clone();
            per.InputsDirectory = weatherDir;
            per.Seed = unchecked(o.Seed + index);
            per.ForceWindDirectionDeg = windFrom;
            per.Log = null; //at --parallel width the per-stage chatter interleaves into noise; one line below instead
            //A stop (convergence, cancel) starts no further WindNinja band for this realization, instead of one band
            //after another for the rest of its series until the stage ended.
            per.Cancelled = stopping;

            WeatherRasterPipeline.Result weather = WeatherRasterPipeline.Run(per).GetAwaiter().GetResult();
            if (stopping()) { outcome.Cancelled = true; return false; }

            string weatherProblem = DescribeWeatherProblem(weather, o);
            if (weatherProblem != null)
            {
                return Settle(outcome, dir, record, "its weather could not be made: " + weatherProblem);
            }

            RecordWeather(record, weather);
            string source = weather.Day.HasValue ? weather.Day.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                          : weather.Drawn.HasValue ? $"drawn {weather.Drawn.Value.Temperature:F0} C / RH {weather.Drawn.Value.RelativeHumidity:F0}%"
                          : "uniform";
            Console.WriteLine($"[{id}] weather {source}: wind {weather.MeanWindSpeedMph:F1} mph at 10 m, dead moisture "
                              + $"{weather.MeanM1Percent:F1}/{weather.MeanM10Percent:F1}/{weather.MeanM100Percent:F1} %"
                              + (weather.Drawn.HasValue && weather.Drawn.Value.LiveDrawn
                                  ? $", live {weather.Drawn.Value.LiveHerbaceousPercent:F0}/{weather.Drawn.Value.LiveWoodyPercent:F0} %" : "")
                              + (weather.Fallbacks.Count > 0 ? " (" + string.Join("; ", weather.Fallbacks) + ")" : ""));

            // ---- ELMFIRE
            string[] lines = BuildNamelist(c, index, dir, weatherDir, record, weather);
            ElmfireRunner.Result run = ElmfireRunner.Run(c.ElmfireExe, dir, id, lines, reuse: false, Console.Out, c.GdalBin);
            if (run.Cancelled || stopping())
            {
                outcome.Cancelled = true;
                return false;
            }

            record.ElmfireSeconds = run.Elapsed.TotalSeconds;
            record.FireAreaAcres = run.FireAreaAcres;
            record.Toa = RelativeTo(dir, run.Toa);
            record.Ros = RelativeTo(dir, run.Ros);
            record.Sd = RelativeTo(dir, run.Sd);
            record.Fi = RelativeTo(dir, run.Fi);
            record.Mfws = RelativeTo(dir, run.Mfws);

            if (run.NoSpread)
            {
                //A realization that threatened nothing, not a failure: its fire belongs in the burn probability.
                record.Stage = RealizationRecord.StageDone;
                record.Status = CampaignLayout.StatusNotThreatened;
                record.Message = run.Message;
                record.Save(dir);
                outcome.Status = CampaignLayout.StatusNotThreatened;
                outcome.Message = run.Message;
                outcome.ToaPath = RealizationRecord.Resolve(dir, record.Toa);
                Console.WriteLine($"[{id}] {run.Message}.");
                return false;
            }

            if (run.MaxRuntimeHit)
            {
                //A failure, and one worth singling out: the fires that run out of wall-clock time are the slowest
                //and usually the largest, so leaving them out quietly biases the probability towards small fires.
                outcome.Truncated = true;
                string stop = run.Message ?? string.Empty;
                int at = stop.IndexOf("early: ", StringComparison.Ordinal);
                if (at >= 0) stop = stop.Substring(at + "early: ".Length);
                return Settle(outcome, dir, record, $"ELMFIRE hit its wall-clock limit ({o.MaxRuntimeSeconds / 60.0:0} min, "
                                                    + $"--max-runtime-minutes) before the fire's {o.Hours:0.##} h were up, so "
                                                    + "the fire is incomplete and the realization counts as failed ("
                                                    + stop.Trim() + ")");
            }

            if (!run.Ok)
            {
                return Settle(outcome, dir, record, "ELMFIRE failed: " + run.Message);
            }

            if (run.Mfws == null)
            {
                return Settle(outcome, dir, record, "ELMFIRE wrote no midflame wind raster (mfws_*.tif), which k-PERIL "
                                                    + "needs; this elmfire build predates DUMP_MIDFLAME_WINDSPEED (a7fb9d6)");
            }

            Console.WriteLine($"[{id}] ELMFIRE: {run.FireAreaAcres:F0} acres in {run.Elapsed.TotalMinutes:F1} min.");
            record.Stage = RealizationRecord.StageFire;
            record.Save(dir);
            return true;
        }

        /// <summary>
        /// The template patched into this realization's namelist. Every directory is relative to the realization
        /// folder (ELMFIRE's command lines do not quote paths); the fuel tables are the campaign's own copies, so
        /// nothing in the shared inputs is ever written.
        /// </summary>
        private static string[] BuildNamelist(Campaign c, int index, string dir, string weatherDir,
            RealizationRecord record, WeatherRasterPipeline.Result weather)
        {
            CampaignOptions o = c.Options;
            string[] lines = (string[])c.TemplateLines.Clone();

            string Set(string group, string key, string value, bool quoted = false)
            {
                lines = ElmfireNamelist.SetKeyInGroup(lines, group, key, value, quoted);
                return value;
            }

            Set(ElmfireNamelistKeys.MonteCarloGroup, ElmfireNamelistKeys.Seed, unchecked(o.Seed + index).ToString(CultureInfo.InvariantCulture));
            Set(ElmfireNamelistKeys.MonteCarloGroup, ElmfireNamelistKeys.NumEnsembleMembers, "1");

            //What the seed varies. A template built from a scenario with a fixed ignition point would otherwise put
            //the same fire in every realization. With the wind aimed at the WUI area, the ignition had to be drawn
            //here (the bearing needs it before the weather is written); otherwise ELMFIRE draws it from the mask.
            lines = !double.IsNaN(record.IgnitionX)
                ? ElmfireNamelist.SetSampledIgnition(lines, record.IgnitionX, record.IgnitionY)
                : ElmfireNamelist.ForceRandomIgnition(lines, c.IgnitionMaskStem);

            Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.FuelsAndTopographyDirectory,
                ElmfireStems.ForNamelist(dir, c.InputsDir), quoted: true);
            Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WeatherDirectory, "./" + CampaignLayout.RealizationWeatherFolder, quoted: true);

            //The stems the weather chain writes, whatever the template's case called its own.
            Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WsFilename, ElmfireStems.WindSpeed, quoted: true);
            Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WdFilename, ElmfireStems.WindDirection, quoted: true);
            Set(ElmfireNamelistKeys.InputsGroup, "M1_FILENAME", ElmfireStems.M1, quoted: true);
            Set(ElmfireNamelistKeys.InputsGroup, "M10_FILENAME", ElmfireStems.M10, quoted: true);
            Set(ElmfireNamelistKeys.InputsGroup, "M100_FILENAME", ElmfireStems.M100, quoted: true);

            //The realization's weather starts at the fire's start, so band 1 is time 0 and the series holds exactly
            //what it wrote. The template's band range described the case's own series. The same fit a single run makes
            //on the case's weather (ElmfireCoupling.PatchNamelist).
            int bands = System.Math.Max(1, AscRaster.GetBandCount(ElmfireStems.Tif(weatherDir, ElmfireStems.WindSpeed)));
            lines = ElmfireNamelist.FitWeatherBands(lines, bands);

            //Live fuel moisture is a namelist scalar: the NFDRS4 GSI model has no terrain, so there is no per-cell
            //answer to write. USE_CONSTANT_LH/LW are forced so the value set here is the one used.
            if (weather.Drawn.HasValue && weather.Drawn.Value.LiveDrawn)
            {
                Set(ElmfireNamelistKeys.InputsGroup, "LH_MOISTURE_CONTENT",
                    weather.Drawn.Value.LiveHerbaceousPercent.ToString("0.###", CultureInfo.InvariantCulture));
                Set(ElmfireNamelistKeys.InputsGroup, "LW_MOISTURE_CONTENT",
                    weather.Drawn.Value.LiveWoodyPercent.ToString("0.###", CultureInfo.InvariantCulture));
                Set(ElmfireNamelistKeys.InputsGroup, "USE_CONSTANT_LH", ".TRUE.");
                Set(ElmfireNamelistKeys.InputsGroup, "USE_CONSTANT_LW", ".TRUE.");
            }

            Set(ElmfireNamelistKeys.OutputsGroup, ElmfireNamelistKeys.OutputsDirectory, "./outputs", quoted: true);
            Set(ElmfireNamelistKeys.MiscellaneousGroup, ElmfireNamelistKeys.Scratch, "./scratch", quoted: true);

            Set(ElmfireNamelistKeys.MiscellaneousGroup, ElmfireNamelistKeys.MiscellaneousInputsDirectory,
                ElmfireStems.ForNamelist(dir, c.TablesDir), quoted: true);
            Set(ElmfireNamelistKeys.MiscellaneousGroup, ElmfireNamelistKeys.FuelModelFile, ElmfireStems.FuelModelTable, quoted: true);
            if (c.BuildingTableSource != null)
            {
                Set(ElmfireNamelistKeys.MiscellaneousGroup, ElmfireNamelistKeys.BuildingFuelModelFile, c.BuildingTableName, quoted: true);
            }

            if (!string.IsNullOrEmpty(c.GdalBin))
            {
                Set(ElmfireNamelistKeys.MiscellaneousGroup, ElmfireNamelistKeys.PathToGdal, c.GdalBin.TrimEnd('/', '\\'), quoted: true);
            }

            Set(ElmfireNamelistKeys.TimeControlGroup, ElmfireNamelistKeys.SimulationTstop,
                CampaignLayout.TstopSeconds(o.Hours).ToString("0.0", CultureInfo.InvariantCulture));
            Set(ElmfireNamelistKeys.SimulatorGroup, ElmfireNamelistKeys.MaxRuntime,
                o.MaxRuntimeSeconds.ToString("0.0", CultureInfo.InvariantCulture));

            return ElmfireNamelistKeys.ForceRequiredOutputs(lines);
        }

        /// <summary>Why a realization's weather is not good enough to run, or null.</summary>
        private static string DescribeWeatherProblem(WeatherRasterPipeline.Result weather, CampaignOptions o)
        {
            if (!weather.ClimatologyUsed)
            {
                return "no weather could be drawn from the climatology (" + string.Join("; ", weather.Fallbacks) + ")";
            }

            if (!weather.WindNinjaUsed && !o.AllowUniformWeather)
            {
                return "WindNinja did not run (" + string.Join("; ", weather.Fallbacks)
                       + "); pass --allow-uniform-weather to accept a uniform wind field";
            }

            if (o.WeatherSampling == WeatherRasterPipeline.SamplingMode.HistoricalDay && !weather.NelsonUsed && !o.AllowUniformWeather)
            {
                return "Nelson did not run (" + string.Join("; ", weather.Fallbacks)
                       + "); pass --allow-uniform-weather to accept uniform dead fuel moisture";
            }

            return null;
        }

        private static void RecordWeather(RealizationRecord record, WeatherRasterPipeline.Result weather)
        {
            record.MeanWindMph = weather.MeanWindSpeedMph;
            record.M1Percent = weather.MeanM1Percent;
            record.M10Percent = weather.MeanM10Percent;
            record.M100Percent = weather.MeanM100Percent;
            if (weather.Day.HasValue)
            {
                record.WeatherDay = weather.Day.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (weather.Drawn.HasValue)
            {
                ClimatologySampler.DrawnFireWeather d = weather.Drawn.Value;
                record.HasDraw = true;
                record.Temperature = d.Temperature;
                record.RelativeHumidity = d.RelativeHumidity;
                record.WindSpeedMps = d.WindSpeedMps;
                record.WindDirectionDeg = d.WindDirectionDeg;
                record.DirectionResampled = d.DirectionResampled;
                record.LiveDrawn = d.LiveDrawn;
                record.LiveHerbaceousPercent = d.LiveHerbaceousPercent;
                record.LiveWoodyPercent = d.LiveWoodyPercent;
            }
        }

        /// <summary>
        /// Whether the fire reached any cell of the WUI area. The arrival raster and the mask are both on the case
        /// grid by construction; a mismatch is reported and treated as reached, so the evacuation decides.
        /// </summary>
        public static bool FireReachesWui(string toaPath, string wuiPath, out string description)
        {
            float[,] toa = AscRaster.Read(toaPath, out AscRaster.Header toaHeader, out bool toaOk);
            float[,] wui = AscRaster.Read(wuiPath, out AscRaster.Header wuiHeader, out bool wuiOk);
            if (!toaOk || !wuiOk || toa == null || wui == null
                || toaHeader.Ncols != wuiHeader.Ncols || toaHeader.Nrows != wuiHeader.Nrows)
            {
                description = "could not compare the fire with the WUI area";
                return true;
            }

            int wuiCells = 0, reached = 0;
            float earliest = float.MaxValue;
            for (int x = 0; x < toaHeader.Ncols; ++x)
            {
                for (int y = 0; y < toaHeader.Nrows; ++y)
                {
                    float w = wui[x, y];
                    if (!(w > 0f) || w == (float)wuiHeader.NoDataValue) continue;
                    ++wuiCells;

                    float t = toa[x, y];
                    if (t >= 0f && t < 1e11f && !float.IsNaN(t))
                    {
                        ++reached;
                        if (t < earliest) earliest = t;
                    }
                }
            }

            description = reached > 0
                ? $"the fire reached {reached} of {wuiCells} WUI cells, first at {earliest / 3600.0:F1} h"
                : $"the fire never reached the WUI area ({wuiCells} cells)";
            return reached > 0;
        }

        private static bool TryReuse(Campaign c, string dir, RealizationRecord record, RealizationOutcome outcome)
        {
            outcome.Record = record;
            outcome.Reused = true;
            outcome.Status = record.Status;
            outcome.Message = record.Message;
            outcome.ToaPath = FilesExist(dir, record.Toa) ? RealizationRecord.Resolve(dir, record.Toa) : null;

            if (record.Status == CampaignLayout.StatusNotThreatened)
            {
                return true;
            }

            if (!LoadBoundary(dir, record, outcome))
            {
                //Recorded as done but its boundary is gone: computed again rather than trusted.
                outcome.Reused = false;
                return false;
            }
            return true;
        }

        private static bool LoadBoundary(string dir, RealizationRecord record, RealizationOutcome outcome)
        {
            string path = RealizationRecord.Resolve(dir, record.Boundary);
            if (path == null || !File.Exists(path)) return false;

            outcome.Boundary = AscRaster.Read(path, out outcome.Header, out bool ok);
            return ok && outcome.Boundary != null;
        }

        private static RealizationOutcome FailOutcome(RealizationOutcome outcome, string dir, RealizationRecord record, string message)
        {
            record.Status = CampaignLayout.StatusFailed;
            record.Message = message;
            record.Save(dir);
            outcome.Status = CampaignLayout.StatusFailed;
            outcome.Message = message;
            Console.Error.WriteLine($"[{outcome.Id}] {message}");
            return outcome;
        }

        /// <summary>Records a failure before the fire exists (stage stays "none", so a resume starts it again).</summary>
        private static bool Settle(RealizationOutcome outcome, string dir, RealizationRecord record, string message)
        {
            record.Stage = RealizationRecord.StageNone;
            FailOutcome(outcome, dir, record, message);
            return false;
        }

        private static void TryRecordFailure(string dir, string message)
        {
            try
            {
                RealizationRecord record = RealizationRecord.Load(dir) ?? new RealizationRecord();
                record.Status = CampaignLayout.StatusFailed;
                record.Message = message;
                record.Save(dir);
            }
            catch
            {
            }
        }

        private static bool FilesExist(string dir, params string[] relative)
        {
            foreach (string r in relative)
            {
                string path = RealizationRecord.Resolve(dir, r);
                if (path == null || !File.Exists(path)) return false;
            }
            return true;
        }

        private static string RelativeTo(string dir, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return Path.GetRelativePath(dir, path).Replace('\\', '/');
        }

        private static void EmptyDirectory(string dir)
        {
            Directory.CreateDirectory(dir);
            foreach (string f in Directory.GetFiles(dir))
            {
                try { File.Delete(f); } catch { }
            }
            foreach (string d in Directory.GetDirectories(dir))
            {
                try { Directory.Delete(d, recursive: true); } catch { }
            }
        }
    }
}
