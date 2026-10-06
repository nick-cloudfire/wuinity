using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PREACT.Utility
{
    /// <summary>
    /// The names and numbers the trigger campaign (<c>PREACTcli converge-trigger</c>), the single ELMFIRE run and
    /// the GUI window that drives the campaign have to agree on: fire duration limits, the campaign folder layout,
    /// and the tags of the progress lines the CLI prints for the window to parse.
    /// </summary>
    /// <remarks>
    /// In core rather than in the CLI because the Unity window cannot reference the CLI, and these used to be
    /// literals on both sides of the process boundary - "PROGRESS_JSON " in two files, "_elmfire", "__prob_",
    /// "0_" + name and "realization_" wherever they were needed.
    /// </remarks>
    public static class CampaignLayout
    {
        // ------------------------------------------------------------------ fire duration (contract C6)

        /// <summary>
        /// How long each campaign realization's fire runs by default, in hours. Three days: ignitions are drawn
        /// from the whole domain, and the distant ones decide how far out the boundary sits.
        /// </summary>
        public const double DefaultFireHours = 72.0;

        public const double MinFireHours = 1.0;

        /// <summary>
        /// The longest fire with hourly weather (a single run, or a historical-day campaign), ten days: every hour is
        /// a weather band and a WindNinja solve. Campaigns with one-band weather run until the fire stops instead
        /// (<see cref="UntilStoppedHours"/>). The 2000 h Mati campaign that set this cost 7-21 h per realization
        /// because ELMFIRE's stall exit fired in only 5 of 20 - an ember-tracker bug, fixed in ELMFIRE 16f306f.
        /// </summary>
        public const double MaxFireHours = 240.0;

        /// <summary>Null when <paramref name="hours"/> is a fire duration this platform runs, else why not.</summary>
        public static string ValidateFireHours(double hours)
        {
            if (double.IsNaN(hours) || double.IsInfinity(hours))
            {
                return "the fire duration is not a number";
            }

            if (hours < MinFireHours || hours > MaxFireHours)
            {
                return $"the fire duration is {hours.ToString("0.##", CultureInfo.InvariantCulture)} h; it has to be "
                       + $"between {MinFireHours:0} and {MaxFireHours:0} hours (it is hours, not seconds)";
            }

            return null;
        }

        /// <summary>The namelist's SIMULATION_TSTOP for a fire of <paramref name="hours"/>: the only place seconds appear.</summary>
        public static double TstopSeconds(double hours) => hours * 3600.0;

        // ------------------------------------------------------------------ campaign fires that run until they stop

        /// <summary>
        /// A campaign's fire duration that means "until the fire stops by itself": ELMFIRE's stall exit ends each
        /// run once nothing moves any more, so no duration has to be guessed. Only with one weather band per
        /// realization (the default fitted weather), which ELMFIRE holds for the whole run; hourly weather has to
        /// cover the run band by band, so it keeps a duration in hours.
        /// </summary>
        /// <remarks>
        /// These realizations run without spotting (<see cref="SpottingGroup"/>). The stall exit waits for every
        /// ember to land; with spotting on, an ELMFIRE without the ember-tracker fix never stopped (an ember at the
        /// domain edge was never retired), and with the fix the fire still crept on to 258 h for 0.14 % more area.
        /// A run that reaches the wall-clock limit is reported as failed, not folded in as a finished fire.
        /// </remarks>
        public const double UntilStoppedHours = 0.0;

        public static bool IsUntilStopped(double hours) => hours == UntilStoppedHours;

        /// <summary>
        /// The SIMULATION_TSTOP sent when the fire runs until it stops: one year, far past any fire that spreads
        /// and well inside ELMFIRE's own cap (a 32-bit count of seconds). Never reached in practice.
        /// </summary>
        public const double UntilStoppedTstopHours = 8760.0;

        /// <summary>Wall-clock limit per ELMFIRE run when the fire runs until it stops: four hours (a Mati fire takes 7 min).</summary>
        public const double UntilStoppedMaxRuntimeSeconds = 4.0 * 3600.0;

        /// <summary>
        /// The range of the per-cell arrival-time histogram when the fire runs until it stops, in hours. Arrivals
        /// after it share the last bin, so a percentile that falls there is reported as this many hours - never
        /// later than the truth. On Mati 99 % of the burned area is reached by 66 h.
        /// </summary>
        public const double UntilStoppedStatisticsHours = 96.0;

        /// <summary>
        /// The namelist group a fire that runs until it stops switches spotting off in. Those campaign realizations
        /// run without spotting: the stall exit waits for every ember to land, and spotting keeps the slow tail going.
        /// </summary>
        public const string SpottingGroup = "SPOTTING";

        /// <summary>The &amp;OUTPUTS flags that need spotting: without it their arrays are never allocated.</summary>
        public static readonly string[] EmberOutputs =
        {
            "DUMP_SPOTTING_OUTPUTS", "ACCUMULATE_EMBER_FLUX", "DUMP_EMBER_FLUX", "DUMP_EMBER_IGNITION",
            "DUMP_EMBER_FLUX_TRANSIENT", "DUMP_TOTAL_DFC_RECEIVED", "DUMP_TOTAL_RAD_RECEIVED", "DUMP_TRANSIENT_DFC",
            "DUMP_TRANSIENT_RAD", "DUMP_HRR_TRANSIENT",
        };

        /// <summary>The SIMULATION_TSTOP of a campaign realization, in seconds.</summary>
        public static double CampaignTstopSeconds(double hours) =>
            TstopSeconds(IsUntilStopped(hours) ? UntilStoppedTstopHours : hours);

        /// <summary>How a campaign's fire duration reads to a user: "until the fire stops" or "72 h".</summary>
        public static string DescribeFireDuration(double hours) => IsUntilStopped(hours)
            ? "until the fire stops"
            : hours.ToString("0.##", CultureInfo.InvariantCulture) + " h";

        /// <summary>
        /// Null when <paramref name="hours"/> is a fire duration a campaign can run with this weather, else why not.
        /// One band per realization: until the fire stops, or any duration of at least an hour up to
        /// <see cref="UntilStoppedTstopHours"/>. Hourly (historical-day) weather: <see cref="ValidateFireHours"/>,
        /// because every hour of fire is a weather band and a WindNinja solve.
        /// </summary>
        public static string ValidateCampaignHours(double hours, bool singleBandWeather)
        {
            if (IsUntilStopped(hours))
            {
                return singleBandWeather
                    ? null
                    : "historical-day weather has one band per hour of fire, so it needs a duration in hours; "
                      + "running until the fire stops needs the fitted (one-band) weather";
            }

            if (!singleBandWeather) return ValidateFireHours(hours);

            if (double.IsNaN(hours) || double.IsInfinity(hours)) return "the fire duration is not a number";
            if (hours < MinFireHours || hours > UntilStoppedTstopHours)
            {
                return $"the fire duration is {hours.ToString("0.##", CultureInfo.InvariantCulture)} h; it has to be "
                       + $"between {MinFireHours:0} and {UntilStoppedTstopHours:0} hours (it is hours, not seconds)";
            }
            return null;
        }

        /// <summary>
        /// A wall-clock limit for one ELMFIRE run, in seconds (MAX_RUNTIME): two minutes per simulated hour, at least
        /// an hour. A 72 h Mati fire takes about six minutes on one core, so this only stops a run that is not
        /// going to finish - and a stopped run is reported as failed rather than aggregated as a short fire.
        /// </summary>
        public static double DefaultMaxRuntimeSeconds(double hours) => IsUntilStopped(hours)
            ? UntilStoppedMaxRuntimeSeconds
            : System.Math.Max(3600.0, 120.0 * hours);

        /// <summary>
        /// Width of the per-cell arrival-time histogram bins for a fire of <paramref name="hours"/>: one hour up to
        /// 96 h, then whole hours so there are never more than about 96 bins (a 240 h fire gets 3 h bins). The bins
        /// used to be one hour whatever the duration, which at the 2000 h real campaign meant 1.2 GB of counters.
        /// </summary>
        public static double StatisticsBinSeconds(double hours) =>
            3600.0 * System.Math.Max(1.0, System.Math.Ceiling(StatisticsHours(hours) / 96.0));

        /// <summary>
        /// The range of the arrival-time histogram, in seconds: the fire's duration, or
        /// <see cref="UntilStoppedStatisticsHours"/> when it runs until it stops.
        /// </summary>
        public static double StatisticsDurationSeconds(double hours) => TstopSeconds(StatisticsHours(hours));

        private static double StatisticsHours(double hours) => IsUntilStopped(hours) ? UntilStoppedStatisticsHours : hours;

        // ------------------------------------------------------------------ folders

        /// <summary>Everything a campaign writes lives in one folder under the scenario's <c>_output</c>.</summary>
        public const string OutputFolder = "_output";

        public const string CampaignFolderPrefix = "campaign_";

        /// <summary>
        /// The name a campaign's folder is made from: the scenario's <c>[Simulation] Name</c>, or the .wui's file
        /// name when it has none.
        /// </summary>
        public static string CampaignScenarioName(string simulationName, string wuiPath)
        {
            if (!string.IsNullOrWhiteSpace(simulationName)) return simulationName.Trim();
            return string.IsNullOrEmpty(wuiPath) ? "scenario" : Path.GetFileNameWithoutExtension(wuiPath);
        }

        /// <summary>
        /// The scenario's most recently written campaign folder under <c>&lt;scenarioFolder&gt;/_output</c>, or
        /// null when it has none. A folder moved aside by a fresh start (<c>..._replaced_&lt;time&gt;</c>) is no
        /// longer a campaign and is skipped.
        /// </summary>
        /// <remarks>
        /// For readers of a campaign's results (the GUI's workflow and Results window). A campaign keeps its
        /// probability raster, convergence CSV and ensemble statistics in its own folder; they used to be written
        /// into <c>_output</c> itself, where every campaign overwrote the last one's.
        /// </remarks>
        public static string LatestCampaignFolder(string scenarioFolder, string scenarioName)
        {
            if (string.IsNullOrEmpty(scenarioFolder)) return null;
            string output = Path.Combine(scenarioFolder, OutputFolder);
            if (!Directory.Exists(output)) return null;

            string prefix = CampaignFolderName(scenarioName, string.Empty);
            string best = null;
            DateTime bestAt = DateTime.MinValue;
            try
            {
                foreach (string folder in Directory.GetDirectories(output, prefix + "*"))
                {
                    if (!IsSettingsHash(Path.GetFileName(folder).Substring(prefix.Length))) continue;

                    DateTime at = LastWritten(folder);
                    if (best == null || at > bestAt)
                    {
                        best = folder;
                        bestAt = at;
                    }
                }
            }
            catch (IOException)
            {
                return best;
            }
            catch (UnauthorizedAccessException)
            {
                return best;
            }
            return best;
        }

        private static bool IsSettingsHash(string text)
        {
            if (text.Length != 8) return false;
            foreach (char c in text)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        //A folder's own time changes only when an entry is added or removed; the files a running campaign
        //rewrites say when it last did anything.
        private static DateTime LastWritten(string folder)
        {
            DateTime at = Directory.GetLastWriteTimeUtc(folder);
            foreach (string name in new[] { ManifestFile, StatusFile, ConvergenceCsv, LiveProbabilityRaster, ProbabilityRaster })
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path))
                {
                    DateTime written = File.GetLastWriteTimeUtc(path);
                    if (written > at) at = written;
                }
            }
            return at;
        }

        /// <summary>
        /// <c>_output/campaign_&lt;scenario&gt;_&lt;settings hash&gt;</c>. The hash is of every setting that decides the
        /// realizations, so a campaign with other settings is another folder, and resuming one reuses only
        /// realizations computed with the same settings.
        /// </summary>
        public static string CampaignFolderName(string scenarioName, string settingsHash8)
        {
            return CampaignFolderPrefix + Sanitize(scenarioName) + "_" + settingsHash8;
        }

        public const string ManifestFile = "campaign.json";
        public const string LockFile = "campaign.lock";

        /// <summary>
        /// The lock every running campaign holds in the case folder whose inputs its realizations read - shared, so
        /// campaigns of several scenarios can run on one case, and seen by a build of the case from any scenario (a
        /// campaign's own folder lock is under its scenario's _output, where a build from another folder does not look).
        /// </summary>
        public const string CaseLockFile = "campaign.lock";

        /// <summary>
        /// The manifest setting that says how each realization's evacuation is seeded (FIX-A's MA-5). A campaign whose
        /// manifest lacks it was made before that - and before the k-PERIL wrapper read its grids the right way round
        /// (BL-1), which came with it - so its boundaries are not comparable with a new campaign's, and since the
        /// setting is part of the hash, it cannot be resumed.
        /// </summary>
        public const string EvacuationSeedSetting = "evacuation.seed";

        /// <summary>
        /// Whether the campaign in <paramref name="campaignFolder"/> - a campaign folder, or <c>_output</c> for a campaign
        /// from before campaigns had folders - was made by a version before <see cref="EvacuationSeedSetting"/>: its
        /// manifest does not have it, or it has results and no manifest at all. False when it cannot tell (no results yet,
        /// or nothing readable), so a campaign that is just starting is never called old.
        /// </summary>
        public static bool PredatesEvacuationSeeds(string campaignFolder)
        {
            if (string.IsNullOrEmpty(campaignFolder) || !Directory.Exists(campaignFolder)) return false;

            try
            {
                string manifest = Path.Combine(campaignFolder, ManifestFile);
                if (File.Exists(manifest))
                {
                    return File.ReadAllText(manifest).IndexOf("\"" + EvacuationSeedSetting + "\"", StringComparison.Ordinal) < 0;
                }

                return File.Exists(Path.Combine(campaignFolder, ConvergenceCsv))
                       || File.Exists(Path.Combine(campaignFolder, ProbabilityRaster))
                       || File.Exists(Path.Combine(campaignFolder, LiveProbabilityRaster));
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>What to tell someone looking at a campaign that <see cref="PredatesEvacuationSeeds"/>.</summary>
        public static string DescribeEarlierCampaign(string campaignFolder)
        {
            string name = Path.GetFileName((campaignFolder ?? string.Empty).TrimEnd('/', '\\'));
            string what = string.Equals(name, OutputFolder, StringComparison.OrdinalIgnoreCase)
                ? "The campaign whose results are in " + OutputFolder
                : name;
            return $"{what} was made by an earlier version of the campaign: its trigger boundaries were "
                   + "computed before k-PERIL read the fire's grids the right way round, so its probability raster is not "
                   + "comparable with a new one, and it cannot be resumed (each realization's evacuation now has its own seed, "
                   + "which changes the settings). Run the campaign again: it starts in a folder of its own, and the old one is kept.";
        }

        /// <summary>
        /// Whether a campaign process holds <paramref name="campaignFolder"/>'s <see cref="LockFile"/> now: it keeps
        /// the file open with no sharing for as long as it runs, so the operating system releases it however the
        /// process ends, and a file left behind is not a lock.
        /// </summary>
        public static bool IsLockHeld(string campaignFolder)
        {
            string path = Path.Combine(campaignFolder, LockFile);
            if (!File.Exists(path)) return false;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return false;
                }
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>The campaign folders under <paramref name="scenarioFolder"/>/_output a campaign is running in now.</summary>
        public static List<string> RunningCampaigns(string scenarioFolder)
        {
            var running = new List<string>();
            if (string.IsNullOrEmpty(scenarioFolder)) return running;
            string output = Path.Combine(scenarioFolder, OutputFolder);
            if (!Directory.Exists(output)) return running;

            try
            {
                foreach (string folder in Directory.GetDirectories(output, CampaignFolderPrefix + "*"))
                {
                    if (IsLockHeld(folder)) running.Add(folder);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return running;
        }

        /// <summary>
        /// Why a scenario's case cannot be built now, or null: a campaign running from the scenario's folder reads the
        /// case's inputs in every realization, and a build re-cuts, rewrites and moves them (review MI-4).
        /// </summary>
        public static string DescribeRunningCampaign(string scenarioFolder, string caseDirectory = null)
        {
            List<string> running = RunningCampaigns(scenarioFolder);
            if (running.Count > 0)
            {
                return $"a trigger campaign is running ({string.Join(", ", running.Select(Path.GetFileName))}) and every one of "
                       + "its realizations reads this case's rasters, which a build re-cuts and rewrites. Wait for it to "
                       + "finish, or stop it, then build the case.";
            }

            //A campaign of another scenario, in another folder, on the same case (review NIT).
            if (!string.IsNullOrEmpty(caseDirectory) && Directory.Exists(caseDirectory) && IsLockHeld(caseDirectory))
            {
                return $"a trigger campaign is running on this case (it holds {Path.Combine(caseDirectory, CaseLockFile)}), started "
                       + "from another scenario or folder, and every one of its realizations reads the case's rasters, which a "
                       + "build re-cuts and rewrites. Wait for it to finish, or stop it, then build the case.";
            }
            return null;
        }
        public const string TemplateSnapshot = "template.data";
        public const string RealizationsFolder = "realizations";
        public const string RealizationRecord = "realization.txt";
        public const string RealizationWeatherFolder = "weather";
        public const string RealizationPreactFolder = "preact";
        public const string RealizationPreactLog = "preact.log";
        public const string RealizationScenarioCopy = "preact_scenario.wui";
        public const string ConvergenceCsv = "trigger_convergence.csv";
        public const string ProbabilityRaster = "trigger_probability.asc";
        public const string LiveProbabilityRaster = "trigger_probability_live.asc";
        public const string RealizationsCsv = "realizations.csv";

        /// <summary>What the campaign is doing now (<see cref="CampaignStatus"/>), rewritten about once a second while it runs.</summary>
        public const string StatusFile = "status.json";

        /// <summary>Everything the campaign CLI printed, appended run after run (a resume adds to it).</summary>
        public const string CampaignLogFile = "campaign.log";
        public const string WeatherDistributionsCsv = "weather_distributions.csv";
        public const string WeatherRealizationsCsv = "weather_realizations.csv";
        public const string EnsemblePrefix = "ensemble";

        /// <summary>
        /// A file written beside a raster that is not a raster: the <c>.prj</c> the engine writes beside every
        /// boundary and campaign raster (its CRS), and what GDAL or QGIS add on opening one - <c>.aux.xml</c>
        /// statistics, <c>.ovr</c> overviews, <c>.tfw</c>/<c>.wld</c> world files. A results listing that matches by
        /// name (<c>ensemble_burn_probability.*</c>) counted each of them as another result.
        /// </summary>
        public static bool IsRasterSidecar(string fileName)
        {
            string lower = Path.GetFileName(fileName ?? string.Empty).ToLowerInvariant();
            foreach (string suffix in new[] { ".prj", ".aux.xml", ".ovr", ".tfw", ".wld", ".aux" })
            {
                if (lower.EndsWith(suffix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>A raster a result can be shown from, by its extension: <c>.asc</c>, <c>.tif</c> or <c>.tiff</c>.</summary>
        public static bool IsRasterFile(string fileName)
        {
            string extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            return extension == ".asc" || extension == ".tif" || extension == ".tiff";
        }

        /// <summary>Realization indices are always this wide, so names sort and never collide across paddings.</summary>
        public const int IndexWidth = 7;

        public static string RealizationId(int index) => index.ToString(CultureInfo.InvariantCulture).PadLeft(IndexWidth, '0');

        // ------------------------------------------------------------------ progress protocol (CLI -> GUI)

        /// <summary><c>PROGRESS &lt;done&gt;/&lt;max&gt; realization &lt;id&gt;</c></summary>
        public const string ProgressTag = "PROGRESS ";

        /// <summary>One JSON object per completed realization: counts, streak, decile areas and deltas.</summary>
        public const string ProgressJsonTag = "PROGRESS_JSON ";

        /// <summary>The live probability raster was rewritten; the rest of the line is its path.</summary>
        public const string ProgressRasterTag = "PROGRESS_RASTER ";

        /// <summary>The campaign's folder; printed once at startup.</summary>
        public const string CampaignDirTag = "CAMPAIGN_DIR ";

        /// <summary>
        /// The answer to <c>--inspect</c>: a JSON object saying whether a campaign with these exact settings exists
        /// and how many realizations it would reuse.
        /// </summary>
        public const string InspectTag = "CAMPAIGN_INSPECT ";

        /// <summary>
        /// PREACT.exe's evacuation progress, <c>SIM_TIME &lt;seconds&gt; of &lt;end seconds&gt;</c>, printed every few
        /// seconds of wall clock while a simulation runs, for the campaign's status (<see cref="CampaignStatus"/>).
        /// </summary>
        public const string SimulationTimeTag = "SIM_TIME ";

        /// <summary>Realization outcome categories, in the CSV and PROGRESS_JSON.</summary>
        public const string StatusOk = "ok";
        public const string StatusNotThreatened = "not-threatened";
        public const string StatusFailed = "failed";

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "scenario";
            char[] chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; ++i)
            {
                char c = chars[i];
                if (!(char.IsLetterOrDigit(c) || c == '-' || c == '_')) chars[i] = '_';
            }
            return new string(chars);
        }

        /// <summary>A path inside a folder, as a relative, forward-slashed path for a .wui.</summary>
        public static string RelativeForWui(string rootFolder, string path)
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(rootFolder);
            string relative;
            try
            {
                relative = Path.GetRelativePath(root, full);
            }
            catch
            {
                relative = full;
            }
            return relative.Replace('\\', '/');
        }
    }
}
