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

        /// <summary>
        /// Deletes the per-realization ELMFIRE run directories under <c>_elmfire</c>, so a restart begins with
        /// nothing on disk that a reader could mistake for this campaign's work.
        /// </summary>
        /// <remarks>
        /// Deleted rather than archived, unlike <see cref="ArchivePrevious"/>. These are reproducible - a
        /// namelist, a log, and dumps that a re-run rewrites - and they are large: on a measured campaign,
        /// ten run directories held ELMFIRE's ENVI scratch conversions of the whole input stack, which for an
        /// 88 MB weather series is most of a gigabyte per realization. Keeping every restart's copy fills the
        /// disk long before anyone reads the third one.
        ///
        /// The aggregate directories, and the per-realization boundaries in <c>_output</c>, are
        /// <see cref="ArchivePrevious"/>'s business and are not touched here: those cost hours to produce and
        /// cannot be regenerated from anything left on disk.
        ///
        /// <paramref name="keepLogs"/> exists because the logs are the exception to "reproducible": a
        /// realization that already failed cannot be re-run into the same failure once its inputs are gone,
        /// and <c>elmfire.log</c> is the only record of what it said. They are small enough that keeping them
        /// costs nothing.
        /// </remarks>
        public static void ClearRunDirectories(string caseDir, bool keepLogs = true)
        {
            if (string.IsNullOrEmpty(caseDir)) return;

            string root = Path.Combine(caseDir, "_elmfire");
            if (!Directory.Exists(root)) return;

            string archive = null;
            int cleared = 0, keptLogs = 0;
            long freed = 0;
            var stuck = new List<string>();

            foreach (string dir in Directory.GetDirectories(root))
            {
                //Only the numbered realization directories. _elmfire also holds shared working folders the
                //driver set up before the first realization - weather_single_band for one - and deleting
                //those would take out inputs this campaign is about to read.
                string name = Path.GetFileName(dir);
                bool numbered = name.Length > 0;
                foreach (char c in name)
                {
                    if (!char.IsDigit(c)) { numbered = false; break; }
                }
                if (!numbered) continue;

                try
                {
                    foreach (string file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { freed += new FileInfo(file).Length; } catch { }
                    }

                    string log = Path.Combine(dir, "elmfire.log");
                    if (keepLogs && File.Exists(log))
                    {
                        if (archive == null)
                        {
                            archive = Path.Combine(root, "previous_logs_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                            Directory.CreateDirectory(archive);
                        }
                        File.Move(log, Path.Combine(archive, name + ".log"));
                        ++keptLogs;
                    }

                    Directory.Delete(dir, recursive: true);
                    ++cleared;
                }
                catch (Exception e)
                {
                    stuck.Add(name + " (" + e.Message + ")");
                }
            }

            if (cleared == 0 && stuck.Count == 0) return;

            Console.WriteLine($"Previous run directories: {cleared} cleared, {freed / (1024.0 * 1024.0):F0} MB freed"
                + (keptLogs > 0 ? $", {keptLogs} log(s) kept in {archive}" : "") + ".");

            if (stuck.Count > 0)
            {
                //Named, because a run directory that could not be emptied is one whose old dumps this
                //campaign's raster glob can still pick up.
                Console.Error.WriteLine("WARNING: could not clear " + string.Join(", ", stuck)
                    + ". Old ELMFIRE dumps left there may be read back as this campaign's.");
            }
        }
    }
}
