//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace PREACT.Input
{
    /// <summary>
    /// Everything in an ELMFIRE namelist that is a modelling choice, so a complete <c>elmfire.data</c> can
    /// be written from the scenario alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Field names are the namelist keys, verbatim.</b> That is why they are not in this codebase's usual
    /// PascalCase: a setting called <c>NEMBERS_MIN</c> is worth nothing unless it is obvious which ELMFIRE
    /// key it becomes, and a rename between the two is a defect nobody would spot. It also makes the
    /// <c>[ElmfireNamelist]</c> section of a <c>.wui</c> read like the namelist it produces, so a value can
    /// be checked against ELMFIRE's own documentation without a translation table.
    /// </para>
    /// <para>
    /// Defaults are ELMFIRE's own except where WUInity's pipeline makes a different value the only correct
    /// one - the canopy scaling flags and <c>WS_AT_10M</c> above all, which describe the rasters the case
    /// builder writes rather than a preference. Each of those is marked below.
    /// </para>
    /// <para>
    /// What is <b>not</b> here: filenames, the grid, the time base, the ignition points, and the four dump
    /// keys the fire reader needs. Those are facts about the case rather than choices, and
    /// <c>ElmfireNamelistBuilder</c> fills them in from the case itself - see the notes there.
    /// </para>
    /// </remarks>
    [System.Serializable]
    public class ElmfireNamelistInput
    {
        /// <summary>How embers are generated. Hyphenated in the namelist; the underscores here are C#'s.</summary>
        public enum EmberGenerationModels { RANDOM, PER_AREA, PER_MW }

        public enum SpottingDistanceModels { UNIFORM, LOGNORMAL, EMPIRICAL }

        public enum EmberAccumulationModels { LAGRANGIAN, EULERIAN }

        public enum SpotIgnitionModels { DIRECT, SIMPLE, PHYSICAL }

        // ------------------------------------------------------------------ &INPUTS

        /// <summary>Seconds between weather bands. One hour, which is what the weather pipeline writes.</summary>
        public double DT_METEOROLOGY = 3600.0;

        //These four describe the canopy rasters rather than express a preference, and WUInity's are in real
        //units (cc %, ch m, cbh m, cbd kg/m3) rather than LANDFIRE's scaled integers. ELMFIRE defaults the
        //three scaling flags to .TRUE., which would divide every value and model a fire in a forest a tenth
        //of its real height.
        /// <summary>
        /// Which surface spread model ELMFIRE uses. <c>ROTHERMEL</c> or <c>CFFDRS</c> — ELMFIRE validates this
        /// against its own <c>VALID_SURFACE_MODELS</c> and refuses to start on anything else.
        /// </summary>
        public SurfaceSpreadModels SURFACE_SPREAD_MODEL = SurfaceSpreadModels.ROTHERMEL;

        /// <summary>The two models ELMFIRE accepts, from <c>elmfire_vars.f90</c>'s VALID_SURFACE_MODELS.</summary>
        public enum SurfaceSpreadModels { ROTHERMEL, CFFDRS }

        /// <summary>
        /// Starting drought code for the Canadian system. <b>Only read when
        /// <see cref="SURFACE_SPREAD_MODEL"/> is CFFDRS</b> (elmfire.f90 seeds DC_prev/DMC_prev and the initial
        /// BUI from these two inside its <c>if (SURFACE_SPREAD_MODEL .eq. "CFFDRS")</c> branch).
        /// </summary>
        public double START_DC = 400.0;

        /// <summary>Starting duff moisture code. Only read under CFFDRS, as <see cref="START_DC"/>.</summary>
        public double START_DMC = 80.0;

        /// <summary>
        /// Let a barrier raster stop spread. Needs <c>barriers.tif</c> in the case, which the builder ingests
        /// from <c>[ELMFIRE] BarriersFile</c>; on without it, ELMFIRE has nothing to read.
        /// </summary>
        /// <remarks>
        /// Defaults to <c>true</c> where ELMFIRE's own default is <c>.FALSE.</c>, deliberately: it is only
        /// written when the case actually carries a barrier raster, and ingesting one is itself the instruction
        /// to apply it. This preserves what the builder did before the flag was exposed.
        /// </remarks>
        public bool USE_BARRIERS = true;

        /// <summary>
        /// Track land value burned. Needs a land-value raster the case builder does not produce, so this is for
        /// a case whose inputs were prepared elsewhere.
        /// </summary>
        public bool USE_LAND_VALUE = false;

        /// <summary>Track population affected. Needs a population-density raster, as <see cref="USE_LAND_VALUE"/>.</summary>
        public bool USE_POPULATION_DENSITY = false;

        /// <summary>Track real-estate value burned. Needs its own raster, as <see cref="USE_LAND_VALUE"/>.</summary>
        public bool USE_REAL_ESTATE_VALUE = false;

        /// <summary>Read and write rasters in tiles rather than whole. An I/O strategy, not a physics choice.</summary>
        public bool USE_TILED_IO = false;

        /// <summary>Expect an XML sidecar on BSQ inputs. Only relevant to a case built around BSQ rather than GeoTIFF.</summary>
        public bool USE_BSQ_XML_HEADER = false;

        /// <summary>Reuse BSQ intermediates already on disk instead of regenerating them from the GeoTIFFs.</summary>
        public bool USE_EXISTING_BSQS = false;

        /// <summary>Write a GDAL .vrt rather than a .tif for outputs.</summary>
        public bool VRT_INSTEAD_OF_TIF = false;

        /// <summary>
        /// Read only the weather bands the ignition actually needs.
        /// </summary>
        /// <remarks>
        /// <b>Only takes effect together with <c>CSV_FIXED_IGNITION_LOCATIONS</c></b> — every use of it in the
        /// Fortran is guarded by that flag, so on its own it does nothing. Worth knowing that it is therefore
        /// <i>not</i> an alternative to raising <c>WX_BANDS_KEPT_IN_MEM</c>: the band-slice crash is reached
        /// through the other branch of the same decision in <c>elmfire_init.f90</c>.
        /// </remarks>
        public bool ONLY_READ_NEEDED_WX_BANDS = false;

        public bool CC_IN_PERCENT = true;
        public bool CBD_TIMES_100 = false;
        public bool CBH_TIMES_10 = false;
        public bool CH_TIMES_10 = false;

        public bool DEAD_MC_IN_PERCENT = true;
        public bool LIVE_MC_IN_PERCENT = true;

        public bool USE_CONSTANT_LH = true;
        public double LH_MOISTURE_CONTENT = 60.0;
        public bool USE_CONSTANT_LW = true;
        public double LW_MOISTURE_CONTENT = 90.0;

        public bool USE_CONSTANT_FMC = true;
        public double FOLIAR_MOISTURE_CONTENT = 90.0;

        /// <summary>
        /// The wind rasters hold 10 m wind, not ELMFIRE's 20 ft default. Not a preference: the weather
        /// pipeline converts Open-Meteo's 10 m wind, and getting this wrong is silent - ELMFIRE reads the
        /// same numbers at the wrong height and the fire simply spreads at the wrong rate.
        /// </summary>
        public bool WS_AT_10M = true;

        /// <summary>Wind speed unit. ELMFIRE reads mph unless this says km/h; the pipeline writes mph.</summary>
        public bool WS_IN_KPH = false;

        public double GRID_DECLINATION = 0.0;
        public bool ROTATE_ASP = false;
        public bool ROTATE_WD = false;

        // ------------------------------------------------------------------ &OUTPUTS

        public double DTDUMP = 3600.0;

        public bool DUMP_FLIN = true;
        public bool DUMP_CROWN_FIRE = true;
        public bool DUMP_FLAME_LENGTH = false;
        public bool DUMP_HPUA = false;
        public bool DUMP_PHI = false;
        public bool DUMP_SURFACE_FIRE = false;
        public bool DUMP_VELOCITY = false;
        public bool DUMP_WS20 = false;
        public bool DUMP_WD20 = false;
        public bool DUMP_FIRE_SIZE_STATS = true;
        public bool DUMP_TRANSIENT_ACREAGE = false;

        public bool DUMP_SPOTTING_OUTPUTS = true;
        public bool ACCUMULATE_EMBER_FLUX = true;

        // ------------------------------------------------------------------ &OUTPUTS, the rest
        //
        // Raster dumps. Each costs a file per dump interval, so they are off by default and turned on for what
        // is actually going to be looked at. None of them changes the fire.

        public bool DUMP_CRITICAL_FLIN = false;
        public bool DUMP_REACTION_INTENSITY = false;
        public bool DUMP_FUEL_CONSUMPTION = false;
        public bool DUMP_FIRE_VOLUME = false;
        public bool DUMP_TAGGED = false;
        public bool DUMP_HOURLY_RASTERS = false;
        public bool DUMP_EVERY_STEP = false;

        /// <summary>Areas rather than rasters: a scalar per dump, into the fire size statistics.</summary>
        public bool DUMP_CROWN_FIRE_AREA = false;

        public bool DUMP_SURFACE_FIRE_AREA = false;

        /// <summary>Timing breakdown of the run itself, for diagnosing where ELMFIRE spends its time.</summary>
        public bool DUMP_TIMINGS = false;

        /// <summary>
        /// CFFDRS intermediate rasters. <b>Only meaningful when <see cref="SURFACE_SPREAD_MODEL"/> is CFFDRS</b>
        /// — under Rothermel the arrays it dumps are never filled.
        /// </summary>
        public bool DUMP_CFFDRS_DEBUG = false;

        /// <summary>HYSPLIT EMITIMES file, for driving a smoke dispersion model from this fire.</summary>
        public bool DUMP_EMITIMES = false;

        /// <summary>Four digits rather than three in the band number of an output filename.</summary>
        public bool USE_FOUR_DIGITS_IN_IWX_BAND = false;

        // ---- ember and radiation dumps. All require ENABLE_SPOTTING: the arrays they write are only
        // allocated when spotting is on, and dumping an unallocated array is what produced the
        // "Fatal error in internal_Reduce: Invalid buffer pointer" that named MPI and meant a raster flag.

        public bool DUMP_EMBER_FLUX_TRANSIENT = false;
        public bool DUMP_TOTAL_DFC_RECEIVED = false;
        public bool DUMP_TOTAL_RAD_RECEIVED = false;
        public bool DUMP_TRANSIENT_DFC = false;
        public bool DUMP_TRANSIENT_RAD = false;
        public bool DUMP_HRR_TRANSIENT = false;

        // ---- exposure dumps. Each needs its INPUTS counterpart on, and the raster that counterpart reads.

        /// <summary>Requires <see cref="USE_POPULATION_DENSITY"/>.</summary>
        public bool DUMP_AFFECTED_POPULATION = false;

        /// <summary>Requires <see cref="USE_LAND_VALUE"/>.</summary>
        public bool DUMP_AFFECTED_LAND_VALUE = false;

        /// <summary>Requires <see cref="USE_REAL_ESTATE_VALUE"/>.</summary>
        public bool DUMP_AFFECTED_REAL_ESTATE_VALUE = false;

        // ---- binary outputs, the per-cell record an ensemble aggregates from.

        public bool DUMP_BINARY_OUTPUTS = false;

        /// <summary>Fraction of cells written. 1 is all of them; lower samples.</summary>
        public double BINARY_OUTPUTS_DUMP_FRACTION = 1.0;

        /// <summary>Write every field rather than the reduced set.</summary>
        public bool FULL_BINARY_OUTPUTS = true;

        /// <summary>Skip binary output for a fire smaller than this, in acres. 0 writes them all.</summary>
        public double MINIMUM_AREA_FOR_BINARY_OUTPUTS = 0.0;

        // ---- flame length statistics and their optional binning.

        public bool CALCULATE_FLAME_LENGTH_STATS = false;

        /// <summary>Requires <see cref="CALCULATE_FLAME_LENGTH_STATS"/>.</summary>
        public bool USE_FLAME_LENGTH_BINS = false;

        public int NUM_FLAME_LENGTH_BINS = 0;

        /// <summary>
        /// Bin edges, ELMFIRE's <c>REAL(100)</c>. Comma-separated, and edited in the namelist rather than the
        /// editor — a hundred-element edge list is not a form.
        /// </summary>
        public double[] FLAME_LENGTH_BIN_LO = new double[0];

        public double[] FLAME_LENGTH_BIN_HI = new double[0];

        // ---- times burned, and ember count binning.

        public bool CALCULATE_TIMES_BURNED = false;

        /// <summary>Requires <see cref="ACCUMULATE_EMBER_FLUX"/>, and so spotting.</summary>
        public bool USE_EMBER_COUNT_BINS = false;

        public int NUM_EMBER_COUNT_BINS = 0;

        /// <summary>Bin edges, ELMFIRE's <c>INTEGER*2(100)</c>. As the flame length edges.</summary>
        public int[] EMBER_COUNT_BIN_LO = new int[0];

        public int[] EMBER_COUNT_BIN_HI = new int[0];

        // ---- virtual weather stations: point time series written out of the running fire.

        public int NUM_VIRTUAL_STATIONS = 0;

        /// <summary>Station coordinates in the case's CRS, ELMFIRE's <c>REAL(100)</c> pair.</summary>
        public double[] VIRTUAL_STATION_X = new double[0];

        public double[] VIRTUAL_STATION_Y = new double[0];

        /// <summary>
        /// Acreages at which to record the time reached. ELMFIRE's is allocatable, so any length.
        /// </summary>
        public double[] TIME_AT_BURNED_ACRES = new double[0];

        // ------------------------------------------------------------------ &SIMULATOR, the rest

        /// <summary>
        /// Vary wind through the run rather than holding each band steady.
        /// </summary>
        /// <remarks>
        /// Gates the three settings below — <c>elmfire_level_set.f90</c> only consults them inside
        /// <c>IF (WIND_FLUCTUATIONS .AND. T - T_LAST_WIND_FLUCTUATIONS .GE. DT_WIND_FLUCTUATIONS)</c>.
        /// </remarks>
        public bool WIND_FLUCTUATIONS = false;

        /// <summary>How often the fluctuation is redrawn, seconds. Requires <see cref="WIND_FLUCTUATIONS"/>.</summary>
        public double DT_WIND_FLUCTUATIONS = 15.0;

        /// <summary>Fluctuation magnitude as a fraction of wind speed. Requires <see cref="WIND_FLUCTUATIONS"/>.</summary>
        public double WIND_SPEED_FLUCTUATION_INTENSITY = 0.0;

        /// <summary>Fluctuation magnitude in degrees. Requires <see cref="WIND_FLUCTUATIONS"/>.</summary>
        public double WIND_DIRECTION_FLUCTUATION_INTENSITY = 0.0;

        /// <summary>
        /// Seed the generator from the system clock, <b>ignoring <see cref="SEED"/></b>.
        /// </summary>
        /// <remarks>
        /// Mutually exclusive with a reproducible run, and worth stating because it silently defeats one: a
        /// probabilistic campaign varies <c>SEED</c> per realization to make the ensemble, and with this on
        /// ELMFIRE takes its seed from <c>SYSTEM_CLOCK</c> instead — so <c>--seed</c> does nothing, and no
        /// realization can be reproduced from the record of what produced it.
        /// </remarks>
        public bool RANDOMIZE_RANDOM_SEED = false;

        /// <summary>
        /// Estimate structure losses over the cells that burned. Needs the building and exposure inputs to
        /// produce anything; on a case without them it iterates burned cells and finds nothing to value.
        /// </summary>
        public bool ESTIMATE_URBAN_LOSSES = false;

        /// <summary>Use a pyrome map for regionalised parameters. Needs a pyromes raster in the case.</summary>
        public bool USE_PYROMES = false;

        /// <summary>Run across more than one host, which changes how weather is broadcast between ranks.</summary>
        public bool MULTIPLE_HOSTS = false;

        /// <summary>
        /// How often burned cells are dropped from the tracked set, in timesteps. Larger keeps more cells live
        /// and costs time; smaller risks dropping a cell still contributing.
        /// </summary>
        public int UNTAG_CELLS_TIMESTEP_INTERVAL = 10;

        /// <summary>Upper bound on the low-wind branch of the spread solution, mi/h.</summary>
        public double MAX_LOW = 8.0;

        /// <summary>
        /// Multiplier turning mid-flame wind into the low-wind branch's effective wind. ELMFIRE's default is
        /// written <c>60.0/5280.0</c> — feet per mile — so it is that ratio, not a round number.
        /// </summary>
        public double WSMFEFF_LOW_MULT = 60.0 / 5280.0;

        /// <summary>Floor on the lightning ignition rate, for a pyrome-driven ignition model.</summary>
        public double PLIGNRATE_MIN = 0.0;
        public bool DUMP_EMBER_FLUX = true;
        public bool DUMP_EMBER_IGNITION = true;

        // ------------------------------------------------------------------ &TIME_CONTROL

        public double SIMULATION_DT = 5.0;
        public double SIMULATION_DTMAX = 300.0;
        public double SIMULATION_TSTART = 0.0;
        public double TARGET_CFL = 0.4;

        public bool USE_DIURNAL_ADJUSTMENT_FACTOR = false;
        public double OVERNIGHT_ADJUSTMENT_FACTOR = 0.1;
        public double BURN_PERIOD_LENGTH = 10.0;
        public double BURN_PERIOD_CENTER_FRAC = 0.667;

        /// <summary>
        /// Draw each case's stop time uniformly between <c>SIMULATION_TSTART</c> and the stop time, instead of
        /// running every case to the stop time.
        /// </summary>
        /// <remarks>
        /// Ensemble variability, not a shortcut: the cases that stop early are the ones the boundary should not
        /// be allowed to rely on. It does mean the run length is no longer the number the scenario states.
        /// </remarks>
        public bool RANDOMIZE_SIMULATION_TSTOP = false;

        // How often each transient weather field is re-read from the rasters, in seconds of simulated time.
        // ELMFIRE's own defaults leave the live and foliar moistures effectively frozen (9E8 s is far longer
        // than any run), which is what the fuel models expect unless a case supplies them per band.

        /// <summary>Re-read interval for 1-hour dead fuel moisture, seconds.</summary>
        public double DT_INTERPOLATE_M1 = 300.0;

        /// <summary>Re-read interval for 10-hour dead fuel moisture, seconds.</summary>
        public double DT_INTERPOLATE_M10 = 3000.0;

        /// <summary>Re-read interval for 100-hour dead fuel moisture, seconds.</summary>
        public double DT_INTERPOLATE_M100 = 30000.0;

        /// <summary>Re-read interval for live herbaceous moisture, seconds. Effectively never by default.</summary>
        public double DT_INTERPOLATE_MLH = 9e8;

        /// <summary>Re-read interval for live woody moisture, seconds. Effectively never by default.</summary>
        public double DT_INTERPOLATE_MLW = 9e8;

        /// <summary>Re-read interval for foliar moisture content, seconds. Effectively never by default.</summary>
        public double DT_INTERPOLATE_FMC = 9e8;

        /// <summary>Re-read interval for wind speed and direction, seconds.</summary>
        public double DT_INTERPOLATE_WIND = 300.0;

        // ------------------------------------------------------------------ &MONTE_CARLO

        /// <summary>Metres of margin ELMFIRE keeps between the fire and the domain edge.</summary>
        public double EDGEBUFFER = 60.0;

        public int SEED = 2024;
        public int NUM_ENSEMBLE_MEMBERS = 1;

        /// <summary>
        /// How many hourly weather bands to read, or 0 to take it from the rasters the case holds.
        /// </summary>
        /// <remarks>
        /// Auto is the useful default and the reason this is not simply <c>NUM_METEOROLOGY_TIMES</c>: asking
        /// for more bands than the rasters have fails the run, and asking for fewer silently simulates the
        /// wrong weather - ELMFIRE reuses band one for the whole fire, which looks like a successful run of
        /// a constant-wind day. Neither is something the user should have to keep in step by hand.
        /// </remarks>
        public int MeteorologyBands = 0;

        /// <summary>
        /// How many weather bands ELMFIRE holds in memory at once, rolling the window forward as the fire
        /// burns past it. ELMFIRE's own default, 30.
        /// </summary>
        /// <remarks>
        /// This is what keeps a long run's memory flat: the five weather rasters are held at
        /// <c>ncols x nrows x this</c>, so 72 bands of a 566x541 domain measured 814 MB peak when the whole
        /// series was held against 218 MB at 8 bands - and the campaign's whole point is runs of hundreds of
        /// hours. Two is the floor, since a band is interpolated against the next one.
        ///
        /// Until the ELMFIRE fix of 2026-08-05 every case here was written with this <i>above</i> the band
        /// count to disable the window, because swapping slices crashed. It no longer does, and the Mati
        /// 72-band case burns the same 14205.4 ac at 80, 30, 8 and 4 bands kept, at the same wall clock.
        /// A value at or above the case's band count still means "hold everything".
        /// </remarks>
        public int WX_BANDS_KEPT_IN_MEM = 30;

        /// <summary>
        /// Let ELMFIRE place the ignition at random inside the ignition mask.
        /// </summary>
        /// <remarks>
        /// Ignored when the scenario has placed ignition points: those are explicit coordinates and win,
        /// with the console saying so. This is what an ensemble draws from, and what a single case falls
        /// back to when only a mask has been painted.
        /// </remarks>
        public bool RANDOM_IGNITIONS = false;

        public bool USE_IGNITION_MASK = false;
        public double PERCENT_OF_PIXELS_TO_IGNITE = 5.0;
        public bool ALLOW_MULTIPLE_IGNITIONS_AT_A_PIXEL = false;

        // ------------------------------------------------------------------ &SIMULATOR

        /// <summary>1 = level set propagation, 2 = fire potential, 3 = both.</summary>
        public int MODE = 1;

        /// <summary>Console verbosity, 0 to 3.</summary>
        public int FEEDBACK_LEVEL = 3;

        public bool CLEAN_SCRATCH = true;
        public int BANDTHICKNESS = 2;
        public double MAX_RUNTIME = 999999.0;

        public bool UNTAG_TYPE_2 = true;
        public bool UNTAG_TYPE_3 = true;

        /// <summary>1 = Scott &amp; Reinhardt, 2 = Cruz.</summary>
        public int CROWN_FIRE_MODEL = 1;

        public double CRITICAL_CANOPY_COVER = 0.39;
        public double CROWN_RATIO = 1.0;
        public double CROWN_FIRE_ADJ = 1.0;
        public double CROWN_FIRE_SPREAD_RATE_LIMIT = 250.0;

        /// <summary>Slope and wind factor multipliers - the blunt calibration knobs.</summary>
        public double PHIS_ADJ = 1.0;
        public double PHIW_ADJ = 1.0;

        public double SURFACE_ACCELERATION_TIME_CONSTANT = 1.0;
        public bool ALLOW_NONBURNABLE_PIXEL_IGNITION = true;
        public bool WX_BILINEAR_INTERPOLATION = false;

        // ------------------------------------------------------------------ &SPOTTING

        public bool ENABLE_SPOTTING = true;

        /// <summary>
        /// The pre-2023 spotting formulation. ELMFIRE defaults this on; the four model keys below only take
        /// effect with it off, so leaving it on quietly discards them.
        /// </summary>
        public bool USE_SUPERSEDED_SPOTTING = false;

        public EmberGenerationModels GENERATION_MODEL = EmberGenerationModels.PER_MW;
        public SpottingDistanceModels SPOTTING_DISTANCE_MODEL = SpottingDistanceModels.EMPIRICAL;
        public EmberAccumulationModels ACCUMULATION_MODEL = EmberAccumulationModels.EULERIAN;
        public SpotIgnitionModels IGNITION_MODEL = SpotIgnitionModels.PHYSICAL;

        /// <summary>
        /// Draw the eight tuning parameters below per case, between the LO/HI (or MIN/MAX) bounds, instead of
        /// using the single values.
        /// </summary>
        /// <remarks>
        /// This is the switch that decides which half of this group matters. With it off ELMFIRE uses
        /// <see cref="MEAN_SPOTTING_DIST"/> and its seven companions as written; with it on those eight are
        /// overwritten per case from the bounds, so editing them has no effect at all.
        /// </remarks>
        public bool STOCHASTIC_SPOTTING = false;

        public bool ENABLE_SURFACE_FIRE_SPOTTING = false;

        /// <summary>
        /// Fireline intensity below which a cell throws no embers, kW/m. Applied to every fuel model.
        /// </summary>
        /// <remarks>
        /// ELMFIRE declares this over the 303 fuel models. It is written subscripted so the value reaches all
        /// of them — an unsubscripted assignment in a Fortran namelist sets only the first element, which here
        /// is fuel model 0.
        /// </remarks>
        public double CRITICAL_SPOTTING_FIRELINE_INTENSITY = 0.0;

        public double CROWN_FIRE_SPOTTING_PERCENT = 100.0;
        public double GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT = 0.0;

        /// <summary>
        /// Percent of surface-fire cells over the intensity threshold that throw embers, per fuel model.
        /// </summary>
        /// <remarks>
        /// Overwritten by ELMFIRE from <see cref="GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT"/> times
        /// <see cref="SURFACE_FIRE_SPOTTING_PERCENT_MULT"/> whenever <see cref="STOCHASTIC_SPOTTING"/> is on,
        /// so it only has a say in the deterministic case.
        /// </remarks>
        public double SURFACE_FIRE_SPOTTING_PERCENT = 100.0;

        /// <summary>Per-fuel-model multiplier on the global surface spotting percent.</summary>
        public double SURFACE_FIRE_SPOTTING_PERCENT_MULT = 1.0;

        /// <summary>Multiplier on the ignition probability of embers thrown by each source fuel model.</summary>
        public double SOURCE_FUEL_IGN_MULT = 1.0;

        public double MIN_SPOTTING_DISTANCE = 0.0;
        public double MAX_SPOTTING_DISTANCE = 0.0;
        public double MEAN_SPOTTING_DIST = 0.0;
        public double NORMALIZED_SPOTTING_DIST_VARIANCE = 0.0;

        public int NEMBERS_MIN = 30;
        public int NEMBERS_MAX = 1;

        /// <summary>Probability an ember ignites what it lands on, as a PERCENT.</summary>
        public double PIGN = 1.0;

        public double TAU_EMBERGEN = 6.0;
        public double EMBER_GR = 1E-3;
        public double SPOT_WS_EXP = 0.9;
        public double SPOT_FLIN_EXP = 0.5;

        // ---- the stochastic bounds. Read only with STOCHASTIC_SPOTTING, and each pair replaces the single
        // value of the same name. ELMFIRE spells two of them LO/HI rather than MIN/MAX; the names are its own.

        public double MEAN_SPOTTING_DIST_MIN = 0.0;
        public double MEAN_SPOTTING_DIST_MAX = 0.0;
        public double NORMALIZED_SPOTTING_DIST_VARIANCE_MIN = 0.0;
        public double NORMALIZED_SPOTTING_DIST_VARIANCE_MAX = 0.0;
        public double SPOT_WS_EXP_LO = 0.9;
        public double SPOT_WS_EXP_HI = 0.9;
        public double SPOT_FLIN_EXP_LO = 0.5;
        public double SPOT_FLIN_EXP_HI = 0.5;
        public int NEMBERS_MAX_LO = 1;
        public int NEMBERS_MAX_HI = 1;
        public double GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MIN = 0.0;
        public double GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MAX = 0.0;
        public double CROWN_FIRE_SPOTTING_PERCENT_MIN = 100.0;
        public double CROWN_FIRE_SPOTTING_PERCENT_MAX = 100.0;
        public double PIGN_MIN = 1.0;
        public double PIGN_MAX = 1.0;

        /// <summary>
        /// Bounds on the smallest ember count. Kept so a namelist round-trips, but ELMFIRE 1.1's sampler does
        /// not read them: <c>NEMBERS_MIN</c> stays as written even with <see cref="STOCHASTIC_SPOTTING"/> on.
        /// </summary>
        public int NEMBERS_MIN_LO = 0;

        /// <inheritdoc cref="NEMBERS_MIN_LO"/>
        public int NEMBERS_MIN_HI = 0;

        // ---- the ember generation, distance, accumulation and ignition models, and what each one reads.

        /// <summary>
        /// Embers generated per MW of heat release from burning buildings, for
        /// <c>GENERATION_MODEL = PER-MW</c>.
        /// </summary>
        public double EMBER_GR_PER_MW_BLDG = 10.0;

        /// <summary>The same rate for burning vegetation, which throws about three times as many.</summary>
        public double EMBER_GR_PER_MW_VEGE = 33.3;

        /// <summary>
        /// Embers each tracked particle stands for. Above 1 the run tracks fewer particles and scales the flux
        /// up, which is how a large domain is made affordable.
        /// </summary>
        public double EMBER_SAMPLING_FACTOR = 1.0;

        /// <summary>Tail probability that sets the furthest distance the empirical PDF is sampled to.</summary>
        public double P_EPS = 0.01;

        /// <summary>
        /// Use the lognormal parameters below instead of Sardoy's (wildland) and Himoto's (structure) fits to
        /// local wind and fireline intensity. Only read by the empirical distance model.
        /// </summary>
        public bool USE_CUSTOMIZED_PDF = false;

        /// <summary>Mean of the downwind lognormal, in log-metres. Read only with a customized PDF.</summary>
        public double MU_DOWNWIND = 0.0;

        /// <inheritdoc cref="MU_DOWNWIND"/>
        public double SIGMA_DOWNWIND = 0.0;

        /// <summary>Spread embers across the wind rather than sending them all downwind.</summary>
        public bool USE_CROSSWIND_DISTRIBUTION = false;

        /// <summary>Mean of the crosswind lognormal, in log-metres.</summary>
        public double MU_CROSSWIND = 0.0;

        /// <inheritdoc cref="MU_CROSSWIND"/>
        public double SIGMA_CROSSWIND = 0.0;

        /// <summary>
        /// How long a cell keeps throwing embers: derive it from the physics rather than the fixed 1200 s.
        /// </summary>
        public bool USE_PHYSICAL_SPOTTING_DURATION = false;

        /// <summary>Seconds an ignited cell waits before it starts spreading. Simple ignition model only.</summary>
        public double LOCAL_IGNITION_TIME = 30.0;

        /// <summary>Seconds between a cell being ignited by embers and its front being set. Simple model only.</summary>
        public double CELL_IGNITION_DELAY = 100.0;

        /// <summary>Let embers be consumed where they land instead of persisting.</summary>
        public bool USE_EMBER_CONSUMPTION = false;

        /// <summary>Treat ignition in wildland fuels differently from ignition of structures.</summary>
        public bool DIFF_WILDLAND_IGNITION = false;

        /// <summary>
        /// Spread by embers alone, with the surface fire suppressed. A diagnostic for the spotting model, not a
        /// way to run a fire.
        /// </summary>
        public bool NO_SURFACE_FIRE = false;

        /// <summary>
        /// Distribution the superseded formulation samples spotting distance from. Ignored unless
        /// <see cref="USE_SUPERSEDED_SPOTTING"/>, which has <see cref="SPOTTING_DISTANCE_MODEL"/> in its place.
        /// </summary>
        public string SPOTTING_DISTRIBUTION_TYPE = "LOGNORMAL";

        // ------------------------------------------------------------------ &SUPPRESSION

        /// <summary>
        /// A single suppression event at <see cref="INITIAL_ATTACK_TIME"/>, sized against how fast the fire is
        /// growing and how hard its most intense edge is burning.
        /// </summary>
        /// <remarks>
        /// Off for a trigger boundary unless the boundary is meant to assume a response. A fire that is
        /// suppressed reaches less far, so the boundary it produces is smaller and evacuates later — which is
        /// only defensible if the response it assumes is one that will actually happen.
        /// </remarks>
        public bool ENABLE_INITIAL_ATTACK = false;

        /// <summary>Seconds after ignition at which the initial attack arrives.</summary>
        public double INITIAL_ATTACK_TIME = 1800.0;

        /// <summary>
        /// Containment growing over days, as crews hold progressively more of the perimeter.
        /// </summary>
        /// <remarks>
        /// Only means anything over a run long enough for a day to pass. See
        /// <see cref="ENABLE_INITIAL_ATTACK"/> on what assuming a response does to a trigger boundary.
        /// </remarks>
        public bool ENABLE_EXTENDED_ATTACK = false;

        /// <summary>Seconds between containment updates.</summary>
        public double DT_EXTENDED_ATTACK = 3600.0;

        /// <summary>Most of the perimeter that can be contained in one day, as a percent.</summary>
        public double MAX_CONTAINMENT_PER_DAY = 100.0;

        /// <summary>
        /// Growth rate, in acres per day, above which containment stops advancing. ELMFIRE's units, not this
        /// platform's: the suppression model is written in acres throughout.
        /// </summary>
        public double AREA_NO_CONTAINMENT_CHANGE = 10000.0;

        /// <summary>Take the log of the growth rate before comparing it, which softens the fast-growth end.</summary>
        public bool USE_SDI_LOG_FUNCTION = false;

        /// <summary>
        /// Weight the containment rate by how difficult the burning ground is to suppress, read from an SDI
        /// raster.
        /// </summary>
        /// <remarks>
        /// Needs <c>[ELMFIRE] SuppressionDifficultyFile</c>: ELMFIRE fails its own input check with this on and
        /// no SDI raster, so the builder refuses it for a case that has none rather than writing a run that
        /// cannot start.
        /// </remarks>
        public bool USE_SDI = false;

        /// <summary>Multiplier on the raster's values.</summary>
        public double SDI_FACTOR = 1.0;

        /// <summary>How sharply the difficulty index slows containment.</summary>
        public double B_SDI = 1.0;

        // ------------------------------------------------------------------ &SMOKE

        /// <summary>
        /// PM2.5 release, written per interval alongside the fire's own rasters.
        /// </summary>
        /// <remarks>
        /// ELMFIRE's own emissions accounting, and nothing to do with the <c>GlobalSmoke</c> module — that one
        /// takes a fire and disperses its smoke over the domain, while this reports how much was released.
        /// ELMFIRE needs the year and hour of year to timestamp the output; the builder always writes both.
        /// </remarks>
        public bool ENABLE_SMOKE_OUTPUTS = false;

        /// <summary>Seconds between smoke outputs. Also the accounting window each one covers.</summary>
        public double DT_SMOKE_OUTPUTS = 3600.0;

        /// <summary>Grams of PM2.5 per kg of fuel consumed while flaming.</summary>
        public double PM_EMISSION_FACTOR_FLAMING = 17.4;

        /// <summary>The same while smouldering, which is far dirtier per kilogram.</summary>
        public double PM_EMISSION_FACTOR_SMOLDERING = 49.8;

        /// <summary>
        /// Calorific value of dry wood, MJ/kg. Reduced by the cell's own 10-hour moisture to give the wet
        /// value the emissions are divided by.
        /// </summary>
        public double DRY_WOOD_CALORIFIC_VALUE = 19.0;

        /// <summary>Seconds a cell flames for.</summary>
        public double FLAMING_TIME = 180.0;

        /// <summary>Seconds a cell smoulders for after flaming.</summary>
        public double SMOLDERING_TIME = 3600.0;

        // ---- &MONTE_CARLO, beyond the seed and the band range the builder decides.

        /// <summary>
        /// Scale every value in the ignition mask, which scales how many ignitions a mask produces.
        /// </summary>
        public double IGNITION_MASK_SCALE_FACTOR = 1.0;

        /// <summary>
        /// Add this to the ignition mask on every burnable cell, so nowhere burnable has zero chance of
        /// starting. Negative leaves the mask alone, which is ELMFIRE's own default.
        /// </summary>
        public double ADD_TO_IGNITION_MASK = -9e9;

        /// <summary>
        /// How random ignitions are drawn: 1 samples the mask as a density, 2 as a per-cell probability.
        /// </summary>
        public int RANDOM_IGNITIONS_TYPE = 1;

        /// <summary>
        /// Take ignition locations from a CSV instead of drawing them.
        /// </summary>
        /// <remarks>
        /// The CSV is <c>IGNITIONS_CSV_FILENAME</c> in the case's inputs, which this platform does not write —
        /// it places ignitions through the namelist's own <c>X_IGN</c>/<c>Y_IGN</c> instead. Left switchable for
        /// a case whose inputs were prepared elsewhere.
        /// </remarks>
        public bool CSV_FIXED_IGNITION_LOCATIONS = false;

        /// <summary>
        /// Scale the ignition rate by the energy release component, read per weather band from an ERC raster.
        /// </summary>
        /// <remarks>
        /// Needs <c>[ELMFIRE] EnergyReleaseComponentFile</c>. Like <see cref="USE_SDI"/>, ELMFIRE shuts down
        /// with this on and no raster, so the builder writes it off for a case that has none.
        /// </remarks>
        public bool USE_ERC = false;

        /// <summary>Treat the ERC values as a lightning ignition rate rather than a multiplier.</summary>
        public bool ERC_IS_PLIGNRATE = false;

        /// <summary>Aim the wind at the domain centre, which drives every fire inward.</summary>
        /// <remarks>
        /// A landscape-scale device for making every ignition threaten the same place. It replaces the wind
        /// direction the weather rasters hold, so a case whose wind came from WindNinja loses it.
        /// </remarks>
        public bool POINT_WIND_TO_CENTER = false;

        // The wind fluctuation intensities, sampled per case between these bounds. ELMFIRE turns the sampling on
        // by seeing both ends of a pair above zero - there is no separate switch - so -1 means "not sampled" and
        // the fixed values in &SIMULATOR stand.

        /// <summary>Lower bound on the sampled wind speed fluctuation intensity. Negative means not sampled.</summary>
        public double WIND_SPEED_FLUCTUATION_INTENSITY_MIN = -1.0;

        /// <inheritdoc cref="WIND_SPEED_FLUCTUATION_INTENSITY_MIN"/>
        public double WIND_SPEED_FLUCTUATION_INTENSITY_MAX = -1.0;

        /// <summary>Lower bound on the sampled wind direction fluctuation intensity, degrees. Negative means not sampled.</summary>
        public double WIND_DIRECTION_FLUCTUATION_INTENSITY_MIN = -1.0;

        /// <inheritdoc cref="WIND_DIRECTION_FLUCTUATION_INTENSITY_MIN"/>
        public double WIND_DIRECTION_FLUCTUATION_INTENSITY_MAX = -1.0;

        // ---- raster perturbations: one variation per entry, read across the parallel lists below. Held as
        // lists rather than as a list of records because the .wui writer reflects over fields and can write an
        // array but not a nested object, and because this is the shape ELMFIRE's own namelist has.

        /// <summary>
        /// Which raster each variation perturbs. One of ADJ, CBD, CBH, CC, CH, FBFM, FMC, M1, M10, M100, MLH,
        /// MLW, WAF, WD, WS — ELMFIRE stops on anything else.
        /// </summary>
        public string[] RASTER_TO_PERTURB = new string[0];

        /// <summary>GLOBAL to shift the whole raster by one draw, PIXEL to draw per cell.</summary>
        public string[] SPATIAL_PERTURBATION = new string[0];

        /// <summary>STATIC to draw once, DYNAMIC to redraw every weather band.</summary>
        public string[] TEMPORAL_PERTURBATION = new string[0];

        /// <summary>UNIFORM, GAUSSIAN or LOGNORMAL.</summary>
        public string[] PDF_TYPE = new string[0];

        /// <summary>Lower limit, read by the UNIFORM distribution only.</summary>
        public double[] PDF_LOWER_LIMIT = new double[0];

        /// <summary>Upper limit, read by the UNIFORM distribution only.</summary>
        public double[] PDF_UPPER_LIMIT = new double[0];

        /// <summary>Mean, read by GAUSSIAN and LOGNORMAL.</summary>
        public double[] PDF_MEAN = new double[0];

        /// <summary>Standard deviation, read by GAUSSIAN and LOGNORMAL.</summary>
        public double[] PDF_SIGMA = new double[0];

        // ------------------------------------------------------------------ &CALIBRATION
        //
        // Per-pyrome tables for calibrating a landscape-scale ensemble against recorded fire history: one row per
        // pyrome, 128 of them. Each is a CSV that has to be sitting in the case's inputs folder already — the
        // builder has nothing to derive them from — so each switch is written only when a filename is given, and
        // the filename is a bare name rather than a path because that is what ELMFIRE resolves against inputs/.

        /// <summary>Per-pyrome, per-fuel-model spread rate adjustment factors. Also needs <see cref="USE_PYROMES"/>.</summary>
        public string ADJUSTMENT_FACTORS_FILENAME = string.Empty;

        /// <summary>Per-pyrome suppression constants: attack time, containment rate, SDI weight.</summary>
        public string CALIBRATION_CONSTANTS_FILENAME = string.Empty;

        /// <summary>Per-pyrome fire duration probabilities, one column per day.</summary>
        public string DURATION_PDF_FILENAME = string.Empty;

        /// <summary>How many days the duration PDF covers. Columns beyond it are zero.</summary>
        public int DURATION_MAX_DAYS = 28;

        // The switches that read the three tables. Each is written on only when its filename is given: ELMFIRE
        // checks the filename is set for whichever of these is true and shuts down otherwise.

        /// <summary>Read <see cref="ADJUSTMENT_FACTORS_FILENAME"/>.</summary>
        public bool ADJUSTMENT_FACTORS_BY_PYROME = false;

        /// <summary>Read <see cref="CALIBRATION_CONSTANTS_FILENAME"/>.</summary>
        public bool CALIBRATION_CONSTANTS_BY_PYROME = false;

        /// <summary>Read <see cref="DURATION_PDF_FILENAME"/>.</summary>
        public bool DURATION_PDF_BY_PYROME = false;

        // ------------------------------------------------------------------ &WUI

        /// <summary>
        /// Building-to-building spread.
        /// </summary>
        /// <remarks>
        /// Only written when the case actually carries the complete set of five building rasters, or when
        /// constant parameters are used instead. A partial set makes ELMFIRE read a raster nobody wrote.
        /// </remarks>
        public bool USE_BLDG_SPREAD_MODEL = false;

        /// <summary>Use the constants below instead of the case's building rasters.</summary>
        public bool USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS = false;

        /// <summary>1 = Hamada, 2 = UCB/UMD, 3 = the fork's own.</summary>
        public int BLDG_SPREAD_MODEL_TYPE = 3;

        /// <summary>1 = Ellipse, 2 = Threshold.</summary>
        public int INTERFACE_MODEL_TYPE = 2;

        public double GLOBAL_HARDENING_FACTOR = 1.0;
        public int BANDTHICKNESS_WUI = 5;
        public double CRITICL_HF_WUI = 0.0;
        public double HRR_ELLIPSE_ADJ = 0.5;

        public double BLDG_AREA_CONSTANT = 20.0;
        public double BLDG_SEPARATION_DIST_CONSTANT = 10.0;
        public double BLDG_NONBURNABLE_FRAC_CONSTANT = 0.0;
        public double BLDG_FOOTPRINT_FRAC_CONSTANT = 1.0;
        public int BLDG_FUEL_MODEL_CONSTANT = 1;

        public ElmfireNamelistInput()
        {
        }

        /// <summary>
        /// Reads whichever of these keys the section names, leaving the rest at their defaults.
        /// </summary>
        /// <remarks>
        /// By reflection, unlike the hand-written parsers elsewhere in this folder. With ninety-odd fields
        /// whose names are exactly the keys, a hand-written parser is ninety chances to typo a key into
        /// something that silently never loads - and the writer that produces these lines is already
        /// reflection-driven, so this is the same walk in the other direction. Nothing here is critical:
        /// every field has a working default, so an unreadable value is reported and skipped rather than
        /// failing the scenario.
        /// </remarks>
        public static ElmfireNamelistInput Parse(string[] inputLines, int startIndex)
        {
            var newInput = new ElmfireNamelistInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);

            foreach (FieldInfo field in typeof(ElmfireNamelistInput).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!inputToParse.TryGetValue(field.Name, out string userInput) || userInput.Length == 0)
                {
                    continue;
                }

                if (TryConvert(field.FieldType, userInput, out object parsed))
                {
                    field.SetValue(newInput, parsed);
                }
                else
                {
                    PREACTInput.CouldNotInterpretInputMessage(field.Name, userInput);
                }
            }

            return newInput;
        }

        /// <summary>
        /// Fills these settings from an actual ELMFIRE namelist file, so the editor shows what the case will
        /// really run rather than a set of defaults beside a file that overrides them.
        /// </summary>
        /// <remarks>
        /// Every field here is named verbatim after its ELMFIRE key, which is what makes this possible by
        /// reflection: the file is read as <c>KEY = value</c> pairs and matched against the field names, so a
        /// key this class does not model is simply reported rather than silently dropped.
        ///
        /// Fortran namelist syntax rather than the <c>.wui</c>'s: groups are <c>&amp;GROUP … /</c>, comments start
        /// with <c>!</c>, strings are single-quoted, and logicals are <c>.TRUE.</c>/<c>.FALSE.</c>. Group
        /// membership is ignored — the keys are unique across groups, and treating them as a flat set is what
        /// lets one pass fill fields belonging to nine different sections.
        /// </remarks>
        /// <param name="applied">How many settings were taken from the file.</param>
        /// <param name="unrecognised">Keys in the file this class has no field for, in file order.</param>
        public bool LoadFromNamelist(string path, out int applied, out List<string> unrecognised)
        {
            applied = 0;
            unrecognised = new List<string>();

            string[] lines;
            try
            {
                lines = System.IO.File.ReadAllLines(path);
            }
            catch
            {
                return false;
            }

            var fields = new Dictionary<string, FieldInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (FieldInfo f in typeof(ElmfireNamelistInput).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                fields[f.Name] = f;
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("!") || line.StartsWith("&") || line == "/") continue;

                //Trailing comments: a key set on a commented line is not set, and a comment after a value is
                //not part of it. The builder itself emits both forms.
                int bang = line.IndexOf('!');
                if (bang >= 0) line = line.Substring(0, bang).Trim();

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim().TrimEnd(',');

                //A subscript is part of Fortran's assignment, not part of the key: several spotting settings are
                //arrays over the 303 fuel models and are written KEY(0:303) = 304*value, so the name has to be
                //recovered before it can be matched against a field.
                int bracket = key.IndexOf('(');
                if (bracket > 0)
                {
                    key = key.Substring(0, bracket).Trim();
                }

                if (!fields.TryGetValue(key, out FieldInfo field))
                {
                    //Only keys that look like settings, not the file paths and directories the builder writes
                    //from the case's own contents - those are not this class's business and listing them would
                    //make the report noise.
                    if (!key.EndsWith("_FILENAME", StringComparison.OrdinalIgnoreCase)
                        && !key.EndsWith("_DIRECTORY", StringComparison.OrdinalIgnoreCase)
                        && !unrecognised.Contains(key))
                    {
                        unrecognised.Add(key);
                    }
                    continue;
                }

                if (TryConvert(field.FieldType, Normalise(value), out object parsed))
                {
                    field.SetValue(this, parsed);
                    ++applied;
                }
            }

            return true;
        }

        /// <summary>Turns a namelist value into the form <see cref="TryConvert"/> reads.</summary>
        private static string Normalise(string value)
        {
            //Fortran's repeat form: 304*1.0 means the same value 304 times. The settings written that way are
            //the per-fuel-model spotting arrays, which this class holds as one value applied to every fuel
            //model, so the count carries no information here and the element is the whole answer. No
            //array-typed field is ever written in this form, which is what makes dropping the count safe.
            int star = value.IndexOf('*');
            if (star > 0 && int.TryParse(value.Substring(0, star).Trim(), out _))
            {
                value = value.Substring(star + 1).Trim();
            }

            //Logicals: .TRUE. / .T. / T and their negatives.
            string bare = value.Trim('.').Trim();
            if (bare.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || bare.Equals("T", StringComparison.OrdinalIgnoreCase))
            {
                return "true";
            }
            if (bare.Equals("FALSE", StringComparison.OrdinalIgnoreCase) || bare.Equals("F", StringComparison.OrdinalIgnoreCase))
            {
                return "false";
            }

            //Single-quoted strings, which is how a namelist writes an enum value such as 'LOGNORMAL'.
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2).Trim();
            }

            //Fortran exponent forms: 1.0D-3 is the same number as 1.0E-3, and double.TryParse reads only one.
            return value.Replace("D+", "E+").Replace("D-", "E-").Replace("d+", "E+").Replace("d-", "E-");
        }

        private static bool TryConvert(Type type, string text, out object value)
        {
            value = null;

            //Arrays, for the handful of keys ELMFIRE declares with a fixed extent - the flame-length and
            //ember-count bin edges and the virtual station coordinates, all REAL(100) or INTEGER*2(100).
            //Comma-separated in both the .wui and a namelist, which is Fortran's own list syntax, so one
            //conversion serves both. An empty element is skipped rather than parsed as zero: a trailing comma
            //is ordinary in a hand-edited list and should not add a bin at the origin.
            //A list of strings, which is how the Monte Carlo perturbation variations are held: one entry per
            //variation, in the same order across the parallel lists. Quotes are stripped per element because a
            //namelist writes each one quoted - Normalise only sees the whole right-hand side.
            if (type == typeof(string[]))
            {
                var items = new List<string>();
                foreach (string raw in text.Split(','))
                {
                    string part = raw.Trim().Trim('\'', '"').Trim();
                    if (part.Length > 0) items.Add(part);
                }
                value = items.ToArray();
                return true;
            }

            if (type == typeof(double[]) || type == typeof(int[]))
            {
                string[] parts = text.Split(',');
                var doubles = new List<double>();
                var ints = new List<int>();

                foreach (string raw in parts)
                {
                    string part = raw.Trim();
                    if (part.Length == 0) continue;

                    if (type == typeof(double[]))
                    {
                        if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return false;
                        doubles.Add(d);
                    }
                    else
                    {
                        if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return false;
                        ints.Add(i);
                    }
                }

                value = type == typeof(double[]) ? (object)doubles.ToArray() : ints.ToArray();
                return true;
            }

            if (type == typeof(bool))
            {
                if (!bool.TryParse(text, out bool b)) return false;
                value = b;
                return true;
            }

            if (type == typeof(int))
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return false;
                value = i;
                return true;
            }

            if (type == typeof(double))
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return false;
                value = d;
                return true;
            }

            if (type.IsEnum)
            {
                //The writer emits the C# name, so PER_MW round-trips as PER_MW; the hyphenated spelling is
                //only ELMFIRE's, and only the namelist builder produces it. Accepting it here too costs
                //nothing and means a value copied out of a namelist is understood.
                try
                {
                    value = Enum.Parse(type, text.Replace('-', '_'), ignoreCase: true);
                    return true;
                }
                catch (ArgumentException)
                {
                    return false;
                }
            }

            return false;
        }
    }
}
