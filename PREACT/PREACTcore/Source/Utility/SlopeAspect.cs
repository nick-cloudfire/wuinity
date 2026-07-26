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
        /// Computes slope (degrees from horizontal) and aspect (degrees clockwise from... see
        /// note) for every cell of an elevation raster, using the standard 3x3 Horn's-method
        /// kernel. Edge cells clamp to the nearest valid row/column instead of wrapping.
        /// </summary>
        public static void Compute(float[,] elevation, double cellSize, out float[,] slopeDegrees, out float[,] aspectDegrees)
        {
            int ncols = elevation.GetLength(0);
            int nrows = elevation.GetLength(1);

            slopeDegrees = new float[ncols, nrows];
            aspectDegrees = new float[ncols, nrows];

            const double radToDeg = 180.0 / System.Math.PI;

            float Sample(int x, int y)
            {
                x = System.Math.Clamp(x, 0, ncols - 1);
                y = System.Math.Clamp(y, 0, nrows - 1);
                return elevation[x, y];
            }

            for (int x = 0; x < ncols; ++x)
            {
                for (int y = 0; y < nrows; ++y)
                {
                    float z1 = Sample(x - 1, y - 1);
                    float z2 = Sample(x - 1, y);
                    float z3 = Sample(x - 1, y + 1);
                    float z4 = Sample(x, y - 1);
                    float z6 = Sample(x, y + 1);
                    float z7 = Sample(x + 1, y - 1);
                    float z8 = Sample(x + 1, y);
                    float z9 = Sample(x + 1, y + 1);

                    double dzdx = ((z3 + 2.0 * z6 + z9) - (z1 + 2.0 * z4 + z7)) / (8.0 * cellSize);
                    double dzdy = ((z7 + 2.0 * z8 + z9) - (z1 + 2.0 * z2 + z3)) / (8.0 * cellSize);

                    double gradient = System.Math.Sqrt(dzdx * dzdx + dzdy * dzdy);
                    slopeDegrees[x, y] = (float)(System.Math.Atan(gradient) * radToDeg);

                    if (System.Math.Abs(dzdx) < 1e-6 && System.Math.Abs(dzdy) < 1e-6)
                    {
                        aspectDegrees[x, y] = 0f; //flat terrain
                        continue;
                    }

                    double aspectRadians = System.Math.Atan2(dzdy, -dzdx);
                    if (aspectRadians < 0) aspectRadians += 2 * System.Math.PI;
                    aspectDegrees[x, y] = (float)(aspectRadians * radToDeg);
                }
            }
        }
    }
}
