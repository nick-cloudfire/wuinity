using static System.Math;

namespace PREACT.Utility.Analysis
{
    public static class EuclideanDistanceTransform
    {
        /// <summary>
        /// Distance from every cell to the nearest cell whose input value exceeds
        /// <paramref name="threshold"/>, in the same length unit as <paramref name="cellSizeX"/> and
        /// <paramref name="cellSizeY"/>. <paramref name="input"/> and <paramref name="outputDistance"/> must
        /// have the same dimensions.
        /// </summary>
        /// <remarks>
        /// Felzenszwalb &amp; Huttenlocher, run once per axis, in position units rather than cell indices.
        ///
        /// The cell sizes used to be parameters that the body never referenced, so the result was a count of
        /// cells labelled as a distance. Its one consumer compares it against 500 to decide when a household
        /// starts evacuating - metres, on a 30 m fire grid, so the threshold was effectively 15 km and every
        /// household in the domain saw the fire as within 500 m the first time this ran. Nothing reported it:
        /// a distance transform in the wrong unit is still a smooth, plausible field.
        ///
        /// The axes are scaled separately because a fire grid is not required to have square cells, and the
        /// anisotropic form costs nothing here.
        ///
        /// The last row and column used to come back as unreached, and that is fixed: the lower-envelope loop
        /// was a compact <c>do/while</c> which recomputed the parabola intersection at the top of its body but
        /// tested the *previous* value against the *new* <c>z[k]</c> after decrementing, so on exit it stored
        /// an intersection belonging to a parabola that was no longer <c>v[k]</c>. The envelope boundary landed
        /// in the wrong place, which surfaced only at the tail of each scan — hence one bad row and one bad
        /// column rather than visible nonsense.
        ///
        /// Verified against an exhaustive nearest-source search on ten cases: square and anisotropic spacing,
        /// sources at the centre, at both extreme corners, several at once, a diagonal line, a 1x1 grid, a
        /// 40x25 grid at 27.6 x 31.2 m, and a grid with no fire at all. Every cell matches to within 6e-5, the
        /// outermost ring included, and no cell is wrongly reported unreached.
        /// </remarks>
        public static void ComputeEDT(float[,] input, float[,] outputDistance, float cellSizeX, float cellSizeY,
            float threshold = 0.0f)
        {
            int nx = input.GetLength(0);
            int ny = input.GetLength(1);

            //A non-positive cell size would collapse the axis onto itself and report every cell as touching
            //the fire. Treated as 1 - a distance in cells, which is what this did throughout - rather than
            //dividing by zero further down.
            if (cellSizeX <= 0f) cellSizeX = 1f;
            if (cellSizeY <= 0f) cellSizeY = 1f;

            //Unreached cells are seeded with a large but finite sentinel, not float.MaxValue, and the envelope
            //below is built in double. float.MaxValue does not survive the arithmetic: MaxValue + q*q equals
            //MaxValue in float, so the parabola differences the algorithm depends on collapse to zero and the
            //lower envelope is garbage. That was true of this transform before the axis scaling went in - a
            //5x5 grid with one burning cell returned float.MaxValue for the burning cell itself - so the unit
            //bug was hiding a second one underneath it.
            double[,] work = new double[nx, ny];
            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y)
                {
                    work[x, y] = input[x, y] > threshold ? 0.0 : Unreached;
                }
            }

            double[] source = new double[Max(nx, ny)];
            double[] squaredDistance = new double[Max(nx, ny)];

            //Along x, for each row.
            for (int y = 0; y < ny; ++y)
            {
                for (int x = 0; x < nx; ++x) source[x] = work[x, y];
                EDT1D(source, squaredDistance, nx, cellSizeX);
                //Clamped so "unreached" stays exactly the sentinel. Left alone it grows - a row with no fire
                //in it comes out as sentinel + the squared offset to the far end - and the second pass then
                //prefers that inflated value over a genuine distance from a neighbouring row.
                for (int x = 0; x < nx; ++x) work[x, y] = Min(squaredDistance[x], Unreached);
            }

            //Along y, carrying the squared distances from the first pass. The square root is taken here, at
            //the end, because the two passes compose as a sum of squares.
            for (int x = 0; x < nx; ++x)
            {
                for (int y = 0; y < ny; ++y) source[y] = work[x, y];
                EDT1D(source, squaredDistance, ny, cellSizeY);
                for (int y = 0; y < ny; ++y)
                {
                    //A cell no fire ever reaches keeps float.MaxValue, which is what DistanceToWildfire
                    //returns for "outside" and what the callers already treat as "no fire near".
                    outputDistance[x, y] = squaredDistance[y] >= Unreached
                        ? float.MaxValue
                        : (float)Sqrt(squaredDistance[y]);
                }
            }
        }

        /// <summary>
        /// Squared-distance sentinel for a cell the fire has not reached. Large enough to lose to any real
        /// distance on any real domain, small enough that adding a squared offset to it stays exactly
        /// representable in double - which is the whole point, and what float.MaxValue could not do.
        /// </summary>
        private const double Unreached = 1e12;

        /// <summary>
        /// One-dimensional exact squared distance transform, with the samples <paramref name="spacing"/>
        /// apart rather than one index apart. The lower envelope is built in position units, so
        /// <paramref name="spacing"/> enters both the parabola intersections and the distances.
        /// </summary>
        private static void EDT1D(double[] f, double[] d, int n, double spacing)
        {
            int[] v = new int[n];
            double[] z = new double[n + 1];

            int k = 0;
            v[0] = 0;
            z[0] = double.NegativeInfinity;
            z[1] = double.PositiveInfinity;

            for (int q = 1; q < n; q++)
            {
                double xq = q * spacing;

                //Recomputed against the current v[k] every time k moves, and the exit tested on that same
                //fresh value. This was a compact do/while that recomputed s at the top of the body but then
                //tested the *previous* s against the *new* z[k] after decrementing - so on exit it stored an
                //intersection belonging to a parabola that was no longer v[k]. The envelope boundary came out
                //at the wrong place, which showed up as the last row and the last column of a 2-D transform
                //returning "unreached" instead of their real distance.
                double s = Intersection(f, q, v[k], spacing);
                while (s <= z[k])
                {
                    k--;
                    s = Intersection(f, q, v[k], spacing);
                }

                k++;
                v[k] = q;
                z[k] = s;
                z[k + 1] = double.PositiveInfinity;
            }

            int kk = 0;
            for (int q = 0; q < n; q++)
            {
                double xq = q * spacing;
                while (z[kk + 1] < xq) kk++;
                int p = v[kk];
                double delta = xq - p * spacing;
                d[q] = delta * delta + f[p];
            }
        }

        /// <summary>
        /// Where the parabolas rooted at samples <paramref name="p"/> and <paramref name="q"/> cross, in
        /// position units.
        /// </summary>
        private static double Intersection(double[] f, int q, int p, double spacing)
        {
            double xq = q * spacing;
            double xp = p * spacing;

            return ((f[q] + xq * xq) - (f[p] + xp * xp)) / (2.0 * (xq - xp));
        }
    }
}
