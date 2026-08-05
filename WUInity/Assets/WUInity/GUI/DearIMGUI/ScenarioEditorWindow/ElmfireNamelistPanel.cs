using ImGuiNET;
using System;
using UnityEngine;
using PREACT.Input;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// The modelling half of the ELMFIRE settings - everything that becomes a key in the generated
    /// <c>elmfire.data</c>.
    /// </summary>
    /// <remarks>
    /// Its own file, and its own collapsing sections, because there are ninety of these and they belong to
    /// ELMFIRE rather than to WUInity: the sections mirror the namelist's own groups so a value can be found
    /// from ELMFIRE's documentation, and each control is labelled with the key it writes. What is
    /// deliberately absent is anything the case already answers - filenames, the grid, the time base, the
    /// ignition coordinates. Offering those here would be offering the chance to disagree with the rasters,
    /// and a namelist that disagrees with its case fails in ways that name the wrong thing.
    /// </remarks>
    internal static class ElmfireNamelistPanel
    {
        private static readonly Vector4 NoteColor = new Vector4(0.65f, 0.65f, 0.65f, 1f);
        private static readonly Vector4 WarnColor = new Vector4(0.9f, 0.7f, 0.2f, 1f);

        /// <summary>ELMFIRE's own VALID_SURFACE_MODELS, which it checks the namelist against.</summary>
        private static readonly string[] SurfaceSpreadModelNames =
            System.Enum.GetNames(typeof(ElmfireNamelistInput.SurfaceSpreadModels));

        private static readonly string[] GenerationModels =
            Enum.GetNames(typeof(ElmfireNamelistInput.EmberGenerationModels));
        private static readonly string[] DistanceModels =
            Enum.GetNames(typeof(ElmfireNamelistInput.SpottingDistanceModels));
        private static readonly string[] AccumulationModels =
            Enum.GetNames(typeof(ElmfireNamelistInput.EmberAccumulationModels));
        private static readonly string[] IgnitionModels =
            Enum.GetNames(typeof(ElmfireNamelistInput.SpotIgnitionModels));

        //The superseded formulation's own distance switch. A plain string rather than an enum because it is the
        //only key of its kind and ELMFIRE tests it for 'UNIFORM' alone, treating anything else as lognormal.
        private static readonly string[] DistributionTypes = { "UNIFORM", "LOGNORMAL" };

        public static void Draw(ElmfireInput elmfire)
        {
            ElmfireNamelistInput n = elmfire.Namelist;

            if (!string.IsNullOrEmpty(elmfire.NamelistTemplate))
            {
                ImGui.TextColored(WarnColor, "A namelist template is set, so none of this is used.");
                ImGui.TextWrapped("The scenario points at " + elmfire.NamelistTemplate + ", and that file's own "
                    + "values win. Clear NamelistTemplate above to build the namelist from these settings instead.");
                ImGui.Separator();
            }

            if (ImGui.Button("Preview the namelist these settings produce"))
            {
                ElmfireNamelistPreviewWindow.Open(elmfire);
            }
            ImGui.SameLine();
            if (ImGui.Button("Reset all to defaults"))
            {
                elmfire.Namelist = new ElmfireNamelistInput();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("ELMFIRE's own defaults, except where the case builder's rasters make "
                    + "another value the only correct one.");
            }

            //------------------------------------------------------------------ fuel and canopy
            if (ImGui.CollapsingHeader("Fuel and canopy"))
            {
                ImGui.Indent();
                ImGui.TextColored(NoteColor, "Which fuel raster is used is the case's answer, not a setting:");
                ImGui.TextColored(NoteColor, "fbfm40 if the case has one, otherwise fbfm13.");

                Flag("CC_IN_PERCENT", ref n.CC_IN_PERCENT,
                    "Canopy cover is 0-100 rather than 0-1.");
                Flag("CBD_TIMES_100", ref n.CBD_TIMES_100,
                    "Canopy bulk density is stored multiplied by 100, as LANDFIRE ships it. WUInity writes "
                    + "real kg/m3, so this stays off for a case it built.");
                Flag("CBH_TIMES_10", ref n.CBH_TIMES_10,
                    "Canopy base height is stored multiplied by 10. Off for real metres.");
                Flag("CH_TIMES_10", ref n.CH_TIMES_10,
                    "Canopy height is stored multiplied by 10. Off for real metres.");
                ImGui.TextColored(NoteColor, "These three describe the rasters, not a preference. Wrong values");
                ImGui.TextColored(NoteColor, "model a forest a tenth of its real height, with no error.");
                ImGui.Unindent();
            }

            //------------------------------------------------------------------ surface spread model
            if (ImGui.CollapsingHeader("Surface spread model"))
            {
                ImGui.Indent();

                Choice("SURFACE_SPREAD_MODEL", SurfaceSpreadModelNames, ref n.SURFACE_SPREAD_MODEL,
                    "Which surface spread model ELMFIRE uses. It validates this against its own list and "
                    + "refuses to start on anything else.");

                //The Canadian system's two seeds. Disabled rather than hidden under Rothermel, so a scenario
                //that carries them keeps showing them and it is visible why they do nothing - ELMFIRE reads
                //both only inside its CFFDRS branch.
                bool cffdrs = n.SURFACE_SPREAD_MODEL == ElmfireNamelistInput.SurfaceSpreadModels.CFFDRS;
                ImGui.BeginDisabled(!cffdrs);
                Real("START_DC", ref n.START_DC, "Starting drought code. Read only by CFFDRS.");
                Real("START_DMC", ref n.START_DMC, "Starting duff moisture code. Read only by CFFDRS.");
                ImGui.EndDisabled();

                if (!cffdrs)
                {
                    ImGui.TextColored(NoteColor, "START_DC and START_DMC are read only by CFFDRS, so they are");
                    ImGui.TextColored(NoteColor, "written as comments while Rothermel is selected.");
                }

                ImGui.Unindent();
            }

            //------------------------------------------------------------------ exposure and I/O
            if (ImGui.CollapsingHeader("Exposure tracking and raster I/O"))
            {
                ImGui.Indent();

                ImGui.TextColored(NoteColor, "Each of these three needs a raster the case builder does not");
                ImGui.TextColored(NoteColor, "produce, so they are for a case whose inputs came from elsewhere.");
                Flag("USE_LAND_VALUE", ref n.USE_LAND_VALUE,
                    "Track land value burned. Needs a land value raster in the case.");
                Flag("USE_POPULATION_DENSITY", ref n.USE_POPULATION_DENSITY,
                    "Track population affected. Needs a population density raster.");
                Flag("USE_REAL_ESTATE_VALUE", ref n.USE_REAL_ESTATE_VALUE,
                    "Track real estate value burned. Needs a real estate value raster.");

                ImGui.Separator();
                Flag("USE_BARRIERS", ref n.USE_BARRIERS,
                    "Let a barrier raster stop spread. Only written when the case carries barriers.tif, which "
                    + "comes from [ELMFIRE] BarriersFile.");

                ImGui.Separator();
                ImGui.TextColored(NoteColor, "I/O strategy rather than physics - none of these change the fire.");
                Flag("USE_TILED_IO", ref n.USE_TILED_IO, "Read and write rasters in tiles rather than whole.");
                Flag("VRT_INSTEAD_OF_TIF", ref n.VRT_INSTEAD_OF_TIF, "Write a GDAL .vrt rather than a .tif.");
                Flag("USE_BSQ_XML_HEADER", ref n.USE_BSQ_XML_HEADER,
                    "Expect an XML sidecar on BSQ inputs. Only for a case built around BSQ rather than GeoTIFF.");
                Flag("USE_EXISTING_BSQS", ref n.USE_EXISTING_BSQS,
                    "Reuse BSQ intermediates already on disk instead of regenerating them.");
                Flag("ONLY_READ_NEEDED_WX_BANDS", ref n.ONLY_READ_NEEDED_WX_BANDS,
                    "Read only the weather bands the ignition needs. Takes effect ONLY together with "
                    + "CSV_FIXED_IGNITION_LOCATIONS - on its own it does nothing, and it is not an alternative "
                    + "to WX_BANDS_KEPT_IN_MEM.");

                ImGui.Unindent();
            }

            //------------------------------------------------------------------ weather and moisture
            if (ImGui.CollapsingHeader("Weather and moisture"))
            {
                ImGui.Indent();
                Real("DT_METEOROLOGY", ref n.DT_METEOROLOGY, "Seconds between weather bands. 3600 = hourly.");

                Whole("MeteorologyBands (0 = from the rasters)", ref n.MeteorologyBands,
                    "How many hourly bands to read. Left at 0, the case's own ws.tif is counted, which is "
                    + "what you want: too many fails the run, too few silently reuses hour one for the "
                    + "whole fire.");
                if (n.MeteorologyBands < 0) n.MeteorologyBands = 0;

                Whole("WX_BANDS_KEPT_IN_MEM", ref n.WX_BANDS_KEPT_IN_MEM,
                    "How many weather bands ELMFIRE holds at once, rolling the window forward as the fire "
                    + "burns past it. This is what keeps a long run's memory flat - 72 bands of a 566x541 "
                    + "domain cost 814 MB held whole against 218 MB at 8 - and it does not change the fire. "
                    + "At or above the case's band count, everything is held.");
                if (n.WX_BANDS_KEPT_IN_MEM < 2) n.WX_BANDS_KEPT_IN_MEM = 2;

                Flag("WS_AT_10M", ref n.WS_AT_10M,
                    "The wind rasters hold 10 m wind rather than ELMFIRE's 20 ft default. Wrong is silent: "
                    + "the same numbers are read at the wrong height and the fire spreads at the wrong rate.");
                Flag("WS_IN_KPH", ref n.WS_IN_KPH, "Wind speed is km/h rather than mph. The pipeline writes mph.");
                Flag("DEAD_MC_IN_PERCENT", ref n.DEAD_MC_IN_PERCENT, "Dead fuel moisture is 0-100 rather than 0-1.");
                Flag("LIVE_MC_IN_PERCENT", ref n.LIVE_MC_IN_PERCENT, "Live fuel moisture is 0-100 rather than 0-1.");

                ImGui.Separator();
                Flag("USE_CONSTANT_LH", ref n.USE_CONSTANT_LH, "One live herbaceous moisture everywhere.");
                Real("LH_MOISTURE_CONTENT", ref n.LH_MOISTURE_CONTENT, "Live herbaceous moisture, percent.");
                Flag("USE_CONSTANT_LW", ref n.USE_CONSTANT_LW, "One live woody moisture everywhere.");
                Real("LW_MOISTURE_CONTENT", ref n.LW_MOISTURE_CONTENT, "Live woody moisture, percent.");
                Flag("USE_CONSTANT_FMC", ref n.USE_CONSTANT_FMC, "One foliar moisture everywhere.");
                Real("FOLIAR_MOISTURE_CONTENT", ref n.FOLIAR_MOISTURE_CONTENT,
                    "Foliar moisture, percent. Drives crown fire initiation.");

                ImGui.Separator();
                Real("GRID_DECLINATION", ref n.GRID_DECLINATION,
                    "Degrees between grid north and true north. 0 for a UTM case built here.");
                Flag("ROTATE_ASP", ref n.ROTATE_ASP, "Rotate aspect by the grid declination.");
                Flag("ROTATE_WD", ref n.ROTATE_WD, "Rotate wind direction by the grid declination.");
                ImGui.Unindent();
            }

            //------------------------------------------------------------------ time stepping
            if (ImGui.CollapsingHeader("Time stepping"))
            {
                ImGui.Indent();
                ImGui.TextColored(NoteColor, "The start and the stop time are the scenario's, above. These are");
                ImGui.TextColored(NoteColor, "how the solver gets from one to the other.");

                Real("SIMULATION_DT", ref n.SIMULATION_DT, "Base timestep, seconds.");
                Real("SIMULATION_DTMAX", ref n.SIMULATION_DTMAX, "Largest timestep the CFL control may grow to.");
                Real("SIMULATION_TSTART", ref n.SIMULATION_TSTART, "Seconds into the weather at which the fire starts.");
                Real("TARGET_CFL", ref n.TARGET_CFL,
                    "Courant number the adaptive timestep aims at. Lower is slower and steadier.");

                ImGui.Separator();
                Flag("USE_DIURNAL_ADJUSTMENT_FACTOR", ref n.USE_DIURNAL_ADJUSTMENT_FACTOR,
                    "Damp the fire outside the burning period. Off for a run of a few hours inside one day.");
                if (n.USE_DIURNAL_ADJUSTMENT_FACTOR)
                {
                    ImGui.Indent();
                    Real("OVERNIGHT_ADJUSTMENT_FACTOR", ref n.OVERNIGHT_ADJUSTMENT_FACTOR,
                        "Multiplier applied outside the burning period.");
                    Real("BURN_PERIOD_LENGTH", ref n.BURN_PERIOD_LENGTH, "Hours of active burning per day.");
                    Real("BURN_PERIOD_CENTER_FRAC", ref n.BURN_PERIOD_CENTER_FRAC,
                        "Where the burning period sits in the day, as a fraction.");
                    ImGui.Unindent();
                }

                ImGui.Separator();
                Flag("RANDOMIZE_SIMULATION_TSTOP", ref n.RANDOMIZE_SIMULATION_TSTOP,
                    "Draw each case's stop time between the start and the stop time instead of running every "
                    + "case the whole way.");
                if (n.RANDOMIZE_SIMULATION_TSTOP)
                {
                    ImGui.TextColored(NoteColor, "The scenario's run length becomes a ceiling, not the length");
                    ImGui.TextColored(NoteColor, "any one case gets.");
                }

                ImGui.SeparatorText("Weather re-read intervals");
                ImGui.TextColored(NoteColor, "Seconds of simulated time between re-reads. The live and foliar");
                ImGui.TextColored(NoteColor, "moistures default to 9e8 s - longer than any run - which holds");
                ImGui.TextColored(NoteColor, "them at their band-one value.");
                Real("DT_INTERPOLATE_M1", ref n.DT_INTERPOLATE_M1, "1-hour dead fuel moisture.");
                Real("DT_INTERPOLATE_M10", ref n.DT_INTERPOLATE_M10, "10-hour dead fuel moisture.");
                Real("DT_INTERPOLATE_M100", ref n.DT_INTERPOLATE_M100, "100-hour dead fuel moisture.");
                Real("DT_INTERPOLATE_MLH", ref n.DT_INTERPOLATE_MLH, "Live herbaceous moisture.");
                Real("DT_INTERPOLATE_MLW", ref n.DT_INTERPOLATE_MLW, "Live woody moisture.");
                Real("DT_INTERPOLATE_FMC", ref n.DT_INTERPOLATE_FMC, "Foliar moisture content.");
                Real("DT_INTERPOLATE_WIND", ref n.DT_INTERPOLATE_WIND, "Wind speed and direction.");

                ImGui.Unindent();
            }

            //------------------------------------------------------------------ ignition
            if (ImGui.CollapsingHeader("Ignition"))
            {
                ImGui.Indent();
                ImGui.TextWrapped("Ignition points placed on the map win over everything here, and switch both "
                    + "of the following off. These are what a case with only a painted mask falls back to, and "
                    + "what an ensemble draws from.");

                Flag("RANDOM_IGNITIONS", ref n.RANDOM_IGNITIONS, "Let ELMFIRE choose where the fire starts.");
                Flag("USE_IGNITION_MASK", ref n.USE_IGNITION_MASK,
                    "Confine that choice to the painted ignition mask, intersected with burnable fuel. "
                    + "Without it the fire can start in the sea, run to completion and report 0 acres.");

                if (n.RANDOM_IGNITIONS)
                {
                    ImGui.Indent();
                    Real("PERCENT_OF_PIXELS_TO_IGNITE", ref n.PERCENT_OF_PIXELS_TO_IGNITE, null);
                    Flag("ALLOW_MULTIPLE_IGNITIONS_AT_A_PIXEL", ref n.ALLOW_MULTIPLE_IGNITIONS_AT_A_PIXEL, null);
                    Whole("RANDOM_IGNITIONS_TYPE", ref n.RANDOM_IGNITIONS_TYPE,
                        "1 samples the mask as a density, 2 as a per-cell probability.");
                    ImGui.Unindent();
                }

                if (n.USE_IGNITION_MASK)
                {
                    ImGui.Indent();
                    Real("IGNITION_MASK_SCALE_FACTOR", ref n.IGNITION_MASK_SCALE_FACTOR,
                        "Scales every value in the mask, and so how many ignitions it produces.");
                    Real("ADD_TO_IGNITION_MASK", ref n.ADD_TO_IGNITION_MASK,
                        "Added to the mask on every burnable cell, so nowhere burnable has zero chance. "
                        + "Negative leaves the mask alone and is not written.");
                    ImGui.Unindent();
                }

                if (n.RANDOM_IGNITIONS && !n.USE_IGNITION_MASK)
                {
                    ImGui.TextColored(WarnColor, "Random ignition with no mask can start the fire anywhere in the");
                    ImGui.TextColored(WarnColor, "domain, including open water.");
                }

                Flag("ALLOW_NONBURNABLE_PIXEL_IGNITION", ref n.ALLOW_NONBURNABLE_PIXEL_IGNITION,
                    "Let an ignition land on a cell that cannot burn. It simply does not spread.");

                Flag("CSV_FIXED_IGNITION_LOCATIONS", ref n.CSV_FIXED_IGNITION_LOCATIONS,
                    "Take ignitions from a CSV in the case's inputs. Nothing here writes one - it is for a case "
                    + "prepared elsewhere.");

                Flag("USE_ERC", ref n.USE_ERC,
                    "Scale the ignition rate by the energy release component, per weather band.");
                if (n.USE_ERC)
                {
                    ImGui.Indent();
                    if (string.IsNullOrWhiteSpace(elmfire.EnergyReleaseComponentFile))
                    {
                        ImGui.TextColored(WarnColor, "No EnergyReleaseComponentFile is set, so this is written");
                        ImGui.TextColored(WarnColor, "off: ELMFIRE will not start with it on and no raster.");
                    }
                    Flag("ERC_IS_PLIGNRATE", ref n.ERC_IS_PLIGNRATE,
                        "Treat the values as a lightning ignition rate rather than a multiplier.");
                    ImGui.Unindent();
                }
                ImGui.Unindent();
            }

            //------------------------------------------------------------------ fire behaviour
            if (ImGui.CollapsingHeader("Fire behaviour"))
            {
                ImGui.Indent();
                Whole("CROWN_FIRE_MODEL", ref n.CROWN_FIRE_MODEL, "1 = Scott & Reinhardt, 2 = Cruz.");
                Real("CRITICAL_CANOPY_COVER", ref n.CRITICAL_CANOPY_COVER,
                    "Cover fraction below which crown fire cannot carry.");
                Real("CROWN_RATIO", ref n.CROWN_RATIO, "Crown ratio where the case has no raster for it.");
                Real("CROWN_FIRE_ADJ", ref n.CROWN_FIRE_ADJ, "Multiplier on crown fire spread rate.");
                Real("CROWN_FIRE_SPREAD_RATE_LIMIT", ref n.CROWN_FIRE_SPREAD_RATE_LIMIT, "Ceiling, ft/min.");

                ImGui.Separator();
                Real("PHIS_ADJ", ref n.PHIS_ADJ, "Slope factor multiplier - a calibration knob, 1.0 is unadjusted.");
                Real("PHIW_ADJ", ref n.PHIW_ADJ, "Wind factor multiplier - a calibration knob, 1.0 is unadjusted.");
                Real("SURFACE_ACCELERATION_TIME_CONSTANT", ref n.SURFACE_ACCELERATION_TIME_CONSTANT,
                    "How quickly a surface fire reaches its equilibrium spread rate.");
                Flag("WX_BILINEAR_INTERPOLATION", ref n.WX_BILINEAR_INTERPOLATION,
                    "Interpolate the weather rasters bilinearly rather than nearest-neighbour.");

                ImGui.Separator();
                Whole("MODE", ref n.MODE, "1 = level set propagation, 2 = fire potential, 3 = both. "
                    + "The fire reader needs 1.");
                if (n.MODE != 1)
                {
                    ImGui.TextColored(WarnColor, "Mode 1 is what produces the arrival-time raster WUInity reads.");
                }
                Whole("BANDTHICKNESS", ref n.BANDTHICKNESS, "Cells of narrow band around the fire front.");
                Whole("FEEDBACK_LEVEL", ref n.FEEDBACK_LEVEL, "Console verbosity, 0 to 3.");
                Real("MAX_RUNTIME", ref n.MAX_RUNTIME, "Wall-clock ceiling in seconds; the run stops when reached.");
                Flag("CLEAN_SCRATCH", ref n.CLEAN_SCRATCH, "Delete the scratch intermediates as it goes.");
                Flag("UNTAG_TYPE_2", ref n.UNTAG_TYPE_2, null);
                Flag("UNTAG_TYPE_3", ref n.UNTAG_TYPE_3, null);
                Whole("UNTAG_CELLS_TIMESTEP_INTERVAL", ref n.UNTAG_CELLS_TIMESTEP_INTERVAL,
                    "How often burned cells leave the tracked set, in timesteps. Larger keeps more cells live "
                    + "and costs time; smaller risks dropping a cell still contributing.");
                Flag("MULTIPLE_HOSTS", ref n.MULTIPLE_HOSTS,
                    "Running across more than one host, which changes how weather is broadcast between ranks.");

                ImGui.SeparatorText("Low-wind branch");
                Real("MAX_LOW", ref n.MAX_LOW, "Upper bound on the low-wind branch of the spread solution, mi/h.");
                Real("WSMFEFF_LOW_MULT", ref n.WSMFEFF_LOW_MULT,
                    "Multiplier turning mid-flame wind into the low-wind branch's effective wind. ELMFIRE's "
                    + "default is 60/5280 - feet per mile - so it is that ratio rather than a round number.");

                ImGui.SeparatorText("Wind fluctuations");
                Flag("WIND_FLUCTUATIONS", ref n.WIND_FLUCTUATIONS,
                    "Vary wind through the run rather than holding each band steady.");
                ImGui.BeginDisabled(!n.WIND_FLUCTUATIONS);
                ImGui.Indent();
                Real("DT_WIND_FLUCTUATIONS", ref n.DT_WIND_FLUCTUATIONS, "How often it is redrawn, seconds.");
                Real("WIND_SPEED_FLUCTUATION_INTENSITY", ref n.WIND_SPEED_FLUCTUATION_INTENSITY,
                    "Magnitude as a fraction of wind speed.");
                Real("WIND_DIRECTION_FLUCTUATION_INTENSITY", ref n.WIND_DIRECTION_FLUCTUATION_INTENSITY,
                    "Magnitude in degrees.");
                ImGui.Unindent();
                ImGui.EndDisabled();

                ImGui.SeparatorText("Other");
                Flag("ESTIMATE_URBAN_LOSSES", ref n.ESTIMATE_URBAN_LOSSES,
                    "Estimate structure losses over the cells that burned. Needs the building and exposure "
                    + "inputs to produce anything.");
                Flag("USE_PYROMES", ref n.USE_PYROMES,
                    "Use a pyrome map for regionalised parameters. Needs a pyromes raster in the case.");
                Real("PLIGNRATE_MIN", ref n.PLIGNRATE_MIN, "Floor on the lightning ignition rate.");

                //Shown so a scenario carrying it is visible, but it is refused when the namelist is written:
                //it makes SEED inert, and every ensemble here varies SEED to produce its realizations.
                Flag("RANDOMIZE_RANDOM_SEED", ref n.RANDOMIZE_RANDOM_SEED,
                    "Seed from the system clock instead of SEED. REFUSED when the namelist is written.");
                if (n.RANDOMIZE_RANDOM_SEED)
                {
                    ImGui.TextColored(WarnColor, "This makes SEED inert, so no run could be reproduced and a");
                    ImGui.TextColored(WarnColor, "campaign could not vary its realizations. Written as .FALSE.");
                }

                ImGui.Unindent();
            }

            //------------------------------------------------------------------ spotting
            if (ImGui.CollapsingHeader("Spotting and embers"))
            {
                ImGui.Indent();
                Flag("ENABLE_SPOTTING", ref n.ENABLE_SPOTTING, "Embers can start fires ahead of the front.");

                if (n.ENABLE_SPOTTING)
                {
                    Flag("USE_SUPERSEDED_SPOTTING", ref n.USE_SUPERSEDED_SPOTTING,
                        "The pre-2023 formulation. ELMFIRE defaults this ON, and with it on the four models "
                        + "below are ignored.");

                    bool superseded = n.USE_SUPERSEDED_SPOTTING;
                    if (!superseded)
                    {
                        Choice("GENERATION_MODEL", GenerationModels, ref n.GENERATION_MODEL,
                            "How many embers are produced: at random, per unit area, or per megawatt.");
                        Choice("SPOTTING_DISTANCE_MODEL", DistanceModels, ref n.SPOTTING_DISTANCE_MODEL,
                            "How far they travel.");
                        Choice("ACCUMULATION_MODEL", AccumulationModels, ref n.ACCUMULATION_MODEL,
                            "How landed embers are accumulated - tracked individually or on the grid.");
                        Choice("IGNITION_MODEL", IgnitionModels, ref n.IGNITION_MODEL,
                            "Whether a landed ember ignites: directly, by a probability, or physically.");

                        if (n.ACCUMULATION_MODEL == ElmfireNamelistInput.EmberAccumulationModels.EULERIAN
                            && n.SPOTTING_DISTANCE_MODEL == ElmfireNamelistInput.SpottingDistanceModels.UNIFORM)
                        {
                            ImGui.TextColored(WarnColor, "ELMFIRE refuses UNIFORM distances with EULERIAN");
                            ImGui.TextColored(WarnColor, "accumulation and will stop at startup.");
                        }
                    }
                    else
                    {
                        ImGui.TextColored(NoteColor, "The four ember models are not written while this is on.");
                        Choice("SPOTTING_DISTRIBUTION_TYPE", DistributionTypes, ref n.SPOTTING_DISTRIBUTION_TYPE,
                            "What the superseded formulation samples distance from.");
                    }

                    bool empirical = !superseded
                        && n.SPOTTING_DISTANCE_MODEL == ElmfireNamelistInput.SpottingDistanceModels.EMPIRICAL;

                    ImGui.SeparatorText("How many embers a cell throws");
                    if (superseded || n.GENERATION_MODEL == ElmfireNamelistInput.EmberGenerationModels.RANDOM)
                    {
                        Whole("NEMBERS_MIN", ref n.NEMBERS_MIN, "Fewest embers a throwing cell produces.");
                        ImGui.BeginDisabled(n.STOCHASTIC_SPOTTING);
                        Whole("NEMBERS_MAX", ref n.NEMBERS_MAX,
                            "Most embers a throwing cell produces. Sampled below when spotting is stochastic.");
                        ImGui.EndDisabled();
                    }
                    else if (n.GENERATION_MODEL == ElmfireNamelistInput.EmberGenerationModels.PER_AREA)
                    {
                        Real("EMBER_GR", ref n.EMBER_GR, "Embers per square metre per second of burning.");
                    }
                    else
                    {
                        Real("EMBER_GR_PER_MW_BLDG", ref n.EMBER_GR_PER_MW_BLDG,
                            "Embers per MW of heat release from burning buildings.");
                        Real("EMBER_GR_PER_MW_VEGE", ref n.EMBER_GR_PER_MW_VEGE,
                            "The same for burning vegetation, which throws about three times as many.");
                    }
                    Real("EMBER_SAMPLING_FACTOR", ref n.EMBER_SAMPLING_FACTOR,
                        "Embers each tracked particle stands for. Above 1 the run tracks fewer and scales the "
                        + "flux up, which is how a large domain is made affordable.");

                    ImGui.SeparatorText("Which cells throw, and what ignites");
                    Flag("ENABLE_SURFACE_FIRE_SPOTTING", ref n.ENABLE_SURFACE_FIRE_SPOTTING,
                        "Surface fire throws embers too, not only crown fire.");
                    Real("CRITICAL_SPOTTING_FIRELINE_INTENSITY", ref n.CRITICAL_SPOTTING_FIRELINE_INTENSITY,
                        "Below this fireline intensity nothing is thrown, kW/m. Applied to every fuel model.");
                    Real("SOURCE_FUEL_IGN_MULT", ref n.SOURCE_FUEL_IGN_MULT,
                        "Multiplier on the ignition probability of embers by the fuel that threw them. "
                        + "Applied to every fuel model.");
                    Flag("DIFF_WILDLAND_IGNITION", ref n.DIFF_WILDLAND_IGNITION,
                        "Treat ignition in wildland fuels differently from ignition of structures.");
                    Flag("USE_EMBER_CONSUMPTION", ref n.USE_EMBER_CONSUMPTION,
                        "Let embers be consumed where they land instead of persisting.");

                    ImGui.SeparatorText("Tuning parameters");
                    Flag("STOCHASTIC_SPOTTING", ref n.STOCHASTIC_SPOTTING,
                        "Draw the eight parameters below per case between their bounds, instead of using single "
                        + "values. This decides which half of this section is read at all.");
                    if (n.STOCHASTIC_SPOTTING)
                    {
                        ImGui.Indent();
                        ImGui.TextColored(NoteColor, "Sampled per case. The single values are not written.");
                        Real("MEAN_SPOTTING_DIST_MIN", ref n.MEAN_SPOTTING_DIST_MIN, "Metres.");
                        Real("MEAN_SPOTTING_DIST_MAX", ref n.MEAN_SPOTTING_DIST_MAX, "Metres.");
                        Real("NORMALIZED_SPOTTING_DIST_VARIANCE_MIN",
                            ref n.NORMALIZED_SPOTTING_DIST_VARIANCE_MIN, null);
                        Real("NORMALIZED_SPOTTING_DIST_VARIANCE_MAX",
                            ref n.NORMALIZED_SPOTTING_DIST_VARIANCE_MAX, null);
                        Real("SPOT_WS_EXP_LO", ref n.SPOT_WS_EXP_LO, "Wind speed exponent, low end.");
                        Real("SPOT_WS_EXP_HI", ref n.SPOT_WS_EXP_HI, "Wind speed exponent, high end.");
                        Real("SPOT_FLIN_EXP_LO", ref n.SPOT_FLIN_EXP_LO, "Fireline intensity exponent, low end.");
                        Real("SPOT_FLIN_EXP_HI", ref n.SPOT_FLIN_EXP_HI, "Fireline intensity exponent, high end.");
                        Whole("NEMBERS_MAX_LO", ref n.NEMBERS_MAX_LO, null);
                        Whole("NEMBERS_MAX_HI", ref n.NEMBERS_MAX_HI, null);
                        Real("GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MIN",
                            ref n.GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MIN, null);
                        Real("GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MAX",
                            ref n.GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MAX, null);
                        Real("CROWN_FIRE_SPOTTING_PERCENT_MIN", ref n.CROWN_FIRE_SPOTTING_PERCENT_MIN, null);
                        Real("CROWN_FIRE_SPOTTING_PERCENT_MAX", ref n.CROWN_FIRE_SPOTTING_PERCENT_MAX, null);
                        Real("PIGN_MIN", ref n.PIGN_MIN, "Ignition probability, as a PERCENT.");
                        Real("PIGN_MAX", ref n.PIGN_MAX, "Ignition probability, as a PERCENT.");
                        Real("SURFACE_FIRE_SPOTTING_PERCENT_MULT", ref n.SURFACE_FIRE_SPOTTING_PERCENT_MULT,
                            "Multiplier turning the sampled global percent into the per-fuel-model percent.");

                        ImGui.TextColored(NoteColor, "ELMFIRE 1.1's sampler does not read the two below:");
                        ImGui.TextColored(NoteColor, "NEMBERS_MIN stays as set above.");
                        Whole("NEMBERS_MIN_LO", ref n.NEMBERS_MIN_LO, null);
                        Whole("NEMBERS_MIN_HI", ref n.NEMBERS_MIN_HI, null);
                        ImGui.Unindent();
                    }
                    else
                    {
                        Real("CROWN_FIRE_SPOTTING_PERCENT", ref n.CROWN_FIRE_SPOTTING_PERCENT,
                            "Percent of crown-fire cells over the threshold that throw embers.");
                        Real("GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT",
                            ref n.GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT, null);
                        Real("SURFACE_FIRE_SPOTTING_PERCENT", ref n.SURFACE_FIRE_SPOTTING_PERCENT,
                            "Percent of surface-fire cells that throw embers. Applied to every fuel model.");
                        Real("PIGN", ref n.PIGN, "Probability a landed ember ignites, as a PERCENT.");
                        Real("SPOT_WS_EXP", ref n.SPOT_WS_EXP, "Wind speed exponent in the spotting distance.");
                        Real("SPOT_FLIN_EXP", ref n.SPOT_FLIN_EXP, "Fireline intensity exponent.");
                    }

                    ImGui.SeparatorText("How far they travel");
                    if (superseded
                        || n.SPOTTING_DISTANCE_MODEL == ElmfireNamelistInput.SpottingDistanceModels.UNIFORM)
                    {
                        Real("MIN_SPOTTING_DISTANCE", ref n.MIN_SPOTTING_DISTANCE, "Metres.");
                        Real("MAX_SPOTTING_DISTANCE", ref n.MAX_SPOTTING_DISTANCE,
                            "Metres. 0 leaves it unbounded.");
                    }
                    if (!n.STOCHASTIC_SPOTTING && !empirical)
                    {
                        Real("MEAN_SPOTTING_DIST", ref n.MEAN_SPOTTING_DIST,
                            "Metres, before the wind and intensity exponents are applied.");
                        Real("NORMALIZED_SPOTTING_DIST_VARIANCE",
                            ref n.NORMALIZED_SPOTTING_DIST_VARIANCE, null);
                    }
                    if (empirical)
                    {
                        Real("P_EPS", ref n.P_EPS,
                            "Tail probability that sets the furthest distance the PDF is sampled to.");
                        Flag("USE_CUSTOMIZED_PDF", ref n.USE_CUSTOMIZED_PDF,
                            "Use the parameters below instead of Sardoy's (wildland) and Himoto's (structure) "
                            + "fits to local wind and fireline intensity.");
                        if (n.USE_CUSTOMIZED_PDF)
                        {
                            ImGui.Indent();
                            Real("MU_DOWNWIND", ref n.MU_DOWNWIND, "Lognormal mean, in log-metres.");
                            Real("SIGMA_DOWNWIND", ref n.SIGMA_DOWNWIND, "Lognormal standard deviation.");
                            ImGui.Unindent();
                        }
                    }
                    Flag("USE_CROSSWIND_DISTRIBUTION", ref n.USE_CROSSWIND_DISTRIBUTION,
                        "Spread embers across the wind rather than sending them all downwind.");
                    if (n.USE_CROSSWIND_DISTRIBUTION)
                    {
                        ImGui.Indent();
                        if (empirical && !n.USE_CUSTOMIZED_PDF)
                        {
                            ImGui.TextColored(NoteColor, "The crosswind parameters come from the empirical fits,");
                            ImGui.TextColored(NoteColor, "so these two are not written.");
                        }
                        ImGui.BeginDisabled(empirical && !n.USE_CUSTOMIZED_PDF);
                        Real("MU_CROSSWIND", ref n.MU_CROSSWIND, "Lognormal mean, in log-metres.");
                        Real("SIGMA_CROSSWIND", ref n.SIGMA_CROSSWIND, "Lognormal standard deviation.");
                        ImGui.EndDisabled();
                        ImGui.Unindent();
                    }

                    ImGui.SeparatorText("How long, and what happens on landing");
                    Flag("USE_PHYSICAL_SPOTTING_DURATION", ref n.USE_PHYSICAL_SPOTTING_DURATION,
                        "Derive how long a cell throws embers from the physics rather than the fixed 1200 s.");
                    Real("TAU_EMBERGEN", ref n.TAU_EMBERGEN, "Ember generation time constant, seconds.");
                    if (!superseded && n.IGNITION_MODEL != ElmfireNamelistInput.SpotIgnitionModels.DIRECT)
                    {
                        Real("LOCAL_IGNITION_TIME", ref n.LOCAL_IGNITION_TIME,
                            "Seconds an ignited cell waits before it starts spreading.");
                        Real("CELL_IGNITION_DELAY", ref n.CELL_IGNITION_DELAY,
                            "Seconds between a cell being ignited by embers and its front being set.");
                    }

                    Flag("NO_SURFACE_FIRE", ref n.NO_SURFACE_FIRE,
                        "Spread by embers alone, with the surface fire suppressed.");
                    if (n.NO_SURFACE_FIRE)
                    {
                        ImGui.TextColored(WarnColor, "A diagnostic for the spotting model, not a way to run");
                        ImGui.TextColored(WarnColor, "a fire: the front itself will not spread.");
                    }
                }
                ImGui.Unindent();
            }

            //------------------------------------------------------------------ suppression
            if (ImGui.CollapsingHeader("Suppression"))
            {
                ImGui.Indent();
                ImGui.TextWrapped("A suppressed fire reaches less far, so a trigger boundary drawn from one is "
                    + "smaller and evacuates later. Only assume a response that will actually happen.");

                ImGui.SeparatorText("Initial attack");
                Flag("ENABLE_INITIAL_ATTACK", ref n.ENABLE_INITIAL_ATTACK,
                    "One suppression event, sized against how fast the fire is growing and how hard its most "
                    + "intense edge is burning.");
                ImGui.BeginDisabled(!n.ENABLE_INITIAL_ATTACK);
                ImGui.Indent();
                Real("INITIAL_ATTACK_TIME", ref n.INITIAL_ATTACK_TIME, "Seconds after ignition that it arrives.");
                ImGui.Unindent();
                ImGui.EndDisabled();

                ImGui.SeparatorText("Extended attack");
                Flag("ENABLE_EXTENDED_ATTACK", ref n.ENABLE_EXTENDED_ATTACK,
                    "Containment growing over days as crews hold progressively more of the perimeter. Needs a "
                    + "run long enough for a day to pass.");
                ImGui.BeginDisabled(!n.ENABLE_EXTENDED_ATTACK);
                ImGui.Indent();
                Real("DT_EXTENDED_ATTACK", ref n.DT_EXTENDED_ATTACK, "Seconds between containment updates.");
                Real("MAX_CONTAINMENT_PER_DAY", ref n.MAX_CONTAINMENT_PER_DAY,
                    "Most of the perimeter containable in one day, as a percent.");
                Real("AREA_NO_CONTAINMENT_CHANGE", ref n.AREA_NO_CONTAINMENT_CHANGE,
                    "Growth rate above which containment stops advancing, in ACRES per day - ELMFIRE's "
                    + "suppression model is written in acres throughout.");
                Flag("USE_SDI_LOG_FUNCTION", ref n.USE_SDI_LOG_FUNCTION,
                    "Take the log of the growth rate before comparing it, which softens the fast-growth end.");

                Flag("USE_SDI", ref n.USE_SDI,
                    "Weight containment by how difficult the burning ground is to suppress.");
                if (n.USE_SDI)
                {
                    ImGui.Indent();
                    if (string.IsNullOrWhiteSpace(elmfire.SuppressionDifficultyFile))
                    {
                        ImGui.TextColored(WarnColor, "No SuppressionDifficultyFile is set, so this is written");
                        ImGui.TextColored(WarnColor, "off: ELMFIRE will not start with it on and no raster.");
                    }
                    Real("SDI_FACTOR", ref n.SDI_FACTOR, "Multiplier on the raster's values.");
                    Real("B_SDI", ref n.B_SDI, "How sharply the difficulty index slows containment.");
                    ImGui.Unindent();
                }
                ImGui.Unindent();
                ImGui.EndDisabled();
                ImGui.Unindent();
            }

            //------------------------------------------------------------------ smoke
            if (ImGui.CollapsingHeader("Smoke emissions"))
            {
                ImGui.Indent();
                ImGui.TextWrapped("ELMFIRE's own PM2.5 accounting: how much was released. Not the GlobalSmoke "
                    + "module, which takes a fire and disperses its smoke over the domain.");

                Flag("ENABLE_SMOKE_OUTPUTS", ref n.ENABLE_SMOKE_OUTPUTS, "Write PM2.5 release per interval.");
                ImGui.BeginDisabled(!n.ENABLE_SMOKE_OUTPUTS);
                ImGui.Indent();
                Real("DT_SMOKE_OUTPUTS", ref n.DT_SMOKE_OUTPUTS,
                    "Seconds between outputs, and the window each one accounts for.");
                Real("PM_EMISSION_FACTOR_FLAMING", ref n.PM_EMISSION_FACTOR_FLAMING,
                    "Grams of PM2.5 per kg of fuel consumed while flaming.");
                Real("PM_EMISSION_FACTOR_SMOLDERING", ref n.PM_EMISSION_FACTOR_SMOLDERING,
                    "The same while smouldering, which is far dirtier per kilogram.");
                Real("DRY_WOOD_CALORIFIC_VALUE", ref n.DRY_WOOD_CALORIFIC_VALUE,
                    "MJ/kg for dry wood. Reduced by each cell's 10-hour moisture to give the wet value.");
                Real("FLAMING_TIME", ref n.FLAMING_TIME, "Seconds a cell flames for.");
                Real("SMOLDERING_TIME", ref n.SMOLDERING_TIME, "Seconds a cell smoulders for after flaming.");
                ImGui.Unindent();
                ImGui.EndDisabled();
                ImGui.Unindent();
            }

            //------------------------------------------------------------------ buildings
            if (ImGui.CollapsingHeader("Buildings (WUI spread)"))
            {
                ImGui.Indent();
                Flag("USE_BLDG_SPREAD_MODEL", ref n.USE_BLDG_SPREAD_MODEL,
                    "Building-to-building spread. Needs either the case's five building rasters or the "
                    + "constants below.");

                if (n.USE_BLDG_SPREAD_MODEL)
                {
                    ImGui.TextColored(NoteColor, "Switched off again at build time if the case has neither -");
                    ImGui.TextColored(NoteColor, "ELMFIRE would otherwise read a raster nobody wrote.");

                    Flag("USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS", ref n.USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS,
                        "Use one value everywhere instead of the case's building rasters.");
                    Whole("BLDG_SPREAD_MODEL_TYPE", ref n.BLDG_SPREAD_MODEL_TYPE, "1 = Hamada, 2 = UCB/UMD, 3 = fork.");
                    Whole("INTERFACE_MODEL_TYPE", ref n.INTERFACE_MODEL_TYPE, "1 = Ellipse, 2 = Threshold.");
                    Real("GLOBAL_HARDENING_FACTOR", ref n.GLOBAL_HARDENING_FACTOR,
                        "How much harder than baseline the building stock is to ignite.");
                    Whole("BANDTHICKNESS_WUI", ref n.BANDTHICKNESS_WUI, null);
                    Real("CRITICL_HF_WUI", ref n.CRITICL_HF_WUI, "Critical heat flux. ELMFIRE's spelling.");
                    Real("HRR_ELLIPSE_ADJ", ref n.HRR_ELLIPSE_ADJ, null);

                    if (n.USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS)
                    {
                        ImGui.Separator();
                        Real("BLDG_AREA_CONSTANT", ref n.BLDG_AREA_CONSTANT, "Average building area, m2.");
                        Real("BLDG_SEPARATION_DIST_CONSTANT", ref n.BLDG_SEPARATION_DIST_CONSTANT, "Metres.");
                        Real("BLDG_NONBURNABLE_FRAC_CONSTANT", ref n.BLDG_NONBURNABLE_FRAC_CONSTANT, "0 to 1.");
                        Real("BLDG_FOOTPRINT_FRAC_CONSTANT", ref n.BLDG_FOOTPRINT_FRAC_CONSTANT, "0 to 1.");
                        Whole("BLDG_FUEL_MODEL_CONSTANT", ref n.BLDG_FUEL_MODEL_CONSTANT,
                            "Row of building_fuel_models.csv to use everywhere.");
                    }
                }
                ImGui.Unindent();
            }

            //------------------------------------------------------------------ outputs
            if (ImGui.CollapsingHeader("Output rasters"))
            {
                ImGui.Indent();
                ImGui.TextColored(NoteColor, "Arrival time, spread rate, spread direction and SPREAD_RATE_IN_M are");
                ImGui.TextColored(NoteColor, "always written: the fire reader needs all four.");

                Real("DTDUMP", ref n.DTDUMP, "Seconds between dumps.");
                Flag("DUMP_FLIN", ref n.DUMP_FLIN, "Fireline intensity.");
                Flag("DUMP_CROWN_FIRE", ref n.DUMP_CROWN_FIRE, "Where the fire went to crown.");
                Flag("DUMP_FLAME_LENGTH", ref n.DUMP_FLAME_LENGTH, null);
                Flag("DUMP_HPUA", ref n.DUMP_HPUA, "Heat per unit area.");
                Flag("DUMP_PHI", ref n.DUMP_PHI, "The level set field itself.");
                Flag("DUMP_SURFACE_FIRE", ref n.DUMP_SURFACE_FIRE, null);
                Flag("DUMP_VELOCITY", ref n.DUMP_VELOCITY, null);
                Flag("DUMP_WS20", ref n.DUMP_WS20, "20 ft wind speed as ELMFIRE resolved it.");
                Flag("DUMP_WD20", ref n.DUMP_WD20, "20 ft wind direction as ELMFIRE resolved it.");
                Flag("DUMP_FIRE_SIZE_STATS", ref n.DUMP_FIRE_SIZE_STATS, "The per-timestep CSV of fire area.");
                Flag("DUMP_TRANSIENT_ACREAGE", ref n.DUMP_TRANSIENT_ACREAGE, null);
                Flag("DUMP_CRITICAL_FLIN", ref n.DUMP_CRITICAL_FLIN,
                    "The fireline intensity needed for crown fire at each cell.");
                Flag("DUMP_REACTION_INTENSITY", ref n.DUMP_REACTION_INTENSITY, null);
                Flag("DUMP_FUEL_CONSUMPTION", ref n.DUMP_FUEL_CONSUMPTION, null);
                Flag("DUMP_FIRE_VOLUME", ref n.DUMP_FIRE_VOLUME, null);
                Flag("DUMP_TAGGED", ref n.DUMP_TAGGED, "Which cells the level set is tracking.");
                Flag("DUMP_CROWN_FIRE_AREA", ref n.DUMP_CROWN_FIRE_AREA, "A scalar per dump, not a raster.");
                Flag("DUMP_SURFACE_FIRE_AREA", ref n.DUMP_SURFACE_FIRE_AREA, "A scalar per dump, not a raster.");
                Flag("DUMP_EMITIMES", ref n.DUMP_EMITIMES,
                    "A HYSPLIT EMITIMES file, for driving a smoke dispersion model from this fire.");
                Flag("DUMP_TIMINGS", ref n.DUMP_TIMINGS, "Where ELMFIRE spent its own run time.");
                Flag("USE_FOUR_DIGITS_IN_IWX_BAND", ref n.USE_FOUR_DIGITS_IN_IWX_BAND,
                    "Four digits rather than three for the band number in an output filename.");

                //Only the CFFDRS model fills what this dumps, so it is disabled under Rothermel rather than
                //silently writing nothing.
                bool cffdrs = n.SURFACE_SPREAD_MODEL == ElmfireNamelistInput.SurfaceSpreadModels.CFFDRS;
                ImGui.BeginDisabled(!cffdrs);
                Flag("DUMP_CFFDRS_DEBUG", ref n.DUMP_CFFDRS_DEBUG, "CFFDRS intermediate rasters.");
                ImGui.EndDisabled();
                if (!cffdrs)
                {
                    ImGui.TextColored(NoteColor, "DUMP_CFFDRS_DEBUG needs the CFFDRS surface spread model.");
                }

                ImGui.SeparatorText("How often, and every step");
                Flag("DUMP_HOURLY_RASTERS", ref n.DUMP_HOURLY_RASTERS, "Ignore DTDUMP and write hourly.");
                Flag("DUMP_EVERY_STEP", ref n.DUMP_EVERY_STEP,
                    "One dump per timestep. Enormous output - for debugging a single short case.");

                ImGui.SeparatorText("Exposure");
                ImGui.TextColored(NoteColor, "Each needs its counterpart under Exposure tracking, and the");
                ImGui.TextColored(NoteColor, "raster that counterpart reads. Without it they would dump zeros,");
                ImGui.TextColored(NoteColor, "which reads as 'nothing at risk' rather than 'not measured'.");

                ImGui.BeginDisabled(!n.USE_POPULATION_DENSITY);
                Flag("DUMP_AFFECTED_POPULATION", ref n.DUMP_AFFECTED_POPULATION, null);
                ImGui.EndDisabled();
                ImGui.BeginDisabled(!n.USE_LAND_VALUE);
                Flag("DUMP_AFFECTED_LAND_VALUE", ref n.DUMP_AFFECTED_LAND_VALUE, null);
                ImGui.EndDisabled();
                ImGui.BeginDisabled(!n.USE_REAL_ESTATE_VALUE);
                Flag("DUMP_AFFECTED_REAL_ESTATE_VALUE", ref n.DUMP_AFFECTED_REAL_ESTATE_VALUE, null);
                ImGui.EndDisabled();

                ImGui.SeparatorText("Binary outputs");
                Flag("DUMP_BINARY_OUTPUTS", ref n.DUMP_BINARY_OUTPUTS,
                    "The per-cell binary record an ensemble aggregates from.");
                ImGui.BeginDisabled(!n.DUMP_BINARY_OUTPUTS);
                ImGui.Indent();
                Real("BINARY_OUTPUTS_DUMP_FRACTION", ref n.BINARY_OUTPUTS_DUMP_FRACTION,
                    "Fraction of cells written. 1 is all of them.");
                Flag("FULL_BINARY_OUTPUTS", ref n.FULL_BINARY_OUTPUTS, "Every field rather than the reduced set.");
                Real("MINIMUM_AREA_FOR_BINARY_OUTPUTS", ref n.MINIMUM_AREA_FOR_BINARY_OUTPUTS,
                    "Skip a fire smaller than this, in acres. 0 writes them all.");
                ImGui.Unindent();
                ImGui.EndDisabled();

                ImGui.SeparatorText("Statistics and binning");
                Flag("CALCULATE_TIMES_BURNED", ref n.CALCULATE_TIMES_BURNED, null);
                Flag("CALCULATE_FLAME_LENGTH_STATS", ref n.CALCULATE_FLAME_LENGTH_STATS, null);

                ImGui.BeginDisabled(!n.CALCULATE_FLAME_LENGTH_STATS);
                ImGui.Indent();
                Flag("USE_FLAME_LENGTH_BINS", ref n.USE_FLAME_LENGTH_BINS, null);
                ImGui.BeginDisabled(!n.USE_FLAME_LENGTH_BINS);
                Whole("NUM_FLAME_LENGTH_BINS", ref n.NUM_FLAME_LENGTH_BINS, null);
                ImGui.TextColored(NoteColor,
                    $"Bin edges: {n.FLAME_LENGTH_BIN_LO.Length} low, {n.FLAME_LENGTH_BIN_HI.Length} high.");
                ImGui.TextColored(NoteColor, "Edited in the namelist - a 100-element edge list is not a form.");
                ImGui.EndDisabled();
                ImGui.Unindent();
                ImGui.EndDisabled();

                ImGui.SeparatorText("Virtual stations");
                Whole("NUM_VIRTUAL_STATIONS", ref n.NUM_VIRTUAL_STATIONS,
                    "Point time series written out of the running fire.");
                ImGui.TextColored(NoteColor,
                    $"Coordinates: {n.VIRTUAL_STATION_X.Length} x, {n.VIRTUAL_STATION_Y.Length} y - in the namelist.");
                ImGui.TextColored(NoteColor,
                    $"TIME_AT_BURNED_ACRES: {n.TIME_AT_BURNED_ACRES.Length} value(s) - in the namelist.");

                ImGui.SeparatorText("Embers and radiation");
                ImGui.BeginDisabled(!n.ENABLE_SPOTTING);
                Flag("DUMP_SPOTTING_OUTPUTS", ref n.DUMP_SPOTTING_OUTPUTS, null);
                Flag("ACCUMULATE_EMBER_FLUX", ref n.ACCUMULATE_EMBER_FLUX, null);
                Flag("DUMP_EMBER_FLUX", ref n.DUMP_EMBER_FLUX, null);
                Flag("DUMP_EMBER_IGNITION", ref n.DUMP_EMBER_IGNITION, "Where embers started new fires.");
                Flag("DUMP_EMBER_FLUX_TRANSIENT", ref n.DUMP_EMBER_FLUX_TRANSIENT, null);
                Flag("DUMP_TOTAL_DFC_RECEIVED", ref n.DUMP_TOTAL_DFC_RECEIVED, "Total direct flame contact.");
                Flag("DUMP_TRANSIENT_DFC", ref n.DUMP_TRANSIENT_DFC, null);
                Flag("DUMP_TOTAL_RAD_RECEIVED", ref n.DUMP_TOTAL_RAD_RECEIVED, "Total radiation received.");
                Flag("DUMP_TRANSIENT_RAD", ref n.DUMP_TRANSIENT_RAD, null);
                Flag("DUMP_HRR_TRANSIENT", ref n.DUMP_HRR_TRANSIENT, "Heat release rate over time.");

                //The ember count bins need the flux they count, which is a second condition on top of spotting.
                ImGui.BeginDisabled(!n.ACCUMULATE_EMBER_FLUX);
                ImGui.Indent();
                Flag("USE_EMBER_COUNT_BINS", ref n.USE_EMBER_COUNT_BINS, null);
                ImGui.BeginDisabled(!n.USE_EMBER_COUNT_BINS);
                Whole("NUM_EMBER_COUNT_BINS", ref n.NUM_EMBER_COUNT_BINS, null);
                ImGui.TextColored(NoteColor,
                    $"Bin edges: {n.EMBER_COUNT_BIN_LO.Length} low, {n.EMBER_COUNT_BIN_HI.Length} high - in the namelist.");
                ImGui.EndDisabled();
                ImGui.Unindent();
                ImGui.EndDisabled();

                ImGui.EndDisabled();
                if (!n.ENABLE_SPOTTING)
                {
                    //Greyed rather than hidden, and with the reason: these are written as off regardless
                    //when spotting is, and the alternative is a run that dies inside MPI.
                    ImGui.TextColored(NoteColor, "Needs spotting. Without it these arrays are never allocated");
                    ImGui.TextColored(NoteColor, "and ELMFIRE aborts inside MPI_Reduce instead of saying so.");
                }
                ImGui.Unindent();
            }

            //------------------------------------------------------------------ ensemble
            if (ImGui.CollapsingHeader("Ensemble and randomness"))
            {
                ImGui.Indent();
                Real("EDGEBUFFER", ref n.EDGEBUFFER,
                    "Metres kept between the fire and the domain edge. The case is padded for this reason.");
                Whole("NUM_ENSEMBLE_MEMBERS", ref n.NUM_ENSEMBLE_MEMBERS,
                    "Leave at 1. A probabilistic campaign runs realizations as separate cases, because "
                    + "k-PERIL needs one arrival-time raster each.");
                Whole("SEED", ref n.SEED, "Random seed, so a run with random ignition can be repeated.");

                ImGui.SeparatorText("Wind");
                Flag("POINT_WIND_TO_CENTER", ref n.POINT_WIND_TO_CENTER,
                    "Aim the wind at the domain centre so every ignition threatens the same place.");
                if (n.POINT_WIND_TO_CENTER)
                {
                    ImGui.TextColored(WarnColor, "This replaces the wind direction in the weather rasters, so");
                    ImGui.TextColored(WarnColor, "whatever WindNinja produced is not what the fire sees.");
                }

                ImGui.TextColored(NoteColor, "Fluctuation intensities sampled per case. ELMFIRE has no switch");
                ImGui.TextColored(NoteColor, "for these - both ends above zero is the switch. Leave at -1 to");
                ImGui.TextColored(NoteColor, "use the fixed values under Simulator instead.");
                Real("WIND_SPEED_FLUCTUATION_INTENSITY_MIN",
                    ref n.WIND_SPEED_FLUCTUATION_INTENSITY_MIN, "Fraction of wind speed.");
                Real("WIND_SPEED_FLUCTUATION_INTENSITY_MAX",
                    ref n.WIND_SPEED_FLUCTUATION_INTENSITY_MAX, "Fraction of wind speed.");
                Real("WIND_DIRECTION_FLUCTUATION_INTENSITY_MIN",
                    ref n.WIND_DIRECTION_FLUCTUATION_INTENSITY_MIN, "Degrees.");
                Real("WIND_DIRECTION_FLUCTUATION_INTENSITY_MAX",
                    ref n.WIND_DIRECTION_FLUCTUATION_INTENSITY_MAX, "Degrees.");
                WarnHalfSet("WIND_SPEED_FLUCTUATION_INTENSITY",
                    n.WIND_SPEED_FLUCTUATION_INTENSITY_MIN, n.WIND_SPEED_FLUCTUATION_INTENSITY_MAX);
                WarnHalfSet("WIND_DIRECTION_FLUCTUATION_INTENSITY",
                    n.WIND_DIRECTION_FLUCTUATION_INTENSITY_MIN, n.WIND_DIRECTION_FLUCTUATION_INTENSITY_MAX);

                DrawPerturbations(n);
                ImGui.Unindent();
            }

            //------------------------------------------------------------------ calibration
            if (ImGui.CollapsingHeader("Calibration (per pyrome)"))
            {
                ImGui.Indent();
                ImGui.TextWrapped("Tables for calibrating a landscape-scale ensemble against recorded fire "
                    + "history: one row per pyrome, 128 of them. Each CSV must already be in the case's "
                    + "inputs folder - nothing here derives or copies them - and is named, not pathed.");

                Flag("ADJUSTMENT_FACTORS_BY_PYROME", ref n.ADJUSTMENT_FACTORS_BY_PYROME,
                    "Per-pyrome, per-fuel-model spread rate adjustment factors.");
                if (n.ADJUSTMENT_FACTORS_BY_PYROME)
                {
                    ImGui.Indent();
                    Text("ADJUSTMENT_FACTORS_FILENAME", ref n.ADJUSTMENT_FACTORS_FILENAME,
                        "CSV name inside the case's inputs folder.");
                    if (!n.USE_PYROMES)
                    {
                        ImGui.TextColored(WarnColor, "Also needs USE_PYROMES and a pyromes raster, without");
                        ImGui.TextColored(WarnColor, "which there is nothing to index the table by.");
                    }
                    ImGui.Unindent();
                }

                Flag("CALIBRATION_CONSTANTS_BY_PYROME", ref n.CALIBRATION_CONSTANTS_BY_PYROME,
                    "Per-pyrome suppression constants: attack time, containment rate, SDI weight.");
                if (n.CALIBRATION_CONSTANTS_BY_PYROME)
                {
                    ImGui.Indent();
                    Text("CALIBRATION_CONSTANTS_FILENAME", ref n.CALIBRATION_CONSTANTS_FILENAME,
                        "CSV name inside the case's inputs folder.");
                    ImGui.Unindent();
                }

                Flag("DURATION_PDF_BY_PYROME", ref n.DURATION_PDF_BY_PYROME,
                    "Per-pyrome fire duration probabilities, one column per day.");
                if (n.DURATION_PDF_BY_PYROME)
                {
                    ImGui.Indent();
                    Text("DURATION_PDF_FILENAME", ref n.DURATION_PDF_FILENAME,
                        "CSV name inside the case's inputs folder.");
                    Whole("DURATION_MAX_DAYS", ref n.DURATION_MAX_DAYS,
                        "How many days the PDF covers. Columns beyond it are zero.");
                    ImGui.Unindent();
                }
                ImGui.Unindent();
            }
        }

        private static void Text(string key, ref string value, string tooltip)
        {
            Fields.Text(key, ref value, tooltip);
        }

        private static void WarnHalfSet(string key, double min, double max)
        {
            if ((min > 0.0) == (max > 0.0)) return;

            ImGui.TextColored(WarnColor, key + ": only one end is set, so nothing is");
            ImGui.TextColored(WarnColor, "sampled. Set both above zero.");
        }

        //Valid values ELMFIRE checks each variation against, and stops the run over.
        private static readonly string[] PerturbableRasters =
        {
            "ADJ", "CBD", "CBH", "CC", "CH", "FBFM", "FMC",
            "M1", "M10", "M100", "MLH", "MLW", "WAF", "WD", "WS",
        };
        private static readonly string[] SpatialModes = { "GLOBAL", "PIXEL" };
        private static readonly string[] TemporalModes = { "STATIC", "DYNAMIC" };
        private static readonly string[] PdfTypes = { "UNIFORM", "GAUSSIAN", "LOGNORMAL" };

        /// <summary>
        /// The raster perturbation variations, as a list that can be added to and removed from.
        /// </summary>
        /// <remarks>
        /// ELMFIRE holds these as eight parallel arrays and a count, so a variation is a row across them rather
        /// than an object. The rows are kept in step here — adding or removing one resizes all eight — because
        /// arrays of different lengths would give ELMFIRE a variation whose distribution is <c>'null'</c>, which
        /// it stops the run over.
        /// </remarks>
        private static void DrawPerturbations(ElmfireNamelistInput n)
        {
            ImGui.SeparatorText("Raster perturbations");
            ImGui.TextWrapped("Each variation perturbs one raster per case, which is how an ensemble varies "
                + "fuel and weather rather than only the ignition point.");

            int count = n.RASTER_TO_PERTURB == null ? 0 : n.RASTER_TO_PERTURB.Length;
            Resize(n, count);

            for (int i = 0; i < count; ++i)
            {
                ImGui.PushID(i);
                ImGui.Separator();

                Choice($"RASTER_TO_PERTURB({i + 1})", PerturbableRasters, ref n.RASTER_TO_PERTURB[i],
                    "Which raster this variation perturbs.");
                Choice($"SPATIAL_PERTURBATION({i + 1})", SpatialModes, ref n.SPATIAL_PERTURBATION[i],
                    "GLOBAL shifts the whole raster by one draw; PIXEL draws per cell.");
                Choice($"TEMPORAL_PERTURBATION({i + 1})", TemporalModes, ref n.TEMPORAL_PERTURBATION[i],
                    "STATIC draws once; DYNAMIC redraws every weather band.");
                Choice($"PDF_TYPE({i + 1})", PdfTypes, ref n.PDF_TYPE[i], "Which distribution it is drawn from.");

                if (n.PDF_TYPE[i] == "UNIFORM")
                {
                    Real($"PDF_LOWER_LIMIT({i + 1})", ref n.PDF_LOWER_LIMIT[i], null);
                    Real($"PDF_UPPER_LIMIT({i + 1})", ref n.PDF_UPPER_LIMIT[i], null);
                }
                else
                {
                    Real($"PDF_MEAN({i + 1})", ref n.PDF_MEAN[i], null);
                    Real($"PDF_SIGMA({i + 1})", ref n.PDF_SIGMA[i], null);
                }

                if (ImGui.Button("Remove this variation"))
                {
                    RemoveAt(n, i);
                    ImGui.PopID();
                    break;
                }
                ImGui.PopID();
            }

            ImGui.Separator();
            if (ImGui.Button("Add a variation"))
            {
                Resize(n, count + 1);
                n.RASTER_TO_PERTURB[count] = PerturbableRasters[0];
                n.SPATIAL_PERTURBATION[count] = SpatialModes[0];
                n.TEMPORAL_PERTURBATION[count] = TemporalModes[0];
                n.PDF_TYPE[count] = PdfTypes[0];
            }
        }

        private static void Resize(ElmfireNamelistInput n, int count)
        {
            n.RASTER_TO_PERTURB = Resize(n.RASTER_TO_PERTURB, count, PerturbableRasters[0]);
            n.SPATIAL_PERTURBATION = Resize(n.SPATIAL_PERTURBATION, count, SpatialModes[0]);
            n.TEMPORAL_PERTURBATION = Resize(n.TEMPORAL_PERTURBATION, count, TemporalModes[0]);
            n.PDF_TYPE = Resize(n.PDF_TYPE, count, PdfTypes[0]);
            n.PDF_LOWER_LIMIT = Resize(n.PDF_LOWER_LIMIT, count);
            n.PDF_UPPER_LIMIT = Resize(n.PDF_UPPER_LIMIT, count);
            n.PDF_MEAN = Resize(n.PDF_MEAN, count);
            n.PDF_SIGMA = Resize(n.PDF_SIGMA, count);
        }

        private static string[] Resize(string[] a, int count, string fill)
        {
            var next = new string[count];
            for (int i = 0; i < count; ++i)
            {
                next[i] = a != null && i < a.Length && !string.IsNullOrEmpty(a[i]) ? a[i] : fill;
            }
            return next;
        }

        private static double[] Resize(double[] a, int count)
        {
            var next = new double[count];
            for (int i = 0; i < count; ++i)
            {
                next[i] = a != null && i < a.Length ? a[i] : 0.0;
            }
            return next;
        }

        private static void RemoveAt(ElmfireNamelistInput n, int index)
        {
            n.RASTER_TO_PERTURB = RemoveAt(n.RASTER_TO_PERTURB, index);
            n.SPATIAL_PERTURBATION = RemoveAt(n.SPATIAL_PERTURBATION, index);
            n.TEMPORAL_PERTURBATION = RemoveAt(n.TEMPORAL_PERTURBATION, index);
            n.PDF_TYPE = RemoveAt(n.PDF_TYPE, index);
            n.PDF_LOWER_LIMIT = RemoveAt(n.PDF_LOWER_LIMIT, index);
            n.PDF_UPPER_LIMIT = RemoveAt(n.PDF_UPPER_LIMIT, index);
            n.PDF_MEAN = RemoveAt(n.PDF_MEAN, index);
            n.PDF_SIGMA = RemoveAt(n.PDF_SIGMA, index);
        }

        private static T[] RemoveAt<T>(T[] a, int index)
        {
            if (a == null || index < 0 || index >= a.Length) return a;

            var next = new T[a.Length - 1];
            for (int i = 0, j = 0; i < a.Length; ++i)
            {
                if (i != index) next[j++] = a[i];
            }
            return next;
        }

        //Thin forwarders to Fields, kept only because there are around a hundred call sites below written
        //against these names and this argument order. The implementations that used to live here were the
        //better ones - InputDouble rather than InputFloat, enums matched by name rather than by index - so
        //Fields adopted them rather than the reverse, and every other panel got the improvement.

        private static void Flag(string key, ref bool value, string tooltip)
        {
            Fields.Check(key, ref value, tooltip);
        }

        private static void Real(string key, ref double value, string tooltip)
        {
            Fields.Real(key, ref value, tooltip);
        }

        private static void Whole(string key, ref int value, string tooltip)
        {
            Fields.Whole(key, ref value, tooltip);
        }

        private static void Choice<T>(string key, string[] names, ref T value, string tooltip) where T : struct, Enum
        {
            Fields.Choice(key, ref value, names, tooltip);
        }

        private static void Choice(string key, string[] names, ref string value, string tooltip)
        {
            Fields.Choice(key, ref value, names, tooltip);
        }
    }
}
