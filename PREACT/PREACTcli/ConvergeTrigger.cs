using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PREACT.Utility;
using PREACTcli.Campaigns;

namespace PREACTcli
{
    /// <summary>
    /// <c>PREACTcli converge-trigger</c>: a convergence-driven probabilistic trigger boundary
    /// (docs/trigger-campaigns.md; the options in docs/command-line-tools.md).
    /// </summary>
    /// <remarks>
    /// Each realization is one draw of the whole scenario - an ignition from the case's mask, its own weather,
    /// its own ELMFIRE fire - followed, when the fire reaches the WUI area, by an evacuation and a k-PERIL
    /// boundary through PREACT.exe (<see cref="RealizationBuilder"/>). The boundaries are aggregated into a per-cell
    /// probability raster until it stops moving (<see cref="ConvergenceAggregator"/>). Everything is resolved and
    /// checked up front (<see cref="CampaignSetup"/>), and a campaign's folder is named after the hash of the
    /// settings that decide its realizations, so resuming reuses only what those settings produced
    /// (<see cref="CampaignManifest"/>).
    /// </remarks>
    internal static class ConvergeTrigger
    {
        public static int Run(string[] args)
        {
            CampaignOptions o;
            try
            {
                o = CampaignOptions.Parse(args);
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                Console.Error.WriteLine("Run PREACTcli without arguments for the options.");
                return 2;
            }

            ChildProcessGuard.Install(o.CancelOnStdinClose);

            Campaign c = CampaignSetup.Resolve(o);
            if (c == null) return 1;

            CampaignManifest existing = CampaignManifest.Read(c.Folder);
            bool match = existing != null && string.Equals(existing.Hash, c.SettingsHash, StringComparison.Ordinal);
            var others = CampaignManifest.Others(c);

            if (o.Inspect)
            {
                PrintInspect(c, match, others);
                return 0;
            }

            if (o.Resume && !match)
            {
                if (others.Count > 0)
                {
                    CampaignManifest newest = others[0];
                    Console.Error.WriteLine("ERROR: --resume, but no campaign of this scenario was run with these settings. "
                                            + $"The newest one, {Path.GetFileName(newest.Folder)}, differs in:");
                    foreach (string d in newest.DifferencesFrom(c)) Console.Error.WriteLine("         " + d);
                    Console.Error.WriteLine("       Its realizations would not be the ones these settings produce. Run without "
                                            + "--resume to start a campaign with these settings (the other stays as it is).");
                    return 1;
                }

                if (o.ResumeOnly)
                {
                    Console.Error.WriteLine("ERROR: --resume-only, but there is no campaign with these settings to aggregate.");
                    return 1;
                }

                Console.WriteLine("Nothing to resume: no campaign with these settings has run yet; starting one.");
            }

            string held = Directory.Exists(c.Folder) ? CampaignLock.HeldBy(c.Folder) : null;
            if (held != null)
            {
                Console.Error.WriteLine("ERROR: " + held);
                return 1;
            }

            if (!o.Resume && Directory.Exists(c.Folder))
            {
                //Moved aside rather than deleted: its realizations cost hours to compute. Same settings, so it is the
                //same campaign run again, and a fresh start was asked for.
                string aside = c.Folder + "_replaced_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                Directory.Move(c.Folder, aside);
                Console.WriteLine($"A campaign with these settings already existed; it was moved to {Path.GetFileName(aside)} "
                                  + "(pass --resume to reuse its realizations instead).");
                match = false;
            }

            Directory.CreateDirectory(c.RealizationsDir);
            using (CampaignLock campaignLock = CampaignLock.Acquire(c.Folder, out string lockProblem))
            //The case's too, so a build of it from another scenario's folder waits for this campaign (review NIT).
            using (CampaignLock caseLock = CampaignLock.AcquireCase(Path.GetDirectoryName(c.InputsDir)))
            {
                if (campaignLock == null)
                {
                    Console.Error.WriteLine("ERROR: " + lockProblem);
                    return 1;
                }

                if (match) WarnIfPreactChanged(existing, c);

                if (!o.ResumeOnly)
                {
                    if (!CampaignSetup.PrepareWeather(c)) return 1;
                    WriteSnapshots(c);
                }

                if (!match) CampaignManifest.Write(c);
                if (!o.ResumeOnly) CampaignReports.WriteWeatherDistributions(c);

                Console.WriteLine(CampaignLayout.CampaignDirTag + c.Folder);
                PrintSummary(c, match);

                return new ConvergenceAggregator(c).Run();
            }
        }

        /// <summary>
        /// The template and fuel tables, copied once into the campaign folder: every realization runs from these,
        /// so an edit to the case's own files mid-campaign cannot reach half of it, and no realization writes to
        /// anything shared.
        /// </summary>
        private static void WriteSnapshots(Campaign c)
        {
            File.WriteAllLines(Path.Combine(c.Folder, CampaignLayout.TemplateSnapshot), c.TemplateLines);
            File.Copy(c.FuelTableSource, Path.Combine(c.TablesDir, ElmfireStems.FuelModelTable), overwrite: true);
            if (c.BuildingTableSource != null)
            {
                File.Copy(c.BuildingTableSource, Path.Combine(c.TablesDir, c.BuildingTableName), overwrite: true);
            }
        }

        private static void WarnIfPreactChanged(CampaignManifest existing, Campaign c)
        {
            existing.Information.TryGetValue("preact", out string was);
            c.Information.TryGetValue("preact", out string now);
            if (!string.IsNullOrEmpty(was) && !string.IsNullOrEmpty(now) && was != now)
            {
                Console.Error.WriteLine("WARNING: PREACT.exe is not the build this campaign started with; reused boundaries "
                                        + "were computed by the earlier one.");
            }
        }

        private static void PrintSummary(Campaign c, bool resumed)
        {
            CampaignOptions o = c.Options;
            (int started, int ok, int not, int failed) = CampaignManifest.Count(c.Folder);

            Console.WriteLine($"Campaign {Path.GetFileName(c.Folder)}" + (resumed ? $" (resumed: {ok} boundaries and {not} "
                              + $"not-threatened realizations are reused, {failed} failed ones run again)" : ""));
            Console.WriteLine($"  scenario  {c.BaseWuiPath}");
            Console.WriteLine($"  template  {c.TemplatePath}");
            Console.WriteLine($"  inputs    {c.InputsDir} (fuel {c.FuelStem})");
            Console.WriteLine($"  fire      {o.Hours:0.##} h per realization (SIMULATION_TSTOP = {CampaignLayout.TstopSeconds(o.Hours):0} s), "
                              + $"wall-clock limit {o.MaxRuntimeSeconds / 60.0:0} min");
            Console.WriteLine($"  ignition  drawn from {c.IgnitionMaskStem}.tif ({c.IgnitableCells} cells with a positive weight)"
                              + (o.WindToWui ? $", wind aimed at the WUI area centroid ({c.WuiCentreX:F0}, {c.WuiCentreY:F0}; {c.WuiCells} cells)" : ""));
            Console.WriteLine($"  k-PERIL   each realization's own midflame wind (ELMFIRE mfws) and wind direction");
            Console.WriteLine($"  evacuation seeded per realization: [Simulation] RandomSeed = {o.Seed} + "
                              + $"{RealizationRunner.EvacuationSeedOffset} + index"
                              + (c.BaseRandomSeed != 0 ? $" (the scenario's own RandomSeed={c.BaseRandomSeed} is not used)" : ""));
            Console.WriteLine($"  runs      up to {o.MaxRealizations}, {o.Parallelism} at a time, until {o.Streak} consecutive "
                              + $"within {o.Tolerance:P0} per decile");
        }

        /// <summary>
        /// The answer to <c>--inspect</c>, as one tagged JSON line for the GUI and a readable summary for a person.
        /// </summary>
        private static void PrintInspect(Campaign c, bool match, System.Collections.Generic.List<CampaignManifest> others)
        {
            (int started, int ok, int not, int failed) = match ? CampaignManifest.Count(c.Folder) : (0, 0, 0, 0);
            string held = Directory.Exists(c.Folder) ? CampaignLock.HeldBy(c.Folder) : null;

            var sb = new StringBuilder(CampaignLayout.InspectTag);
            sb.Append('{');
            sb.Append("\"match\":").Append(match ? "true" : "false").Append(',');
            sb.Append("\"folder\":").Append(JsonString(c.Folder)).Append(',');
            sb.Append("\"ok\":").Append(ok).Append(',');
            sb.Append("\"notThreatened\":").Append(not).Append(',');
            sb.Append("\"failed\":").Append(failed).Append(',');
            sb.Append("\"started\":").Append(started).Append(',');
            sb.Append("\"running\":").Append(held != null ? "true" : "false").Append(',');
            sb.Append("\"others\":[");
            for (int i = 0; i < others.Count; ++i)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"folder\":").Append(JsonString(others[i].Folder)).Append(",\"differences\":[");
                sb.Append(string.Join(",", others[i].DifferencesFrom(c).Select(JsonString)));
                sb.Append("]}");
            }
            sb.Append("]}");
            Console.WriteLine(sb.ToString());

            if (held != null) Console.WriteLine("Running now: " + held);
            if (match)
            {
                Console.WriteLine($"A campaign with these settings exists: {c.Folder}. --resume would reuse {ok + not} "
                                  + $"finished realization(s) ({ok} boundaries, {not} not threatened) and run {failed} failed one(s) again.");
            }
            else
            {
                Console.WriteLine($"No campaign with these settings exists; it would be {c.Folder}.");
                foreach (CampaignManifest m in others)
                {
                    Console.WriteLine($"  {Path.GetFileName(m.Folder)} differs in: {string.Join("; ", m.DifferencesFrom(c))}");
                }
            }
        }

        private static string JsonString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? string.Empty)
            {
                if (ch == '"' || ch == '\\') sb.Append('\\').Append(ch);
                else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        public static void PrintUsage() => CampaignOptions.PrintUsage();
    }
}
