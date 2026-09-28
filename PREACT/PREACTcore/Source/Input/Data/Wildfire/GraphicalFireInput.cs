//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.IO;

namespace PREACT
{
    /// <summary>
    /// The four masks painted on the fire grid - WUI area, random ignition area, initial ignition and
    /// a manually painted trigger buffer - as a single binary file: two integers for the grid, then one
    /// byte per cell per mask.
    /// </summary>
    public static class GraphicalFireInput
    {
        /// <summary>The name a scenario's masks are written under when the caller has no preference.</summary>
        public const string DefaultFileName = "painted_fire_areas.gfi";

        /// <summary>
        /// Writes the four masks on a grid of <paramref name="xCount"/> x <paramref name="yCount"/> cells.
        ///
        /// The grid is passed in rather than read from the landscape, which is what this used to do. The
        /// masks are painted on whatever grid the painter resolved, and for the module the trigger
        /// pipeline uses that is the imported arrival time raster, not a landscape - a scenario with an
        /// imported fire has no LandscapeData at all, so asking it for a cell count threw. The two have
        /// to be the same grid the masks were painted on, or the file describes cells that are not the
        /// ones that were painted.
        /// </summary>
        public static void SaveGraphicalFireInput(string filePath, Input.WildfireData fireData, int xCount, int yCount)
        {
            SaveGraphicalFireInput(filePath, fireData, xCount, yCount, null);
        }

        /// <summary>
        /// Where the grid a painting was made on lies: its south-west corner in its CRS, its cell size, and the CRS
        /// as an EPSG code (0 when unknown).
        /// </summary>
        public sealed class PaintedGrid
        {
            public double XllCorner, YllCorner, CellSize;
            public int EpsgCode;
        }

        /// <summary>Marks the optional trailer after the four masks that says where their grid lies.</summary>
        public const string GridTrailerTag = "GFIGRID1";

        /// <summary>
        /// As <see cref="SaveGraphicalFireInput(string, Input.WildfireData, int, int)"/>, and records where the grid
        /// lies after the masks when <paramref name="grid"/> is given.
        /// </summary>
        /// <remarks>
        /// A painting used to be recognised by its cell count alone, so a domain moved by a whole number of cells
        /// with its size unchanged placed the old painting on the new ground without a word (review MI-3). The
        /// position is a trailer: every reader of the format stops after the masks, so files with it open in older
        /// builds, and files without it are still matched by size.
        /// </remarks>
        public static void SaveGraphicalFireInput(string filePath, Input.WildfireData fireData, int xCount, int yCount,
            PaintedGrid grid)
        {
            int cells = xCount * yCount;

            using (FileStream fs = new FileStream(filePath, FileMode.Create))
            {
                using (BinaryWriter bw = new BinaryWriter(fs))
                {
                    bw.Write(xCount);
                    bw.Write(yCount);
                    bw.Write(GetBytes(fireData.WuiArea, cells));
                    bw.Write(GetBytes(fireData.RandomIgnition, cells));
                    bw.Write(GetBytes(fireData.InitialIgnition, cells));
                    bw.Write(GetBytes(fireData.ManualTriggerBuffer, cells));

                    if (grid != null && grid.CellSize > 0.0)
                    {
                        bw.Write(System.Text.Encoding.ASCII.GetBytes(GridTrailerTag));
                        bw.Write(grid.XllCorner);
                        bw.Write(grid.YllCorner);
                        bw.Write(grid.CellSize);
                        bw.Write(grid.EpsgCode);
                    }
                }
            }
        }

        /// <summary>
        /// Where a painting's grid lies, read from the trailer after its four masks, or null when the file does
        /// not record it (every file written before the trailer existed).
        /// </summary>
        public static PaintedGrid ReadPaintedGrid(BinaryReader afterMasks)
        {
            try
            {
                byte[] tag = afterMasks.ReadBytes(GridTrailerTag.Length);
                if (tag.Length != GridTrailerTag.Length || System.Text.Encoding.ASCII.GetString(tag) != GridTrailerTag)
                {
                    return null;
                }

                var grid = new PaintedGrid
                {
                    XllCorner = afterMasks.ReadDouble(),
                    YllCorner = afterMasks.ReadDouble(),
                    CellSize = afterMasks.ReadDouble(),
                    EpsgCode = afterMasks.ReadInt32(),
                };
                return grid.CellSize > 0.0 ? grid : null;
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }

        /// <summary>
        /// The masks as bytes, one per cell. A mask that was never painted is absent rather than empty,
        /// and is written as all-false so every mask occupies its declared place in the file - a short
        /// file would make the reader take the next mask's bytes for this one's.
        /// </summary>
        static byte[] GetBytes(bool[] values, int cells)
        {
            byte[] result = new byte[cells];
            if (values == null)
            {
                return result;
            }

            System.Buffer.BlockCopy(values, 0, result, 0, System.Math.Min(values.Length, cells));
            return result;
        }

        static bool[] GetBools(byte[] values, int length)
        {
            bool[] result = new bool[length];
            System.Buffer.BlockCopy(values, 0, result, 0, values.Length);
            return result;
        }

        /// <summary>
        /// Reads the masks and the grid they were painted on, taking the grid from the file itself.
        ///
        /// Nothing is checked against a landscape here, because the caller generally has nothing to check
        /// against: the masks may have been painted on an imported fire's arrival times, and a scenario
        /// loading them has not necessarily resolved that grid yet. Whoever is going to use the masks
        /// compares the cell count it expects with the one reported here, which is a comparison it can
        /// actually explain.
        /// </summary>
        public static void LoadGraphicalFireInput(string file, out int ncols, out int nrows,
            out bool[] wuiArea, out bool[] randomIgnitionArea, out bool[] initialIgnitionIndices,
            out bool[] triggerBufferIndices, out bool success)
        {
            success = false;
            ncols = nrows = 0;
            wuiArea = randomIgnitionArea = initialIgnitionIndices = triggerBufferIndices = null;

            if (!File.Exists(file))
            {
                Engine.Message(null, Engine.LogType.Warning, "Could not find painted fire areas: " + file + ".");
                return;
            }

            //Read-only, and letting others read too: a painting shared read-only, or one open elsewhere for reading,
            //failed to load because it was opened for writing as well.
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (BinaryReader br = new BinaryReader(fs))
                {
                    ncols = br.ReadInt32();
                    nrows = br.ReadInt32();

                    if (ncols <= 0 || nrows <= 0)
                    {
                        Engine.Message(null, Engine.LogType.Warning,
                            $"{file} declares a {ncols}x{nrows} grid, so nothing could be read from it.");
                        ncols = nrows = 0;
                        return;
                    }

                    int dataSize = ncols * nrows;

                    byte[] b = br.ReadBytes(dataSize);
                    if (b.Length < dataSize)
                    {
                        Engine.Message(null, Engine.LogType.Warning,
                            $"{file} is shorter than the {ncols}x{nrows} grid it declares.");
                        ncols = nrows = 0;
                        return;
                    }
                    wuiArea = GetBools(b, dataSize);

                    randomIgnitionArea = ReadMask(br, dataSize);
                    initialIgnitionIndices = ReadMask(br, dataSize);
                    triggerBufferIndices = ReadMask(br, dataSize);

                    success = true;
                }
            }
        }

        /// <summary>One mask, or an empty one once the file runs out - older files hold fewer than four.</summary>
        private static bool[] ReadMask(BinaryReader br, int dataSize)
        {
            byte[] b = br.ReadBytes(dataSize);
            return b.Length < dataSize ? new bool[dataSize] : GetBools(b, dataSize);
        }

        public static void LoadGraphicalFireInput(string file, Wildfire.LandscapeData lcpData, out bool[] wuiArea, out bool[] randomIgnitionArea, out bool[] initialIgnitionIndices, out bool[] triggerBufferIndices, out bool success)
        {
            LoadGraphicalFireInput(file, out int ncols, out int nrows,
                out wuiArea, out randomIgnitionArea, out initialIgnitionIndices, out triggerBufferIndices, out success);

            if (success && (ncols != lcpData.GetCellCountX() || nrows != lcpData.GetCellCountY()))
            {
                Engine.Message(null, Engine.LogType.Warning, "Could read GFI data but there was a mismatch with the LCP file colums/rows, creating empty default.");
                success = false;
            }

            if (!success)
            {
                CreateDefault(lcpData, out wuiArea, out randomIgnitionArea, out initialIgnitionIndices, out triggerBufferIndices);
            }
        }

        private static void CreateDefault(Wildfire.LandscapeData lcpData, out bool[] wuiArea, out bool[] randomIgnitionArea, out bool[] initialIgnitionIndices, out bool[] triggerBufferIndices)
        {
            //LCP file has already been read, use that for dimensions
            int xDim = lcpData.GetCellCountX();
            int yDim = lcpData.GetCellCountY();
            wuiArea = new bool[xDim * yDim];
            randomIgnitionArea = new bool[xDim * yDim];
            initialIgnitionIndices = new bool[xDim * yDim];
            triggerBufferIndices = new bool[xDim * yDim];
        }
    }
}
