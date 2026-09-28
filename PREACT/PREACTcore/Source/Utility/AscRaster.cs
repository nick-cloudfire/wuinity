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
    ///
    /// That claim about GetMaxROS() was for a long time untrue — it flipped the y axis while saying so in its
    /// own summary, so this line described an agreement that did not exist. It does now.
    /// </summary>
    public static class AscRaster
    {
        public struct Header
        {
            public int Ncols;
            public int Nrows;
            public double XllCorner;
            public double YllCorner;
            /// <summary>Cell width (x) in the raster's units.</summary>
            public double CellSize;
            public double NoDataValue;

            /// <summary>
            /// Cell height (y). Equal to <see cref="CellSize"/> for an .asc and for square GeoTIFF pixels; a GeoTIFF
            /// with rectangular pixels (the Mati DEM is 27.592 x 27.616 m) has its own here. It used to be taken to
            /// be the width, which misplaces the far edge of such a grid by the difference times the row count
            /// (about 14 m across Mati). Code that assumes square cells should compare the two.
            /// </summary>
            public double CellSizeY;

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

                //PROJCS names a projected CRS and GEOGCS a geographic one, and only one of the two is
                //present. Asking for PROJCS alone reported "no CRS" for every lat/lon raster - including
                //the DEMs OpenTopography serves, which are EPSG:4326 - so a raster in degrees came out
                //indistinguishable from one that says nothing at all. The difference matters: the first
                //needs reprojecting and can say so, the second can only be guessed at.
                string code = srs.GetAuthorityCode("PROJCS");
                if (string.IsNullOrEmpty(code))
                {
                    code = srs.GetAuthorityCode("GEOGCS");
                }
                if (string.IsNullOrEmpty(code))
                {
                    //Some CRSs name no authority but are still recognisable from their parameters.
                    srs.AutoIdentifyEPSG();
                    code = srs.GetAuthorityCode("PROJCS") ?? srs.GetAuthorityCode("GEOGCS");
                }

                return int.TryParse(code, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int epsg) ? epsg : 0;
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

        /// <summary>
        /// Writes <c>&lt;raster&gt;.prj</c> for an ESRI ASCII grid in the CRS <paramref name="epsgCode"/>, as OGC WKT
        /// with its EPSG authority, which GDAL (and so QGIS) identifies as that code - the ESRI dialect loses the code
        /// and comes back as an unnamed "UTM zone 34N". Quietly does nothing when the code is unknown or GDAL cannot
        /// describe it: the grid is still correct, only without a CRS attached.
        /// </summary>
        /// <remarks>
        /// An .asc cannot hold its own CRS, and the platform's boundaries and probability rasters used to be written
        /// without one, so every one of them opened in QGIS as "unknown CRS" at the right numbers but the wrong place
        /// until the zone was set by hand. <see cref="Read"/> takes the CRS back from this file.
        /// </remarks>
        public static void WriteCompanionPrj(string rasterFilePath, int epsgCode)
        {
            if (epsgCode <= 0 || string.IsNullOrEmpty(rasterFilePath)) return;

            try
            {
                using (var srs = new OSGeo.OSR.SpatialReference(""))
                {
                    if (srs.ImportFromEPSG(epsgCode) != 0) return;
                    srs.ExportToWkt(out string wkt, null);
                    if (string.IsNullOrEmpty(wkt)) return;
                    File.WriteAllText(Path.ChangeExtension(rasterFilePath, ".prj"), wkt);
                }
            }
            catch
            {
                //A missing CRS is a convenience lost, not a wrong raster; never fail the write over it.
            }
        }

        private static string[] SplitLine(string line)
        {
            return (line ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Reads an ESRI ASCII grid header: keyword/value lines in any order, case-insensitive - ncols, nrows,
        /// xllcorner or xllcenter, yllcorner or yllcenter, cellsize (or dx/dy), and an optional NODATA_value
        /// (default -9999). Returns the number of header lines, or -1 with a message when a required key is
        /// missing. It used to assume exactly six lines in a fixed order, so a grid written with xllcenter, or
        /// without NODATA_value, was read with the first data row taken for the nodata value.
        /// </summary>
        private static int ParseAscHeader(Func<int, string> line, string filePath, ref Header header)
        {
            bool haveCols = false, haveRows = false, haveX = false, haveY = false, haveSize = false;
            bool xCentre = false, yCentre = false;
            header.NoDataValue = -9999.0;
            double dx = 0.0, dy = 0.0;
            int count = 0;
            for (int i = 0; ; ++i)
            {
                //A header line is one of the header's own keywords, not any line starting with a letter: a first data
                //row starting with "nan" or "NaN" (a float grid with a hole in its north-west corner) was taken for a
                //header line, and the grid was read one row off.
                string[] parts = SplitLine(line(i));
                if (parts.Length < 2 || !IsHeaderKeyword(parts[0]))
                {
                    break;
                }
                count = i + 1;
                string key = parts[0].ToLowerInvariant();
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value);
                switch (key)
                {
                    case "ncols": header.Ncols = (int)value; haveCols = true; break;
                    case "nrows": header.Nrows = (int)value; haveRows = true; break;
                    case "xllcorner": header.XllCorner = value; haveX = true; break;
                    case "xllcenter": header.XllCorner = value; haveX = true; xCentre = true; break;
                    case "yllcorner": header.YllCorner = value; haveY = true; break;
                    case "yllcenter": header.YllCorner = value; haveY = true; yCentre = true; break;
                    case "cellsize": header.CellSize = value; header.CellSizeY = value; haveSize = true; break;
                    case "dx": dx = value; break;
                    case "dy": dy = value; break;
                    case "nodata_value": header.NoDataValue = value; break;
                }
            }

            if (!haveSize && dx > 0.0)
            {
                header.CellSize = dx;
                header.CellSizeY = dy > 0.0 ? dy : dx;
                haveSize = true;
            }

            if (!(haveCols && haveRows && haveX && haveY && haveSize) || header.Ncols <= 0 || header.Nrows <= 0 || header.CellSize <= 0.0)
            {
                Engine.Message(null, Engine.LogType.InputError, "ASC file has an incomplete header (ncols, nrows, xll*, yll*, cellsize): " + filePath);
                return -1;
            }

            //A centre is half a cell in from the corner every consumer expects.
            if (xCentre) header.XllCorner -= 0.5 * header.CellSize;
            if (yCentre) header.YllCorner -= 0.5 * header.CellSizeY;
            return count;
        }

        private static bool IsHeaderKeyword(string token)
        {
            switch (token.ToLowerInvariant())
            {
                case "ncols": case "nrows": case "xllcorner": case "xllcenter": case "yllcorner": case "yllcenter":
                case "cellsize": case "dx": case "dy": case "nodata_value":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Cell size from a GDAL geotransform, with a word about what the reader cannot represent.</summary>
        private static void ApplyGeoTransform(double[] gt, string filePath, ref Header header)
        {
            header.CellSize = System.Math.Abs(gt[1]);
            header.CellSizeY = System.Math.Abs(gt[5]);
            header.XllCorner = gt[0];
            header.YllCorner = gt[3] + gt[5] * header.Nrows; //gt[5] negative -> bottom edge

            if (gt[2] != 0.0 || gt[4] != 0.0)
            {
                Engine.Message(null, Engine.LogType.Warning, $"{Path.GetFileName(filePath)} is rotated in its geotransform ({gt[2]}, {gt[4]}); "
                    + "the rotation is ignored and the raster will be misplaced. Warp it north-up first.");
            }
            else if (gt[5] > 0.0)
            {
                //South-up: the origin is the lower-left corner already.
                header.YllCorner = gt[3];
            }
        }

        /// <summary>
        /// Read a raster into a [ncols, nrows] array with a lower-left origin. Dispatches on
        /// extension: .tif/.tiff via GDAL, otherwise an ESRI ASCII grid (.asc). Both are
        /// flipped so the file's north row lands at y = nrows-1.
        /// </summary>
        public static float[,] Read(string filePath, out Header header, out bool success)
        {
            return Read(filePath, 1, out header, out success, out int _);
        }

        /// <summary>Reads one band, whichever format the file is. An .asc has exactly one.</summary>
        public static float[,] Read(string filePath, int bandNumber, out Header header, out bool success,
            out int bandCount)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            if (ext == ".tif" || ext == ".tiff")
            {
                return ReadGeoTiff(filePath, bandNumber, out header, out success, out bandCount);
            }

            bandCount = 1;
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
                Engine.Message(null, Engine.LogType.InputError, "ASC file not found: " + filePath);
                return null;
            }

            string[] lines = File.ReadAllLines(filePath);
            int headerLines = ParseAscHeader(i => i < lines.Length ? lines[i] : null, filePath, ref header);
            if (headerLines < 0)
            {
                return null;
            }
            header.EpsgCode = EpsgFromCompanionPrj(filePath);

            float[,] data = new float[header.Ncols, header.Nrows];
            int shortRows = 0;
            for (int y = 0; y < header.Nrows; y++)
            {
                if (y + headerLines >= lines.Length)
                {
                    Engine.Message(null, Engine.LogType.InputError, "ASC file has fewer data rows than nrows: " + filePath);
                    return null;
                }
                string[] row = SplitLine(lines[y + headerLines]);
                shortRows += row.Length < header.Ncols ? 1 : 0;
                int yIndex = header.Nrows - 1 - y; //flip: first row is north
                for (int x = 0; x < header.Ncols && x < row.Length; x++)
                {
                    float.TryParse(row[x], NumberStyles.Float, CultureInfo.InvariantCulture, out float v);
                    data[x, yIndex] = v;
                }
            }
            if (shortRows > 0)
            {
                Engine.Message(null, Engine.LogType.Warning, $"{Path.GetFileName(filePath)}: {shortRows} row(s) have fewer than {header.Ncols} values; the missing cells read as 0.");
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
            return ReadGeoTiff(filePath, 1, out header, out success, out int _);
        }

        /// <summary>
        /// How many bands a GeoTIFF has, or 0 if it cannot be opened.
        /// </summary>
        /// <remarks>
        /// Opens the dataset without reading any pixels, because the callers that want this - the namelist
        /// builder asking how many hours of weather the case holds - want the count and nothing else, and
        /// the weather rasters are large enough that reading a band to find out would be silly. Quiet on
        /// failure: a missing raster is the caller's business to report, in its own terms.
        /// </remarks>
        public static int GetBandCount(string filePath)
        {
            if (!File.Exists(filePath))
            {
                return 0;
            }

            try
            {
                OSGeo.GDAL.Gdal.AllRegister();
                using (OSGeo.GDAL.Dataset ds = OSGeo.GDAL.Gdal.Open(filePath, OSGeo.GDAL.Access.GA_ReadOnly))
                {
                    return ds == null ? 0 : ds.RasterCount;
                }
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Reads one band of a GeoTIFF, and reports how many it has.
        ///
        /// The band matters for the weather rasters an ELMFIRE case carries: <c>ws.tif</c> and
        /// <c>wd.tif</c> hold one band per hour, and reading such a file as though it were single-band -
        /// which is what happened - silently uses the first hour for whatever the caller is doing. The band
        /// count comes back so a caller can say which hour it is using instead of implying there is only
        /// one.
        /// </summary>
        public static float[,] ReadGeoTiff(string filePath, int bandNumber, out Header header, out bool success,
            out int bandCount)
        {
            header = new Header();
            success = false;
            bandCount = 0;

            if (!File.Exists(filePath))
            {
                Engine.Message(null, Engine.LogType.InputError, "GeoTIFF not found: " + filePath);
                return null;
            }

            OSGeo.GDAL.Gdal.AllRegister();
            using (OSGeo.GDAL.Dataset ds = OSGeo.GDAL.Gdal.Open(filePath, OSGeo.GDAL.Access.GA_ReadOnly))
            {
                if (ds == null)
                {
                    Engine.Message(null, Engine.LogType.InputError, "GDAL could not open: " + filePath);
                    return null;
                }

                int ncols = ds.RasterXSize;
                int nrows = ds.RasterYSize;

                double[] gt = new double[6];
                ds.GetGeoTransform(gt); //[originX, pxW, 0, originY, 0, pxH(neg)]
                bool southUp = gt[5] > 0.0;

                bandCount = ds.RasterCount;
                if (bandNumber < 1 || bandNumber > bandCount)
                {
                    Engine.Message(null, Engine.LogType.InputError,
                        $"{Path.GetFileName(filePath)} has {bandCount} band(s); band {bandNumber} was asked for.");
                    return null;
                }

                OSGeo.GDAL.Band band = ds.GetRasterBand(bandNumber);
                band.GetNoDataValue(out double nodata, out int hasNodata);

                header.Ncols = ncols;
                header.Nrows = nrows;
                ApplyGeoTransform(gt, filePath, ref header);
                header.NoDataValue = hasNodata != 0 ? nodata : -9999.0;
                //Populated here as well as in ReadHeader. Leaving it out meant a raster read for its data
                //reported no CRS while the same file read for its header reported one, so whether a raster
                //appeared georeferenced depended on which function had been called.
                header.EpsgCode = GetEpsgCode(ds);

                float[] buffer = new float[ncols * nrows];
                band.ReadRaster(0, 0, ncols, nrows, buffer, ncols, nrows, 0, 0);

                float[,] data = new float[ncols, nrows];
                for (int row = 0; row < nrows; row++)
                {
                    int yIndex = southUp ? row : nrows - 1 - row; //flip: GDAL row 0 is north (for a north-up image)
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
                Engine.Message(null, Engine.LogType.InputError, "Raster not found: " + filePath);
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
                        Engine.Message(null, Engine.LogType.InputError, "GDAL could not open: " + filePath);
                        return header;
                    }

                    double[] gt = new double[6];
                    ds.GetGeoTransform(gt); //[originX, pxW, 0, originY, 0, pxH(neg)]

                    header.Ncols = ds.RasterXSize;
                    header.Nrows = ds.RasterYSize;
                    ApplyGeoTransform(gt, filePath, ref header);

                    ds.GetRasterBand(1).GetNoDataValue(out double nodata, out int hasNodata);
                    header.NoDataValue = hasNodata != 0 ? nodata : -9999.0;
                    header.EpsgCode = GetEpsgCode(ds);

                    success = true;
                    return header;
                }
            }

            //An .asc grid keeps its header in its first few lines, so the data never has to be touched.
            using (StreamReader reader = new StreamReader(filePath))
            {
                var lines = new System.Collections.Generic.List<string>();
                int headerLines = ParseAscHeader(i =>
                {
                    while (lines.Count <= i)
                    {
                        string next = reader.ReadLine();
                        if (next == null) return null;
                        lines.Add(next);
                    }
                    return lines[i];
                }, filePath, ref header);
                if (headerLines < 0)
                {
                    return header;
                }
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

            //The CRS the header carries (read from a GeoTIFF, or from a .prj beside an .asc), so what is written
            //opens where it belongs.
            WriteCompanionPrj(outputFilePath, header.EpsgCode);
        }
    }
}
