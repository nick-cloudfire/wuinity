using System.Collections.Generic;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// The FIRE-RES pan-European canopy dataset as a source of ELMFIRE's four canopy layers.
    /// </summary>
    /// <remarks>
    /// FIRE-RES publishes LANDFIRE-equivalent layers for Europe as a handful of continent-sized GeoTIFFs
    /// rather than an API that cuts a case for you, so there is nothing to download per scenario: the whole
    /// continent sits on disk and each case is a window onto it. That is exactly what
    /// <see cref="RasterHarmonizer.WarpToGrid"/> already does — <c>-te</c> in the target CRS plus <c>-ts</c>
    /// clips, reprojects and resamples in one pass, and GDAL reads only the window it needs, so a 50651 x 42485
    /// source costs no more than the domain cut out of it.
    ///
    /// <b>The units are the thing to get right.</b> These rasters hold <b>real</b> units — canopy height and
    /// base height in metres, bulk density in kg/m³, cover in percent. LANDFIRE ships the same quantities as
    /// scaled integers (height in decimetres, density x100), and <b>ELMFIRE defaults to expecting LANDFIRE's
    /// scaling</b>: <c>CH_TIMES_10</c>, <c>CBH_TIMES_10</c> and <c>CBD_TIMES_100</c> are all <c>.TRUE.</c> in
    /// its own namelist defaults. Feeding it FIRE-RES data with those defaults divides the canopy by 10 and the
    /// bulk density by 100 — a 44 m forest becomes 4.4 m and a bulk density of 0.05 becomes 0.0005 kg/m³, so
    /// crown fire essentially never initiates and nothing anywhere says why. A case built from this dataset
    /// therefore has those three flags forced off rather than left to the scenario.
    ///
    /// Terrain is deliberately <b>not</b> taken from here even though the dataset carries elevation, slope and
    /// aspect: the case's grid of record is its DEM, that DEM comes from OpenTopography at 30 m, and slope and
    /// aspect are recomputed from it after harmonisation. Mixing in a second, coarser terrain source would put
    /// the slope 100 m away from the elevation it belongs to. Fuel is not taken from here either — the fuel
    /// model stays the user's own raster.
    /// </remarks>
    public static class FireResCanopy
    {
        /// <summary>
        /// ELMFIRE's canopy stems paired with the FIRE-RES file that supplies each.
        /// </summary>
        /// <remarks>
        /// All four or none is not required — a dataset missing one file still contributes the other three, and
        /// the builder fills what is absent with zero as it always did. The names are FIRE-RES's own.
        /// </remarks>
        private static readonly (string Stem, string FileName)[] Layers =
        {
            ("cc",  "panEu_canopyCover.tif"),
            ("ch",  "panEu_canopyHeight.tif"),
            ("cbh", "panEu_cbh.tif"),
            ("cbd", "panEu_cbd.tif"),
        };

        /// <summary>Files the dataset carries that this deliberately ignores, and why. For reporting.</summary>
        public static readonly string[] IgnoredFiles =
        {
            "panEu_elevation.tif", "panEu_slope.tif", "panEu_aspect.tif", //terrain comes from the case's DEM
            "panEu_fuel.tif",                                             //the fuel model is the user's own
            "panEu_biomass.tif",                                          //ELMFIRE has no input for it
        };

        /// <summary>
        /// The canopy layers this dataset can supply, as (ELMFIRE stem, source path).
        /// </summary>
        /// <returns>False when the folder is unset or holds none of the four files.</returns>
        public static bool TryResolve(string datasetFolder, out List<KeyValuePair<string, string>> layers)
        {
            layers = new List<KeyValuePair<string, string>>();

            if (string.IsNullOrWhiteSpace(datasetFolder) || !Directory.Exists(datasetFolder))
            {
                return false;
            }

            foreach ((string stem, string fileName) in Layers)
            {
                string path = Path.Combine(datasetFolder, fileName);
                if (File.Exists(path))
                {
                    layers.Add(new KeyValuePair<string, string>(stem, path));
                }
            }

            return layers.Count > 0;
        }

        /// <summary>The four file names, for a message naming what a folder should contain.</summary>
        public static string ExpectedFileNames()
        {
            var names = new List<string>();
            foreach ((string _, string fileName) in Layers) names.Add(fileName);
            return string.Join(", ", names);
        }
    }
}
