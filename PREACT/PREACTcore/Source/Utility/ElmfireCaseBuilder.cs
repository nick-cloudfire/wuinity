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
        /// <summary>
        /// Where every raster the namelist references lives, relative to the case. Shared and read-only during
        /// a campaign: realizations get their own <c>outputs/</c> and <c>scratch/</c>, and their own weather
        /// folder when weather varies, but they all read one copy of the terrain and fuel.
        /// </summary>
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

            /// <summary>
            /// A folder holding the FIRE-RES pan-European canopy rasters, used for whichever of
            /// <c>cc</c>/<c>ch</c>/<c>cbh</c>/<c>cbd</c> were not named in <see cref="UserRasters"/>.
            /// </summary>
            /// <remarks>
            /// Canopy is the one layer group with no global source the builder can download, and the reason a
            /// case outside the United States has had zero canopy — hence surface fire only — unless someone
            /// supplied four rasters by hand. This is that source for Europe. See
            /// <see cref="FireResCanopy"/> for the unit trap it brings with it.
            /// </remarks>
            public string CanopyDatasetFolder;

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

            /// <summary>
            /// The modelling choices the generated namelist carries - the scenario's <c>[ElmfireNamelist]</c>
            /// section. Left at its defaults, which are ELMFIRE's own except where the case builder's own
            /// rasters make another value the only correct one, so a CLI build needs no scenario to produce
            /// a runnable namelist.
            /// </summary>
            public PREACT.Input.ElmfireNamelistInput Namelist = new PREACT.Input.ElmfireNamelistInput();

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

            /// <summary>
            /// True when the canopy layers came from a source that stores real units rather than LANDFIRE's
            /// scaled integers, which decides three namelist flags. See <see cref="FireResCanopy"/>.
            /// </summary>
            public bool CanopyInRealUnits;

            /// <summary>Layers ELMFIRE requires that nobody supplied, filled with a neutral default.</summary>
            public List<string> Defaulted = new List<string>();

            /// <summary>Layers the case already had, kept rather than rebuilt.</summary>
            public List<string> Reused = new List<string>();

            /// <summary>The fuel model stem the case carries and the namelist references
            /// (<c>fbfm40</c> or <c>fbfm13</c>), or null if it has neither - which ELMFIRE cannot run.</summary>
            public string FuelStem;

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

            /// <summary>
            /// Whether the case's rasters actually agree with each other, checked once the case is complete.
            /// Null only if validation could not be attempted at all.
            /// </summary>
            public ElmfireCaseValidator.Report Validation;
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
            //
            //The canopy dataset is folded in here rather than handled separately, because a continent-sized
            //GeoTIFF and a hand-supplied raster need exactly the same treatment: warp onto the master grid,
            //scrub non-finite values, keep what the case already has. Added *before* the loop so an explicitly
            //named raster still wins - naming one is a deliberate choice and the dataset is the fallback.
            AddCanopyDatasetLayers(o, result, Log);

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
                string warped = Path.Combine(inputs, stem + ".tif");
                Log($"  {stem}: warping onto the master grid ({method}).");
                RasterHarmonizer.WarpToGrid(source, warped, grid, method);

                //Scrubbed here, on the way in, because ELMFIRE traps on floating-point invalid and one NaN
                //aborts the whole run with a message naming an unrelated line. These external products are
                //exactly where NaN comes from - several declare no nodata value at all, so voids arrive as NaN
                //rather than as anything a reader would skip. 0 means "none of this here" for every layer
                //ingested through this path: no canopy, no buildings, not in the mask.
                long scrubbed = RasterScrubber.ReplaceNonFinite(warped, 0f, Log);
                if (scrubbed > 0)
                {
                    long cells = (long)grid.Header.Ncols * grid.Header.Nrows;
                    Log($"    {stem}: {scrubbed} non-finite cell(s) replaced with 0 "
                        + $"({100.0 * scrubbed / System.Math.Max(1, cells):F1} % of the grid).");
                    result.Fallbacks.Add($"{stem}: {scrubbed} non-finite cells replaced with 0");
                }

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
                WarnIfKeptWindIsUniform(Path.Combine(inputs, "ws.tif"), Log);
            }
            else
            {
                Log("Building baseline weather (climatology -> WindNinja -> Nelson)...");

                WeatherRasterPipeline.Options w = o.Weather ?? new WeatherRasterPipeline.Options();
                w.Grid = grid;
                w.InputsDirectory = inputs;

                //The weather series has to span the fire and be read at the interval it was written at.
                //DT_METEOROLOGY comes from the namelist settings so the two cannot disagree - a series
                //written hourly and read at any other interval is silently stretched in time.
                w.SimulationStartDateTime = o.StartDateTime;
                w.SimulationTstopSeconds = o.SimulationTstopSeconds;
                if (o.Namelist != null && o.Namelist.DT_METEOROLOGY > 0)
                {
                    w.SecondsPerBand = o.Namelist.DT_METEOROLOGY;
                }

                //WindNinja is not resolved here: the pipeline probes for it itself when none is named, so no
                //caller can forget to and quietly get a uniform wind field.

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
            //Resolved once every layer is in place, so it reflects the case as it now stands whether the
            //namelist is about to be written or kept.
            result.FuelStem = ResolveStem(result, FuelStems);

            result.NamelistPath = Path.Combine(o.OutputDirectory, "elmfire.data");
            if (File.Exists(result.NamelistPath) && !o.OverwriteExistingLayers)
            {
                Log($"Keeping the case's own namelist, {Path.GetFileName(result.NamelistPath)}.");
                result.Reused.Add("elmfire.data");
                WarnIfNamelistLacksFuelModel(result, Log);
            }
            else
            {
                File.WriteAllLines(result.NamelistPath, BuildNamelist(o, result));
                Log($"Wrote {result.NamelistPath}");
            }

            Directory.CreateDirectory(Path.Combine(o.OutputDirectory, "outputs"));
            Directory.CreateDirectory(Path.Combine(o.OutputDirectory, "scratch"));

            //---------------------------------------------------------------- 9b. Provenance
            //What each raster was made from, which the namelist cannot say: its *_FILENAME keys name stems
            //inside inputs/ ('cc'), not the source those stems were warped out of. So once a case was built the
            //provenance was gone, and the editor could not show - or restore - which layers a case had come
            //from. Written beside the namelist so the case describes itself.
            WriteSourceManifest(o, result, Log);

            //---------------------------------------------------------------- 10. Validate
            //Last, so it sees the case as ELMFIRE will: every layer written or kept, on whatever grid it
            //actually ended up on. Reported rather than thrown - a case with a misregistered optional layer is
            //still worth having on disk to look at, and the run is where refusing belongs.
            result.Validation = ElmfireCaseValidator.Validate(inputs, grid, result.FuelStem, OptionalStems(), Log);

            return result;
        }

        /// <summary>
        /// Layers a case may or may not carry: validated for registration when present, not missed when absent.
        /// </summary>
        /// <summary>The manifest's filename, beside the namelist in the case root.</summary>
        public const string SourceManifestName = "case_sources.txt";

        /// <summary>
        /// Records where each of the case's rasters came from, so the case can be read back into the editor.
        /// </summary>
        /// <remarks>
        /// The namelist cannot serve this purpose and never could: <c>CC_FILENAME = 'cc'</c> says the case holds
        /// a <c>cc.tif</c>, not that it was warped out of a pan-European canopy raster on another drive. Those
        /// are different facts, and only the second one can be edited and rebuilt from.
        ///
        /// Deliberately a flat <c>Key=Value</c> text file rather than anything structured: it is written once
        /// per build, read once per import, and being obvious in a text editor is worth more here than being
        /// parseable by something else. Paths are recorded as they were given, absolute or not, because that is
        /// what would have to be typed back in.
        /// </remarks>
        private static void WriteSourceManifest(Options o, Result result, Action<string> log)
        {
            try
            {
                var lines = new List<string>
                {
                    "# Where this case's rasters came from, written by the case builder.",
                    "# Read back by the scenario editor to restore the source layer fields.",
                    "# The namelist cannot hold this: its *_FILENAME keys name stems inside inputs/,",
                    "# not the sources those stems were warped out of.",
                    "",
                    "Name=" + o.Name,
                    "CellSizeMetres=" + o.CellSizeMetres.ToString(CultureInfo.InvariantCulture),
                    "PaddingMetres=" + o.PaddingMetres.ToString(CultureInfo.InvariantCulture),
                    "CanopyInRealUnits=" + (result.CanopyInRealUnits ? "true" : "false"),
                };

                if (!string.IsNullOrWhiteSpace(o.CanopyDatasetFolder))
                {
                    lines.Add("CanopyDatasetFolder=" + o.CanopyDatasetFolder);
                }

                if (!string.IsNullOrWhiteSpace(o.LocalDemPath))
                {
                    lines.Add("LocalDemPath=" + o.LocalDemPath);
                }

                lines.Add("");
                lines.Add("# stem = source raster it was warped from");

                foreach (KeyValuePair<string, string> kv in o.UserRasters)
                {
                    lines.Add(kv.Key + "=" + kv.Value);
                }

                //What the case *holds*, which is not the same list. UserRasters is only what this build warped
                //in - a layer the case already had is kept, not re-warped, so it never appears there. The first
                //manifest written for the Mati case therefore listed four canopy rasters and no fuel and no
                //buildings, even though the case has all of them. Recorded separately because the two facts are
                //different: one says where a layer came from, the other says the layer is there at all.
                lines.Add("");
                lines.Add("# layers present in inputs/, whether this build made them or kept them");
                lines.Add("Present=" + string.Join(",", PresentStems(result.InputsDirectory)));

                File.WriteAllLines(Path.Combine(o.OutputDirectory, SourceManifestName), lines);
                log($"  sources: recorded in {SourceManifestName}, so the editor can read this case back.");
            }
            catch (Exception e)
            {
                //Never fatal. The case is complete and runnable without it; only the editor's ability to
                //restore the source fields is lost, and that is not worth failing a build over.
                log($"  sources: could not write {SourceManifestName} ({e.Message}).");
            }
        }

        /// <summary>
        /// Every ELMFIRE input stem the case actually holds a raster for.
        /// </summary>
        /// <remarks>
        /// Read off the folder rather than tracked through the build, because "what this build produced" and
        /// "what the case contains" are different lists and the second is the one worth recording: a layer the
        /// case already had is kept rather than re-warped, so it appears in neither <c>Written</c> nor
        /// <c>UserRasters</c>. That is why the first manifest written for a case with fuel and five building
        /// layers listed only the four canopy rasters this build happened to ingest.
        /// </remarks>
        private static IEnumerable<string> PresentStems(string inputsDirectory)
        {
            var stems = new List<string>();

            if (string.IsNullOrEmpty(inputsDirectory) || !Directory.Exists(inputsDirectory))
            {
                return stems;
            }

            foreach (string stem in KnownStems())
            {
                if (File.Exists(Path.Combine(inputsDirectory, stem + ".tif")))
                {
                    stems.Add(stem);
                }
            }

            return stems;
        }

        /// <summary>Every stem this builder or the external pipeline can put in a case.</summary>
        private static IEnumerable<string> KnownStems()
        {
            //Terrain and the constants the builder always writes.
            yield return "dem";
            yield return "slp";
            yield return "asp";
            yield return "adj";
            yield return "phi";

            //Weather, the five that must agree on band count.
            yield return "ws";
            yield return "wd";
            yield return "m1";
            yield return "m10";
            yield return "m100";

            //Fuel, either standard.
            foreach (string s in FuelStems) yield return s;

            //Canopy.
            yield return "cc";
            yield return "ch";
            yield return "cbh";
            yield return "cbd";

            //Masks, barriers and the suppression difficulty index.
            yield return "ignition_mask";
            yield return "barriers";
            yield return "sdi";

            //Exposure, ignition-rate and pyrome layers ELMFIRE can read but nothing here produces.
            yield return "land_value";
            yield return "population_density";
            yield return "real_estate_value";
            yield return "erc";
            yield return "pyromes";

            //Buildings, under either the descriptive or the pipeline's own names.
            foreach ((string _, string[] aliases) in BuildingLayers)
            {
                foreach (string alias in aliases) yield return alias;
            }
        }

        /// <summary>
        /// Adds the FIRE-RES canopy layers to the rasters to be ingested, and records that they are in real
        /// units so the namelist can say so.
        /// </summary>
        /// <remarks>
        /// Does not overwrite a stem the caller named explicitly: the dataset is where canopy comes from when
        /// nothing better was given, not an override. Continuous layers, so they warp bilinearly like any other
        /// canopy raster — the <c>CategoricalStems</c> set does not contain them, which is already correct.
        /// </remarks>
        private static void AddCanopyDatasetLayers(Options o, Result result, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(o.CanopyDatasetFolder))
            {
                return;
            }

            if (!FireResCanopy.TryResolve(o.CanopyDatasetFolder, out List<KeyValuePair<string, string>> layers))
            {
                log($"  canopy: no FIRE-RES canopy rasters in {o.CanopyDatasetFolder} "
                    + $"(expected {FireResCanopy.ExpectedFileNames()}); canopy will be defaulted to zero.");
                result.Fallbacks.Add("canopy dataset: no usable files in " + o.CanopyDatasetFolder);
                return;
            }

            var taken = new List<string>();
            foreach (KeyValuePair<string, string> layer in layers)
            {
                if (o.UserRasters.ContainsKey(layer.Key)) continue; //named explicitly; that wins
                o.UserRasters[layer.Key] = layer.Value;
                taken.Add(layer.Key);
            }

            //Recorded even when every layer was overridden, because it is the dataset's units that decide the
            //namelist flags and a partial override still leaves FIRE-RES layers in the case.
            result.CanopyInRealUnits = taken.Count > 0;

            if (taken.Count > 0)
            {
                log($"  canopy: {string.Join(", ", taken)} from the FIRE-RES dataset, clipped out of the "
                    + "pan-European rasters onto this case's grid.");
                log("    real units (m, kg/m3, percent), so CH_TIMES_10 / CBH_TIMES_10 / CBD_TIMES_100 are "
                    + "forced off - ELMFIRE's own defaults assume LANDFIRE's scaled integers.");
            }
        }

        private static IEnumerable<string> OptionalStems()
        {
            yield return "cc";
            yield return "ch";
            yield return "cbh";
            yield return "cbd";
            yield return "ignition_mask";

            foreach ((string key, string[] stems) in BuildingLayers)
            {
                foreach (string stem in stems) yield return stem;
            }
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
        /// are left for the campaign driver (PREACTcli converge-trigger) to patch in.
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
            //The same stem the namelist will reference, so the restriction is applied against the fuel
            //model the run will actually use - reading a fixed fbfm13.tif meant this quietly did nothing
            //on every case whose fuel raster is fbfm40.tif.
            string fuelStem = ResolveStem(result, FuelStems);
            if (fuelStem == null) return;

            string fuelPath = Path.Combine(inputs, fuelStem + ".tif");
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
        /// Says so when a kept namelist has no fuel model set but the case has one to offer.
        /// </summary>
        /// <remarks>
        /// A namelist the case already has is kept rather than rebuilt, which is right - it is where the
        /// physics is tuned. But it also means a namelist generated by an earlier, wrong version of this
        /// builder survives the fix to that version. That is how it went here: the fuel stem was hardcoded
        /// to <c>fbfm13</c>, cases carrying <c>fbfm40.tif</c> got the key written as a comment, and simply
        /// correcting the builder left every such case still broken and still silent about it.
        /// </remarks>
        /// <summary>
        /// Says so when the wind field the case is keeping is spatially flat.
        ///
        /// A case built before WindNinja was reachable carries a uniform <c>ws.tif</c>, and because the
        /// weather layers are kept by default, installing WindNinja and rebuilding changes nothing at all —
        /// the build reports success, the file stays flat, and the only symptom appears much later as a
        /// circular spread ellipse in k-PERIL. That is a confusing distance between cause and effect, so the
        /// build says it here, where <c>RebuildExistingLayers</c> is the answer.
        ///
        /// Only band 1 is read: the bands are one series, so a flat first hour is enough to recognise the
        /// uniform fallback, and reading them all would cost the whole raster to say the same thing.
        /// </summary>
        private static void WarnIfKeptWindIsUniform(string windSpeedPath, Action<string> log)
        {
            if (!File.Exists(windSpeedPath)) return;

            float[,] ws;
            try
            {
                ws = AscRaster.ReadGeoTiff(windSpeedPath, out AscRaster.Header _, out bool ok);
                if (!ok || ws == null) return;
            }
            catch
            {
                //Reporting on a kept layer must not be able to fail a build that is otherwise fine.
                return;
            }

            float min = float.MaxValue, max = float.MinValue;
            foreach (float v in ws)
            {
                if (v <= -9000f || float.IsNaN(v)) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }

            if (min > max || min != max) return;

            log($"  wind: the kept ws.tif is uniform at {min:F1} mph, which is the pipeline's fallback rather " +
                "than terrain-resolved wind. Turn RebuildExistingLayers on (or delete inputs/ws.tif and " +
                "inputs/wd.tif) to have WindNinja write it.");
        }

        private static void WarnIfNamelistLacksFuelModel(Result result, Action<string> log)
        {
            if (result.FuelStem == null) return;

            foreach (string line in File.ReadAllLines(result.NamelistPath))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("!") || trimmed.Length == 0) continue;
                if (trimmed.StartsWith("FBFM_FILENAME", StringComparison.OrdinalIgnoreCase)) return;
            }

            result.Fallbacks.Add("namelist: kept, but FBFM_FILENAME is not set in it");
            log($"  WARNING the case's own {Path.GetFileName(result.NamelistPath)} does not set FBFM_FILENAME, "
                + $"so ELMFIRE will refuse to start. The case has {result.FuelStem}.tif: add "
                + $"FBFM_FILENAME = '{result.FuelStem}' to its &INPUTS group, or delete the namelist to have "
                + "one written.");
        }

        /// <summary>
        /// The stems ELMFIRE's <c>FBFM_FILENAME</c> may point at, in the order they are preferred.
        /// Scott &amp; Burgan's 40 classes first: a case carrying both is carrying the finer one on purpose.
        /// </summary>
        private static readonly string[] FuelStems = { "fbfm40", "fbfm13" };

        /// <summary>
        /// The ELMFIRE-WUINITY fork's urban-spread inputs. All five have to be present for the
        /// building spread model to be switched on — it is a complete set or nothing.
        /// Each carries the stems seen in practice: the builder's own descriptive names, and the
        /// short ones the external building pipeline writes, which is what the prepared cases hold.
        /// </summary>
        private static readonly (string Key, string[] Stems)[] BuildingLayers =
        {
            ("BLDG_AREA_FILENAME",            new[] { "bldg_area_avg", "baa" }),
            ("BLDG_SEPARATION_DIST_FILENAME", new[] { "bldg_separation_distance", "ssd" }),
            ("BLDG_NONBURNABLE_FRAC_FILENAME",new[] { "bldg_nonburnable_frac", "nbf_h" }),
            ("BLDG_FOOTPRINT_FRAC_FILENAME",  new[] { "bldg_footprint_frac", "ff_h" }),
            ("BLDG_FUEL_MODEL_FILENAME",      new[] { "bldg_fuel_model", "bfm_h" }),
        };

        /// <summary>
        /// The first of <paramref name="stems"/> the case actually has, or null.
        /// </summary>
        /// <remarks>
        /// Resolved against the case rather than hardcoded because the stem is not the builder's to
        /// choose: fuel and building layers are external products (see P3 in
        /// <c>docs/elmfire-case-automation.md</c>) and arrive under whichever name the pipeline that made
        /// them uses. Hardcoding <c>fbfm13</c> here meant a case whose fuel raster is <c>fbfm40.tif</c> —
        /// which is every case built so far — got <c>FBFM_FILENAME</c> emitted as a comment, and ELMFIRE
        /// stopped at its own "is this key set?" check before MPI came up.
        ///
        /// The file system is the authority, not <see cref="Result.Written"/>: a layer the case already had
        /// and kept is equally referenceable, and only some of those are recorded as written.
        /// </remarks>
        private static string ResolveStem(Result r, params string[] stems)
        {
            foreach (string stem in stems)
            {
                if (r.Written.Contains(stem) || File.Exists(Path.Combine(r.InputsDirectory, stem + ".tif")))
                {
                    return stem;
                }
            }

            return null;
        }

        /// <summary>
        /// Reads the case as it now stands and hands it to <see cref="ElmfireNamelistBuilder"/>, which owns
        /// the namelist's shape. This used to be a long string list here; the settings it emits are the
        /// scenario's now, so the two halves - what the case has, and what the user chose - are written down
        /// in different places.
        /// </summary>
        private static string[] BuildNamelist(Options o, Result r)
        {
            var facts = new ElmfireNamelistBuilder.CaseFacts
            {
                Name = o.Name,
                FuelStem = r.FuelStem,
                StartDateTime = o.StartDateTime,
                SimulationTstopSeconds = o.SimulationTstopSeconds,
                PathToGdal = o.PathToGdal,
                AvailableMeteorologyBands = AscRaster.GetBandCount(Path.Combine(r.InputsDirectory, "ws.tif")),
                HasBuildingFuelModelFile = File.Exists(Path.Combine(r.InputsDirectory, "building_fuel_models.csv")),
                HasFuelModelFile = File.Exists(Path.Combine(r.InputsDirectory, "fuel_models.csv")),
                CanopyInRealUnits = r.CanopyInRealUnits,
            };

            foreach (string stem in new[]
            {
                "cc", "ch", "cbh", "cbd", "ignition_mask", "barriers", "sdi",
                "land_value", "population_density", "real_estate_value", "erc", "pyromes",
            })
            {
                if (ResolveStem(r, stem) != null) facts.AvailableStems.Add(stem);
            }

            //Every non-raster file in inputs/, so the namelist builder can tell a named calibration table that
            //is there from one that only has a name.
            try
            {
                foreach (string path in Directory.GetFiles(r.InputsDirectory, "*.csv"))
                {
                    facts.AvailableInputFiles.Add(Path.GetFileName(path));
                }
            }
            catch
            {
                //A case whose inputs cannot be listed has bigger problems, and they are reported elsewhere.
            }

            //All five or none: a partial set makes ELMFIRE read a raster nobody produced, and the namelist
            //builder decides what to do about that from the count.
            foreach ((string key, string[] stems) in BuildingLayers)
            {
                string stem = ResolveStem(r, stems);
                if (stem != null) facts.BuildingLayers.Add((key, stem));
            }
            if (facts.BuildingLayers.Count != BuildingLayers.Length) facts.BuildingLayers.Clear();

            foreach (PlacedIgnition ignition in r.Ignitions)
            {
                facts.Ignitions.Add((ignition.X, ignition.Y, ignition.TimeSeconds));
            }

            return ElmfireNamelistBuilder.Build(o.Namelist, facts);
        }
    }
}
