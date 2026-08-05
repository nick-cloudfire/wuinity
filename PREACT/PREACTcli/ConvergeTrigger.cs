using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PREACT.Utility;

namespace PREACTcli
{
    /// <summary>
    /// Convergence-driven probabilistic trigger-boundary driver (docs/probabilistic-trigger-convergence.md).
    ///
    /// Runs realizations (same per-realization mechanics as <see cref="ProbabilisticTrigger"/>:
    /// evacuation + k-PERIL boundary via PREACT.exe) and aggregates them into a running per-cell
    /// probability raster. Instead of a fixed count it stops once the probability raster's
    /// decile-area footprint has stabilized: for a streak of consecutive realizations, every
    /// decile's area changed by less than a tolerance from the previous realization. --max bounds
    /// how many realizations may be consumed.
    ///
    /// The fire rasters behind each realization come from one of two sources. By default they are
    /// read from a pre-generated set on disk (--dir with indexed filename patterns). With
    /// --elmfire they are generated on demand instead: per realization the template namelist is
    /// re-seeded and ELMFIRE is run in its own directory, so the ensemble no longer has to exist
    /// up front (see <see cref="TryGenerateRasters"/>).
    ///
    /// Up to <c>--parallel</c> realizations run concurrently, each as its own PREACT.exe OS
    /// process. This is deliberate, not incidental: WUInity's evacuation step runs on SUMO via
    /// libsumo, a native library with process-global state that cannot safely run more than one
    /// simulation per process (see <c>Engine.RunSimulationsParallel</c>'s own code comment on
    /// this) — so genuine concurrency across realizations only works at the OS-process level,
    /// which is exactly what <see cref="RealizationRunner"/> already does per realization. Since
    /// SUMO dominates a realization's wall-clock time (an ELMFIRE run is comparatively quick),
    /// this is where parallelism actually pays off. Aggregation itself stays single-threaded and
    /// order-independent — realizations are i.i.d. Monte Carlo draws, so folding them into the
    /// running probability/streak state in completion order (not launch order) is statistically
    /// equivalent to the strictly-serial version.
    /// </summary>
    internal static class ConvergeTrigger
    {
        private static readonly double[] Deciles = { 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1.0 };

        public static int Run(string[] args)
        {
            var opts = ParseArgs(args);

            //Same treatment as build-case: the tools are located when --gdal was not given, so a campaign of
            //hundreds of realizations does not fail on every one of them for want of a path that could have
            //been worked out. ELMFIRE still resolves it itself from the PATH this puts them on.
            opts.PathToGdal ??= GdalTools.FindBinDirectory();

            //Same for the executable, and for the same reason: a run resolves the vendored build by itself, so
            //nobody ever has a path to hand and being asked for one is a surprise.
            //
            //Guarded on the other two ELMFIRE options rather than done unconditionally, because
            //GenerateRealizations is *inferred* from these being set - filling the executable in on every
            //invocation would silently turn --dir mode into generate mode and ignore the ensemble it was
            //pointed at.
            if (opts.ElmfireExe == null && (opts.ElmfireTemplate != null || opts.ElmfireInputs != null))
            {
                opts.ElmfireExe = ElmfireCoupling.ResolveExecutable(null, null);
                if (opts.ElmfireExe != null)
                {
                    Console.WriteLine($"elmfire.exe: {opts.ElmfireExe}");
                }
            }

            if (opts.GenerateRealizations && opts.PathToGdal != null)
            {
                Console.WriteLine($"GDAL tools: {opts.PathToGdal}");
            }

            if (opts.BaseWui == null || opts.MaxRealizations <= 0)
            {
                Console.Error.WriteLine("ERROR: --wui and --max are required.");
                PrintUsage();
                return 1;
            }
            if (!opts.GenerateRealizations && opts.RasterDir == null)
            {
                Console.Error.WriteLine("ERROR: --dir is required unless generating realizations with --elmfire.");
                PrintUsage();
                return 1;
            }
            if (!File.Exists(opts.BaseWui))
            {
                Console.Error.WriteLine("ERROR: base .wui not found: " + opts.BaseWui);
                return 1;
            }
            if (opts.RasterDir != null && !Directory.Exists(opts.RasterDir))
            {
                Console.Error.WriteLine("ERROR: raster folder not found: " + opts.RasterDir);
                return 1;
            }
            if (opts.GenerateRealizations && !opts.ResumeOnly)
            {
                if (opts.ElmfireExe == null || !File.Exists(opts.ElmfireExe))
                {
                    Console.Error.WriteLine("ERROR: --elmfire must point at the elmfire executable.");
                    return 1;
                }
                if (opts.ElmfireTemplate == null || !File.Exists(opts.ElmfireTemplate))
                {
                    Console.Error.WriteLine("ERROR: --elmfire-template must point at a base elmfire.data namelist.");
                    return 1;
                }
                if (opts.ElmfireInputs == null || !Directory.Exists(opts.ElmfireInputs))
                {
                    Console.Error.WriteLine("ERROR: --elmfire-inputs must point at the shared fuels/topography/weather raster folder.");
                    return 1;
                }
                // ELMFIRE resolves these against each realization's own run directory, so a
                // relative path would silently point somewhere else per realization.
                opts.ElmfireInputs = Path.GetFullPath(opts.ElmfireInputs).TrimEnd(Path.DirectorySeparatorChar);
                opts.ElmfireExe = Path.GetFullPath(opts.ElmfireExe);
                opts.ElmfireTemplate = Path.GetFullPath(opts.ElmfireTemplate);

                //Taken from the template rather than assumed, because the band count written into every
                //realization is derived from it: assuming 3600 against a template that says otherwise would
                //stretch or compress the whole series in time without anything saying so.
                opts.SecondsPerBand = ReadDtMeteorology(opts.ElmfireTemplate, opts.SecondsPerBand);

                //Before the first realization rather than on it: every realization draws its ignition from this
                //mask, so a campaign without a usable one has nothing to vary and would fail identically
                //hundreds of times over.
                if (!SetUpRandomIgnition(opts)) return 1;

                opts.MiscInputsDirectory = ResolveMiscellaneousInputs(opts);

                //The case folder is worked out again here rather than moved up: this block validates the ELMFIRE
                //half before the driver's own paths are established, and reordering it would move the checks.
                if (opts.SingleBandWeather
                    && !SetUpSingleBandWeather(opts, Path.GetDirectoryName(Path.GetFullPath(opts.BaseWui))))
                {
                    return 1;
                }

                if (opts.WindToWui && !SetUpWindToWui(opts)) return 1;

                if (opts.RealizationWeather && !SetUpRealizationWeather(opts)) return 1;
            }
            if (opts.Streak <= 0 || opts.Tolerance <= 0)
            {
                Console.Error.WriteLine("ERROR: --streak and --tolerance must be positive.");
                return 1;
            }
            if (opts.Parallelism <= 0)
            {
                Console.Error.WriteLine("ERROR: --parallel must be positive.");
                return 1;
            }

            string caseDir = Path.GetDirectoryName(Path.GetFullPath(opts.BaseWui));
            string outputDir = Path.Combine(caseDir, "_output");
            Directory.CreateDirectory(outputDir);

            string preactExe = opts.PreactExe ?? RealizationRunner.FindPreactExe();
            if (!opts.ResumeOnly && (preactExe == null || !File.Exists(preactExe)))
            {
                Console.Error.WriteLine("ERROR: could not locate PREACT.exe; pass --preact <path>. (Use --resume-only to aggregate already-computed boundaries without running.)");
                return 1;
            }

            string baseName = Path.GetFileNameWithoutExtension(opts.BaseWui);
            string[] baseLines = File.ReadAllLines(opts.BaseWui);

            if (!CheckWuiAreaSourceIsAggregatable(baseLines)) return 1;

            string diagnosticsPath = opts.DiagnosticsPath ?? Path.Combine(outputDir, "trigger_convergence.csv");
            string livePath = Path.Combine(outputDir, "trigger_probability_live.asc");

            //Before anything is written or read back, so this campaign's results cannot be mixed with the last
            //one's. The boundaries stay when resuming - reusing them is what resuming means.
            CampaignReset.ArchivePrevious(outputDir, caseDir, includeBoundaries: !opts.Resume,
                new[] { opts.OutPath, opts.DiagnosticsPath });

            //After the reset, so it belongs to this campaign rather than being archived as the last one's.
            if (opts.WeatherStatistics != null)
            {
                try
                {
                    string path = WeatherStatisticsReport.Write(
                        Path.Combine(outputDir, "weather_distributions.csv"),
                        opts.WeatherStatistics, opts.WeatherStatisticsHeader);
                    if (path != null) Console.WriteLine("Weather distributions written to " + path);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("WARNING: could not write the weather distribution report: " + e.Message);
                }
            }

            return RunAsync(opts, caseDir, outputDir, preactExe, baseName, baseLines, diagnosticsPath, livePath).GetAwaiter().GetResult();
        }

        private class RealizationOutcome
        {
            public bool Ok;
            public string Idx;
            public float[,] Boundary;
            public AscRaster.Header Header;

            /// <summary>
            /// This realization's time-of-arrival raster, so the fire itself can be aggregated and not only the
            /// boundary derived from it. Empty in --resume-only, which never touches the source rasters.
            /// </summary>
            public string ToaPath;
        }

        /// <summary>
        /// Produces realization <paramref name="index"/>'s fire rasters by running ELMFIRE, rather
        /// than reading a pre-generated set from --dir.
        ///
        /// The ensemble comes from ELMFIRE's own Monte Carlo machinery, not from a second sampler
        /// on this side: the template keeps whatever RASTER_TO_PERTURB configuration it was written
        /// with, and SEED is what changes per realization. That keeps the physics in one place and
        /// means a template tuned by hand behaves identically here.
        ///
        /// Ignition is the exception, and it is not optional: <see cref="ApplyRandomIgnition"/>
        /// switches the template's fixed ignition points off and has each realization draw its
        /// ignition from the ignition mask instead. Otherwise the seed varies nothing that matters
        /// and every realization runs the same fire.
        ///
        /// NUM_ENSEMBLE_MEMBERS is pinned to 1 because this driver's unit of parallelism is the
        /// realization — one ELMFIRE process per member, each with its own scratch and outputs.
        /// </summary>
        /// <summary>
        /// Resolves the ignition mask every realization will be ignited out of, and says how much of the
        /// domain it offers.
        /// </summary>
        /// <remarks>
        /// A trigger boundary is a statement about where a fire could start, so the ignition has to move: with
        /// one fixed ignition point every realization runs the same fire, the probability raster is that one
        /// fire's boundary at probability 1, and no amount of realizations makes it a distribution. A fixed
        /// <c>[IgnitionPoint]</c> is for running a single named fire through WUInity — which is why the
        /// scenario keeps it and <c>build-case</c> writes it into the template — and this driver overrides it
        /// per realization rather than asking the user to maintain two templates.
        ///
        /// Fatal when the mask is missing or empty. ELMFIRE would refuse each realization anyway
        /// ("[ERROR] No ignitable pixels found in the ignition mask"), and a campaign that consumes --max
        /// realizations to discover that has wasted the run.
        /// </remarks>
        private static bool SetUpRandomIgnition(Options opts)
        {
            string[] template;
            try
            {
                template = File.ReadAllLines(opts.ElmfireTemplate);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: could not read " + opts.ElmfireTemplate + ": " + e.Message);
                return false;
            }

            //The template's own stem when it names one, so a case whose mask is called something else is still
            //found; the name build-case writes otherwise.
            opts.IgnitionMaskStem = ElmfireNamelist.GetKeyInGroup(template, ElmfireNamelistKeys.InputsGroup,
                                        ElmfireNamelistKeys.IgnitionMaskFilename);
            if (string.IsNullOrEmpty(opts.IgnitionMaskStem)) opts.IgnitionMaskStem = "ignition_mask";

            string maskPath = Path.Combine(opts.ElmfireInputs, opts.IgnitionMaskStem + ".tif");
            if (!File.Exists(maskPath))
            {
                Console.Error.WriteLine("ERROR: every realization's ignition is drawn from the ignition mask, and "
                    + maskPath + " is not there.");
                Console.Error.WriteLine("       Build the case with build-case (it always writes one - a painted "
                    + "ignition area if the scenario has one, otherwise an all-ones mask restricted to burnable "
                    + "fuel), or set [ELMFIRE] IgnitionMaskFile in the scenario.");
                return false;
            }

            //Counted rather than assumed: the mask build-case writes has already been cut back to burnable fuel,
            //and on a coastal or urban domain that can remove most of it. How much is left is the size of the
            //population the campaign samples, so it belongs in the log beside the realization count.
            int ignitable = -1;
            try
            {
                float[,] mask = AscRaster.ReadGeoTiff(maskPath, out AscRaster.Header _, out bool ok);
                if (ok && mask != null)
                {
                    ignitable = 0;
                    for (int x = 0; x < mask.GetLength(0); ++x)
                    {
                        for (int y = 0; y < mask.GetLength(1); ++y)
                        {
                            if (mask[x, y] > 0f) ++ignitable;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                //Not fatal: the count is for the log, and ELMFIRE reads the mask itself.
                Console.Error.WriteLine("WARNING: could not count the ignition mask's cells (" + e.Message + ").");
            }

            if (ignitable == 0)
            {
                Console.Error.WriteLine("ERROR: " + maskPath + " has no cell with a positive weight, so there is "
                    + "nowhere for a realization to ignite. ELMFIRE refuses this with \"No ignitable pixels found "
                    + "in the ignition mask\".");
                return false;
            }

            //"positive weight" rather than "ignitable": ELMFIRE narrows this further by the edge buffer and the
            //fuel model, and claiming its number here would be a guess at its arithmetic.
            Console.WriteLine("Ignition: drawn per realization from " + opts.IgnitionMaskStem + ".tif"
                + (ignitable > 0 ? $" ({ignitable} cells with a positive weight)" : "")
                + ". Any fixed ignition point in the template is switched off - it belongs to a single run, not "
                + "to a campaign.");
            return true;
        }

        /// <summary>
        /// Finds the community the wind will be aimed at, and checks that aiming it is possible at all.
        /// </summary>
        /// <remarks>
        /// The target is the centroid of the case's <c>wui_area.tif</c> — the painted WUI area, on the fire's
        /// own grid. Not the domain centre, which is what ELMFIRE's own <c>POINT_WIND_TO_CENTER</c> uses
        /// (<c>XCEN = ASP%NCOLS / 2</c>) and which is a different place: on the Mati domain the two are 3.4 km
        /// apart, enough to aim a distant fire ~19 degrees off the town it is supposed to threaten. That is why
        /// this is done here rather than by setting ELMFIRE's flag.
        ///
        /// Requires per-realization weather, because the bearing is a property of the realization's ignition
        /// and the wind rasters are what carry it. With one shared series there is one wind field for every
        /// realization and no bearing to give it.
        /// </remarks>
        private static bool SetUpWindToWui(Options opts)
        {
            if (!opts.RealizationWeather)
            {
                Console.Error.WriteLine("ERROR: --wind-to-wui aims each realization's wind from its own ignition "
                    + "at the WUI area, which means the wind rasters have to be written per realization. It "
                    + "cannot be combined with --shared-weather, whose one series is shared by all of them.");
                return false;
            }

            string wuiArea = Path.Combine(opts.ElmfireInputs, "wui_area.tif");
            if (!MaskIgnitionSampler.TryGetMaskCentroid(wuiArea, out double x, out double y, out int cells))
            {
                Console.Error.WriteLine("ERROR: --wind-to-wui needs the case's wui_area.tif to aim at, and "
                    + wuiArea + (File.Exists(wuiArea) ? " has no marked cell." : " is not there."));
                Console.Error.WriteLine("       Paint a WUI area in the scenario and build the case again, or "
                    + "pass --no-wind-to-wui to let the weather's own wind direction stand.");
                return false;
            }

            opts.WuiCentreX = x;
            opts.WuiCentreY = y;

            //Taken from the template rather than assumed, because the sampler on this side has to exclude the
            //same border ELMFIRE does - a point inside it is one ELMFIRE would never have drawn.
            try
            {
                string buffer = ElmfireNamelist.GetKeyInGroup(File.ReadAllLines(opts.ElmfireTemplate),
                                    ElmfireNamelistKeys.MonteCarloGroup, "EDGEBUFFER");
                if (!string.IsNullOrEmpty(buffer)
                    && double.TryParse(buffer, NumberStyles.Any, CultureInfo.InvariantCulture, out double metres)
                    && metres >= 0.0)
                {
                    opts.EdgeBufferMetres = metres;
                }
            }
            catch { }

            //The domain centre is reported beside it because that is what ELMFIRE's own flag would have used,
            //and the difference is the reason this exists.
            string message = $"Wind: aimed from each realization's ignition at the WUI area centroid "
                             + $"({x:F0}, {y:F0}), from {cells} painted cell(s).";
            if (opts.WeatherGrid != null)
            {
                AscRaster.Header h = opts.WeatherGrid.Header;
                double cx = h.XllCorner + 0.5 * h.Ncols * h.CellSize;
                double cy = h.YllCorner + 0.5 * h.Nrows * h.CellSize;
                double offset = System.Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                message += $" ELMFIRE's own POINT_WIND_TO_CENTER would have used the domain centre, "
                           + $"{offset / 1000.0:F1} km away.";
            }
            Console.WriteLine(message);
            return true;
        }

        /// <summary>
        /// This realization's ignition, drawn from the mask here rather than by ELMFIRE, and the direction the
        /// wind must come from to carry it at the community. Null when the draw failed.
        /// </summary>
        /// <remarks>
        /// The seed is the realization's own, so the draw is reproducible and independent per realization, and
        /// is offset from the weather seed so that ignition and weather are not drawn from the same stream.
        /// </remarks>
        private static (double X, double Y, double WindFromDeg)? DrawIgnitionAndBearing(Options opts, string idx,
            int index)
        {
            string mask = Path.Combine(opts.ElmfireInputs, opts.IgnitionMaskStem + ".tif");
            string fuel = ResolveFuelRaster(opts.ElmfireInputs);

            MaskIgnitionSampler.Result draw = MaskIgnitionSampler.Sample(mask, fuel, opts.EdgeBufferMetres,
                                                 unchecked(opts.Seed + 1_000_000 + index));
            if (!draw.Ok)
            {
                Console.Error.WriteLine($"[{idx}] could not draw an ignition: {draw.Message}");
                return null;
            }

            double windFrom = MaskIgnitionSampler.WindDirectionFromBearing(draw.X, draw.Y,
                                  opts.WuiCentreX, opts.WuiCentreY);

            double distance = System.Math.Sqrt((opts.WuiCentreX - draw.X) * (opts.WuiCentreX - draw.X)
                                               + (opts.WuiCentreY - draw.Y) * (opts.WuiCentreY - draw.Y));

            Console.WriteLine($"[{idx}] ignition {draw.X:F0}, {draw.Y:F0} ({draw.Candidates} candidate cells), "
                + $"{distance / 1000.0:F1} km from the WUI area; wind set to blow from {windFrom:F0} deg.");

            return (draw.X, draw.Y, windFrom);
        }

        /// <summary>The case's fuel model raster, whichever of the two conventions it uses, or null.</summary>
        private static string ResolveFuelRaster(string inputs)
        {
            foreach (string stem in new[] { "fbfm40", "fbfm13", "fbfm" })
            {
                string path = Path.Combine(inputs, stem + ".tif");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>
        /// Writes a one-band copy of the case's five weather rasters and points the campaign at it, so a run
        /// longer than the weather series is legal instead of refused.
        /// </summary>
        /// <remarks>
        /// ELMFIRE's own check exempts a single-band series:
        /// <c>IF (WS%NBANDS * DT_METEOROLOGY .LT. SIMULATION_TSTOP .AND. WS%NBANDS .GT. 1)</c>. One band means
        /// "this is the weather", and it is held for however long the fire burns. So a 300-hour campaign against
        /// a 72-band case has this way out, and it needs no WindNinja and no rebuild — 72 bands would otherwise
        /// have to become 300, one solve each.
        ///
        /// What it costs is stated rather than hidden: the fire sees one hour's weather for the whole run, and
        /// the diurnal cycle Nelson put in the moisture rasters is gone with it. A three-day fire under a fixed
        /// afternoon wind is not the same fire as one that calms overnight. That is a modelling choice, which is
        /// why this is a flag and not a fallback the driver reaches for on its own.
        ///
        /// The band kept is the one the run would have started on — the template's
        /// <c>METEOROLOGY_BAND_START</c> — and the namelist's band range is then rewritten to 1, since the copy
        /// has only that one.
        ///
        /// Prepared once for the whole campaign, into a folder beside the realizations. Only the five weather
        /// stems are copied; terrain and fuels keep coming from the shared inputs.
        /// </remarks>
        private static bool SetUpSingleBandWeather(Options opts, string caseDir)
        {
            //With per-realization weather the two compose rather than conflict: the realization still draws
            //its own historical day, and "single band" means that day gets one band instead of one per hour of
            //the run. Nothing to copy from the case here, so this returns before touching the shared rasters -
            //TryGenerateWeather caps the band count instead.
            if (opts.RealizationWeather)
            {
                Console.WriteLine("Weather: one band per realization, from that realization's own drawn day - "
                    + $"one WindNinja solve each rather than {opts.TstopSeconds / 3600.0:F0}, held for the "
                    + "whole fire.");
                return true;
            }

            string ws = Path.Combine(opts.ElmfireInputs, "ws.tif");
            if (!File.Exists(ws))
            {
                Console.Error.WriteLine("ERROR: --single-band-weather needs the case's weather rasters in "
                    + opts.ElmfireInputs + "; ws.tif is not there.");
                return false;
            }

            int band = 1;
            try
            {
                string start = ElmfireNamelist.GetKeyInGroup(File.ReadAllLines(opts.ElmfireTemplate),
                                   ElmfireNamelistKeys.MonteCarloGroup, ElmfireNamelistKeys.MeteorologyBandStart);
                if (!string.IsNullOrEmpty(start)
                    && int.TryParse(start, NumberStyles.Any, CultureInfo.InvariantCulture, out int parsed)
                    && parsed >= 1)
                {
                    band = parsed;
                }
            }
            catch { }

            string directory = Path.Combine(caseDir, "_elmfire", "weather_single_band");
            Directory.CreateDirectory(directory);

            MasterGrid grid;
            try
            {
                grid = MasterGrid.FromRasterFile(ws);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: could not read the weather grid from " + ws + ": " + e.Message);
                return false;
            }

            int discarded = 0;
            foreach (string stem in new[] { "ws", "wd", "m1", "m10", "m100" })
            {
                string source = Path.Combine(opts.ElmfireInputs, stem + ".tif");
                if (!File.Exists(source))
                {
                    Console.Error.WriteLine($"ERROR: --single-band-weather needs all five weather rasters; "
                        + $"{stem}.tif is missing from {opts.ElmfireInputs}.");
                    return false;
                }

                try
                {
                    float[,] data = AscRaster.ReadGeoTiff(source, band, out AscRaster.Header _, out bool ok,
                                        out int bandCount);
                    if (!ok || data == null)
                    {
                        Console.Error.WriteLine($"ERROR: could not read band {band} of {source}.");
                        return false;
                    }

                    if (band > bandCount)
                    {
                        Console.Error.WriteLine($"ERROR: {stem}.tif has {bandCount} band(s), so band {band} - "
                            + "the template's METEOROLOGY_BAND_START - does not exist.");
                        return false;
                    }

                    discarded = System.Math.Max(discarded, bandCount - 1);
                    GeoTiffRasterWriter.WriteBand(grid, data, Path.Combine(directory, stem + ".tif"));
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"ERROR: could not write the single-band {stem}.tif: " + e.Message);
                    return false;
                }
            }

            opts.WeatherDirectory = directory;
            Console.WriteLine($"Weather: band {band} only, written to {directory}. "
                + $"{discarded} later band(s) are not used, so every realization burns its whole "
                + $"{opts.TstopSeconds / 3600.0:F0} h under that one hour's wind and moisture.");
            return true;
        }

        /// <summary>
        /// Where the fuel model tables live, as an absolute path, or null when the template does not say.
        /// </summary>
        /// <remarks>
        /// <c>MISCELLANEOUS_INPUTS_DIRECTORY</c> holds <c>fuel_models.csv</c> and
        /// <c>building_fuel_models.csv</c>, and <c>build-case</c> writes it as <c>'./inputs'</c> — relative to
        /// the case root, where the namelist sits. A realization runs in its own directory instead, which has no
        /// <c>inputs</c> beside it, so the relative path resolved to nothing and every realization died with
        /// "Problem opening fuel model table file ./inputs\fuel_models.csv" — the same class of mistake as the
        /// ignition one above, and the reason it is fixed in the same place.
        ///
        /// Resolved against the template's own directory rather than pointed at <c>--elmfire-inputs</c>: the
        /// tables are not required to sit with the rasters, and resolving preserves whatever the template meant.
        /// An absolute path in the template is already right and is returned unchanged.
        /// </remarks>
        private static string ResolveMiscellaneousInputs(Options opts)
        {
            string value;
            try
            {
                value = ElmfireNamelist.GetKeyInGroup(File.ReadAllLines(opts.ElmfireTemplate),
                            ElmfireNamelistKeys.MiscellaneousGroup, ElmfireNamelistKeys.MiscellaneousInputsDirectory);
            }
            catch
            {
                return null;
            }

            //'null' is ELMFIRE's own way of spelling "not set", and must be left alone rather than turned into
            //a directory called null.
            if (string.IsNullOrEmpty(value) || value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string resolved = Path.IsPathRooted(value)
                ? value
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.ElmfireTemplate)), value));

            if (!Directory.Exists(resolved))
            {
                //Not fatal: ELMFIRE only reads the tables the run needs, and a case whose fuel models come from
                //its own defaults has nothing here. Said because the failure it causes names a relative path
                //that looks like it should have worked.
                Console.Error.WriteLine($"WARNING: the template's {ElmfireNamelistKeys.MiscellaneousInputsDirectory} "
                    + $"('{value}') resolves to {resolved}, which does not exist. A realization needing "
                    + "fuel_models.csv will fail there.");
            }

            return resolved.TrimEnd(Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// Resolves everything the per-realization weather chain needs once, at startup, rather
        /// than rediscovering it inside every realization: the master grid (read off the shared
        /// DEM), the domain centre the archive is queried at, and where the cached archive lives.
        /// Failing here is fatal — the alternative is a campaign that silently runs every
        /// realization on identical weather, which looks exactly like a working one.
        /// </summary>
        private static bool SetUpRealizationWeather(Options opts)
        {
            string dem = Path.Combine(opts.ElmfireInputs, "dem.tif");
            if (!File.Exists(dem))
            {
                Console.Error.WriteLine("ERROR: --realization-weather needs dem.tif in --elmfire-inputs (build the case with build-case).");
                return false;
            }

            try
            {
                opts.WeatherGrid = MasterGrid.FromRasterFile(dem);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: could not read the master grid from " + dem + ": " + e.Message);
                return false;
            }

            if (!BuildCase.TryReadCentreLatLon(opts.BaseWui, 0.0, out PREACT.Math.Vector2d centre))
            {
                Console.Error.WriteLine("ERROR: could not read the domain from " + opts.BaseWui + " for the weather query.");
                return false;
            }
            opts.Weather.LatLon = centre;

            if (string.IsNullOrEmpty(opts.Weather.ArchiveCsvPath))
            {
                //Defaults to the archive build-case already cached beside the namelist, so a
                //campaign against a built case reuses it instead of re-downloading decades of data.
                string caseRoot = Path.GetDirectoryName(opts.ElmfireTemplate);
                opts.Weather.ArchiveCsvPath = Path.Combine(caseRoot, "climatology",
                    Path.GetFileNameWithoutExtension(opts.BaseWui) + "_era5_hourly.csv");
            }
            opts.Weather.ArchiveCsvPath = Path.GetFullPath(opts.Weather.ArchiveCsvPath);
            opts.Weather.WindNinjaExe ??= BuildCase.FindWindNinja();
            opts.Weather.Seed = opts.Seed;

            //Fetched once here, serially, rather than by whichever realizations happen to start
            //first: several concurrent processes downloading and writing the same cache file is
            //the race that makes long campaigns fail.
            if (!File.Exists(opts.Weather.ArchiveCsvPath))
            {
                Console.WriteLine("Fetching the ERA5 climatology archive once for the whole campaign...");
            }

            try
            {
                var warm = new WeatherRasterPipeline.Options
                {
                    Grid = opts.WeatherGrid,
                    InputsDirectory = Path.Combine(Path.GetDirectoryName(opts.Weather.ArchiveCsvPath), "_warmup"),
                    TerrainDirectory = opts.ElmfireInputs,
                    LatLon = opts.Weather.LatLon,
                    ArchiveCsvPath = opts.Weather.ArchiveCsvPath,
                    ArchiveStartYear = opts.Weather.ArchiveStartYear,
                    ArchiveEndYear = opts.Weather.ArchiveEndYear,
                    //Only the archive matters here. Said with ArchiveOnly rather than by withholding the
                    //WindNinja path, which the chain simply looks up for itself - so this used to solve eight
                    //bands and march Nelson over 34 hours before the first realization started.
                    ArchiveOnly = true,
                    Log = Console.WriteLine,
                };
                Directory.CreateDirectory(warm.InputsDirectory);
                WeatherRasterPipeline.Run(warm).GetAwaiter().GetResult();
                try { Directory.Delete(warm.InputsDirectory, recursive: true); } catch { }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: could not prepare the climatology archive: " + e.Message);
                return false;
            }

            Console.WriteLine($"Per-realization weather: archive {opts.Weather.ArchiveCsvPath}, " +
                              (opts.Weather.WindNinjaExe != null ? "WindNinja " + Path.GetFileName(opts.Weather.WindNinjaExe) : "no WindNinja (uniform wind)"));

            //Done once here, for the whole campaign, and for two reasons. The live moisture march is a
            //seasonal state over the entire record, identical for every realization, so marching it per
            //realization would repeat ~19,000 native calls per fire for an answer that cannot differ - and
            //realizations run as concurrent tasks in one process, so it would repeat them in parallel. The
            //statistics report needs the same pool and the same march, and computing them together is what
            //stops the reported distributions drifting from the ones actually drawn from.
            if (opts.WeatherSampling == WeatherRasterPipeline.SamplingMode.FittedDistributions)
            {
                SetUpFittedWeather(opts);
            }

            //Said in solves rather than in flags, because that is the number that decides whether the campaign
            //finishes tonight. WindNinja is roughly 5 s a solve on a 15 km domain at fine mesh.
            int wanted = (int)System.Math.Ceiling(System.Math.Max(opts.TstopSeconds, 0.0)
                                                  / System.Math.Max(opts.SecondsPerBand, 1.0));

            bool fitted = opts.WeatherSampling == WeatherRasterPipeline.SamplingMode.FittedDistributions;
            int perRealization = (fitted || opts.SingleBandWeather) ? 1
                : (opts.MaxWeatherBands > 0 && wanted > opts.MaxWeatherBands ? 1 : wanted);

            if (fitted)
            {
                //Reported here and only here: the consequences of the mode are campaign-wide, and repeating
                //them per realization would bury them. The band count is not a budget in this mode - it is
                //one by construction, since the draw is a scalar held for the whole fire.
                Console.WriteLine($"         Weather is drawn from distributions fitted to the "
                    + $"{opts.CandidateDaysPerYear} worst fire-weather days of each year on record: one value "
                    + "per parameter per realization, one band, one WindNinja solve.");
                Console.WriteLine("         Nelson does not run in this mode - it needs real antecedent hours. "
                    + "Dead fuel moisture comes from the drawn temperature and humidity by Simard's "
                    + "equilibrium content, so it is uniform over the domain rather than varying with "
                    + "slope and aspect, and carries no drying history.");
                Console.WriteLine("         Temperature, humidity and wind speed are drawn independently, so a "
                    + "realization can be hot and humid at once in a way the record is not. Pass "
                    + "--historical-day-weather to replay whole days instead.");
            }
            else if (perRealization == 1 && wanted > 1)
            {
                Console.WriteLine($"         One band per realization ({wanted} would cover the run"
                    + (opts.SingleBandWeather ? ", --single-band-weather" : $", over --max-weather-bands {opts.MaxWeatherBands}")
                    + "), so its drawn day's wind is solved once and held for the whole fire.");
            }
            else
            {
                Console.WriteLine($"         {perRealization} band(s) per realization = {perRealization} "
                    + $"WindNinja solve(s) each, roughly {perRealization * 5.0 / 60.0:F0} min of the "
                    + $"realization's wall clock, and {perRealization + 1} bands held in memory "
                    + $"({(perRealization + 1) * 5L * 4L * opts.WeatherGrid.Header.Ncols * opts.WeatherGrid.Header.Nrows / (1024 * 1024)} "
                    + "MB) times --parallel.");
            }

            return true;
        }

        /// <summary>
        /// Marches the live fuel moisture model over the record once, and writes the campaign's weather
        /// distribution report. Both are properties of the case and the pool, not of any realization.
        /// </summary>
        /// <remarks>
        /// Neither failure is fatal. A live moisture march that cannot run leaves the case's own live
        /// moisture constants in place, and a report that cannot be written costs a diagnostic, not a
        /// result — but both say so, because a campaign that quietly stopped varying live fuel while the
        /// mode advertises it would be indistinguishable from one that is working.
        /// </remarks>
        private static void SetUpFittedWeather(Options opts)
        {
            List<HourlyWeatherRow> rows;
            try
            {
                rows = ClimatologySampler.ParseOpenMeteoCsv(opts.Weather.ArchiveCsvPath);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("WARNING: could not read the archive for the live moisture march "
                    + "or the statistics report: " + e.Message);
                return;
            }

            List<AnnualMaximaDay> pool = ClimatologySampler.BuildCandidatePool(rows, opts.CandidateDaysPerYear);
            if (pool.Count == 0)
            {
                Console.Error.WriteLine("WARNING: the archive holds no day with a non-zero FWI, so no "
                    + "distribution can be fitted; realizations will fall back to uniform weather.");
                return;
            }

            if (opts.Weather.FitLiveFuelMoisture)
            {
                try
                {
                    opts.Weather.LiveFuelMoisture.Latitude = opts.Weather.LatLon.x;
                    opts.Weather.LiveMoistureByDate = LiveFuelMoistureSampler.March(
                        rows, opts.Weather.LiveFuelMoisture, m => Console.WriteLine("        " + m.Trim()));
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("WARNING: the NFDRS4 live fuel moisture march failed (" + e.Message
                        + "). Every realization will keep the case's own LH/LW constants, so the ensemble "
                        + "carries no live fuel uncertainty.");
                }
            }

            //Computed now, while the archive is in hand, but written later: the campaign reset archives
            //everything already in _output, so a report written here would be filed away as the previous
            //run's before this one had started. The statistics are a couple of dozen structs, so carrying
            //them to the write costs nothing, where carrying the archive's quarter-million rows would.
            try
            {
                opts.WeatherStatistics = WeatherStatisticsReport.Compute(
                    pool, rows, opts.Weather.LiveMoistureByDate,
                    opts.Weather.Lag10HourPercent, opts.Weather.Lag100HourPercent);

                opts.WeatherStatisticsHeader =
                    $"Campaign weather distributions, fitted over the {opts.CandidateDaysPerYear} "
                    + "highest-FWI days of each year.\n"
                    + $"Pool: {pool.Count} days over {pool.Select(d => d.Year).Distinct().Count()} years, "
                    + $"archive {Path.GetFileName(opts.Weather.ArchiveCsvPath)}.\n"
                    + "Temperature, relative humidity and wind speed are drawn independently from fitted "
                    + "normals. The two live moistures are the one exception: they are resampled as a pair "
                    + "from these same days, because their distribution sits on the fully-cured floor and a "
                    + "truncated normal came out systematically wetter than the pool. Their mean and sd "
                    + "below therefore describe the sample drawn from, not a distribution's parameters.";
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("WARNING: could not compute the weather distribution report: " + e.Message);
            }
        }

        /// <summary>
        /// Runs the climatology → WindNinja → Nelson chain for one realization, into its own
        /// private weather directory. Returns that directory, or null when per-realization weather
        /// is not enabled (in which case the realization uses whatever weather rasters the shared
        /// inputs folder already holds, as built by <c>build-case</c>).
        ///
        /// The seed is offset by the realization index so each draws a different historical day
        /// while the campaign as a whole stays reproducible from <c>--seed</c>. The ERA5 archive is
        /// downloaded once and cached, so this costs no extra network traffic per realization.
        /// </summary>
        /// <param name="result">
        /// What the chain produced, so the caller can write the drawn live fuel moisture into the
        /// realization's namelist and record the draw for the campaign summary. Null when no chain ran.
        /// </param>
        private static string TryGenerateWeather(Options opts, string runDir, string idx, int index,
            out WeatherRasterPipeline.Result result, double? forceWindFromDeg = null)
        {
            result = null;
            if (!opts.RealizationWeather) return null;

            string weatherDir = Path.Combine(runDir, "weather");
            Directory.CreateDirectory(weatherDir);

            try
            {
                WeatherRasterPipeline.Options w = opts.Weather;
                var per = new WeatherRasterPipeline.Options
                {
                    Grid = opts.WeatherGrid,
                    InputsDirectory = weatherDir,
                    TerrainDirectory = opts.ElmfireInputs,
                    LatLon = w.LatLon,
                    ArchiveCsvPath = w.ArchiveCsvPath,
                    ArchiveStartYear = w.ArchiveStartYear,
                    ArchiveEndYear = w.ArchiveEndYear,
                    ConditioningDays = w.ConditioningDays,
                    BurningPeriodStartHour = w.BurningPeriodStartHour,
                    BurningPeriodEndHour = w.BurningPeriodEndHour,
                    WindNinjaExe = w.WindNinjaExe,
                    WindNinjaVegetation = w.WindNinjaVegetation,
                    WindNinjaMesh = w.WindNinjaMesh,

                    //The realization's own duration, so the series covers the fire rather than the pipeline's
                    //8-hour default. Without these the chain wrote 8 bands however long the run was, and a
                    //three-day realization spent 64 of its 72 hours on hour 8's weather.
                    SimulationTstopSeconds = opts.TstopSeconds > 0 ? opts.TstopSeconds : 3.0 * 24.0 * 3600.0,
                    SecondsPerBand = opts.SecondsPerBand,

                    //One band when asked for it: the drawn day still varies per realization, but its wind is
                    //solved once and held, instead of one solve per hour of a run that may be days long. The
                    //cap otherwise stands as the WindNinja budget - past it the chain writes one band anyway,
                    //because a multi-band series shorter than the run is refused by ELMFIRE.
                    MaxBands = opts.SingleBandWeather ? 1 : opts.MaxWeatherBands,

                    //Aimed at the community, before WindNinja rather than after it: what gets written is the
                    //terrain's answer to a wind from this direction, not a uniform field. Only the direction is
                    //replaced - the drawn day's speed, moisture and diurnal shape are untouched.
                    ForceWindDirectionDeg = forceWindFromDeg,

                    Sampling = opts.WeatherSampling,
                    CandidateDaysPerYear = opts.CandidateDaysPerYear,
                    Lag10HourPercent = w.Lag10HourPercent,
                    Lag100HourPercent = w.Lag100HourPercent,

                    //The march done once at startup, handed over rather than repeated per realization.
                    FitLiveFuelMoisture = w.FitLiveFuelMoisture,
                    LiveFuelMoisture = w.LiveFuelMoisture,
                    LiveMoistureByDate = w.LiveMoistureByDate,

                    Seed = unchecked(w.Seed + index),
                    //quiet: at --parallel width the per-stage chatter from several realizations
                    //interleaves into noise. The drawn day is reported on one line below instead.
                    Log = null,
                };

                WeatherRasterPipeline.Result r = WeatherRasterPipeline.Run(per).GetAwaiter().GetResult();
                result = r;

                //Named by what the weather actually is, because the two modes are not distinguishable from
                //the numbers alone and a date printed against a synthetic draw would be a lie about where
                //it came from.
                string source = r.Day.HasValue ? r.Day.Value.Date.ToString("yyyy-MM-dd")
                              : r.Drawn.HasValue ? $"drawn {r.Drawn.Value.Temperature:F0} C / RH {r.Drawn.Value.RelativeHumidity:F0}%"
                              : "uniform";
                Console.WriteLine($"[{idx}] weather {source}: wind {r.MeanWindSpeedMph:F1} mph, " +
                                  $"dead moisture {r.MeanM1Percent:F1}/{r.MeanM10Percent:F1}/{r.MeanM100Percent:F1} %" +
                                  (r.Drawn.HasValue && r.Drawn.Value.LiveDrawn
                                      ? $", live {r.Drawn.Value.LiveHerbaceousPercent:F0}/{r.Drawn.Value.LiveWoodyPercent:F0} %" : "") +
                                  (r.Fallbacks.Count > 0 ? $" ({string.Join("; ", r.Fallbacks)})" : ""));

                if (r.Drawn.HasValue)
                {
                    lock (opts.DrawnWeatherLock)
                    {
                        opts.DrawnWeather.Add((index, r.Drawn.Value,
                            r.MeanM1Percent, r.MeanM10Percent, r.MeanM100Percent));
                    }
                }

                return weatherDir;
            }
            catch (Exception e)
            {
                //A weather failure falls back to the shared rasters rather than failing the
                //realization: the case-level set is a valid, if less varied, input.
                Console.Error.WriteLine($"[{idx}] per-realization weather failed ({e.Message}); using the shared rasters.");
                return null;
            }
        }

        private static bool TryGenerateRasters(Options opts, string caseDir, string idx, int index,
            out string toa, out string ros, out string sd, out string fi)
        {
            toa = ros = sd = fi = null;

            string runDir = Path.Combine(caseDir, "_elmfire", idx);
            Directory.CreateDirectory(runDir);

            string[] lines = File.ReadAllLines(opts.ElmfireTemplate);
            lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                        ElmfireNamelistKeys.Seed, unchecked(opts.Seed + index).ToString(CultureInfo.InvariantCulture));
            lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                        ElmfireNamelistKeys.NumEnsembleMembers, "1");

            //What the seed above actually varies. Without this the seed changes nothing: a template built from a
            //scenario with a fixed ignition point places the same fire in every realization.
            //
            //Two ways to vary it, and which one is in force is decided by whether anything else in the
            //realization has to be derived from the ignition. Normally ELMFIRE draws it, which keeps the physics
            //in one place. With --wind-to-wui the wind has to be aimed from the fire at the community, and the
            //wind rasters are written before ELMFIRE starts - so the draw has to happen here instead. Same mask,
            //same weighting, same kind of ensemble; only the sampler moves.
            (double X, double Y, double WindFromDeg)? drawn = null;
            if (opts.WindToWui)
            {
                drawn = DrawIgnitionAndBearing(opts, idx, index);
                if (drawn == null) return false;

                lines = ElmfireNamelist.SetSampledIgnition(lines, drawn.Value.X, drawn.Value.Y);
            }
            else
            {
                lines = ElmfireNamelist.ForceRandomIgnition(lines, opts.IgnitionMaskStem);
            }

            // Inputs stay shared and read-only; outputs and scratch are per realization. ELMFIRE
            // appends the path separator to these itself, so they are passed without one.
            lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup,
                        ElmfireNamelistKeys.FuelsAndTopographyDirectory, opts.ElmfireInputs, quoted: true);

            // Weather is per realization when the climatology chain is driving it: each draws its
            // own historical peak fire-weather day, which is what makes the ensemble vary in
            // weather rather than only in ignition location. Terrain still comes from the one
            // shared copy - only the five weather stems are private.
            string weatherDir = TryGenerateWeather(opts, runDir, idx, index,
                                    out WeatherRasterPipeline.Result weather, drawn?.WindFromDeg);
            string weatherSource = weatherDir ?? opts.WeatherDirectory ?? opts.ElmfireInputs;

            //A bearing computed and then not applied would be silent: the fire would run on the drawn day's own
            //wind while the log said otherwise. Only reachable if the weather chain fell back to the shared
            //rasters, which it does when a stage fails.
            if (drawn != null && weatherDir == null)
            {
                Console.Error.WriteLine($"[{idx}] the wind could not be aimed at the WUI area: this realization "
                    + "has no weather of its own, so it is skipped rather than run on a wind pointing elsewhere.");
                return false;
            }
            lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup,
                        ElmfireNamelistKeys.WeatherDirectory, weatherSource, quoted: true);

            //NUM_METEOROLOGY_TIMES has to describe the rasters actually being read, and the template's value
            //describes whatever the case was built for. Left alone, a template built for an 8-hour case was
            //used verbatim for a 72-hour realization: ELMFIRE read 8 bands and held the last one for the
            //remaining 64 hours, so the campaign's whole premise - distant ignitions taking days to arrive -
            //played out under one frozen hour of weather. Per-realization weather made it worse by writing a
            //different band count than the template claimed.
            ApplyMeteorologyBandCount(ref lines, weatherSource, opts, idx);

            //Live fuel moisture is a namelist scalar, not a raster, which is what the NFDRS4 GSI model can
            //actually support: it takes latitude and weather and no terrain at all, so there is no per-cell
            //answer to write. USE_CONSTANT_LH/LW therefore stay true and only the two values move - writing a
            //spatially constant MLH/MLW raster instead would cost a million cells per realization to say the
            //same number, and would imply a resolution the model does not have.
            if (weather?.Drawn != null && weather.Drawn.Value.LiveDrawn)
            {
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup,
                            "LH_MOISTURE_CONTENT",
                            weather.Drawn.Value.LiveHerbaceousPercent.ToString("0.###", CultureInfo.InvariantCulture));
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup,
                            "LW_MOISTURE_CONTENT",
                            weather.Drawn.Value.LiveWoodyPercent.ToString("0.###", CultureInfo.InvariantCulture));

                //Forced rather than assumed: a template built with USE_CONSTANT_LH false would send ELMFIRE
                //looking for an MLH raster that no realization writes, and the value set above would be
                //ignored while appearing in the namelist.
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup,
                            "USE_CONSTANT_LH", ".TRUE.");
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.InputsGroup,
                            "USE_CONSTANT_LW", ".TRUE.");
            }

            lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.OutputsGroup,
                        ElmfireNamelistKeys.OutputsDirectory, "./outputs", quoted: true);
            lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MiscellaneousGroup,
                        ElmfireNamelistKeys.Scratch, "./scratch", quoted: true);

            //Absolute, because the run directory is not the case root the template's relative path was written
            //against - see ResolveMiscellaneousInputs.
            if (opts.MiscInputsDirectory != null)
            {
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MiscellaneousGroup,
                            ElmfireNamelistKeys.MiscellaneousInputsDirectory, opts.MiscInputsDirectory, quoted: true);
            }

            if (opts.PathToGdal != null)
            {
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MiscellaneousGroup,
                            ElmfireNamelistKeys.PathToGdal, opts.PathToGdal, quoted: true);
            }
            if (opts.TstopSeconds > 0)
            {
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.TimeControlGroup,
                            ElmfireNamelistKeys.SimulationTstop, opts.TstopSeconds.ToString(CultureInfo.InvariantCulture));
            }

            var r = ElmfireRunner.Run(opts.ElmfireExe, runDir, idx, lines, opts.Resume, Console.Out, opts.PathToGdal);
            if (!r.Ok)
            {
                Console.Error.WriteLine($"[{idx}] ELMFIRE failed: {r.Message}");
                return false;
            }

            toa = r.Toa; ros = r.Ros; sd = r.Sd; fi = r.Fi ?? "";
            return true;
        }

        /// <summary>
        /// Sets <c>NUM_METEOROLOGY_TIMES</c> to the number of bands the weather rasters actually carry, and says
        /// so when that is not enough weather to cover the run.
        /// </summary>
        /// <remarks>
        /// Reported once, from the first realization, rather than per realization: it is a property of the case
        /// and the campaign's duration, so saying it hundreds of times would bury it.
        ///
        /// The shortfall is a warning rather than an error because the case is still runnable and the result is
        /// still a fire — just one whose later hours are all weathered alike. Fixing it means rebuilding the
        /// case's weather for the campaign's duration, which is the user's call and destroys the existing
        /// series.
        /// </remarks>
        private static void ApplyMeteorologyBandCount(ref string[] lines, string weatherDirectory, Options opts, string idx)
        {
            string ws = Path.Combine(weatherDirectory, "ws.tif");
            int bands = AscRaster.GetBandCount(ws);

            if (bands <= 0)
            {
                Console.Error.WriteLine($"[{idx}] could not read the band count from {ws}; leaving "
                    + "NUM_METEOROLOGY_TIMES as the template has it.");
                return;
            }

            //The band range has to fit inside the rasters that exist, and the template's describes whatever the
            //case was built for. Left alone, a template saying METEOROLOGY_BAND_STOP = 72 against a realization
            //with one band of weather made ELMFIRE read a slice past the end of it and refuse the run with
            //"[ERROR] Error processing ./scratch\ws.hdr slice band end (2" - which names neither the key nor the
            //raster's band count.
            //
            //The stop collapses onto the start because this driver's unit is one case per realization; ELMFIRE
            //forces the same thing itself for a fixed ignition, and with a drawn one it would otherwise run a
            //case per band in the range.
            int start = 1;
            string templateStart = ElmfireNamelist.GetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                                       ElmfireNamelistKeys.MeteorologyBandStart);
            if (!string.IsNullOrEmpty(templateStart)
                && int.TryParse(templateStart, NumberStyles.Any, CultureInfo.InvariantCulture, out int parsed)
                && parsed >= 1)
            {
                start = parsed;
            }

            if (start > bands)
            {
                //A start past the end of the series is not a preference that can be honoured.
                Console.Error.WriteLine($"[{idx}] METEOROLOGY_BAND_START is {start} but the weather has {bands} "
                    + "band(s); starting at 1.");
                start = 1;
            }

            lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                        ElmfireNamelistKeys.MeteorologyBandStart, start.ToString(CultureInfo.InvariantCulture));
            lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                        ElmfireNamelistKeys.MeteorologyBandStop, start.ToString(CultureInfo.InvariantCulture));

            //Counted from the start band, not from 1: ELMFIRE reads the window
            //[START, START + NUM_METEOROLOGY_TIMES - 1], so a run starting at band 5 of a 72-band series has 68
            //bands available, not 72.
            lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.MonteCarloGroup,
                        ElmfireNamelistKeys.NumMeteorologyTimes,
                        (bands - start + 1).ToString(CultureInfo.InvariantCulture));

            //The template's WX_BANDS_KEPT_IN_MEM is left alone: it used to be forced above the band count here
            //because swapping weather slices crashed ELMFIRE, and with that fixed the rolling window is what
            //keeps a campaign of long realizations off the memory ceiling. Only the floor is enforced, since a
            //band is interpolated against the next one.
            string kept = ElmfireNamelist.GetKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup,
                            ElmfireNamelistKeys.WxBandsKeptInMem);
            if (!int.TryParse(kept, NumberStyles.Integer, CultureInfo.InvariantCulture, out int keptBands)
                || keptBands < 2)
            {
                lines = ElmfireNamelist.SetKeyInGroup(lines, ElmfireNamelistKeys.SimulatorGroup,
                            ElmfireNamelistKeys.WxBandsKeptInMem, "30");
            }

            //A single-band series is exempt, in ELMFIRE's own check:
            //  IF (WS%NBANDS * DT_METEOROLOGY .LT. SIMULATION_TSTOP .AND. WS%NBANDS .GT. 1)
            //One band means "this is the weather, for the whole run", and ELMFIRE holds it. So a one-band case
            //runs a 300 h fire happily, and warning about it would be advising a rebuild that changes nothing.
            //The Mati campaign of 2026-07-27 is the demonstration: one band, 20 h fires, 96 realizations.
            double covered = bands * opts.SecondsPerBand;
            if (bands == 1 || covered >= opts.TstopSeconds
                || Interlocked.Exchange(ref _weatherShortfallReported, 1) != 0)
            {
                return;
            }

            //ELMFIRE checks this itself and refuses - "[ERROR] Not enough weather bands for given SIMULATION
            //TSTOP" - so this is a warning about a campaign that is about to fail on every realization, not
            //about one that will quietly use stale weather. Said here because ELMFIRE's own message names
            //neither the case nor the numbers, and by then it has been repeated once per realization.
            Console.Error.WriteLine(
                $"WARNING: the weather series covers {covered / 3600.0:F0} h ({bands} bands of "
                + $"{opts.SecondsPerBand / 3600.0:F0} h) but each realization runs {opts.TstopSeconds / 3600.0:F0} h. "
                + "ELMFIRE requires enough bands to cover the run and will refuse every realization with "
                + "\"Not enough weather bands for given SIMULATION TSTOP\".");
            Console.Error.WriteLine(
                "         Four ways out. --single-band-weather holds hour 1 for the whole run, which ELMFIRE "
                + "allows at any duration (its check exempts a one-band series) and costs nothing to prepare.");
            Console.Error.WriteLine(
                $"         Or give the case {(int)System.Math.Ceiling(opts.TstopSeconds / opts.SecondsPerBand)} "
                + "bands - delete inputs/ws,wd,m1,m10,m100.tif, set [ELMFIRE] SimulationTstopSeconds to "
                + $"{opts.TstopSeconds:F0} and build the case again, which is one WindNinja solve per band. Or "
                + $"--realization-weather for a full-length series per realization. Or lower --tstop to "
                + $"{covered:F0} to match the weather you have.");
        }

        /// <summary>Guards the shortfall warning so it is printed once per campaign, not once per realization.</summary>
        private static int _weatherShortfallReported;

        /// <summary>
        /// Refuses a base scenario whose WUI areas produce one boundary per evacuation group.
        /// </summary>
        /// <remarks>
        /// The driver reads back exactly one boundary per realization, at a filename it constructs itself.
        /// <c>WuiAreaSource=EvacuationGroupsSeparate</c> makes the simulation write one per group, each with the
        /// group's name appended — so nothing matches what the driver looks for, every realization reports
        /// "produced no trigger boundary", and the campaign ends with "no realizations produced a usable trigger
        /// boundary" after consuming <c>--max</c> of them.
        ///
        /// Refused at startup rather than discovered a hundred realizations in. Aggregating per-group
        /// boundaries is a real thing to want - it would mean one probability raster per group - but it is a
        /// feature, not a filename fix, and pretending otherwise here would silently union areas whose whole
        /// point is that they evacuate on different schedules.
        /// </remarks>
        private static bool CheckWuiAreaSourceIsAggregatable(string[] baseLines)
        {
            string source = null;
            bool inSection = false;

            foreach (string raw in baseLines)
            {
                string line = raw.Trim();

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inSection = string.Equals(line, "[kPERIL]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                if (string.Equals(line.Substring(0, eq).Trim(), "WuiAreaSource", StringComparison.OrdinalIgnoreCase))
                {
                    source = line.Substring(eq + 1).Trim();
                    break;
                }
            }

            if (source == null
                || !source.Equals("EvacuationGroupsSeparate", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            Console.Error.WriteLine(
                "ERROR: [kPERIL] WuiAreaSource=EvacuationGroupsSeparate writes one trigger boundary per "
                + "evacuation group, and this driver aggregates one boundary per realization.");
            Console.Error.WriteLine(
                "       Use Raster or EvacuationGroupsCombined for a campaign, and run the per-group "
                + "boundaries as single simulations.");
            return false;
        }

        /// <summary>The template's <c>DT_METEOROLOGY</c>, or <paramref name="fallback"/> when it does not say.</summary>
        private static double ReadDtMeteorology(string templatePath, double fallback)
        {
            try
            {
                foreach (string raw in File.ReadAllLines(templatePath))
                {
                    string line = raw.Trim();
                    //Comment markers are '!' in a Fortran namelist; a commented key must not be read as set.
                    if (line.Length == 0 || line.StartsWith("!")) continue;

                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    if (!string.Equals(line.Substring(0, eq).Trim(), ElmfireNamelistKeys.DtMeteorology,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string value = line.Substring(eq + 1).Trim();
                    if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double parsed)
                        && parsed > 0)
                    {
                        return parsed;
                    }
                }
            }
            catch { }

            return fallback;
        }

        private static RealizationOutcome RunRealization(string preactExe, string caseDir, string baseName, string[] baseLines,
            int index, Options opts, string outputDir)
        {
            string idx = index.ToString().PadLeft(opts.Pad, '0');
            string toa, ros, sd, fi;

            if (opts.GenerateRealizations && !opts.ResumeOnly)
            {
                if (!TryGenerateRasters(opts, caseDir, idx, index, out toa, out ros, out sd, out fi))
                {
                    return new RealizationOutcome { Ok = false, Idx = idx };
                }
            }
            else if (opts.RasterDir != null)
            {
                //Resolved rather than composed: an ELMFIRE filename ends in the run's stop time, so the
                //patterns stop matching the moment the ensemble is regenerated for a different duration.
                toa = EnsembleRasters.Resolve(opts.RasterDir, opts.ToaPattern, idx);
                ros = EnsembleRasters.Resolve(opts.RasterDir, opts.RosPattern, idx);
                sd  = EnsembleRasters.Resolve(opts.RasterDir, opts.SdPattern, idx);
                fi  = EnsembleRasters.Resolve(opts.RasterDir, opts.FiPattern, idx);
            }
            else
            {
                //--resume-only against a generated ensemble. There is no --dir in that
                //configuration (it is not required when --elmfire generates the rasters), and
                //resume-only reads each boundary straight out of the output folder without ever
                //touching the source rasters - so the paths are simply unused here. Building them
                //from a null RasterDir threw instead, which made resume-only unusable in exactly
                //the case it is most wanted: re-aggregating an interrupted generating campaign.
                toa = ros = sd = fi = string.Empty;
            }

            bool ok = RealizationRunner.TryRun(
                preactExe, caseDir, baseName, baseLines, idx, toa, ros, sd, fi,
                outputDir, opts.Resume, opts.ResumeOnly,
                out float[,] boundary, out AscRaster.Header h,
                opts.ElmfireInputs);

            return new RealizationOutcome { Ok = ok, Idx = idx, Boundary = boundary, Header = h, ToaPath = toa };
        }

        private static async Task<int> RunAsync(Options opts, string caseDir, string outputDir, string preactExe,
            string baseName, string[] baseLines, string diagnosticsPath, string livePath)
        {
            using var diag = new StreamWriter(diagnosticsPath);
            WriteDiagnosticsHeader(diag);

            int[,] insideCount = null;
            AscRaster.Header header = default;

            //Created on the first realization that has an arrival raster, because its grid is what the
            //statistics live on and the boundary's grid is not necessarily the same one.
            EnsembleFireStatistics fireStatistics = null;
            int nSuccess = 0;
            int nFailed = 0;
            int streak = 0;
            bool converged = false;
            int launched = 0;
            int completed = 0;

            //previous realization's decile areas; null until that decile has a non-zero baseline
            double?[] previousArea = new double?[Deciles.Length];

            var pending = new List<Task<RealizationOutcome>>();

            while (true)
            {
                while (pending.Count < opts.Parallelism && launched < opts.MaxRealizations && !converged)
                {
                    int index = opts.Start + launched;
                    ++launched;
                    pending.Add(Task.Run(() => RunRealization(preactExe, caseDir, baseName, baseLines, index, opts, outputDir)));
                }

                if (pending.Count == 0) break;

                Task<RealizationOutcome> finishedTask = await Task.WhenAny(pending);
                pending.Remove(finishedTask);
                RealizationOutcome result = await finishedTask;
                ++completed;

                Console.WriteLine($"PROGRESS {completed}/{opts.MaxRealizations} realization {result.Idx}");

                if (!result.Ok)
                {
                    ++nFailed;
                    continue;
                }

                if (insideCount == null)
                {
                    header = result.Header;
                    insideCount = new int[header.Ncols, header.Nrows];
                }
                else if (result.Header.Ncols != header.Ncols || result.Header.Nrows != header.Nrows)
                {
                    Console.Error.WriteLine($"[{result.Idx}] boundary dimensions {result.Header.Ncols}x{result.Header.Nrows} differ from {header.Ncols}x{header.Nrows}, skipping.");
                    ++nFailed;
                    continue;
                }

                float[,] boundary = result.Boundary;
                for (int x = 0; x < header.Ncols; ++x)
                {
                    for (int y = 0; y < header.Nrows; ++y)
                    {
                        float v = boundary[x, y];
                        if (v >= 1f && v != header.NoDataValue)
                        {
                            insideCount[x, y] += 1;
                        }
                    }
                }
                ++nSuccess;

                //The fire itself, alongside the boundary derived from it. Folded here rather than in a second
                //pass at the end because the rasters are on disk per realization and re-reading hundreds of
                //them would cost more than the running sums do, and because --resume-only has no rasters to
                //re-read at all.
                AccumulateFireStatistics(opts, result, ref fireStatistics);

                double cellArea = header.CellSize * header.CellSize;
                double[] area = new double[Deciles.Length];
                double?[] delta = new double?[Deciles.Length];
                bool allWithinTolerance = true;
                bool anyDeltaChecked = false;

                for (int t = 0; t < Deciles.Length; ++t)
                {
                    long cellsAtOrAbove = 0;
                    for (int x = 0; x < header.Ncols; ++x)
                    {
                        for (int y = 0; y < header.Nrows; ++y)
                        {
                            double p = (double)insideCount[x, y] / nSuccess;
                            if (p >= Deciles[t]) ++cellsAtOrAbove;
                        }
                    }
                    area[t] = cellsAtOrAbove * cellArea;

                    //a decile with no established (non-zero) baseline yet is excluded from the
                    //convergence test rather than compared against a zero denominator
                    if (previousArea[t].HasValue && previousArea[t].Value > 0)
                    {
                        anyDeltaChecked = true;
                        double d = Math.Abs(area[t] - previousArea[t].Value) / previousArea[t].Value;
                        delta[t] = d;
                        if (d >= opts.Tolerance) allWithinTolerance = false;
                    }

                    previousArea[t] = area[t];
                }

                //the first realization aggregated (and any run where every decile is still
                //awaiting its baseline) has nothing to compare against yet, so it must not count
                //toward the streak in either direction
                if (anyDeltaChecked) streak = allWithinTolerance ? streak + 1 : 0;
                WriteDiagnosticsRow(diag, nSuccess, result.Idx, nSuccess, area, delta, streak);

                Console.WriteLine($"[{result.Idx}] streak {streak}/{opts.Streak} (nSuccess={nSuccess}).");

                //Live state for the Unity window, which reads this stream rather than
                //re-implementing the convergence loop. Emitted here because everything the UI
                //needs is already assembled at this point. One self-delimiting line per
                //realization, so a partially-flushed write can never be half-parsed, and plain
                //stdout so it costs nothing when nobody is listening.
                if (opts.EmitProgressJson)
                {
                    EmitProgressJson(result.Idx, nSuccess, nFailed, streak, opts.Streak, converged, area, delta);
                }

                //Snapshot the running raster so the UI can show the probability field building up
                //rather than only its final state. Cheap next to a realization (one SUMO run), and
                //it doubles as a crash-safety net for long campaigns.
                if (opts.EmitProgressJson && insideCount != null)
                {
                    try
                    {
                        var snapHeader = header;
                        snapHeader.NoDataValue = -9999.0;
                        AscRaster.Write(BuildProbability(insideCount, header, nSuccess), snapHeader, livePath);
                        Console.WriteLine("PROGRESS_RASTER " + livePath);
                    }
                    catch (Exception e)
                    {
                        //a failed snapshot must never abort the campaign
                        Console.Error.WriteLine("WARNING: could not write live raster snapshot: " + e.Message);
                    }
                }

                if (streak >= opts.Streak)
                {
                    converged = true;
                    Console.WriteLine($"Converged after {nSuccess} realizations ({streak} consecutive within {opts.Tolerance:P0} per decile).");
                }
            }

            if (insideCount == null || nSuccess == 0)
            {
                Console.Error.WriteLine("ERROR: no realizations produced a usable trigger boundary; nothing to aggregate.");
                return 1;
            }

            if (!converged)
            {
                Console.Error.WriteLine($"WARNING: reached --max {opts.MaxRealizations} realizations ({nSuccess} successful) without converging; probability raster is not yet stable.");
            }

            float[,] probability = BuildProbability(insideCount, header, nSuccess);

            string outPath = opts.OutPath ?? Path.Combine(outputDir, "trigger_probability.asc");
            AscRaster.Header outHeader = header;
            outHeader.NoDataValue = -9999.0;
            AscRaster.Write(probability, outHeader, outPath);

            //The fire the boundary was derived from, as against the boundary itself: which ground burns, how
            //often, and how soon. Written from rasters the campaign already read.
            if (fireStatistics != null)
            {
                List<string> stats = fireStatistics.WriteAll(outputDir, "ensemble",
                    new[] { 0.1, 0.5, 0.9 }, Console.WriteLine);

                if (fireStatistics.Realizations != nSuccess)
                {
                    //Worth saying rather than hiding: the two denominators differ when a realization produced a
                    //boundary but no readable arrival raster, and the probabilities are then over different
                    //numbers of realizations.
                    Console.WriteLine($"  ensemble: fire statistics cover {fireStatistics.Realizations} of the "
                        + $"{nSuccess} realizations that produced a boundary.");
                }

                foreach (string s in stats) Console.WriteLine("  " + s);
            }
            else
            {
                Console.WriteLine("  ensemble: no arrival rasters were available, so only the trigger boundary "
                    + "was aggregated (expected with --resume-only).");
            }

            WriteRealizedWeather(opts, outputDir);

            Console.WriteLine($"Done. Aggregated {nSuccess} realizations ({nFailed} skipped/failed). Converged: {converged}.");
            Console.WriteLine("Probability raster: " + outPath);
            Console.WriteLine("Convergence diagnostics: " + diagnosticsPath);
            Console.WriteLine("PROGRESS " + opts.MaxRealizations + "/" + opts.MaxRealizations);
            return 0;
        }

        // ---- diagnostics CSV ----------------------------------------------------------------

        /// <summary>
        /// Writes what the ensemble actually drew: one row per realization, then the realized mean and
        /// standard deviation of every variable.
        /// </summary>
        /// <remarks>
        /// The companion to <c>weather_distributions.csv</c>, and the reason both exist. That file says what
        /// the campaign intended to sample from; this one says what it got. They should agree to within
        /// sampling error, and when they do not the campaign is not sampling what it claims — a truncation
        /// binding harder than expected, a realization set too small for the spread, or a fit whose mean sits
        /// outside its own bounds. None of that is visible from the boundary raster.
        ///
        /// The two live moisture columns are the exception to that check, in a useful way: they are
        /// resampled from the pool rather than drawn from a normal, so their realized mean should match
        /// the pool's almost exactly rather than merely within sampling error. They also always belong to
        /// the same candidate day as each other.
        /// </remarks>
        private static void WriteRealizedWeather(Options opts, string outputDir)
        {
            List<(int Index, ClimatologySampler.DrawnFireWeather Drawn, double M1, double M10, double M100)> drawn;
            lock (opts.DrawnWeatherLock)
            {
                if (opts.DrawnWeather.Count == 0) return;
                drawn = opts.DrawnWeather.OrderBy(d => d.Index).ToList();
            }

            try
            {
                string path = Path.Combine(outputDir, "weather_realizations.csv");
                var sb = new StringBuilder();
                sb.AppendLine("# What each realization actually drew. Compare against weather_distributions.csv,");
                sb.AppendLine("# which is the pool these were drawn from. The last two rows are the realized");
                sb.AppendLine("# mean and standard deviation over the ensemble.");
                sb.AppendLine("realization,temperature_c,relative_humidity_pct,wind_speed_ms,wind_direction_deg,"
                    + "wind_direction_source,dead_1h_pct,dead_10h_pct,dead_100h_pct,live_herbaceous_pct,live_woody_pct");

                foreach (var d in drawn)
                {
                    sb.AppendLine(string.Join(",",
                        d.Index.ToString(CultureInfo.InvariantCulture),
                        N(d.Drawn.Temperature), N(d.Drawn.RelativeHumidity), N(d.Drawn.WindSpeedMps),
                        N(d.Drawn.WindDirectionDeg),
                        d.Drawn.DirectionResampled ? "resampled" : "aimed",
                        N(d.M1), N(d.M10), N(d.M100),
                        d.Drawn.LiveDrawn ? N(d.Drawn.LiveHerbaceousPercent) : "",
                        d.Drawn.LiveDrawn ? N(d.Drawn.LiveWoodyPercent) : ""));
                }

                //The direction column is left out of both summary rows on purpose: aimed directions are a
                //property of where each fire started, not a sample from a distribution, and a linear mean of
                //bearings is wrong even when they are. weather_distributions.csv reports the pool's circular
                //statistics for the resampled case.
                var live = drawn.Where(d => d.Drawn.LiveDrawn).ToList();
                AppendSummary(sb, "MEAN", drawn, live, (v) => v.Average());
                AppendSummary(sb, "SD", drawn, live, Sd);

                File.WriteAllText(path, sb.ToString());
                Console.WriteLine($"Realized ensemble weather ({drawn.Count} realizations): " + path);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("WARNING: could not write the realized weather summary: " + e.Message);
            }

            string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

            double Sd(IEnumerable<double> values)
            {
                List<double> v = values.ToList();
                if (v.Count < 2) return 0.0;
                double m = v.Average();
                return Math.Sqrt(v.Sum(x => (x - m) * (x - m)) / (v.Count - 1));
            }

            void AppendSummary(StringBuilder sb, string label,
                List<(int Index, ClimatologySampler.DrawnFireWeather Drawn, double M1, double M10, double M100)> all,
                List<(int Index, ClimatologySampler.DrawnFireWeather Drawn, double M1, double M10, double M100)> withLive,
                Func<IEnumerable<double>, double> f)
            {
                sb.AppendLine(string.Join(",", label,
                    N(f(all.Select(d => d.Drawn.Temperature))),
                    N(f(all.Select(d => d.Drawn.RelativeHumidity))),
                    N(f(all.Select(d => d.Drawn.WindSpeedMps))),
                    "", "",
                    N(f(all.Select(d => d.M1))), N(f(all.Select(d => d.M10))), N(f(all.Select(d => d.M100))),
                    withLive.Count > 0 ? N(f(withLive.Select(d => d.Drawn.LiveHerbaceousPercent))) : "",
                    withLive.Count > 0 ? N(f(withLive.Select(d => d.Drawn.LiveWoodyPercent))) : ""));
            }
        }

        private static void WriteDiagnosticsHeader(StreamWriter w)
        {
            var cols = new List<string> { "run", "realization_id", "nSuccess", "streak" };
            foreach (double tau in Deciles) cols.Add("area_p" + (int)Math.Round(tau * 100));
            foreach (double tau in Deciles) cols.Add("delta_p" + (int)Math.Round(tau * 100));
            w.WriteLine(string.Join(",", cols));
        }

        /// <summary>
        /// Folds one realization's time-of-arrival raster into the ensemble's fire statistics.
        /// </summary>
        /// <remarks>
        /// Best-effort by design. A realization whose arrival raster cannot be read still contributed a valid
        /// trigger boundary — that is what the campaign is for — so failing the run over the statistics would
        /// discard the answer for the sake of the supplementary figures. It says so once per realization and
        /// carries on.
        ///
        /// The realization is not counted at all when its raster is missing, rather than counted as
        /// "did not burn": an unreadable file is no evidence about the ground, and treating it as evidence of
        /// safety is the one interpretation that biases the result in the dangerous direction.
        /// </remarks>
        private static void AccumulateFireStatistics(Options opts, RealizationOutcome result,
            ref EnsembleFireStatistics statistics)
        {
            if (string.IsNullOrEmpty(result.ToaPath) || !File.Exists(result.ToaPath))
            {
                return;
            }

            try
            {
                float[,] toa = AscRaster.Read(result.ToaPath, out AscRaster.Header toaHeader, out bool ok);
                if (!ok || toa == null)
                {
                    Console.Error.WriteLine($"[{result.Idx}] arrival raster could not be read; it is left out of the fire statistics.");
                    return;
                }

                if (statistics == null)
                {
                    //Binned over the run's own duration. --tstop of 0 means the template decides, and the
                    //template is not parsed here, so three days is assumed - the same default --tstop carries.
                    double duration = opts.TstopSeconds > 0 ? opts.TstopSeconds : 3.0 * 24.0 * 3600.0;
                    statistics = new EnsembleFireStatistics(toaHeader, duration);
                }

                statistics.Add(toa);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[{result.Idx}] arrival raster could not be folded in ({e.Message}).");
            }
        }

        /// <summary>Per-cell probability = fraction of successful realizations enclosing the cell.</summary>
        private static float[,] BuildProbability(int[,] insideCount, AscRaster.Header header, int nSuccess)
        {
            float[,] probability = new float[header.Ncols, header.Nrows];
            for (int x = 0; x < header.Ncols; ++x)
            {
                for (int y = 0; y < header.Nrows; ++y)
                {
                    probability[x, y] = (float)insideCount[x, y] / nSuccess;
                }
            }
            return probability;
        }

        /// <summary>
        /// One JSON object per line on stdout, tagged so a reader can pick it out of the ordinary
        /// log stream. Hand-built rather than serialized to keep PREACTcli free of a JSON
        /// dependency for a single fixed-shape record; every value is written with
        /// InvariantCulture so a comma-decimal locale cannot produce malformed JSON.
        /// </summary>
        private static void EmitProgressJson(string idx, int nSuccess, int nFailed, int streak, int streakTarget,
                                             bool converged, double[] area, double?[] delta)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("PROGRESS_JSON {");
            sb.Append("\"realization\":\"").Append(idx).Append("\",");
            sb.Append("\"nSuccess\":").Append(nSuccess).Append(',');
            sb.Append("\"nFailed\":").Append(nFailed).Append(',');
            sb.Append("\"streak\":").Append(streak).Append(',');
            sb.Append("\"streakTarget\":").Append(streakTarget).Append(',');
            sb.Append("\"converged\":").Append(converged ? "true" : "false").Append(',');

            sb.Append("\"deciles\":[");
            for (int i = 0; i < Deciles.Length; ++i)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Deciles[i].ToString("0.0#", CultureInfo.InvariantCulture));
            }
            sb.Append("],\"area\":[");
            for (int i = 0; i < area.Length; ++i)
            {
                if (i > 0) sb.Append(',');
                sb.Append(area[i].ToString("R", CultureInfo.InvariantCulture));
            }
            //null where a decile has no baseline yet; JSON null keeps that distinct from 0
            sb.Append("],\"delta\":[");
            for (int i = 0; i < delta.Length; ++i)
            {
                if (i > 0) sb.Append(',');
                sb.Append(delta[i].HasValue ? delta[i].Value.ToString("R", CultureInfo.InvariantCulture) : "null");
            }
            sb.Append("]}");

            Console.WriteLine(sb.ToString());
        }

        private static void WriteDiagnosticsRow(StreamWriter w, int run, string realizationId, int nSuccess, double[] area, double?[] delta, int streak)
        {
            var cols = new List<string> { run.ToString(), realizationId, nSuccess.ToString(), streak.ToString() };
            foreach (double a in area) cols.Add(a.ToString(CultureInfo.InvariantCulture));
            foreach (double? d in delta) cols.Add(d.HasValue ? d.Value.ToString(CultureInfo.InvariantCulture) : "");
            w.WriteLine(string.Join(",", cols));
            w.Flush();
        }

        // ---- args -----------------------------------------------------------------------------

        private class Options
        {
            public string BaseWui;
            public string RasterDir;
            public int MaxRealizations;
            public int Start = 1;
            public int Pad = 4;
            public string ToaPattern = "TOA_{i}.tif";
            public string RosPattern = "ROS_{i}.tif";
            public string SdPattern = "SD_{i}.tif";
            public string FiPattern = "FI_{i}.tif";
            public string PreactExe;
            public string OutPath;
            public string DiagnosticsPath;
            public int Streak = 20;
            public double Tolerance = 0.02;
            public bool Resume;
            public bool ResumeOnly;
            public int Parallelism = Environment.ProcessorCount;

            // ---- generate-realizations mode -----------------------------------------------
            // With --elmfire set, each realization's fire rasters are produced on demand instead
            // of being read from --dir: sample an ignition, patch the template, run ELMFIRE.
            public string ElmfireExe;
            public string ElmfireTemplate;
            public string ElmfireInputs;
            public string PathToGdal;
            /// <summary>
            /// How long each realization's ELMFIRE run simulates, in seconds. Three days.
            /// </summary>
            /// <remarks>
            /// Long on purpose, and much longer than a single case needs. A realization only contributes to
            /// the burn probability and to the trigger boundary if its fire actually reaches the community,
            /// and ignitions are drawn from across the whole domain - so the ones started furthest away, which
            /// are precisely the ones that decide how far out the boundary has to sit, need days to arrive. A
            /// run cut short does not merely lose those realizations: it counts them as fires that did not
            /// threaten the town, and the boundary comes out too tight.
            ///
            /// Overridden with --tstop. Zero leaves the template's own value alone.
            /// </remarks>
            public double TstopSeconds = 3.0 * 24.0 * 3600.0;

            /// <summary>
            /// Seconds each weather band covers. Must be the template's <c>DT_METEOROLOGY</c>, and is read from
            /// it at startup so the two cannot disagree — the band count this driver writes is derived from it.
            /// </summary>
            public double SecondsPerBand = 3600.0;
            public int Seed = 12345;

            /// <summary>
            /// The ignition mask's filename stem inside <see cref="ElmfireInputs"/>, taken from the template or
            /// defaulted, and checked to exist before the first realization.
            /// </summary>
            public string IgnitionMaskStem;

            /// <summary>
            /// The template's <c>MISCELLANEOUS_INPUTS_DIRECTORY</c> made absolute, or null when it does not set
            /// one. See <see cref="ResolveMiscellaneousInputs"/>.
            /// </summary>
            public string MiscInputsDirectory;
            /// <summary>Emit machine-readable per-realization progress (PROGRESS_JSON lines plus a
            /// PROGRESS_RASTER snapshot) for the Unity window, which drives its live view from this
            /// stream instead of re-implementing the convergence loop.</summary>
            public bool EmitProgressJson;

            /// <summary>
            /// Draw a fresh historical peak fire-weather day per realization — the ERA5 sample, a WindNinja
            /// solve per band and Nelson's moisture, run per realization into its own weather folder.
            /// </summary>
            /// <remarks>
            /// On by default. A realization is meant to be a draw of the whole scenario, weather included:
            /// sharing the case's one series makes every realization burn under the same wind, so the
            /// ensemble's only uncertainty is where the fire starts. Turn it off with <c>--shared-weather</c>
            /// when the case's own series is the point (replaying a known day), or to save the solves.
            ///
            /// The cost is real and is reported at startup: one WindNinja solve per band per realization.
            /// <c>--single-band-weather</c> reduces that to one solve per realization, still on that
            /// realization's own drawn day.
            /// </remarks>
            public bool RealizationWeather = true;

            /// <summary>
            /// Hold one hour of weather for the whole of every realization. With per-realization weather that
            /// is one band of that realization's own drawn day — one WindNinja solve rather than one per hour
            /// of the run. Without it, a one-band copy of the case's rasters
            /// (<see cref="SetUpSingleBandWeather"/>).
            /// </summary>
            /// <remarks>
            /// Implied by <see cref="WeatherRasterPipeline.SamplingMode.FittedDistributions"/>, which is one
            /// band by construction — there is nothing for a second band to hold that the first does not.
            /// </remarks>
            public bool SingleBandWeather;

            /// <summary>
            /// How a realization's weather is drawn: from fitted distributions (the default) or by replaying
            /// a historical day. See <see cref="WeatherRasterPipeline.SamplingMode"/>.
            /// </summary>
            /// <remarks>
            /// Fitted by default, because the campaign is asking a question the record cannot answer on its
            /// own. Replaying days confines the ensemble to the ones that happened — 25 of them in a 25-year
            /// record, so a 500-realization campaign draws each about twenty times and the weather carries
            /// far less variety than the realization count implies, none of it worse than the worst on file.
            /// A trigger boundary is meant to hold for longer than the record is, so the distribution has to
            /// be able to reach past it.
            ///
            /// <c>--historical-day-weather</c> goes back to replaying days, which is the better answer when
            /// the heavy fuels matter: only that path can run Nelson, and only Nelson knows the antecedent
            /// drying and gives every slope and aspect its own moisture.
            /// </remarks>
            public WeatherRasterPipeline.SamplingMode WeatherSampling
                = WeatherRasterPipeline.SamplingMode.FittedDistributions;

            /// <summary>Days per year in the pool the distributions are fitted to, highest FWI first.</summary>
            public int CandidateDaysPerYear = 10;

            /// <summary>
            /// The campaign's weather distribution report, computed at startup while the archive is in
            /// hand and written once the output folder has been reset. Null when it could not be computed.
            /// </summary>
            public List<WeatherStatisticsReport.VariableStatistics> WeatherStatistics;
            public string WeatherStatisticsHeader;

            /// <summary>
            /// Every realization's drawn weather, collected as they finish so the realized ensemble can be
            /// summarised against the distributions it was drawn from.
            /// </summary>
            /// <remarks>
            /// Guarded by its own lock: realizations run as concurrent tasks, and this is written from
            /// whichever one happens to finish. Accumulated and written once at the end rather than
            /// appended per realization, because a half-written line from two tasks interleaving in a CSV
            /// is worse than no CSV.
            /// </remarks>
            public readonly List<(int Index, ClimatologySampler.DrawnFireWeather Drawn, double M1, double M10, double M100)>
                DrawnWeather = new List<(int, ClimatologySampler.DrawnFireWeather, double, double, double)>();

            public readonly object DrawnWeatherLock = new object();

            /// <summary>
            /// Ceiling on bands generated per realization, each one a WindNinja solve. Beyond it the chain
            /// writes a single band, since a multi-band series shorter than the run is refused by ELMFIRE.
            /// </summary>
            public int MaxWeatherBands = 72;

            /// <summary>
            /// Aim every realization's wind from its own ignition at the WUI area's centroid, by forcing that
            /// direction into WindNinja before it solves.
            /// </summary>
            /// <remarks>
            /// On by default. A trigger boundary answers "how much warning does this community need", and a
            /// realization whose wind blows the fire away from town contributes nothing to that while still
            /// counting as a draw. Aiming the wind makes every realization a fire that is coming, and the
            /// ensemble's variety then lives in where it starts, how far it has to travel, and the day's own
            /// speed and moisture.
            ///
            /// It is not a physical day any more, and that is the trade: the wind direction is chosen rather
            /// than sampled. <c>--no-wind-to-wui</c> keeps the drawn day's own direction.
            ///
            /// Implies drawing the ignition on this side rather than ELMFIRE's, since the bearing needs it
            /// before the weather is written.
            /// </remarks>
            public bool WindToWui = true;

            /// <summary>The WUI area centroid the wind is aimed at, resolved once at startup.</summary>
            public double WuiCentreX, WuiCentreY;

            /// <summary>
            /// The namelist's <c>EDGEBUFFER</c>, which ELMFIRE will not ignite inside — so the sampler on this
            /// side must not either, or a drawn point would be one ELMFIRE would never have chosen.
            /// </summary>
            public double EdgeBufferMetres = 60.0;

            /// <summary>
            /// The folder the five weather stems are read from when it is not the shared inputs — set by
            /// <see cref="SetUpSingleBandWeather"/>. Null means the case's own rasters.
            /// </summary>
            public string WeatherDirectory;

            /// <summary>Settings for that chain; <c>Seed</c> here is offset by the realization index.</summary>
            public WeatherRasterPipeline.Options Weather = new WeatherRasterPipeline.Options();

            /// <summary>The case's master grid, read from the shared inputs' dem.tif once at startup
            /// rather than per realization.</summary>
            public MasterGrid WeatherGrid;


            public bool GenerateRealizations => ElmfireExe != null || ElmfireTemplate != null;
        }

        private static Options ParseArgs(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; ++i)
            {
                switch (args[i])
                {
                    case "--wui":         o.BaseWui = Next(args, ref i); break;
                    case "--dir":         o.RasterDir = Next(args, ref i); break;
                    case "--max":         int.TryParse(Next(args, ref i), out o.MaxRealizations); break;
                    case "--start":       int.TryParse(Next(args, ref i), out o.Start); break;
                    case "--pad":         int.TryParse(Next(args, ref i), out o.Pad); break;
                    case "--toa":         o.ToaPattern = Next(args, ref i); break;
                    case "--ros":         o.RosPattern = Next(args, ref i); break;
                    case "--sd":          o.SdPattern = Next(args, ref i); break;
                    case "--fi":          o.FiPattern = Next(args, ref i); break;
                    case "--preact":      o.PreactExe = Next(args, ref i); break;
                    case "--out":         o.OutPath = Next(args, ref i); break;
                    case "--diagnostics": o.DiagnosticsPath = Next(args, ref i); break;
                    case "--streak":      int.TryParse(Next(args, ref i), out o.Streak); break;
                    case "--tolerance":   double.TryParse(Next(args, ref i), NumberStyles.Any, CultureInfo.InvariantCulture, out o.Tolerance); break;
                    case "--parallel":    int.TryParse(Next(args, ref i), out o.Parallelism); break;
                    case "--resume":      o.Resume = true; break;
                    case "--resume-only": o.Resume = true; o.ResumeOnly = true; break;
                    case "--elmfire":          o.ElmfireExe = Next(args, ref i); break;
                    case "--elmfire-template": o.ElmfireTemplate = Next(args, ref i); break;
                    case "--elmfire-inputs":   o.ElmfireInputs = Next(args, ref i); break;
                    case "--gdal":             o.PathToGdal = Next(args, ref i); break;
                    case "--tstop":            double.TryParse(Next(args, ref i), NumberStyles.Any, CultureInfo.InvariantCulture, out o.TstopSeconds); break;
                    case "--seed":             int.TryParse(Next(args, ref i), out o.Seed); break;
                    case "--progress-json":    o.EmitProgressJson = true; break;
                    //Kept although it is now the default: campaigns and scripts pass it, and a flag that
                    //asks for what already happens should not be an error.
                    case "--realization-weather": o.RealizationWeather = true; break;
                    case "--shared-weather":      o.RealizationWeather = false; break;
                    case "--single-band-weather": o.SingleBandWeather = true; break;
                    //Kept although it is now the default, for the same reason --realization-weather is.
                    case "--fitted-weather":
                        o.WeatherSampling = WeatherRasterPipeline.SamplingMode.FittedDistributions; break;
                    case "--historical-day-weather":
                        o.WeatherSampling = WeatherRasterPipeline.SamplingMode.HistoricalDay; break;
                    case "--candidate-days-per-year": int.TryParse(Next(args, ref i), out o.CandidateDaysPerYear); break;
                    case "--live-fuel-moisture":     o.Weather.FitLiveFuelMoisture = true; break;
                    case "--no-live-fuel-moisture":  o.Weather.FitLiveFuelMoisture = false; break;
                    case "--wind-to-wui":         o.WindToWui = true; break;
                    case "--no-wind-to-wui":      o.WindToWui = false; break;
                    case "--max-weather-bands":   int.TryParse(Next(args, ref i), out o.MaxWeatherBands); break;
                    case "--weather-archive":     o.Weather.ArchiveCsvPath = Next(args, ref i); break;
                    case "--climatology-from":    o.Weather.ArchiveStartYear = int.Parse(Next(args, ref i)); break;
                    case "--climatology-to":      o.Weather.ArchiveEndYear = int.Parse(Next(args, ref i)); break;
                    case "--conditioning-days":   o.Weather.ConditioningDays = int.Parse(Next(args, ref i)); break;
                    case "--windninja":           o.Weather.WindNinjaExe = Next(args, ref i); break;
                    case "--wn-mesh":             o.Weather.WindNinjaMesh = Next(args, ref i); break;
                }
            }
            return o;
        }

        private static string Next(string[] args, ref int i)
        {
            return (i + 1 < args.Length) ? args[++i] : null;
        }

        public static void PrintUsage()
        {
            Console.WriteLine("  PREACTcli converge-trigger --wui <base.wui> --dir <rasterFolder> --max <N>");
            Console.WriteLine("    or, generating realizations instead of reading them from --dir:");
            Console.WriteLine("  PREACTcli converge-trigger --wui <base.wui> --max <N> \\");
            Console.WriteLine("      --elmfire <elmfire.exe> --elmfire-template <elmfire.data> --elmfire-inputs <inputsFolder>");
            Console.WriteLine("      [--tstop <seconds=259200, i.e. 3 days>] [--seed <n=12345>] [--gdal <gdalBinFolder>]");
            Console.WriteLine("      [--start <n=1>] [--pad <width=4>]");
            Console.WriteLine("      [--toa TOA_{i}.tif] [--ros ROS_{i}.tif] [--sd SD_{i}.tif] [--fi FI_{i}.tif]");
            Console.WriteLine("        ({i} = the index; '*' and '?' are allowed, e.g. time_of_arrival_{i}_*.tif.");
            Console.WriteLine("         An ELMFIRE dump's name ends in the run's stop time in seconds, so a pattern");
            Console.WriteLine("         spelling one out is retried with it wildcarded when no such file exists.)");
            Console.WriteLine("      [--preact <PREACT.exe>] [--out <probability.asc>] [--diagnostics <convergence.csv>]");
            Console.WriteLine("      [--streak <runs=20>] [--tolerance <fraction=0.02>] [--parallel <N=cpuCount>] [--resume] [--resume-only]");
            Console.WriteLine("      Weather. Each realization gets its own, into its own weather folder. By");
            Console.WriteLine("      default the parameters are drawn from normal distributions fitted to the");
            Console.WriteLine("      worst fire-weather days on record: one value each for wind speed,");
            Console.WriteLine("      temperature and humidity, held for the whole fire, so one band and one");
            Console.WriteLine("      WindNinja solve. Moisture comes from the drawn air by Simard's equilibrium");
            Console.WriteLine("      content - Nelson cannot run without real antecedent hours, so this mode has");
            Console.WriteLine("      no drying history and a domain-uniform moisture.");
            Console.WriteLine("      [--candidate-days-per-year <n=10>] pool the fit is taken over, per year");
            Console.WriteLine("        of record, highest FWI first. This is a severity dial as much as a");
            Console.WriteLine("        sample size: 1/year fits 'the worst day of the year', 10/year fits 'a");
            Console.WriteLine("        bad day', and on Mati that is 1.9 C cooler and 4 points more humid at");
            Console.WriteLine("        the mean. The spread barely moves. Fewer days is more severe but fits");
            Console.WriteLine("        on one sample per year of record.");
            Console.WriteLine("      Live fuel moisture is drawn too, from the NFDRS4 GSI model marched over the");
            Console.WriteLine("      record once, and written as LH/LW_MOISTURE_CONTENT per realization. Without");
            Console.WriteLine("      it those are two fixed namelist constants, the same in every realization.");
            Console.WriteLine("      These two are resampled as a pair from the candidate days rather than drawn");
            Console.WriteLine("      from a fitted normal: their values pile up on the fully-cured floor, where a");
            Console.WriteLine("      truncated normal loses its lower half and comes out wetter than the record.");
            Console.WriteLine("      [--no-live-fuel-moisture] leave the case's own LH/LW constants alone");
            Console.WriteLine("      Both fitted and realized statistics for every variable, primary and derived,");
            Console.WriteLine("      are written to _output/weather_distributions.csv and");
            Console.WriteLine("      _output/weather_realizations.csv.");
            Console.WriteLine("      [--historical-day-weather] replay whole days instead: draw one actual peak");
            Console.WriteLine("        fire-weather day per realization and run WindNinja per band plus Nelson");
            Console.WriteLine("        over the 20 real days before it. Every parameter is then consistent with");
            Console.WriteLine("        the others and the moisture varies with slope and aspect, but the ensemble");
            Console.WriteLine("        cannot contain a day worse than the record's worst.");
            Console.WriteLine("      [--shared-weather] instead read the one series the case was built with");
            Console.WriteLine("      [--single-band-weather] one band, held for the whole run: one WindNinja solve");
            Console.WriteLine("        per realization, and it lets a run be longer than its weather series -");
            Console.WriteLine("        ELMFIRE allows a one-band series at any duration, a longer one must cover");
            Console.WriteLine("        the run. Combines with either of the two above.");
            Console.WriteLine("      [--max-weather-bands <n=72>] each band is a WindNinja solve; past the cap");
            Console.WriteLine("        one band is written, since a series that does not cover the run is refused");
            Console.WriteLine("      Wind direction. By default each realization's wind is aimed from its own");
            Console.WriteLine("      ignition at the WUI area's centroid, forced into WindNinja before it solves,");
            Console.WriteLine("      so what ELMFIRE reads is that direction bent by the terrain. The ignition is");
            Console.WriteLine("      then drawn on this side (same mask, same weighting) since the bearing needs it.");
            Console.WriteLine("      [--no-wind-to-wui] keep the drawn day's own wind direction instead");
            Console.WriteLine("    Writes trigger_probability.asc plus ensemble_burn_probability and");
            Console.WriteLine("    ensemble_arrival_earliest/mean/p10/p50/p90, all beside the case's _output.");
        }
    }
}
