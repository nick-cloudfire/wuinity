//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using PREACT.Input;

namespace PREACT.Utility
{
    /// <summary>
    /// Writes a complete ELMFIRE namelist from the scenario's settings and the case's own contents.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The split is deliberate. <see cref="ElmfireNamelistInput"/> holds the modelling choices - what the
    /// user decides. Everything else here is a fact about the case rather than a choice, and is read off
    /// the case instead of being asked for: the raster stems, which of them exist, the grid, the time base
    /// from the scenario's start, and the ignition points. Offering those as settings would mean offering
    /// the chance to disagree with the case, and a namelist that disagrees with its rasters fails in ways
    /// that name the wrong thing.
    /// </para>
    /// <para>
    /// Four output keys are forced regardless of what was asked for -
    /// <c>DUMP_TIME_OF_ARRIVAL</c>, <c>DUMP_SPREAD_RATE</c>, <c>DUMP_SPREAD_DIRECTION</c> and
    /// <c>SPREAD_RATE_IN_M</c>. The reader needs all four, and the last is the one that is not an error
    /// anywhere when wrong: without it ELMFIRE dumps ft/min and the fire simply spreads 3.28 times too fast.
    /// </para>
    /// </remarks>
    public static class ElmfireNamelistBuilder
    {
        /// <summary>What the namelist needs to know about the case it is being written for.</summary>
        public class CaseFacts
        {
            /// <summary>Label for the header comment only.</summary>
            public string Name = "case";

            /// <summary>The fuel model stem the case carries, or null if it has none.</summary>
            public string FuelStem;

            /// <summary>Stems present in <c>inputs/</c>, for the optional layers.</summary>
            public HashSet<string> AvailableStems = new HashSet<string>();

            /// <summary>Building layer keys resolved to the case's own stems. Complete set or empty.</summary>
            public List<(string Key, string Stem)> BuildingLayers = new List<(string, string)>();

            /// <summary>Simulation start, for CURRENT_YEAR / BAND_ONE_HOUR_OF_YEAR.</summary>
            public DateTime StartDateTime = new DateTime(2020, 7, 1, 12, 0, 0);

            public double SimulationTstopSeconds = 28800.0;

            /// <summary>Weather bands the case's rasters actually hold; 0 if it could not be determined.</summary>
            public int AvailableMeteorologyBands;

            /// <summary>Ignition points already transformed into the case's CRS.</summary>
            public List<(double X, double Y, double TimeSeconds)> Ignitions = new List<(double, double, double)>();

            /// <summary>GDAL bin directory, or null to leave ELMFIRE's own 'auto' detection to it.</summary>
            public string PathToGdal;

            /// <summary>Whether the case carries a building fuel model table.</summary>
            public bool HasBuildingFuelModelFile;

            /// <summary>
            /// Non-raster files sitting in the case's <c>inputs/</c>, by filename.
            /// </summary>
            /// <remarks>
            /// The calibration tables are checked against this. ELMFIRE only verifies that their filenames are
            /// set, not that the files exist, and then dies on the read with a bare Fortran
            /// <c>severe (29): file not found, unit 100</c> — no mention of which table, from a unit shared with
            /// the namelist itself. Refusing the switch here costs a comment in the file instead.
            /// </remarks>
            public HashSet<string> AvailableInputFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Whether the case carries a surface fuel model table.</summary>
            /// <remarks>
            /// Without one ELMFIRE writes its own built-in table into the case, which is the right behaviour for
            /// a standard FBFM40 or FBFM13 raster and the wrong one for custom fuel models — hence naming the
            /// file only when it is there.
            /// </remarks>
            public bool HasFuelModelFile;

            /// <summary>
            /// Whether the case's canopy rasters hold real units rather than LANDFIRE's scaled integers. Decides
            /// <c>CH_TIMES_10</c>, <c>CBH_TIMES_10</c> and <c>CBD_TIMES_100</c>, which are then not the
            /// scenario's to choose.
            /// </summary>
            public bool CanopyInRealUnits;
        }

        public static string[] Build(ElmfireNamelistInput s, CaseFacts c)
        {
            s = s ?? new ElmfireNamelistInput();
            var l = new List<string>();

            int hourOfYear = (int)(c.StartDateTime - new DateTime(c.StartDateTime.Year, 1, 1)).TotalHours;
            int bands = ResolveMeteorologyBands(s, c);

            l.Add($"! ELMFIRE case '{c.Name}', written by WUInity from the scenario's ELMFIRE settings.");
            l.Add("! Every value below except the filenames, the time base and the ignitions comes from the");
            l.Add("! Hazards tab of the scenario editor. Editing this file directly works, but the next build");
            l.Add("! keeps it rather than rewriting it - see docs/elmfire-case-automation.md.");
            l.Add("!");
            l.Add("! Static layers are on the master grid defined by dem.tif; ELMFIRE reads the domain, CRS and");
            l.Add("! cell size from that file's own georeferencing, so it is the grid of record.");
            l.Add("");

            //---------------------------------------------------------------------------------- &INPUTS
            l.Add("&INPUTS");
            l.Add(Str("FUELS_AND_TOPOGRAPHY_DIRECTORY", "./inputs"));
            l.Add(Str("DEM_FILENAME", "dem"));
            l.Add(Str("SLP_FILENAME", "slp"));
            l.Add(Str("ASP_FILENAME", "asp"));
            l.Add(Str("ADJ_FILENAME", "adj"));
            l.Add(Str("PHI_FILENAME", "phi"));

            if (c.FuelStem != null)
            {
                l.Add(Str("FBFM_FILENAME", c.FuelStem));
            }
            else
            {
                l.Add("! FBFM_FILENAME - the case has no fuel model raster; ELMFIRE will refuse to start");
            }

            foreach (string stem in new[] { "cc", "ch", "cbh", "cbd" })
            {
                string key = stem.ToUpperInvariant() + "_FILENAME";
                if (c.AvailableStems.Contains(stem)) l.Add(Str(key, stem));
                else l.Add($"! {key} - no {stem} raster in the case; ELMFIRE will refuse to start");
            }

            //Scaling is a property of the rasters in the case, not a modelling choice, so where the case knows
            //its canopy is in real units these are forced rather than taken from the scenario. Getting them
            //wrong is silent and large: ELMFIRE's own defaults assume LANDFIRE's scaled integers, so real-unit
            //canopy read with them on is divided by 10 and bulk density by 100 - a 44 m forest becomes 4.4 m
            //and crown fire stops initiating, with nothing reporting a problem.
            if (c.CanopyInRealUnits)
            {
                l.Add("! Canopy is in real units (m, kg/m3, percent), so the LANDFIRE scaling flags are off.");
                l.Add(Bool("CC_IN_PERCENT", true));
                l.Add(Bool("CBD_TIMES_100", false));
                l.Add(Bool("CBH_TIMES_10", false));
                l.Add(Bool("CH_TIMES_10", false));
            }
            else
            {
                l.Add(Bool("CC_IN_PERCENT", s.CC_IN_PERCENT));
                l.Add(Bool("CBD_TIMES_100", s.CBD_TIMES_100));
                l.Add(Bool("CBH_TIMES_10", s.CBH_TIMES_10));
                l.Add(Bool("CH_TIMES_10", s.CH_TIMES_10));
            }
            l.Add("");

            //Validated by ELMFIRE against its own list, so a wrong value stops the run rather than misbehaving.
            l.Add(Str("SURFACE_SPREAD_MODEL", s.SURFACE_SPREAD_MODEL.ToString()));

            //Only read under CFFDRS. Emitted as comments otherwise so the file still records what they are set
            //to without implying the run uses them.
            if (s.SURFACE_SPREAD_MODEL == ElmfireNamelistInput.SurfaceSpreadModels.CFFDRS)
            {
                l.Add(Num("START_DC", s.START_DC));
                l.Add(Num("START_DMC", s.START_DMC));
            }
            else
            {
                l.Add($"! START_DC = {s.START_DC} - only read by the CFFDRS surface model");
                l.Add($"! START_DMC = {s.START_DMC} - only read by the CFFDRS surface model");
            }

            //USE_BARRIERS is written further down, where BARRIER_FILENAME is, so the flag and the file it needs
            //stay together - a barrier raster only stops spread if the case has one to read.

            //Exposure tracking. Each needs a raster this builder does not produce, and ELMFIRE shuts down at
            //startup if the switch is on with no filename set - so each is written only for a case that has the
            //layer, with the filename beside it.
            WriteGatedLayer(l, c, "USE_LAND_VALUE", s.USE_LAND_VALUE, "LAND_VALUE_FILENAME", "land_value");
            WriteGatedLayer(l, c, "USE_POPULATION_DENSITY", s.USE_POPULATION_DENSITY,
                "POPULATION_DENSITY_FILENAME", "population_density");
            WriteGatedLayer(l, c, "USE_REAL_ESTATE_VALUE", s.USE_REAL_ESTATE_VALUE,
                "REAL_ESTATE_VALUE_FILENAME", "real_estate_value");

            //I/O strategy rather than physics.
            l.Add(Bool("USE_TILED_IO", s.USE_TILED_IO));
            l.Add(Bool("USE_BSQ_XML_HEADER", s.USE_BSQ_XML_HEADER));
            l.Add(Bool("USE_EXISTING_BSQS", s.USE_EXISTING_BSQS));
            l.Add(Bool("VRT_INSTEAD_OF_TIF", s.VRT_INSTEAD_OF_TIF));
            l.Add(Bool("ONLY_READ_NEEDED_WX_BANDS", s.ONLY_READ_NEEDED_WX_BANDS));
            l.Add("");

            l.Add(Num("DT_METEOROLOGY", s.DT_METEOROLOGY));
            l.Add(Str("WEATHER_DIRECTORY", "./inputs"));
            l.Add(Str("WS_FILENAME", "ws"));
            l.Add(Str("WD_FILENAME", "wd"));
            l.Add(Str("M1_FILENAME", "m1"));
            l.Add(Str("M10_FILENAME", "m10"));
            l.Add(Str("M100_FILENAME", "m100"));
            l.Add(Bool("WS_AT_10M", s.WS_AT_10M));
            l.Add(Bool("WS_IN_KPH", s.WS_IN_KPH));
            l.Add(Bool("DEAD_MC_IN_PERCENT", s.DEAD_MC_IN_PERCENT));
            l.Add(Bool("LIVE_MC_IN_PERCENT", s.LIVE_MC_IN_PERCENT));
            l.Add(Bool("USE_CONSTANT_LH", s.USE_CONSTANT_LH));
            l.Add(Num("LH_MOISTURE_CONTENT", s.LH_MOISTURE_CONTENT));
            l.Add(Bool("USE_CONSTANT_LW", s.USE_CONSTANT_LW));
            l.Add(Num("LW_MOISTURE_CONTENT", s.LW_MOISTURE_CONTENT));
            l.Add(Bool("USE_CONSTANT_FMC", s.USE_CONSTANT_FMC));
            l.Add(Num("FOLIAR_MOISTURE_CONTENT", s.FOLIAR_MOISTURE_CONTENT));
            l.Add("");

            l.Add(Num("GRID_DECLINATION", s.GRID_DECLINATION));
            l.Add(Bool("ROTATE_ASP", s.ROTATE_ASP));
            l.Add(Bool("ROTATE_WD", s.ROTATE_WD));

            if (c.AvailableStems.Contains("ignition_mask"))
            {
                l.Add(Str("IGNITION_MASK_FILENAME", "ignition_mask"));
            }

            //The flag beside the file it reads. On only when the case actually has the raster: ELMFIRE reading
            //a barrier file that is not there is a failure, and silently on-with-nothing-to-read would be a
            //setting that appears to work. A case with the raster defaults to using it, since ingesting a
            //barrier layer is itself the instruction to apply it, but the scenario can turn it off.
            if (c.AvailableStems.Contains("barriers"))
            {
                l.Add(Bool("USE_BARRIERS", s.USE_BARRIERS));
                l.Add(Str("BARRIER_FILENAME", "barriers"));
            }
            else if (s.USE_BARRIERS)
            {
                l.Add("! USE_BARRIERS was asked for but the case has no barriers.tif, so it is left off.");
                l.Add(Bool("USE_BARRIERS", false));
            }

            //Named whenever the case has them, whether or not the switch that reads them is on: the filename is
            //a fact about the case, and the switch - USE_SDI in &SUPPRESSION, USE_PYROMES in &SIMULATOR, USE_ERC
            //in &MONTE_CARLO - is the decision about using it. They cannot be written together because Fortran
            //refuses a key outside its own group.
            if (c.AvailableStems.Contains("sdi")) l.Add(Str("SDI_FILENAME", "sdi"));
            if (c.AvailableStems.Contains("pyromes")) l.Add(Str("PYROMES_FILENAME", "pyromes"));
            if (c.AvailableStems.Contains("erc")) l.Add(Str("ERC_FILENAME", "erc"));

            foreach ((string key, string stem) in c.BuildingLayers)
            {
                l.Add(Str(key, stem));
            }

            l.Add("/");
            l.Add("");

            //--------------------------------------------------------------------------------- &OUTPUTS
            l.Add("&OUTPUTS");
            l.Add(Str("OUTPUTS_DIRECTORY", "./outputs"));
            l.Add(Num("DTDUMP", s.DTDUMP));
            l.Add("! The fire reader needs these four; they are not the scenario's to switch off.");
            l.Add(Bool("DUMP_TIME_OF_ARRIVAL", true));
            l.Add(Bool("DUMP_SPREAD_RATE", true));
            l.Add(Bool("DUMP_SPREAD_DIRECTION", true));
            l.Add(Bool("SPREAD_RATE_IN_M", true));
            l.Add(Bool("CONVERT_TO_GEOTIFF", true));
            l.Add(Bool("DUMP_FLIN", s.DUMP_FLIN));
            l.Add(Bool("DUMP_CROWN_FIRE", s.DUMP_CROWN_FIRE));
            l.Add(Bool("DUMP_FLAME_LENGTH", s.DUMP_FLAME_LENGTH));
            l.Add(Bool("DUMP_HPUA", s.DUMP_HPUA));
            l.Add(Bool("DUMP_PHI", s.DUMP_PHI));
            l.Add(Bool("DUMP_SURFACE_FIRE", s.DUMP_SURFACE_FIRE));
            l.Add(Bool("DUMP_VELOCITY", s.DUMP_VELOCITY));
            l.Add(Bool("DUMP_WS20", s.DUMP_WS20));
            l.Add(Bool("DUMP_WD20", s.DUMP_WD20));
            l.Add(Bool("DUMP_FIRE_SIZE_STATS", s.DUMP_FIRE_SIZE_STATS));
            l.Add(Bool("DUMP_TRANSIENT_ACREAGE", s.DUMP_TRANSIENT_ACREAGE));
            //The ember outputs are gated on spotting, and not as tidiness: with ENABLE_SPOTTING off, the
            //arrays they dump are never allocated, and ELMFIRE reduces a null pointer across MPI. It dies
            //with "Fatal error in internal_Reduce: Invalid buffer pointer" and a count that happens to equal
            //the cell count - an error naming MPI, about a raster, caused by an output flag. Reproduced
            //deliberately: switching spotting off in a namelist that was otherwise working is enough.
            if (!s.ENABLE_SPOTTING)
            {
                l.Add("! Ember outputs are off with spotting: without it their arrays are never allocated,");
                l.Add("! and ELMFIRE aborts inside MPI_Reduce rather than saying so.");
            }
            l.Add(Bool("DUMP_SPOTTING_OUTPUTS", s.ENABLE_SPOTTING && s.DUMP_SPOTTING_OUTPUTS));
            l.Add(Bool("ACCUMULATE_EMBER_FLUX", s.ENABLE_SPOTTING && s.ACCUMULATE_EMBER_FLUX));
            l.Add(Bool("DUMP_EMBER_FLUX", s.ENABLE_SPOTTING && s.DUMP_EMBER_FLUX));
            l.Add(Bool("DUMP_EMBER_IGNITION", s.ENABLE_SPOTTING && s.DUMP_EMBER_IGNITION));

            //The rest of the ember and radiation dumps, gated for the same reason.
            l.Add(Bool("DUMP_EMBER_FLUX_TRANSIENT", s.ENABLE_SPOTTING && s.DUMP_EMBER_FLUX_TRANSIENT));
            l.Add(Bool("DUMP_TOTAL_DFC_RECEIVED", s.ENABLE_SPOTTING && s.DUMP_TOTAL_DFC_RECEIVED));
            l.Add(Bool("DUMP_TOTAL_RAD_RECEIVED", s.ENABLE_SPOTTING && s.DUMP_TOTAL_RAD_RECEIVED));
            l.Add(Bool("DUMP_TRANSIENT_DFC", s.ENABLE_SPOTTING && s.DUMP_TRANSIENT_DFC));
            l.Add(Bool("DUMP_TRANSIENT_RAD", s.ENABLE_SPOTTING && s.DUMP_TRANSIENT_RAD));
            l.Add(Bool("DUMP_HRR_TRANSIENT", s.ENABLE_SPOTTING && s.DUMP_HRR_TRANSIENT));
            l.Add("");

            //Plain raster and scalar dumps. Nothing here changes the fire, so they are written as asked.
            l.Add(Bool("DUMP_CRITICAL_FLIN", s.DUMP_CRITICAL_FLIN));
            l.Add(Bool("DUMP_REACTION_INTENSITY", s.DUMP_REACTION_INTENSITY));
            l.Add(Bool("DUMP_FUEL_CONSUMPTION", s.DUMP_FUEL_CONSUMPTION));
            l.Add(Bool("DUMP_FIRE_VOLUME", s.DUMP_FIRE_VOLUME));
            l.Add(Bool("DUMP_TAGGED", s.DUMP_TAGGED));
            l.Add(Bool("DUMP_HOURLY_RASTERS", s.DUMP_HOURLY_RASTERS));
            l.Add(Bool("DUMP_EVERY_STEP", s.DUMP_EVERY_STEP));
            l.Add(Bool("DUMP_CROWN_FIRE_AREA", s.DUMP_CROWN_FIRE_AREA));
            l.Add(Bool("DUMP_SURFACE_FIRE_AREA", s.DUMP_SURFACE_FIRE_AREA));
            l.Add(Bool("DUMP_TIMINGS", s.DUMP_TIMINGS));
            l.Add(Bool("DUMP_EMITIMES", s.DUMP_EMITIMES));
            l.Add(Bool("USE_FOUR_DIGITS_IN_IWX_BAND", s.USE_FOUR_DIGITS_IN_IWX_BAND));

            //Only the CFFDRS model fills the arrays this dumps.
            bool cffdrs = s.SURFACE_SPREAD_MODEL == ElmfireNamelistInput.SurfaceSpreadModels.CFFDRS;
            l.Add(Bool("DUMP_CFFDRS_DEBUG", cffdrs && s.DUMP_CFFDRS_DEBUG));
            if (s.DUMP_CFFDRS_DEBUG && !cffdrs)
            {
                l.Add("! DUMP_CFFDRS_DEBUG is off: the Rothermel model never fills what it would dump.");
            }

            //Exposure dumps need their INPUTS counterpart, which in turn needs a raster the case may not have.
            //Off without it rather than on-and-empty, since an empty exposure raster reads as "nothing at risk".
            WriteExposureDump(l, "DUMP_AFFECTED_POPULATION", s.DUMP_AFFECTED_POPULATION,
                s.USE_POPULATION_DENSITY, "USE_POPULATION_DENSITY");
            WriteExposureDump(l, "DUMP_AFFECTED_LAND_VALUE", s.DUMP_AFFECTED_LAND_VALUE,
                s.USE_LAND_VALUE, "USE_LAND_VALUE");
            WriteExposureDump(l, "DUMP_AFFECTED_REAL_ESTATE_VALUE", s.DUMP_AFFECTED_REAL_ESTATE_VALUE,
                s.USE_REAL_ESTATE_VALUE, "USE_REAL_ESTATE_VALUE");
            l.Add("");

            //Binary outputs and their three modifiers, which do nothing on their own.
            l.Add(Bool("DUMP_BINARY_OUTPUTS", s.DUMP_BINARY_OUTPUTS));
            if (s.DUMP_BINARY_OUTPUTS)
            {
                l.Add(Num("BINARY_OUTPUTS_DUMP_FRACTION", s.BINARY_OUTPUTS_DUMP_FRACTION));
                l.Add(Bool("FULL_BINARY_OUTPUTS", s.FULL_BINARY_OUTPUTS));
                l.Add(Num("MINIMUM_AREA_FOR_BINARY_OUTPUTS", s.MINIMUM_AREA_FOR_BINARY_OUTPUTS));
            }

            l.Add(Bool("CALCULATE_TIMES_BURNED", s.CALCULATE_TIMES_BURNED));

            //Flame length stats, then its binning, then the bin edges - each only when the one above it is on.
            l.Add(Bool("CALCULATE_FLAME_LENGTH_STATS", s.CALCULATE_FLAME_LENGTH_STATS));
            if (s.CALCULATE_FLAME_LENGTH_STATS)
            {
                l.Add(Bool("USE_FLAME_LENGTH_BINS", s.USE_FLAME_LENGTH_BINS));
                if (s.USE_FLAME_LENGTH_BINS)
                {
                    l.Add(Int("NUM_FLAME_LENGTH_BINS", s.NUM_FLAME_LENGTH_BINS));
                    WriteArray(l, "FLAME_LENGTH_BIN_LO", s.FLAME_LENGTH_BIN_LO);
                    WriteArray(l, "FLAME_LENGTH_BIN_HI", s.FLAME_LENGTH_BIN_HI);
                }
            }
            else if (s.USE_FLAME_LENGTH_BINS)
            {
                l.Add("! USE_FLAME_LENGTH_BINS needs CALCULATE_FLAME_LENGTH_STATS, so it is off.");
            }

            //Ember count bins need the ember flux they count, and so spotting.
            bool emberFlux = s.ENABLE_SPOTTING && s.ACCUMULATE_EMBER_FLUX;
            if (emberFlux && s.USE_EMBER_COUNT_BINS)
            {
                l.Add(Bool("USE_EMBER_COUNT_BINS", true));
                l.Add(Int("NUM_EMBER_COUNT_BINS", s.NUM_EMBER_COUNT_BINS));
                WriteArray(l, "EMBER_COUNT_BIN_LO", s.EMBER_COUNT_BIN_LO);
                WriteArray(l, "EMBER_COUNT_BIN_HI", s.EMBER_COUNT_BIN_HI);
            }
            else if (s.USE_EMBER_COUNT_BINS)
            {
                l.Add("! USE_EMBER_COUNT_BINS needs ACCUMULATE_EMBER_FLUX and spotting, so it is off.");
            }

            //Virtual stations: the count and the coordinate pair travel together or not at all.
            if (s.NUM_VIRTUAL_STATIONS > 0)
            {
                l.Add(Int("NUM_VIRTUAL_STATIONS", s.NUM_VIRTUAL_STATIONS));
                WriteArray(l, "VIRTUAL_STATION_X", s.VIRTUAL_STATION_X);
                WriteArray(l, "VIRTUAL_STATION_Y", s.VIRTUAL_STATION_Y);
            }

            WriteArray(l, "TIME_AT_BURNED_ACRES", s.TIME_AT_BURNED_ACRES);

            l.Add("/");
            l.Add("");

            //---------------------------------------------------------------------------- &TIME_CONTROL
            l.Add("&TIME_CONTROL");
            l.Add($"! {c.StartDateTime:yyyy-MM-dd HH:mm}, the scenario's own start. Band one is this hour, so");
            l.Add("! these must agree with what the weather rasters hold or the fire runs on the wrong day.");
            l.Add(Int("CURRENT_YEAR", c.StartDateTime.Year));
            l.Add(Int("BAND_ONE_HOUR_OF_YEAR", hourOfYear));

            //Both are the same instant said a different way, and both are read: the diurnal adjustment and the
            //smoke outputs refuse to start without HOUR_OF_YEAR, and FORECAST_START_HOUR is what the diurnal
            //branch adds simulated time to when deciding where in the day it is. Derived, never asked for.
            l.Add(Int("HOUR_OF_YEAR", hourOfYear));
            l.Add(Num("FORECAST_START_HOUR", c.StartDateTime.TimeOfDay.TotalHours));

            l.Add(Num("SIMULATION_TSTART", s.SIMULATION_TSTART));
            l.Add(Num("SIMULATION_TSTOP", c.SimulationTstopSeconds));
            l.Add(Bool("RANDOMIZE_SIMULATION_TSTOP", s.RANDOMIZE_SIMULATION_TSTOP));
            if (s.RANDOMIZE_SIMULATION_TSTOP)
            {
                l.Add("! Each case stops somewhere between TSTART and TSTOP, so the run length above is a");
                l.Add("! ceiling rather than the length any one case gets.");
            }
            l.Add(Num("SIMULATION_DT", s.SIMULATION_DT));
            l.Add(Num("SIMULATION_DTMAX", s.SIMULATION_DTMAX));
            l.Add(Num("TARGET_CFL", s.TARGET_CFL));
            l.Add(Bool("USE_DIURNAL_ADJUSTMENT_FACTOR", s.USE_DIURNAL_ADJUSTMENT_FACTOR));
            if (s.USE_DIURNAL_ADJUSTMENT_FACTOR)
            {
                l.Add(Num("OVERNIGHT_ADJUSTMENT_FACTOR", s.OVERNIGHT_ADJUSTMENT_FACTOR));
                l.Add(Num("BURN_PERIOD_LENGTH", s.BURN_PERIOD_LENGTH));
                l.Add(Num("BURN_PERIOD_CENTER_FRAC", s.BURN_PERIOD_CENTER_FRAC));
            }

            l.Add("! How often each transient weather field is re-read, in seconds of simulated time.");
            l.Add(Num("DT_INTERPOLATE_M1", s.DT_INTERPOLATE_M1));
            l.Add(Num("DT_INTERPOLATE_M10", s.DT_INTERPOLATE_M10));
            l.Add(Num("DT_INTERPOLATE_M100", s.DT_INTERPOLATE_M100));
            l.Add(Num("DT_INTERPOLATE_MLH", s.DT_INTERPOLATE_MLH));
            l.Add(Num("DT_INTERPOLATE_MLW", s.DT_INTERPOLATE_MLW));
            l.Add(Num("DT_INTERPOLATE_FMC", s.DT_INTERPOLATE_FMC));
            l.Add(Num("DT_INTERPOLATE_WIND", s.DT_INTERPOLATE_WIND));
            l.Add("/");
            l.Add("");

            //----------------------------------------------------------------------------- &MONTE_CARLO
            l.Add("&MONTE_CARLO");
            l.Add(bands == c.AvailableMeteorologyBands && c.AvailableMeteorologyBands > 0
                ? $"! {bands} hourly band(s), counted from the case's own weather rasters."
                : $"! {bands} hourly band(s), as set in the scenario.");
            l.Add(Int("METEOROLOGY_BAND_START", 1));
            l.Add(Int("METEOROLOGY_BAND_STOP", bands));
            l.Add(Int("METEOROLOGY_BAND_SKIP_INTERVAL", 1));
            l.Add(Int("NUM_METEOROLOGY_TIMES", bands));
            l.Add(Int("NUM_ENSEMBLE_MEMBERS", s.NUM_ENSEMBLE_MEMBERS));
            l.Add(Int("SEED", s.SEED));
            l.Add(Num("EDGEBUFFER", s.EDGEBUFFER));

            //Explicit points beat the mask, and saying so in the file saves the next reader working out why
            //RANDOM_IGNITIONS is off in a scenario whose settings ask for it.
            bool placed = c.Ignitions.Count > 0;
            if (placed && (s.RANDOM_IGNITIONS || s.USE_IGNITION_MASK))
            {
                l.Add("! Random ignition is off: the scenario places explicit ignition points, in &SIMULATOR.");
            }
            bool random = !placed && s.RANDOM_IGNITIONS;
            bool mask = !placed && s.USE_IGNITION_MASK;
            l.Add(Bool("RANDOM_IGNITIONS", random));
            l.Add(Bool("USE_IGNITION_MASK", mask));
            if (random)
            {
                l.Add(Num("PERCENT_OF_PIXELS_TO_IGNITE", s.PERCENT_OF_PIXELS_TO_IGNITE));
                l.Add(Bool("ALLOW_MULTIPLE_IGNITIONS_AT_A_PIXEL", s.ALLOW_MULTIPLE_IGNITIONS_AT_A_PIXEL));
                l.Add(Int("RANDOM_IGNITIONS_TYPE", s.RANDOM_IGNITIONS_TYPE));
            }
            if (mask)
            {
                l.Add(Num("IGNITION_MASK_SCALE_FACTOR", s.IGNITION_MASK_SCALE_FACTOR));

                //Only read when positive, and ELMFIRE's own default is a large negative meaning "leave the mask
                //alone". Written only when it is actually asking for something.
                if (s.ADD_TO_IGNITION_MASK > 0.0)
                {
                    l.Add(Num("ADD_TO_IGNITION_MASK", s.ADD_TO_IGNITION_MASK));
                }
            }

            //The CSV is not something this platform writes; a case whose inputs came from elsewhere may have one.
            l.Add(Bool("CSV_FIXED_IGNITION_LOCATIONS", s.CSV_FIXED_IGNITION_LOCATIONS));

            if (WriteGatedFlag(l, c, "USE_ERC", s.USE_ERC, "erc"))
            {
                l.Add(Bool("ERC_IS_PLIGNRATE", s.ERC_IS_PLIGNRATE));
            }

            l.Add(Bool("POINT_WIND_TO_CENTER", s.POINT_WIND_TO_CENTER));
            if (s.POINT_WIND_TO_CENTER)
            {
                l.Add("! This replaces the wind direction the weather rasters hold, so whatever WindNinja");
                l.Add("! produced for this domain is not what the fire will see.");
            }

            //Sampling is switched on by both ends of a pair being above zero - there is no separate flag - so a
            //half-set pair silently does nothing and is worth saying so about.
            WriteFluctuationRange(l, "WIND_SPEED_FLUCTUATION_INTENSITY",
                s.WIND_SPEED_FLUCTUATION_INTENSITY_MIN, s.WIND_SPEED_FLUCTUATION_INTENSITY_MAX);
            WriteFluctuationRange(l, "WIND_DIRECTION_FLUCTUATION_INTENSITY",
                s.WIND_DIRECTION_FLUCTUATION_INTENSITY_MIN, s.WIND_DIRECTION_FLUCTUATION_INTENSITY_MAX);

            WritePerturbations(l, s, bands);

            l.Add("/");
            l.Add("");

            //------------------------------------------------------------------------------- &SIMULATOR
            l.Add("&SIMULATOR");

            //The rolling weather window, and the reason a 300-hour run does not hold 300 bands of five rasters
            //in RAM. This used to be written above the band count to disable the window, because ELMFIRE
            //crashed swapping slices; that was fixed in the vendored source on 2026-08-05 (band indices are
            //rebased before they are clamped) and the window is measurably identical to holding everything.
            //Two is the floor: a band is interpolated against the next one.
            l.Add(Int("WX_BANDS_KEPT_IN_MEM", System.Math.Max(2, s.WX_BANDS_KEPT_IN_MEM)));

            l.Add(Int("MODE", s.MODE));
            l.Add(Int("FEEDBACK_LEVEL", s.FEEDBACK_LEVEL));
            l.Add(Bool("CLEAN_SCRATCH", s.CLEAN_SCRATCH));
            l.Add(Int("BANDTHICKNESS", s.BANDTHICKNESS));
            l.Add(Num("MAX_RUNTIME", s.MAX_RUNTIME));
            l.Add(Bool("UNTAG_TYPE_2", s.UNTAG_TYPE_2));
            l.Add(Bool("UNTAG_TYPE_3", s.UNTAG_TYPE_3));
            l.Add(Int("UNTAG_CELLS_TIMESTEP_INTERVAL", s.UNTAG_CELLS_TIMESTEP_INTERVAL));

            //Never on for a case WUInity generates. A campaign varies SEED per realization to make the
            //ensemble, and this makes ELMFIRE seed from SYSTEM_CLOCK instead - so the seed would do nothing and
            //no realization could be reproduced from the record of what produced it. Written explicitly rather
            //than left to ELMFIRE's default, which happens to agree, because agreeing by luck is not the same
            //as being decided.
            l.Add(Bool("RANDOMIZE_RANDOM_SEED", false));
            if (s.RANDOMIZE_RANDOM_SEED)
            {
                l.Add("! RANDOMIZE_RANDOM_SEED was asked for but is refused: it makes SEED inert, and every");
                l.Add("! ensemble this platform builds varies SEED to produce its realizations.");
            }

            l.Add(Bool("MULTIPLE_HOSTS", s.MULTIPLE_HOSTS));
            l.Add(Bool("ESTIMATE_URBAN_LOSSES", s.ESTIMATE_URBAN_LOSSES));
            WriteGatedFlag(l, c, "USE_PYROMES", s.USE_PYROMES, "pyromes");
            l.Add(Num("MAX_LOW", s.MAX_LOW));
            l.Add(Num("WSMFEFF_LOW_MULT", s.WSMFEFF_LOW_MULT));
            l.Add(Num("PLIGNRATE_MIN", s.PLIGNRATE_MIN));

            //The three fluctuation settings only exist inside the guard, so they are written only with it.
            l.Add(Bool("WIND_FLUCTUATIONS", s.WIND_FLUCTUATIONS));
            if (s.WIND_FLUCTUATIONS)
            {
                l.Add(Num("DT_WIND_FLUCTUATIONS", s.DT_WIND_FLUCTUATIONS));
                l.Add(Num("WIND_SPEED_FLUCTUATION_INTENSITY", s.WIND_SPEED_FLUCTUATION_INTENSITY));
                l.Add(Num("WIND_DIRECTION_FLUCTUATION_INTENSITY", s.WIND_DIRECTION_FLUCTUATION_INTENSITY));
            }
            l.Add(Int("CROWN_FIRE_MODEL", s.CROWN_FIRE_MODEL));
            l.Add(Num("CRITICAL_CANOPY_COVER", s.CRITICAL_CANOPY_COVER));
            l.Add(Num("CROWN_RATIO", s.CROWN_RATIO));
            l.Add(Num("CROWN_FIRE_ADJ", s.CROWN_FIRE_ADJ));
            l.Add(Num("CROWN_FIRE_SPREAD_RATE_LIMIT", s.CROWN_FIRE_SPREAD_RATE_LIMIT));
            l.Add(Num("PHIS_ADJ", s.PHIS_ADJ));
            l.Add(Num("PHIW_ADJ", s.PHIW_ADJ));
            l.Add(Num("SURFACE_ACCELERATION_TIME_CONSTANT", s.SURFACE_ACCELERATION_TIME_CONSTANT));
            l.Add(Bool("ALLOW_NONBURNABLE_PIXEL_IGNITION", s.ALLOW_NONBURNABLE_PIXEL_IGNITION));
            l.Add(Bool("WX_BILINEAR_INTERPOLATION", s.WX_BILINEAR_INTERPOLATION));

            if (placed)
            {
                l.Add("");
                l.Add("! From the scenario's [IgnitionPoint] sections, in the case's own CRS.");
                l.Add(Int("NUM_IGNITIONS", c.Ignitions.Count));
                for (int i = 0; i < c.Ignitions.Count; ++i)
                {
                    (double x, double y, double t) = c.Ignitions[i];
                    l.Add(Num($"X_IGN({i + 1})", x));
                    l.Add(Num($"Y_IGN({i + 1})", y));
                    l.Add(Num($"T_IGN({i + 1})", t));
                }
            }

            l.Add("/");
            l.Add("");

            //-------------------------------------------------------------------------------- &SPOTTING
            l.Add("&SPOTTING");
            l.Add(Bool("ENABLE_SPOTTING", s.ENABLE_SPOTTING));
            if (s.ENABLE_SPOTTING)
            {
                l.Add(Bool("USE_SUPERSEDED_SPOTTING", s.USE_SUPERSEDED_SPOTTING));
                l.Add(Bool("NO_SURFACE_FIRE", s.NO_SURFACE_FIRE));

                //Which distance settings are read depends on which formulation is in force, so the two are
                //written apart. The superseded one has one distribution switch and no separate models.
                bool empirical = false;
                bool lagrangian = true;
                if (s.USE_SUPERSEDED_SPOTTING)
                {
                    l.Add(Str("SPOTTING_DISTRIBUTION_TYPE", s.SPOTTING_DISTRIBUTION_TYPE));
                }
                else
                {
                    empirical = s.SPOTTING_DISTANCE_MODEL == ElmfireNamelistInput.SpottingDistanceModels.EMPIRICAL;
                    lagrangian = s.ACCUMULATION_MODEL == ElmfireNamelistInput.EmberAccumulationModels.LAGRANGIAN;

                    l.Add(Str("GENERATION_MODEL", Hyphenated(s.GENERATION_MODEL)));
                    l.Add(Str("SPOTTING_DISTANCE_MODEL", Hyphenated(s.SPOTTING_DISTANCE_MODEL)));
                    l.Add(Str("ACCUMULATION_MODEL", Hyphenated(s.ACCUMULATION_MODEL)));
                    l.Add(Str("IGNITION_MODEL", Hyphenated(s.IGNITION_MODEL)));

                    //ELMFIRE stops on this combination rather than working around it, so it is caught here where
                    //there is something to say about it.
                    if (!lagrangian && s.SPOTTING_DISTANCE_MODEL == ElmfireNamelistInput.SpottingDistanceModels.UNIFORM)
                    {
                        l.Add("! ELMFIRE refuses UNIFORM distances with EULERIAN accumulation. This run will stop");
                        l.Add("! at startup; choose LOGNORMAL or EMPIRICAL distances, or LAGRANGIAN accumulation.");
                    }
                }

                l.Add("");
                l.Add("! How many embers a burning cell throws.");
                if (s.USE_SUPERSEDED_SPOTTING
                    || s.GENERATION_MODEL == ElmfireNamelistInput.EmberGenerationModels.RANDOM)
                {
                    l.Add(Int("NEMBERS_MIN", s.NEMBERS_MIN));
                    if (!s.STOCHASTIC_SPOTTING) l.Add(Int("NEMBERS_MAX", s.NEMBERS_MAX));
                }
                else if (s.GENERATION_MODEL == ElmfireNamelistInput.EmberGenerationModels.PER_AREA)
                {
                    l.Add(Num("EMBER_GR", s.EMBER_GR));
                }
                else
                {
                    l.Add(Num("EMBER_GR_PER_MW_BLDG", s.EMBER_GR_PER_MW_BLDG));
                    l.Add(Num("EMBER_GR_PER_MW_VEGE", s.EMBER_GR_PER_MW_VEGE));
                }
                l.Add(Num("EMBER_SAMPLING_FACTOR", s.EMBER_SAMPLING_FACTOR));

                l.Add("");
                l.Add("! Which burning cells throw them, and how likely each ember is to start a fire.");
                l.Add(Bool("ENABLE_SURFACE_FIRE_SPOTTING", s.ENABLE_SURFACE_FIRE_SPOTTING));
                l.Add(PerFuel("CRITICAL_SPOTTING_FIRELINE_INTENSITY", s.CRITICAL_SPOTTING_FIRELINE_INTENSITY));
                l.Add(PerFuel("SOURCE_FUEL_IGN_MULT", s.SOURCE_FUEL_IGN_MULT));
                l.Add(Bool("DIFF_WILDLAND_IGNITION", s.DIFF_WILDLAND_IGNITION));
                l.Add(Bool("USE_EMBER_CONSUMPTION", s.USE_EMBER_CONSUMPTION));

                l.Add("");
                l.Add(Bool("STOCHASTIC_SPOTTING", s.STOCHASTIC_SPOTTING));
                if (s.STOCHASTIC_SPOTTING)
                {
                    l.Add("! Sampled per case between these bounds, which is why the single values are absent.");
                    l.Add(Num("MEAN_SPOTTING_DIST_MIN", s.MEAN_SPOTTING_DIST_MIN));
                    l.Add(Num("MEAN_SPOTTING_DIST_MAX", s.MEAN_SPOTTING_DIST_MAX));
                    l.Add(Num("NORMALIZED_SPOTTING_DIST_VARIANCE_MIN", s.NORMALIZED_SPOTTING_DIST_VARIANCE_MIN));
                    l.Add(Num("NORMALIZED_SPOTTING_DIST_VARIANCE_MAX", s.NORMALIZED_SPOTTING_DIST_VARIANCE_MAX));
                    l.Add(Num("SPOT_WS_EXP_LO", s.SPOT_WS_EXP_LO));
                    l.Add(Num("SPOT_WS_EXP_HI", s.SPOT_WS_EXP_HI));
                    l.Add(Num("SPOT_FLIN_EXP_LO", s.SPOT_FLIN_EXP_LO));
                    l.Add(Num("SPOT_FLIN_EXP_HI", s.SPOT_FLIN_EXP_HI));
                    l.Add(Int("NEMBERS_MAX_LO", s.NEMBERS_MAX_LO));
                    l.Add(Int("NEMBERS_MAX_HI", s.NEMBERS_MAX_HI));
                    l.Add(Num("GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MIN", s.GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MIN));
                    l.Add(Num("GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MAX", s.GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT_MAX));
                    l.Add(Num("CROWN_FIRE_SPOTTING_PERCENT_MIN", s.CROWN_FIRE_SPOTTING_PERCENT_MIN));
                    l.Add(Num("CROWN_FIRE_SPOTTING_PERCENT_MAX", s.CROWN_FIRE_SPOTTING_PERCENT_MAX));
                    l.Add(Num("PIGN_MIN", s.PIGN_MIN));
                    l.Add(Num("PIGN_MAX", s.PIGN_MAX));

                    //The surface percent is recomputed from the global percent times this multiplier every time
                    //the sampler runs, so writing the percent itself here would be writing something overwritten.
                    l.Add(PerFuel("SURFACE_FIRE_SPOTTING_PERCENT_MULT", s.SURFACE_FIRE_SPOTTING_PERCENT_MULT));

                    //ELMFIRE 1.1 samples eight parameters and NEMBERS_MIN is not one of them, so its bounds are
                    //written for the record and to keep a hand-edited file's values, not because they act.
                    l.Add("! ELMFIRE 1.1's sampler does not read these two: NEMBERS_MIN stays as written above.");
                    l.Add(Int("NEMBERS_MIN_LO", s.NEMBERS_MIN_LO));
                    l.Add(Int("NEMBERS_MIN_HI", s.NEMBERS_MIN_HI));
                }
                else
                {
                    l.Add(Num("CROWN_FIRE_SPOTTING_PERCENT", s.CROWN_FIRE_SPOTTING_PERCENT));
                    l.Add(Num("GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT", s.GLOBAL_SURFACE_FIRE_SPOTTING_PERCENT));
                    l.Add(PerFuel("SURFACE_FIRE_SPOTTING_PERCENT", s.SURFACE_FIRE_SPOTTING_PERCENT));
                    l.Add(Num("PIGN", s.PIGN));
                    l.Add(Num("SPOT_WS_EXP", s.SPOT_WS_EXP));
                    l.Add(Num("SPOT_FLIN_EXP", s.SPOT_FLIN_EXP));
                }

                l.Add("");
                l.Add("! How far they travel.");
                if (s.USE_SUPERSEDED_SPOTTING
                    || s.SPOTTING_DISTANCE_MODEL == ElmfireNamelistInput.SpottingDistanceModels.UNIFORM)
                {
                    l.Add(Num("MIN_SPOTTING_DISTANCE", s.MIN_SPOTTING_DISTANCE));
                    l.Add(Num("MAX_SPOTTING_DISTANCE", s.MAX_SPOTTING_DISTANCE));
                }
                if (!s.STOCHASTIC_SPOTTING && !empirical)
                {
                    //The lognormal parameters are derived from these two, the local wind and the fireline
                    //intensity; the empirical model gets them from its own fits instead.
                    l.Add(Num("MEAN_SPOTTING_DIST", s.MEAN_SPOTTING_DIST));
                    l.Add(Num("NORMALIZED_SPOTTING_DIST_VARIANCE", s.NORMALIZED_SPOTTING_DIST_VARIANCE));
                }
                if (empirical)
                {
                    l.Add(Num("P_EPS", s.P_EPS));
                    l.Add(Bool("USE_CUSTOMIZED_PDF", s.USE_CUSTOMIZED_PDF));
                    if (s.USE_CUSTOMIZED_PDF)
                    {
                        l.Add(Num("MU_DOWNWIND", s.MU_DOWNWIND));
                        l.Add(Num("SIGMA_DOWNWIND", s.SIGMA_DOWNWIND));
                    }
                }

                //Crosswind spread is read by both distance branches, so it sits outside them.
                l.Add(Bool("USE_CROSSWIND_DISTRIBUTION", s.USE_CROSSWIND_DISTRIBUTION));
                if (s.USE_CROSSWIND_DISTRIBUTION && !(empirical && !s.USE_CUSTOMIZED_PDF))
                {
                    l.Add(Num("MU_CROSSWIND", s.MU_CROSSWIND));
                    l.Add(Num("SIGMA_CROSSWIND", s.SIGMA_CROSSWIND));
                }

                l.Add("");
                l.Add("! How long a cell throws them, and what happens where they land.");
                l.Add(Bool("USE_PHYSICAL_SPOTTING_DURATION", s.USE_PHYSICAL_SPOTTING_DURATION));
                l.Add(Num("TAU_EMBERGEN", s.TAU_EMBERGEN));
                if (!s.USE_SUPERSEDED_SPOTTING
                    && s.IGNITION_MODEL != ElmfireNamelistInput.SpotIgnitionModels.DIRECT)
                {
                    l.Add(Num("LOCAL_IGNITION_TIME", s.LOCAL_IGNITION_TIME));
                    l.Add(Num("CELL_IGNITION_DELAY", s.CELL_IGNITION_DELAY));
                }
            }
            l.Add("/");
            l.Add("");

            //------------------------------------------------------------------------------------- &WUI
            //Only when the model can actually run: it needs either the full set of building rasters or the
            //constants. Writing USE_BLDG_SPREAD_MODEL = .TRUE. without either makes ELMFIRE read a raster
            //nobody produced.
            bool buildingsUsable = s.USE_BLDG_SPREAD_MODEL
                                   && (s.USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS || c.BuildingLayers.Count == 5);
            l.Add("&WUI");
            l.Add(Bool("USE_BLDG_SPREAD_MODEL", buildingsUsable));
            if (buildingsUsable)
            {
                l.Add(Bool("USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS", s.USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS));
                l.Add(Int("BLDG_SPREAD_MODEL_TYPE", s.BLDG_SPREAD_MODEL_TYPE));
                l.Add(Int("INTERFACE_MODEL_TYPE", s.INTERFACE_MODEL_TYPE));
                l.Add(Num("GLOBAL_HARDENING_FACTOR", s.GLOBAL_HARDENING_FACTOR));
                l.Add(Int("BANDTHICKNESS_WUI", s.BANDTHICKNESS_WUI));
                l.Add(Num("CRITICL_HF_WUI", s.CRITICL_HF_WUI));
                l.Add(Num("HRR_ELLIPSE_ADJ", s.HRR_ELLIPSE_ADJ));

                if (s.USE_CONSTANT_BLDG_SPREAD_MODEL_PARAMS)
                {
                    l.Add(Num("BLDG_AREA_CONSTANT", s.BLDG_AREA_CONSTANT));
                    l.Add(Num("BLDG_SEPARATION_DIST_CONSTANT", s.BLDG_SEPARATION_DIST_CONSTANT));
                    l.Add(Num("BLDG_NONBURNABLE_FRAC_CONSTANT", s.BLDG_NONBURNABLE_FRAC_CONSTANT));
                    l.Add(Num("BLDG_FOOTPRINT_FRAC_CONSTANT", s.BLDG_FOOTPRINT_FRAC_CONSTANT));
                    l.Add(Int("BLDG_FUEL_MODEL_CONSTANT", s.BLDG_FUEL_MODEL_CONSTANT));
                }
            }
            else if (s.USE_BLDG_SPREAD_MODEL)
            {
                l.Add($"! Asked for, but switched off: the case has {c.BuildingLayers.Count} of the 5 building");
                l.Add("! rasters and constant parameters are not enabled.");
            }
            l.Add("/");
            l.Add("");

            //----------------------------------------------------------------------------- &SUPPRESSION
            l.Add("&SUPPRESSION");
            l.Add(Bool("ENABLE_INITIAL_ATTACK", s.ENABLE_INITIAL_ATTACK));
            if (s.ENABLE_INITIAL_ATTACK)
            {
                l.Add(Num("INITIAL_ATTACK_TIME", s.INITIAL_ATTACK_TIME));
            }

            l.Add(Bool("ENABLE_EXTENDED_ATTACK", s.ENABLE_EXTENDED_ATTACK));
            if (s.ENABLE_EXTENDED_ATTACK)
            {
                l.Add(Num("DT_EXTENDED_ATTACK", s.DT_EXTENDED_ATTACK));
                l.Add(Num("MAX_CONTAINMENT_PER_DAY", s.MAX_CONTAINMENT_PER_DAY));
                l.Add(Num("AREA_NO_CONTAINMENT_CHANGE", s.AREA_NO_CONTAINMENT_CHANGE));
                l.Add(Bool("USE_SDI_LOG_FUNCTION", s.USE_SDI_LOG_FUNCTION));

                //The index is only read inside the extended attack, and only with a raster behind it. ELMFIRE
                //fails its input check on USE_SDI with no SDI_FILENAME, so a case without one is told plainly
                //rather than being handed a run that stops at startup.
                if (WriteGatedFlag(l, c, "USE_SDI", s.USE_SDI, "sdi"))
                {
                    l.Add(Num("SDI_FACTOR", s.SDI_FACTOR));
                    l.Add(Num("B_SDI", s.B_SDI));
                }
            }
            l.Add("/");
            l.Add("");

            //----------------------------------------------------------------------------------- &SMOKE
            l.Add("&SMOKE");
            l.Add(Bool("ENABLE_SMOKE_OUTPUTS", s.ENABLE_SMOKE_OUTPUTS));
            if (s.ENABLE_SMOKE_OUTPUTS)
            {
                l.Add(Num("DT_SMOKE_OUTPUTS", s.DT_SMOKE_OUTPUTS));
                l.Add(Num("PM_EMISSION_FACTOR_FLAMING", s.PM_EMISSION_FACTOR_FLAMING));
                l.Add(Num("PM_EMISSION_FACTOR_SMOLDERING", s.PM_EMISSION_FACTOR_SMOLDERING));
                l.Add(Num("DRY_WOOD_CALORIFIC_VALUE", s.DRY_WOOD_CALORIFIC_VALUE));
                l.Add(Num("FLAMING_TIME", s.FLAMING_TIME));
                l.Add(Num("SMOLDERING_TIME", s.SMOLDERING_TIME));
            }
            l.Add("/");
            l.Add("");

            //--------------------------------------------------------------------------- &MISCELLANEOUS
            l.Add("&MISCELLANEOUS");
            l.Add(Str("SCRATCH", "./scratch"));
            if (c.HasBuildingFuelModelFile)
            {
                l.Add(Str("BUILDING_FUEL_MODEL_FILE", "building_fuel_models.csv"));
            }

            //Only when the case has one. ELMFIRE writes its own built-in table when this is unset, which is
            //correct for a standard FBFM raster; naming a file that is not there would be a failure instead.
            //The directory goes with it because that is where ELMFIRE looks, and its own default for it is the
            //fuels directory - the same folder - so this only makes the resolution explicit.
            if (c.HasFuelModelFile)
            {
                l.Add(Str("FUEL_MODEL_FILE", "fuel_models.csv"));
                l.Add(Str("MISCELLANEOUS_INPUTS_DIRECTORY", "./inputs"));
            }
            if (!string.IsNullOrEmpty(c.PathToGdal))
            {
                //No trailing separator: ELMFIRE appends PATH_SEPARATOR itself once it has a directory.
                l.Add(Str("PATH_TO_GDAL", c.PathToGdal.TrimEnd('/', '\\')));
            }
            l.Add("/");
            l.Add("");

            //--------------------------------------------------------------------------- &CALIBRATION
            //Per-pyrome tables the builder has nothing to derive, so each is written only when named and only
            //then is its switch on. ELMFIRE shuts down on a switch whose filename is unset.
            l.Add("&CALIBRATION");
            WriteNamedTable(l, c, "ADJUSTMENT_FACTORS", s.ADJUSTMENT_FACTORS_BY_PYROME,
                s.ADJUSTMENT_FACTORS_FILENAME);
            WriteNamedTable(l, c, "CALIBRATION_CONSTANTS", s.CALIBRATION_CONSTANTS_BY_PYROME,
                s.CALIBRATION_CONSTANTS_FILENAME);
            if (WriteNamedTable(l, c, "DURATION_PDF", s.DURATION_PDF_BY_PYROME, s.DURATION_PDF_FILENAME))
            {
                l.Add(Int("DURATION_MAX_DAYS", s.DURATION_MAX_DAYS));
            }

            //Only read together: the per-pyrome adjustment factors are indexed by the pyrome each cell is in.
            if (s.ADJUSTMENT_FACTORS_BY_PYROME && !s.USE_PYROMES)
            {
                l.Add("! ADJUSTMENT_FACTORS_BY_PYROME also needs USE_PYROMES and a pyromes raster, without");
                l.Add("! which there is nothing to index the table by.");
            }
            l.Add("/");

            return l.ToArray();
        }

        /// <summary>
        /// How many weather bands to read: what the scenario asked for, or what the case holds.
        /// </summary>
        /// <remarks>
        /// Clamped to what exists, because asking for more bands than the rasters have fails the run outright
        /// while asking for fewer fails silently - ELMFIRE reuses band one and produces a plausible fire of
        /// the wrong day. Never less than one, since zero bands is not a runnable case.
        /// </remarks>
        private static int ResolveMeteorologyBands(ElmfireNamelistInput s, CaseFacts c)
        {
            int requested = s.MeteorologyBands > 0 ? s.MeteorologyBands : c.AvailableMeteorologyBands;
            if (requested <= 0) requested = 1;

            if (c.AvailableMeteorologyBands > 0 && requested > c.AvailableMeteorologyBands)
            {
                requested = c.AvailableMeteorologyBands;
            }

            return requested;
        }

        /// <summary>ELMFIRE spells these with hyphens; C# enum members cannot.</summary>
        private static string Hyphenated(Enum value)
        {
            return value.ToString().Replace('_', '-');
        }

        private const int KeyWidth = -37;

        private static string Str(string key, string value) => $"{key,KeyWidth} = '{value}'";

        private static string Bool(string key, bool value) => $"{key,KeyWidth} = {(value ? ".TRUE." : ".FALSE.")}";

        /// <summary>
        /// A switch that needs a raster, written on only when the case has one, with the filename beside it.
        /// </summary>
        /// <remarks>
        /// ELMFIRE checks each of these filenames is set and shuts down when it is not, so writing the switch on
        /// for a case with no such layer produces a run that cannot start. Writing it off instead, and saying
        /// why, is a setting that visibly did not apply rather than a case that fails at startup for a reason
        /// several groups away from the switch that caused it.
        /// </remarks>
        private static void WriteGatedLayer(List<string> l, CaseFacts c, string flag, bool asked,
                                            string filenameKey, string stem)
        {
            bool usable = WriteGatedFlag(l, c, flag, asked, stem);
            if (usable)
            {
                l.Add(Str(filenameKey, stem));
            }
        }

        /// <summary>
        /// The same gate for a switch whose filename belongs to a different namelist group.
        /// </summary>
        /// <remarks>
        /// Fortran refuses a key that is not a member of the group it appears in, so <c>USE_SDI</c>,
        /// <c>USE_PYROMES</c> and <c>USE_ERC</c> cannot be written beside the filenames they depend on — those
        /// are all <c>&amp;INPUTS</c> keys and the switches are not. The filename is written in
        /// <c>&amp;INPUTS</c> whenever the case has the layer; this decides the switch.
        /// </remarks>
        private static bool WriteGatedFlag(List<string> l, CaseFacts c, string flag, bool asked, string stem)
        {
            bool usable = asked && c.AvailableStems.Contains(stem);
            l.Add(Bool(flag, usable));
            if (!usable && asked)
            {
                l.Add($"! {flag} was asked for but the case has no {stem}.tif, so it is left off.");
            }
            return usable;
        }

        /// <summary>
        /// A <c>*_BY_PYROME</c> switch and the table it reads, written as a pair or not at all.
        /// </summary>
        /// <remarks>
        /// The switch on with no filename is a startup failure, and a filename with the switch off is a table
        /// nobody reads. The file has to be there as well as named: ELMFIRE checks only that the name is set,
        /// then dies on the read with a bare <c>severe (29): file not found, unit 100</c> that names neither the
        /// table nor the switch. Returns whether the table is actually in use, so settings that only that table
        /// needs can follow it.
        /// </remarks>
        private static bool WriteNamedTable(List<string> l, CaseFacts c, string prefix, bool asked, string filename)
        {
            string name = filename == null ? string.Empty : filename.Trim();
            bool present = name.Length > 0 && c.AvailableInputFiles.Contains(name);

            l.Add(Bool(prefix + "_BY_PYROME", asked && present));
            if (asked && present)
            {
                l.Add(Str(prefix + "_FILENAME", name));
                return true;
            }

            if (asked && name.Length == 0)
            {
                l.Add($"! {prefix}_BY_PYROME was asked for with no {prefix}_FILENAME, so it is left off.");
            }
            else if (asked)
            {
                l.Add($"! {prefix}_BY_PYROME was asked for but '{name}' is not in the case's inputs folder,");
                l.Add("! so it is left off. ELMFIRE would not report which table was missing.");
            }
            return false;
        }

        /// <summary>
        /// A fluctuation intensity range, written only when both ends of it ask for something.
        /// </summary>
        /// <remarks>
        /// ELMFIRE has no switch for these: it decides to sample by finding both the minimum and the maximum
        /// above zero. So one end set and the other left at -1 is not half a setting, it is no setting, and the
        /// fixed intensity in <c>&amp;SIMULATOR</c> stands instead. Written as a pair or not at all, with the
        /// half-set case said out loud.
        /// </remarks>
        private static void WriteFluctuationRange(List<string> l, string key, double min, double max)
        {
            if (min > 0.0 && max > 0.0)
            {
                l.Add(Num(key + "_MIN", min));
                l.Add(Num(key + "_MAX", max));
                return;
            }

            if (min > 0.0 || max > 0.0)
            {
                l.Add($"! {key}_MIN/_MAX: only one end is set, so ELMFIRE will not sample it and the fixed");
                l.Add($"! value in &SIMULATOR is used. Set both above zero to sample.");
            }
        }

        /// <summary>
        /// The raster perturbation variations, as parallel subscripted lists.
        /// </summary>
        /// <remarks>
        /// Written as <c>KEY(1:n) = a, b, c</c> rather than one element per line so the file reads back into the
        /// editor: a per-line subscripted assignment would be parsed as the whole list each time and only the
        /// last one would survive.
        ///
        /// A variation is written only if all four of its descriptive entries are present, because ELMFIRE
        /// validates each against its own list and stops on anything it does not recognise — including the
        /// <c>'null'</c> it fills a short list with. Which limits are read depends on the distribution, so the
        /// unread pair is left out.
        /// </remarks>
        private static void WritePerturbations(List<string> l, ElmfireNamelistInput s, int bands)
        {
            int n = s.RASTER_TO_PERTURB == null ? 0 : s.RASTER_TO_PERTURB.Length;
            n = System.Math.Min(n, Count(s.SPATIAL_PERTURBATION));
            n = System.Math.Min(n, Count(s.TEMPORAL_PERTURBATION));
            n = System.Math.Min(n, Count(s.PDF_TYPE));

            if (n == 0)
            {
                l.Add(Int("NUM_RASTERS_TO_PERTURB", 0));
                return;
            }

            if (n < Count(s.RASTER_TO_PERTURB))
            {
                l.Add($"! {Count(s.RASTER_TO_PERTURB)} rasters were named to perturb but only {n} of them have a");
                l.Add("! spatial mode, a temporal mode and a distribution, so the rest are left out.");
            }

            l.Add("");
            l.Add($"! {n} raster perturbation(s), read across these lists in order.");
            l.Add(Int("NUM_RASTERS_TO_PERTURB", n));
            l.Add(StrList("RASTER_TO_PERTURB", s.RASTER_TO_PERTURB, n));
            l.Add(StrList("SPATIAL_PERTURBATION", s.SPATIAL_PERTURBATION, n));
            l.Add(StrList("TEMPORAL_PERTURBATION", s.TEMPORAL_PERTURBATION, n));
            l.Add(StrList("PDF_TYPE", s.PDF_TYPE, n));

            bool anyUniform = false;
            bool anyMoments = false;
            for (int i = 0; i < n; ++i)
            {
                if (string.Equals(s.PDF_TYPE[i], "UNIFORM", StringComparison.OrdinalIgnoreCase)) anyUniform = true;
                else anyMoments = true;
            }

            if (anyUniform)
            {
                l.Add(NumList("PDF_LOWER_LIMIT", s.PDF_LOWER_LIMIT, n));
                l.Add(NumList("PDF_UPPER_LIMIT", s.PDF_UPPER_LIMIT, n));
            }
            if (anyMoments)
            {
                l.Add(NumList("PDF_MEAN", s.PDF_MEAN, n));
                l.Add(NumList("PDF_SIGMA", s.PDF_SIGMA, n));
            }

            //A dynamic global perturbation costs one sampled parameter per weather band rather than one per run,
            //which is the difference between a handful and hundreds.
            int dynamic = 0;
            for (int i = 0; i < n; ++i)
            {
                bool pixel = string.Equals(s.SPATIAL_PERTURBATION[i], "PIXEL", StringComparison.OrdinalIgnoreCase);
                bool isDynamic = string.Equals(s.TEMPORAL_PERTURBATION[i], "DYNAMIC", StringComparison.OrdinalIgnoreCase);
                if (!pixel && isDynamic) ++dynamic;
            }
            if (dynamic > 0)
            {
                l.Add($"! {dynamic} of them are global and dynamic, so each costs {bands} sampled parameters -");
                l.Add($"! one per weather band - rather than one.");
            }
        }

        private static int Count(string[] a) => a == null ? 0 : a.Length;

        /// <summary>A subscripted list of quoted strings, truncated or padded to <paramref name="n"/>.</summary>
        private static string StrList(string key, string[] values, int n)
        {
            var parts = new List<string>(n);
            for (int i = 0; i < n; ++i)
            {
                string v = values != null && i < values.Length ? values[i] : string.Empty;
                parts.Add($"'{v}'");
            }
            return $"{key + "(1:" + n + ")",KeyWidth} = {string.Join(", ", parts)}";
        }

        /// <summary>A subscripted list of reals, zero-filled where the caller supplied fewer.</summary>
        private static string NumList(string key, double[] values, int n)
        {
            var parts = new List<string>(n);
            for (int i = 0; i < n; ++i)
            {
                double v = values != null && i < values.Length ? values[i] : 0.0;
                parts.Add(v.ToString("0.#####", CultureInfo.InvariantCulture));
            }
            return $"{key + "(1:" + n + ")",KeyWidth} = {string.Join(", ", parts)}";
        }

        /// <summary>The number of fuel models ELMFIRE dimensions its per-fuel spotting arrays over, 0 to 303.</summary>
        private const int FuelModelSlots = 304;

        /// <summary>
        /// One value applied to every fuel model, for the spotting settings ELMFIRE declares as arrays.
        /// </summary>
        /// <remarks>
        /// Written subscripted, with Fortran's repeat count, because an unsubscripted assignment to an array in
        /// a namelist sets only its first element. These arrays start at index 0, so the plain form would set
        /// fuel model 0 — which nothing burns — and leave every real fuel model at ELMFIRE's default. Silent,
        /// and indistinguishable from the setting having no effect.
        /// </remarks>
        private static string PerFuel(string key, double value)
        {
            string subscripted = $"{key}(0:{FuelModelSlots - 1})";
            return $"{subscripted,KeyWidth} = {FuelModelSlots}*{value.ToString("0.###", CultureInfo.InvariantCulture)}";
        }

        /// <summary>
        /// An array key, or nothing when it is empty.
        /// </summary>
        /// <remarks>
        /// Omitted rather than written empty: a namelist key with no value on the right is a parse error in
        /// Fortran, and ELMFIRE's own defaults are already correct for an array nobody set.
        /// </remarks>
        private static void WriteArray(List<string> l, string key, double[] values)
        {
            if (values == null || values.Length == 0) return;

            var parts = new List<string>(values.Length);
            foreach (double v in values) parts.Add(v.ToString("0.###", CultureInfo.InvariantCulture));
            l.Add($"{key,KeyWidth} = {string.Join(", ", parts)}");
        }

        private static void WriteArray(List<string> l, string key, int[] values)
        {
            if (values == null || values.Length == 0) return;

            var parts = new List<string>(values.Length);
            foreach (int v in values) parts.Add(v.ToString(CultureInfo.InvariantCulture));
            l.Add($"{key,KeyWidth} = {string.Join(", ", parts)}");
        }

        /// <summary>
        /// An exposure dump, written on only when the INPUTS flag that supplies its raster is also on.
        /// </summary>
        /// <remarks>
        /// Dumping an exposure raster that was never populated writes zeros, and a zero exposure raster reads
        /// as "nothing at risk" rather than as "this was not measured" — the wrong answer rather than a missing
        /// one, which is why this is refused rather than passed through.
        /// </remarks>
        private static void WriteExposureDump(List<string> l, string key, bool wanted, bool sourceOn, string sourceKey)
        {
            l.Add(Bool(key, wanted && sourceOn));

            if (wanted && !sourceOn)
            {
                l.Add($"! {key} needs {sourceKey}, which is off, so it would dump zeros. Left off.");
            }
        }

        private static string Int(string key, int value)
            => $"{key,KeyWidth} = {value.ToString(CultureInfo.InvariantCulture)}";

        private static string Num(string key, double value)
        {
            //"R" would give 1E-3 as 0.001 and 999999 as 999999, both fine, but it also gives things like
            //0.66700000000000004. G17 is round-trippable without that.
            string text = value.ToString("G9", CultureInfo.InvariantCulture);

            //Fortran reads an integer literal into a REAL happily, but a namelist that says DTDUMP = 3600
            //where every other case says 3600.0 is harder to diff against a hand-written one.
            if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0)
            {
                text += ".0";
            }

            return $"{key,KeyWidth} = {text}";
        }
    }
}
