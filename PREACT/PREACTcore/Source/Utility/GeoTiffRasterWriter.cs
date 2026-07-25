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
        /// Writes a multi-band GeoTIFF with every band set to the same constant value — the
        /// "constant transient rasters" fallback docs/probabilistic-trigger-convergence.md
        /// allows for per-realization wind/moisture until a real time-varying series (WindNinja
        /// terrain wind, Nelson per-cell moisture) is available. <paramref name="bandCount"/>
        /// should match the realization's <c>NUM_METEOROLOGY_TIMES</c>.
        /// </summary>
        public static void WriteConstantTimeSeries(MasterGrid grid, float value, int bandCount, string outputPath)
        {
            Gdal.AllRegister();

            int ncols = grid.Header.Ncols;
            int nrows = grid.Header.Nrows;

            Driver drv = Gdal.GetDriverByName("GTiff");
            Dataset ds = drv.Create(outputPath, ncols, nrows, bandCount, DataType.GDT_Float32, null);

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
    }
}
