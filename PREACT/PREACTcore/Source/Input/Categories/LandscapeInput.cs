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
    /// The landscape as a set of GeoTIFF bands, as an alternative to a FARSITE <c>.lcp</c>.
    ///
    /// A section of its own, rather than more keys under the wildfire module, for two reasons. The
    /// terrain is not the fire module's property - the elevation is what a trigger boundary needs to
    /// correct spread for slope, what a scenario needs to place anything painted on a cell grid, and
    /// what the map is drawn against - and a scenario often has terrain long before it has fuels.
    ///
    /// Every band is optional. The elevation is the one worth having on its own: it is available for
    /// anywhere on Earth from a DEM, whereas fuels and canopy are only distributed for some countries,
    /// and slope and aspect are derived from it when they are not supplied. What is present decides
    /// what can be done - a fire cannot be spread without fuels, but an area can be painted on any grid
    /// - so nothing here is required and a missing band is not an error.
    ///
    /// The order of the bands, when they are given as one multiband file, is the order a LANDFIRE
    /// landscape uses: elevation, slope, aspect, fuel model, canopy cover, canopy height, canopy base
    /// height, canopy bulk density, and then duff and coarse woody if present. That is also the order
    /// the classic .lcp stores them in, which is why the two can share a reader.
    /// </summary>
    [System.Serializable]
    public class LandscapeInput
    {
        /// <summary>
        /// A multiband GeoTIFF holding the bands in LANDFIRE order, or a FARSITE .lcp. When this is set
        /// the individual bands below are ignored, since it already defines all of them.
        /// </summary>
        public string LandscapeFile = string.Empty;

        /// <summary>
        /// Terrain height in metres. A DEM, from anywhere - and the only band that always can be.
        /// </summary>
        public string ElevationFile = string.Empty;

        /// <summary>Steepness in degrees. Computed from the elevation when not given.</summary>
        public string SlopeFile = string.Empty;

        /// <summary>Downhill direction in degrees clockwise from north. Computed from the elevation when not given.</summary>
        public string AspectFile = string.Empty;

        /// <summary>Fire behaviour fuel model number per cell, in whichever set the scenario declares.</summary>
        public string FuelModelFile = string.Empty;

        /// <summary>Canopy cover as a percentage.</summary>
        public string CanopyCoverFile = string.Empty;

        //Crown fuels. All three are needed for a crown fire to be modelled, so a partial set is
        //reported rather than half-used.
        public string CanopyHeightFile = string.Empty;
        public string CanopyBaseHeightFile = string.Empty;
        public string CanopyBulkDensityFile = string.Empty;

        public LandscapeInput()
        {

        }

        public void Parse(string[] inputLines, int startIndex, string rootFolder, out bool success)
        {
            //Nothing here is critical: a scenario with no landscape at all is a valid thing to be
            //editing, and what is missing limits what can be done rather than failing the load.
            success = true;

            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);

            ReadOptionalFile(inputToParse, nameof(LandscapeFile), ref LandscapeFile, rootFolder);
            ReadOptionalFile(inputToParse, nameof(ElevationFile), ref ElevationFile, rootFolder);
            ReadOptionalFile(inputToParse, nameof(SlopeFile), ref SlopeFile, rootFolder);
            ReadOptionalFile(inputToParse, nameof(AspectFile), ref AspectFile, rootFolder);
            ReadOptionalFile(inputToParse, nameof(FuelModelFile), ref FuelModelFile, rootFolder);
            ReadOptionalFile(inputToParse, nameof(CanopyCoverFile), ref CanopyCoverFile, rootFolder);
            ReadOptionalFile(inputToParse, nameof(CanopyHeightFile), ref CanopyHeightFile, rootFolder);
            ReadOptionalFile(inputToParse, nameof(CanopyBaseHeightFile), ref CanopyBaseHeightFile, rootFolder);
            ReadOptionalFile(inputToParse, nameof(CanopyBulkDensityFile), ref CanopyBulkDensityFile, rootFolder);

            //A crown fire needs all three crown bands. Two of them is a mistake worth naming, since the
            //alternative is a run that quietly models no crown fire at all.
            int crownBands = (string.IsNullOrEmpty(CanopyHeightFile) ? 0 : 1)
                             + (string.IsNullOrEmpty(CanopyBaseHeightFile) ? 0 : 1)
                             + (string.IsNullOrEmpty(CanopyBulkDensityFile) ? 0 : 1);
            if (crownBands > 0 && crownBands < 3)
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "Only " + crownBands + " of the three crown fuel bands (canopy height, base height, bulk density) "
                    + "are given. All three are needed together, so none of them will be used.");
            }
        }

        /// <summary>
        /// Reads a band's path if the key is there, and says so if the file it names is not. Absence of
        /// the key is silent: these are optional, and reporting each one that was not asked for would
        /// bury the ones that were.
        /// </summary>
        private static void ReadOptionalFile(Dictionary<string, string> inputToParse, string key, ref string field, string rootFolder)
        {
            if (!inputToParse.TryGetValue(key, out string userInput) || string.IsNullOrWhiteSpace(userInput))
            {
                return;
            }

            field = userInput;
            //By reference, so a band that has been moved into one of the scenario's subfolders is found
            //and the path corrected in place rather than merely reported.
            PREACTInput.CheckIfFileExist(key, ref field, rootFolder, out bool exists);
            if (!exists)
            {
                //Kept rather than cleared, so the path stays visible and editable instead of vanishing
                //from the scenario when it is saved again.
                Engine.Message(null, Engine.LogType.Warning, key + " points at a file that is not there: " + userInput);
            }
        }

        /// <summary>
        /// The bands in the order a landscape stores them, with an empty string where one is absent.
        /// </summary>
        public string[] GetOrderedBandFiles()
        {
            return new string[]
            {
                ElevationFile,
                SlopeFile,
                AspectFile,
                FuelModelFile,
                CanopyCoverFile,
                CanopyHeightFile,
                CanopyBaseHeightFile,
                CanopyBulkDensityFile
            };
        }

        /// <summary>
        /// Whether anything here describes a landscape at all.
        /// </summary>
        public bool HaveAnything()
        {
            if (!string.IsNullOrEmpty(LandscapeFile))
            {
                return true;
            }

            foreach (string file in GetOrderedBandFiles())
            {
                if (!string.IsNullOrEmpty(file))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The band that georeferences the landscape: whichever one is actually present. Any of them
        /// will do - they are all on the same grid - and this is what the simulation's UTM zone and the
        /// paintable cell grid are taken from when there is no fire data to take them from.
        /// </summary>
        public string GetReferenceFile()
        {
            if (!string.IsNullOrEmpty(LandscapeFile))
            {
                return LandscapeFile;
            }

            foreach (string file in GetOrderedBandFiles())
            {
                if (!string.IsNullOrEmpty(file))
                {
                    return file;
                }
            }

            return string.Empty;
        }
    }
}
