using System;
using System.Collections.Generic;
using System.Globalization;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>
    /// What <c>converge-trigger</c> was asked to do, parsed strictly (<see cref="CliArgs"/>).
    /// </summary>
    internal sealed class CampaignOptions
    {
        public string BaseWui;
        public int MaxRealizations;
        public int Start = 1;
        public int Streak = 20;
        public double Tolerance = 0.02;

        /// <summary>
        /// Realizations running at once. Half the cores: each is an ELMFIRE run, a WindNinja solve and a SUMO
        /// evacuation, and at one per core the real campaign ran 22 of them at once on a machine that had the
        /// memory for fewer.
        /// </summary>
        public int Parallelism = System.Math.Max(1, Environment.ProcessorCount / 2);

        public bool Resume;
        public bool ResumeOnly;

        /// <summary>Report whether a campaign with these settings exists and what it would reuse, then exit.</summary>
        public bool Inspect;

        public string PreactExe;
        public string OutPath;

        // ---- the fire
        public string ElmfireExe;
        public string ElmfireTemplate;
        public string ElmfireInputs;
        public string PathToGdal;

        /// <summary>Hours of fire per realization (contract C6: hours at every interface; seconds in the namelist).</summary>
        public double Hours = CampaignLayout.DefaultFireHours;

        /// <summary>Wall-clock limit per ELMFIRE run in minutes; 0 means <see cref="CampaignLayout.DefaultMaxRuntimeSeconds"/>.</summary>
        public double MaxRuntimeMinutes;

        public int Seed = 12345;

        // ---- weather
        public WeatherRasterPipeline.SamplingMode WeatherSampling = WeatherRasterPipeline.SamplingMode.FittedDistributions;
        public int CandidateDaysPerYear = 10;
        public bool FitLiveFuelMoisture = true;
        public bool WindToWui = true;
        public string WeatherArchive;
        public int ArchiveStartYear = 2000;
        public int ArchiveEndYear;
        public int ConditioningDays = 20;
        public string WindNinjaExe;
        public string WindNinjaMesh = "fine";

        /// <summary>
        /// Accept a spatially uniform wind (no WindNinja) and uniform dead fuel moisture (Nelson unavailable)
        /// instead of stopping. Off by default: a uniform wind field makes every boundary ignore the terrain, and
        /// a campaign that silently degraded to it looks exactly like one that did not.
        /// </summary>
        public bool AllowUniformWeather;

        /// <summary>
        /// Treat the closing of stdin as a cancel. The GUI passes this, so closing the pipe - or the GUI going
        /// away - stops the campaign and every child it started.
        /// </summary>
        public bool CancelOnStdinClose;

        public double MaxRuntimeSeconds => MaxRuntimeMinutes > 0 ? MaxRuntimeMinutes * 60.0 : CampaignLayout.DefaultMaxRuntimeSeconds(Hours);

        /// <summary>Parses; throws <see cref="ArgumentException"/> with a message for the user.</summary>
        public static CampaignOptions Parse(string[] args)
        {
            var o = new CampaignOptions();
            new CliArgs()
                .Value("--wui", v => o.BaseWui = v)
                .Int("--max", v => o.MaxRealizations = v, 1)
                .Int("--start", v => o.Start = v, 1)
                .Int("--streak", v => o.Streak = v, 1)
                .Double("--tolerance", v => o.Tolerance = v, 1e-9, 1.0)
                .Int("--parallel", v => o.Parallelism = v, 1, 1024)
                .Switch("--resume", () => o.Resume = true)
                .Switch("--resume-only", () => { o.Resume = true; o.ResumeOnly = true; })
                .Switch("--inspect", () => o.Inspect = true)
                .Value("--preact", v => o.PreactExe = v)
                .Value("--out", v => o.OutPath = v)
                .Value("--elmfire", v => o.ElmfireExe = v)
                .Value("--elmfire-template", v => o.ElmfireTemplate = v)
                .Value("--elmfire-inputs", v => o.ElmfireInputs = v)
                .Value("--gdal", v => o.PathToGdal = v)
                .Value("--hours", v =>
                {
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                        throw new ArgumentException($"--hours takes a number of hours, not '{v}'.");
                    string problem = CampaignLayout.ValidateFireHours(h);
                    if (problem != null) throw new ArgumentException("--hours: " + problem + ".");
                    o.Hours = h;
                })
                .Double("--max-runtime-minutes", v => o.MaxRuntimeMinutes = v, 1.0, 100000.0)
                .Int("--seed", v => o.Seed = v)
                .Switch("--historical-day-weather", () => o.WeatherSampling = WeatherRasterPipeline.SamplingMode.HistoricalDay)
                .Int("--candidate-days-per-year", v => o.CandidateDaysPerYear = v, 1, 366)
                .Switch("--no-live-fuel-moisture", () => o.FitLiveFuelMoisture = false)
                .Switch("--no-wind-to-wui", () => o.WindToWui = false)
                .Value("--weather-archive", v => o.WeatherArchive = v)
                .Int("--climatology-from", v => o.ArchiveStartYear = v, 1940, 2100)
                .Int("--climatology-to", v => o.ArchiveEndYear = v, 1940, 2100)
                .Int("--conditioning-days", v => o.ConditioningDays = v, 1, 365)
                .Value("--windninja", v => o.WindNinjaExe = v)
                .Value("--wn-mesh", v => o.WindNinjaMesh = v)
                .Switch("--allow-uniform-weather", () => o.AllowUniformWeather = true)
                .Switch("--cancel-on-stdin-close", () => o.CancelOnStdinClose = true)
                .Retired("--tstop", "the fire duration is --hours now, in hours (it was seconds, and 7200000 s was a "
                                    + "2000 h campaign).")
                .Retired("--dir", "pre-generated ensembles are no longer read; realizations are generated with ELMFIRE.")
                .Retired("--toa", "pre-generated ensembles are no longer read.")
                .Retired("--ros", "pre-generated ensembles are no longer read.")
                .Retired("--sd", "pre-generated ensembles are no longer read.")
                .Retired("--fi", "pre-generated ensembles are no longer read.")
                .Retired("--pad", "realization indices are always 7 digits wide.")
                .Retired("--shared-weather", "every realization draws its own weather.")
                .Retired("--single-band-weather", "fitted weather is one band by construction; historical-day weather "
                                                  + "covers the whole fire.")
                .Retired("--realization-weather", "it is what always happens; drop the flag.")
                .Retired("--fitted-weather", "it is the default; drop the flag.")
                .Retired("--wind-to-wui", "it is the default; drop the flag.")
                .Retired("--live-fuel-moisture", "it is the default; drop the flag.")
                .Retired("--progress-json", "progress lines are always written; drop the flag.")
                .Retired("--max-weather-bands", "historical-day weather always covers the whole fire.")
                .Retired("--diagnostics", "the convergence CSV is always in the campaign folder.")
                .Parse(args);

            if (string.IsNullOrEmpty(o.BaseWui))
            {
                throw new ArgumentException("--wui is required.");
            }
            if (o.MaxRealizations <= 0 && !o.Inspect)
            {
                throw new ArgumentException("--max is required.");
            }
            if (o.ArchiveEndYear > 0 && o.ArchiveEndYear < o.ArchiveStartYear)
            {
                throw new ArgumentException("--climatology-to is before --climatology-from.");
            }
            return o;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("  PREACTcli converge-trigger --wui <base.wui> --max <N> [options]");
            Console.WriteLine("      Runs fire realizations with ELMFIRE, an evacuation + k-PERIL boundary per realization");
            Console.WriteLine("      (PREACT.exe), and aggregates the boundaries into a probability raster until every decile");
            Console.WriteLine("      of it moves by less than --tolerance for --streak consecutive realizations.");
            Console.WriteLine("      Everything goes to <scenario>/_output/campaign_<name>_<settings hash>/.");
            Console.WriteLine("      [--hours <h=72>]              hours of fire per realization, 1 to 240");
            Console.WriteLine("      [--seed <n=12345>] [--start <n=1>] [--streak <n=20>] [--tolerance <fraction=0.02>]");
            Console.WriteLine("      [--parallel <n=cores/2>] [--preact <PREACT.exe>] [--out <copy of the probability raster>]");
            Console.WriteLine("      [--resume]                    reuse the realizations of a campaign with exactly these settings;");
            Console.WriteLine("                                    refused when the only campaigns on disk used other settings");
            Console.WriteLine("      [--resume-only]               aggregate that campaign's realizations without running any");
            Console.WriteLine("      [--inspect]                   say whether such a campaign exists and what it would reuse, then exit");
            Console.WriteLine("      The ELMFIRE case, template and executable come from the scenario's [ELMFIRE] section; override:");
            Console.WriteLine("      [--elmfire <exe>] [--elmfire-template <namelist>] [--elmfire-inputs <folder>] [--gdal <bin>]");
            Console.WriteLine("      [--max-runtime-minutes <m>]   wall-clock limit per ELMFIRE run (default 2 min per hour of fire, >= 60)");
            Console.WriteLine("      Weather: every realization draws its own, into its own folder. By default each parameter is drawn");
            Console.WriteLine("      from a normal fitted to the worst fire-weather days on record (one band, one WindNinja solve),");
            Console.WriteLine("      with dead fuel moisture from the drawn air (Simard) and live fuel moisture resampled from the");
            Console.WriteLine("      NFDRS4 GSI march over the record.");
            Console.WriteLine("      [--historical-day-weather]    replay whole historical days instead (WindNinja per hour, Nelson)");
            Console.WriteLine("      [--candidate-days-per-year <n=10>] [--no-live-fuel-moisture]");
            Console.WriteLine("      [--weather-archive <csv>] [--climatology-from <year=2000>] [--climatology-to <year>]");
            Console.WriteLine("      [--conditioning-days <n=20>] [--windninja <exe>] [--wn-mesh coarse|medium|fine]");
            Console.WriteLine("      [--allow-uniform-weather]     run without WindNinja/Nelson (uniform wind and moisture) instead of stopping");
            Console.WriteLine("      [--no-wind-to-wui]            keep each draw's own wind direction instead of aiming it from the");
            Console.WriteLine("                                    ignition at the WUI area");
            Console.WriteLine("      [--cancel-on-stdin-close]     stop, killing every child, when stdin closes (the GUI's cancel)");
            Console.WriteLine("    Writes trigger_probability.asc, ensemble_burn_probability/arrival rasters, trigger_convergence.csv,");
            Console.WriteLine("    realizations.csv and the weather reports into the campaign folder.");
        }
    }
}
