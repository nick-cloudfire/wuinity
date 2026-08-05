using System;
using System.Collections.Generic;
using System.IO;
using PREACT.Math;

namespace PREACT.Utility
{
    /// <summary>
    /// Draws an ignition point out of an ignition mask, the way ELMFIRE does, so that a caller who needs to
    /// know <i>where</i> the fire starts before the fire runs can have it.
    /// </summary>
    /// <remarks>
    /// ELMFIRE samples its own ignitions and normally that is where it should stay — the physics belongs in one
    /// place, and this platform deliberately hands it <c>RANDOM_IGNITIONS</c> rather than a point it chose.
    /// The exception is anything that has to be <b>derived from</b> the ignition: aiming a realization's wind
    /// from the fire at the community means the ignition must exist before the weather is generated, and
    /// ELMFIRE draws it several minutes later, inside the run.
    ///
    /// So this reproduces <c>DETERMINE_NUM_CASES_TOTAL</c> and <c>DETERMINE_IGNITION_LOCATIONS</c>
    /// (elmfire_ignition.f90) for the single-case case: candidates are cells whose mask weight is above
    /// <c>IGN_MASK_CRIT</c> and outside the edge buffer, optionally restricted to burnable fuel, and one is
    /// drawn with probability proportional to its weight. What it does not reproduce is ERC and pyrome
    /// weighting (<c>RANDOM_IGNITIONS_TYPE = 2</c>), which the caller is expected to check for and say so
    /// about.
    ///
    /// Not a rewrite of ELMFIRE's sampler as a policy: the ELMFIRE-side path stays the default, and this is
    /// used only when the ignition has to be known up front.
    /// </remarks>
    public static class MaskIgnitionSampler
    {
        /// <summary>ELMFIRE's own threshold for "this cell has a weight at all" (elmfire_vars.f90).</summary>
        private const float MaskCritical = 1e-30f;

        public class Result
        {
            public bool Ok;
            public string Message;

            /// <summary>The drawn point in the raster's own CRS.</summary>
            public double X, Y;

            /// <summary>Its cell, for a caller that wants to report or check it.</summary>
            public int Column, Row;

            /// <summary>How many cells could have been drawn — the size of the population sampled.</summary>
            public int Candidates;
        }

        /// <summary>
        /// Draws one point from <paramref name="maskPath"/>.
        /// </summary>
        /// <param name="fuelPath">
        /// Optional fuel raster; when given, cells whose fuel cannot carry fire are not candidates. Worth
        /// passing: ELMFIRE stops a case whose ignition cell is nonburnable ("IGNITION CELL IS NONBURNABLE"),
        /// and a mask lifted by <c>ADD_TO_IGNITION_MASK</c> covers water and tarmac.
        /// </param>
        /// <param name="edgeBufferMetres">
        /// The namelist's <c>EDGEBUFFER</c>: ELMFIRE will not ignite within it, so neither does this.
        /// </param>
        public static Result Sample(string maskPath, string fuelPath, double edgeBufferMetres, int seed,
                                    HashSet<int> nonBurnableFuelCodes = null)
        {
            var result = new Result();

            if (!File.Exists(maskPath))
            {
                result.Message = "ignition mask not found: " + maskPath;
                return result;
            }

            float[,] mask = AscRaster.ReadGeoTiff(maskPath, out AscRaster.Header header, out bool ok);
            if (!ok || mask == null)
            {
                result.Message = "could not read the ignition mask " + maskPath;
                return result;
            }

            float[,] fuel = null;
            if (!string.IsNullOrEmpty(fuelPath) && File.Exists(fuelPath))
            {
                fuel = AscRaster.ReadGeoTiff(fuelPath, out AscRaster.Header fuelHeader, out bool fuelOk);
                if (!fuelOk || fuel == null
                    || fuel.GetLength(0) != mask.GetLength(0) || fuel.GetLength(1) != mask.GetLength(1))
                {
                    //A fuel raster on a different grid cannot be indexed against the mask, and guessing an
                    //alignment would exclude the wrong cells. Dropped rather than misused.
                    fuel = null;
                }
            }

            int nx = mask.GetLength(0);
            int ny = mask.GetLength(1);

            //Cells within the buffer of any edge are excluded, matching ELMFIRE's own treatment of EDGEBUFFER.
            int margin = header.CellSize > 0.0 ? (int)(edgeBufferMetres / header.CellSize) + 1 : 1;

            var columns = new List<int>();
            var rows = new List<int>();
            var weights = new List<double>();
            double total = 0.0;

            for (int x = margin; x < nx - margin; ++x)
            {
                for (int y = margin; y < ny - margin; ++y)
                {
                    float weight = mask[x, y];
                    if (float.IsNaN(weight) || weight < MaskCritical) continue;
                    if (fuel != null && !IsBurnable(fuel[x, y], nonBurnableFuelCodes)) continue;

                    columns.Add(x);
                    rows.Add(y);
                    weights.Add(weight);
                    total += weight;
                }
            }

            result.Candidates = columns.Count;

            if (columns.Count == 0)
            {
                result.Message = "no cell in " + Path.GetFileName(maskPath) + " is ignitable "
                    + $"(mask weight above zero, outside a {edgeBufferMetres:F0} m edge buffer"
                    + (fuel != null ? ", on burnable fuel)" : ")");
                return result;
            }

            //Weighted draw over the cumulative distribution, as ELMFIRE does. A mask that sums to zero cannot
            //happen here - every candidate is above the threshold - so there is no uniform fallback to make.
            var rng = new MonteCarloRng(seed);
            double target = rng.NextDouble() * total;
            double running = 0.0;
            int chosen = columns.Count - 1;
            for (int i = 0; i < columns.Count; ++i)
            {
                running += weights[i];
                if (running >= target) { chosen = i; break; }
            }

            result.Column = columns[chosen];
            result.Row = rows[chosen];

            //Cell centre. The raster is indexed [x, y] with y = 0 at the south edge, which is the convention
            //every grid in this codebase uses and the reason this is not header.YllCorner + (nrows - y).
            result.X = header.XllCorner + (result.Column + 0.5) * header.CellSize;
            result.Y = header.YllCorner + (result.Row + 0.5) * header.CellSize;
            result.Ok = true;
            return result;
        }

        /// <summary>
        /// The centroid of the cells a mask marks — the WUI area's middle, for anything that needs to aim at
        /// the community rather than at the domain.
        /// </summary>
        /// <remarks>
        /// Unweighted: a WUI mask says which ground is the community, not how much community is in a cell, so
        /// every marked cell counts once. Returns false for a mask with nothing marked, which is a scenario
        /// that has not been painted rather than one whose community is empty.
        /// </remarks>
        public static bool TryGetMaskCentroid(string maskPath, out double x, out double y, out int cells)
        {
            x = y = 0.0;
            cells = 0;

            if (!File.Exists(maskPath)) return false;

            float[,] mask = AscRaster.ReadGeoTiff(maskPath, out AscRaster.Header header, out bool ok);
            if (!ok || mask == null) return false;

            double sx = 0.0, sy = 0.0;
            for (int i = 0; i < mask.GetLength(0); ++i)
            {
                for (int j = 0; j < mask.GetLength(1); ++j)
                {
                    if (float.IsNaN(mask[i, j]) || mask[i, j] <= 0f) continue;
                    sx += i + 0.5;
                    sy += j + 0.5;
                    ++cells;
                }
            }

            if (cells == 0) return false;

            x = header.XllCorner + header.CellSize * sx / cells;
            y = header.YllCorner + header.CellSize * sy / cells;
            return true;
        }

        /// <summary>
        /// The direction wind must blow <b>from</b> for it to carry a fire at <paramref name="fromX"/>,
        /// <paramref name="fromY"/> towards <paramref name="toX"/>, <paramref name="toY"/>. Degrees.
        /// </summary>
        /// <remarks>
        /// Meteorological convention, because that is what both WindNinja and ELMFIRE's <c>wd</c> raster use:
        /// 270 means a westerly, blowing towards the east. So the bearing of the fire-to-community vector is
        /// computed and then reversed.
        ///
        /// The bearing is <c>atan2(dx, dy)</c>, not the usual <c>atan2(dy, dx)</c>: compass bearings are
        /// measured clockwise from north, which swaps the arguments and the sense. ELMFIRE's own
        /// <c>POINT_WIND_TO_CENTER</c> does the same thing — <c>atan2d(XCEN - IX_IGN, YCEN - IY_IGN)</c> then
        /// <c>+ 180</c> — against the centre of the domain rather than the community.
        /// </remarks>
        public static double WindDirectionFromBearing(double fromX, double fromY, double toX, double toY)
        {
            double bearing = System.Math.Atan2(toX - fromX, toY - fromY) * 180.0 / System.Math.PI;
            return ((bearing + 180.0) % 360.0 + 360.0) % 360.0;
        }

        /// <summary>
        /// Whether a fuel code can carry fire — the 91-99 block is nonburnable in both FBFM13 and FBFM40, as
        /// are zero and NoData. Mirrors <see cref="ElmfireCaseBuilder"/>'s own test, which is what built the
        /// mask this samples.
        /// </summary>
        private static bool IsBurnable(float code, HashSet<int> nonBurnable)
        {
            if (float.IsNaN(code) || code <= 0f) return false;
            int c = (int)System.Math.Round(code);
            if (c >= 91 && c <= 99) return false;
            return nonBurnable == null || !nonBurnable.Contains(c);
        }
    }
}
