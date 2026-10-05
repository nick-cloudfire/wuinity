using System;
using System.Collections.Generic;
using System.IO;
using PREACT.Utility;

namespace PREACT.Visualization.MapLayers
{
    /// <summary>
    /// Reads input layers for the map: a band at display size (decimated by GDAL, nearest neighbour, so fuel codes stay
    /// codes), and single cells for the point info. For a worker thread; nothing here touches Unity.
    /// </summary>
    /// <remarks>
    /// Display reads are cached by file, band, size and the file's write time and length, so switching between layers,
    /// moving the opacity slider or stepping back to a band seen before reads nothing; a file rewritten by a case build
    /// is read again. The cache holds a few entries only - a 1024 x 1024 band is 4 MB.
    /// </remarks>
    public static class LayerRasterReader
    {
        /// <summary>A band read for display.</summary>
        public sealed class Display
        {
            /// <summary>The file's own grid at full resolution: where it lies and its cell count.</summary>
            public AscRaster.Header Header;

            /// <summary>The band at display size, [x, y] with y = 0 at the south.</summary>
            public float[,] Data;

            public int Bands;
            public double NoData;
        }

        private const int CacheEntries = 6;
        private static readonly object _lock = new object();
        private static readonly LinkedList<(string Key, Display Value)> _cache = new LinkedList<(string, Display)>();

        /// <summary>Drops every cached read (a scenario was closed).</summary>
        public static void Clear()
        {
            lock (_lock) _cache.Clear();
        }

        private static string Stamp(string path)
        {
            var info = new FileInfo(path);
            return info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":"
                   + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Band <paramref name="band"/> of <paramref name="path"/> with its longer side at most <paramref name="maxSide"/>
        /// cells. Throws when the file cannot be read, with GDAL's reason.
        /// </summary>
        public static Display ReadForDisplay(string path, int band, int maxSide)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("The raster is not there (any more): " + path);
            string key = Path.GetFullPath(path) + "|" + band + "|" + maxSide + "|" + Stamp(path);
            lock (_lock)
            {
                for (LinkedListNode<(string Key, Display Value)> n = _cache.First; n != null; n = n.Next)
                {
                    if (n.Value.Key != key) continue;
                    _cache.Remove(n);
                    _cache.AddFirst(n);
                    return n.Value.Value;
                }
            }

            Display read = Read(path, band, maxSide);
            lock (_lock)
            {
                _cache.AddFirst((key, read));
                while (_cache.Count > CacheEntries) _cache.RemoveLast();
            }
            return read;
        }

        private static Display Read(string path, int bandNumber, int maxSide)
        {
            OSGeo.GDAL.Gdal.AllRegister();
            using (OSGeo.GDAL.Dataset ds = OSGeo.GDAL.Gdal.Open(path, OSGeo.GDAL.Access.GA_ReadOnly))
            {
                if (ds == null) throw new IOException("GDAL could not open " + Path.GetFileName(path) + ": " + OSGeo.GDAL.Gdal.GetLastErrorMsg());
                int bands = ds.RasterCount;
                if (bandNumber < 1 || bandNumber > bands)
                {
                    throw new ArgumentOutOfRangeException(nameof(bandNumber), $"{Path.GetFileName(path)} has {bands} band(s); band {bandNumber} was asked for.");
                }

                int ncols = ds.RasterXSize, nrows = ds.RasterYSize;
                double[] gt = new double[6];
                ds.GetGeoTransform(gt);
                var header = HeaderOf(ds, gt);

                LayerValues.DisplaySize(ncols, nrows, maxSide, out int w, out int h);
                using (OSGeo.GDAL.Band b = ds.GetRasterBand(bandNumber))
                {
                    b.GetNoDataValue(out double nodata, out int hasNodata);
                    header.NoDataValue = hasNodata != 0 ? nodata : -9999.0;
                    var buffer = new float[w * h];
                    //A buffer smaller than the window decimates, nearest neighbour by default: one cell's value, never an
                    //average of two fuel codes.
                    OSGeo.GDAL.CPLErr err = b.ReadRaster(0, 0, ncols, nrows, buffer, w, h, 0, 0);
                    if (err != OSGeo.GDAL.CPLErr.CE_None) throw new IOException("GDAL could not read " + Path.GetFileName(path) + ": " + OSGeo.GDAL.Gdal.GetLastErrorMsg());

                    bool southUp = gt[5] > 0.0;
                    var data = new float[w, h];
                    for (int row = 0; row < h; ++row)
                    {
                        int y = southUp ? row : h - 1 - row;
                        for (int x = 0; x < w; ++x) data[x, y] = buffer[row * w + x];
                    }
                    return new Display { Header = header, Data = data, Bands = bands, NoData = header.NoDataValue };
                }
            }
        }

        private static AscRaster.Header HeaderOf(OSGeo.GDAL.Dataset ds, double[] gt)
        {
            var h = new AscRaster.Header
            {
                Ncols = ds.RasterXSize,
                Nrows = ds.RasterYSize,
                CellSize = System.Math.Abs(gt[1]),
                CellSizeY = System.Math.Abs(gt[5]),
                XllCorner = gt[0],
                YllCorner = gt[5] > 0.0 ? gt[3] : gt[3] + gt[5] * ds.RasterYSize,
                NoDataValue = -9999.0,
            };
            //The CRS the way every other reader here decides it (PROJCS, then GEOGCS, then identified).
            h.EpsgCode = AscRaster.GetEpsgCode(ds);
            return h;
        }

        /// <summary>One cell read from a raster.</summary>
        public struct Sample
        {
            public bool Inside;
            public double Value;
            public double NoData;

            /// <summary>GDAL pixel (from the west) and line (from the north, for a north-up file).</summary>
            public int Col, Row;

            public int Bands;

            /// <summary>The raster's CRS, 0 when it states none.</summary>
            public int EpsgCode;
        }

        /// <summary>
        /// The value of band <paramref name="band"/> of <paramref name="path"/> at (<paramref name="x"/>, <paramref name="y"/>)
        /// in the raster's own coordinates. <see cref="Sample.Inside"/> is false off the raster; a band past the last is
        /// read as the last. Throws when the file cannot be opened.
        /// </summary>
        public static Sample SampleAt(string path, int band, double x, double y)
        {
            OSGeo.GDAL.Gdal.AllRegister();
            using (OSGeo.GDAL.Dataset ds = OSGeo.GDAL.Gdal.Open(path, OSGeo.GDAL.Access.GA_ReadOnly))
            {
                if (ds == null) throw new IOException("GDAL could not open " + Path.GetFileName(path));
                var s = new Sample { Bands = ds.RasterCount, Col = -1, Row = -1, NoData = -9999.0, EpsgCode = AscRaster.GetEpsgCode(ds) };
                double[] gt = new double[6];
                ds.GetGeoTransform(gt);
                if (gt[1] == 0.0 || gt[5] == 0.0 || s.Bands < 1) return s;
                int col = (int)System.Math.Floor((x - gt[0]) / gt[1]);
                int row = (int)System.Math.Floor((y - gt[3]) / gt[5]);
                s.Col = col;
                s.Row = row;
                if (col < 0 || row < 0 || col >= ds.RasterXSize || row >= ds.RasterYSize) return s;

                int b = System.Math.Max(1, System.Math.Min(band, s.Bands));
                using (OSGeo.GDAL.Band rb = ds.GetRasterBand(b))
                {
                    rb.GetNoDataValue(out double nodata, out int hasNodata);
                    s.NoData = hasNodata != 0 ? nodata : -9999.0;
                    var v = new double[1];
                    if (rb.ReadRaster(col, row, 1, 1, v, 1, 1, 0, 0) != OSGeo.GDAL.CPLErr.CE_None) return s;
                    s.Value = v[0];
                    s.Inside = true;
                }
                return s;
            }
        }
    }
}
