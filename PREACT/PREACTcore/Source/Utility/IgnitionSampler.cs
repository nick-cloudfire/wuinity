using System.Collections.Generic;
using PREACT.Math;

namespace PREACT.Utility
{
    /// <summary>
    /// Samples an ignition cell for one Monte Carlo realization (docs/probabilistic-trigger-
    /// convergence.md, "Ignition sampler (mask → point)"). Two source formats are supported:
    ///
    /// - The existing uniform boolean ignition mask (<c>WildfireData.RandomIgnition</c>, painted
    ///   in Unity via <c>Painter.PaintMode.RandomIgnitionArea</c>) — every flagged cell is equally
    ///   likely.
    /// - A georeferenced float probability/likelihood raster (read via <see cref="AscRaster"/>) —
    ///   cells are weighted by their value, for callers that have a non-uniform ignition
    ///   likelihood surface rather than a plain in/out mask.
    /// </summary>
    public static class IgnitionSampler
    {
        /// <summary>
        /// Samples one flat index uniformly among the cells flagged in a boolean mask
        /// (e.g. <c>WildfireData.RandomIgnition</c>, indexed as <c>x + y*xCount</c>).
        /// </summary>
        public static bool TrySampleFromMask(bool[] mask, MonteCarloRng rng, out int x, out int y, int xCount)
        {
            x = -1;
            y = -1;
            if (mask == null || mask.Length == 0 || xCount <= 0) return false;

            var candidates = new List<int>();
            for (int i = 0; i < mask.Length; ++i)
            {
                if (mask[i]) candidates.Add(i);
            }
            if (candidates.Count == 0) return false;

            int flatIndex = candidates[rng.Range(0, candidates.Count)];
            x = flatIndex % xCount;
            y = flatIndex / xCount;
            return true;
        }

        /// <summary>
        /// Samples one cell from a probability/likelihood raster, weighted by cell value
        /// (cells at or below zero, or equal to <paramref name="header"/>'s NoData value, are
        /// never selected). Uses inverse-CDF sampling over the flattened, row-major cell list.
        /// </summary>
        public static bool TrySampleFromRaster(float[,] raster, AscRaster.Header header, MonteCarloRng rng, out int x, out int y)
        {
            x = -1;
            y = -1;
            if (raster == null) return false;

            int ncols = header.Ncols;
            int nrows = header.Nrows;

            double total = 0.0;
            for (int xi = 0; xi < ncols; ++xi)
            {
                for (int yi = 0; yi < nrows; ++yi)
                {
                    float v = raster[xi, yi];
                    if (v > 0f && v != header.NoDataValue) total += v;
                }
            }
            if (total <= 0.0) return false;

            double target = rng.Range(0.0, total);
            double cumulative = 0.0;
            int lastWeightedX = -1, lastWeightedY = -1;
            for (int xi = 0; xi < ncols; ++xi)
            {
                for (int yi = 0; yi < nrows; ++yi)
                {
                    float v = raster[xi, yi];
                    if (v <= 0f || v == header.NoDataValue) continue;
                    lastWeightedX = xi;
                    lastWeightedY = yi;
                    cumulative += v;
                    if (cumulative >= target)
                    {
                        x = xi;
                        y = yi;
                        return true;
                    }
                }
            }

            //floating-point rounding can leave `target` a hair past the summed total; fall
            //back to the last weighted cell visited rather than reporting failure
            if (lastWeightedX < 0) return false;
            x = lastWeightedX;
            y = lastWeightedY;
            return true;
        }
    }
}
