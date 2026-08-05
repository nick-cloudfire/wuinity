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

            //Everything downstream - Header.CellSize, the corner arithmetic below, SlopeAspect, the EDT, the
            //k-PERIL grid - is written for a north-up raster with square cells. None of that checks, so a
            //grid that is not is accepted and then quietly measured wrong. Refusing here is the only place
            //that can catch it once, before it becomes a set of plausible-looking numbers.
            double pixelWidth = gt[1];
            double pixelHeight = -gt[5]; //north-up rasters store a negative north-south step

            if (gt[2] != 0.0 || gt[4] != 0.0)
            {
                ds.Dispose();
                throw new System.InvalidOperationException(
                    $"The grid raster is rotated (geotransform skew {gt[2]}, {gt[4]}), and every distance and " +
                    $"direction in this pipeline assumes an axis-aligned grid: {path}. Warp it to a north-up " +
                    "grid first.");
            }

            if (pixelHeight <= 0.0)
            {
                ds.Dispose();
                throw new System.InvalidOperationException(
                    $"The grid raster is not north-up (north-south step {gt[5]}), so its rows run the opposite " +
                    $"way to the lower-left origin everything here reads in: {path}.");
            }

            //A tolerance rather than equality: a warped raster's two steps can differ in the last bits without
            //meaning anything. A tenth of a percent is far below what would displace a cell.
            if (System.Math.Abs(pixelWidth - pixelHeight) > 0.001 * System.Math.Max(pixelWidth, pixelHeight))
            {
                ds.Dispose();
                throw new System.InvalidOperationException(
                    $"The grid raster has non-square cells ({pixelWidth} x {pixelHeight} m): {path}. A single " +
                    "CellSize is carried from here into slope, aspect, the distance transform and k-PERIL, so a " +
                    "rectangular cell would be measured as square and every distance along one axis would be " +
                    "wrong by their ratio. Resample it to square cells.");
            }

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
