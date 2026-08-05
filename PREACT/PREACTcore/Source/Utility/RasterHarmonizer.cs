using System;
using System.Collections.Generic;
using System.Globalization;
using OSGeo.GDAL;
using OSGeo.OSR;

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
        /// Warps a DEM into the local UTM zone <b>and clips it to a lat/lon bounding box</b>,
        /// establishing a master grid whose extent is the domain that was asked for rather than
        /// whatever the source DEM happened to cover. Necessary whenever the DEM is not a
        /// download cut to the domain — a local or cached DEM is usually much larger, and without
        /// the clip every downstream raster (and ELMFIRE's whole computational domain, which it
        /// infers from the DEM) would silently inherit the wrong extent.
        ///
        /// The corners are reprojected rather than assumed: a lat/lon box is not a rectangle in
        /// UTM, so all four are transformed and the bounding box of the result is used.
        /// </summary>
        public static MasterGrid BuildUtmMasterGrid(
            string sourcePath, string destPath,
            double southLatitude, double westLongitude, double northLatitude, double eastLongitude,
            double? cellSize = null, int targetEpsgCode = 0)
        {
            double centreLat = 0.5 * (southLatitude + northLatitude);
            double centreLon = 0.5 * (westLongitude + eastLongitude);

            //The zone is named by the caller when it has one to name, and only otherwise derived from the
            //box. It has to be: a simulation whose domain straddles a zone boundary measures in the zone
            //its other data is in, and warping a DEM into the zone recomputed from the centre would put
            //the DEM in one zone and the fire in the next - the half-a-million-metre disagreement this
            //whole path exists to avoid.
            string epsg = targetEpsgCode != 0
                ? "EPSG:" + targetEpsgCode
                : UtmUtility.GetUtmEpsg(centreLat, centreLon);

            var wgs84 = new SpatialReference("");
            wgs84.ImportFromEPSG(4326);
            //lat/lon in that order, matching how the corners are passed in below, instead of
            //EPSG:4326's official axis order.
            wgs84.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            var utm = new SpatialReference("");
            utm.ImportFromEPSG(int.Parse(epsg.Replace("EPSG:", "")));
            utm.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            var transform = new CoordinateTransformation(wgs84, utm);

            double xMin = double.MaxValue, yMin = double.MaxValue;
            double xMax = double.MinValue, yMax = double.MinValue;
            foreach ((double lon, double lat) in new[]
                     { (westLongitude, southLatitude), (eastLongitude, southLatitude),
                       (westLongitude, northLatitude), (eastLongitude, northLatitude) })
            {
                double[] p = { lon, lat, 0 };
                transform.TransformPoint(p);
                xMin = System.Math.Min(xMin, p[0]); xMax = System.Math.Max(xMax, p[0]);
                yMin = System.Math.Min(yMin, p[1]); yMax = System.Math.Max(yMax, p[1]);
            }

            transform.Dispose();
            wgs84.Dispose();
            utm.Dispose();

            var args = new List<string>
            {
                "-t_srs", epsg,
                "-te", D(xMin), D(yMin), D(xMax), D(yMax),
                "-r", "bilinear", "-overwrite"
            };

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

            //Before the handle is closed, while it is still the writable one the warp produced.
            ForceAreaPixelConvention(dst, destPath);

            dst.FlushCache();
            dst.Dispose();
            src.Dispose();
        }

        /// <summary>
        /// Stamps <c>AREA_OR_POINT=Area</c> on a warp output.
        /// </summary>
        /// <remarks>
        /// A raster tagged <c>AREA_OR_POINT=Point</c> declares that its geotransform names cell <b>centres</b>
        /// rather than corners, which is a half-cell shift in both axes — 15 m on a 30 m grid. gdalwarp honours
        /// the tag when reading, so the warp itself is correct, but it <b>copies the tag to the output</b>: the
        /// shift comes back the next time anything reads the harmonized file, and every reader here treats the
        /// geotransform as corners.
        ///
        /// This is not hypothetical. The reference Mati case arrived with a <c>RasterPixelIsPoint</c> DEM and
        /// its layers half a cell out of register with each other, and clearing the tag by hand was one of the
        /// steps that had to be repeated every time a raster was re-warped. Doing it in the one function every
        /// warp goes through is what stops it being a step anyone can forget.
        ///
        /// Setting the metadata rather than adjusting the geotransform: the warp already placed the pixels
        /// correctly, so the only thing wrong is the label describing what the numbers mean.
        /// </remarks>
        private static void ForceAreaPixelConvention(Dataset dataset, string path)
        {
            try
            {
                string current = dataset.GetMetadataItem("AREA_OR_POINT", string.Empty);

                //Already Area, or unset - which GDAL reads as Area anyway. Writing it regardless would be
                //harmless, but leaving an untouched file untouched keeps the "did this change anything?"
                //question answerable from the file's own timestamp.
                if (string.IsNullOrEmpty(current)
                    || string.Equals(current, "Area", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                dataset.SetMetadataItem("AREA_OR_POINT", "Area", string.Empty);
            }
            catch (Exception e)
            {
                //Reported rather than thrown: the warp itself succeeded and its pixels are in the right place,
                //so failing the build over the label would discard good work. Half-cell misregistration in a
                //later read is what this costs, and the case validator compares origins to a tenth of a cell,
                //so it is caught there rather than passing unnoticed.
                Engine.Message(null, Engine.LogType.Warning,
                    $"Could not set AREA_OR_POINT=Area on {path} ({e.Message}). If it was tagged Point, "
                    + "readers will see it half a cell out of position.");
            }
        }
    }
}
