//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;

namespace PREACT.Input
{
    /// <summary>
    /// Settings for running ELMFIRE itself - the Fortran model - as the scenario's fire.
    ///
    /// ELMFIRE is a batch program: it computes a whole fire to completion and writes rasters, so it cannot
    /// be advanced a timestep at a time from inside the simulation loop. The module therefore runs it once
    /// when the simulation starts and then reads its output as the fire, which is the same thing
    /// <c>AscImport</c> does with a fire computed anywhere else. Existing output for an unchanged case is
    /// reused, so only the first run pays for it.
    ///
    /// Not to be confused with the settings the cell-based <c>CellSpread</c> module takes: that is
    /// WUInity's own spread model, and this section used to configure it under this name.
    /// </summary>
    [System.Serializable]
    public class ElmfireInput
    {
        /// <summary>
        /// Where the case is built and run, relative to the scenario. Holds <c>inputs/</c>,
        /// <c>outputs/</c>, <c>scratch/</c> and the namelist, which is the layout ELMFIRE and the
        /// realization writer both expect.
        /// </summary>
        public string CaseDirectory = "elmfire";

        /// <summary>
        /// <c>elmfire.exe</c>. Left empty, the one vendored under
        /// <c>ThirdParty/elmfire/build/windows/bin</c> is used when it can be found.
        /// </summary>
        public string ElmfireExe = string.Empty;

        /// <summary>
        /// A namelist to patch rather than generate. This is the point of a template: the physics is the
        /// user's to tune and only the grid, the weather stems and the ignition are WUInity's to fill in.
        /// Empty means the builder writes one from scratch.
        /// </summary>
        public string NamelistTemplate = string.Empty;

        /// <summary>
        /// How long ELMFIRE simulates, in <b>hours</b>. Independent of the evacuation's own end time.
        /// </summary>
        /// <remarks>
        /// Hours because that is the unit the decision is actually made in — a fire is run for eight hours or
        /// for three days, never for 259200 of anything — and because the two numbers this has to be reasoned
        /// against are both in hours: the weather series carries one band per hour, and ELMFIRE refuses a run
        /// with fewer bands than it has hours to cover. In seconds that comparison was arithmetic every time.
        ///
        /// Converted to seconds at the one place ELMFIRE is told about it. A scenario written before this key
        /// existed is still read: <c>SimulationTstopSeconds</c> is accepted and divided by 3600.
        /// </remarks>
        public double SimulationTstopHours = 8.0;

        /// <summary>
        /// The same duration in seconds, which is what ELMFIRE's <c>SIMULATION_TSTOP</c> and the case builder
        /// take.
        /// </summary>
        /// <remarks>
        /// A method rather than a property on purpose: <see cref="PREACTInputWriter"/> reflects over public
        /// properties as well as fields, so a computed property would be written back into the <c>.wui</c> as a
        /// second key holding the same duration in a different unit — two sources of truth that could drift
        /// apart on the next edit.
        /// </remarks>
        public double TstopSeconds()
        {
            return SimulationTstopHours * 3600.0;
        }

        /// <summary>Master grid resolution in metres. 30 m matches Copernicus GLO-30.</summary>
        public double CellSizeMetres = 30.0;

        /// <summary>
        /// Margin around the evacuation domain, in metres. A fire is free to burn outside the domain, and
        /// clipping it at the edge would truncate the spread a trigger boundary is measuring.
        /// </summary>
        public double PaddingMetres = 2000.0;

        /// <summary>
        /// Reuse output already in the case's <c>outputs/</c> instead of running again. On by default: the
        /// run takes minutes and nothing about it changes between two simulations of the same case.
        /// </summary>
        public bool ReuseExistingOutput = true;

        /// <summary>
        /// Build the case's rasters before running. Off once a case has been prepared - the rasters are the
        /// slow part and they do not change unless the domain does.
        ///
        /// Additive: only what the case is missing is produced. Turning this on for a prepared case is safe
        /// and cheap, and is how a case gains a layer it never had.
        /// </summary>
        public bool BuildCase = false;

        /// <summary>
        /// Rebuild layers the case already has, rather than keeping them.
        /// </summary>
        /// <remarks>
        /// What a changed domain or cell size needs, since every raster then has to be re-cut to the new
        /// grid. Otherwise destructive: it replaces harmonized rasters with freshly warped ones, resamples
        /// canopy back to zero where none was supplied, redraws the weather, and rewrites the namelist -
        /// which is where the physics is tuned.
        /// </remarks>
        public bool RebuildExistingLayers = false;

        /// <summary>GDAL bin directory written into the namelist's PATH_TO_GDAL for ELMFIRE's own shell-outs.</summary>
        public string PathToGdal = string.Empty;

        /// <summary>
        /// <c>WindNinja_cli.exe</c> (<c>WindNinja_cli</c> on Linux), when it is somewhere the standard probe does not
        /// look. Empty means found automatically, which covers the installer's own locations and anything on PATH.
        /// </summary>
        /// <remarks>
        /// Only used while building a case. Without WindNinja the weather stage writes one wind value across
        /// the whole domain, which costs k-PERIL the terrain-driven variation its spread ellipse is built
        /// from - so the boundary comes out isotropic. The build reports which of the two it did.
        /// </remarks>
        public string WindNinjaExe = string.Empty;

        /// <summary>
        /// The modelling choices a generated namelist carries.
        /// </summary>
        /// <remarks>
        /// Its own <c>[ElmfireNamelist]</c> section in the <c>.wui</c> rather than more keys in this one:
        /// there are ninety of them, they are named after ELMFIRE's keys rather than in this codebase's
        /// style, and they answer a different question - this section is about how to run ELMFIRE, that one
        /// is about what to tell it. The writer cannot descend into a nested object, so the section is
        /// written and parsed explicitly, the same way <c>[ELMFIRE]</c> itself is.
        /// </remarks>
        public ElmfireNamelistInput Namelist = new ElmfireNamelistInput();

        /// <summary>The section header the namelist settings are written under.</summary>
        public const string NamelistSection = "ElmfireNamelist";

        // ------------------------------------------------------------------ source layers
        //
        // Fuel, canopy and buildings have no global source the builder can download, so they come from
        // outside. They used to be reachable only as PREACTcli --fbfm40/--cc/... arguments, which meant a case
        // prepared from the GUI could not have them at all: canopy silently defaulted to zero (surface fire
        // only, no crown fire) and the building spread model stayed off, with the scenario recording none of
        // it. Naming them here makes the case reproducible from the .wui alone.
        //
        // Any CRS and any resolution: each is warped onto the case's master grid when the case is built,
        // nearest-neighbour for the categorical ones and bilinear for the continuous ones. Paths are relative
        // to the scenario folder. A layer the case already carries is kept rather than re-warped unless
        // RebuildExistingLayers is on, so naming a source here is safe on a prepared case.

        /// <summary>Which fuel model standard <see cref="FuelModelFile"/> holds, and so which stem it becomes.</summary>
        public enum FuelModelStandards
        {
            /// <summary>Scott &amp; Burgan 40, written as <c>fbfm40.tif</c>. What LANDFIRE and the global products ship.</summary>
            FBFM40,

            /// <summary>Anderson 13, written as <c>fbfm13.tif</c>.</summary>
            FBFM13,
        }

        /// <summary>Surface fuel model raster. Categorical — resampled nearest-neighbour, never interpolated,
        /// since averaging model 1 and model 9 would produce model 5, a fuel neither cell contains.</summary>
        public string FuelModelFile = string.Empty;

        public FuelModelStandards FuelModelStandard = FuelModelStandards.FBFM40;

        /// <summary>
        /// A folder holding the FIRE-RES pan-European canopy rasters, supplying whichever of the four canopy
        /// layers below are not named individually.
        /// </summary>
        /// <remarks>
        /// Canopy is the only layer group with no global source the builder can fetch, which is why a case
        /// outside the United States has had zero canopy — and therefore surface fire only, no crown fire —
        /// unless four rasters were supplied by hand. Pointing this at the dataset once covers every case in
        /// Europe: the continent-sized rasters are clipped to each case's grid as it is built.
        ///
        /// The dataset stores <b>real</b> units where LANDFIRE stores scaled integers, and ELMFIRE defaults to
        /// expecting LANDFIRE's. A case built from here therefore has <c>CH_TIMES_10</c>, <c>CBH_TIMES_10</c>
        /// and <c>CBD_TIMES_100</c> forced off, whatever the Fire behaviour tab says.
        ///
        /// Only canopy is taken. Terrain stays the case's own DEM from OpenTopography, with slope and aspect
        /// recomputed from it, and the fuel model stays whatever raster the scenario names.
        /// </remarks>
        public string CanopyDatasetFolder = string.Empty;

        /// <summary>Canopy cover, percent. Also shades Nelson's dead fuel sticks when present.</summary>
        public string CanopyCoverFile = string.Empty;

        /// <summary>Canopy height. See <c>[ElmfireNamelist] CH_TIMES_10</c> for the LANDFIRE scaled-integer trap.</summary>
        public string CanopyHeightFile = string.Empty;

        public string CanopyBaseHeightFile = string.Empty;
        public string CanopyBulkDensityFile = string.Empty;

        /// <summary>Mean building area. First of the five that together switch <c>USE_BLDG_SPREAD_MODEL</c> on.</summary>
        public string BuildingAreaFile = string.Empty;

        public string BuildingSeparationFile = string.Empty;
        public string BuildingNonBurnableFractionFile = string.Empty;
        public string BuildingFootprintFractionFile = string.Empty;

        /// <summary>Building fuel model codes. Categorical, and paired with <c>building_fuel_models.csv</c>.</summary>
        public string BuildingFuelModelFile = string.Empty;

        /// <summary>Where ELMFIRE may place its own ignitions. Categorical: interpolating a binary mask
        /// invents fractional "partly ignitable" cells along its edge.</summary>
        public string IgnitionMaskFile = string.Empty;

        /// <summary>Fuel breaks and other barriers to spread.</summary>
        public string BarriersFile = string.Empty;

        /// <summary>
        /// Suppression difficulty index, for the extended attack model's containment rate.
        /// </summary>
        /// <remarks>
        /// Nothing in this platform produces one, so it is a raster the user brings. Named here rather than left
        /// out because <c>USE_SDI</c> fails ELMFIRE's own input check without a file behind it, and a switch that
        /// cannot be honoured is worse than one that is absent.
        /// </remarks>
        public string SuppressionDifficultyFile = string.Empty;

        // The rest of what ELMFIRE can read and this platform does not produce. Each one is required by a
        // switch — ELMFIRE checks the filename is set and shuts down if it is not — so each is named here and
        // the switch is written only for a case that has the raster.

        /// <summary>Land value per cell, for <c>USE_LAND_VALUE</c>'s exposure accounting.</summary>
        public string LandValueFile = string.Empty;

        /// <summary>People per cell, for <c>USE_POPULATION_DENSITY</c>.</summary>
        public string PopulationDensityFile = string.Empty;

        /// <summary>Real estate value per cell, for <c>USE_REAL_ESTATE_VALUE</c>.</summary>
        public string RealEstateValueFile = string.Empty;

        /// <summary>
        /// Energy release component, one band per weather band, for <c>USE_ERC</c>'s ignition-rate model.
        /// </summary>
        public string EnergyReleaseComponentFile = string.Empty;

        /// <summary>
        /// Pyrome identifiers, for <c>USE_PYROMES</c> and the per-pyrome calibration tables.
        /// </summary>
        public string PyromesFile = string.Empty;

        /// <summary>One source layer: its <c>[ELMFIRE]</c> key, the ELMFIRE stem it becomes, and its field.</summary>
        private sealed class SourceLayer
        {
            public readonly string Key;
            public readonly string Stem;
            public readonly Func<ElmfireInput, string> Get;
            public readonly Action<ElmfireInput, string> Set;

            public SourceLayer(string key, string stem, Func<ElmfireInput, string> get, Action<ElmfireInput, string> set)
            {
                Key = key;
                Stem = stem;
                Get = get;
                Set = set;
            }
        }

        /// <summary>
        /// Every source layer but the fuel model (whose stem depends on <see cref="FuelModelStandard"/>), in the
        /// order they are listed to ELMFIRE.
        /// </summary>
        /// <remarks>
        /// The one list the parser, <see cref="GetSourceRasters"/> and <see cref="LoadSourcesFromCase"/> all read, so
        /// that adding a layer is one edit: the stems are ELMFIRE's filenames and the keys are this codebase's, and
        /// nothing else should have to know both. There used to be three lists, and the parser's lacked the last six
        /// layers: <c>SuppressionDifficultyFile</c> to <c>PyromesFile</c> were saved but not read back, so the next
        /// save of a reopened scenario dropped them.
        /// </remarks>
        private static readonly SourceLayer[] SourceLayers =
        {
            new SourceLayer(nameof(CanopyCoverFile), "cc", e => e.CanopyCoverFile, (e, v) => e.CanopyCoverFile = v),
            new SourceLayer(nameof(CanopyHeightFile), "ch", e => e.CanopyHeightFile, (e, v) => e.CanopyHeightFile = v),
            new SourceLayer(nameof(CanopyBaseHeightFile), "cbh", e => e.CanopyBaseHeightFile, (e, v) => e.CanopyBaseHeightFile = v),
            new SourceLayer(nameof(CanopyBulkDensityFile), "cbd", e => e.CanopyBulkDensityFile, (e, v) => e.CanopyBulkDensityFile = v),
            new SourceLayer(nameof(BuildingAreaFile), "bldg_area_avg", e => e.BuildingAreaFile, (e, v) => e.BuildingAreaFile = v),
            new SourceLayer(nameof(BuildingSeparationFile), "bldg_separation_distance", e => e.BuildingSeparationFile, (e, v) => e.BuildingSeparationFile = v),
            new SourceLayer(nameof(BuildingNonBurnableFractionFile), "bldg_nonburnable_frac", e => e.BuildingNonBurnableFractionFile, (e, v) => e.BuildingNonBurnableFractionFile = v),
            new SourceLayer(nameof(BuildingFootprintFractionFile), "bldg_footprint_frac", e => e.BuildingFootprintFractionFile, (e, v) => e.BuildingFootprintFractionFile = v),
            new SourceLayer(nameof(BuildingFuelModelFile), "bldg_fuel_model", e => e.BuildingFuelModelFile, (e, v) => e.BuildingFuelModelFile = v),
            new SourceLayer(nameof(IgnitionMaskFile), "ignition_mask", e => e.IgnitionMaskFile, (e, v) => e.IgnitionMaskFile = v),
            new SourceLayer(nameof(BarriersFile), "barriers", e => e.BarriersFile, (e, v) => e.BarriersFile = v),
            new SourceLayer(nameof(SuppressionDifficultyFile), "sdi", e => e.SuppressionDifficultyFile, (e, v) => e.SuppressionDifficultyFile = v),
            new SourceLayer(nameof(LandValueFile), "land_value", e => e.LandValueFile, (e, v) => e.LandValueFile = v),
            new SourceLayer(nameof(PopulationDensityFile), "population_density", e => e.PopulationDensityFile, (e, v) => e.PopulationDensityFile = v),
            new SourceLayer(nameof(RealEstateValueFile), "real_estate_value", e => e.RealEstateValueFile, (e, v) => e.RealEstateValueFile = v),
            new SourceLayer(nameof(EnergyReleaseComponentFile), "erc", e => e.EnergyReleaseComponentFile, (e, v) => e.EnergyReleaseComponentFile = v),
            new SourceLayer(nameof(PyromesFile), "pyromes", e => e.PyromesFile, (e, v) => e.PyromesFile = v),
        };

        /// <summary>
        /// Fills the source-layer fields from a case's own <c>case_sources.txt</c>.
        /// </summary>
        /// <remarks>
        /// The companion to <see cref="ElmfireNamelistInput.LoadFromNamelist"/>, and needed because the two hold
        /// different facts. The namelist says which rasters the case contains — <c>CC_FILENAME = 'cc'</c> — and
        /// says nothing about where <c>cc.tif</c> was warped out of; only the manifest records that. So reading
        /// the namelist alone could never restore these fields, and a case built once could not be read back
        /// into the editor, edited and rebuilt.
        ///
        /// Written by the builder on every build, so it always describes the case as it currently stands. A case
        /// built before this existed has no manifest and simply reports so.
        /// </remarks>
        public bool LoadSourcesFromCase(string caseDirectory, out int applied, out string problem)
        {
            applied = 0;
            problem = null;

            string path = System.IO.Path.Combine(caseDirectory, Utility.ElmfireCaseBuilder.SourceManifestName);
            if (!System.IO.File.Exists(path))
            {
                problem = "This case has no " + Utility.ElmfireCaseBuilder.SourceManifestName
                          + " - it was built before the builder recorded its sources. Building it again writes one.";
                return false;
            }

            string[] lines;
            try { lines = System.IO.File.ReadAllLines(path); }
            catch (Exception e) { problem = e.Message; return false; }

            //stem -> the field that names its source, mirroring GetSourceRasters in the other direction.
            var byStem = new Dictionary<string, Action<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "fbfm40", v => { FuelModelFile = v; FuelModelStandard = FuelModelStandards.FBFM40; } },
                { "fbfm13", v => { FuelModelFile = v; FuelModelStandard = FuelModelStandards.FBFM13; } },
                { "CanopyDatasetFolder", v => CanopyDatasetFolder = v },
                { "CellSizeMetres", v => { if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d) && d > 0) CellSizeMetres = d; } },
                { "PaddingMetres", v => { if (double.TryParse(v, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double d) && d >= 0) PaddingMetres = d; } },
            };
            foreach (SourceLayer layer in SourceLayers)
            {
                SourceLayer captured = layer;
                byStem[layer.Stem] = v => captured.Set(this, v);
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                if (value.Length == 0) continue;

                if (byStem.TryGetValue(key, out Action<string> set))
                {
                    set(value);
                    ++applied;
                }
            }

            return true;
        }

        /// <summary>
        /// The named source layers as (ELMFIRE stem, path) pairs, skipping the ones left empty.
        /// </summary>
        public IEnumerable<KeyValuePair<string, string>> GetSourceRasters()
        {
            string fuelStem = FuelModelStandard == FuelModelStandards.FBFM13 ? "fbfm13" : "fbfm40";
            if (!string.IsNullOrWhiteSpace(FuelModelFile))
            {
                yield return new KeyValuePair<string, string>(fuelStem, FuelModelFile);
            }

            foreach (SourceLayer layer in SourceLayers)
            {
                string path = layer.Get(this);
                if (!string.IsNullOrWhiteSpace(path)) yield return new KeyValuePair<string, string>(layer.Stem, path);
            }
        }

        public ElmfireInput()
        {
        }

        /// <summary>
        /// Reads the <c>[ELMFIRE]</c> section, and the <c>[ElmfireNamelist]</c> one when the file has it.
        /// A scenario without the second gets namelist defaults, which is what every existing <c>.wui</c>
        /// is: the settings used to be implicit in the code that wrote the namelist.
        /// </summary>
        public static ElmfireInput Parse(string[] inputLines, int startIndex,
            Dictionary<string, int> headerLineIndex, string rootFolder, out bool success)
        {
            ElmfireInput newInput = Parse(inputLines, startIndex, rootFolder, out success);

            if (headerLineIndex != null && headerLineIndex.TryGetValue(NamelistSection, out int namelistIndex))
            {
                //So what the namelist reports is filed under its own section, not [ELMFIRE].
                PREACTInput.ReadingInputMessage(NamelistSection);
                newInput.Namelist = ElmfireNamelistInput.Parse(inputLines, namelistIndex);
            }

            return newInput;
        }

        public static ElmfireInput Parse(string[] inputLines, int startIndex, string rootFolder, out bool success)
        {
            //Nothing here is critical. Every value has a usable default - a value that cannot be read is said and
            //the default kept, as everywhere else in the format - and what actually has to exist - the executable,
            //the case, its rasters - cannot be judged from the file: it is checked when the module is created, where
            //the paths have been resolved and the reason can be specific.
            success = true;

            var newInput = new ElmfireInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);

            if (inputToParse.TryGetValue(nameof(CaseDirectory), out string userInput) && userInput.Length > 0)
            {
                newInput.CaseDirectory = userInput;
            }

            if (inputToParse.TryGetValue(nameof(ElmfireExe), out userInput))
            {
                newInput.ElmfireExe = userInput;
            }

            if (inputToParse.TryGetValue(nameof(NamelistTemplate), out userInput))
            {
                newInput.NamelistTemplate = userInput;
            }

            if (inputToParse.TryGetValue(nameof(PathToGdal), out userInput))
            {
                newInput.PathToGdal = userInput;
            }

            if (inputToParse.TryGetValue(nameof(WindNinjaExe), out userInput))
            {
                newInput.WindNinjaExe = userInput;
            }

            //Source layers. Warned about rather than rejected when a named file is absent: these are only read
            //while building a case, so a scenario whose layers live on a drive that is not mounted is still a
            //perfectly good scenario to open, edit and run against an already-prepared case. But a path that
            //is set and wrong has to say so here - it would otherwise show up as canopy quietly defaulting to
            //zero, which reads as "this domain has no trees" rather than "your path is wrong".
            //A folder rather than a file, so the file-existence check does not apply; absence is reported when
            //the case is built, where the four expected names can be listed.
            if (inputToParse.TryGetValue(nameof(CanopyDatasetFolder), out userInput))
            {
                newInput.CanopyDatasetFolder = userInput;
            }

            ReadSourceRaster(inputToParse, nameof(FuelModelFile), ref newInput.FuelModelFile, rootFolder);
            foreach (SourceLayer layer in SourceLayers)
            {
                string path = layer.Get(newInput);
                ReadSourceRaster(inputToParse, layer.Key, ref path, rootFolder);
                layer.Set(newInput, path);
            }

            if (inputToParse.TryGetValue(nameof(FuelModelStandard), out userInput))
            {
                if (System.Enum.TryParse(userInput, true, out FuelModelStandards parsedStandard)
                    && System.Enum.IsDefined(typeof(FuelModelStandards), parsedStandard))
                {
                    newInput.FuelModelStandard = parsedStandard;
                }
                else
                {
                    PREACTInput.CouldNotInterpretInputMessage(nameof(FuelModelStandard), userInput, false, newInput.FuelModelStandard.ToString());
                }
            }

            ReadDouble(inputToParse, nameof(SimulationTstopHours), ref newInput.SimulationTstopHours);

            //The key this replaced, still read so an existing scenario keeps its duration. Only consulted when
            //the hours key is absent, so a file carrying both is not ambiguous - and saying so, because a
            //duration silently reinterpreted by a factor of 3600 would be the worst possible outcome here.
            if (!inputToParse.ContainsKey(nameof(SimulationTstopHours))
                && inputToParse.TryGetValue("SimulationTstopSeconds", out userInput)
                && double.TryParse(userInput, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double legacySeconds)
                && legacySeconds > 0.0)
            {
                newInput.SimulationTstopHours = legacySeconds / 3600.0;
                Engine.Message(null, Engine.LogType.Log,
                    $"[ELMFIRE] SimulationTstopSeconds={legacySeconds:F0} was read as "
                    + $"{newInput.SimulationTstopHours:F2} h. That key is superseded by SimulationTstopHours "
                    + "and saving the scenario will write the new one.");
            }
            ReadDouble(inputToParse, nameof(CellSizeMetres), ref newInput.CellSizeMetres);
            ReadDouble(inputToParse, nameof(PaddingMetres), ref newInput.PaddingMetres);
            ReadBool(inputToParse, nameof(ReuseExistingOutput), ref newInput.ReuseExistingOutput);
            ReadBool(inputToParse, nameof(BuildCase), ref newInput.BuildCase);
            ReadBool(inputToParse, nameof(RebuildExistingLayers), ref newInput.RebuildExistingLayers);

            return newInput;
        }

        /// <summary>
        /// Reads a source-layer path, keeping it scenario-relative but reporting one that does not resolve.
        /// </summary>
        /// <remarks>
        /// The field keeps the path as written rather than an absolute one, so the scenario stays portable —
        /// the builder resolves it against the root folder when it actually needs the file. The existence check
        /// is a warning, not a failure: these are read only while building a case, so a scenario whose source
        /// drive is not mounted is still openable and runnable against an already-prepared case.
        /// </remarks>
        private static void ReadSourceRaster(Dictionary<string, string> input, string key, ref string field,
            string rootFolder)
        {
            if (!input.TryGetValue(key, out string userInput) || userInput.Length == 0) return;

            //Stored with forward slashes and resolved the way every other scenario path is (either slash, a file
            //since moved within the scenario's folders): a Windows-written "sources\fbfm40.tif" was one file name
            //with a backslash in it on Linux, so a case built there had no fuel and no canopy (e2e F4).
            field = PREACTInput.NormalisePath(userInput);

            string resolved = string.IsNullOrEmpty(rootFolder) ? field : PREACTInput.ResolvePath(rootFolder, field);

            if (!System.IO.File.Exists(resolved))
            {
                Engine.Message(null, Engine.LogType.Warning,
                    $"{key} names {userInput}, which is not there. Building the case will skip that layer - "
                    + "which for canopy means defaulting it to zero, so surface fire only.");
            }
        }

        //An unreadable value keeps the default and says so; it used to be dropped without a word, so a scenario whose
        //SimulationTstopHours=24h burned for the default 8 hours.
        private static void ReadDouble(Dictionary<string, string> input, string key, ref double field)
        {
            if (!input.TryGetValue(key, out string userInput))
            {
                return;
            }

            if (InputParse.Double(userInput, out double parsed))
            {
                field = parsed;
            }
            else
            {
                PREACTInput.CouldNotInterpretInputMessage(key, userInput, false, InputParse.Format(field));
            }
        }

        private static void ReadBool(Dictionary<string, string> input, string key, ref bool field)
        {
            if (!input.TryGetValue(key, out string userInput))
            {
                return;
            }

            if (InputParse.Bool(userInput, out bool parsed))
            {
                field = parsed;
            }
            else
            {
                PREACTInput.CouldNotInterpretInputMessage(key, userInput, false, field ? "true" : "false");
            }
        }
    }
}
