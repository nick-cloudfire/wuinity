//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Globalization;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// Minimal ESRI ASCII grid (.asc) reader/writer. Data is returned/expected in
    /// [x, y] order with a lower-left origin (y = 0 is the bottom row), matching the
    /// convention used by AscFireImport.GetMaxROS() and the k-PERIL rasters.
    /// </summary>
    public static class AscRaster
    {
        public struct Header
        {
            public int Ncols;
            public int Nrows;
            public double XllCorner;
            public double YllCorner;
            public double CellSize;
            public double NoDataValue;

            /// <summary>
            /// The raster's coordinate reference system as an EPSG code, or 0 when it does not say.
            ///
            /// This is what makes the corner above interpretable. An easting is meaningless without
            /// knowing which UTM zone measured it: at the boundary between two zones the same ground
            /// has eastings half a million metres apart, so treating an unknown-CRS easting as if it
            /// were in the simulation's own zone is how fire data ends up hundreds of kilometres from
            /// the domain. A GeoTIFF carries this; a bare .asc does not, unless a .prj sits beside it.
            /// </summary>
            public int EpsgCode;
        }

        /// <summary>
        /// The EPSG code of a GDAL dataset's projection, or 0 if it has none or cannot be identified.
        /// </summary>
        private static int GetEpsgCode(OSGeo.GDAL.Dataset dataset)
        {
            string wkt = dataset.GetProjectionRef();
            if (string.IsNullOrEmpty(wkt))
            {
                return 0;
            }

            return EpsgFromWkt(wkt);
        }

        private static int EpsgFromWkt(string wkt)
        {
            try
            {
                var srs = new OSGeo.OSR.SpatialReference(wkt);

                //Well-known CRSs usually name their authority; those that do not can often still be
                //recognised from their parameters, which is what AutoIdentifyEPSG is for.
                string code = srs.GetAuthorityCode("PROJCS");
                if (string.IsNullOrEmpty(code))
                {
                    srs.AutoIdentifyEPSG();
                    code = srs.GetAuthorityCode("PROJCS");
                }

                return int.TryParse(code, out int epsg) ? epsg : 0;
            }
            catch
            {
                //An unreadable projection is the same as an absent one for every caller here.
                return 0;
            }
        }

        /// <summary>
        /// The CRS of a companion .prj file, or 0 when there is none. An ESRI ASCII grid keeps no
        /// projection of its own, so this is the only place one can come from.
        /// </summary>
        private static int EpsgFromCompanionPrj(string rasterFilePath)
        {
            string prj = Path.ChangeExtension(rasterFilePath, ".prj");
            if (!File.Exists(prj))
            {
                return 0;
            }

            return EpsgFromWkt(File.ReadAllText(prj));
        }

        private static string[] SplitLine(string line)
        {
            return line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Read a raster into a [ncols, nrows] array with a lower-left origin. Dispatches on
        /// extension: .tif/.tiff via GDAL, otherwise an ESRI ASCII grid (.asc). Both are
        /// flipped so the file's north row lands at y = nrows-1.
        /// </summary>
        public static float[,] Read(string filePath, out Header header, out bool success)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".tif" || ext == ".tiff")
            {
                return ReadGeoTiff(filePath, out header, out success);
            }
            return ReadAsc(filePath, out header, out success);
        }

        /// <summary>
        /// Read an .asc grid into a [ncols, nrows] array with a lower-left origin
        /// (the file's first data row is the north edge, so it is flipped to y = nrows-1).
        /// </summary>
        public static float[,] ReadAsc(string filePath, out Header header, out bool success)
        {
            header = new Header();
            success = false;

            if (!File.Exists(filePath))
            {
                Engine.Message(null, Engine.LogType.SimulationError, "ASC file not found: " + filePath);
                return null;
            }

            string[] lines = File.ReadAllLines(filePath);
            if (lines.Length < 6)
            {
                Engine.Message(null, Engine.LogType.SimulationError, "ASC file has no data: " + filePath);
                return null;
            }

            int.TryParse(SplitLine(lines[0])[1], out header.Ncols);
            int.TryParse(SplitLine(lines[1])[1], out header.Nrows);
            double.TryParse(SplitLine(lines[2])[1], NumberStyles.Any, CultureInfo.InvariantCulture, out header.XllCorner);
            double.TryParse(SplitLine(lines[3])[1], NumberStyles.Any, CultureInfo.InvariantCulture, out header.YllCorner);
            double.TryParse(SplitLine(lines[4])[1], NumberStyles.Any, CultureInfo.InvariantCulture, out header.CellSize);
            double.TryParse(SplitLine(lines[5])[1], NumberStyles.Any, CultureInfo.InvariantCulture, out header.NoDataValue);

            float[,] data = new float[header.Ncols, header.Nrows];
            for (int y = 0; y < header.Nrows; y++)
            {
                if (y + 6 >= lines.Length)
                {
                    Engine.Message(null, Engine.LogType.SimulationError, "ASC file has fewer data rows than nrows: " + filePath);
                    return null;
                }
                string[] row = SplitLine(lines[y + 6]);
                int yIndex = header.Nrows - 1 - y; //flip: first row is north
                for (int x = 0; x < header.Ncols && x < row.Length; x++)
                {
                    float.TryParse(row[x], NumberStyles.Any, CultureInfo.InvariantCulture, out float v);
                    data[x, yIndex] = v;
                }
            }

            success = true;
            return data;
        }

        /// <summary>
        /// Read a GeoTIFF (band 1) into a [ncols, nrows] array with a lower-left origin.
        /// GDAL rows run north-to-south for a north-up image, so rows are flipped.
        /// </summary>
        public static float[,] ReadGeoTiff(string filePath, out Header header, out bool success)
        {
            header = new Header();
            success = false;

            if (!File.Exists(filePath))
            {
                Engine.Message(null, Engine.LogType.SimulationError, "GeoTIFF not found: " + filePath);
                return null;
            }

            OSGeo.GDAL.Gdal.AllRegister();
            using (OSGeo.GDAL.Dataset ds = OSGeo.GDAL.Gdal.Open(filePath, OSGeo.GDAL.Access.GA_ReadOnly))
            {
                if (ds == null)
                {
                    Engine.Message(null, Engine.LogType.SimulationError, "GDAL could not open: " + filePath);
                    return null;
                }

                int ncols = ds.RasterXSize;
                int nrows = ds.RasterYSize;

                double[] gt = new double[6];
                ds.GetGeoTransform(gt); //[originX, pxW, 0, originY, 0, pxH(neg)]
                double cellSize = gt[1];
                double originX = gt[0];
                double originY = gt[3];

                OSGeo.GDAL.Band band = ds.GetRasterBand(1);
                band.GetNoDataValue(out double nodata, out int hasNodata);

                header.Ncols = ncols;
                header.Nrows = nrows;
                header.CellSize = cellSize;
                header.XllCorner = originX;
                header.YllCorner = originY + gt[5] * nrows; //gt[5] negative -> bottom edge
                header.NoDataValue = hasNodata != 0 ? nodata : -9999.0;

                float[] buffer = new float[ncols * nrows];
                band.ReadRaster(0, 0, ncols, nrows, buffer, ncols, nrows, 0, 0);

                float[,] data = new float[ncols, nrows];
                for (int row = 0; row < nrows; row++)
                {
                    int yIndex = nrows - 1 - row; //flip: GDAL row 0 is north
                    for (int x = 0; x < ncols; x++)
                    {
                        data[x, yIndex] = buffer[row * ncols + x];
                    }
                }

                success = true;
                return data;
            }
        }

        /// <summary>
        /// Reads only the georeferencing of a raster - extent, cell size, cell count - without its
        /// data. For the cases that need to know what grid a file is on rather than what is in it:
        /// reading a whole arrival time raster to learn its dimensions costs a large allocation for
        /// six numbers.
        /// </summary>
        public static Header ReadHeader(string filePath, out bool success)
        {
            var header = new Header();
            success = false;

            if (!File.Exists(filePath))
            {
                Engine.Message(null, Engine.LogType.SimulationError, "Raster not found: " + filePath);
                return header;
            }

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".tif" || ext == ".tiff")
            {
                OSGeo.GDAL.Gdal.AllRegister();
                using (OSGeo.GDAL.Dataset ds = OSGeo.GDAL.Gdal.Open(filePath, OSGeo.GDAL.Access.GA_ReadOnly))
                {
                    if (ds == null)
                    {
                        Engine.Message(null, Engine.LogType.SimulationError, "GDAL could not open: " + filePath);
                        return header;
                    }

                    double[] gt = new double[6];
                    ds.GetGeoTransform(gt); //[originX, pxW, 0, originY, 0, pxH(neg)]

                    header.Ncols = ds.RasterXSize;
                    header.Nrows = ds.RasterYSize;
                    header.CellSize = gt[1];
                    header.XllCorner = gt[0];
                    header.YllCorner = gt[3] + gt[5] * header.Nrows; //gt[5] negative -> bottom edge

                    ds.GetRasterBand(1).GetNoDataValue(out double nodata, out int hasNodata);
                    header.NoDataValue = hasNodata != 0 ? nodata : -9999.0;
                    header.EpsgCode = GetEpsgCode(ds);

                    success = true;
                    return header;
                }
            }

            //An .asc grid keeps all six in its first six lines, so the data never has to be touched.
            using (StreamReader reader = new StreamReader(filePath))
            {
                string[] lines = new string[6];
                for (int i = 0; i < 6; ++i)
                {
                    lines[i] = reader.ReadLine();
                    if (lines[i] == null)
                    {
                        Engine.Message(null, Engine.LogType.SimulationError, "ASC file has no header: " + filePath);
                        return header;
                    }
                }

                int.TryParse(SplitLine(lines[0])[1], out header.Ncols);
                int.TryParse(SplitLine(lines[1])[1], out header.Nrows);
                double.TryParse(SplitLine(lines[2])[1], NumberStyles.Any, CultureInfo.InvariantCulture, out header.XllCorner);
                double.TryParse(SplitLine(lines[3])[1], NumberStyles.Any, CultureInfo.InvariantCulture, out header.YllCorner);
                double.TryParse(SplitLine(lines[4])[1], NumberStyles.Any, CultureInfo.InvariantCulture, out header.CellSize);
                double.TryParse(SplitLine(lines[5])[1], NumberStyles.Any, CultureInfo.InvariantCulture, out header.NoDataValue);
            }

            header.EpsgCode = EpsgFromCompanionPrj(filePath);

            success = header.Ncols > 0 && header.Nrows > 0 && header.CellSize > 0.0;
            return header;
        }

        /// <summary>
        /// Write a [ncols, nrows] lower-left-origin array to an .asc grid (flipping back
        /// so the north row is written first).
        /// </summary>
        public static void Write(float[,] data, Header header, string outputFilePath)
        {
            int ncols = data.GetLength(0);
            int nrows = data.GetLength(1);

            using (StreamWriter w = new StreamWriter(outputFilePath))
            {
                w.WriteLine("ncols " + ncols);
                w.WriteLine("nrows " + nrows);
                w.WriteLine("xllcorner " + header.XllCorner.ToString(CultureInfo.InvariantCulture));
                w.WriteLine("yllcorner " + header.YllCorner.ToString(CultureInfo.InvariantCulture));
                w.WriteLine("cellsize " + header.CellSize.ToString(CultureInfo.InvariantCulture));
                w.WriteLine("NODATA_value " + header.NoDataValue.ToString(CultureInfo.InvariantCulture));

                for (int y = 0; y < nrows; y++)
                {
                    int yIndex = nrows - 1 - y; //flip back: write north row first
                    var sb = new System.Text.StringBuilder();
                    for (int x = 0; x < ncols; x++)
                    {
                        if (x > 0) sb.Append(' ');
                        sb.Append(data[x, yIndex].ToString(CultureInfo.InvariantCulture));
                    }
                    w.WriteLine(sb.ToString());
                }
            }
        }
    }
}
