using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using PREACT.Math;
using PREACT.Population;
using PREACT.Tools;

namespace PREACT.Utility
{
    /// <summary>
    /// Builds a complete ELMFIRE case folder for a domain from scratch — the orchestration step
    /// docs/probabilistic-trigger-convergence.md lists as missing ("the pieces exist, wiring them
    /// into one 'build this case's topography' call doesn't yet").
    ///
    /// Given only a lower-left lat/lon and a domain size in metres, it downloads the DEM, chooses
    /// the local UTM zone, establishes the <see cref="MasterGrid"/> every other raster is snapped
    /// to, derives slope/aspect, fills the trivial adj/phi layers, warps whatever user-supplied
    /// fuel/canopy layers were handed to it onto the same grid, and writes an <c>elmfire.data</c>
    /// template the realization writer can then re-seed per Monte Carlo draw.
    ///
    /// **What it deliberately does not produce**, per the same doc's "User-supplied (not
    /// auto-sourced)" note: the fuel model and canopy rasters (outside the US there is no clean
    /// global LANDFIRE equivalent), and the building layers the ELMFIRE-WUINITY fork's urban
    /// spread model uses. Those are passed in via <see cref="UserRasters"/> and only reprojected;
    /// if they are absent the case still builds and ELMFIRE simply runs without them.
    /// </summary>
    public static class ElmfireCaseBuilder
    {
        /// <summary>Per-realization weather rasters, written later by <see cref="ElmfireRealizationWriter"/>.</summary>
        private const string InputsFolder = "inputs";

        /// <summary>An ignition in WGS84, with when it starts relative to the simulation start.</summary>
        public class IgnitionPoint
        {
            public Vector2d LatLon;
            public double TimeSeconds;
        }

        /// <summary>An ignition placed in the case's own coordinates, ready for the namelist.</summary>
        public class PlacedIgnition
        {
            public double X, Y, TimeSeconds;
        }

        public class Options
        {
            /// <summary>Case name; only used for messages and the default namelist filename.</summary>
            public string Name = "case";

            /// <summary>Domain's south-west corner, this codebase's (lat, lon) = (x, y) convention.</summary>
            public Vector2d LowerLeftLatLon;

            /// <summary>Domain size in metres, (east, north).</summary>
            public Vector2d DomainSizeMetres;

            /// <summary>Master-grid resolution in metres. 30 m matches Copernicus GLO-30 / SRTMGL1.</summary>
            public double CellSizeMetres = 30.0;

            /// <summary>Extra margin in metres added on every side of the requested domain.
            /// A fire is free to burn outside the evacuation domain, and clipping it at the domain
            /// edge would truncate the very spread the trigger boundary is measuring.</summary>
            public double PaddingMetres = 2000.0;

            public string OpenTopographyApiKey;
            public string DemType = OpenTopographyDownloader.DemTypeCopernicus30;

            /// <summary>An existing DEM covering the domain, used instead of downloading one.
            /// Any CRS — it is warped to the master grid like a downloaded DEM would be. Lets a
            /// case be built offline, without an OpenTopography key, or from a better local DEM
            /// than the global products offer.</summary>
            public string LocalDemPath;

            /// <summary>User-supplied source rasters, keyed by ELMFIRE input stem
            /// (<c>fbfm13</c>, <c>cc</c>, <c>ch</c>, <c>cbh</c>, <c>cbd</c>, <c>bldg_*</c>, ...).
            /// Any CRS/resolution — each is warped onto the master grid.</summary>
            public Dictionary<string, string> UserRasters = new Dictionary<string, string>();

            /// <summary>Stems in <see cref="UserRasters"/> that are categorical and must be
            /// resampled nearest-neighbour rather than bilinear (fuel model codes, mostly).
            /// The ignition mask is in here because interpolating it would invent fractional
            /// "partly ignitable" cells along the edge of an otherwise binary mask.</summary>
            public HashSet<string> CategoricalStems = new HashSet<string>
                { "fbfm13", "fbfm40", "bldg_fuel_model", "ignition_mask" };

            /// <summary>
            /// Baseline weather (ws/wd/m1/m10/m100) comes from the history-based chain in
            /// <see cref="WeatherRasterPipeline"/>: ERA5 climatology → a sampled historical peak
            /// fire-weather day → WindNinja terrain wind + Nelson dead fuel moisture. The namelist
            /// references those five stems unconditionally, so a case without them cannot run.
            /// Set to null to skip the chain and write uniform rasters from the fallback values.
            /// </summary>
            public WeatherRasterPipeline.Options Weather = new WeatherRasterPipeline.Options();

            /// <summary>Optional CSVs (fuel_models.csv, building_fuel_models.csv) copied verbatim.</summary>
            public List<string> CopyFiles = new List<string>();

            /// <summary>A graphical-fire-input file holding masks painted in Unity, and the
            /// landscape raster they were painted against (which supplies their georeferencing).
            /// Both are needed, or neither.</summary>
            public string PaintedMasksPath;
            public string PaintedMasksGridPath;

            /// <summary>Where the case is written. Must be empty or <see cref="Force"/> must be set.</summary>
            public string OutputDirectory;

            /// <summary>Permission to write into a folder that is not empty. Says nothing about
            /// overwriting individual layers - see <see cref="OverwriteExistingLayers"/>.</summary>
            public bool Force;

            /// <summary>
            /// Rewrite layers the case already has, instead of keeping them.
            /// </summary>
            /// <remarks>
            /// Off by default, because a prepared case is work: its rasters have been harmonized onto one
            /// grid, and some of them - canopy above all - cannot be re-derived from anything the builder
            /// has. Rebuilding unconditionally is what this used to do, and the canopy case was the worst of
            /// it: cc/ch/cbh/cbd not passed in on a given run were overwritten with the zero-fill default, so
            /// a case with real canopy silently lost it and modelled surface fire only.
            ///
            /// On means "build this case again from scratch", which is what a changed domain or cell size
            /// needs, since every raster then has to be re-cut to the new grid.
            /// </remarks>
            public bool OverwriteExistingLayers;

            /// <summary>Zero the ignition mask wherever the fuel model cannot burn, so ELMFIRE's
            /// random ignition never lands on water, urban or bare ground.</summary>
            public bool RestrictIgnitionToBurnableFuel = true;

            /// <summary>Extra non-burnable fuel codes beyond the 91-99 block, for a fuel model
            /// whose unburnable classes sit elsewhere (14 is Anderson FBFM13's).</summary>
            public HashSet<int> NonBurnableFuelCodes = new HashSet<int> { 14 };

            /// <summary>
            /// Explicit ignitions, in WGS84 - the scenario's <c>[IgnitionPoint]</c> sections, as placed
            /// in the ignition point editor. Given in latitude and longitude rather than in case
            /// coordinates because the case's CRS is not known until its DEM has been warped: the
            /// transform is <see cref="ApplyIgnitionPoints"/>'s job, and doing it anywhere else is what
            /// put zone-35 eastings into a zone-34 case by hand.
            ///
            /// These win over a painted initial ignition, which is a brush stroke reduced to its
            /// centroid, and they switch <c>RANDOM_IGNITIONS</c> off.
            /// </summary>
            public List<IgnitionPoint> IgnitionPoints = new List<IgnitionPoint>();

            /// <summary>Simulation start, for the namelist's CURRENT_YEAR / BAND_ONE_HOUR_OF_YEAR.</summary>
            public DateTime StartDateTime = new DateTime(2020, 7, 1, 12, 0, 0);

            public double SimulationTstopSeconds = 72000.0;

            /// <summary>Copied into &amp;MISCELLANEOUS PATH_TO_GDAL so ELMFIRE's own shell-outs
            /// resolve to a known-good GDAL rather than whatever is first on PATH.</summary>
            public string PathToGdal;

            /// <summary>Reports progress; may be null.</summary>
            public Action<string> Log;
        }

        public class Result
        {
            public MasterGrid Grid;
            public string InputsDirectory;
            public string NamelistPath;
            public List<string> Written = new List<string>();
            public List<string> Skipped = new List<string>();

            /// <summary>Layers ELMFIRE requires that nobody supplied, filled with a neutral default.</summary>
            public List<string> Defaulted = new List<string>();

            /// <summary>Layers the case already had, kept rather than rebuilt.</summary>
            public List<string> Reused = new List<string>();

            /// <summary>Cells an ignition may be placed in after the burnable-fuel restriction; 0 if not applied.</summary>
            public int IgnitableCells;

            /// <summary>Anything the builder had to work around, surfaced so it is not silent.</summary>
            public List<string> Fallbacks = new List<string>();

            /// <summary>Every explicit ignition, in the case's own coordinates - from the scenario's
            /// ignition points, or failing that from a painted initial ignition. Empty means ELMFIRE
            /// draws its own from the ignition mask.</summary>
            public List<PlacedIgnition> Ignitions = new List<PlacedIgnition>();

            public bool HasIgnitionPoint => Ignitions.Count > 0;
            public double IgnitionX => Ignitions.Count > 0 ? Ignitions[0].X : 0.0;
            public double IgnitionY => Ignitions.Count > 0 ? Ignitions[0].Y : 0.0;

            /// <summary>The exported painted WUI area, for k-PERIL's WuiAreaFile; null if none was painted.</summary>
            public string WuiAreaFile;

            /// <summary>What the history-based weather chain actually managed to use, and where it fell back.</summary>
            public WeatherRasterPipeline.Result Weather;
        }

        public static async Task<Result> Build(Options o)
        {
            if (string.IsNullOrEmpty(o.OutputDirectory)) throw new ArgumentException("OutputDirectory is required.");
            if (o.CellSizeMetres <= 0) throw new ArgumentException("CellSizeMetres must be positive.");

            void Log(string m) => o.Log?.Invoke(m);

            string inputs = Path.Combine(o.OutputDirectory, InputsFolder);
            PrepareOutputDirectory(o, inputs);

            //---------------------------------------------------------------- 1. DEM
            //Padded so the fire can grow past the evacuation domain's edge; ELMFIRE reads the
            //domain/CRS straight off this file's georeferencing (no &COMPUTATIONAL_DOMAIN group).
            (Vector2d southWest, Vector2d northEast) = PaddedBounds(o);
            Log($"Domain (padded {o.PaddingMetres:F0} m): {southWest.x:F5},{southWest.y:F5} -> {northEast.x:F5},{northEast.y:F5}");

            var result = new Result { InputsDirectory = inputs };

            //Whether a layer has to be produced at all. A case that already has one is left alone unless the
            //caller asked for a rebuild - see Options.OverwriteExistingLayers for why that is the default.
            bool Needed(string stem)
            {
                string path = Path.Combine(inputs, stem + ".tif");
                if (o.OverwriteExistingLayers || !File.Exists(path))
                {
                    return true;
                }

                result.Reused.Add(stem);
                result.Written.Add(stem);
                return false;
            }

            //---------------------------------------------------------------- 1-2. DEM and master grid
            //The grid comes from dem.tif, which ELMFIRE also reads the domain and CRS from, so when the case
            //already has one it is the grid - there is nothing to download, warp or decide. This is also why
            //a prepared case needs no OpenTopography key.
            string demPath = Path.Combine(inputs, "dem.tif");
            MasterGrid grid;

            if (!Needed("dem"))
            {
                grid = MasterGrid.FromRasterFile(demPath);
                Log($"Reusing the case's own DEM: {grid.Header.Ncols}x{grid.Header.Nrows} @ "
                    + $"{grid.Header.CellSize:F1} m, {grid.Epsg}.");
            }
            else
            {
                string rawDem = Path.Combine(inputs, "dem_source.tif");
                if (!string.IsNullOrEmpty(o.LocalDemPath))
                {
                    if (!File.Exists(o.LocalDemPath)) throw new FileNotFoundException("No such DEM: " + o.LocalDemPath);
                    Log($"Using local DEM {o.LocalDemPath}.");
                    File.Copy(o.LocalDemPath, rawDem, overwrite: true);
                }
                else if (File.Exists(rawDem))
                {
                    //A downloaded DEM is reused even under --force: --force is about writing into a
                    //non-empty case folder, not about re-fetching data that cannot have changed.
                    Log("Reusing the previously downloaded DEM.");
                }
                else
                {
                    if (string.IsNullOrEmpty(o.OpenTopographyApiKey))
                    {
                        throw new Exception(
                            "A DEM is needed and there is none in the case, so one has to be downloaded - but no "
                            + "OpenTopography key was found. Put it in "
                            + "WUInity/Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt or set "
                            + "OPENTOPOGRAPHY_API_KEY, or point the case at a DEM you already have.");
                    }

                    Log($"Downloading {o.DemType} DEM from OpenTopography...");
                    await OpenTopographyDownloader.Download(southWest, northEast, o.OpenTopographyApiKey, rawDem, o.DemType);
                }

                Log($"Warping DEM to the local UTM zone at {o.CellSizeMetres:F0} m...");
                grid = RasterHarmonizer.BuildUtmMasterGrid(
                    rawDem, demPath,
                    southWest.x, southWest.y, northEast.x, northEast.y,
                    o.CellSizeMetres);
                Log($"Master grid: {grid.Header.Ncols}x{grid.Header.Nrows} @ {grid.Header.CellSize:F1} m, {grid.Epsg}");
                result.Written.Add("dem");
            }

            result.Grid = grid;

            //---------------------------------------------------------------- 3. Slope / aspect
            bool needSlope = Needed("slp");
            bool needAspect = Needed("asp");
            if (needSlope || needAspect)
            {
                Log("Deriving slope and aspect (Horn's method)...");
                float[,] elevation = AscRaster.ReadGeoTiff(demPath, out AscRaster.Header demHeader, out bool demOk);
                if (!demOk || elevation == null)
                {
                    throw new Exception("Could not read the warped DEM back: " + demPath);
                }

                SlopeAspect.Compute(elevation, grid.Header.CellSize, out float[,] slope, out float[,] aspect);
                if (needSlope)
                {
                    GeoTiffRasterWriter.WriteBand(grid, slope, Path.Combine(inputs, "slp.tif"));
                    result.Written.Add("slp");
                }
                if (needAspect)
                {
                    GeoTiffRasterWriter.WriteBand(grid, aspect, Path.Combine(inputs, "asp.tif"));
                    result.Written.Add("asp");
                }
            }

            //---------------------------------------------------------------- 4. adj / phi
            //Both are just 1.0 everywhere, same shape/CRS as the DEM (WildfireAV's
            //makePhiAndAdjFiles.py) - they are not related to ignition.
            foreach (string stem in new[] { "adj", "phi" })
            {
                if (!Needed(stem)) continue;
                GeoTiffRasterWriter.WriteConstant(grid, 1.0f, Path.Combine(inputs, stem + ".tif"));
                result.Written.Add(stem);
            }

            //---------------------------------------------------------------- 5. User rasters
            foreach (KeyValuePair<string, string> kv in o.UserRasters)
            {
                string stem = kv.Key;
                string source = kv.Value;

                if (string.IsNullOrEmpty(source) || !File.Exists(source))
                {
                    Log($"  {stem}: source not found, skipping ({source}).");
                    result.Skipped.Add(stem);
                    continue;
                }

                //A raster handed in explicitly still does not overwrite one the case has, unless a rebuild
                //was asked for: the one on the grid is the harmonized copy, and re-warping from the source
                //can only lose to it.
                if (!Needed(stem))
                {
                    Log($"  {stem}: the case already has it; keeping it.");
                    continue;
                }

                string method = o.CategoricalStems.Contains(stem) ? "near" : "bilinear";
                Log($"  {stem}: warping onto the master grid ({method}).");
                RasterHarmonizer.WarpToGrid(source, Path.Combine(inputs, stem + ".tif"), grid, method);
                result.Written.Add(stem);
            }

            //---------------------------------------------------------------- 5b. Canopy defaults
            //ELMFIRE treats CC/CH/CBH/CBD as required inputs and refuses to start without them
            //("is not specified and is a required input"), so leaving them out of the namelist
            //produces a case that builds cleanly and then cannot run. Canopy is also precisely the
            //layer that has no global source, so defaulting it to zero - no canopy fuel, hence
            //surface fire only, no crown fire - is what makes an arbitrary domain runnable at all.
            //Needed() is what keeps this from destroying real canopy: it used to test only whether this run
            //had warped the stem, so a case whose cc/ch/cbh/cbd were prepared earlier had them overwritten
            //with zeros the next time the builder ran without being handed them again - and zero canopy is a
            //silent switch from crown fire to surface fire only.
            foreach (string stem in new[] { "cc", "ch", "cbh", "cbd" })
            {
                if (result.Written.Contains(stem) || !Needed(stem)) continue;
                GeoTiffRasterWriter.WriteConstant(grid, 0.0f, Path.Combine(inputs, stem + ".tif"));
                result.Written.Add(stem);
                result.Defaulted.Add(stem);
            }

            if (result.Defaulted.Count > 0)
            {
                Log($"  canopy: {string.Join(", ", result.Defaulted)} not supplied, defaulted to zero " +
                    "(surface fire only - no crown fire will be modelled).");
            }

            //---------------------------------------------------------------- 5c. Painted masks
            //Run before the ignition-mask default below, so a painted ignition area is used rather
            //than being overwritten by the ignite-anywhere fallback.
            ApplyPaintedMasks(o, result, inputs, grid, Log);

            //---------------------------------------------------------------- 5d. Ignition points
            //After the painted masks, because an explicitly placed point supersedes the centroid of a
            //painted stroke.
            ApplyIgnitionPoints(o, result, grid, Log);

            //---------------------------------------------------------------- 6. Ignition mask
            //Only generated when the user did not supply one: an all-ones mask lets ELMFIRE's
            //RANDOM_IGNITIONS place a fire anywhere in the domain, which is the neutral default.
            string ignitionMask = Path.Combine(inputs, "ignition_mask.tif");
            if (!File.Exists(ignitionMask))
            {
                Log("  ignition_mask: none supplied, writing an all-ones (ignite-anywhere) mask.");
                GeoTiffRasterWriter.WriteConstant(grid, 1.0f, ignitionMask);
                result.Written.Add("ignition_mask");
            }

            if (o.RestrictIgnitionToBurnableFuel)
            {
                RestrictIgnitionMask(o, result, inputs, grid, Log);
            }

            //---------------------------------------------------------------- 7. Baseline weather
            //Runs after the user rasters so Nelson can shade its sticks with the canopy cover
            //layer if one was supplied.
            //
            //All five together, or none: they are one weather series split across five files, and a mixture
            //of a case's own wind and freshly sampled moisture is not a description of any day. The wind is
            //also what k-PERIL derives its spread ellipse from, so silently redrawing it changes a trigger
            //boundary computed against the fire that came before.
            string[] weatherStems = { "ws", "wd", "m1", "m10", "m100" };
            bool haveAllWeather = true;
            foreach (string stem in weatherStems)
            {
                if (!File.Exists(Path.Combine(inputs, stem + ".tif"))) { haveAllWeather = false; break; }
            }

            if (haveAllWeather && !o.OverwriteExistingLayers)
            {
                Log("  weather: the case already has ws/wd/m1/m10/m100; keeping them.");
                result.Reused.AddRange(weatherStems);
                result.Written.AddRange(weatherStems);
            }
            else
            {
                Log("Building baseline weather (climatology -> WindNinja -> Nelson)...");

                WeatherRasterPipeline.Options w = o.Weather ?? new WeatherRasterPipeline.Options();
                w.Grid = grid;
                w.InputsDirectory = inputs;
                w.LatLon = new Vector2d(0.5 * (southWest.x + northEast.x), 0.5 * (southWest.y + northEast.y));
                w.Log = o.Log;
                if (string.IsNullOrEmpty(w.ArchiveCsvPath))
                {
                    //Beside the case rather than inside inputs/: it is a cache shared by every
                    //realization, not one of ELMFIRE's inputs.
                    w.ArchiveCsvPath = Path.Combine(o.OutputDirectory, "climatology", $"{o.Name}_era5_hourly.csv");
                }

                result.Weather = await WeatherRasterPipeline.Run(w);
                result.Written.AddRange(weatherStems);
            }

            //---------------------------------------------------------------- 8. Loose files
            foreach (string f in o.CopyFiles)
            {
                if (string.IsNullOrEmpty(f) || !File.Exists(f)) { Log($"  copy: not found, skipping ({f})."); continue; }
                File.Copy(f, Path.Combine(inputs, Path.GetFileName(f)), overwrite: true);
                Log($"  copy: {Path.GetFileName(f)}");
            }

            //---------------------------------------------------------------- 9. Namelist
            //Kept when the case has one, for the same reason the rasters are: the namelist is where the
            //physics is tuned, and a generated one is a starting point rather than an improvement on a
            //template someone has worked on. Overwriting it was the most expensive thing this could quietly
            //undo.
            result.NamelistPath = Path.Combine(o.OutputDirectory, "elmfire.data");
            if (File.Exists(result.NamelistPath) && !o.OverwriteExistingLayers)
            {
                Log($"Keeping the case's own namelist, {Path.GetFileName(result.NamelistPath)}.");
                result.Reused.Add("elmfire.data");
            }
            else
            {
                File.WriteAllLines(result.NamelistPath, BuildNamelist(o, result));
                Log($"Wrote {result.NamelistPath}");
            }

            Directory.CreateDirectory(Path.Combine(o.OutputDirectory, "outputs"));
            Directory.CreateDirectory(Path.Combine(o.OutputDirectory, "scratch"));

            return result;
        }

        private static void PrepareOutputDirectory(Options o, string inputs)
        {
            if (Directory.Exists(o.OutputDirectory))
            {
                bool empty = Directory.GetFileSystemEntries(o.OutputDirectory).Length == 0;
                if (!empty && !o.Force)
                {
                    throw new IOException(
                        $"{o.OutputDirectory} already exists and is not empty. Pass --force to build into it anyway.");
                }
            }

            Directory.CreateDirectory(inputs);
        }

        /// <summary>
        /// Converts the requested (lat/lon + metres) domain into the padded lat/lon bounding box
        /// the DEM request needs, reusing the same flat-earth metres/degrees conversion the
        /// population tools already use to interpret <c>DomainSize</c>.
        /// </summary>
        private static (Vector2d southWest, Vector2d northEast) PaddedBounds(Options o)
        {
            Vector2d padded = new Vector2d(
                o.DomainSizeMetres.x + 2.0 * o.PaddingMetres,
                o.DomainSizeMetres.y + 2.0 * o.PaddingMetres);

            //SizeToDegrees returns (lonDegrees, latDegrees) for a size given as (east, north).
            Vector2d padDeg = LocalGPWData.SizeToDegrees(o.LowerLeftLatLon, new Vector2d(o.PaddingMetres, o.PaddingMetres));
            Vector2d spanDeg = LocalGPWData.SizeToDegrees(o.LowerLeftLatLon, padded);

            Vector2d southWest = new Vector2d(o.LowerLeftLatLon.x - padDeg.y, o.LowerLeftLatLon.y - padDeg.x);
            Vector2d northEast = new Vector2d(southWest.x + spanDeg.y, southWest.y + spanDeg.x);
            return (southWest, northEast);
        }

        /// <summary>
        /// Writes a template covering only what the builder can actually guarantee. It is a
        /// starting point, not a tuned scenario: the doc's contract is that "the user will
        /// fine-tune the ELMFIRE input template; this pipeline only has to produce grid-aligned
        /// inputs and invoke the runner". Per-realization keys (weather stems, SEED, ignition)
        /// are left for <see cref="ElmfireRealizationWriter"/> to patch in.
        /// </summary>
        /// <summary>
        /// Brings masks painted in Unity into the case: the random-ignition area becomes
        /// <c>ignition_mask.tif</c>, the WUI area becomes <c>wui_area.tif</c> (what k-PERIL's
        /// <c>WuiAreaFile</c> should point at), and a painted initial ignition becomes an explicit
        /// <c>X_IGN</c>/<c>Y_IGN</c> point in the namelist instead of a random draw.
        ///
        /// A painted mask that is entirely empty is treated as "not painted" rather than as "ignite
        /// nowhere" — an all-false mask is what an untouched painter produces, and honouring it
        /// literally would give ELMFIRE no valid ignition cell at all.
        /// </summary>
        private static void ApplyPaintedMasks(Options o, Result result, string inputs, MasterGrid grid, Action<string> log)
        {
            if (string.IsNullOrEmpty(o.PaintedMasksPath)) return;

            if (!File.Exists(o.PaintedMasksPath))
            {
                result.Fallbacks.Add("painted masks: file not found, " + o.PaintedMasksPath);
                log("  painted: file not found, skipping.");
                return;
            }

            if (string.IsNullOrEmpty(o.PaintedMasksGridPath) || !File.Exists(o.PaintedMasksGridPath))
            {
                result.Fallbacks.Add("painted masks: no landscape raster given for their georeferencing");
                log("  painted: --painted-grid is required (the landscape raster the painting was done against); skipping.");
                return;
            }

            try
            {
                PaintedMaskExporter.Masks masks = PaintedMaskExporter.Load(o.PaintedMasksPath);
                MasterGrid painted = MasterGrid.FromRasterFile(o.PaintedMasksGridPath);

                if (masks.Any(masks.RandomIgnition))
                {
                    PaintedMaskExporter.Export(masks.RandomIgnition, masks, painted, grid, Path.Combine(inputs, "ignition_mask.tif"));
                    if (!result.Written.Contains("ignition_mask")) result.Written.Add("ignition_mask");
                    log($"  painted: ignition area -> ignition_mask.tif ({masks.Count(masks.RandomIgnition)} painted cells).");
                }

                if (masks.Any(masks.WuiArea))
                {
                    PaintedMaskExporter.Export(masks.WuiArea, masks, painted, grid, Path.Combine(inputs, "wui_area.tif"));
                    result.Written.Add("wui_area");
                    result.WuiAreaFile = Path.Combine(inputs, "wui_area.tif");
                    log($"  painted: WUI area -> wui_area.tif ({masks.Count(masks.WuiArea)} painted cells) - " +
                        "point the .wui's [kPERIL] WuiAreaFile at it.");
                }

                if (masks.Any(masks.InitialIgnition) &&
                    PaintedMaskExporter.TryGetIgnitionPoint(masks.InitialIgnition, masks, painted, grid, out double ix, out double iy))
                {
                    result.Ignitions.Add(new PlacedIgnition { X = ix, Y = iy, TimeSeconds = 0.0 });
                    log($"  painted: initial ignition -> X_IGN/Y_IGN ({ix:F1}, {iy:F1}), random ignition disabled.");
                }
            }
            catch (Exception e)
            {
                result.Fallbacks.Add("painted masks: " + e.Message);
                log("  painted: could not apply (" + e.Message + ").");
            }
        }

        /// <summary>
        /// Places the scenario's ignition points in the case's own coordinates.
        ///
        /// The transform is the whole point of this method and the one thing the hand-built Mati case got
        /// wrong: its ignition was written as <c>(232043.4, 4215113.9)</c>, which is that point measured
        /// in UTM zone 35 while the case is in zone 34. Mati sits on the 24 E boundary, so the same
        /// ground is at easting 232 km in one zone and 758 km in the other - the ignition was half a zone
        /// outside the domain, and ELMFIRE said nothing about it. So the CRS is taken from the case grid
        /// that now exists rather than from the scenario, from a raster, or from the zone the domain
        /// corner happens to fall in.
        ///
        /// A point outside the domain is dropped rather than clamped: clamping would ignite a fire
        /// somewhere nobody asked for and the run would look successful.
        /// </summary>
        private static void ApplyIgnitionPoints(Options o, Result result, MasterGrid grid, Action<string> log)
        {
            if (o.IgnitionPoints == null || o.IgnitionPoints.Count == 0)
            {
                return;
            }

            if (string.IsNullOrEmpty(grid.Epsg))
            {
                result.Fallbacks.Add("ignition points: the case grid has no resolvable CRS, so they could not be placed");
                log("  ignition: the case grid has no resolvable CRS; ignition points skipped.");
                return;
            }

            var placed = new List<PlacedIgnition>();
            foreach (IgnitionPoint point in o.IgnitionPoints)
            {
                if (!CrsTransform.TryWgs84To(grid.Epsg, point.LatLon.x, point.LatLon.y, out double x, out double y))
                {
                    result.Fallbacks.Add($"ignition point {point.LatLon.x:F5},{point.LatLon.y:F5}: could not be measured in {grid.Epsg}");
                    log($"  ignition: {point.LatLon.x:F5},{point.LatLon.y:F5} could not be measured in {grid.Epsg}; dropped.");
                    continue;
                }

                if (x < grid.XMin || x > grid.XMax || y < grid.YMin || y > grid.YMax)
                {
                    //Said with both numbers, because the useful part is how far outside it is: a few
                    //hundred metres is a point placed just off the edge, and hundreds of kilometres is a
                    //scenario whose coordinates were never in this zone to begin with.
                    string outside = $"ignition point {point.LatLon.x:F5},{point.LatLon.y:F5} is at {x:F0},{y:F0} in "
                                     + $"{grid.Epsg}, outside the case domain ({grid.XMin:F0}..{grid.XMax:F0}, "
                                     + $"{grid.YMin:F0}..{grid.YMax:F0})";
                    result.Fallbacks.Add(outside + "; dropped");
                    log("  ignition: " + outside + "; dropped. Increase --padding, or move the point.");
                    continue;
                }

                placed.Add(new PlacedIgnition { X = x, Y = y, TimeSeconds = point.TimeSeconds });
                log($"  ignition: {point.LatLon.x:F5},{point.LatLon.y:F5} -> {x:F1}, {y:F1} in {grid.Epsg}"
                    + (point.TimeSeconds > 0.0 ? $" at t = {point.TimeSeconds:F0} s." : "."));
            }

            if (placed.Count == 0)
            {
                return;
            }

            if (result.Ignitions.Count > 0)
            {
                //Both were given, which is not an error - a painted initial ignition is easy to leave
                //behind - but only one of them can be the ignition, so which one is worth saying.
                log($"  ignition: {placed.Count} placed ignition point(s) used instead of the painted initial ignition.");
                result.Ignitions.Clear();
            }

            result.Ignitions.AddRange(placed);
        }

        /// <summary>
        /// Zeroes the ignition mask wherever the fuel model says nothing can burn, so ELMFIRE's own
        /// <c>RANDOM_IGNITIONS</c> cannot place a fire there.
        ///
        /// This is the cheap half of WildfireAV's <c>_snap_to_valid_fuel</c>, and it is preferable
        /// to snapping after the fact because it leaves ignition placement with ELMFIRE rather than
        /// taking it over in C#. It is not cosmetic: the padded Mati domain is largely sea, and with
        /// an all-ones mask ELMFIRE happily ignited open water, ran to completion, exited 0, wrote
        /// all four output rasters and reported a fire area of 0.0 acres. The driver counts that as
        /// a successful realization and folds an empty trigger boundary into the probability raster,
        /// so the failure is both silent and statistically corrupting.
        /// </summary>
        private static void RestrictIgnitionMask(Options o, Result result, string inputs, MasterGrid grid, Action<string> log)
        {
            string fuelPath = Path.Combine(inputs, "fbfm13.tif");
            string maskPath = Path.Combine(inputs, "ignition_mask.tif");
            if (!File.Exists(fuelPath) || !File.Exists(maskPath)) return;

            float[,] fuel = AscRaster.ReadGeoTiff(fuelPath, out AscRaster.Header _, out bool fuelOk);
            float[,] mask = AscRaster.ReadGeoTiff(maskPath, out AscRaster.Header _, out bool maskOk);
            if (!fuelOk || !maskOk || fuel == null || mask == null) return;

            int nx = grid.Header.Ncols;
            int ny = grid.Header.Nrows;
            if (fuel.GetLength(0) != nx || fuel.GetLength(1) != ny) return;

            int before = 0, after = 0;
            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y)
                {
                    bool wasIgnitable = mask[x, y] > 0f;
                    if (wasIgnitable) ++before;

                    if (wasIgnitable && !IsBurnable(fuel[x, y], o.NonBurnableFuelCodes))
                    {
                        mask[x, y] = 0f;
                    }

                    if (mask[x, y] > 0f) ++after;
                }
            }

            if (after == 0)
            {
                //Writing this mask would leave ELMFIRE with nowhere to ignite at all, which is a
                //worse failure than the one being prevented - leave the original alone and say so.
                result.Fallbacks.Add("ignition mask: no burnable cell in the domain, left unrestricted");
                log("  ignition_mask: WARNING no cell has burnable fuel; leaving the mask unrestricted.");
                return;
            }

            GeoTiffRasterWriter.WriteBand(grid, mask, maskPath);
            result.IgnitableCells = after;
            log($"  ignition_mask: restricted to burnable fuel, {after} of {before} ignitable cells kept " +
                $"({100.0 * after / System.Math.Max(1, before):F0} %).");
        }

        /// <summary>
        /// Whether a fuel code can carry fire. The 91-99 block is non-burnable in both Anderson
        /// FBFM13 and Scott &amp; Burgan FBFM40 (urban, snow, agriculture, water, barren), as is 0
        /// and anything negative — which covers NoData, the case that matters most here since a
        /// clipped domain is padded with it.
        /// </summary>
        private static bool IsBurnable(float code, HashSet<int> nonBurnable)
        {
            if (float.IsNaN(code) || code <= 0f) return false;
            int c = (int)System.Math.Round(code);
            if (c >= 91 && c <= 99) return false;
            return !nonBurnable.Contains(c);
        }

        /// <summary>
        /// The ELMFIRE-WUINITY fork's urban-spread inputs. All five have to be present for the
        /// building spread model to be switched on — it is a complete set or nothing.
        /// </summary>
        private static readonly (string Stem, string Key)[] BuildingLayers =
        {
            ("bldg_area_avg", "BLDG_AREA_FILENAME"),
            ("bldg_separation_distance", "BLDG_SEPARATION_DIST_FILENAME"),
            ("bldg_nonburnable_frac", "BLDG_NONBURNABLE_FRAC_FILENAME"),
            ("bldg_footprint_frac", "BLDG_FOOTPRINT_FRAC_FILENAME"),
            ("bldg_fuel_model", "BLDG_FUEL_MODEL_FILENAME"),
        };

        private static string[] BuildNamelist(Options o, Result r)
        {
            var l = new List<string>();
            string C(double v) => v.ToString(CultureInfo.InvariantCulture);
            bool Has(string stem) => r.Written.Contains(stem);

            int hourOfYear = (int)(o.StartDateTime - new DateTime(o.StartDateTime.Year, 1, 1)).TotalHours;

            l.Add($"! ELMFIRE case '{o.Name}', generated by ElmfireCaseBuilder.");
            l.Add("! Static layers are on the master grid defined by dem.tif; ELMFIRE reads the");
            l.Add("! domain and CRS from that file's own georeferencing.");
            l.Add("");
            l.Add("&INPUTS");
            l.Add("FUELS_AND_TOPOGRAPHY_DIRECTORY = './inputs'");
            l.Add("DEM_FILENAME                   = 'dem'");
            l.Add("SLP_FILENAME                   = 'slp'");
            l.Add("ASP_FILENAME                   = 'asp'");
            l.Add("ADJ_FILENAME                   = 'adj'");
            l.Add("PHI_FILENAME                   = 'phi'");

            //Canopy layers are optional: ELMFIRE only needs them for crown fire, and outside the
            //US they often simply do not exist for the domain.
            //All of these are required by ELMFIRE; the canopy set is guaranteed present by the
            //zero-fill above, so only the fuel model can still be genuinely missing.
            foreach ((string stem, string key) in new[]
                     { ("fbfm13", "FBFM_FILENAME"), ("cc", "CC_FILENAME"), ("ch", "CH_FILENAME"),
                       ("cbh", "CBH_FILENAME"), ("cbd", "CBD_FILENAME") })
            {
                if (Has(stem)) l.Add($"{key,-30} = '{stem}'");
                else l.Add($"! {key} - no {stem} raster supplied; ELMFIRE will refuse to start");
            }

            l.Add("DT_METEOROLOGY                 = 3600.0");
            l.Add("WEATHER_DIRECTORY              = './inputs'");
            l.Add("WS_FILENAME                    = 'ws'");
            l.Add("WD_FILENAME                    = 'wd'");
            l.Add("M1_FILENAME                    = 'm1'");
            l.Add("M10_FILENAME                   = 'm10'");
            l.Add("M100_FILENAME                  = 'm100'");
            l.Add("USE_CONSTANT_LH                = .TRUE.");
            l.Add("USE_CONSTANT_LW                = .TRUE.");
            l.Add("LH_MOISTURE_CONTENT            = 60.0");
            l.Add("LW_MOISTURE_CONTENT            = 90.0");
            //WS_FILENAME is always mph unless told otherwise; WS_AT_10M only says the raster is
            //10 m wind rather than ELMFIRE's 20 ft default, it does not change the unit.
            l.Add("WS_AT_10M                      = .TRUE.");
            l.Add("IGNITION_MASK_FILENAME         = 'ignition_mask'");

            if (Has("barriers"))
            {
                l.Add("USE_BARRIERS                   = .TRUE.");
                l.Add("BARRIER_FILENAME               = 'barriers'");
            }

            foreach ((string stem, string key) in BuildingLayers)
            {
                if (Has(stem)) l.Add($"{key,-30} = '{stem}'");
            }

            l.Add("/");
            l.Add("");
            l.Add("&OUTPUTS");
            l.Add("OUTPUTS_DIRECTORY    = './outputs'");
            l.Add("DTDUMP               = 3600.0");
            l.Add("DUMP_TIME_OF_ARRIVAL = .TRUE.");
            l.Add("DUMP_SPREAD_RATE     = .TRUE.");
            l.Add("DUMP_SPREAD_DIRECTION= .TRUE.");
            l.Add("DUMP_FLIN            = .TRUE.");
            l.Add("CONVERT_TO_GEOTIFF   = .TRUE.");
            //AscImport reads spread rate in m/min; without this ELMFIRE dumps ft/min.
            l.Add("SPREAD_RATE_IN_M     = .TRUE.");
            l.Add("/");
            l.Add("");
            l.Add("&TIME_CONTROL");
            l.Add($"CURRENT_YEAR          = {o.StartDateTime.Year.ToString(CultureInfo.InvariantCulture)}");
            l.Add($"BAND_ONE_HOUR_OF_YEAR = {hourOfYear.ToString(CultureInfo.InvariantCulture)}");
            l.Add("SIMULATION_DT         = 5.0");
            l.Add("SIMULATION_DTMAX      = 300.0");
            l.Add("TARGET_CFL            = 0.4");
            l.Add($"SIMULATION_TSTOP      = {C(o.SimulationTstopSeconds)}");
            l.Add("/");
            l.Add("");
            l.Add("&MONTE_CARLO");
            l.Add("METEOROLOGY_BAND_START         = 1");
            l.Add("METEOROLOGY_BAND_STOP          = 1");
            l.Add("METEOROLOGY_BAND_SKIP_INTERVAL = 1");
            l.Add("NUM_METEOROLOGY_TIMES          = 1");
            //One member per invocation: the realization, not the ensemble member, is this
            //pipeline's unit of parallelism (the driver re-seeds and reruns per realization).
            l.Add("NUM_ENSEMBLE_MEMBERS           = 1");
            l.Add("EDGEBUFFER                     = 30");

            if (r.HasIgnitionPoint)
            {
                //An explicitly painted ignition and a random draw are mutually exclusive: leaving
                //RANDOM_IGNITIONS on would have ELMFIRE ignore the point it was just given.
                l.Add("RANDOM_IGNITIONS               = .FALSE.");
                l.Add("USE_IGNITION_MASK              = .FALSE.");
            }
            else
            {
                l.Add("RANDOM_IGNITIONS               = .TRUE.");
                l.Add("USE_IGNITION_MASK              = .TRUE.");
                l.Add("RANDOM_IGNITIONS_TYPE          = 1");
            }
            l.Add("");
            l.Add("! Fill in RASTER_TO_PERTURB blocks here to make the ensemble vary in weather /");
            l.Add("! moisture as well as ignition location - see the Mati template for the shape.");
            l.Add("/");
            l.Add("");
            l.Add("&SIMULATOR");
            l.Add("MODE           = 1");
            l.Add("CLEAN_SCRATCH  = .TRUE.");
            if (r.HasIgnitionPoint)
            {
                //Fortran's own 1-based indexing, since these are namelist array elements.
                l.Add($"NUM_IGNITIONS  = {r.Ignitions.Count.ToString(CultureInfo.InvariantCulture)}");
                for (int i = 0; i < r.Ignitions.Count; ++i)
                {
                    PlacedIgnition ign = r.Ignitions[i];
                    string n = (i + 1).ToString(CultureInfo.InvariantCulture);
                    l.Add($"X_IGN({n})       = {C(ign.X)}");
                    l.Add($"Y_IGN({n})       = {C(ign.Y)}");
                    //Two decimals rather than the shortest round trip: a whole number of seconds would be
                    //written "0", and a namelist REAL is clearer read as one.
                    l.Add($"T_IGN({n})       = {ign.TimeSeconds.ToString("0.00", CultureInfo.InvariantCulture)}");
                }
            }
            l.Add("/");
            l.Add("");

            //Only switched on with the complete set of building layers - a partial set makes
            //ELMFIRE read a raster that was never written.
            bool allBuildingLayers = true;
            foreach ((string stem, string _) in BuildingLayers)
            {
                if (!Has(stem)) { allBuildingLayers = false; break; }
            }

            if (allBuildingLayers)
            {
                l.Add("&WUI");
                l.Add("USE_BLDG_SPREAD_MODEL                 = .TRUE.");
                l.Add("USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS = .FALSE.");
                l.Add("BLDG_SPREAD_MODEL_TYPE                = 3");
                l.Add("INTERFACE_MODEL_TYPE                  = 2");
                l.Add("/");
                l.Add("");
            }

            l.Add("&MISCELLANEOUS");
            if (!string.IsNullOrEmpty(o.PathToGdal)) l.Add($"PATH_TO_GDAL = '{o.PathToGdal.Replace('\\', '/')}/'");
            l.Add("SCRATCH      = './scratch'");
            if (File.Exists(Path.Combine(r.InputsDirectory, "building_fuel_models.csv")))
            {
                l.Add("BUILDING_FUEL_MODEL_FILE = 'building_fuel_models.csv'");
            }
            l.Add("/");

            return l.ToArray();
        }
    }
}
