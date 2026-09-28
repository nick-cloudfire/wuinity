using System;
using System.Globalization;
using System.IO;

namespace WUInity.Workflow
{
    /// <summary>
    /// What the workflow needs to know about a raster without reading its cells: size, band count, and where
    /// its lower-left corner is in its own CRS.
    /// </summary>
    /// <remarks>
    /// Read from the file header directly - the TIFF directory for a GeoTIFF, the six header lines of an ESRI
    /// ASCII grid, the two integers of a painted-areas file - rather than through GDAL: these questions are
    /// asked about a dozen files every time the workflow refreshes, answering them must not load a native
    /// library, and the model has to run from a plain console program too.
    /// </remarks>
    public sealed class RasterInfo
    {
        public int Width;
        public int Height;
        public int Bands = 1;

        /// <summary>False when the file carries no georeferencing this reader understands.</summary>
        public bool HasGeoTransform;
        public double XllCorner;
        public double YllCorner;
        public double CellSize;

        public string Describe()
        {
            string size = $"{Width} x {Height}";
            if (HasGeoTransform && CellSize > 0) size += $" cells of {CellSize:0.##} m";
            if (Bands > 1) size += $", {Bands} bands";
            return size;
        }

        /// <summary>Same cell count as <paramref name="other"/>.</summary>
        public bool SameSize(RasterInfo other) => other != null && other.Width == Width && other.Height == Height;

        /// <summary>
        /// Same grid as <paramref name="other"/>: cell count, cell size and corner (to half a cell) - when both
        /// say where they are. Without georeferencing on one side, the cell count is all there is to compare.
        /// </summary>
        public bool SameGrid(RasterInfo other, double otherCornerShiftX = 0.0, double otherCornerShiftY = 0.0)
        {
            if (!SameSize(other)) return false;
            if (!HasGeoTransform || !other.HasGeoTransform) return true;
            double tolerance = 0.5 * Math.Max(CellSize, other.CellSize);
            return Math.Abs(CellSize - other.CellSize) < 1e-3
                   && Math.Abs(XllCorner - (other.XllCorner + otherCornerShiftX)) < tolerance
                   && Math.Abs(YllCorner - (other.YllCorner + otherCornerShiftY)) < tolerance;
        }

        /// <summary>A GeoTIFF or an ESRI ASCII grid, by extension (anything that is not .tif/.tiff is read as ASCII).</summary>
        public static RasterInfo Read(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".tif" || ext == ".tiff" ? ReadTiff(path) : ReadAscii(path);
        }

        // ------------------------------------------------------------------ ESRI ASCII grid

        public static RasterInfo ReadAscii(string path)
        {
            var info = new RasterInfo();
            bool centre = false;
            using (var reader = new StreamReader(path))
            {
                for (int i = 0; i < 6; ++i)
                {
                    string line = reader.ReadLine();
                    if (line == null) break;
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    string key = parts[0].ToLowerInvariant();
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v);
                    switch (key)
                    {
                        case "ncols": info.Width = (int)v; break;
                        case "nrows": info.Height = (int)v; break;
                        case "xllcorner": info.XllCorner = v; break;
                        case "yllcorner": info.YllCorner = v; break;
                        case "xllcenter": info.XllCorner = v; centre = true; break;
                        case "yllcenter": info.YllCorner = v; centre = true; break;
                        case "cellsize": info.CellSize = v; break;
                    }
                }
            }

            if (centre)
            {
                info.XllCorner -= 0.5 * info.CellSize;
                info.YllCorner -= 0.5 * info.CellSize;
            }
            info.HasGeoTransform = info.CellSize > 0;
            if (info.Width <= 0 || info.Height <= 0) throw new InvalidDataException("Not an ESRI ASCII grid: " + path);
            return info;
        }

        // ------------------------------------------------------------------ GeoTIFF

        private const int TagImageWidth = 256;
        private const int TagImageLength = 257;
        private const int TagSamplesPerPixel = 277;
        private const int TagModelPixelScale = 33550;
        private const int TagModelTiepoint = 33922;

        /// <summary>
        /// Width, height, band count (SamplesPerPixel, which is how GDAL stores a multi-band GeoTIFF) and the
        /// corner from ModelTiepoint + ModelPixelScale. Classic TIFF and BigTIFF, either byte order.
        /// </summary>
        public static RasterInfo ReadTiff(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                byte b0 = br.ReadByte(), b1 = br.ReadByte();
                bool little;
                if (b0 == 'I' && b1 == 'I') little = true;
                else if (b0 == 'M' && b1 == 'M') little = false;
                else throw new InvalidDataException("Not a TIFF: " + path);

                var r = new Reader(br, little);
                ushort magic = r.U16();
                bool big;
                long ifd;
                if (magic == 42)
                {
                    big = false;
                    ifd = r.U32();
                }
                else if (magic == 43)
                {
                    big = true;
                    r.U16(); //offset size, always 8
                    r.U16();
                    ifd = (long)r.U64();
                }
                else
                {
                    throw new InvalidDataException("Not a TIFF: " + path);
                }

                fs.Position = ifd;
                long entries = big ? (long)r.U64() : r.U16();
                var info = new RasterInfo();
                double[] scale = null, tie = null;

                for (long e = 0; e < entries; ++e)
                {
                    ushort tag = r.U16();
                    ushort type = r.U16();
                    long count = big ? (long)r.U64() : r.U32();
                    long valuePosition = fs.Position;
                    int typeSize = TypeSize(type);
                    long bytes = typeSize * count;
                    int inline = big ? 8 : 4;
                    long dataPosition = bytes <= inline ? valuePosition : (big ? (long)r.U64() : r.U32());

                    if (tag == TagImageWidth || tag == TagImageLength || tag == TagSamplesPerPixel)
                    {
                        fs.Position = dataPosition;
                        long v = type == 3 ? r.U16() : type == 16 ? (long)r.U64() : r.U32();
                        if (tag == TagImageWidth) info.Width = (int)v;
                        else if (tag == TagImageLength) info.Height = (int)v;
                        else info.Bands = (int)v;
                    }
                    else if ((tag == TagModelPixelScale || tag == TagModelTiepoint) && type == 12 && count <= 64)
                    {
                        fs.Position = dataPosition;
                        var values = new double[count];
                        for (int i = 0; i < count; ++i) values[i] = r.F64();
                        if (tag == TagModelPixelScale) scale = values; else tie = values;
                    }

                    fs.Position = valuePosition + inline;
                }

                if (scale != null && tie != null && scale.Length >= 2 && tie.Length >= 6 && scale[0] > 0)
                {
                    //Tie point (i, j) in the raster to (x, y) in the CRS; north-up with square cells assumed, as
                    //everything downstream does.
                    double left = tie[3] - tie[0] * scale[0];
                    double top = tie[4] + tie[1] * scale[1];
                    info.XllCorner = left;
                    info.YllCorner = top - info.Height * scale[1];
                    info.CellSize = scale[0];
                    info.HasGeoTransform = true;
                }

                if (info.Width <= 0 || info.Height <= 0) throw new InvalidDataException("TIFF without dimensions: " + path);
                return info;
            }
        }

        private static int TypeSize(ushort type)
        {
            switch (type)
            {
                case 1: case 2: case 6: case 7: return 1;
                case 3: case 8: return 2;
                case 4: case 9: case 11: case 13: return 4;
                case 5: case 10: case 12: case 16: case 17: case 18: return 8;
                default: return 1;
            }
        }

        private sealed class Reader
        {
            private readonly BinaryReader _br;
            private readonly bool _little;
            public Reader(BinaryReader br, bool little) { _br = br; _little = little; }

            private byte[] Bytes(int n)
            {
                byte[] b = _br.ReadBytes(n);
                if (b.Length < n) throw new EndOfStreamException();
                if (BitConverter.IsLittleEndian != _little) Array.Reverse(b);
                return b;
            }

            public ushort U16() => BitConverter.ToUInt16(Bytes(2), 0);
            public uint U32() => BitConverter.ToUInt32(Bytes(4), 0);
            public ulong U64() => BitConverter.ToUInt64(Bytes(8), 0);
            public double F64() => BitConverter.ToDouble(Bytes(8), 0);
        }
    }

    /// <summary>The header and painted-cell counts of a painted-areas (.gfi) file.</summary>
    public sealed class PaintedAreasInfo
    {
        public int Width;
        public int Height;
        public int WuiCells;
        public int IgnitionAreaCells;
        public int InitialIgnitionCells;

        /// <summary>Where the painting's grid lies, when the file records it (paintings saved since the record existed).</summary>
        public PREACT.GraphicalFireInput.PaintedGrid Grid;

        public bool SameSize(RasterInfo grid) => grid != null && grid.Width == Width && grid.Height == Height;

        /// <summary>
        /// Why the painting's record says <paramref name="grid"/> is not the grid it was painted on, or null when it is
        /// or cannot tell (no record, or a raster without georeferencing). The case builder's rule, without the CRS,
        /// which this header reader does not know.
        /// </summary>
        public string DescribeMismatch(RasterInfo grid)
        {
            if (Grid == null || grid == null || !grid.HasGeoTransform) return null;
            return Grid.DescribeMismatch(grid.XllCorner, grid.YllCorner, grid.CellSize, 0);
        }

        /// <summary>The painting's size, and - when it records one - its place: what the case build accepts.</summary>
        public bool OnGrid(RasterInfo grid) => SameSize(grid) && DescribeMismatch(grid) == null;

        /// <summary>
        /// Two little-endian int32 (columns, rows), then one byte per cell for each of four masks: WUI area,
        /// random-ignition area, initial ignition, manual trigger buffer (GraphicalFireInput's format).
        /// </summary>
        public static PaintedAreasInfo Read(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var br = new BinaryReader(fs))
            {
                var info = new PaintedAreasInfo { Width = br.ReadInt32(), Height = br.ReadInt32() };
                if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 200_000_000L)
                {
                    throw new InvalidDataException("Not a painted-areas file: " + path);
                }

                int cells = info.Width * info.Height;
                info.WuiCells = CountSet(br, cells);
                info.IgnitionAreaCells = CountSet(br, cells);
                info.InitialIgnitionCells = CountSet(br, cells);
                if (fs.Length >= 8L + 4L * cells)
                {
                    fs.Seek(8L + 4L * cells, SeekOrigin.Begin);
                    info.Grid = PREACT.GraphicalFireInput.ReadPaintedGrid(br);
                }
                return info;
            }
        }

        private static int CountSet(BinaryReader br, int cells)
        {
            byte[] mask = br.ReadBytes(cells);
            int n = 0;
            for (int i = 0; i < mask.Length; ++i)
            {
                if (mask[i] != 0) ++n;
            }
            return n;
        }
    }
}
