//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

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

        /// <summary>How long ELMFIRE simulates, in seconds. Independent of the evacuation's own end time.</summary>
        public double SimulationTstopSeconds = 28800.0;

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

        public ElmfireInput()
        {
        }

        public static ElmfireInput Parse(string[] inputLines, int startIndex, string rootFolder, out bool success)
        {
            //Nothing here is critical. Every value has a usable default, and what actually has to exist -
            //the executable, the case, its rasters - cannot be judged from the file: it is checked when the
            //module is created, where the paths have been resolved and the reason can be specific.
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

            ReadDouble(inputToParse, nameof(SimulationTstopSeconds), ref newInput.SimulationTstopSeconds);
            ReadDouble(inputToParse, nameof(CellSizeMetres), ref newInput.CellSizeMetres);
            ReadDouble(inputToParse, nameof(PaddingMetres), ref newInput.PaddingMetres);
            ReadBool(inputToParse, nameof(ReuseExistingOutput), ref newInput.ReuseExistingOutput);
            ReadBool(inputToParse, nameof(BuildCase), ref newInput.BuildCase);
            ReadBool(inputToParse, nameof(RebuildExistingLayers), ref newInput.RebuildExistingLayers);

            return newInput;
        }

        private static void ReadDouble(Dictionary<string, string> input, string key, ref double field)
        {
            if (input.TryGetValue(key, out string userInput)
                && double.TryParse(userInput, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            {
                field = parsed;
            }
        }

        private static void ReadBool(Dictionary<string, string> input, string key, ref bool field)
        {
            if (input.TryGetValue(key, out string userInput) && bool.TryParse(userInput, out bool parsed))
            {
                field = parsed;
            }
        }
    }
}
