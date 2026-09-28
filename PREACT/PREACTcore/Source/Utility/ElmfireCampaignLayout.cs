using System;
using System.Globalization;
using System.IO;

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
        /// The longest fire anything here will ask ELMFIRE for, ten days. The real Mati campaign ran 2000 h
        /// (<c>SIMULATION_TSTOP = 7200000</c>): every spreading fire had burned the whole domain within two days and
        /// then crept for weeks, costing 7-21 h per realization instead of minutes, and ELMFIRE's stall exit
        /// fired in only 5 of 20.
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

        /// <summary>
        /// A wall-clock limit for one ELMFIRE run, in seconds (MAX_RUNTIME): two minutes per simulated hour, at least
        /// an hour. A 72 h Mati fire takes about six minutes on one core, so this only stops a run that is not
        /// going to finish - and a stopped run is reported as failed rather than aggregated as a short fire.
        /// </summary>
        public static double DefaultMaxRuntimeSeconds(double hours) => System.Math.Max(3600.0, 120.0 * hours);

        /// <summary>
        /// Width of the per-cell arrival-time histogram bins for a fire of <paramref name="hours"/>: one hour up to
        /// 96 h, then whole hours so there are never more than about 96 bins (a 240 h fire gets 3 h bins). The bins
        /// used to be one hour whatever the duration, which at the 2000 h real campaign meant 1.2 GB of counters.
        /// </summary>
        public static double StatisticsBinSeconds(double hours) => 3600.0 * System.Math.Max(1.0, System.Math.Ceiling(hours / 96.0));

        // ------------------------------------------------------------------ folders

        /// <summary>Everything a campaign writes lives in one folder under the scenario's <c>_output</c>.</summary>
        public const string OutputFolder = "_output";

        public const string CampaignFolderPrefix = "campaign_";

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
        public const string WeatherDistributionsCsv = "weather_distributions.csv";
        public const string WeatherRealizationsCsv = "weather_realizations.csv";
        public const string EnsemblePrefix = "ensemble";

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
