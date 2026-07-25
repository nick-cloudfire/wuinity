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
    }
}
