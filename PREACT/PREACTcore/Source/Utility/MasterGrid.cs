using OSGeo.GDAL;
using OSGeo.OSR;

namespace PREACT.Utility
{
    /// <summary>
    /// The CRS/cell-size/extent that every ELMFIRE input raster must share (docs/probabilistic-
    /// trigger-convergence.md, "Master-grid principle"). Read back from an already-warped raster
    /// file (typically the domain's DEM, once <see cref="RasterHarmonizer"/> has reprojected it
    /// to the local UTM zone) rather than constructed by hand, so it always reflects the grid
    /// that actually exists on disk.
    /// </summary>
    public class MasterGrid
    {
        public AscRaster.Header Header;

        /// <summary>"EPSG:32634"-style CRS identifier, or null if it could not be resolved
        /// from the raster's projection metadata.</summary>
        public string Epsg;

        public double XMin => Header.XllCorner;
        public double YMin => Header.YllCorner;
        public double XMax => Header.XllCorner + Header.Ncols * Header.CellSize;
        public double YMax => Header.YllCorner + Header.Nrows * Header.CellSize;

        public static MasterGrid FromRasterFile(string path)
        {
            Gdal.AllRegister();
            Dataset ds = Gdal.Open(path, Access.GA_ReadOnly);
            if (ds == null)
            {
                throw new System.IO.FileNotFoundException("Could not open raster as the master grid: " + path);
            }

            double[] gt = new double[6];
            ds.GetGeoTransform(gt);

            var header = new AscRaster.Header
            {
                Ncols = ds.RasterXSize,
                Nrows = ds.RasterYSize,
                CellSize = gt[1],
                XllCorner = gt[0],
                YllCorner = gt[3] + gt[5] * ds.RasterYSize, //gt[5] negative -> bottom edge
                NoDataValue = -9999.0,
            };

            Band band = ds.GetRasterBand(1);
            band.GetNoDataValue(out double nodata, out int hasNodata);
            if (hasNodata != 0) header.NoDataValue = nodata;

            string epsg = null;
            SpatialReference srs = new SpatialReference(ds.GetProjection());
            srs.AutoIdentifyEPSG();
            string code = srs.GetAuthorityCode(null);
            if (!string.IsNullOrEmpty(code)) epsg = "EPSG:" + code;
            srs.Dispose();

            ds.Dispose();

            return new MasterGrid { Header = header, Epsg = epsg };
        }
    }
}
