using OSGeo.GDAL;
using OSGeo.OSR;

namespace PREACT.Utility
{
    /// <summary>
    /// Writes uniform-value GeoTIFFs on a <see cref="MasterGrid"/> — ELMFIRE's real inputs
    /// (confirmed against <c>WildfireAV/pipeline/makePhiAndAdjFiles.py</c> and
    /// <c>wn_to_geotiff.py</c>) are always GeoTIFF, never ESRI ASCII grid: static per-case
    /// rasters like <c>phi.tif</c>/<c>adj.tif</c> are single-band, while weather inputs
    /// (<c>ws.tif</c>/<c>wd.tif</c>/<c>m1.tif</c>/...) are multi-band time series with one band
    /// per <c>DT_METEOROLOGY</c> step.
    /// </summary>
    public static class GeoTiffRasterWriter
    {
        /// <summary>Writes a single-band constant-value GeoTIFF (e.g. phi.tif/adj.tif: always 1.0).</summary>
        public static void WriteConstant(MasterGrid grid, float value, string outputPath)
        {
            WriteConstantTimeSeries(grid, value, 1, outputPath);
        }

        /// <summary>
        /// Writes a single-band GeoTIFF from a <c>[ncols, nrows]</c> array with a <b>lower-left
        /// origin</b> — the convention <see cref="AscRaster.ReadGeoTiff"/> reads into and
        /// <see cref="SlopeAspect"/> works in. Rows are flipped back on the way out, since a
        /// north-up GeoTIFF stores its northernmost row first.
        /// </summary>
        public static void WriteBand(MasterGrid grid, float[,] data, string outputPath)
        {
            int ncols = data.GetLength(0);
            int nrows = data.GetLength(1);

            if (ncols != grid.Header.Ncols || nrows != grid.Header.Nrows)
            {
                throw new System.ArgumentException(
                    $"Raster is {ncols}x{nrows} but the master grid is {grid.Header.Ncols}x{grid.Header.Nrows}.", nameof(data));
            }

            float[] buffer = new float[ncols * nrows];
            for (int row = 0; row < nrows; ++row)
            {
                int yIndex = nrows - 1 - row; //flip: GDAL row 0 is north, y index 0 is south
                for (int x = 0; x < ncols; ++x)
                {
                    buffer[row * ncols + x] = data[x, yIndex];
                }
            }

            Dataset ds = CreateOnGrid(grid, ncols, nrows, 1, outputPath);
            Band band = ds.GetRasterBand(1);
            band.SetNoDataValue(grid.Header.NoDataValue);
            band.WriteRaster(0, 0, ncols, nrows, buffer, ncols, nrows, 0, 0);
            ds.FlushCache();
            ds.Dispose();
        }

        /// <summary>
        /// Writes a multi-band GeoTIFF from one <c>[ncols, nrows]</c> array per band — a real
        /// time-varying weather series, one band per <c>DT_METEOROLOGY</c> step, in the same
        /// lower-left-origin convention as <see cref="WriteBand"/>.
        /// </summary>
        /// <remarks>
        /// Bands are written one at a time and never all held as a single buffer: a weather series
        /// long enough to matter is the largest thing this pipeline produces (72 bands of a 566x541
        /// domain is 88 MB), and the per-band cost is what keeps that off the heap.
        /// </remarks>
        public static void WriteBands(MasterGrid grid, System.Collections.Generic.IList<float[,]> bands, string outputPath)
        {
            if (bands == null || bands.Count == 0)
            {
                throw new System.ArgumentException("A time series needs at least one band.", nameof(bands));
            }

            int ncols = grid.Header.Ncols;
            int nrows = grid.Header.Nrows;

            for (int b = 0; b < bands.Count; ++b)
            {
                if (bands[b] == null)
                {
                    throw new System.ArgumentException($"Band {b + 1} of {bands.Count} is missing.", nameof(bands));
                }

                //Checked per band rather than once: a series assembled band by band is exactly where one
                //raster of the wrong size can slip in, and GDAL would accept the write and misplace the data.
                if (bands[b].GetLength(0) != ncols || bands[b].GetLength(1) != nrows)
                {
                    throw new System.ArgumentException(
                        $"Band {b + 1} is {bands[b].GetLength(0)}x{bands[b].GetLength(1)} but the master grid is {ncols}x{nrows}.",
                        nameof(bands));
                }
            }

            Dataset ds = CreateOnGrid(grid, ncols, nrows, bands.Count, outputPath);
            float[] buffer = new float[ncols * nrows];

            for (int b = 0; b < bands.Count; ++b)
            {
                float[,] data = bands[b];
                for (int row = 0; row < nrows; ++row)
                {
                    int yIndex = nrows - 1 - row; //flip: GDAL row 0 is north, y index 0 is south
                    for (int x = 0; x < ncols; ++x)
                    {
                        buffer[row * ncols + x] = data[x, yIndex];
                    }
                }

                Band band = ds.GetRasterBand(b + 1);
                band.SetNoDataValue(grid.Header.NoDataValue);
                band.WriteRaster(0, 0, ncols, nrows, buffer, ncols, nrows, 0, 0);
            }

            ds.FlushCache();
            ds.Dispose();
        }

        /// <summary>
        /// Writes a multi-band GeoTIFF with every band set to the same constant value — the
        /// "constant transient rasters" fallback docs/probabilistic-trigger-convergence.md
        /// allows for per-realization wind/moisture until a real time-varying series (WindNinja
        /// terrain wind, Nelson per-cell moisture) is available. <paramref name="bandCount"/>
        /// should match the realization's <c>NUM_METEOROLOGY_TIMES</c>.
        /// </summary>
        public static void WriteConstantTimeSeries(MasterGrid grid, float value, int bandCount, string outputPath)
        {
            int ncols = grid.Header.Ncols;
            int nrows = grid.Header.Nrows;

            Dataset ds = CreateOnGrid(grid, ncols, nrows, bandCount, outputPath);

            float[] buffer = new float[ncols * nrows];
            for (int i = 0; i < buffer.Length; ++i) buffer[i] = value;

            for (int b = 1; b <= bandCount; ++b)
            {
                Band band = ds.GetRasterBand(b);
                band.SetNoDataValue(grid.Header.NoDataValue);
                band.WriteRaster(0, 0, ncols, nrows, buffer, ncols, nrows, 0, 0);
            }

            ds.FlushCache();
            ds.Dispose();
        }

        /// <summary>Creates a Float32 GeoTIFF carrying the master grid's geotransform and CRS.</summary>
        private static Dataset CreateOnGrid(MasterGrid grid, int ncols, int nrows, int bandCount, string outputPath)
        {
            Gdal.AllRegister();

            Driver drv = Gdal.GetDriverByName("GTiff");
            Dataset ds = drv.Create(outputPath, ncols, nrows, bandCount, DataType.GDT_Float32, null);
            if (ds == null)
            {
                throw new System.Exception("Could not create GeoTIFF: " + outputPath);
            }

            double[] gt = { grid.XMin, grid.Header.CellSize, 0, grid.YMax, 0, -grid.Header.CellSize };
            ds.SetGeoTransform(gt);

            if (!string.IsNullOrEmpty(grid.Epsg))
            {
                SpatialReference srs = new SpatialReference("");
                srs.ImportFromEPSG(int.Parse(grid.Epsg.Replace("EPSG:", "")));
                srs.ExportToWkt(out string wkt, null);
                ds.SetProjection(wkt);
                srs.Dispose();
            }

            return ds;
        }
    }
}
