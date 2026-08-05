namespace PREACT.Math
{
    /// <summary>
    /// Explicitly-seeded RNG for the probabilistic-trigger Monte Carlo (docs/probabilistic-
    /// trigger-convergence.md), which needs one seed per run to make a converged result
    /// reproducible. Deliberately separate from <see cref="Random"/>, whose single process-
    /// wide unseeded instance cannot be reset or reproduced.
    /// </summary>
    public class MonteCarloRng
    {
        private readonly System.Random _random;

        public int Seed { get; }

        public MonteCarloRng(int seed)
        {
            Seed = seed;
            _random = new System.Random(seed);
        }

        /// <summary>Uniform double in [0, 1).</summary>
        public double NextDouble()
        {
            return _random.NextDouble();
        }

        /// <summary>Uniform double in [minInclusive, maxExclusive).</summary>
        public double Range(double minInclusive, double maxExclusive)
        {
            return minInclusive + _random.NextDouble() * (maxExclusive - minInclusive);
        }

        /// <summary>Uniform int in [minInclusive, maxExclusive).</summary>
        public int Range(int minInclusive, int maxExclusive)
        {
            return _random.Next(minInclusive, maxExclusive);
        }

        /// <summary>
        /// A draw from the normal distribution N(mean, standardDeviation), by the polar form of
        /// Box-Muller.
        /// </summary>
        /// <remarks>
        /// The polar (Marsaglia) form rather than the trigonometric one because it needs no sine or
        /// cosine, and the second of the pair it generates is kept for the next call rather than
        /// discarded — so a run of draws costs half the rejections.
        ///
        /// A zero or negative standard deviation returns the mean, which is how a fitted parameter
        /// with a single sample behind it (one year in the record) degrades: no spread, not a NaN.
        /// </remarks>
        public double NextNormal(double mean, double standardDeviation)
        {
            if (!(standardDeviation > 0.0)) return mean;
            return mean + NextStandardNormal() * standardDeviation;
        }

        /// <summary>
        /// A draw from N(0, 1), by the polar (Marsaglia) form of Box-Muller.
        /// </summary>
        /// <remarks>
        /// Private because <see cref="NextNormal"/> and <see cref="NextNormalInRange"/> are the whole
        /// public surface today. Worth promoting if correlated draws are ever wanted — two quantities
        /// that must move together have to share one deviate rather than take one each — but an unused
        /// public method is a worse thing to leave behind than a one-line change.
        /// </remarks>
        private double NextStandardNormal()
        {
            if (_haveSpareNormal)
            {
                _haveSpareNormal = false;
                return _spareNormal;
            }

            double u, v, s;
            do
            {
                u = 2.0 * _random.NextDouble() - 1.0;
                v = 2.0 * _random.NextDouble() - 1.0;
                s = u * u + v * v;
            }
            while (s >= 1.0 || s == 0.0);

            double factor = System.Math.Sqrt(-2.0 * System.Math.Log(s) / s);
            _spareNormal = v * factor;
            _haveSpareNormal = true;

            return u * factor;
        }

        private double _spareNormal;
        private bool _haveSpareNormal;

        /// <summary>
        /// A normal draw confined to [min, max] by redrawing rather than by clamping.
        /// </summary>
        /// <remarks>
        /// Clamping would pile every rejected draw onto the bound itself, which for a parameter
        /// whose fitted mean sits near one — relative humidity on peak fire-weather days is not far
        /// from zero — turns a tail into a spike of identical realizations. Redrawing keeps the
        /// shape of the truncated normal.
        ///
        /// The attempt cap exists because a fit whose mean lies outside [min, max] would otherwise
        /// spin forever; past it the value is clamped, which is wrong but bounded, and the caller
        /// can see it happened because the result sits exactly on a bound.
        /// </remarks>
        public double NextNormalInRange(double mean, double standardDeviation, double min, double max)
        {
            const int attempts = 64;

            for (int i = 0; i < attempts; ++i)
            {
                double v = NextNormal(mean, standardDeviation);
                if (v >= min && v <= max) return v;
            }

            return System.Math.Min(System.Math.Max(mean, min), max);
        }
    }
}
