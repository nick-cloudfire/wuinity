using System;
using System.Collections.Generic;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// Folds an ensemble's time-of-arrival rasters into per-cell fire statistics: how often each cell burns,
    /// and when.
    /// </summary>
    /// <remarks>
    /// The campaign aggregated only the trigger boundary — the fraction of realizations whose boundary enclosed
    /// each cell. That answers "when must this area leave", and says nothing about the fire itself: which ground
    /// is threatened at all, how often, and how soon. Both come out of rasters the campaign already has and was
    /// discarding after reading the boundary.
    ///
    /// Everything is accumulated in one pass per realization with no per-realization storage, because a
    /// campaign is hundreds of realizations of a 566x541 grid and holding them would be gigabytes. Mean and
    /// earliest arrival need only running sums; percentiles are taken from a per-cell histogram over the run's
    /// own duration, which is bounded and modest — 72 hourly bins over three days is 44 MB for that grid, as
    /// against 245 MB to store 200 realizations outright.
    ///
    /// The histogram makes percentiles approximate to one bin. That is far finer than the question needs: an
    /// evacuation decision turns on which hour a fire arrives, not which minute, and the arrival times
    /// themselves carry more uncertainty than a bin's width.
    /// </remarks>
    public class EnsembleFireStatistics
    {
        private readonly int _nx;
        private readonly int _ny;
        private readonly double _binSeconds;
        private readonly int _binCount;

        private readonly int[] _burned;        //realizations in which this cell burned
        private readonly double[] _sum;        //sum of arrival times over those realizations
        private readonly float[] _earliest;    //earliest arrival seen
        private readonly ushort[] _histogram;  //[cell * binCount + bin]

        /// <summary>Realizations folded in, burned or not — the denominator for burn probability.</summary>
        public int Realizations { get; private set; }

        public AscRaster.Header Header { get; }

        /// <summary>
        /// <paramref name="durationSeconds"/> is how long a realization simulates, which sets the histogram's
        /// range. <paramref name="binSeconds"/> is the percentile resolution; one hour by default.
        /// </summary>
        public EnsembleFireStatistics(AscRaster.Header header, double durationSeconds, double binSeconds = 3600.0)
        {
            Header = header;
            _nx = header.Ncols;
            _ny = header.Nrows;

            _binSeconds = binSeconds > 0 ? binSeconds : 3600.0;

            //At least one bin, and one spare at the end: a cell reached exactly at tstop lands one past the
            //last whole bin, and clamping it into the previous one would report it as arriving an hour early.
            _binCount = (int)System.Math.Ceiling(System.Math.Max(durationSeconds, _binSeconds) / _binSeconds) + 1;

            int cells = _nx * _ny;
            _burned = new int[cells];
            _sum = new double[cells];
            _earliest = new float[cells];
            _histogram = new ushort[(long)cells * _binCount <= int.MaxValue
                ? cells * _binCount
                : throw new ArgumentException(
                    $"An ensemble histogram of {cells} cells x {_binCount} bins does not fit in one array. "
                    + "Use a coarser binSeconds.")];

            for (int i = 0; i < cells; ++i) _earliest[i] = float.MaxValue;
        }

        /// <summary>
        /// Folds in one realization's arrival times, in <b>seconds</b>.
        /// </summary>
        /// <remarks>
        /// A cell counts as burned when its arrival time is finite and non-negative. Unburned cells are the
        /// large sentinel or the nodata value depending on the writer, so the test is for a plausible time
        /// rather than for any particular marker — which is what lets this read ELMFIRE's own output and a
        /// re-exported copy of it without being told which it has.
        /// </remarks>
        public void Add(float[,] timeOfArrivalSeconds)
        {
            if (timeOfArrivalSeconds == null) return;

            if (timeOfArrivalSeconds.GetLength(0) != _nx || timeOfArrivalSeconds.GetLength(1) != _ny)
            {
                throw new ArgumentException(
                    $"Arrival raster is {timeOfArrivalSeconds.GetLength(0)}x{timeOfArrivalSeconds.GetLength(1)} "
                    + $"but the ensemble is {_nx}x{_ny}.", nameof(timeOfArrivalSeconds));
            }

            //Counted even when nothing in it burned. A realization whose fire went the other way is evidence
            //about this ground - it is the reason a cell's probability is below 1 - so leaving it out of the
            //denominator would inflate every probability towards certainty.
            ++Realizations;

            //The ceiling exists because the histogram counts per realization in a ushort. Reaching it would
            //silently wrap to zero and start the count again.
            if (Realizations > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    $"More than {ushort.MaxValue} realizations; the per-cell histogram cannot count them.");
            }

            for (int y = 0; y < _ny; ++y)
            {
                for (int x = 0; x < _nx; ++x)
                {
                    float t = timeOfArrivalSeconds[x, y];

                    if (float.IsNaN(t) || float.IsInfinity(t) || t < 0f || t >= LargeSentinel) continue;

                    int cell = Index(x, y);
                    ++_burned[cell];
                    _sum[cell] += t;
                    if (t < _earliest[cell]) _earliest[cell] = t;

                    int bin = (int)(t / _binSeconds);
                    if (bin < 0) bin = 0;
                    if (bin >= _binCount) bin = _binCount - 1;
                    ++_histogram[cell * _binCount + bin];
                }
            }
        }

        /// <summary>
        /// Anything at or above this is not an arrival time. Covers the distance-transform sentinel, the
        /// unreached marker a re-export may carry, and float.MaxValue.
        /// </summary>
        private const float LargeSentinel = 1e11f;

        /// <summary>
        /// Fraction of realizations in which each cell burned. 0 where none did.
        /// </summary>
        public float[,] BurnProbability()
        {
            var result = new float[_nx, _ny];
            if (Realizations == 0) return result;

            for (int y = 0; y < _ny; ++y)
            {
                for (int x = 0; x < _nx; ++x)
                {
                    result[x, y] = (float)_burned[Index(x, y)] / Realizations;
                }
            }

            return result;
        }

        /// <summary>
        /// Mean arrival time in seconds, <b>over the realizations in which the cell burned</b>. Cells that
        /// never burned come back as the nodata value.
        /// </summary>
        /// <remarks>
        /// Conditional on burning, not averaged over all realizations. Averaging a non-arrival in as a zero
        /// would make rarely-burned cells look like the first to go, and averaging it in as tstop would make
        /// them look safest — both are statements about how often the cell burns, which is what
        /// <see cref="BurnProbability"/> is for. Read the two together.
        /// </remarks>
        public float[,] MeanArrivalSeconds()
        {
            var result = new float[_nx, _ny];

            for (int y = 0; y < _ny; ++y)
            {
                for (int x = 0; x < _nx; ++x)
                {
                    int cell = Index(x, y);
                    result[x, y] = _burned[cell] > 0
                        ? (float)(_sum[cell] / _burned[cell])
                        : (float)Header.NoDataValue;
                }
            }

            return result;
        }

        /// <summary>Earliest arrival seen in any realization, seconds. Nodata where the cell never burned.</summary>
        public float[,] EarliestArrivalSeconds()
        {
            var result = new float[_nx, _ny];

            for (int y = 0; y < _ny; ++y)
            {
                for (int x = 0; x < _nx; ++x)
                {
                    int cell = Index(x, y);
                    result[x, y] = _burned[cell] > 0 ? _earliest[cell] : (float)Header.NoDataValue;
                }
            }

            return result;
        }

        /// <summary>
        /// A percentile of arrival time, seconds, over the realizations in which the cell burned.
        /// </summary>
        /// <remarks>
        /// <paramref name="percentile"/> is a fraction. The low percentiles are the conservative ones: the 10th
        /// is "when the fire arrives in the fastest tenth of the cases it arrives at all", which is the figure
        /// an evacuation has to survive, where the median describes a typical fire nobody is planning for.
        ///
        /// Resolved to a bin, and reported at the bin's <b>lower edge</b>, so the answer is never later than
        /// the truth. Rounding a conservative arrival time upwards would be the one direction that matters.
        /// </remarks>
        public float[,] PercentileArrivalSeconds(double percentile)
        {
            if (percentile < 0.0) percentile = 0.0;
            if (percentile > 1.0) percentile = 1.0;

            var result = new float[_nx, _ny];

            for (int y = 0; y < _ny; ++y)
            {
                for (int x = 0; x < _nx; ++x)
                {
                    int cell = Index(x, y);
                    int burned = _burned[cell];

                    if (burned == 0)
                    {
                        result[x, y] = (float)Header.NoDataValue;
                        continue;
                    }

                    //The first bin at which the cumulative count reaches the requested fraction. Ceiling, and
                    //at least one, so p=0 asks for the earliest observation rather than for nothing.
                    int target = (int)System.Math.Ceiling(percentile * burned);
                    if (target < 1) target = 1;

                    int running = 0;
                    int found = _binCount - 1;
                    int offset = cell * _binCount;

                    for (int b = 0; b < _binCount; ++b)
                    {
                        running += _histogram[offset + b];
                        if (running >= target) { found = b; break; }
                    }

                    result[x, y] = (float)(found * _binSeconds);
                }
            }

            return result;
        }

        /// <summary>
        /// Writes the whole set beside the campaign's other output, named so they sort together.
        /// </summary>
        /// <remarks>
        /// Percentiles are written as whole percents in the filename (<c>p10</c>), and arrival times in
        /// <b>seconds</b> like every other arrival time in this codebase. Returns what it wrote, for reporting.
        /// </remarks>
        public List<string> WriteAll(string outputDirectory, string prefix = "ensemble",
            double[] percentiles = null, Action<string> log = null)
        {
            var written = new List<string>();

            if (Realizations == 0)
            {
                log?.Invoke("  ensemble: no realizations were folded in, so no fire statistics were written.");
                return written;
            }

            percentiles ??= new[] { 0.1, 0.5, 0.9 };

            Directory.CreateDirectory(outputDirectory);

            Write(outputDirectory, prefix + "_burn_probability.asc", BurnProbability(), written);
            Write(outputDirectory, prefix + "_arrival_earliest.asc", EarliestArrivalSeconds(), written);
            Write(outputDirectory, prefix + "_arrival_mean.asc", MeanArrivalSeconds(), written);

            foreach (double p in percentiles)
            {
                string name = $"{prefix}_arrival_p{(int)System.Math.Round(p * 100.0)}.asc";
                Write(outputDirectory, name, PercentileArrivalSeconds(p), written);
            }

            log?.Invoke($"  ensemble: {written.Count} fire statistic raster(s) from {Realizations} realization(s), "
                        + $"arrival times in seconds, percentiles to {_binSeconds / 3600.0:F1} h.");

            return written;
        }

        private void Write(string directory, string name, float[,] data, List<string> written)
        {
            string path = Path.Combine(directory, name);
            AscRaster.Write(data, Header, path);
            written.Add(path);
        }

        private int Index(int x, int y) => y * _nx + x;
    }
}
