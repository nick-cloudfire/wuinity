using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using PREACT.Math;
using PREACT.Population;
using PREACT.Tools;

namespace PREACT.Utility
{
    /// <summary>
    /// Builds a complete ELMFIRE case folder for a domain from scratch: the one call that wires the terrain,
    /// source-layer, weather and namelist pieces together (docs/elmfire-cases.md, "Building a case, step by
    /// step").
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

            /// <summary>
            /// Source rasters the scenario names that are not there, by stem, as they were named - left out of
            /// <see cref="UserRasters"/>, and kept only so a build that cannot go on without one (the fuel model) can
            /// say which file it looked for.
            /// </summary>
            public Dictionary<string, string> UnresolvedSourceRasters = new Dictionary<string, string>();

            /// <summary>Stems in <see cref="UserRasters"/> that are categorical and must be
            /// resampled nearest-neighbour rather than bilinear (fuel model codes, mostly).
            /// The ignition mask is in here because interpolating it would invent fractional
            /// "partly ignitable" cells along the edge of an otherwise binary mask. The same list decides
            /// how layers carried over from a previous grid are re-cut, so the building fuel model and the
            /// WUI area are here too: re-cut bilinearly, either grows a fringe of fractional values that are
            /// not a class at all.</summary>
            public HashSet<string> CategoricalStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "fbfm13", "fbfm40", "bldg_fuel_model", "bfm_h", "ignition_mask", "wui_area", "barriers", "pyromes" };

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

            /// <summary>How long the fire runs, in seconds - the namelist's SIMULATION_TSTOP, and what the weather
            /// series has to cover. The CLI and the GUI both pass the scenario's own
            /// <c>[ELMFIRE] SimulationTstopHours</c>; this default only applies to a bare API call.</summary>
            public double SimulationTstopSeconds = 8.0 * 3600.0;

            /// <summary>The ELMFIRE executable, used only to find ELMFIRE's default fuel model table.</summary>
            public string ElmfireExe;

            /// <summary>
            /// The namelist template the case will run (<c>[ELMFIRE] NamelistTemplate</c>), when there is one. Only
            /// its <c>FBFM_FILENAME</c> is read, so the ignition mask is restricted against the fuel the run burns.
            /// </summary>
            public string TemplateNamelistPath;

            /// <summary>
            /// DEMs the caller already has (the scenario's own landscape DEM), used for the grid when they cover
            /// the padded domain - so a case needs no second download. One that does not cover it is passed over:
            /// cutting the grid from it would shrink the padding.
            /// </summary>
            public List<string> CandidateDemPaths = new List<string>();

            /// <summary>
            /// Every file the scenario names (its <c>...File</c> keys, resolved), which the build must never rewrite: a
            /// raster a namelist names that is also one of these is re-cut into a copy, not in place (review RC-MI-2).
            /// </summary>
            public List<string> ScenarioFiles = new List<string>();

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

            /// <summary>
            /// Asked at the points where the build can stop and leave the case consistent; once it answers true
            /// <see cref="Build"/> throws <see cref="OperationCanceledException"/>. Null never stops. Handed to the
            /// weather stage when that has no predicate of its own.
            /// </summary>
            /// <remarks>
            /// Those points: before the grid is decided (nothing changed yet), after a new case's first DEM, before
            /// the weather, and before every WindNinja solve. Not between a re-cut grid and the layers carried onto
            /// it, which would leave the case on a new grid without them until the next build carried them (see
            /// <see cref="CarryPendingMarker"/>), and not once the wind is written, since the moisture and the namelist
            /// that counts its bands have to follow it.
            /// </remarks>
            public Func<bool> Cancelled;
        }

        /// <summary>Throws the stop <see cref="Options.Cancelled"/> asked for, saying where the build stopped.</summary>
        private static void StopIfCancelled(Options o, string where)
        {
            if (o.Cancelled != null && o.Cancelled())
            {
                throw new OperationCanceledException("The case build was stopped " + where);
            }
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

            /// <summary>True when the case's old grid did not cover the padded domain and was cut again.</summary>
            public bool GridRebuilt;

            /// <summary>Layers warped from the old grid onto a re-cut one, because this build had no source for them.</summary>
            public List<string> Carried = new List<string>();

            /// <summary>SHA-256 of the namelist this build wrote, recorded so the next build can tell a hand edit.</summary>
            public string NamelistSha256;

            /// <summary>Where a hand-edited namelist was set aside before regenerating, or null.</summary>
            public string KeptNamelistPath;

            /// <summary>The exported painted WUI area, for k-PERIL's WuiAreaFile; null if none was painted.</summary>
            public string WuiAreaFile;

            /// <summary>
            /// Where the scenario's painting was placed from when that was not the case grid - "the landscape raster
            /// mati_dem.tif (painted before the case grid existed)" - or null (no painting, or painted on the case grid).
            /// A scenario whose landscape is pointed at the case loses the grid such a painting was placed via, so the
            /// next build cannot place it until it is moved onto the case grid.
            /// </summary>
            public string PaintingOffCaseGrid;

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

            //Refused before anything is made: a case without it passes its own validation and then stops ELMFIRE at
            //start-up, on the first run (e2e N2).
            string noBuildingTable = DescribeMissingBuildingTable(o, inputs);
            if (noBuildingTable != null) throw new InvalidDataException(char.ToUpperInvariant(noBuildingTable[0]) + noBuildingTable.Substring(1) + ".");

            //The fuel model too, and for the same reason: the case is worthless without one, and the build used to find
            //that out at its very end - in its validation, after the DEM download, the ERA5 archive and some minutes of
            //WindNinja (Auburn2: "The case carries neither fbfm40.tif nor fbfm13.tif" at 22:57:59, three minutes in).
            string noFuel = DescribeMissingFuel(o, inputs);
            if (noFuel != null) throw new InvalidDataException(noFuel);

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
                string path = ElmfireStems.Tif(inputs, stem);
                if (o.OverwriteExistingLayers || !File.Exists(path))
                {
                    return true;
                }

                if (!result.Reused.Contains(stem)) result.Reused.Add(stem);
                if (!result.Written.Contains(stem)) result.Written.Add(stem);
                return false;
            }

            //---------------------------------------------------------------- 1-2. DEM and master grid
            //The grid comes from dem.tif, which ELMFIRE also reads the domain and CRS from, so a case's own DEM
            //is the grid of record - but only if it is the grid this scenario asks for. A dem.tif that does not
            //cover the padded domain used to be kept anyway, and the grid silently shrank to it: on Mati the
            //"2000 m" padding came out as 473 m to the west, 14 m to the east and minus 28 m to the south, so the
            //southern strip of the evacuation domain had no fire data at all.
            string demPath = ElmfireStems.Tif(inputs, ElmfireStems.Dem);
            MasterGrid grid = null;
            string previousGridDirectory = null;

            //The namelists in force - the case's own elmfire.data, hand-edited or not, and the scenario's template -
            //read before anything moves: every raster they name has to end up on the grid this build settles on, or
            //the one who runs them gets an ELMFIRE segfault (Mati's FBFM_FILENAME='fbfm40_roads101' was left on the
            //old 566x541 grid when the case was re-cut to 704x680).
            string[] existingNamelist = ReadTemplate(Path.Combine(o.OutputDirectory, "elmfire.data"));
            List<ElmfireStems.NamelistRaster> namelistRasters = NamelistRastersInForce(o, inputs, existingNamelist);

            StopIfCancelled(o, "before it changed anything in the case.");

            if (!o.OverwriteExistingLayers && File.Exists(demPath))
            {
                MasterGrid existing = MasterGrid.FromRasterFile(demPath);
                string mismatch = DescribeGridMismatch(existing, o, southWest, northEast);
                if (mismatch == null)
                {
                    grid = existing;
                    result.Reused.Add(ElmfireStems.Dem);
                    result.Written.Add(ElmfireStems.Dem);
                    Log($"Reusing the case's own DEM: {grid.Header.Ncols}x{grid.Header.Nrows} @ "
                        + $"{grid.Header.CellSize:F1} m, {grid.Epsg}.");
                }
                else
                {
                    Log($"The case's dem.tif is not this scenario's grid: {mismatch}");
                    Log("  Re-cutting the grid from the source DEM, and carrying every other layer the case holds "
                        + "onto it; the old rasters are kept in inputs/" + PreviousGridFolder + ".");
                    previousGridDirectory = SetAsidePreviousGrid(inputs, Log);
                }
            }

            //A grid an earlier build set aside and never carried onto its new one - it failed or was killed after the
            //set-aside (no DEM to cut the new grid from, a source layer that would not warp): carried now. Without this
            //the next build found no dem.tif (or a new one that fits) and never looked in inputs/_previous_grid again,
            //so the building layers, the WUI area and every hand-made layer stayed there, and a painting on the old grid
            //was refused as painted on no known grid.
            if (previousGridDirectory == null && !o.OverwriteExistingLayers && IsCarryPending(inputs))
            {
                previousGridDirectory = Path.Combine(inputs, PreviousGridFolder);
                Log($"  inputs/{PreviousGridFolder} holds the grid an earlier build set aside and did not finish carrying "
                    + "onto the new one; carrying its layers now.");
            }

            if (grid == null)
            {
                string rawDem = await ResolveSourceDem(o, inputs, southWest, northEast, previousGridDirectory, Log);

                Log($"Warping DEM to the local UTM zone at {o.CellSizeMetres:F0} m...");
                grid = RasterHarmonizer.BuildUtmMasterGrid(
                    rawDem, demPath,
                    southWest.x, southWest.y, northEast.x, northEast.y,
                    o.CellSizeMetres);
                Log($"Master grid: {grid.Header.Ncols}x{grid.Header.Nrows} @ {grid.Header.CellSize:F1} m, {grid.Epsg}");
                result.Written.Add(ElmfireStems.Dem);
                ReportDemCoverage(rawDem, southWest, northEast, result, Log);

                //A DEM download cannot be interrupted, so a stop during one is honoured once it is on the grid - for a
                //new case only: a re-cut goes on until the old grid's layers are carried onto the new one.
                if (previousGridDirectory == null)
                {
                    StopIfCancelled(o, "after its grid (inputs/dem.tif) was made and before its other layers; the next "
                                       + "build carries on from that grid.");
                }
            }

            result.Grid = grid;
            result.GridRebuilt = previousGridDirectory != null;

            //---------------------------------------------------------------- 3. Slope / aspect
            bool needSlope = Needed(ElmfireStems.Slope);
            bool needAspect = Needed(ElmfireStems.Aspect);
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
                    GeoTiffRasterWriter.WriteBand(grid, slope, ElmfireStems.Tif(inputs, ElmfireStems.Slope));
                    result.Written.Add(ElmfireStems.Slope);
                }
                if (needAspect)
                {
                    GeoTiffRasterWriter.WriteBand(grid, aspect, ElmfireStems.Tif(inputs, ElmfireStems.Aspect));
                    result.Written.Add(ElmfireStems.Aspect);
                }
            }

            //---------------------------------------------------------------- 4. adj / phi
            //Both are just 1.0 everywhere, same shape/CRS as the DEM (WildfireAV's
            //makePhiAndAdjFiles.py) - they are not related to ignition.
            foreach (string stem in new[] { ElmfireStems.Adj, ElmfireStems.Phi })
            {
                if (!Needed(stem)) continue;
                GeoTiffRasterWriter.WriteConstant(grid, 1.0f, ElmfireStems.Tif(inputs, stem));
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

                WarpLayer(o, result, grid, stem, source, ElmfireStems.Tif(inputs, stem), Log);
            }

            //---------------------------------------------------------------- 5a. Layers from the old grid
            //Only after a re-cut grid. Whatever the case held that this build had no source for - hand-prepared
            //fuel, buildings, canopy - is warped from its old copy onto the new grid rather than lost. Inside the
            //old extent that is the same data; in the new padding it is nodata, i.e. no fuel and no buildings,
            //which is what the old grid said about ground it did not cover.
            if (previousGridDirectory != null)
            {
                CarryPreviousGridLayers(o, result, grid, inputs, previousGridDirectory, Log);
            }

            //---------------------------------------------------------------- 5a'. Rasters the namelists name
            //Whatever raster a namelist in force names and the build does not make itself - a hand-made fuel
            //variant, a building layer under its own name - is re-cut onto the grid when it is not on it, the same
            //way as a layer carried from the old grid.
            CarryNamelistRasters(o, result, grid, inputs, namelistRasters, Log);

            //---------------------------------------------------------------- 5b. Canopy defaults
            //ELMFIRE treats CC/CH/CBH/CBD as required inputs and refuses to start without them
            //("is not specified and is a required input"), so leaving them out of the namelist
            //produces a case that builds cleanly and then cannot run. Canopy is also precisely the
            //layer that has no global source, so defaulting it to zero - no canopy fuel, hence
            //surface fire only, no crown fire - is what makes an arbitrary domain runnable at all.
            //Needed() is what keeps this from destroying real canopy.
            foreach (string stem in new[] { "cc", "ch", "cbh", "cbd" })
            {
                if (result.Written.Contains(stem) || !Needed(stem)) continue;
                GeoTiffRasterWriter.WriteConstant(grid, 0.0f, ElmfireStems.Tif(inputs, stem));
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
            ApplyPaintedMasks(o, result, inputs, grid, previousGridDirectory, Log);

            //---------------------------------------------------------------- 5d. Ignition points
            //After the painted masks, because an explicitly placed point supersedes the centroid of a
            //painted stroke.
            ApplyIgnitionPoints(o, result, grid, Log);

            //---------------------------------------------------------------- 6. Ignition mask
            //Only generated when the user did not supply one: an all-ones mask lets ELMFIRE's
            //RANDOM_IGNITIONS place a fire anywhere in the domain, which is the neutral default.
            string ignitionMask = ElmfireStems.Tif(inputs, ElmfireStems.IgnitionMask);
            if (!File.Exists(ignitionMask))
            {
                Log("  ignition_mask: none supplied, writing an all-ones (ignite-anywhere) mask.");
                GeoTiffRasterWriter.WriteConstant(grid, 1.0f, ignitionMask);
                result.Written.Add(ElmfireStems.IgnitionMask);
            }

            //The fuel stem the run will burn: the template's FBFM_FILENAME when a template is in force, else what
            //the case holds. Resolved once, so the mask restriction, the namelist and the validator agree.
            string[] template = ReadTemplate(o.TemplateNamelistPath);
            result.FuelStem = ElmfireStems.FuelStem(template, inputs) ?? ResolveStem(result, ElmfireStems.Fuel);

            if (o.RestrictIgnitionToBurnableFuel)
            {
                RestrictIgnitionMask(o, result, inputs, grid, Log);
            }

            StopIfCancelled(o, "after its layers were made and before its weather; elmfire.data was not written again, "
                               + "and the next build makes the weather.");

            //---------------------------------------------------------------- 7. Baseline weather
            //Runs after the user rasters so Nelson can shade its sticks with the canopy cover
            //layer if one was supplied.
            //
            //All five together, or none: they are one weather series split across five files, and a mixture
            //of a case's own wind and freshly sampled moisture is not a description of any day. Kept only when
            //the series covers the fire: ELMFIRE refuses a multi-band series shorter than SIMULATION_TSTOP ("Not
            //enough weather bands"), and a case kept at 8 bands for a 72 h fire failed that way on every run.
            double secondsPerBand = o.Namelist != null && o.Namelist.DT_METEOROLOGY > 0 ? o.Namelist.DT_METEOROLOGY : 3600.0;
            string keptWeatherProblem = DescribeKeptWeather(inputs, o.SimulationTstopSeconds, secondsPerBand);

            if (keptWeatherProblem == null && !o.OverwriteExistingLayers && previousGridDirectory == null)
            {
                Log("  weather: the case already has ws/wd/m1/m10/m100 covering the fire; keeping them.");
                result.Reused.AddRange(ElmfireStems.Weather);
                result.Written.AddRange(ElmfireStems.Weather);
                WarnIfKeptWindIsUniform(ElmfireStems.Tif(inputs, ElmfireStems.WindSpeed), Log);
            }
            else
            {
                if (keptWeatherProblem != null && File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.WindSpeed)))
                {
                    Log("  weather: " + keptWeatherProblem + "; building it again.");
                }

                Log("Building baseline weather (climatology -> WindNinja -> Nelson)...");

                WeatherRasterPipeline.Options w = o.Weather ?? new WeatherRasterPipeline.Options();
                w.Grid = grid;
                w.InputsDirectory = inputs;
                if (w.Cancelled == null) w.Cancelled = o.Cancelled;

                //The weather series has to span the fire and be read at the interval it was written at.
                //DT_METEOROLOGY comes from the namelist settings so the two cannot disagree - a series
                //written hourly and read at any other interval is silently stretched in time.
                w.SimulationStartDateTime = o.StartDateTime;
                if (w.StartTimeZone == null) w.StartTimeZone = LocalTime.ZoneAt(o.LowerLeftLatLon.x, o.LowerLeftLatLon.y);
                w.SimulationTstopSeconds = o.SimulationTstopSeconds;
                w.SecondsPerBand = secondsPerBand;

                //A single case wants the whole series, one band per hour of fire: the cap exists for campaigns.
                w.MaxBands = 0;

                //WindNinja is not resolved here: the pipeline probes for it itself when none is named, so no
                //caller can forget to and quietly get a uniform wind field.

                w.LatLon = new Vector2d(0.5 * (southWest.x + northEast.x), 0.5 * (southWest.y + northEast.y));
                w.Log = o.Log;
                if (string.IsNullOrEmpty(w.ArchiveCsvPath))
                {
                    //Beside the case rather than inside inputs/: it is a cache shared by every
                    //realization, not one of ELMFIRE's inputs.
                    w.ArchiveCsvPath = ArchivePath(o.OutputDirectory, o.Name);
                }

                result.Weather = await WeatherRasterPipeline.Run(w);
                if (result.Weather.Cancelled)
                {
                    throw new OperationCanceledException("The case build was stopped while its weather was being made: "
                        + "no wind was written (and no uniform field in its place), elmfire.data was not written again, "
                        + "and the next build makes the weather.");
                }
                result.Written.AddRange(ElmfireStems.Weather);
            }

            //---------------------------------------------------------------- 8. Loose files
            foreach (string f in o.CopyFiles)
            {
                if (string.IsNullOrEmpty(f) || !File.Exists(f)) { Log($"  copy: not found, skipping ({f})."); continue; }
                File.Copy(f, Path.Combine(inputs, Path.GetFileName(f)), overwrite: true);
                Log($"  copy: {Path.GetFileName(f)}");
            }

            //The surface fuel table, written once here rather than by every ELMFIRE run. With FUEL_MODEL_FILE unset
            //ELMFIRE writes its built-in table into the inputs folder at startup and reads it back - so every run
            //rewrote a shared file (concurrently, in a campaign) and a hand-edited table was silently replaced.
            EnsureFuelModelTable(inputs, o.ElmfireExe, Log);
            if (o.Namelist != null && o.Namelist.USE_BLDG_SPREAD_MODEL)
            {
                EnsureBuildingFuelModelTable(inputs, ElmfireStems.BuildingFuelModelTable, o.ElmfireExe, Log);
            }

            //---------------------------------------------------------------- 9. Namelist
            //Regenerated on every build, so the scenario's [ElmfireNamelist] settings, its stop time and the case's
            //layers are what the namelist says: a namelist kept from the first build made later edits on the
            //Fire behaviour page do nothing at all. A namelist edited by hand is not destroyed - it is set aside with a
            //timestamp - and the way to run a hand-tuned namelist is [ELMFIRE] NamelistTemplate, which is used
            //verbatim.
            result.NamelistPath = Path.Combine(o.OutputDirectory, "elmfire.data");
            if (IsSameFile(o.TemplateNamelistPath, result.NamelistPath))
            {
                //The scenario runs this very file as its template, which is the instruction to leave it alone.
                result.Reused.Add("elmfire.data");
                result.NamelistSha256 = ReadManifestValue(o.OutputDirectory, GeneratedNamelistKey);
                Log($"Keeping {Path.GetFileName(result.NamelistPath)}: the scenario names it as its NamelistTemplate.");
            }
            else
            {
                string[] namelist = BuildNamelist(o, result);
                SetAsideHandEditedNamelist(o, result, existingNamelist, namelist, Log);
                File.WriteAllLines(result.NamelistPath, namelist);
                result.NamelistSha256 = ElmfireFingerprint.HashFile(result.NamelistPath);
                Log($"Wrote {result.NamelistPath}");
            }

            Directory.CreateDirectory(Path.Combine(o.OutputDirectory, "outputs"));
            Directory.CreateDirectory(Path.Combine(o.OutputDirectory, "scratch"));

            //---------------------------------------------------------------- 9b. Provenance
            //What each raster was made from, which the namelist cannot say: its *_FILENAME keys name stems
            //inside inputs/ ('cc'), not the source those stems were warped out of. Written beside the namelist
            //so the case describes itself - and so the next build can tell its own namelist from a hand edit.
            WriteSourceManifest(o, result, Log);

            //---------------------------------------------------------------- 10. Validate
            //Last, so it sees the case as ELMFIRE will: every layer written or kept, on whatever grid it
            //actually ended up on. Reported rather than thrown - a case with a misregistered optional layer is
            //still worth having on disk to look at, and the run is where refusing belongs.
            result.Validation = ElmfireCaseValidator.Validate(inputs, grid, result.FuelStem, OptionalStems(), Log);

            //Everything that reads the set-aside grid has run; it is kept for reference, no longer pending.
            if (previousGridDirectory != null)
            {
                try { File.Delete(Path.Combine(previousGridDirectory, CarryPendingMarker)); } catch (IOException) { }
            }

            return result;
        }

        /// <summary>
        /// Left in <see cref="PreviousGridFolder"/> by the build that sets a grid aside, until a build has carried its
        /// layers onto the new grid and finished: while it is there, the set-aside grid is the case's real one.
        /// </summary>
        public const string CarryPendingMarker = "carry_pending.txt";

        private static bool IsCarryPending(string inputs)
        {
            string previous = Path.Combine(inputs, PreviousGridFolder);
            return File.Exists(Path.Combine(previous, CarryPendingMarker)) && File.Exists(ElmfireStems.Tif(previous, ElmfireStems.Dem));
        }

        /// <summary>Where a case keeps its ERA5 archive: <c>climatology/&lt;scenario name&gt;_era5_hourly.csv</c>.</summary>
        /// <remarks>
        /// One rule for the case build, the GUI and the campaign. The campaign used to name it after the .wui file
        /// instead of <c>[Simulation] Name</c>, so a scenario saved as <c>mati_v2.wui</c> re-downloaded 26 years
        /// of ERA5 next to the one it already had.
        /// </remarks>
        public static string ArchivePath(string caseDirectory, string scenarioName)
        {
            return Path.Combine(caseDirectory, "climatology", scenarioName + "_era5_hourly.csv");
        }

        /// <summary>Where the rasters of a replaced grid are kept, inside inputs/.</summary>
        public const string PreviousGridFolder = "_previous_grid";

        /// <summary>
        /// Why an existing dem.tif is not the grid this build asks for, or null when it is: it must cover the
        /// padded domain (to within a cell) at the requested cell size.
        /// </summary>
        private static string DescribeGridMismatch(MasterGrid existing, Options o, Vector2d southWest, Vector2d northEast)
        {
            double cs = existing.Header.CellSize;
            if (System.Math.Abs(cs - o.CellSizeMetres) > 0.01 * o.CellSizeMetres)
            {
                return $"its cells are {cs:F1} m and the scenario asks for {o.CellSizeMetres:F1} m";
            }

            if (string.IsNullOrEmpty(existing.Epsg))
            {
                return "its CRS cannot be identified";
            }

            (double xMin, double yMin, double xMax, double yMax) = RasterHarmonizer.ProjectBounds(
                existing.Epsg, southWest.x, southWest.y, northEast.x, northEast.y);

            //One cell of slack: the warp snaps the requested extent to whole cells.
            double tolerance = cs;
            var shortfalls = new List<string>();
            if (existing.XMin > xMin + tolerance) shortfalls.Add($"{existing.XMin - xMin:F0} m short on the west");
            if (existing.XMax < xMax - tolerance) shortfalls.Add($"{xMax - existing.XMax:F0} m short on the east");
            if (existing.YMin > yMin + tolerance) shortfalls.Add($"{existing.YMin - yMin:F0} m short on the south");
            if (existing.YMax < yMax - tolerance) shortfalls.Add($"{yMax - existing.YMax:F0} m short on the north");

            if (shortfalls.Count == 0) return null;

            return $"it is {existing.Header.Ncols}x{existing.Header.Nrows} and does not cover the domain padded by "
                   + $"{o.PaddingMetres:F0} m ({string.Join(", ", shortfalls)})";
        }

        /// <summary>
        /// Moves every raster of the old grid out of inputs/ into <see cref="PreviousGridFolder"/>, so nothing
        /// kept can disagree with the new grid, and returns that folder.
        /// </summary>
        /// <remarks>
        /// Only the stems a case is built from are moved. Anything else in inputs/ - a scenario's own landscape
        /// raster, a hand-made variant of a layer - is left where it is, since the scenario may point at it.
        /// </remarks>
        private static string SetAsidePreviousGrid(string inputs, Action<string> log)
        {
            string previous = Path.Combine(inputs, PreviousGridFolder);
            string into = previous;
            if (IsCarryPending(inputs))
            {
                //The grid set aside by an earlier build that did not finish is the one to carry from, so it stays; what
                //that build left on its own new grid goes beside it, kept rather than deleted.
                into = Path.Combine(previous, "superseded_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            }
            else if (Directory.Exists(previous))
            {
                try { Directory.Delete(previous, recursive: true); } catch { }
            }
            Directory.CreateDirectory(into);

            var moved = new List<string>();
            foreach (string stem in KnownStems().Concat(new[] { ElmfireStems.WuiArea }).Distinct())
            {
                string path = ElmfireStems.Tif(inputs, stem);
                if (!File.Exists(path)) continue;

                File.Move(path, ElmfireStems.Tif(into, stem));
                string aux = path + ".aux.xml";
                if (File.Exists(aux)) { try { File.Delete(aux); } catch { } }
                moved.Add(stem);
            }

            File.WriteAllText(Path.Combine(previous, CarryPendingMarker),
                "The build that set this grid aside has not finished carrying its layers onto the case's new grid; the next "
                + "build carries them. Removed once a build has.\n");
            log($"  moved {moved.Count} raster(s) of the old grid to inputs/{PreviousGridFolder}"
                + (into == previous ? string.Empty : "/" + Path.GetFileName(into) + " (an earlier set-aside grid is still to be carried)")
                + $": {string.Join(", ", moved)}");
            return previous;
        }

        /// <summary>
        /// The DEM to cut the grid from: the one named with --dem, the case's cached download, one of the
        /// caller's candidates that covers the padded domain, or a fresh OpenTopography download.
        /// </summary>
        private static async Task<string> ResolveSourceDem(Options o, string inputs, Vector2d southWest, Vector2d northEast,
            string previousGridDirectory, Action<string> log)
        {
            string rawDem = Path.Combine(inputs, "dem_source.tif");

            if (!string.IsNullOrEmpty(o.LocalDemPath))
            {
                if (!File.Exists(o.LocalDemPath)) throw new FileNotFoundException("No such DEM: " + o.LocalDemPath);
                log($"Using local DEM {o.LocalDemPath}.");
                File.Copy(o.LocalDemPath, rawDem, overwrite: true);
                return rawDem;
            }

            if (File.Exists(rawDem))
            {
                //A downloaded DEM is reused even under --force: --force is about writing into a non-empty case
                //folder, not about re-fetching data that cannot have changed. Only if it covers the domain,
                //though - a download cut for a smaller padding is the same shrunken grid again.
                if (Covers(rawDem, southWest, northEast))
                {
                    log("Reusing the previously downloaded DEM.");
                    return rawDem;
                }
                log("The previously downloaded DEM does not cover the padded domain; not using it.");
            }

            foreach (string candidate in o.CandidateDemPaths)
            {
                if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate)) continue;
                if (!Covers(candidate, southWest, northEast))
                {
                    log($"  {candidate} does not cover the padded domain, so it is not used for the grid.");
                    continue;
                }

                log($"Using the scenario's DEM {candidate}, which covers the padded domain.");
                File.Copy(candidate, rawDem, overwrite: true);
                return rawDem;
            }

            if (string.IsNullOrEmpty(o.OpenTopographyApiKey))
            {
                throw new Exception(
                    (previousGridDirectory != null
                        ? "The case's grid has to be cut again to cover the padded domain, "
                        : "A DEM is needed and there is none in the case, ")
                    + "so one has to be downloaded - but no OpenTopography key was found, and no local DEM covering "
                    + "the padded domain was given. Put the key in "
                    + "WUInity/Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt or set "
                    + "OPENTOPOGRAPHY_API_KEY, or point the build at a DEM (build-case --dem) that covers "
                    + $"{southWest.x:F4},{southWest.y:F4} to {northEast.x:F4},{northEast.y:F4}.");
            }

            log($"Downloading {o.DemType} DEM from OpenTopography...");
            await OpenTopographyDownloader.Download(southWest, northEast, o.OpenTopographyApiKey, rawDem, o.DemType);
            return rawDem;
        }

        /// <summary>Whether a raster's extent contains the lat/lon box (to about 1e-4 degrees).</summary>
        private static bool Covers(string path, Vector2d southWest, Vector2d northEast)
        {
            if (!RasterHarmonizer.TryGetWgs84Bounds(path, out double s, out double w, out double n, out double e))
            {
                return false;
            }

            const double slack = 1e-4;
            return s <= southWest.x + slack && w <= southWest.y + slack
                   && n >= northEast.x - slack && e >= northEast.y - slack;
        }

        /// <summary>
        /// Says how much of the padded domain the source DEM actually covers, when it does not cover all of it.
        /// </summary>
        /// <remarks>
        /// The grid is the padded domain either way; what the DEM does not cover is nodata elevation, which
        /// every downstream layer reads as ground with nothing on it. That is right for sea and wrong for land,
        /// and only the user knows which it is - so it is said, with the numbers, rather than refused.
        /// </remarks>
        private static void ReportDemCoverage(string rawDem, Vector2d southWest, Vector2d northEast, Result result,
            Action<string> log)
        {
            if (Covers(rawDem, southWest, northEast)) return;

            if (!RasterHarmonizer.TryGetWgs84Bounds(rawDem, out double s, out double w, out double n, out double e))
            {
                return;
            }

            string message = $"the source DEM covers {s:F4},{w:F4} to {n:F4},{e:F4}, less than the padded domain "
                             + $"{southWest.x:F4},{southWest.y:F4} to {northEast.x:F4},{northEast.y:F4}; the rest of "
                             + "the grid has no elevation, and the fire cannot spread there unless other layers say "
                             + "otherwise";
            result.Fallbacks.Add("DEM: " + message);
            log("  WARNING " + message + ". Use a larger DEM to give the fire the whole padding.");
        }

        /// <summary>Warps one source raster onto the grid and scrubs its non-finite values.</summary>
        private static void WarpLayer(Options o, Result result, MasterGrid grid, string stem, string source,
            string destination, Action<string> log)
        {
            string method = o.CategoricalStems.Contains(stem) ? "near" : "bilinear";
            log($"  {stem}: warping onto the master grid ({method}).");
            RasterHarmonizer.WarpToGrid(source, destination, grid, method);

            //Scrubbed here, on the way in, because ELMFIRE traps on floating-point invalid and one NaN aborts the
            //whole run with a message naming an unrelated line. External products are exactly where NaN comes
            //from - several declare no nodata value at all. 0 means "none of this here" for every layer ingested
            //through this path: no canopy, no buildings, not in the mask.
            long scrubbed = RasterScrubber.ReplaceNonFinite(destination, 0f, log);
            if (scrubbed > 0)
            {
                long cells = (long)grid.Header.Ncols * grid.Header.Nrows;
                log($"    {stem}: {scrubbed} non-finite cell(s) replaced with 0 "
                    + $"({100.0 * scrubbed / System.Math.Max(1, cells):F1} % of the grid).");
                result.Fallbacks.Add($"{stem}: {scrubbed} non-finite cells replaced with 0");
            }

            if (!result.Written.Contains(stem)) result.Written.Add(stem);
        }

        /// <summary>
        /// Warps the layers the old grid had and this build did not produce onto the new grid.
        /// </summary>
        /// <remarks>
        /// Terrain (dem, slp, asp, adj, phi) and weather are derived again rather than carried, since they are
        /// functions of the grid; the painted masks are exported again from the painting. Everything else - the
        /// layers a user brought - is carried.
        /// </remarks>
        private static void CarryPreviousGridLayers(Options o, Result result, MasterGrid grid, string inputs,
            string previous, Action<string> log)
        {
            var derived = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ElmfireStems.Dem, ElmfireStems.Slope, ElmfireStems.Aspect, ElmfireStems.Adj, ElmfireStems.Phi,
            };
            foreach (string stem in ElmfireStems.Weather) derived.Add(stem);

            foreach (string path in Directory.GetFiles(previous, "*.tif"))
            {
                string stem = Path.GetFileNameWithoutExtension(path);
                if (derived.Contains(stem) || result.Written.Contains(stem)) continue;
                //What a build that died while re-cutting a namelist's raster left half-written; the original is beside it.
                if (path.EndsWith(RecutSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(ElmfireStems.Tif(inputs, stem))) continue;

                WarpLayer(o, result, grid, stem, path, ElmfireStems.Tif(inputs, stem), log);
                result.Carried.Add(stem);
            }

            if (result.Carried.Count > 0)
            {
                log($"  carried onto the new grid from the old one: {string.Join(", ", result.Carried)}.");
            }
        }

        /// <summary>
        /// Why the case's weather cannot be kept for this fire, or null when it can: all five rasters present,
        /// with one band count, and either one band (which ELMFIRE holds for any duration) or enough bands to
        /// cover the stop time.
        /// </summary>
        public static string DescribeKeptWeather(string weatherDirectory, double tstopSeconds, double secondsPerBand)
        {
            int bands = -1;
            foreach (string stem in ElmfireStems.Weather)
            {
                string path = ElmfireStems.Tif(weatherDirectory, stem);
                if (!File.Exists(path)) return $"{stem}.tif is missing";

                int n = AscRaster.GetBandCount(path);
                if (n <= 0) return $"{stem}.tif cannot be read";
                if (bands < 0) bands = n;
                else if (n != bands) return $"the weather rasters disagree on band count ({bands} vs {n} in {stem}.tif)";
            }

            if (bands > 1 && tstopSeconds > 0 && bands * secondsPerBand < tstopSeconds)
            {
                return $"the weather covers {bands * secondsPerBand / 3600.0:F0} h ({bands} bands) and the fire runs "
                       + $"{tstopSeconds / 3600.0:F0} h";
            }

            return null;
        }

        /// <summary>
        /// Makes sure <c>fuel_models.csv</c> exists in <paramref name="directory"/>, copying ELMFIRE's own default
        /// table when it does not. Returns whether it exists afterwards.
        /// </summary>
        /// <remarks>
        /// The default is <c>build/source/fuel_models.csv</c> in the ELMFIRE source tree beside the executable - the
        /// same table ELMFIRE writes from WRITE_FUEL_MODEL_TABLE when none is named. A table already there is
        /// never replaced: it may be hand-edited, and when it is not it is this same table.
        /// </remarks>
        public static bool EnsureFuelModelTable(string directory, string elmfireExe, Action<string> log)
        {
            string table = Path.Combine(directory, ElmfireStems.FuelModelTable);
            if (File.Exists(table)) return true;

            string source = DefaultFuelModelTable(elmfireExe ?? ElmfireCoupling.ResolveExecutable(null, null));
            if (source == null)
            {
                log?.Invoke($"  fuel table: no {ElmfireStems.FuelModelTable} in the case and ELMFIRE's default could "
                            + "not be found beside the executable; ELMFIRE will write its built-in table itself.");
                return false;
            }

            File.Copy(source, table);
            log?.Invoke($"  fuel table: {ElmfireStems.FuelModelTable} copied from ELMFIRE's default ({source}).");
            return true;
        }

        /// <summary>ELMFIRE's shipped fuel model table for an executable in <c>build/&lt;os&gt;/bin</c>, or null.</summary>
        public static string DefaultFuelModelTable(string elmfireExe)
        {
            return DefaultElmfireTable(elmfireExe, ElmfireStems.FuelModelTable);
        }

        /// <summary>ELMFIRE's shipped building fuel model table for an executable in <c>build/&lt;os&gt;/bin</c>, or null.</summary>
        public static string DefaultBuildingFuelModelTable(string elmfireExe)
        {
            return DefaultElmfireTable(elmfireExe, ElmfireStems.BuildingFuelModelTable);
        }

        /// <summary><c>build/source/&lt;fileName&gt;</c> of the ELMFIRE tree an executable in <c>build/&lt;os&gt;/bin</c> belongs to, or null.</summary>
        private static string DefaultElmfireTable(string elmfireExe, string fileName)
        {
            if (string.IsNullOrEmpty(elmfireExe)) return null;

            try
            {
                string bin = Path.GetDirectoryName(Path.GetFullPath(elmfireExe));
                string candidate = Path.GetFullPath(Path.Combine(bin, "..", "..", "source", fileName));
                return File.Exists(candidate) ? candidate : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Makes sure the building fuel model table <paramref name="tableName"/> exists in <paramref name="directory"/>,
        /// copying ELMFIRE's own <c>building_fuel_models.csv</c> when it is that name and not there. Returns whether it
        /// exists afterwards.
        /// </summary>
        /// <remarks>
        /// ELMFIRE reads it whenever <c>USE_BLDG_SPREAD_MODEL</c> is on - <c>BUILDING_FUEL_MODEL_FILE</c>, default
        /// <c>building_fuel_models.csv</c>, in <c>MISCELLANEOUS_INPUTS_DIRECTORY</c> - and stops at start-up without it
        /// ("Problem opening building fuel model table file ./inputs/building_fuel_models.csv"). A table under another
        /// name is the user's own and is not made up; one already there is never replaced (e2e N2).
        /// </remarks>
        public static bool EnsureBuildingFuelModelTable(string directory, string tableName, string elmfireExe, Action<string> log)
        {
            string name = string.IsNullOrWhiteSpace(tableName) ? ElmfireStems.BuildingFuelModelTable : tableName;
            string table = Path.Combine(directory, name);
            if (File.Exists(table)) return true;
            if (!string.Equals(name, ElmfireStems.BuildingFuelModelTable, StringComparison.OrdinalIgnoreCase)) return false;

            string source = DefaultBuildingFuelModelTable(elmfireExe ?? ElmfireCoupling.ResolveExecutable(null, null));
            if (source == null) return false;

            File.Copy(source, table);
            log?.Invoke($"  building fuel table: {name} copied from ELMFIRE's default ({source}), for the building spread model.");
            return true;
        }

        /// <summary>
        /// Why a build that switches the building spread model on could not give the case its building fuel table, or
        /// null when it can: the table is already in the inputs, is among the files to copy, or ELMFIRE's default is there.
        /// </summary>
        private static string DescribeMissingBuildingTable(Options o, string inputs)
        {
            if (o.Namelist == null || !o.Namelist.USE_BLDG_SPREAD_MODEL) return null;
            if (File.Exists(Path.Combine(inputs, ElmfireStems.BuildingFuelModelTable))) return null;
            foreach (string f in o.CopyFiles)
            {
                if (string.Equals(Path.GetFileName(f ?? string.Empty), ElmfireStems.BuildingFuelModelTable, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(f)) return null;
            }
            if (DefaultBuildingFuelModelTable(o.ElmfireExe ?? ElmfireCoupling.ResolveExecutable(null, null)) != null) return null;

            return $"the building spread model is on ([ElmfireNamelist] USE_BLDG_SPREAD_MODEL) and ELMFIRE reads "
                   + $"{ElmfireStems.BuildingFuelModelTable} from the case's inputs whenever it is, but the case has none and "
                   + "ELMFIRE's default (build/source/building_fuel_models.csv beside the executable) was not found. Put the "
                   + $"table into {inputs} (PREACTcli build-case --copy <file>), set [ELMFIRE] ElmfireExe to an ELMFIRE "
                   + "build that has its source tree, or switch the building spread model off";
        }

        /// <summary>
        /// Why the case would end up without a fuel model ELMFIRE can spread through, or null when it will have one: a
        /// fuel raster named for this build that exists, one the case already holds (the namelist template's or the
        /// case's own <c>FBFM_FILENAME</c>, else <c>fbfm40.tif</c>/<c>fbfm13.tif</c>), or one on a grid an earlier
        /// build set aside and has still to carry onto the new one.
        /// </summary>
        /// <remarks>
        /// Asked before anything is downloaded or computed. Nothing in the build can make a fuel model - it is the one
        /// layer with no default - so a build without one can only end in the validator's "neither fbfm40.tif nor
        /// fbfm13.tif", and everything done before that is wasted.
        /// </remarks>
        private static string DescribeMissingFuel(Options o, string inputs)
        {
            foreach (string stem in ElmfireStems.Fuel)
            {
                if (o.UserRasters.TryGetValue(stem, out string source) && !string.IsNullOrEmpty(source) && File.Exists(source))
                {
                    return null;
                }
            }

            if (ElmfireStems.FuelStem(ReadTemplate(o.TemplateNamelistPath), inputs) != null) return null;
            if (ElmfireStems.FuelStem(ReadTemplate(Path.Combine(o.OutputDirectory, "elmfire.data")), inputs) != null) return null;
            if (IsCarryPending(inputs) && ElmfireStems.FuelStem(null, Path.Combine(inputs, PreviousGridFolder)) != null) return null;

            string named = null;
            foreach (string stem in ElmfireStems.Fuel)
            {
                if (o.UnresolvedSourceRasters.TryGetValue(stem, out string missing) && !string.IsNullOrWhiteSpace(missing))
                {
                    named = missing;
                    break;
                }
            }

            return "The case has no fuel model, so ELMFIRE would have nothing to spread a fire through; the build stopped "
                   + "before downloading or computing anything. "
                   + (named != null
                       ? $"[ELMFIRE] FuelModelFile names {named}, which is not there."
                       : $"The scenario names no fuel model raster ([ELMFIRE] FuelModelFile), and {inputs} holds neither "
                         + "fbfm40.tif nor fbfm13.tif.")
                   + " Download the LANDFIRE fuels (workflow step 4), or point FuelModelFile at an FBFM40 or FBFM13 raster "
                   + "(PREACTcli build-case --fbfm40 <tif>), then build again.";
        }

        private static bool IsSameFile(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string[] ReadTemplate(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try { return File.ReadAllLines(path); }
            catch { return null; }
        }

        /// <summary>
        /// Moves the case's elmfire.data aside when it is not the namelist the last build wrote - an edit by hand,
        /// or a case built before the builder recorded what it wrote - so regenerating it destroys nothing.
        /// </summary>
        /// <remarks>
        /// The keys it and the regenerated namelist disagree on are logged one by one, so what running it as
        /// <c>NamelistTemplate</c> would change is visible, and its rasters are checked against the case grid - they
        /// were re-cut with the case's own layers (<see cref="CarryNamelistRasters"/>) - so the log says whether it
        /// would still run.
        /// </remarks>
        private static void SetAsideHandEditedNamelist(Options o, Result result, string[] keptLines, string[] generated,
            Action<string> log)
        {
            string path = result.NamelistPath;
            if (!File.Exists(path)) return;

            string recorded = ReadManifestValue(o.OutputDirectory, GeneratedNamelistKey);
            string current = ElmfireFingerprint.HashFile(path);
            if (recorded != null && string.Equals(recorded, current, StringComparison.OrdinalIgnoreCase)) return;

            string aside = path + ".kept-" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            File.Move(path, aside);
            result.KeptNamelistPath = aside;

            string why = recorded == null
                ? "the build that wrote it did not record it (built before namelists were regenerated)"
                : "it was edited since the last build";
            log($"  namelist: {Path.GetFileName(path)} is regenerated from the scenario on every build; the existing "
                + $"one was kept as {Path.GetFileName(aside)} because {why}. To run a hand-tuned namelist, set "
                + "[ELMFIRE] NamelistTemplate to it.");

            string[] kept = keptLines ?? ReadTemplate(aside);
            List<string> differences = ElmfireNamelist.DescribeDifferences(kept, generated);
            int changed = differences.Count(d => d.IndexOf("(unset)", StringComparison.Ordinal) < 0);
            if (differences.Count == 0)
            {
                log("  namelist: the kept one and the regenerated one set the same keys to the same values.");
            }
            else
            {
                log($"  namelist: {differences.Count} key(s) differ, kept -> regenerated ({changed} set to another value, "
                    + $"{differences.Count - changed} set by only one of them):");
                foreach (string d in differences) log("    " + d);
            }

            ElmfireCaseValidator.Report rasters = ElmfireCaseValidator.ValidateNamelistRasters(kept, o.OutputDirectory,
                includeWeather: true);
            if (rasters.Ok)
            {
                log($"  namelist: every raster {Path.GetFileName(aside)} names is on the case grid, so it runs as "
                    + "[ELMFIRE] NamelistTemplate.");
            }
            else
            {
                log($"  namelist: {Path.GetFileName(aside)} would not run on this case as it stands:");
                foreach (ElmfireCaseValidator.Problem p in rasters.Fatal) log("    " + p);
            }

            string keys = string.Join(", ", differences.Take(6).Select(d => d.Substring(0, d.IndexOf(':'))));
            result.Fallbacks.Add($"namelist: previous {Path.GetFileName(path)} kept as {Path.GetFileName(aside)}"
                                 + (differences.Count > 0 ? $"; it differs from the regenerated one in {differences.Count} "
                                    + $"key(s) ({keys}{(differences.Count > 6 ? ", ..." : "")})" : ""));
        }

        /// <summary>
        /// The non-weather rasters the namelists in force name, that sit in the case's inputs folder and are not the
        /// terrain the builder derives itself: from the case's current <c>elmfire.data</c>, the scenario's template,
        /// and every namelist an earlier build set aside (<c>elmfire.data.kept-*</c>), since those exist to be run
        /// as a template.
        /// </summary>
        private static List<ElmfireStems.NamelistRaster> NamelistRastersInForce(Options o, string inputs, string[] existing)
        {
            var sources = new List<(string[] Lines, string RunDirectory)>
            {
                (existing, o.OutputDirectory),
                (ReadTemplate(o.TemplateNamelistPath),
                 string.IsNullOrEmpty(o.TemplateNamelistPath) ? o.OutputDirectory : Path.GetDirectoryName(Path.GetFullPath(o.TemplateNamelistPath))),
            };
            try
            {
                foreach (string kept in Directory.GetFiles(o.OutputDirectory, "elmfire.data.kept-*"))
                {
                    sources.Add((ReadTemplate(kept), o.OutputDirectory));
                }
            }
            catch
            {
                //A case folder that cannot be listed has nothing kept to carry.
            }

            var derived = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ElmfireStems.Dem, ElmfireStems.Slope, ElmfireStems.Aspect, ElmfireStems.Adj, ElmfireStems.Phi,
            };
            string inputsFull = Path.GetFullPath(inputs).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var byStem = new Dictionary<string, ElmfireStems.NamelistRaster>(StringComparer.OrdinalIgnoreCase);
            foreach ((string[] lines, string runDirectory) in sources)
            {
                foreach (ElmfireStems.NamelistRaster r in ElmfireStems.ReferencedRasters(lines, runDirectory))
                {
                    if (r.Weather || derived.Contains(r.Stem)) continue;

                    string folder = Path.GetDirectoryName(Path.GetFullPath(r.Path))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!string.Equals(folder, inputsFull, StringComparison.OrdinalIgnoreCase)) continue;

                    if (byStem.TryGetValue(r.Stem, out ElmfireStems.NamelistRaster seen))
                    {
                        seen.Categorical |= r.Categorical;
                        continue;
                    }
                    byStem[r.Stem] = r;
                }
            }

            return byStem.Values.ToList();
        }

        /// <summary>
        /// Re-cuts onto <paramref name="grid"/> every raster a namelist in force names that is not on it, keeping
        /// the original in <see cref="PreviousGridFolder"/>. Class layers (the fuel model, masks) nearest-neighbour.
        /// </summary>
        /// <remarks>
        /// A raster the scenario itself points at - its landscape DEM, the painting's reference grid, a source layer
        /// - is left alone and reported instead: re-cutting it in place would change what the scenario reads.
        /// </remarks>
        private static void CarryNamelistRasters(Options o, Result result, MasterGrid grid, string inputs,
            List<ElmfireStems.NamelistRaster> rasters, Action<string> log)
        {
            if (rasters == null || rasters.Count == 0) return;

            var scenarioFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in o.CandidateDemPaths.Concat(o.UserRasters.Values).Concat(o.ScenarioFiles)
                         .Concat(new[] { o.PaintedMasksGridPath, o.LocalDemPath }))
            {
                if (string.IsNullOrEmpty(f)) continue;
                try { scenarioFiles.Add(Path.GetFullPath(f)); }
                catch { }
            }

            foreach (ElmfireStems.NamelistRaster r in rasters)
            {
                string path = ElmfireStems.Tif(inputs, r.Stem);
                if (!File.Exists(path) || IsOnGrid(path, grid)) continue;

                //A file the scenario itself reads - its landscape's slope, a source layer - is never rewritten: the
                //scenario would read a different raster than it names (review RC-MI-2). The namelist gets a re-cut copy
                //beside it instead, under a name that says which grid it is on, and is told to use it.
                if (scenarioFiles.Contains(Path.GetFullPath(path)))
                {
                    string copyStem = r.Stem + "_" + grid.Header.Ncols.ToString(CultureInfo.InvariantCulture) + "x"
                                      + grid.Header.Nrows.ToString(CultureInfo.InvariantCulture);
                    string copy = ElmfireStems.Tif(inputs, copyStem);
                    if (!File.Exists(copy) || !IsOnGrid(copy, grid))
                    {
                        if (r.Categorical) o.CategoricalStems.Add(copyStem);
                        RecutInto(o, result, grid, copyStem, path, copy, log);
                    }
                    string why = $"{r.Key} = '{r.Stem}' is not on the case grid, and the scenario itself reads {path}, so that "
                                 + $"file was left as it is; its copy on the case grid is inputs/{copyStem}.tif. Set {r.Key} = "
                                 + $"'{copyStem}' in the namelist that names '{r.Stem}' to run it";
                    result.Fallbacks.Add(why);
                    log("  WARNING " + why + ".");
                    continue;
                }

                if (r.Categorical) o.CategoricalStems.Add(r.Stem);

                //Re-cut into a file of its own first, and only then the original moved aside and the copy put in its
                //place: moving it aside first meant a warp that failed (a GDAL error, a full disk) left inputs/ without
                //the raster, and the next build - finding nothing to carry - left it at that (review RC-MI-1).
                string recut = Path.Combine(inputs, r.Stem + RecutSuffix);
                RecutInto(o, result, grid, r.Stem, path, recut, log);

                string previous = Path.Combine(inputs, PreviousGridFolder);
                Directory.CreateDirectory(previous);
                string aside = ElmfireStems.Tif(previous, r.Stem);
                if (File.Exists(aside))
                {
                    aside = Path.Combine(previous, r.Stem + ".off-grid-"
                                                   + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".tif");
                }
                File.Move(path, aside);
                string aux = path + ".aux.xml";
                if (File.Exists(aux)) { try { File.Delete(aux); } catch { } }
                File.Move(recut, path);

                log($"  {r.Stem}: named by the namelist ({r.Key}) and not on the case grid; re-cut onto it, the original "
                    + $"kept as inputs/{PreviousGridFolder}/{Path.GetFileName(aside)}.");
                if (!result.Carried.Contains(r.Stem)) result.Carried.Add(r.Stem);
            }
        }

        /// <summary>The temporary name a raster is re-cut under before it replaces the original (never a stem a namelist names).</summary>
        private const string RecutSuffix = ".recut-in-progress.tif";

        /// <summary>
        /// Warps <paramref name="source"/> onto the grid as <paramref name="destination"/>, leaving nothing at the
        /// destination if the warp fails - and the source untouched either way.
        /// </summary>
        private static void RecutInto(Options o, Result result, MasterGrid grid, string stem, string source,
            string destination, Action<string> log)
        {
            void Remove()
            {
                foreach (string f in new[] { destination, destination + ".aux.xml" })
                {
                    try { if (File.Exists(f)) File.Delete(f); } catch { }
                }
            }

            Remove();
            try
            {
                WarpLayer(o, result, grid, stem, source, destination, log);
            }
            catch
            {
                Remove();
                throw;
            }
            try { if (File.Exists(destination + ".aux.xml")) File.Delete(destination + ".aux.xml"); } catch { }
        }

        /// <summary>Whether a raster has the grid's size, origin and cell size (to a tenth of a cell).</summary>
        private static bool IsOnGrid(string path, MasterGrid grid)
        {
            try
            {
                MasterGrid g = MasterGrid.FromRasterFile(path);
                double tolerance = 0.1 * grid.Header.CellSize;
                return g.Header.Ncols == grid.Header.Ncols && g.Header.Nrows == grid.Header.Nrows
                       && System.Math.Abs(g.Header.CellSize - grid.Header.CellSize) <= 0.001 * grid.Header.CellSize
                       && System.Math.Abs(g.XMin - grid.XMin) <= tolerance && System.Math.Abs(g.YMax - grid.YMax) <= tolerance;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The manifest key the builder records its own namelist's hash under.</summary>
        private const string GeneratedNamelistKey = "GeneratedNamelistSha256";

        /// <summary>One value from the case's <see cref="SourceManifestName"/>, or null.</summary>
        private static string ReadManifestValue(string caseDirectory, string key)
        {
            string path = Path.Combine(caseDirectory, SourceManifestName);
            if (!File.Exists(path)) return null;

            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch { }

            return null;
        }

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
                    //What this build wrote as elmfire.data. The next build regenerates it and uses this to tell
                    //its own namelist from one edited by hand, which it keeps aside rather than overwriting.
                    GeneratedNamelistKey + "=" + (result.NamelistSha256 ?? string.Empty),
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
            foreach (string s in ElmfireStems.Weather) yield return s;

            //Fuel, either standard.
            foreach (string s in ElmfireStems.Fuel) yield return s;

            //Canopy.
            yield return "cc";
            yield return "ch";
            yield return "cbh";
            yield return "cbd";

            //Masks, barriers and the suppression difficulty index.
            yield return ElmfireStems.IgnitionMask;
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

        /// <summary>
        /// Layers a case may or may not carry: validated for registration when present, not missed when absent.
        /// </summary>
        private static IEnumerable<string> OptionalStems()
        {
            yield return "cc";
            yield return "ch";
            yield return "cbh";
            yield return "cbd";
            yield return ElmfireStems.IgnitionMask;

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
        public static (Vector2d southWest, Vector2d northEast) PaddedBounds(Options o)
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
        /// Brings masks painted in Unity into the case: the random-ignition area becomes <c>ignition_mask.tif</c>,
        /// the WUI area becomes <c>wui_area.tif</c> (what k-PERIL's <c>WuiAreaFile</c> points at), and a painted
        /// initial ignition becomes an explicit <c>X_IGN</c>/<c>Y_IGN</c> point in the namelist.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Contract C2. The painting carries a cell count and no georeferencing, so the grid it was painted on is
        /// recognised by its dimensions: the case grid first (the grid of record, which the GUI paints on once the
        /// case exists), then the grid this build replaced, then the landscape raster (legacy: paintings made
        /// before the case had a grid). A painting that matches none of them is refused and the build fails -
        /// placing it on a grid of the wrong shape shears it into a different community, and silently skipping it
        /// left the case with no WUI area and the campaign failing hundreds of realizations later.
        /// </para>
        /// <para>
        /// A painted mask that is entirely empty is treated as "not painted" rather than as "ignite nowhere" - an
        /// all-false mask is what an untouched painter produces.
        /// </para>
        /// </remarks>
        private static void ApplyPaintedMasks(Options o, Result result, string inputs, MasterGrid grid,
            string previousGridDirectory, Action<string> log)
        {
            if (string.IsNullOrEmpty(o.PaintedMasksPath)) return;

            if (!File.Exists(o.PaintedMasksPath))
            {
                throw new FileNotFoundException(
                    "The scenario's painted areas (GraphicalFireInputFile) are not there: " + o.PaintedMasksPath);
            }

            PaintedMaskExporter.Masks masks = PaintedMaskExporter.Load(o.PaintedMasksPath);
            var misplaced = new List<string>();
            MasterGrid painted = ResolvePaintedGrid(masks, grid, previousGridDirectory, o.PaintedMasksGridPath,
                                     out string paintedOn, misplaced);

            if (painted == null)
            {
                string landscape = string.IsNullOrEmpty(o.PaintedMasksGridPath) || !File.Exists(o.PaintedMasksGridPath)
                    ? "no landscape raster"
                    : "the landscape raster " + Path.GetFileName(o.PaintedMasksGridPath) + " is "
                      + DescribeDimensions(o.PaintedMasksGridPath);
                throw new InvalidDataException(
                    $"The painted areas in {Path.GetFileName(o.PaintedMasksPath)} are {masks.Ncols}x{masks.Nrows} cells, "
                    + $"but the fire-case grid is {grid.Header.Ncols}x{grid.Header.Nrows} and {landscape}"
                    + (misplaced.Count > 0 ? " (" + string.Join("; ", misplaced) + ")" : "")
                    + ", so there is no telling which ground they were painted on. Move the painting onto the fire-case "
                    + "grid (the GUI's workflow step 6 offers it when it knows the grid it was painted on) or repaint the "
                    + "ignition and WUI areas on it, then build again.");
            }

            if (!ReferenceEquals(painted, grid)) result.PaintingOffCaseGrid = paintedOn;
            log($"  painted: {masks.Ncols}x{masks.Nrows} painting placed via {paintedOn}"
                + (masks.Grid == null
                    ? " - matched by its size alone, since the file does not record where its grid lies."
                    : $", which the file places at {masks.Grid.XllCorner:F1}, {masks.Grid.YllCorner:F1}."));

            if (masks.Any(masks.RandomIgnition))
            {
                PaintedMaskExporter.Export(masks.RandomIgnition, masks, painted, grid,
                    ElmfireStems.Tif(inputs, ElmfireStems.IgnitionMask));
                if (!result.Written.Contains(ElmfireStems.IgnitionMask)) result.Written.Add(ElmfireStems.IgnitionMask);
                log($"  painted: ignition area -> ignition_mask.tif ({masks.Count(masks.RandomIgnition)} painted cells).");
            }

            if (masks.Any(masks.WuiArea))
            {
                string wui = ElmfireStems.Tif(inputs, ElmfireStems.WuiArea);
                PaintedMaskExporter.Export(masks.WuiArea, masks, painted, grid, wui);
                if (!result.Written.Contains(ElmfireStems.WuiArea)) result.Written.Add(ElmfireStems.WuiArea);
                result.WuiAreaFile = wui;
                log($"  painted: WUI area -> wui_area.tif ({masks.Count(masks.WuiArea)} painted cells).");
            }

            if (masks.Any(masks.InitialIgnition) &&
                PaintedMaskExporter.TryGetIgnitionPoint(masks.InitialIgnition, masks, painted, grid, out double ix, out double iy))
            {
                result.Ignitions.Add(new PlacedIgnition { X = ix, Y = iy, TimeSeconds = 0.0 });
                log($"  painted: initial ignition -> X_IGN/Y_IGN ({ix:F1}, {iy:F1}), random ignition disabled.");
            }
        }

        /// <summary>
        /// The grid a painting of this shape was made on, per the rule in <see cref="ApplyPaintedMasks"/>: the right
        /// size, and - when the file records where its grid lies - in the same place, to half a cell, with the same
        /// cell size and CRS. A candidate of the right size in the wrong place is described in
        /// <paramref name="misplaced"/>.
        /// </summary>
        private static MasterGrid ResolvePaintedGrid(PaintedMaskExporter.Masks masks, MasterGrid caseGrid,
            string previousGridDirectory, string landscapePath, out string paintedOn, List<string> misplaced = null)
        {
            paintedOn = null;

            bool Fits(MasterGrid g, string what)
            {
                if (masks.Ncols != g.Header.Ncols || masks.Nrows != g.Header.Nrows) return false;

                string why = DescribePaintedGridMismatch(masks.Grid, g);
                if (why == null) return true;
                misplaced?.Add(what + " is the right size but " + why);
                return false;
            }

            if (Fits(caseGrid, "the fire-case grid"))
            {
                paintedOn = "the fire-case grid";
                return caseGrid;
            }

            if (previousGridDirectory != null)
            {
                string previousDem = ElmfireStems.Tif(previousGridDirectory, ElmfireStems.Dem);
                if (File.Exists(previousDem))
                {
                    MasterGrid previous = TryReadGrid(previousDem, "the case grid this build replaced", misplaced);
                    if (previous != null && Fits(previous, "the case grid this build replaced"))
                    {
                        paintedOn = "the case grid this build replaced";
                        return previous;
                    }
                }
            }

            if (!string.IsNullOrEmpty(landscapePath) && File.Exists(landscapePath))
            {
                MasterGrid landscape = TryReadGrid(landscapePath, "the landscape raster " + Path.GetFileName(landscapePath), misplaced);
                if (landscape != null && Fits(landscape, "the landscape raster " + Path.GetFileName(landscapePath)))
                {
                    paintedOn = "the landscape raster " + Path.GetFileName(landscapePath) + " (painted before the case grid existed)";
                    return landscape;
                }
            }

            return null;
        }

        /// <summary>
        /// A candidate grid for a painting, or null with the reason added to <paramref name="unusable"/>: Mati's own
        /// 27.59 x 27.62 m DEM is refused as a grid (non-square cells), which used to end the build with that message
        /// instead of the one about the painting.
        /// </summary>
        private static MasterGrid TryReadGrid(string path, string what, List<string> unusable)
        {
            try
            {
                return MasterGrid.FromRasterFile(path);
            }
            catch (InvalidOperationException e)
            {
                unusable?.Add(what + " cannot hold a painting the build can place (" + e.Message + ")");
                return null;
            }
        }

        /// <summary>
        /// Why a painting recorded at <paramref name="recorded"/> was not painted on <paramref name="grid"/>, or null
        /// when it was - or cannot be told, because the file records no position.
        /// </summary>
        public static string DescribePaintedGridMismatch(GraphicalFireInput.PaintedGrid recorded, MasterGrid grid)
        {
            //The rule itself is GraphicalFireInput's, so the resampler and the GUI apply the same one.
            return recorded?.DescribeMismatch(grid.XMin, grid.YMin, grid.Header.CellSize,
                GraphicalFireInput.PaintedGrid.EpsgNumber(grid.Epsg));
        }

        private static string DescribeDimensions(string rasterPath)
        {
            try
            {
                MasterGrid g = MasterGrid.FromRasterFile(rasterPath);
                return $"{g.Header.Ncols}x{g.Header.Nrows}";
            }
            catch (Exception e)
            {
                return "unreadable (" + e.Message + ")";
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
            //The stem the run will burn (the template's FBFM_FILENAME, else the case's own), so the restriction is
            //applied against the fuel model ELMFIRE will actually use - reading a fixed fbfm13.tif meant this
            //quietly did nothing on every case whose fuel raster is fbfm40.tif.
            string fuelStem = result.FuelStem;
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

                    if (wasIgnitable && !ElmfireStems.IsBurnable(fuel[x, y], o.NonBurnableFuelCodes))
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
        /// choose: fuel and building layers are external products (docs/elmfire-cases.md, "Where the layers
        /// come from") and arrive under whichever name the pipeline that made
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
                HasFuelModelFile = File.Exists(Path.Combine(r.InputsDirectory, ElmfireStems.FuelModelTable)),
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
