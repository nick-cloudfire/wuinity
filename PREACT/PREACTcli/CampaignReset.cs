using System;
using System.Collections.Generic;
using System.IO;

namespace PREACTcli
{
    /// <summary>
    /// Moves a previous campaign's results out of the way before a new one starts, so nothing left on disk
    /// can be mistaken for — or read back as — a result of the run about to happen.
    /// </summary>
    /// <remarks>
    /// Running without <c>--resume</c> used to mean only "do not reuse": every realization was genuinely
    /// re-run, but into directories that still held the last campaign's files, and a new file only replaced
    /// an old one when it happened to have the same name. Three things followed from that, all of them quiet:
    ///
    /// A realization's boundary survived a failed re-run and was aggregated as though it were new
    /// (<see cref="RealizationRunner"/> now deletes it before running, which is the direct fix); ELMFIRE dumps
    /// accumulated and the longest-lived one won the glob (<c>ElmfireRunner</c> now empties the run directory);
    /// and the campaign-level rasters — which are only written at the very end — stayed put, so a campaign that
    /// ended with "no realizations produced a usable trigger boundary" left the previous week's probability
    /// raster sitting in <c>_output</c> looking like the current answer. This class handles that last one.
    ///
    /// Moved into a timestamped folder rather than deleted. These files cost hours of SUMO and ELMFIRE runtime
    /// to produce and cannot be regenerated from anything on disk, so quietly destroying them to protect
    /// against confusing them would be the worse trade. The folder name says what they are.
    /// </remarks>
    internal static class CampaignReset
    {
        /// <summary>Aggregates, which every campaign rewrites from scratch at the end.</summary>
        private static readonly string[] AggregatePatterns =
        {
            "trigger_probability*.asc",
            "trigger_convergence*.csv",
            "ensemble_*.asc",
        };

        /// <summary>
        /// Archives what a previous campaign left in <paramref name="outputDir"/>.
        /// </summary>
        /// <param name="includeBoundaries">
        /// Whether the per-realization boundaries go too. False when resuming — those are precisely what a
        /// resumed campaign exists to reuse.
        /// </param>
        /// <param name="alsoArchive">
        /// Extra paths the caller writes outside the patterns above, e.g. a <c>--out</c> or
        /// <c>--diagnostics</c> pointed somewhere else. Nulls and missing files are ignored.
        /// </param>
        /// <param name="caseDir">
        /// The scenario's folder, swept for <c>__prob_*.wui</c> left behind by interrupted realizations. Only
        /// when <paramref name="includeBoundaries"/>: they are regenerated per realization, and one still lying
        /// there is litter that reads like a run in progress.
        /// </param>
        public static void ArchivePrevious(string outputDir, string caseDir, bool includeBoundaries,
                                           IEnumerable<string> alsoArchive = null)
        {
            if (!Directory.Exists(outputDir)) return;

            //Ordered and deduplicated by full path: '*trigger_*.asc' also matches trigger_probability.asc,
            //and moving a file twice would fail the second time and be reported as a problem.
            var found = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Consider(string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                string full = Path.GetFullPath(path);
                if (seen.Add(full)) found.Add(full);
            }

            foreach (string pattern in AggregatePatterns)
            {
                foreach (string path in Directory.GetFiles(outputDir, pattern)) Consider(path);
            }

            if (alsoArchive != null)
            {
                foreach (string path in alsoArchive) Consider(path);
            }

            if (includeBoundaries)
            {
                foreach (string path in Directory.GetFiles(outputDir, "*trigger_*.asc")) Consider(path);
            }

            if (found.Count == 0) return;

            //Timestamped, so restarting twice cannot destroy the archive of the first restart.
            string archive = Path.Combine(outputDir, "previous_campaign_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(archive);

            int moved = 0;
            var stuck = new List<string>();
            foreach (string path in found)
            {
                try
                {
                    File.Move(path, Path.Combine(archive, Path.GetFileName(path)));
                    ++moved;
                }
                catch (Exception e)
                {
                    stuck.Add(Path.GetFileName(path) + " (" + e.Message + ")");
                }
            }

            Console.WriteLine($"Previous campaign: {moved} file(s) moved to {archive}"
                + (includeBoundaries ? "." : " (boundaries kept for --resume)."));

            if (stuck.Count > 0)
            {
                //Named rather than counted: a probability raster that could not be moved is the one file whose
                //staying behind can be read as this campaign's answer.
                Console.Error.WriteLine("WARNING: could not move " + string.Join(", ", stuck)
                    + ". Anything left here is from the previous campaign, not this one.");
            }

            if (!includeBoundaries || string.IsNullOrEmpty(caseDir) || !Directory.Exists(caseDir)) return;

            foreach (string path in Directory.GetFiles(caseDir, "__prob_*.wui"))
            {
                try { File.Delete(path); } catch { }
            }
        }
    }
}
