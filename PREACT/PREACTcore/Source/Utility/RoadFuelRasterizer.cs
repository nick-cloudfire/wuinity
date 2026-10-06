using System;
using System.Collections.Generic;
using System.IO;
using OSGeo.GDAL;
using PREACT.Math;

namespace PREACT.Utility
{
    /// <summary>
    /// Burns a road network into a copy of the fuel raster as a spreadable fuel model, so a landscape that
    /// roads have cut into disconnected pieces can carry fire across them.
    /// </summary>
    /// <remarks>
    /// The problem this addresses is a fuel map in which roads, and the urban and barren cells around them,
    /// are non-burnable: a burnable cell fully enclosed by them is an islet, and an ignition drawn there burns
    /// that one cell and stops. Such a realization reaches nothing, produces no trigger boundary, and reports
    /// "End of simulation reached successfully" while doing it.
    ///
    /// **Measure before using this.** On the Mediterranean case it was written for, islets turned out to be a
    /// rounding error: 1000 burnable patches, but the largest held 94.4 % of all burnable area, and of the
    /// cells the ignition mask offered only 0.33 % sat on a single-cell islet and 0.85 % on a patch of four or
    /// fewer. Converting roads there changes the physics of the whole landscape to recover under one percent of
    /// realizations. <see cref="Result.IsletsBefore"/> and <see cref="Result.IsletsAfter"/> are reported for
    /// exactly this reason - they are the only numbers that say whether the conversion did anything.
    ///
    /// Two deliberate constraints. The original raster is never modified: a new file is written and the caller
    /// re-points <c>FBFM_FILENAME</c> at it, so reverting is one namelist key rather than a rebuild. And a cell
    /// is only changed where it is currently non-burnable, so a road crossing forest cannot downgrade the
    /// forest to grass.
    ///
    /// Note what the road fuel model means physically. 101 is GR1, sparse dry-climate grass: it is not inert,
    /// it spreads fire, slowly. A dual carriageway therefore becomes a continuous grass strip rather than a
    /// neutral seam, and at a 30 m cell most roads are narrower than a pixel to begin with, so this paints
    /// corridors wider than the roads it is drawn from. That is a modelling choice, not a bookkeeping fix.
    ///
    /// Ported from Nick's Aug 5-7 work (local-aug, ee789fd6) onto v1: the output is written by its own Int16 writer
    /// carrying the source's georeferencing, the raster's CRS is checked against the zone the lanes are measured in,
    /// and the output records what it was burned from (<c>ROADS_BURNED_FROM</c>), so it can be undone.
    /// </remarks>
    public static class RoadFuelRasterizer
    {
        /// <summary>Anderson/Scott FBFM40 non-burnable codes: urban, snow, agriculture, water, barren.</summary>
        private const int FirstNonBurnableCode = 90;
        private const int LastNonBurnableCode = 100;

        public class Options
        {
            /// <summary>The case's fuel model raster. Read only.</summary>
            public string FuelRasterPath;

            /// <summary>Where the converted copy goes. Defaults beside the source with a _roads suffix.</summary>
            public string OutputRasterPath;

            /// <summary>Lane polylines, in simulation coordinates.</summary>
            public IReadOnlyList<SumoNetworkGeometry.Lane> Lanes;

            /// <summary>
            /// The simulation's UTM origin, which its coordinates are measured from. Lanes come back from
            /// <see cref="SumoNetworkGeometry"/> already relative to this, so adding it recovers the UTM
            /// position the raster's corner is also in.
            /// </summary>
            public Vector2d UtmOrigin;

            /// <summary>
            /// The EPSG code of the zone the lanes are measured in (the simulation's). The raster must be in it: a raster in
            /// another zone takes the roads half a million metres away. 0 skips the check.
            /// </summary>
            public int UtmEpsg;

            /// <summary>Recorded in the output as what the roads were burned into, for undoing it. Defaults to the input.</summary>
            public string OriginalSource;

            /// <summary>GR1 by default: the sparsest spreading grass in the standard set.</summary>
            public int RoadFuelModel = 101;

            /// <summary>
            /// How wide to paint, in metres. Zero means a single cell per road, which at any realistic cell
            /// size is already wider than the road.
            /// </summary>
            public double RoadWidthMetres;

            /// <summary>
            /// Only convert cells that are currently non-burnable. Turning this off lets a road overwrite real
            /// fuel, which is almost never wanted - the roads are already in the map as non-burnable where the
            /// map knows about them.
            /// </summary>
            public bool OnlyWhereNonBurnable = true;

            /// <summary>
            /// The building-area raster (<c>baa</c>), whose cells the building spread model owns. Cells it
            /// marks are left alone. Null skips the test.
            /// </summary>
            /// <remarks>
            /// This matters as soon as <c>USE_BLDG_SPREAD_MODEL</c> is on, and roads are where it matters most:
            /// a road network runs through the built-up area by definition, and those cells are non-burnable in
            /// the fuel map precisely because ELMFIRE is meant to burn them as buildings instead. Painting
            /// surface grass into them gives the same ground two independent ways to burn - a wildland rate of
            /// spread through GR1 and a building-to-building ignition - so the fire crosses the town by
            /// whichever is faster, and the building model's separation distances and hardening factors stop
            /// deciding anything.
            ///
            /// Measured on the case this was written for, 28,120 of the 53,118 cells carrying buildings were
            /// non-burnable in the fuel map, so without this test the conversion would have handed the
            /// building model's own territory to the surface model.
            ///
            /// <c>baa</c> rather than <c>bfm_h</c>: the building fuel model raster is dense - it carries a code
            /// almost everywhere - whereas the building area raster is nodata wherever there is no building,
            /// which is exactly the footprint the other building layers share.
            /// </remarks>
            public string BuildingAreaRasterPath;

            public Action<string> Log;
        }

        public class Result
        {
            public bool Ok;
            public string Message;
            public string OutputPath;

            /// <summary>Cells the road network touched, before any burnable/non-burnable test.</summary>
            public int RoadCells;

            public int Changed;

            /// <summary>Road cells left alone because they already carry a burnable fuel model.</summary>
            public int SkippedBurnable;

            /// <summary>Road cells left to the building spread model.</summary>
            public int SkippedBuildings;

            /// <summary>Road cells where the fuel raster has no data, left as they are rather than given a fuel.</summary>
            public int SkippedNoData;

            /// <summary>Road vertices that fell outside the raster (the segments to them are drawn up to its edge).</summary>
            public int OutsideGrid;

            public int PatchesBefore, PatchesAfter;
            public int IsletsBefore, IsletsAfter;
            public int LargestPatchBefore, LargestPatchAfter;
            public long BurnableBefore, BurnableAfter;

            /// <summary>One line fit to be shown to a user, whether it worked or not.</summary>
            public string Summary;
        }

        public static Result Run(Options o)
        {
            var result = new Result();

            if (o == null || string.IsNullOrEmpty(o.FuelRasterPath) || !File.Exists(o.FuelRasterPath))
            {
                result.Message = "No fuel raster to convert" + (o?.FuelRasterPath != null ? ": " + o.FuelRasterPath : ".");
                return result;
            }
            if (o.Lanes == null || o.Lanes.Count == 0)
            {
                result.Message = "No road network was loaded, so there is nothing to burn into the fuel map.";
                return result;
            }

            float[,] fuel;
            AscRaster.Header header;
            try
            {
                fuel = AscRaster.ReadGeoTiff(o.FuelRasterPath, 1, out header, out bool ok, out int _);
                if (!ok || fuel == null)
                {
                    result.Message = "Could not read " + o.FuelRasterPath;
                    return result;
                }
            }
            catch (Exception e)
            {
                result.Message = "Could not read " + o.FuelRasterPath + ": " + e.Message;
                return result;
            }

            if (!(header.CellSize > 0.0))
            {
                result.Message = "The fuel raster reports a cell size of " + header.CellSize + ".";
                return result;
            }

            if (o.UtmEpsg > 0 && header.EpsgCode > 0 && header.EpsgCode != o.UtmEpsg)
            {
                result.Message = $"The fuel raster is in EPSG:{header.EpsgCode} and the road network in EPSG:{o.UtmEpsg}, the "
                    + "simulation's zone; burned in, the roads would land somewhere else entirely.";
                return result;
            }

            int nx = fuel.GetLength(0), ny = fuel.GetLength(1);

            //Loaded before anything is painted, and a grid mismatch is fatal rather than ignored: comparing
            //cell for cell against a raster on a different grid would protect the wrong ground, which is worse
            //than not protecting any - it would look like it worked.
            float[,] buildings = null;
            double buildingNoData = 0.0;
            if (!string.IsNullOrEmpty(o.BuildingAreaRasterPath))
            {
                if (!File.Exists(o.BuildingAreaRasterPath))
                {
                    result.Message = "No building raster at " + o.BuildingAreaRasterPath
                        + ". Point at the case's baa.tif, or clear it to convert road cells regardless.";
                    return result;
                }

                try
                {
                    buildings = AscRaster.ReadGeoTiff(o.BuildingAreaRasterPath, 1,
                                    out AscRaster.Header bh, out bool bok, out int _);
                    if (!bok || buildings == null)
                    {
                        result.Message = "Could not read " + o.BuildingAreaRasterPath;
                        return result;
                    }
                    buildingNoData = bh.NoDataValue;
                }
                catch (Exception e)
                {
                    result.Message = "Could not read " + o.BuildingAreaRasterPath + ": " + e.Message;
                    return result;
                }

                if (buildings.GetLength(0) != nx || buildings.GetLength(1) != ny)
                {
                    result.Message = $"The building raster is {buildings.GetLength(0)}x{buildings.GetLength(1)} "
                        + $"but the fuel raster is {nx}x{ny}; they must be on the same grid to be compared "
                        + "cell for cell.";
                    return result;
                }
            }

            //Before, so the after-figures have something to be compared against. Cheap next to the read.
            Describe(fuel, nx, ny, out result.PatchesBefore, out result.IsletsBefore,
                     out result.LargestPatchBefore, out result.BurnableBefore);

            //Half-width in cells, rounded out: a width of zero paints the single cell the line passes through,
            //which is the honest default when a 30 m cell is wider than the road.
            int radius = o.RoadWidthMetres > 0.0
                ? (int)System.Math.Floor(o.RoadWidthMetres / (2.0 * header.CellSize))
                : 0;

            var touched = new bool[nx, ny];

            foreach (SumoNetworkGeometry.Lane lane in o.Lanes)
            {
                if (lane?.Points == null || lane.Points.Length == 0) continue;

                bool havePrevious = false;
                int px = 0, py = 0;

                foreach (Vector2d point in lane.Points)
                {
                    //Simulation coordinates are metres from the UTM origin, and the raster's corner is in the
                    //same UTM system, so the origin is what ties the two together. Getting this wrong does not
                    //fail - it paints roads in the next municipality.
                    double easting = point.x + o.UtmOrigin.x;
                    double northing = point.y + o.UtmOrigin.y;

                    double fx = System.Math.Floor((easting - header.XllCorner) / header.CellSize);
                    double fy = System.Math.Floor((northing - header.YllCorner) / header.CellSize);

                    //A vertex outside the raster still ends a segment that may cross it - a road leaving the domain is
                    //drawn up to the edge, not dropped from its last vertex inside (which the stamp below clips). One far
                    //beyond any sensible network (a broken offset) is not, rather than walked cell by cell.
                    if (System.Math.Abs(fx) > 1e6 || System.Math.Abs(fy) > 1e6)
                    {
                        ++result.OutsideGrid;
                        havePrevious = false;
                        continue;
                    }
                    int cx = (int)fx, cy = (int)fy;
                    if (cx < 0 || cy < 0 || cx >= nx || cy >= ny)
                    {
                        ++result.OutsideGrid;
                    }

                    if (havePrevious)
                    {
                        DrawLine(touched, nx, ny, px, py, cx, cy, radius);
                    }
                    else
                    {
                        Stamp(touched, nx, ny, cx, cy, radius);
                    }

                    px = cx; py = cy;
                    havePrevious = true;
                }
            }

            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y)
                {
                    if (!touched[x, y]) continue;
                    ++result.RoadCells;

                    //Buildings first: a cell the building model owns is left alone whatever its fuel code says,
                    //and reporting it as "already burnable" would hide why it was skipped.
                    if (buildings != null && HasBuilding(buildings[x, y], buildingNoData))
                    {
                        ++result.SkippedBuildings;
                        continue;
                    }

                    //No data is not "no fuel": nothing is known there, and a road painted into it would run fuel into the void.
                    if (fuel[x, y] == (float)header.NoDataValue || float.IsNaN(fuel[x, y]))
                    {
                        ++result.SkippedNoData;
                        continue;
                    }

                    if (o.OnlyWhereNonBurnable && IsBurnable(fuel[x, y]))
                    {
                        ++result.SkippedBurnable;
                        continue;
                    }

                    fuel[x, y] = o.RoadFuelModel;
                    ++result.Changed;
                }
            }

            Describe(fuel, nx, ny, out result.PatchesAfter, out result.IsletsAfter,
                     out result.LargestPatchAfter, out result.BurnableAfter);

            string output = o.OutputRasterPath;
            if (string.IsNullOrEmpty(output))
            {
                string dir = Path.GetDirectoryName(o.FuelRasterPath) ?? ".";
                output = Path.Combine(dir,
                    Path.GetFileNameWithoutExtension(o.FuelRasterPath) + "_roads" + o.RoadFuelModel + ".tif");
            }

            try
            {
                //Int16, matching what a fuel model raster is and what ELMFIRE reads it into. Writing Float32
                //here produced a file that was correct in every visible respect - right grid, right CRS, right
                //values through GDAL - and that ELMFIRE read as garbage, because FBFM%I2 is an INTEGER*2 array
                //and nothing converts. The whole landscape then tested non-burnable and no fire would spread.
                WriteInt16Like(o.FuelRasterPath, fuel, output, o.OriginalSource ?? o.FuelRasterPath, o.RoadFuelModel);
            }
            catch (Exception e)
            {
                result.Message = "Could not write " + output + ": " + e.Message;
                return result;
            }

            result.Ok = true;
            result.OutputPath = output;
            result.Summary =
                $"{result.Changed} of {result.RoadCells} road cells set to fuel model {o.RoadFuelModel} "
                + $"({result.SkippedBurnable} already burnable"
                + (buildings != null ? $", {result.SkippedBuildings} left to the building spread model" : "")
                + (result.SkippedNoData > 0 ? $", {result.SkippedNoData} without fuel data" : "")
                + ", left alone). "
                + $"Isolated single-cell islets {result.IsletsBefore} -> {result.IsletsAfter}; "
                + $"burnable patches {result.PatchesBefore} -> {result.PatchesAfter}; "
                + $"largest patch {Share(result.LargestPatchBefore, result.BurnableBefore)} -> "
                + $"{Share(result.LargestPatchAfter, result.BurnableAfter)} of burnable area.";

            o.Log?.Invoke(result.Summary);
            if (result.OutsideGrid > 0)
            {
                o.Log?.Invoke($"{result.OutsideGrid} road vertices fell outside the fuel raster and were "
                    + "ignored; the network extends past the fire domain.");
            }
            return result;
        }

        /// <summary>
        /// Writes <paramref name="fuel"/> (x east, y north from the south-west corner, as AscRaster reads it) as a
        /// single-band Int16 GeoTIFF with the georeferencing, projection and nodata of <paramref name="likePath"/>.
        /// </summary>
        internal static void WriteInt16Like(string likePath, float[,] fuel, string output, string burnedFrom, int roadFuelModel)
        {
            int nx = fuel.GetLength(0), ny = fuel.GetLength(1);
            Gdal.AllRegister();
            using (Dataset like = Gdal.Open(likePath, Access.GA_ReadOnly))
            {
                if (like == null) throw new IOException("Could not open " + likePath);
                double[] gt = new double[6];
                like.GetGeoTransform(gt);
                bool southUp = gt[5] > 0.0;
                double nodata;
                int hasNodata;
                using (Band band = like.GetRasterBand(1)) band.GetNoDataValue(out nodata, out hasNodata);

                var buffer = new short[nx * ny];
                for (int row = 0; row < ny; ++row)
                {
                    int y = southUp ? row : ny - 1 - row;
                    for (int x = 0; x < nx; ++x)
                    {
                        double v = System.Math.Round(fuel[x, y]);
                        buffer[row * nx + x] = (short)System.Math.Min(System.Math.Max(v, short.MinValue), short.MaxValue);
                    }
                }

                string folder = Path.GetDirectoryName(Path.GetFullPath(output));
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                string temporary = output + ".tmp.tif";
                using (Driver driver = Gdal.GetDriverByName("GTiff"))
                using (Dataset ds = driver.Create(temporary, nx, ny, 1, DataType.GDT_Int16, new[] { "COMPRESS=DEFLATE" }))
                {
                    if (ds == null) throw new IOException("Could not create " + output);
                    ds.SetGeoTransform(gt);
                    ds.SetProjection(like.GetProjection());
                    ds.SetMetadataItem("ROADS_BURNED_FROM", burnedFrom, "");
                    ds.SetMetadataItem("ROAD_FUEL_MODEL", roadFuelModel.ToString(System.Globalization.CultureInfo.InvariantCulture), "");
                    using (Band band = ds.GetRasterBand(1))
                    {
                        band.SetNoDataValue(hasNodata != 0 ? nodata : -9999.0);
                        band.WriteRaster(0, 0, nx, ny, buffer, nx, ny, 0, 0);
                    }
                    ds.FlushCache();
                }

                //Into place only once complete; the input may be the file being replaced.
                if (File.Exists(output)) File.Delete(output);
                File.Move(temporary, output);
            }
        }

        /// <summary>What a roads raster records it was burned from, or null for one that is not.</summary>
        public static string BurnedFrom(string path)
        {
            try
            {
                Gdal.AllRegister();
                using (Dataset ds = Gdal.Open(path, Access.GA_ReadOnly))
                {
                    string value = ds?.GetMetadataItem("ROADS_BURNED_FROM", "");
                    return string.IsNullOrEmpty(value) ? null : value;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Share(int part, long whole)
        {
            return whole <= 0 ? "n/a" : (100.0 * part / whole).ToString("0.#") + " %";
        }

        /// <summary>
        /// Whether the building spread model has anything to burn in this cell.
        /// </summary>
        /// <remarks>
        /// The building-area raster is nodata where there is no building rather than zero, so both tests are
        /// needed: a nodata sentinel of -9999 is not greater than zero, but a case that wrote zeros instead
        /// would otherwise mark every cell in the domain as built.
        /// </remarks>
        private static bool HasBuilding(float value, double noData)
        {
            if (float.IsNaN(value)) return false;
            if (value == noData) return false;
            return value > 0f;
        }

        internal static bool IsBurnable(float value)
        {
            //Nodata is negative and 0 is "no fuel", so neither needs naming separately. 256 is FireDX's pavement and roads,
            //which ELMFIRE does not burn (nor 90-100: elmfire_init.f90).
            int code = (int)System.Math.Round(value);
            if (code <= 0 || code == 256) return false;
            return code < FirstNonBurnableCode || code > LastNonBurnableCode;
        }

        private static void Stamp(bool[,] touched, int nx, int ny, int x, int y, int radius)
        {
            for (int dx = -radius; dx <= radius; ++dx)
            {
                for (int dy = -radius; dy <= radius; ++dy)
                {
                    int ax = x + dx, ay = y + dy;
                    if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                    touched[ax, ay] = true;
                }
            }
        }

        /// <summary>
        /// Bresenham, because a polyline sampled only at its vertices leaves gaps: SUMO edges are often a
        /// single straight segment hundreds of metres long, which would otherwise mark two cells and nothing
        /// between them.
        /// </summary>
        private static void DrawLine(bool[,] touched, int nx, int ny, int x0, int y0, int x1, int y1, int radius)
        {
            int dx = System.Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -System.Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                Stamp(touched, nx, ny, x0, y0, radius);
                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        /// <summary>
        /// Counts 8-connected patches of burnable fuel, how many are a single cell, and how big the largest is.
        /// </summary>
        /// <remarks>
        /// 8-connected rather than 4, because a fire crosses a diagonal: treating diagonal neighbours as
        /// disconnected would report islets ELMFIRE does not have. Iterative rather than recursive - a patch
        /// covering most of a million-cell raster overflows the stack.
        /// </remarks>
        private static void Describe(float[,] fuel, int nx, int ny,
            out int patches, out int islets, out int largest, out long burnable)
        {
            patches = 0; islets = 0; largest = 0; burnable = 0;

            var seen = new bool[nx, ny];
            var stack = new Stack<int>();

            for (int sx = 0; sx < nx; ++sx)
            {
                for (int sy = 0; sy < ny; ++sy)
                {
                    if (seen[sx, sy] || !IsBurnable(fuel[sx, sy])) continue;

                    ++patches;
                    int size = 0;
                    seen[sx, sy] = true;
                    stack.Push(sx * ny + sy);

                    while (stack.Count > 0)
                    {
                        int packed = stack.Pop();
                        int x = packed / ny, y = packed % ny;
                        ++size;

                        for (int dx = -1; dx <= 1; ++dx)
                        {
                            for (int dy = -1; dy <= 1; ++dy)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int ax = x + dx, ay = y + dy;
                                if (ax < 0 || ay < 0 || ax >= nx || ay >= ny) continue;
                                if (seen[ax, ay] || !IsBurnable(fuel[ax, ay])) continue;
                                seen[ax, ay] = true;
                                stack.Push(ax * ny + ay);
                            }
                        }
                    }

                    burnable += size;
                    if (size == 1) ++islets;
                    if (size > largest) largest = size;
                }
            }
        }
    }
}
