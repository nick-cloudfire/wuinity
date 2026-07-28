namespace PREACT.Utility
{
    /// <summary>
    /// Derives slope/aspect from a DEM via Horn's method (docs/probabilistic-trigger-convergence.md,
    /// "Topography / DEM downloader" — "already used in AscRaster/k-PERIL"). k-PERIL's own copy
    /// (<c>kPERILcore/source/perilData.cs</c>, <c>interpolateSlope()</c>) is private and tightly
    /// coupled to that vendored engine's instance state, so this is a standalone reimplementation
    /// of the same formula for use by the DEM downloader (and anything else that needs slope/
    /// aspect from a raw elevation raster) without touching the vendored third-party engine.
    /// </summary>
    public static class SlopeAspect
    {
        /// <summary>
        /// Computes slope (degrees from horizontal) and aspect for every cell of an elevation
        /// raster, using the standard 3x3 Horn's-method kernel. Edge cells clamp to the nearest
        /// valid row/column instead of wrapping.
        ///
        /// The input is indexed <c>[x, y]</c> with <b>x running east and y running north</b>
        /// (the lower-left-origin convention <see cref="AscRaster.ReadGeoTiff"/> produces).
        /// Aspect is returned the way LANDFIRE and ELMFIRE both define it: the compass bearing
        /// of the <b>downslope</b> direction, degrees clockwise from north, in [0, 360). Flat
        /// cells return 0.
        /// </summary>
        /// <summary>
        /// Whether an elevation value is a height or a hole. -9999 is the convention used throughout;
        /// the wider test catches the other sentinels DEMs are published with (-32768 and friends), all
        /// far outside the range of real ground.
        /// </summary>
        public static bool IsNoElevation(double value)
        {
            return value <= -1000.0 || value >= 30000.0;
        }

        public static void Compute(float[,] elevation, double cellSize, out float[,] slopeDegrees, out float[,] aspectDegrees)
        {
            int ncols = elevation.GetLength(0);
            int nrows = elevation.GetLength(1);

            slopeDegrees = new float[ncols, nrows];
            aspectDegrees = new float[ncols, nrows];

            const double radToDeg = 180.0 / System.Math.PI;

            for (int x = 0; x < ncols; ++x)
            {
                for (int y = 0; y < nrows; ++y)
                {
                    double centre = elevation[x, y];

                    //A cell with no height has no slope or aspect. Flat, rather than a sentinel carried
                    //through: consumers multiply the slope by something (k-PERIL by 0.06, into a wind) and
                    //not all of them check for nodata first.
                    if (IsNoElevation(centre))
                    {
                        slopeDegrees[x, y] = 0f;
                        aspectDegrees[x, y] = 0f;
                        continue;
                    }

                    //A neighbour with no height stands in as the centre's own, which is the same treatment
                    //the raster edge gets and reads as "no change in that direction". Taking -9999 as a
                    //height instead puts a 10 km cliff beside every gap: on Mati's DEM, which has no data
                    //over the sea, that produced 90 degree slopes along the whole coast and a mean slope
                    //three and a half degrees too steep across the raster.
                    float Sample(int sx, int sy)
                    {
                        sx = System.Math.Clamp(sx, 0, ncols - 1);
                        sy = System.Math.Clamp(sy, 0, nrows - 1);
                        float value = elevation[sx, sy];
                        return IsNoElevation(value) ? (float)centre : value;
                    }

                    float z1 = Sample(x - 1, y - 1);
                    float z2 = Sample(x - 1, y);
                    float z3 = Sample(x - 1, y + 1);
                    float z4 = Sample(x, y - 1);
                    float z6 = Sample(x, y + 1);
                    float z7 = Sample(x + 1, y - 1);
                    float z8 = Sample(x + 1, y);
                    float z9 = Sample(x + 1, y + 1);

                    //the y-varying kernel gives the northward gradient, the x-varying one the
                    //eastward gradient - naming them after the axis they actually differentiate
                    //along, because getting these two the wrong way round mirrors the aspect.
                    double dzdNorth = ((z3 + 2.0 * z6 + z9) - (z1 + 2.0 * z4 + z7)) / (8.0 * cellSize);
                    double dzdEast = ((z7 + 2.0 * z8 + z9) - (z1 + 2.0 * z2 + z3)) / (8.0 * cellSize);

                    double gradient = System.Math.Sqrt(dzdEast * dzdEast + dzdNorth * dzdNorth);
                    slopeDegrees[x, y] = (float)(System.Math.Atan(gradient) * radToDeg);

                    if (System.Math.Abs(dzdEast) < 1e-6 && System.Math.Abs(dzdNorth) < 1e-6)
                    {
                        aspectDegrees[x, y] = 0f; //flat terrain
                        continue;
                    }

                    //compass bearing of the downslope vector (-dzdEast, -dzdNorth): Atan2 takes
                    //(east, north) in that order because bearings are measured clockwise from
                    //north, the mirror image of the usual counter-clockwise-from-east angle.
                    double aspectRadians = System.Math.Atan2(-dzdEast, -dzdNorth);
                    if (aspectRadians < 0) aspectRadians += 2 * System.Math.PI;
                    aspectDegrees[x, y] = (float)(aspectRadians * radToDeg);
                }
            }
        }
    }
}
