using System;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// Turns the masks painted in Unity's <c>Painter</c> into the georeferenced rasters the ELMFIRE
    /// case pipeline consumes.
    ///
    /// The brush itself already exists (<c>WUInity/Assets/WUInity/Core/Painter.cs</c>, which paints the
    /// random-ignition area) and what it paints is not merely a Unity texture — it is written through to
    /// <c>WildfireData</c>'s <c>bool[]</c> arrays and persisted by <see cref="Input.GraphicalFireInput"/>. An older
    /// file's WUI area and initial ignition are read here only to be noted: neither is placed any more. This reads that file back
    /// and reprojects the masks, so the whole path runs outside Unity: paint in the editor, save,
    /// then build the case from the command line.
    ///
    /// The painted masks live on the <b>LCP grid</b> (the landscape file's own CRS, extent and cell
    /// size), which is generally not the case's master grid — on Mati the LCP is EPSG:32635 at 27 m
    /// while the generated case is EPSG:32634 at 30 m. Every export therefore goes through
    /// <see cref="RasterHarmonizer.WarpToGrid"/> with nearest-neighbour resampling; interpolating a
    /// mask would invent fractional cells along every edge.
    /// </summary>
    public static class PaintedMaskExporter
    {
        /// <summary>The four places of a painting, in file order (the WUI area and initial ignition empty in a file written now).</summary>
        public class Masks
        {
            public int Ncols, Nrows;
            public bool[] WuiArea, RandomIgnition, InitialIgnition, ManualTriggerBuffer;

            /// <summary>Where the painting's grid lies, when the file records it; null for older files.</summary>
            public GraphicalFireInput.PaintedGrid Grid;

            public bool Any(bool[] mask)
            {
                if (mask == null) return false;
                foreach (bool b in mask) if (b) return true;
                return false;
            }

            public int Count(bool[] mask)
            {
                if (mask == null) return 0;
                int n = 0;
                foreach (bool b in mask) if (b) ++n;
                return n;
            }
        }

        /// <summary>
        /// Reads a graphical-fire-input file into the shape this exporter works in.
        ///
        /// The file states its own dimensions in its first two integers, so they are read from there
        /// and checked against the target grid rather than against a <c>LandscapeData</c> — a case
        /// builder running before the case exists has none to check against.
        /// </summary>
        public static Masks Load(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                var m = new Masks
                {
                    Ncols = br.ReadInt32(),
                    Nrows = br.ReadInt32(),
                };

                if (m.Ncols <= 0 || m.Nrows <= 0)
                {
                    throw new InvalidDataException($"{path} declares a {m.Ncols}x{m.Nrows} grid.");
                }

                int count = m.Ncols * m.Nrows;
                m.WuiArea = ReadMask(br, count);
                m.RandomIgnition = ReadMask(br, count);
                m.InitialIgnition = ReadMask(br, count);
                m.ManualTriggerBuffer = ReadMask(br, count);
                if (m.ManualTriggerBuffer != null) m.Grid = GraphicalFireInput.ReadPaintedGrid(br);
                return m;
            }
        }

        /// <summary>Reads one mask block; returns null once the file runs out, since older files may hold fewer.</summary>
        private static bool[] ReadMask(BinaryReader br, int count)
        {
            byte[] bytes = br.ReadBytes(count * sizeof(bool));
            if (bytes.Length < count) return null;

            var result = new bool[count];
            Buffer.BlockCopy(bytes, 0, result, 0, count);
            return result;
        }

        /// <summary>
        /// Writes one painted mask as a 1/0 GeoTIFF on <paramref name="targetGrid"/>.
        ///
        /// <paramref name="sourceGrid"/> is the grid the mask was painted on — read it from the
        /// landscape raster the Unity session used, so the georeferencing comes from the file
        /// rather than being reconstructed.
        /// </summary>
        public static void Export(bool[] mask, Masks shape, MasterGrid sourceGrid, MasterGrid targetGrid, string outputPath)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));

            if (sourceGrid.Header.Ncols != shape.Ncols || sourceGrid.Header.Nrows != shape.Nrows)
            {
                throw new InvalidDataException(
                    $"The painted masks are {shape.Ncols}x{shape.Nrows} but the grid they are being placed on is " +
                    $"{sourceGrid.Header.Ncols}x{sourceGrid.Header.Nrows}. Point --painted-grid at the landscape " +
                    "raster the painting was done against.");
            }

            //bool[] is indexed x + y * ncols with y running north, the same lower-left-origin
            //convention AscRaster.Read produces and WriteBand consumes (confirmed against
            //EvacuationManager.LoadWuiAreaMask, which is what reads these masks back).
            var data = new float[shape.Ncols, shape.Nrows];
            for (int y = 0; y < shape.Nrows; ++y)
            {
                for (int x = 0; x < shape.Ncols; ++x)
                {
                    data[x, y] = mask[x + y * shape.Ncols] ? 1f : 0f;
                }
            }

            string temp = outputPath + ".painted.tmp.tif";
            try
            {
                GeoTiffRasterWriter.WriteBand(sourceGrid, data, temp);
                RasterHarmonizer.WarpToGrid(temp, outputPath, targetGrid, "near");
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
