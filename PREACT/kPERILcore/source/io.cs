using System.Runtime.CompilerServices;
using System;
using System.IO;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;
using System.Globalization;

namespace kperil
{
    public class perilInputOutput
    {
        public static float[,] ReadRaster(string fileLocation)
        {
            if (!IsValidInputFile(fileLocation)) throw new FileNotFoundException($"File {fileLocation} could not be loaded", fileLocation);
            
            string filetype = Path.GetExtension(fileLocation).ToLower();

            if (filetype == ".asc")
            {
                return ReadAsciiGrid(fileLocation);
            }
            else if (filetype == ".tif" || filetype == ".tiff")
            {
                return ReadGeoTiff(fileLocation);
            }
            else
            {
                throw new Exception($"File type {filetype} is not a recognised raster file format [.asc, .tif, .tiff].");
            }
        }

        private static bool IsValidInputFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path cannot be null or whitespace.", nameof(filePath));
            }

            if (!File.Exists(filePath))
            {
                return false;
            }

            FileAttributes attributes = File.GetAttributes(filePath);
            return !attributes.HasFlag(FileAttributes.Directory);
        }
        private static float[,] ReadAsciiGrid(string filePath)
        {
            string[] lines = File.ReadAllLines(filePath);
            int headerLines = 6; // typically: ncols, nrows, xllcorner, yllcorner, cellsize, NODATA_value

            // Read header
            int ncols = int.Parse(lines[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1]);
            int nrows = int.Parse(lines[1].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1]);
            float[,] data = new float[nrows,ncols];

            // Read data
            for (int row = 0; row < nrows; row++)
                {
                    // ASCII grid starts at the top row
                    string[] parts = lines[row + headerLines]
                        .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

                    if (parts.Length != ncols)
                        throw new FormatException($"Line {row + headerLines} has {parts.Length} columns, expected {ncols}.");

                    for (int col = 0; col < ncols; col++)
                    {
                        data[row, col] = float.Parse(parts[col], System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            return data;
        }

        private static float[,] ReadGeoTiff(string filePath)
        {
            Gdal.AllRegister();

            using (Dataset ds = Gdal.Open(filePath, Access.GA_ReadOnly))
            {
                if (ds == null)
                    throw new Exception($"Failed to open GeoTIFF: {filePath}");

                Band band = ds.GetRasterBand(1);
                int width = band.XSize;
                int height = band.YSize;

                float[] buffer = new float[width * height];
                band.ReadRaster(0, 0, width, height, buffer, width, height, 0, 0);

                // Convert to jagged array
                float[,] data = new float[height, width];
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        data[y, x] = buffer[y * width + x];
                    }
                }

                return data;
            }
        }

        public static void WriteRasterByCopy(float[,] raster, string parentFile, string outputFileName)
        {
            string filetype = Path.GetExtension(parentFile).ToLower();

            if (filetype == ".asc")
            {
                WriteAsciiGrid(raster, parentFile, outputFileName);
            }
            else if (filetype == ".tif" || filetype == ".tiff")
            {
                WriteGeoTiff(raster, parentFile, outputFileName);
            }
            else
            {
                throw new Exception($"File type {filetype} is not a recognised raster file format [.asc, .tif, .tiff].");
            }
        }

        private static void WriteAsciiGrid(float[,] raster, string parentFile, string outputFileName)
        {
            string[] lines = File.ReadAllLines(parentFile);
            //int headerLines = 6; // typically: ncols, nrows, xllcorner, yllcorner, cellsize, NODATA_value

            // Read header
            int ncols = int.Parse(lines[0].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1]);
            int nrows = int.Parse(lines[1].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1]);
            float xllcorner = float.Parse(lines[2].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1]);
            float yllcorner = float.Parse(lines[3].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1]);
            float cellsize = float.Parse(lines[4].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1]);
            int NODATA_value = int.Parse(lines[5].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[1]);

            // --- Validate dimensions ---
            if (raster.GetLength(0) != nrows || raster.GetLength(1) != ncols)
                throw new Exception($"Input raster dimensions ({raster.GetLength(0)}x{raster.GetLength(1)}) do not match parent ({nrows}x{ncols}).");

            using (var sw = new StreamWriter(outputFileName))
            {
                sw.WriteLine($"ncols         {ncols}");
                sw.WriteLine($"nrows         {nrows}");
                sw.WriteLine($"xllcorner     {xllcorner.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                sw.WriteLine($"yllcorner     {yllcorner.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                sw.WriteLine($"cellsize      {cellsize.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                sw.WriteLine($"NODATA_value  {NODATA_value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

                // Write raster data (top row first, ESRI ASCII convention)
                for (int r = 0; r < nrows; r++)
                {
                    string[] parts = new string[ncols];
                    for (int c = 0; c < ncols; c++)
                    {
                        parts[c] = raster[r, c].ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
                    }

                    string line = string.Join(" ", parts);
                    sw.WriteLine(line);
                }

            }
            // --- Copy matching .prj if present ---
            CopySiblingPrj(parentFile, outputFileName); 
        }

            /// <summary>
            /// If a .prj exists next to the parent .asc, copy it and rename to match the output file.
            /// Example: parent: C:\data\a.asc + C:\data\a.prj  ->  output: D:\out\b.asc + D:\out\b.prj
            /// </summary>
        private static void CopySiblingPrj(string parentAscPath, string outputAscPath)
        {
            try
            {
                string parentPrj = Path.ChangeExtension(parentAscPath, ".prj");
                if (!File.Exists(parentPrj))
                    return; // No .prj to copy; silently skip

                string outputPrj = Path.ChangeExtension(outputAscPath, ".prj");

                // Ensure destination directory exists
                string? outDir = Path.GetDirectoryName(outputPrj);
                if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                    Directory.CreateDirectory(outDir);

                File.Copy(parentPrj, outputPrj, overwrite: true);
                // If you want to be explicit about encoding (e.g., UTF-8), read & write text instead of raw copy:
                // File.WriteAllText(outputPrj, File.ReadAllText(parentPrj, Encoding.UTF8), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // Your call: either swallow, log, or rethrow. Here we keep it non-fatal but informative.
                Console.Error.WriteLine($"Warning: failed to copy PRJ file: {ex.Message}");
            }
        }


        private static void WriteGeoTiff(float[,] raster, string parentFile, string outputFileName)
        {
            if (raster == null || raster.GetLength(0) == 0 || raster.GetLength(1) == 0)
                throw new ArgumentException("Input raster is null or empty.");

            // Initialize GDAL
            Gdal.AllRegister();

            using var src = Gdal.Open(parentFile, Access.GA_ReadOnly);
            if (src is null)
                throw new Exception($"Could not open parent GeoTIFF: {parentFile}");

            int cols = src.RasterXSize;
            int rows = src.RasterYSize;

            // Validate dimensions
            if (rows != raster.GetLength(0) || cols != raster.GetLength(1))
                throw new Exception($"Input raster dimensions ({raster.GetLength(0)}x{raster.GetLength(1)}) do not match parent ({rows}x{cols}).");

            // Copy georeference
            double[] gt = new double[6];
            src.GetGeoTransform(gt);
            string wkt = src.GetProjectionRef();

            // Get NoData value safely (GDAL >=3.8 syntax)
            double noData = 0;
            int hasNoData = 0;
            var srcBand = src.GetRasterBand(1);
            srcBand.GetNoDataValue(out noData, out hasNoData);

            // Create output dataset
            var drv = Gdal.GetDriverByName("GTiff") ?? throw new Exception("GTiff driver not available.");
            string[] options = new[]
            {
                "TILED=YES",
                "COMPRESS=LZW",
                "PREDICTOR=3",
                "BIGTIFF=IF_SAFER"
            };

            using var dst = drv.Create(outputFileName, cols, rows, 1, DataType.GDT_Float32, options);
            if (dst is null)
                throw new Exception($"Failed to create output GeoTIFF: {outputFileName}");

            dst.SetGeoTransform(gt);
            if (!string.IsNullOrWhiteSpace(wkt))
                dst.SetProjection(wkt);

            var dstBand = dst.GetRasterBand(1);
            if (hasNoData != 0)
                dstBand.SetNoDataValue(noData);

            // Flatten float[][] → float[] (row-major, top-to-bottom)
            float[] buffer = new float[rows * cols];
            int idx = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    buffer[idx++] = raster[r, c];
                }
            }
            // Write raster
            CPLErr err = dstBand.WriteRaster(0, 0, cols, rows, buffer, cols, rows, 0, 0);
            if (err != CPLErr.CE_None)
                throw new Exception("Error writing GeoTIFF raster data.");

            dstBand.FlushCache();
            dst.FlushCache();

            // Optional: overviews for faster display
            // dst.BuildOverviews("NEAREST", new int[] { 2, 4, 8, 16 });
        }
        

        public readonly struct RasterMetadata
        {
            public float CellSize { get; init; }
            public float NoDataValue { get; init; }
            public float XllCorner { get; init; }
            public float YllCorner { get; init; }
        }

        public static RasterMetadata GetRasterMetadata(string fileLocation)
        {
            if (!IsValidInputFile(fileLocation))
            {
                throw new FileNotFoundException($"File '{fileLocation}' could not be loaded.", fileLocation);
            }

            string extension = Path.GetExtension(fileLocation).ToLowerInvariant();
            switch (extension)
            {
                case ".asc":
                    return ReadAsciiMetadata(fileLocation);
                case ".tif":
                case ".tiff":
                    return ReadGeoTiffMetadata(fileLocation);
                default:
                    throw new NotSupportedException(
                        $"File type {extension} is not a recognised raster format [.asc, .tif, .tiff].");
            }
        }

        private static RasterMetadata ReadAsciiMetadata(string filePath)
        {
            using var reader = new StreamReader(filePath);
            string? ncolsLine = reader.ReadLine();
            string? nrowsLine = reader.ReadLine();
            string? xllLine = reader.ReadLine();
            string? yllLine = reader.ReadLine();
            string? cellSizeLine = reader.ReadLine();
            string? noDataLine = reader.ReadLine();

            if (xllLine == null || yllLine == null || cellSizeLine == null)
            {
                throw new InvalidDataException("ASCII grid header is incomplete.");
            }

            float ParseHeaderValue(string line)
            {
                string[] parts = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    throw new InvalidDataException($"Malformed header line: '{line}'.");
                }
                return float.Parse(parts[1], CultureInfo.InvariantCulture);
            }

            return new RasterMetadata
            {
                CellSize = ParseHeaderValue(cellSizeLine),
                XllCorner = ParseHeaderValue(xllLine),
                YllCorner = ParseHeaderValue(yllLine),
                NoDataValue = noDataLine != null
                    ? ParseHeaderValue(noDataLine)
                    : float.NaN
            };
        }

        private static RasterMetadata ReadGeoTiffMetadata(string filePath)
        {
            Gdal.AllRegister();

            using Dataset dataset = Gdal.Open(filePath, Access.GA_ReadOnly)
                ?? throw new InvalidOperationException($"Failed to open GeoTIFF: {filePath}");

            double[] geoTransform = new double[6];
            dataset.GetGeoTransform(geoTransform);

            double cellSizeX = geoTransform[1];
            double cellSizeY = Math.Abs(geoTransform[5]);
            double cellSizeScalar = Math.Abs(cellSizeX) < 1e-9 || Math.Abs(cellSizeX - cellSizeY) > 1e-6
                ? Math.Sqrt(cellSizeX * cellSizeX + cellSizeY * cellSizeY) / Math.Sqrt(2.0)
                : Math.Abs(cellSizeX);

            float cellSize = (float)cellSizeScalar;
            float xllCorner = (float)geoTransform[0];
            float yllCorner = (float)(geoTransform[3] + geoTransform[5] * dataset.RasterYSize);

            Band band = dataset.GetRasterBand(1);
            band.GetNoDataValue(out double nodataValue, out int hasNoData);
            float nodata = hasNoData != 0 ? (float)nodataValue : float.NaN;

            return new RasterMetadata
            {
                CellSize = cellSize,
                XllCorner = xllCorner,
                YllCorner = yllCorner,
                NoDataValue = nodata
            };
        }
    }
}