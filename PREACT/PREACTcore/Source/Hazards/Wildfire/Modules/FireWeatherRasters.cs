//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

namespace PREACT.Wildfire
{
    /// <summary>
    /// The weather a fire was computed against, as the fire module knows it - what the trigger boundary should
    /// use so that k-PERIL and the fire cannot disagree about the wind.
    /// </summary>
    /// <remarks>
    /// A runtime object, never part of the scenario (contract C4). It used to be written into
    /// <c>[kPERIL] WindSpeedFile</c> and <c>[AscImport]</c> while the run was set up, so a File &gt; Save after a
    /// run pinned one run's derived paths into the scenario, and the next case's fire used the old case's wind.
    ///
    /// Paths are absolute. Anything left null is not known to the fire module, and the trigger boundary then
    /// takes it from the scenario's <c>[kPERIL]</c> section.
    /// </remarks>
    public class FireWeatherRasters
    {
        /// <summary>
        /// Midflame wind speed in <b>ft/min</b> on the fire grid, from ELMFIRE's <c>mfws_*.tif</c>: valid where
        /// the fire spread, nodata elsewhere. This is what k-PERIL's length-to-breadth ratio is defined for.
        /// </summary>
        public string MidflameWindSpeedFile;

        /// <summary>10 m wind speed in mi/h, one band per weather interval. Only a fallback for k-PERIL.</summary>
        public string WindSpeedFile;

        /// <summary>Wind direction in degrees (the direction it blows from), one band per weather interval.</summary>
        public string WindDirectionFile;

        /// <summary>Seconds each band of the wind rasters covers - the fire's DT_METEOROLOGY.</summary>
        public double SecondsPerBand = 3600.0;

        /// <summary>The band the fire started in (METEOROLOGY_BAND_START): arrival time 0 falls in this band.</summary>
        public int StartBand = 1;

        /// <summary>The case's painted WUI area on the fire grid, when the case carries one.</summary>
        public string WuiAreaFile;

        /// <summary>
        /// Whether the fire module computed the fire against exactly these rasters (ELMFIRE run by this
        /// simulation). Then they win over anything the scenario's <c>[kPERIL]</c> section names, because two wind
        /// fields for one fire change the boundary without changing anything visible.
        /// </summary>
        public bool Authoritative;

        /// <summary>Where these came from, for the log.</summary>
        public string Origin = string.Empty;
    }
}
