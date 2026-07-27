using System.Globalization;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// Namelist groups/keys ELMFIRE's <c>elmfire.data</c> is patched with per realization —
    /// verified against <c>WildfireAV/pipeline/createElmfireInputFiles.py</c> (the real writer
    /// this pipeline is modeled on), <c>pipelineConfig.py</c>, and ELMFIRE's own
    /// <c>docs/archive/user_guide/io.rst</c>. Things earlier (unverified) guesses got wrong,
    /// now corrected:
    ///
    /// - A <c>&amp;COMPUTATIONAL_DOMAIN</c> group (<c>A_SRS</c>/<c>COMPUTATIONAL_DOMAIN_CELLSIZE</c>/
    ///   <c>_XLLCORNER</c>/<c>_YLLCORNER</c>) does exist and can override the domain explicitly, but
    ///   WildfireAV's real writer never sets it — ELMFIRE falls back to reading EPSG/cellsize/xll/yll
    ///   from <c>DEM_FILENAME</c>'s own georeferencing when it's absent, which is what this writer
    ///   relies on too.
    /// - Ignition is <c>NUM_IGNITIONS</c> + indexed <c>X_IGN(1)</c>/<c>Y_IGN(1)</c>/<c>T_IGN(1)</c>
    ///   in <c>&amp;SIMULATOR</c>, not scalar <c>X_IGNITION</c>/<c>Y_IGNITION</c>.
    /// - <c>WS_FILENAME</c> is always in **mph**, regardless of height — <c>WS_AT_10M</c> only tells
    ///   ELMFIRE the raster is 10 m wind instead of the default 20 ft, it does not change the unit.
    ///   A caller supplying m/s (this pipeline's convention, matching Open-Meteo's `wind_speed_10m`)
    ///   must convert; see <see cref="ElmfireRealizationWriter.Write"/>.
    ///
    /// Filenames in <c>&amp;INPUTS</c> are stems only (no directory, no extension) — the
    /// directory comes from <c>FUELS_AND_TOPOGRAPHY_DIRECTORY</c>/<c>WEATHER_DIRECTORY</c> and
    /// ELMFIRE appends its own extension.
    /// </summary>
    public static class ElmfireNamelistKeys
    {
        public const string InputsGroup = "INPUTS";
        public const string OutputsGroup = "OUTPUTS";
        public const string TimeControlGroup = "TIME_CONTROL";
        public const string SimulatorGroup = "SIMULATOR";
        public const string MonteCarloGroup = "MONTE_CARLO";
        public const string MiscellaneousGroup = "MISCELLANEOUS";

        // &INPUTS
        public const string FuelsAndTopographyDirectory = "FUELS_AND_TOPOGRAPHY_DIRECTORY";
        public const string DemFilename = "DEM_FILENAME";
        public const string SlpFilename = "SLP_FILENAME";
        public const string AspFilename = "ASP_FILENAME";
        public const string FbfmFilename = "FBFM_FILENAME";
        public const string CcFilename = "CC_FILENAME";
        public const string ChFilename = "CH_FILENAME";
        public const string CbhFilename = "CBH_FILENAME";
        public const string CbdFilename = "CBD_FILENAME";
        public const string AdjFilename = "ADJ_FILENAME";
        public const string PhiFilename = "PHI_FILENAME";
        public const string DtMeteorology = "DT_METEOROLOGY";
        public const string WeatherDirectory = "WEATHER_DIRECTORY";
        public const string WsFilename = "WS_FILENAME";
        public const string WdFilename = "WD_FILENAME";
        public const string M1Filename = "M1_FILENAME";
        public const string M10Filename = "M10_FILENAME";
        public const string M100Filename = "M100_FILENAME";
        public const string LhMoistureContent = "LH_MOISTURE_CONTENT";
        public const string LwMoistureContent = "LW_MOISTURE_CONTENT";
        public const string UseBarriers = "USE_BARRIERS";
        public const string WsAt10m = "WS_AT_10M";
        public const string BarrierFilename = "BARRIER_FILENAME";

        // &OUTPUTS (DUMP_* keys confirmed in ELMFIRE's docs/archive/user_guide/io.rst; WildfireAV's
        // own validation use case only turns on DUMP_TIME_OF_ARRIVAL, but our k-PERIL/AscImport
        // pipeline also needs fireline intensity and spread rate)
        public const string OutputsDirectory = "OUTPUTS_DIRECTORY";
        public const string Dtdump = "DTDUMP";
        public const string DumpTimeOfArrival = "DUMP_TIME_OF_ARRIVAL";
        public const string DumpFlin = "DUMP_FLIN";
        public const string DumpSpreadRate = "DUMP_SPREAD_RATE";
        public const string DumpSurfaceFire = "DUMP_SURFACE_FIRE";
        public const string ConvertToGeotiff = "CONVERT_TO_GEOTIFF";

        // &TIME_CONTROL
        public const string SimulationDt = "SIMULATION_DT";
        public const string TargetCfl = "TARGET_CFL";
        public const string SimulationTstop = "SIMULATION_TSTOP";
        public const string CurrentYear = "CURRENT_YEAR";
        public const string HourOfYear = "HOUR_OF_YEAR";

        // &SIMULATOR
        public const string NumIgnitions = "NUM_IGNITIONS";
        public const string XIgn1 = "X_IGN(1)";
        public const string YIgn1 = "Y_IGN(1)";
        public const string TIgn1 = "T_IGN(1)";
        public const string DebugLevel = "DEBUG_LEVEL";
        public const string CleanScratch = "CLEAN_SCRATCH";

        // &MONTE_CARLO
        public const string NumMeteorologyTimes = "NUM_METEOROLOGY_TIMES";
        /// <summary>Seeds ELMFIRE's own Monte Carlo draws — random ignition placement and the
        /// RASTER_TO_PERTURB weather/moisture perturbations. Varying it per realization is what
        /// makes an ensemble out of a single template.</summary>
        public const string Seed = "SEED";
        public const string NumEnsembleMembers = "NUM_ENSEMBLE_MEMBERS";

        // &MISCELLANEOUS
        public const string PathToGdal = "PATH_TO_GDAL";
        public const string Scratch = "SCRATCH";
    }

    /// <summary>
    /// One realization's sampled inputs, ready to be patched into a base elmfire.data template.
    /// Static per-case rasters (DEM/slope/aspect/fuel/canopy/adj/phi — the "master grid" and its
    /// LANDFIRE-equivalent layers) are assumed already prepared in
    /// <see cref="FuelsAndTopographyDirectory"/>; this writer only owns the per-realization
    /// weather/moisture/ignition/timing values.
    /// </summary>
    public class ElmfireRealization
    {
        public string RunId;
        public MasterGrid Grid;

        /// <summary>Directory (relative to the case folder) holding the static DEM/slope/aspect/
        /// fuel/canopy/adj/phi rasters — WildfireAV's "inputs/" folder.</summary>
        public string FuelsAndTopographyDirectory = "./inputs";

        /// <summary>Directory (relative to the case folder) this realization's weather rasters are written to.</summary>
        public string WeatherDirectory = "./inputs";

        public string OutputsDirectory = "./outputs";
        public string ScratchDirectory = "./scratch";
        public string PathToGdal;

        /// <summary>Ignition location in the master grid's own coordinate units (matches DEM_FILENAME's CRS).</summary>
        public double IgnitionX, IgnitionY;

        /// <summary>Seconds between weather timesteps (WildfireAV default: 3600 = hourly).</summary>
        public double DtMeteorologySeconds = 3600.0;

        public double WindSpeedMps;
        public double WindDirDeg;
        public double M1Percent, M10Percent, M100Percent;

        /// <summary>Live herbaceous moisture, % of dry mass (WildfireAV default: 60).</summary>
        public double LiveHerbaceousMoisturePercent = 60.0;

        /// <summary>Live woody moisture, % of dry mass (WildfireAV default: 90).</summary>
        public double LiveWoodyMoisturePercent = 90.0;

        /// <summary>Simulation length in seconds (SIMULATION_TSTOP).</summary>
        public double SimulationStopSeconds;

        public double SimulationDtSeconds = 30.0;
        public double TargetCfl = 0.2;
        public double DtdumpSeconds = 7200.0;

        public int CurrentYear;
        public int HourOfYear;

        /// <summary>Relative path (from the case folder) to a road/water barrier raster; null to
        /// leave USE_BARRIERS/BARRIER_FILENAME/WS_AT_10M untouched (no barrier data prepared —
        /// WildfireAV always generates one from OSM roads/waterways, which this pipeline does
        /// not yet do for arbitrary global domains).</summary>
        public string BarrierFilenameStem;
    }

    /// <summary>
    /// Patches a base <c>elmfire.data</c> template with one realization's sampled inputs,
    /// following the same "clone template, patch known keys, write out" convention
    /// <see cref="ProbabilisticTrigger"/> already uses for <c>.wui</c> files. Wind/moisture are
    /// written as constant-value multi-band GeoTIFFs via <see cref="GeoTiffRasterWriter"/> — one
    /// band per <see cref="ElmfireRealization.DtMeteorologySeconds"/> step, all bands holding the
    /// same Monte Carlo-sampled value (docs/probabilistic-trigger-convergence.md's "constant
    /// transient rasters" fallback) — rather than a real time-varying series, until the
    /// WindNinja/Nelson steps produce one.
    /// </summary>
    public static class ElmfireRealizationWriter
    {
        /// <summary>ELMFIRE's WS_FILENAME is always mph, regardless of WS_AT_10M (io.rst).</summary>
        private const double MpsToMph = 2.2369362920544;

        public static string[] Write(string[] baseTemplateLines, ElmfireRealization r, string rasterOutputDir, int numMeteorologyTimes = 1)
        {
            Directory.CreateDirectory(rasterOutputDir);

            string wsStem = $"ws_{r.RunId}";
            string wdStem = $"wd_{r.RunId}";
            string m1Stem = $"m1_{r.RunId}";
            string m10Stem = $"m10_{r.RunId}";
            string m100Stem = $"m100_{r.RunId}";

            float windSpeedMph = (float)(r.WindSpeedMps * MpsToMph);
            GeoTiffRasterWriter.WriteConstantTimeSeries(r.Grid, windSpeedMph, numMeteorologyTimes, Path.Combine(rasterOutputDir, wsStem + ".tif"));
            GeoTiffRasterWriter.WriteConstantTimeSeries(r.Grid, (float)r.WindDirDeg, numMeteorologyTimes, Path.Combine(rasterOutputDir, wdStem + ".tif"));
            GeoTiffRasterWriter.WriteConstantTimeSeries(r.Grid, (float)r.M1Percent, numMeteorologyTimes, Path.Combine(rasterOutputDir, m1Stem + ".tif"));
            GeoTiffRasterWriter.WriteConstantTimeSeries(r.Grid, (float)r.M10Percent, numMeteorologyTimes, Path.Combine(rasterOutputDir, m10Stem + ".tif"));
            GeoTiffRasterWriter.WriteConstantTimeSeries(r.Grid, (float)r.M100Percent, numMeteorologyTimes, Path.Combine(rasterOutputDir, m100Stem + ".tif"));

            string[] lines = (string[])baseTemplateLines.Clone();
            var k = new PatchHelper(lines);

            // &INPUTS
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.FuelsAndTopographyDirectory, r.FuelsAndTopographyDirectory, quoted: true);
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.DtMeteorology, D(r.DtMeteorologySeconds));
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WeatherDirectory, r.WeatherDirectory, quoted: true);
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WsFilename, wsStem, quoted: true);
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WdFilename, wdStem, quoted: true);
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.M1Filename, m1Stem, quoted: true);
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.M10Filename, m10Stem, quoted: true);
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.M100Filename, m100Stem, quoted: true);
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.LhMoistureContent, D(r.LiveHerbaceousMoisturePercent));
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.LwMoistureContent, D(r.LiveWoodyMoisturePercent));
            // our weather (Open-Meteo wind_speed_10m) is measured at 10 m, not ELMFIRE's 20 ft default
            k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WsAt10m, ".TRUE.");
            if (r.BarrierFilenameStem != null)
            {
                k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.UseBarriers, ".TRUE.");
                k.Set(ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.BarrierFilename, r.BarrierFilenameStem, quoted: true);
            }

            // &OUTPUTS
            k.Set(ElmfireNamelistKeys.OutputsGroup, ElmfireNamelistKeys.OutputsDirectory, r.OutputsDirectory, quoted: true);
            k.Set(ElmfireNamelistKeys.OutputsGroup, ElmfireNamelistKeys.Dtdump, D(r.DtdumpSeconds));
            k.Set(ElmfireNamelistKeys.OutputsGroup, ElmfireNamelistKeys.DumpTimeOfArrival, ".TRUE.");
            k.Set(ElmfireNamelistKeys.OutputsGroup, ElmfireNamelistKeys.DumpFlin, ".TRUE.");
            k.Set(ElmfireNamelistKeys.OutputsGroup, ElmfireNamelistKeys.DumpSpreadRate, ".TRUE.");
            k.Set(ElmfireNamelistKeys.OutputsGroup, ElmfireNamelistKeys.DumpSurfaceFire, ".TRUE.");
            k.Set(ElmfireNamelistKeys.OutputsGroup, ElmfireNamelistKeys.ConvertToGeotiff, ".TRUE.");

            // &TIME_CONTROL
            k.Set(ElmfireNamelistKeys.TimeControlGroup, ElmfireNamelistKeys.SimulationDt, D(r.SimulationDtSeconds));
            k.Set(ElmfireNamelistKeys.TimeControlGroup, ElmfireNamelistKeys.TargetCfl, D(r.TargetCfl));
            if (r.SimulationStopSeconds > 0)
            {
                k.Set(ElmfireNamelistKeys.TimeControlGroup, ElmfireNamelistKeys.SimulationTstop, D(r.SimulationStopSeconds));
            }
            k.Set(ElmfireNamelistKeys.TimeControlGroup, ElmfireNamelistKeys.CurrentYear, r.CurrentYear.ToString(CultureInfo.InvariantCulture));
            k.Set(ElmfireNamelistKeys.TimeControlGroup, ElmfireNamelistKeys.HourOfYear, r.HourOfYear.ToString(CultureInfo.InvariantCulture));

            // &SIMULATOR
            k.Set(ElmfireNamelistKeys.SimulatorGroup, ElmfireNamelistKeys.NumIgnitions, "1");
            k.Set(ElmfireNamelistKeys.SimulatorGroup, ElmfireNamelistKeys.XIgn1, D(r.IgnitionX));
            k.Set(ElmfireNamelistKeys.SimulatorGroup, ElmfireNamelistKeys.YIgn1, D(r.IgnitionY));
            k.Set(ElmfireNamelistKeys.SimulatorGroup, ElmfireNamelistKeys.TIgn1, "0.00");
            k.Set(ElmfireNamelistKeys.SimulatorGroup, ElmfireNamelistKeys.DebugLevel, "0");
            k.Set(ElmfireNamelistKeys.SimulatorGroup, ElmfireNamelistKeys.CleanScratch, ".TRUE.");

            // &MONTE_CARLO
            k.Set(ElmfireNamelistKeys.MonteCarloGroup, ElmfireNamelistKeys.NumMeteorologyTimes, numMeteorologyTimes.ToString(CultureInfo.InvariantCulture));

            // &MISCELLANEOUS
            if (r.PathToGdal != null)
            {
                k.Set(ElmfireNamelistKeys.MiscellaneousGroup, ElmfireNamelistKeys.PathToGdal, r.PathToGdal, quoted: true);
            }
            k.Set(ElmfireNamelistKeys.MiscellaneousGroup, ElmfireNamelistKeys.Scratch, r.ScratchDirectory, quoted: true);

            return k.Lines;
        }

        private static string D(double v) => v.ToString(CultureInfo.InvariantCulture);

        /// <summary>Threads the growing line array through repeated SetKeyInGroup calls without a wall of reassignments.</summary>
        private class PatchHelper
        {
            public string[] Lines;
            public PatchHelper(string[] lines) { Lines = lines; }
            public void Set(string group, string key, string value, bool quoted = false)
            {
                Lines = ElmfireNamelist.SetKeyInGroup(Lines, group, key, value, quoted);
            }
        }
    }
}
