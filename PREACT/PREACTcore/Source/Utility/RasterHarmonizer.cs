using System;
using System.Collections.Generic;
using System.Globalization;
using OSGeo.GDAL;

namespace PREACT.Utility
{
    /// <summary>
    /// GDAL warp (reproject/clip/resample) onto the domain's master grid (docs/probabilistic-
    /// trigger-convergence.md, "Reprojection / harmonization step"), so every ELMFIRE input
    /// raster ends up on one shared grid. Extends the existing single-CRS warp used by
    /// <c>WorldPopDownloader.ReprojectToUTM</c> with an exact extent/pixel-size snap (<c>-te</c>/
    /// <c>-ts</c>) rather than just a target CRS (<c>-t_srs</c>).
    /// </summary>
    public static class RasterHarmonizer
    {
        /// <summary>
        /// Warps a freshly-downloaded (typically WGS84) DEM into the local UTM zone picked from
        /// the domain's center lat/lon, establishing the master grid every other input will be
        /// snapped to. Pass <paramref name="cellSize"/> to also resample to a specific
        /// resolution; omit it to keep the source's native resolution (reprojected).
        /// </summary>
        public static MasterGrid BuildUtmMasterGrid(string sourcePath, string destPath, double centerLatitude, double centerLongitude, double? cellSize = null)
        {
            string epsg = UtmUtility.GetUtmEpsg(centerLatitude, centerLongitude);

            var args = new List<string> { "-t_srs", epsg, "-r", "bilinear", "-overwrite" };
            if (cellSize.HasValue)
            {
                string cs = cellSize.Value.ToString(CultureInfo.InvariantCulture);
                args.Add("-tr");
                args.Add(cs);
                args.Add(cs);
            }

            Warp(sourcePath, destPath, args.ToArray());
            return MasterGrid.FromRasterFile(destPath);
        }

        /// <summary>
        /// Warps any other input raster (fuel, canopy, wind, moisture, ...) onto the exact
        /// master grid: same CRS, same extent, same pixel count, so it lines up cell-for-cell
        /// with the DEM. Use <c>"near"</c> for categorical rasters (e.g. fuel model) and
        /// <c>"bilinear"</c> (the default) for continuous ones (e.g. moisture, wind).
        /// </summary>
        public static void WarpToGrid(string sourcePath, string destPath, MasterGrid grid, string resampleMethod = "bilinear")
        {
            if (string.IsNullOrEmpty(grid.Epsg))
            {
                throw new InvalidOperationException("Master grid has no resolvable EPSG; cannot warp onto it.");
            }

            string[] args =
            {
                "-t_srs", grid.Epsg,
                "-te", D(grid.XMin), D(grid.YMin), D(grid.XMax), D(grid.YMax),
                "-ts", grid.Header.Ncols.ToString(CultureInfo.InvariantCulture), grid.Header.Nrows.ToString(CultureInfo.InvariantCulture),
                "-r", resampleMethod,
                "-overwrite"
            };

            Warp(sourcePath, destPath, args);
        }

        private static string D(double v) => v.ToString(CultureInfo.InvariantCulture);

        private static void Warp(string sourcePath, string destPath, string[] args)
        {
            Gdal.AllRegister();

            Dataset src = Gdal.Open(sourcePath, Access.GA_ReadOnly);
            if (src == null)
            {
                throw new Exception("Could not open input raster: " + sourcePath);
            }

            var warpOptions = new GDALWarpAppOptions(args);
            Gdal.GDALProgressFuncDelegate progress = (pct, msg, data) => 1;

            Dataset dst = Gdal.Warp(destPath, new Dataset[] { src }, warpOptions, progress, "");
            if (dst == null)
            {
                src.Dispose();
                throw new Exception($"Warp failed: {sourcePath} -> {destPath}");
            }

            dst.FlushCache();
            dst.Dispose();
            src.Dispose();
        }
    }
}
