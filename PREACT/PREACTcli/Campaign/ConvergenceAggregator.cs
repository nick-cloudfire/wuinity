using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>
    /// Runs realizations and folds them into a running per-cell probability raster until its decile-area footprint
    /// stops moving: for <c>--streak</c> consecutive realizations, every decile's area changed by less than
    /// <c>--tolerance</c> from the previous realization.
    /// </summary>
    /// <remarks>
    /// Up to <c>--parallel</c> realizations run at once, each on its own dedicated thread (they block for minutes
    /// in child processes, which on thread-pool threads starved the pool of the threads the process-output
    /// callbacks need). Aggregation is single-threaded, in completion order: realizations are i.i.d. draws, so
    /// that is statistically the same as launch order.
    ///
    /// The trigger probability is conditional on the fire reaching the WUI area: its denominator is the
    /// realizations that produced a boundary. Realizations whose fire never arrived are counted separately
    /// (not-threatened) and are in the fire statistics, whose burn probability is over every completed fire.
    /// </remarks>
    internal sealed class ConvergenceAggregator
    {
        private static readonly double[] Deciles = { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 };

        private readonly Campaign _c;
        private volatile bool _stopping;

        public ConvergenceAggregator(Campaign campaign)
        {
            _c = campaign;
        }

        private bool Stopping() => _stopping || ChildProcessGuard.IsCancelled;

        public int Run()
        {
            CampaignOptions o = _c.Options;
            string diagnosticsPath = Path.Combine(_c.Folder, CampaignLayout.ConvergenceCsv);
            string livePath = Path.Combine(_c.Folder, CampaignLayout.LiveProbabilityRaster);

            using var diag = new StreamWriter(diagnosticsPath);
            WriteDiagnosticsHeader(diag);
            using var summary = new StreamWriter(Path.Combine(_c.Folder, CampaignLayout.RealizationsCsv));
            summary.WriteLine("realization,status,reused,message,fire_area_acres,elmfire_minutes,ignition_x,ignition_y,"
                              + "wind_from_deg,mean_wind_10m_mph,dead_1h_pct,live_herbaceous_pct,live_woody_pct,evacuation_seed");

            int[,] insideCount = null;
            AscRaster.Header header = default;
            EnsembleFireStatistics fireStatistics = null;
            var usedFires = new List<(string Id, RealizationRecord Record)>();

            int nOk = 0, nNotThreatened = 0, nFailed = 0, nReused = 0, nTruncated = 0;
            int streak = 0, launched = 0, completed = 0;
            bool converged = false;
            var previousArea = new double?[Deciles.Length];
            var pending = new List<Task<RealizationOutcome>>();

            while (true)
            {
                while (pending.Count < o.Parallelism && launched < o.MaxRealizations && !converged && !Stopping())
                {
                    int index = o.Start + launched;
                    ++launched;
                    pending.Add(Task.Factory.StartNew(() => RealizationBuilder.Run(_c, index, Stopping),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                }

                if (pending.Count == 0) break;

                Task<RealizationOutcome> finished = Task.WhenAny(pending).GetAwaiter().GetResult();
                pending.Remove(finished);
                RealizationOutcome result = finished.GetAwaiter().GetResult();

                if (result.Cancelled)
                {
                    continue; //neither a result nor a failure; a resume computes it
                }

                ++completed;
                if (result.Reused) ++nReused;
                WriteSummaryRow(summary, result);

                if (result.ToaPath != null && File.Exists(result.ToaPath))
                {
                    AccumulateFireStatistics(result, ref fireStatistics);
                    if (result.Record != null) usedFires.Add((result.Id, result.Record));
                }

                string status = result.Status;
                if (status == CampaignLayout.StatusOk && result.Boundary != null)
                {
                    if (insideCount == null)
                    {
                        header = result.Header;
                        insideCount = new int[header.Ncols, header.Nrows];
                    }
                    else if (result.Header.Ncols != header.Ncols || result.Header.Nrows != header.Nrows)
                    {
                        Console.Error.WriteLine($"[{result.Id}] boundary is {result.Header.Ncols}x{result.Header.Nrows}, "
                                                + $"not {header.Ncols}x{header.Nrows}; counted as failed.");
                        status = CampaignLayout.StatusFailed;
                    }
                }
                else if (status == CampaignLayout.StatusOk)
                {
                    status = CampaignLayout.StatusFailed;
                }

                if (status == CampaignLayout.StatusNotThreatened) ++nNotThreatened;
                if (status == CampaignLayout.StatusFailed) ++nFailed;
                if (result.Truncated) ++nTruncated;

                Console.WriteLine($"{CampaignLayout.ProgressTag}{completed}/{o.MaxRealizations} realization {result.Id} {status}");

                double[] area = new double[Deciles.Length];
                var delta = new double?[Deciles.Length];

                if (status == CampaignLayout.StatusOk)
                {
                    ++nOk;
                    Fold(insideCount, result.Boundary, header);

                    bool allWithin = true, anyChecked = false;
                    double cellArea = header.CellSize * header.CellSize;
                    for (int t = 0; t < Deciles.Length; ++t)
                    {
                        long cells = 0;
                        foreach (int count in insideCount)
                        {
                            if ((double)count / nOk >= Deciles[t]) ++cells;
                        }
                        area[t] = cells * cellArea;

                        //a decile with no (non-zero) baseline yet is left out rather than divided by zero
                        if (previousArea[t].HasValue && previousArea[t].Value > 0)
                        {
                            anyChecked = true;
                            delta[t] = System.Math.Abs(area[t] - previousArea[t].Value) / previousArea[t].Value;
                            if (delta[t] >= o.Tolerance) allWithin = false;
                        }
                        previousArea[t] = area[t];
                    }

                    //nothing to compare the first realization against, so it moves the streak neither way
                    if (anyChecked) streak = allWithin ? streak + 1 : 0;
                    WriteDiagnosticsRow(diag, nOk, result.Id, area, delta, streak);
                    Console.WriteLine($"[{result.Id}] streak {streak}/{o.Streak} ({nOk} boundaries).");

                    WriteLiveRaster(insideCount, header, nOk, livePath);

                    if (streak >= o.Streak)
                    {
                        converged = true;
                        Console.WriteLine($"Converged after {nOk} boundaries ({streak} consecutive within {o.Tolerance:P0} per decile).");
                    }
                }
                else if (!string.IsNullOrEmpty(result.Message))
                {
                    Console.WriteLine($"[{result.Id}] {status}: {result.Message}");
                }

                //The decile areas only with a boundary: a not-threatened or failed realization leaves the field as
                //it was, and a row of zeros for it made the GUI's table drop to nothing after each one (e2e F6).
                EmitProgressJson(result.Id, status, nOk, nNotThreatened, nFailed, streak, o.Streak, converged,
                    status == CampaignLayout.StatusOk ? area : null, delta);

                if (converged && pending.Count > 0 && !_stopping)
                {
                    //The answer is in; the realizations still running cannot change it. Their records stay
                    //unfinished, so a resumed campaign with a longer streak computes them again.
                    _stopping = true;
                    Console.WriteLine($"Stopping the {pending.Count} realization(s) still running.");
                    ElmfireProcesses.KillAll();
                }
            }

            summary.Flush();

            if (ChildProcessGuard.IsCancelled)
            {
                Console.Error.WriteLine($"Cancelled after {completed} realization(s) ({nOk} boundaries). Nothing is lost: "
                                        + "--resume continues this campaign.");
                return 3;
            }

            string truncated = DescribeTruncated(nTruncated, completed, o.MaxRuntimeSeconds);
            if (truncated != null) Console.Error.WriteLine("WARNING: " + truncated);

            if (insideCount == null || nOk == 0)
            {
                Console.Error.WriteLine($"ERROR: no realization produced a usable trigger boundary ({nNotThreatened} fire(s) "
                                        + $"never reached the WUI area, {nFailed} failed); nothing to aggregate. See "
                                        + Path.Combine(_c.Folder, CampaignLayout.RealizationsCsv) + ".");
                WriteFireStatistics(fireStatistics);
                return 1;
            }

            if (!converged)
            {
                Console.Error.WriteLine($"WARNING: reached --max {o.MaxRealizations} realizations ({nOk} boundaries) without "
                                        + "converging; the probability raster is not yet stable.");
            }

            string outPath = Path.Combine(_c.Folder, CampaignLayout.ProbabilityRaster);
            AscRaster.Header outHeader = header;
            outHeader.NoDataValue = -9999.0;
            float[,] probability = BuildProbability(insideCount, header, nOk);
            AscRaster.Write(probability, outHeader, outPath);
            if (!string.IsNullOrEmpty(o.OutPath))
            {
                AscRaster.Write(probability, outHeader, Path.GetFullPath(o.OutPath));
            }

            WriteFireStatistics(fireStatistics);
            CampaignReports.WriteRealizedWeather(_c, usedFires);

            Console.WriteLine($"Done. {completed} realization(s): {nOk} boundaries, {nNotThreatened} not threatened, "
                              + $"{nFailed} failed" + (nTruncated > 0 ? $" ({nTruncated} of them stopped by the wall-clock limit)" : "")
                              + $" ({nReused} reused from an earlier run). Converged: {converged}.");
            Console.WriteLine("Probability raster: " + outPath);
            Console.WriteLine("Convergence diagnostics: " + diagnosticsPath);
            Console.WriteLine($"{CampaignLayout.ProgressTag}{o.MaxRealizations}/{o.MaxRealizations}");
            return 0;
        }

        /// <summary>
        /// The warning for realizations ELMFIRE stopped at the wall-clock limit, or null when there were none.
        /// </summary>
        /// <remarks>
        /// They are counted as failed - an incomplete fire says nothing about the ground it had not reached yet - but
        /// they are not a random sample of the failures: the fires that run longest are the largest, so every one of
        /// them left out moves the probability towards small fires (review MI-2). Said at the end of every campaign
        /// that had any, with what to change.
        /// </remarks>
        internal static string DescribeTruncated(int truncated, int completed, double maxRuntimeSeconds)
        {
            if (truncated <= 0) return null;
            return $"{truncated} of {completed} realization(s) were stopped by ELMFIRE's wall-clock limit "
                   + $"({maxRuntimeSeconds / 60.0:0} min) and are counted as failed. They are the slowest fires, usually the "
                   + "largest, so the probability raster under-represents large fires. Run the campaign with a larger "
                   + "--max-runtime-minutes (the limit is one of its settings, so that is a new campaign folder).";
        }

        private static void Fold(int[,] insideCount, float[,] boundary, AscRaster.Header header)
        {
            for (int x = 0; x < header.Ncols; ++x)
            {
                for (int y = 0; y < header.Nrows; ++y)
                {
                    float v = boundary[x, y];
                    if (v >= 1f && v != header.NoDataValue) insideCount[x, y] += 1;
                }
            }
        }

        /// <summary>
        /// Folds a completed fire's arrival raster into the ensemble statistics - every completed ELMFIRE run, not
        /// only those that produced a boundary: a fire that went the other way is evidence about the ground, and
        /// leaving it out inflated the burn probability towards certainty.
        /// </summary>
        private void AccumulateFireStatistics(RealizationOutcome result, ref EnsembleFireStatistics statistics)
        {
            try
            {
                float[,] toa = AscRaster.Read(result.ToaPath, out AscRaster.Header toaHeader, out bool ok);
                if (!ok || toa == null)
                {
                    Console.Error.WriteLine($"[{result.Id}] arrival raster could not be read; it is left out of the fire statistics.");
                    return;
                }

                if (statistics == null)
                {
                    double hours = _c.Options.Hours;
                    statistics = new EnsembleFireStatistics(toaHeader, CampaignLayout.StatisticsDurationSeconds(hours),
                        CampaignLayout.StatisticsBinSeconds(hours));
                }

                statistics.Add(toa);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[{result.Id}] arrival raster could not be folded in ({e.Message}).");
            }
        }

        private void WriteFireStatistics(EnsembleFireStatistics statistics)
        {
            if (statistics == null) return;

            List<string> written = statistics.WriteAll(_c.Folder, CampaignLayout.EnsemblePrefix,
                new[] { 0.1, 0.5, 0.9 }, Console.WriteLine);
            Console.WriteLine($"  ensemble: fire statistics over {statistics.Realizations} completed fire(s).");
            foreach (string s in written) Console.WriteLine("  " + s);
        }

        private static void WriteLiveRaster(int[,] insideCount, AscRaster.Header header, int nOk, string livePath)
        {
            //For the GUI, which shows the field building up, and a crash-safety net for long campaigns.
            try
            {
                var snapHeader = header;
                snapHeader.NoDataValue = -9999.0;
                AscRaster.Write(BuildProbability(insideCount, header, nOk), snapHeader, livePath);
                Console.WriteLine(CampaignLayout.ProgressRasterTag + livePath);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("WARNING: could not write the live probability raster: " + e.Message);
            }
        }

        /// <summary>Per-cell probability = fraction of boundaries enclosing the cell.</summary>
        private static float[,] BuildProbability(int[,] insideCount, AscRaster.Header header, int nOk)
        {
            var probability = new float[header.Ncols, header.Nrows];
            for (int x = 0; x < header.Ncols; ++x)
            {
                for (int y = 0; y < header.Nrows; ++y)
                {
                    probability[x, y] = (float)insideCount[x, y] / nOk;
                }
            }
            return probability;
        }

        /// <summary>
        /// One JSON object per realization on stdout, for the GUI window. Hand-built (one fixed shape, no JSON
        /// dependency) with invariant formatting so a comma-decimal locale cannot produce malformed JSON.
        /// </summary>
        internal static void EmitProgressJson(string id, string status, int nOk, int nNotThreatened, int nFailed,
            int streak, int streakTarget, bool converged, double[] area, double?[] delta)
        {
            var sb = new StringBuilder(CampaignLayout.ProgressJsonTag);
            sb.Append('{');
            sb.Append("\"realization\":\"").Append(id).Append("\",");
            sb.Append("\"status\":\"").Append(status).Append("\",");
            sb.Append("\"nSuccess\":").Append(nOk).Append(',');
            sb.Append("\"nNotThreatened\":").Append(nNotThreatened).Append(',');
            sb.Append("\"nFailed\":").Append(nFailed).Append(',');
            sb.Append("\"streak\":").Append(streak).Append(',');
            sb.Append("\"streakTarget\":").Append(streakTarget).Append(',');
            sb.Append("\"converged\":").Append(converged ? "true" : "false");

            if (area != null)
            {
                sb.Append(",\"deciles\":[").Append(string.Join(",", Deciles.Select(d => d.ToString("0.0#", CultureInfo.InvariantCulture))));
                sb.Append("],\"area\":[").Append(string.Join(",", area.Select(a => a.ToString("R", CultureInfo.InvariantCulture))));
                //null where a decile has no baseline yet, which is not the same as no change
                sb.Append("],\"delta\":[").Append(string.Join(",", delta.Select(d => d.HasValue ? d.Value.ToString("R", CultureInfo.InvariantCulture) : "null")));
                sb.Append(']');
            }

            sb.Append('}');
            Console.WriteLine(sb.ToString());
        }

        private static void WriteDiagnosticsHeader(StreamWriter w)
        {
            var cols = new List<string> { "boundaries", "realization_id", "streak" };
            foreach (double tau in Deciles) cols.Add("area_p" + (int)System.Math.Round(tau * 100));
            foreach (double tau in Deciles) cols.Add("delta_p" + (int)System.Math.Round(tau * 100));
            w.WriteLine(string.Join(",", cols));
        }

        private static void WriteDiagnosticsRow(StreamWriter w, int boundaries, string id, double[] area, double?[] delta, int streak)
        {
            var cols = new List<string>
            {
                boundaries.ToString(CultureInfo.InvariantCulture), id, streak.ToString(CultureInfo.InvariantCulture),
            };
            foreach (double a in area) cols.Add(a.ToString(CultureInfo.InvariantCulture));
            foreach (double? d in delta) cols.Add(d.HasValue ? d.Value.ToString(CultureInfo.InvariantCulture) : "");
            w.WriteLine(string.Join(",", cols));
            w.Flush();
        }

        private static void WriteSummaryRow(StreamWriter w, RealizationOutcome r)
        {
            RealizationRecord rec = r.Record;
            string N(double v) => double.IsNaN(v) || v < -1e8 ? "" : v.ToString("0.###", CultureInfo.InvariantCulture);
            string message = (r.Message ?? string.Empty).Replace('"', '\'');

            w.WriteLine(string.Join(",",
                r.Id, r.Status, r.Reused ? "true" : "false", "\"" + message + "\"",
                rec == null ? "" : N(rec.FireAreaAcres),
                rec == null || rec.ElmfireSeconds < 0 ? "" : N(rec.ElmfireSeconds / 60.0),
                rec == null ? "" : N(rec.IgnitionX), rec == null ? "" : N(rec.IgnitionY),
                rec == null ? "" : N(rec.WindFromDeg), rec == null ? "" : N(rec.MeanWindMph),
                rec == null ? "" : N(rec.M1Percent),
                rec == null || !rec.LiveDrawn ? "" : N(rec.LiveHerbaceousPercent),
                rec == null || !rec.LiveDrawn ? "" : N(rec.LiveWoodyPercent),
                rec == null || rec.EvacuationSeed == 0 ? "" : rec.EvacuationSeed.ToString(CultureInfo.InvariantCulture)));
            w.Flush();
        }
    }
}
