//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OSGeo.GDAL;
using OSGeo.OSR;

namespace PREACT.Utility
{
    /// <summary>
    /// Moves a painting (a <c>.gfi</c>: the ignition area, and the manual trigger buffer nothing paints) from the
    /// georeferenced grid it was painted on onto another one - in practice from an old landscape raster onto the fire
    /// case's <c>dem.tif</c>, which is the grid of record for an ELMFIRE scenario. An older painting's WUI area and
    /// initial ignition are counted and left out: the WUI area is the evacuation groups', and an initial ignition is an
    /// ignition point, which the scenario's load makes of it (and which a moved file would only make again elsewhere).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>.gfi</c> holds cell counts and one byte per cell per mask, and no georeferencing at all: which ground
    /// it describes is only known from the raster it was painted on. So both grids are read from rasters, and
    /// the painting is only ever moved between two grids whose georeferencing is on disk.
    /// </para>
    /// <para>
    /// Nearest cell by georeferenced cell centres: every cell of the new grid takes the painted value of the old
    /// cell its centre falls in (transformed into the old grid's CRS when the two differ). That keeps the
    /// painted area to within one cell along its edge - Mati's 7383 WUI cells of 27.6 m were ~6250 cells of 30 m -
    /// and invents no partial cells.
    /// </para>
    /// <para>
    /// The painting is written to a new file beside the old one and the old file is never touched, so moving it
    /// cannot lose anything; what points the scenario at the new file is the caller's business. The new file
    /// keeps the old one's modification time, since what was painted - and so whether a case's masks are older
    /// than it - did not change.
    /// </para>
    /// </remarks>
    public static class PaintedMaskResampler
    {
        /// <summary>A north-up raster grid: size, lower-left corner and cell steps in its own CRS.</summary>
        public sealed class Grid
        {
            public int Ncols;
            public int Nrows;

            /// <summary>Lower-left corner, in the grid's CRS.</summary>
            public double XMin;
            public double YMin;

            /// <summary>Cell width (east) and height (north), both positive. Kept apart: not every raster is square.</summary>
            public double CellWidth;
            public double CellHeight;

            /// <summary>The CRS as WKT, or empty when the raster has none.</summary>
            public string Wkt = string.Empty;

            /// <summary>"EPSG:32634" style, or null when it could not be identified.</summary>
            public string Epsg;

            /// <summary>The raster the grid was read from.</summary>
            public string Path;

            /// <summary>
            /// The grid as a painting records it (<see cref="GraphicalFireInput.PaintedGrid"/>): corner, cell width and
            /// EPSG code - the same numbers the case builder compares a painting's record with.
            /// </summary>
            public GraphicalFireInput.PaintedGrid ToPaintedGrid()
            {
                return new GraphicalFireInput.PaintedGrid
                {
                    XllCorner = XMin,
                    YllCorner = YMin,
                    CellSize = CellWidth,
                    EpsgCode = GraphicalFireInput.PaintedGrid.EpsgNumber(Epsg),
                };
            }

            /// <summary>Why <paramref name="recorded"/> says a painting was not made on this grid, or null.</summary>
            public string DescribeMismatch(GraphicalFireInput.PaintedGrid recorded)
            {
                return recorded?.DescribeMismatch(XMin, YMin, CellWidth, GraphicalFireInput.PaintedGrid.EpsgNumber(Epsg));
            }

            public double XMax => XMin + Ncols * CellWidth;
            public double YMax => YMin + Nrows * CellHeight;
            public double CellArea => CellWidth * CellHeight;

            public string Describe()
            {
                return string.Format(CultureInfo.InvariantCulture, "{0} x {1} cells of {2:0.##} x {3:0.##} m{4}",
                    Ncols, Nrows, CellWidth, CellHeight, string.IsNullOrEmpty(Epsg) ? string.Empty : ", " + Epsg);
            }

            /// <summary>
            /// Reads the grid of a raster: its geotransform and projection. Refuses a rotated or south-up raster,
            /// whose cells are not where this class assumes.
            /// </summary>
            public static Grid FromRaster(string path)
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    throw new FileNotFoundException("There is no raster to read a grid from at " + path + ".", path);
                }

                Gdal.AllRegister();
                using (Dataset ds = Gdal.Open(path, Access.GA_ReadOnly))
                {
                    if (ds == null)
                    {
                        throw new InvalidDataException("GDAL could not open " + path + " as a raster.");
                    }

                    double[] gt = new double[6];
                    ds.GetGeoTransform(gt);
                    if (gt[2] != 0.0 || gt[4] != 0.0)
                    {
                        throw new InvalidDataException($"{path} is rotated (geotransform skew {gt[2]}, {gt[4]}); a painting "
                            + "can only be moved between north-up grids.");
                    }
                    if (gt[1] <= 0.0 || gt[5] >= 0.0)
                    {
                        throw new InvalidDataException($"{path} is not a north-up raster (cell steps {gt[1]}, {gt[5]}).");
                    }

                    var grid = new Grid
                    {
                        Ncols = ds.RasterXSize,
                        Nrows = ds.RasterYSize,
                        CellWidth = gt[1],
                        CellHeight = -gt[5],
                        XMin = gt[0],
                        YMin = gt[3] + gt[5] * ds.RasterYSize,
                        Wkt = ds.GetProjection() ?? string.Empty,
                        Path = path,
                    };

                    if (!string.IsNullOrEmpty(grid.Wkt))
                    {
                        using (var srs = new SpatialReference(grid.Wkt))
                        {
                            srs.AutoIdentifyEPSG();
                            string code = srs.GetAuthorityCode(null);
                            if (!string.IsNullOrEmpty(code)) grid.Epsg = "EPSG:" + code;
                        }
                    }

                    return grid;
                }
            }
        }

        /// <summary>One mask's painted cells on either grid, and the ground they cover.</summary>
        public struct MaskCount
        {
            public int Before;
            public int After;
            public double AreaBeforeSquareMetres;
            public double AreaAfterSquareMetres;

            /// <summary>Painted cells of the old grid whose centre is outside the new grid: that part is cut off.</summary>
            public int OutsideTarget;
        }

        /// <summary>What <see cref="ResampleFile"/> did.</summary>
        public sealed class Result
        {
            public string SourceFile;
            public string OutputFile;
            public Grid Source;
            public Grid Target;
            public MaskCount IgnitionArea;
            public MaskCount TriggerBuffer;

            /// <summary>An older painting's WUI area and initial ignition, in cells: left out of the moved file.</summary>
            public int LeftOutWuiCells, LeftOutInitialIgnitionCells;

            /// <summary>True when the two grids are in different CRSs and every centre was reprojected.</summary>
            public bool Reprojected;

            /// <summary>What happened, one line per mask that has anything painted, for a log or a console.</summary>
            public List<string> Describe()
            {
                var lines = new List<string>
                {
                    $"Moved the painting in {System.IO.Path.GetFileName(SourceFile)} from {Source.Describe()} "
                    + $"({System.IO.Path.GetFileName(Source.Path)}) onto {Target.Describe()} ({System.IO.Path.GetFileName(Target.Path)})"
                    + (Reprojected ? ", reprojecting every cell centre" : string.Empty) + ".",
                };
                Line(lines, "ignition area", IgnitionArea);
                Line(lines, "trigger buffer", TriggerBuffer);
                if (LeftOutWuiCells > 0)
                {
                    lines.Add($"  WUI area: {LeftOutWuiCells} cells, left out - the WUI area is the evacuation groups'.");
                }
                if (LeftOutInitialIgnitionCells > 0)
                {
                    lines.Add($"  initial ignition: {LeftOutInitialIgnitionCells} cells, left out - an initial ignition is an "
                              + "ignition point, which opening the scenario made of it.");
                }
                lines.Add("Written as " + System.IO.Path.GetFileName(OutputFile) + "; " + System.IO.Path.GetFileName(SourceFile)
                    + " is kept as it was.");
                return lines;
            }

            private static void Line(List<string> lines, string name, MaskCount c)
            {
                if (c.Before == 0 && c.After == 0) return;
                string line = string.Format(CultureInfo.InvariantCulture, "  {0}: {1} -> {2} cells ({3:0.000} -> {4:0.000} km2)",
                    name, c.Before, c.After, c.AreaBeforeSquareMetres / 1e6, c.AreaAfterSquareMetres / 1e6);
                if (c.OutsideTarget > 0)
                {
                    line += $"; {c.OutsideTarget} painted cell(s) lie outside the new grid and are cut off";
                }
                lines.Add(line + ".");
            }
        }

        /// <summary>
        /// The four places of a painting on one grid, indexed x + y * ncols with y running north. Only the ignition area and
        /// the trigger buffer are moved; the WUI area and initial ignition of an older painting are read to be counted.
        /// </summary>
        public sealed class Masks
        {
            public int Ncols;
            public int Nrows;
            public bool[] WuiArea;
            public bool[] RandomIgnition;
            public bool[] InitialIgnition;
            public bool[] ManualTriggerBuffer;

            public static int Count(bool[] mask)
            {
                if (mask == null) return 0;
                int n = 0;
                for (int i = 0; i < mask.Length; ++i)
                {
                    if (mask[i]) ++n;
                }
                return n;
            }
        }

        /// <summary>
        /// Moves every mask of <paramref name="masks"/> from <paramref name="source"/> onto <paramref name="target"/>.
        /// Pure apart from the CRS transform (GDAL's), which is only set up when the two grids' CRSs differ.
        /// </summary>
        public static Masks Resample(Masks masks, Grid source, Grid target, out bool reprojected)
        {
            if (masks == null) throw new ArgumentNullException(nameof(masks));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));

            if (masks.Ncols != source.Ncols || masks.Nrows != source.Nrows)
            {
                throw new InvalidDataException($"The painting is {masks.Ncols} x {masks.Nrows} cells, but the grid it is said to be "
                    + $"painted on ({System.IO.Path.GetFileName(source.Path)}) is {source.Ncols} x {source.Nrows}.");
            }

            int targetCells = target.Ncols * target.Nrows;

            //Where each target cell centre is in the source grid's CRS.
            double[] sx = new double[targetCells];
            double[] sy = new double[targetCells];
            for (int j = 0; j < target.Nrows; ++j)
            {
                double y = target.YMin + (j + 0.5) * target.CellHeight;
                for (int i = 0; i < target.Ncols; ++i)
                {
                    int k = i + j * target.Ncols;
                    sx[k] = target.XMin + (i + 0.5) * target.CellWidth;
                    sy[k] = y;
                }
            }

            reprojected = !SameCrs(source, target);
            if (reprojected)
            {
                Transform(target, source, sx, sy);
            }

            //The source cell under each target centre, or -1 outside the source grid.
            int[] sourceIndex = new int[targetCells];
            for (int k = 0; k < targetCells; ++k)
            {
                sourceIndex[k] = CellOf(source, sx[k], sy[k]);
            }

            //The retired WUI area and initial ignition are not moved (null: written empty).
            var result = new Masks { Ncols = target.Ncols, Nrows = target.Nrows };
            result.RandomIgnition = Sample(masks.RandomIgnition, sourceIndex, targetCells);
            result.ManualTriggerBuffer = Sample(masks.ManualTriggerBuffer, sourceIndex, targetCells);
            return result;
        }

        /// <summary>
        /// Reads the painting in <paramref name="gfiPath"/>, painted on the grid of <paramref name="sourceGridRaster"/>,
        /// moves it onto the grid of <paramref name="targetGridRaster"/> and writes it to <paramref name="outputGfi"/>.
        /// </summary>
        /// <remarks>
        /// Never overwrites: an existing <paramref name="outputGfi"/>, or one that is the painting itself, is refused.
        /// <see cref="NewFileName"/> gives a free name beside the painting. A painting that records where its grid lies
        /// is moved only from that grid, and the moved painting records the target grid, so the case builder (which
        /// checks the record) accepts it on that grid and nowhere else.
        /// </remarks>
        public static Result ResampleFile(string gfiPath, string sourceGridRaster, string targetGridRaster, string outputGfi)
        {
            if (string.IsNullOrEmpty(outputGfi)) throw new ArgumentException("No file to write the moved painting to.", nameof(outputGfi));
            if (File.Exists(outputGfi))
            {
                throw new IOException(outputGfi + " exists already; the moved painting is only ever written to a new file.");
            }

            GraphicalFireInput.LoadGraphicalFireInput(gfiPath, out int ncols, out int nrows,
                out bool[] wui, out bool[] ignitionArea, out bool[] initial, out bool[] buffer,
                out GraphicalFireInput.PaintedGrid recorded, out bool success);
            if (!success)
            {
                throw new InvalidDataException("Could not read the painting in " + gfiPath + ".");
            }

            var masks = new Masks
            {
                Ncols = ncols,
                Nrows = nrows,
                WuiArea = wui,
                RandomIgnition = ignitionArea,
                InitialIgnition = initial,
                ManualTriggerBuffer = buffer,
            };

            Grid source = Grid.FromRaster(sourceGridRaster);
            Grid target = Grid.FromRaster(targetGridRaster);

            //Checked once the sizes agree (Resample says so when they do not): a raster of the painting's size is not
            //the grid it was painted on when the painting records another place.
            string misplaced = masks.Ncols == source.Ncols && masks.Nrows == source.Nrows ? source.DescribeMismatch(recorded) : null;
            if (misplaced != null)
            {
                throw new InvalidDataException($"{System.IO.Path.GetFileName(gfiPath)} records that it was painted on {recorded.Describe()}; "
                    + $"{System.IO.Path.GetFileName(sourceGridRaster)} is its size but {misplaced}, so it is not the grid to move it from.");
            }

            Masks moved = Resample(masks, source, target, out bool reprojected);

            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputGfi));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            var data = new Input.WildfireData
            {
                RandomIgnition = moved.RandomIgnition,
                ManualTriggerBuffer = moved.ManualTriggerBuffer,
            };

            //Written beside the target name first, so a failure part-way leaves no half-written painting behind.
            string partial = outputGfi + ".partial";
            try
            {
                GraphicalFireInput.SaveGraphicalFireInput(partial, data, moved.Ncols, moved.Nrows, target.ToPaintedGrid());
                //As old as the painting it came from: nothing was painted, only placed on other cells, so masks a
                //build already made from the original are not stale against it.
                File.SetLastWriteTimeUtc(partial, File.GetLastWriteTimeUtc(gfiPath));
                File.Move(partial, outputGfi);
            }
            finally
            {
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            }

            return new Result
            {
                SourceFile = gfiPath,
                OutputFile = outputGfi,
                Source = source,
                Target = target,
                Reprojected = reprojected,
                IgnitionArea = CountOf(masks.RandomIgnition, moved.RandomIgnition, source, target, reprojected),
                TriggerBuffer = CountOf(masks.ManualTriggerBuffer, moved.ManualTriggerBuffer, source, target, reprojected),
                LeftOutWuiCells = Masks.Count(masks.WuiArea),
                LeftOutInitialIgnitionCells = Masks.Count(masks.InitialIgnition),
            };
        }

        /// <summary>
        /// A file name beside <paramref name="gfiPath"/> that does not exist yet, saying which grid it is on:
        /// <c>painted_fire_areas_566x541.gfi</c>, then <c>painted_fire_areas_566x541_2.gfi</c> and so on. The stem
        /// loses an earlier <c>_WxH</c> suffix, so moving a moved painting again does not stack them.
        /// </summary>
        public static string NewFileName(string gfiPath, int ncols, int nrows)
        {
            string folder = System.IO.Path.GetDirectoryName(gfiPath) ?? string.Empty;
            string stem = System.IO.Path.GetFileNameWithoutExtension(gfiPath);
            string extension = System.IO.Path.GetExtension(gfiPath);
            if (string.IsNullOrEmpty(extension)) extension = ".gfi";

            stem = StripGridSuffix(stem);
            string baseName = $"{stem}_{ncols}x{nrows}";
            string candidate = System.IO.Path.Combine(folder, baseName + extension);
            for (int n = 2; File.Exists(candidate); ++n)
            {
                candidate = System.IO.Path.Combine(folder, $"{baseName}_{n}{extension}");
            }
            return candidate;
        }

        /// <summary>"painted_fire_areas_616x590_2" -> "painted_fire_areas".</summary>
        private static string StripGridSuffix(string stem)
        {
            string s = stem;
            //An optional "_<n>" counter, then "_<W>x<H>".
            int last = s.LastIndexOf('_');
            if (last > 0 && IsDigits(s.Substring(last + 1)) && IsGridToken(s.Substring(0, last), out string without))
            {
                return without;
            }
            return IsGridToken(s, out string plain) ? plain : s;
        }

        private static bool IsGridToken(string s, out string without)
        {
            without = s;
            int last = s.LastIndexOf('_');
            if (last <= 0) return false;
            string token = s.Substring(last + 1);
            int x = token.IndexOf('x');
            if (x <= 0 || x == token.Length - 1) return false;
            if (!IsDigits(token.Substring(0, x)) || !IsDigits(token.Substring(x + 1))) return false;
            without = s.Substring(0, last);
            return true;
        }

        private static bool IsDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if (c < '0' || c > '9') return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ helpers

        private static bool[] Sample(bool[] mask, int[] sourceIndex, int targetCells)
        {
            var result = new bool[targetCells];
            if (mask == null) return result;
            for (int k = 0; k < targetCells; ++k)
            {
                int s = sourceIndex[k];
                if (s >= 0 && s < mask.Length && mask[s]) result[k] = true;
            }
            return result;
        }

        /// <summary>The cell (x + y * ncols, y from the south) that contains the point, or -1.</summary>
        private static int CellOf(Grid g, double x, double y)
        {
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y)) return -1;
            double fx = (x - g.XMin) / g.CellWidth;
            double fy = (y - g.YMin) / g.CellHeight;
            if (fx < 0.0 || fy < 0.0) return -1;
            int c = (int)System.Math.Floor(fx);
            int r = (int)System.Math.Floor(fy);
            if (c >= g.Ncols || r >= g.Nrows) return -1;
            return c + r * g.Ncols;
        }

        /// <summary>For every painted cell of <paramref name="mask"/>, the target cell its centre falls in (or -1).</summary>
        private static List<int> ForwardCells(bool[] mask, Grid source, Grid target, bool reprojected)
        {
            var painted = new List<int>();
            if (mask == null) return painted;
            for (int k = 0; k < mask.Length; ++k)
            {
                if (mask[k]) painted.Add(k);
            }

            double[] x = new double[painted.Count];
            double[] y = new double[painted.Count];
            for (int n = 0; n < painted.Count; ++n)
            {
                int k = painted[n];
                x[n] = source.XMin + (k % source.Ncols + 0.5) * source.CellWidth;
                y[n] = source.YMin + (k / source.Ncols + 0.5) * source.CellHeight;
            }

            if (reprojected && painted.Count > 0)
            {
                Transform(source, target, x, y);
            }

            var cells = new List<int>(painted.Count);
            for (int n = 0; n < painted.Count; ++n)
            {
                cells.Add(CellOf(target, x[n], y[n]));
            }
            return cells;
        }

        private static MaskCount CountOf(bool[] before, bool[] after, Grid source, Grid target, bool reprojected)
        {
            var c = new MaskCount
            {
                Before = Masks.Count(before),
                After = Masks.Count(after),
            };
            c.AreaBeforeSquareMetres = c.Before * source.CellArea;
            c.AreaAfterSquareMetres = c.After * target.CellArea;
            if (c.Before > 0)
            {
                foreach (int k in ForwardCells(before, source, target, reprojected))
                {
                    if (k < 0) ++c.OutsideTarget;
                }
            }
            return c;
        }

        /// <summary>
        /// Whether the two grids are in the same CRS: the same EPSG code when both have one, else GDAL's own
        /// comparison of their WKT. Two grids without any CRS are taken to share one - the only way a painting
        /// on such a grid could be moved at all.
        /// </summary>
        private static bool SameCrs(Grid a, Grid b)
        {
            if (!string.IsNullOrEmpty(a.Epsg) && !string.IsNullOrEmpty(b.Epsg))
            {
                return string.Equals(a.Epsg, b.Epsg, StringComparison.OrdinalIgnoreCase);
            }
            if (string.IsNullOrEmpty(a.Wkt) && string.IsNullOrEmpty(b.Wkt))
            {
                return true;
            }
            if (string.IsNullOrEmpty(a.Wkt) || string.IsNullOrEmpty(b.Wkt))
            {
                throw new InvalidDataException("One of the two grids has no coordinate system ("
                    + System.IO.Path.GetFileName(string.IsNullOrEmpty(a.Wkt) ? a.Path : b.Path)
                    + "), so the painting cannot be placed on the other.");
            }

            using (var sa = new SpatialReference(a.Wkt))
            using (var sb = new SpatialReference(b.Wkt))
            {
                return sa.IsSame(sb, null) != 0;
            }
        }

        /// <summary>Transforms the points in place from <paramref name="from"/>'s CRS into <paramref name="to"/>'s.</summary>
        private static void Transform(Grid from, Grid to, double[] x, double[] y)
        {
            using (var source = new SpatialReference(from.Wkt))
            using (var destination = new SpatialReference(to.Wkt))
            {
                //Easting first on both sides, whatever the CRS's own axis order: that is how the points are held.
                source.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                destination.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
                using (var transform = new CoordinateTransformation(source, destination))
                {
                    double[] z = new double[x.Length];
                    transform.TransformPoints(x.Length, x, y, z);
                }
            }
        }
    }
}
