using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PREACT.Input;
using PREACT.Utility;

namespace PREACTcli
{
    /// <summary>
    /// <c>PREACTcli build-case</c> - builds the ELMFIRE case for a scenario: the same build the GUI's Prepare data
    /// does, from the same <c>.wui</c> keys.
    /// </summary>
    /// <remarks>
    /// The scenario is read with the real parser and turned into builder options by
    /// <see cref="ElmfireCoupling.CreateBuildOptions"/>, the function the GUI uses. The CLI used to parse the
    /// <c>.wui</c> by hand and ignored <c>[ELMFIRE] SimulationTstop*</c>, <c>CellSizeMetres</c>,
    /// <c>PaddingMetres</c> and the painted areas, so a CLI-built case had 20 bands instead of the scenario's, no
    /// <c>wui_area.tif</c>, and a campaign refused it at startup. Flags override single settings on top.
    /// </remarks>
    internal static class BuildCase
    {
        /// <summary>ELMFIRE input stems a source raster can be given for, each as <c>--&lt;stem&gt; &lt;path&gt;</c>.</summary>
        private static readonly string[] UserRasterStems =
        {
            "fbfm40", "fbfm13", "cc", "ch", "cbh", "cbd",
            "bldg_area_avg", "bldg_separation_distance", "bldg_nonburnable_frac",
            "bldg_footprint_frac", "bldg_fuel_model",
            "ignition_mask", "barriers",
        };

        public static int Run(string[] args)
        {
            string wui = null, output = null, apiKey = null, demType = null, localDem = null, gdal = null;
            string windNinja = null, mesh = null, vegetation = null, archive = null, canopy = null;
            string painted = null, paintedGrid = null;
            double? cellSize = null, padding = null, hours = null;
            int? fromYear = null, toYear = null, conditioning = null, burnFrom = null, burnTo = null, seed = null;
            DateTime? weatherDate = null;
            bool noClimatology = false, rebuild = false;
            double? wind = null, windDir = null, m1 = null, m10 = null, m100 = null;
            var copies = new List<string>();
            var rasters = new Dictionary<string, string>(StringComparer.Ordinal);

            var parser = new CliArgs()
                .Value("--wui", v => wui = v)
                .Value("--out", v => output = v)
                .Double("--cellsize", v => cellSize = v, 1.0, 1000.0)
                .Double("--padding", v => padding = v, 0.0, 100000.0)
                .Value("--hours", v =>
                {
                    if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
                        throw new ArgumentException($"--hours takes a number of hours, not '{v}'.");
                    string problem = CampaignLayout.ValidateFireHours(h);
                    if (problem != null) throw new ArgumentException("--hours: " + problem + ".");
                    hours = h;
                })
                .Value("--dem-type", v => demType = v)
                .Value("--dem", v => localDem = v)
                .Value("--api-key", v => apiKey = v)
                .Value("--gdal", v => gdal = v)
                .Value("--windninja", v => windNinja = v)
                .Value("--wn-mesh", v => mesh = v)
                .Value("--wn-vegetation", v => vegetation = v)
                .Value("--weather-archive", v => archive = v)
                .Int("--climatology-from", v => fromYear = v, 1940, 2100)
                .Int("--climatology-to", v => toYear = v, 1940, 2100)
                .Int("--conditioning-days", v => conditioning = v, 1, 365)
                .Int("--burning-from", v => burnFrom = v, 0, 23)
                .Int("--burning-to", v => burnTo = v, 0, 23)
                .Int("--weather-seed", v => seed = v)
                .Value("--weather-date", v =>
                {
                    if (!DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime d))
                        throw new ArgumentException($"--weather-date takes a date (yyyy-MM-dd), not '{v}'.");
                    weatherDate = d;
                })
                .Switch("--no-climatology", () => noClimatology = true)
                .Double("--wind", v => wind = v, 0.0, 100.0)
                .Double("--wind-dir", v => windDir = v, 0.0, 360.0)
                .Double("--m1", v => m1 = v, 0.0, 300.0)
                .Double("--m10", v => m10 = v, 0.0, 300.0)
                .Double("--m100", v => m100 = v, 0.0, 300.0)
                .Value("--copy", v => copies.Add(v))
                .Value("--canopy-dataset", v => canopy = v)
                .Value("--painted", v => painted = v)
                .Value("--painted-grid", v => paintedGrid = v)
                .Switch("--rebuild", () => rebuild = true)
                .Retired("--tstop", "the fire duration is --hours now, in hours (default: the scenario's "
                                    + "[ELMFIRE] SimulationTstopHours).")
                .Retired("--force", "a case folder is always built into; --rebuild replaces the layers it keeps.");
            foreach (string stem in UserRasterStems)
            {
                string s = stem;
                parser.Value("--" + s, v => rasters[s] = v);
            }

            try
            {
                parser.Parse(args);
                if (string.IsNullOrEmpty(wui)) throw new ArgumentException("--wui is required.");
            }
            catch (ArgumentException e)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                PrintUsage();
                return 2;
            }

            string wuiPath = Path.GetFullPath(wui);
            if (!File.Exists(wuiPath))
            {
                Console.Error.WriteLine("ERROR: no such .wui file: " + wuiPath);
                return 1;
            }

            PREACTInput input = PREACTInput.LoadFromDisk(wuiPath, out bool _);
            if (input?.Simulation == null || input.WildfireModule?.ElmfireInput == null)
            {
                string why = PREACTInput.Requirements.Where(r => r.Critical).Select(r => r + ": " + r.Message).FirstOrDefault();
                Console.Error.WriteLine("ERROR: could not read the scenario's [Simulation] and [ELMFIRE] sections"
                                        + (why != null ? " (" + why + ")" : "") + ".");
                return 1;
            }

            ElmfireInput settings = input.WildfireModule.ElmfireInput;
            if (cellSize.HasValue) settings.CellSizeMetres = cellSize.Value;
            if (padding.HasValue) settings.PaddingMetres = padding.Value;
            if (hours.HasValue) settings.SimulationTstopHours = hours.Value;
            if (rebuild) settings.RebuildExistingLayers = true;
            if (gdal != null) settings.PathToGdal = gdal;
            if (windNinja != null) settings.WindNinjaExe = Path.GetFullPath(windNinja);

            string hoursProblem = CampaignLayout.ValidateFireHours(settings.SimulationTstopHours);
            if (hoursProblem != null)
            {
                Console.Error.WriteLine("ERROR: [ELMFIRE] SimulationTstopHours: " + hoursProblem + ". Pass --hours.");
                return 1;
            }

            string caseDir = output != null ? Path.GetFullPath(output) : ElmfireCoupling.CaseDirectoryPath(input.RootFolder, settings);
            ElmfireCaseBuilder.Options o = ElmfireCoupling.CreateBuildOptions(input, settings, caseDir, Console.WriteLine);

            o.PathToGdal ??= GdalTools.FindBinDirectory();
            if (apiKey != null) o.OpenTopographyApiKey = apiKey;
            if (demType != null) o.DemType = demType;
            if (localDem != null) o.LocalDemPath = Path.GetFullPath(localDem);
            if (canopy != null) o.CanopyDatasetFolder = Path.GetFullPath(canopy);
            if (painted != null) o.PaintedMasksPath = Path.GetFullPath(painted);
            if (paintedGrid != null) o.PaintedMasksGridPath = Path.GetFullPath(paintedGrid);
            foreach (string copy in copies) o.CopyFiles.Add(Path.GetFullPath(copy));
            foreach (KeyValuePair<string, string> kv in rasters) o.UserRasters[kv.Key] = Path.GetFullPath(kv.Value);

            if (mesh != null) o.Weather.WindNinjaMesh = mesh;
            if (vegetation != null) o.Weather.WindNinjaVegetation = vegetation;
            if (archive != null) o.Weather.ArchiveCsvPath = Path.GetFullPath(archive);
            if (fromYear.HasValue) o.Weather.ArchiveStartYear = fromYear.Value;
            if (toYear.HasValue) o.Weather.ArchiveEndYear = toYear.Value;
            if (conditioning.HasValue) o.Weather.ConditioningDays = conditioning.Value;
            if (burnFrom.HasValue) o.Weather.BurningPeriodStartHour = burnFrom.Value;
            if (burnTo.HasValue) o.Weather.BurningPeriodEndHour = burnTo.Value;
            if (seed.HasValue) o.Weather.Seed = seed.Value;
            if (weatherDate.HasValue) o.Weather.ForceDate = weatherDate.Value;
            if (noClimatology) o.Weather.UseClimatology = false;
            if (wind.HasValue) o.Weather.FallbackWindSpeedMps = wind.Value;
            if (windDir.HasValue) o.Weather.FallbackWindDirectionDeg = windDir.Value;
            if (m1.HasValue) o.Weather.FallbackM1Percent = m1.Value;
            if (m10.HasValue) o.Weather.FallbackM10Percent = m10.Value;
            if (m100.HasValue) o.Weather.FallbackM100Percent = m100.Value;

            Console.WriteLine($"Case '{o.Name}': lower-left {o.LowerLeftLatLon.x:F6},{o.LowerLeftLatLon.y:F6}, domain "
                              + $"{o.DomainSizeMetres.x:F0}x{o.DomainSizeMetres.y:F0} m padded by {o.PaddingMetres:F0} m, "
                              + $"{o.CellSizeMetres:F0} m cells, {settings.SimulationTstopHours:0.##} h of fire from "
                              + $"{o.StartDateTime:yyyy-MM-dd HH:mm}, {o.IgnitionPoints.Count} ignition point(s), into {caseDir}");
            if (o.PathToGdal != null) Console.WriteLine("GDAL tools: " + o.PathToGdal);

            try
            {
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();
                PrintResult(wuiPath, input.RootFolder, r);

                //A non-zero exit for a case that cannot legitimately be run, so a script that builds and then runs
                //stops here. The rasters stay on disk to inspect.
                if (r.Validation != null && !r.Validation.Ok)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("The case is not internally consistent and should not be run:");
                    foreach (ElmfireCaseValidator.Problem p in r.Validation.Fatal) Console.Error.WriteLine("  " + p);
                    return 1;
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("build-case failed: " + Program.Describe(e));
                return 1;
            }
        }

        private static void PrintResult(string wuiPath, string root, ElmfireCaseBuilder.Result r)
        {
            Console.WriteLine();
            Console.WriteLine($"Case built in {Path.GetDirectoryName(r.NamelistPath)}");
            Console.WriteLine($"  grid      {r.Grid.Header.Ncols}x{r.Grid.Header.Nrows} @ {r.Grid.Header.CellSize:F1} m ({r.Grid.Epsg}), "
                              + $"{r.Grid.XMin:F0},{r.Grid.YMin:F0} to {r.Grid.XMax:F0},{r.Grid.YMax:F0}"
                              + (r.GridRebuilt ? " - cut again: the old grid did not cover the padded domain" : ""));
            Console.WriteLine($"  layers    {string.Join(", ", r.Written.Distinct())}");
            if (r.Carried.Count > 0) Console.WriteLine($"  carried   {string.Join(", ", r.Carried)} (from the old grid)");
            if (r.Skipped.Count > 0) Console.WriteLine($"  missing   {string.Join(", ", r.Skipped)}");
            if (r.Reused.Count > 0) Console.WriteLine($"  kept      {string.Join(", ", r.Reused.Distinct())} (--rebuild replaces them)");
            if (r.Defaulted.Count > 0) Console.WriteLine($"  defaulted {string.Join(", ", r.Defaulted)} to zero (surface fire only)");
            Console.WriteLine(r.FuelStem == null
                ? "  WARNING   no fuel model raster (--fbfm40 or --fbfm13); ELMFIRE will refuse to start on this case."
                : $"  fuel      {r.FuelStem}");
            if (r.HasIgnitionPoint)
            {
                Console.WriteLine($"  ignition  {r.Ignitions.Count} fixed point(s) in {r.Grid.Epsg}:");
                foreach (ElmfireCaseBuilder.PlacedIgnition ign in r.Ignitions)
                {
                    Console.WriteLine($"            {ign.X:F1}, {ign.Y:F1} at t = {ign.TimeSeconds:F0} s");
                }
            }
            foreach (string f in r.Fallbacks) Console.WriteLine($"  !         {f}");
            Console.WriteLine($"  namelist  {r.NamelistPath}" + (r.KeptNamelistPath != null ? $" (the previous one kept as {Path.GetFileName(r.KeptNamelistPath)})" : ""));

            if (r.Weather != null)
            {
                string day = r.Weather.Day.HasValue
                    ? $"{r.Weather.Day.Value.Date:yyyy-MM-dd} (FWI {r.Weather.Day.Value.Fwi:F1}, 1 of {r.Weather.AnnualMaximaCount} annual peaks)"
                    : "none (uniform weather)";
                Console.WriteLine($"  weather   {day}, {r.Weather.BandCount} band(s)");
                Console.WriteLine($"            wind {r.Weather.MeanWindSpeedMph:F1} mph mean at 10 m"
                                  + (r.Weather.WindNinjaUsed ? " (WindNinja, terrain-resolved)" : " (uniform)"));
                Console.WriteLine($"            dead moisture {r.Weather.MeanM1Percent:F1}/{r.Weather.MeanM10Percent:F1}/{r.Weather.MeanM100Percent:F1} %"
                                  + (r.Weather.NelsonUsed ? " (Nelson, per-cell)" : " (uniform)"));
                foreach (string f in r.Weather.Fallbacks) Console.WriteLine($"            ! {f}");
            }

            //The CLI does not rewrite the scenario (a round trip through the writer is not faithful). The GUI's
            //Prepare data records these itself; from here they are said.
            string inputs = r.InputsDirectory;
            Console.WriteLine();
            Console.WriteLine("For the scenario (the GUI's Prepare data sets these itself):");
            Console.WriteLine($"  [Landscape] ElevationFile={CampaignLayout.RelativeForWui(root, ElmfireStems.Tif(inputs, ElmfireStems.Dem))}");
            Console.WriteLine($"  [Landscape] SlopeFile={CampaignLayout.RelativeForWui(root, ElmfireStems.Tif(inputs, ElmfireStems.Slope))}");
            Console.WriteLine($"  [Landscape] AspectFile={CampaignLayout.RelativeForWui(root, ElmfireStems.Tif(inputs, ElmfireStems.Aspect))}");
            if (r.WuiAreaFile != null)
            {
                Console.WriteLine($"  [kPERIL] WuiAreaFile={CampaignLayout.RelativeForWui(root, r.WuiAreaFile)}");
            }
            Console.WriteLine();
            Console.WriteLine("Run a campaign against it with:");
            Console.WriteLine($"  PREACTcli converge-trigger --wui {wuiPath} --max 200");
        }

        public static void PrintUsage()
        {
            Console.WriteLine("  PREACTcli build-case --wui <scenario.wui> [--out <case dir>] [options]");
            Console.WriteLine("      Builds the scenario's ELMFIRE case exactly as the GUI's Prepare data does: the domain, cell size,");
            Console.WriteLine("      padding, fire duration, source layers, ignition points and painted areas all come from the .wui.");
            Console.WriteLine("      --out <dir>        where to build (default: the scenario's [ELMFIRE] CaseDirectory)");
            Console.WriteLine("      --hours <h>        fire duration, 1-240 h (default: [ELMFIRE] SimulationTstopHours)");
            Console.WriteLine("      --cellsize <m>     --padding <m>      override [ELMFIRE] CellSizeMetres / PaddingMetres");
            Console.WriteLine("      --dem <file>       a local DEM for the grid (it should cover the padded domain)");
            Console.WriteLine("      --dem-type <t>     COP30 (default) | COP90 | SRTMGL1 | SRTMGL3, for a download");
            Console.WriteLine("      --api-key <k>      OpenTopography key (else $OPENTOPOGRAPHY_API_KEY, else Unity Resources)");
            Console.WriteLine("      --gdal <bin>       GDAL bin directory for the namelist's PATH_TO_GDAL");
            Console.WriteLine("      --copy <file>      copy a loose file (e.g. building_fuel_models.csv) into inputs/");
            Console.WriteLine("      --painted <file>   --painted-grid <raster>   override the scenario's painted areas / legacy paint grid");
            Console.WriteLine("      --rebuild          replace every layer the case already has");
            Console.WriteLine("      baseline weather (ERA5 climatology -> WindNinja -> Nelson):");
            Console.WriteLine("      --weather-archive <csv>        the ERA5 archive (default <case>/climatology/<Name>_era5_hourly.csv)");
            Console.WriteLine("      --climatology-from/-to <year>  archive range (default 2000 to the archive's own last year)");
            Console.WriteLine("      --weather-date <date>          use this historical day instead of sampling one");
            Console.WriteLine("      --weather-seed <n>             seeds which peak day is drawn");
            Console.WriteLine("      --conditioning-days <n>        Nelson spin-up window (default 20)");
            Console.WriteLine("      --burning-from/-to <0-23>      burning period the moisture minimum is reported over");
            Console.WriteLine("      --windninja <exe>  --wn-mesh coarse|medium|fine  --wn-vegetation grass|brush|trees");
            Console.WriteLine("      --no-climatology               uniform rasters from the fallbacks below");
            Console.WriteLine("      --wind <m/s> --wind-dir <deg> --m1/--m10/--m100 <%>   fallbacks where a stage cannot run");
            Console.WriteLine("      --canopy-dataset <dir>         FIRE-RES canopy rasters for any of cc/ch/cbh/cbd not named");
            Console.WriteLine("      source rasters, warped onto the grid (override the scenario's [ELMFIRE] *File keys):");
            Console.WriteLine("        " + string.Join(", ", Array.ConvertAll(UserRasterStems, s => "--" + s + " <tif>")));
        }
    }
}
