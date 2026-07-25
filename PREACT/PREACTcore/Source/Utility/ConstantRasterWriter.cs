namespace PREACT.Utility
{
    /// <summary>
    /// Writes a uniform-value raster on a <see cref="MasterGrid"/> — the "constant transient
    /// rasters" fallback docs/probabilistic-trigger-convergence.md allows for per-realization
    /// wind/moisture until the WindNinja/Nelson steps produce spatially-varying ones.
    /// </summary>
    public static class ConstantRasterWriter
    {
        public static void WriteConstant(MasterGrid grid, float value, string outputPath)
        {
            float[,] data = new float[grid.Header.Ncols, grid.Header.Nrows];
            for (int x = 0; x < grid.Header.Ncols; ++x)
            {
                for (int y = 0; y < grid.Header.Nrows; ++y)
                {
                    data[x, y] = value;
                }
            }
            AscRaster.Write(data, grid.Header, outputPath);
        }
    }
}
