using System;
using OSGeo.GDAL;

namespace PREACT.Utility
{
    /// <summary>
    /// Replaces non-finite cells in an ingested raster with a usable value.
    /// </summary>
    /// <remarks>
    /// ELMFIRE is compiled with floating-point traps, so a single NaN anywhere in an input aborts the run with
    /// <c>forrtl: error (65): floating invalid</c> and a traceback pointing at an unrelated line — on the
    /// reference case, Mode-2 code the run never enters. Nothing in that message names the raster, the cell, or
    /// the concept.
    ///
    /// This is not a rare input. The canopy products for the reference case carried NaN in about 23 % of cells,
    /// inherited from sources that declare no nodata value at all, and scrubbing them was a hand-run numpy pass
    /// that had to be repeated after every re-warp.
    ///
    /// Distinct from <see cref="ElmfireCaseValidator"/>, which reports non-finite data and repairs nothing:
    /// validation is the safety net for layers that arrive by other routes, this is the repair on the one path
    /// the builder controls.
    /// </remarks>
    public static class RasterScrubber
    {
        /// <summary>
        /// Replaces every NaN and infinity in <paramref name="path"/> with <paramref name="fill"/>, in place.
        /// Returns how many cells were replaced.
        /// </summary>
        /// <remarks>
        /// <paramref name="fill"/> is the caller's decision because it is a per-layer one: 0 is right for a
        /// canopy void (no canopy, hence surface fire there) and for a mask, and would be sea level in a DEM.
        ///
        /// Row at a time, and only written back when something changed, so a clean raster costs one read pass
        /// and no write.
        /// </remarks>
        public static long ReplaceNonFinite(string path, float fill, Action<string> log = null)
        {
            Gdal.AllRegister();

            Dataset ds = Gdal.Open(path, Access.GA_Update);
            if (ds == null)
            {
                //Not fatal: a raster that cannot be opened for update is a problem the validator will report
                //against the whole case, with more context than this function has.
                log?.Invoke($"    scrub: could not open {path} for update; left as it is.");
                return 0;
            }

            long replaced = 0;

            try
            {
                int nx = ds.RasterXSize;
                int ny = ds.RasterYSize;
                if (nx <= 0 || ny <= 0) return 0;

                float[] row = new float[nx];

                for (int b = 1; b <= ds.RasterCount; ++b)
                {
                    Band band = ds.GetRasterBand(b);

                    for (int y = 0; y < ny; ++y)
                    {
                        band.ReadRaster(0, y, nx, 1, row, nx, 1, 0, 0);

                        bool dirty = false;
                        for (int x = 0; x < nx; ++x)
                        {
                            if (!float.IsNaN(row[x]) && !float.IsInfinity(row[x])) continue;
                            row[x] = fill;
                            dirty = true;
                            ++replaced;
                        }

                        if (dirty)
                        {
                            band.WriteRaster(0, y, nx, 1, row, nx, 1, 0, 0);
                        }
                    }
                }

                if (replaced > 0)
                {
                    ds.FlushCache();
                }
            }
            catch (Exception e)
            {
                log?.Invoke($"    scrub: {path} could not be scrubbed ({e.Message}).");
            }
            finally
            {
                ds.Dispose();
            }

            return replaced;
        }
    }
}
