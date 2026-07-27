using System;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// Turns the masks painted in Unity's <c>Painter</c> into the georeferenced rasters the ELMFIRE
    /// case pipeline consumes.
    ///
    /// The brush itself already exists (<c>WUInity/Assets/WUInity/Core/Painter.cs</c>, modes
    /// <c>WUIArea</c> / <c>RandomIgnitionArea</c> / <c>InitialIgnition</c>) and what it paints is
    /// not merely a Unity texture — it is written through to <c>WildfireData</c>'s <c>bool[]</c>
    /// arrays and persisted by <see cref="Input.GraphicalFireInput"/>. This reads that file back
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
        /// <summary>The four masks <see cref="Input.GraphicalFireInput.SaveGraphicalFireInput"/> writes, in file order.</summary>
        public class Masks
        {
            public int Ncols, Nrows;
            public bool[] WuiArea, RandomIgnition, InitialIgnition, ManualTriggerBuffer;

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
        /// Reads a graphical-fire-input file without needing the LCP loaded.
        /// <see cref="Input.GraphicalFireInput.LoadGraphicalFireInput"/> requires a
        /// <c>LandscapeData</c> purely to check the dimensions match, which a case builder running
        /// before the case exists cannot supply — the file states its own dimensions in its first
        /// two integers, so they are read from there and checked against the grid instead.
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

        /// <summary>
        /// The centre of the painted initial-ignition cells, in the target grid's own coordinates —
        /// ready for the namelist's <c>X_IGN(1)</c>/<c>Y_IGN(1)</c>.
        ///
        /// Taken as the centroid of every painted cell rather than the first one found, so a brush
        /// stroke a few cells wide ignites at its middle rather than at whichever corner happened
        /// to be scanned first. Returns false when nothing was painted.
        /// </summary>
        public static bool TryGetIgnitionPoint(bool[] mask, Masks shape, MasterGrid sourceGrid, MasterGrid targetGrid,
                                               out double x, out double y)
        {
            x = y = 0;
            if (mask == null) return false;

            //Resolved through the warped raster rather than by transforming the painted cell's
            //coordinates directly: that reuses the one reprojection path the rest of the pipeline
            //uses, so the point cannot land somewhere the exported mask does not agree with.
            string temp = Path.Combine(Path.GetTempPath(), "preact_initial_ignition_" + Guid.NewGuid().ToString("N") + ".tif");
            try
            {
                Export(mask, shape, sourceGrid, targetGrid, temp);

                float[,] warped = AscRaster.ReadGeoTiff(temp, out AscRaster.Header _, out bool ok);
                if (!ok || warped == null) return false;

                double sumX = 0, sumY = 0;
                int n = 0;
                for (int cx = 0; cx < targetGrid.Header.Ncols; ++cx)
                {
                    for (int cy = 0; cy < targetGrid.Header.Nrows; ++cy)
                    {
                        if (warped[cx, cy] <= 0f) continue;
                        sumX += cx;
                        sumY += cy;
                        ++n;
                    }
                }

                if (n == 0) return false;

                //cell centres, not corners
                double cs = targetGrid.Header.CellSize;
                x = targetGrid.XMin + (sumX / n + 0.5) * cs;
                y = targetGrid.YMin + (sumY / n + 0.5) * cs;
                return true;
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
