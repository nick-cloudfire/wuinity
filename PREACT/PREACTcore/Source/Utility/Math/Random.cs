namespace PREACT.Math
{
    /// <summary>
    /// The shared source of randomness for everything stochastic in a run: departure times drawn from a
    /// response curve, walking speeds, household sizes, destination choice, SUMO start positions.
    /// </summary>
    /// <remarks>
    /// <b>Per thread, not per process.</b> This was one <c>static System.Random</c> shared by everything, and
    /// <c>Engine.RunSimulationsParallel</c> runs a batch of simulations on concurrent tasks — so several
    /// simulations drew from one <c>System.Random</c> at once. That type is explicitly not thread-safe, and
    /// concurrent use corrupts its internal state; the classic symptom is that it starts returning 0 from every
    /// call and never recovers, which here would look like every household leaving at once and every walking
    /// speed pinned to its minimum. Silent, and only under parallel batches.
    ///
    /// <b>Seedable, so a run can be repeated.</b> Unseeded, <c>System.Random</c> takes its state from the clock
    /// and no run could be reproduced — awkward for a platform whose output is a probability distribution,
    /// because a surprising realization could not be re-examined. <see cref="SeedForSimulation"/> is called at
    /// the start of each simulation with the scenario's seed and the simulation's index, so run <i>n</i> of a
    /// batch draws the same numbers however many other runs share the process and whatever order they finish
    /// in.
    ///
    /// Each simulation seeds the thread it is about to run on, which is sound because a simulation owns its
    /// thread for the whole of its <c>Run()</c> — the work is synchronous inside the task, so two simulations
    /// cannot interleave on one thread and overwrite each other's stream.
    /// </remarks>
    public class Random
    {
        /// <summary>
        /// The base seed. 0 means "not seeded": every thread then gets a clock-based stream, which is the
        /// behaviour this had before and remains the default.
        /// </summary>
        private static int _baseSeed;

        [System.ThreadStatic]
        private static System.Random _threadRandom;

        private static System.Random Current
        {
            get
            {
                //Lazily, because a thread that never draws should not pay for one - and because a thread the
                //engine did not seed (a tool, an editor, a test) still needs a working generator.
                if (_threadRandom == null)
                {
                    _threadRandom = _baseSeed == 0
                        ? new System.Random()
                        : new System.Random(Mix(_baseSeed, System.Environment.CurrentManagedThreadId));
                }

                return _threadRandom;
            }
        }

        /// <summary>
        /// Sets the scenario's base seed. 0 restores clock-based, non-reproducible behaviour.
        /// </summary>
        /// <remarks>
        /// Does not itself reseed any thread: threads already running keep the stream they are partway through,
        /// which is what stops a seed set mid-batch from making two simulations draw identical numbers.
        /// </remarks>
        public static void SetBaseSeed(int seed)
        {
            _baseSeed = seed;
        }

        /// <summary>
        /// Gives the calling thread the stream belonging to one simulation. Called once, at the start of a run.
        /// </summary>
        /// <remarks>
        /// The index is mixed in rather than added, so that seed 1 / run 2 and seed 2 / run 1 are different
        /// streams. Adding them would make those identical, and a campaign that varies the seed per batch
        /// would silently repeat realizations across batches.
        /// </remarks>
        public static void SeedForSimulation(int baseSeed, int simulationIndex)
        {
            if (baseSeed == 0)
            {
                //Explicitly a fresh clock-based stream rather than leaving whatever this thread had: a pooled
                //thread reused by a later simulation would otherwise continue the previous one's sequence,
                //which is not wrong but makes "unseeded" mean something different on run 1 than on run 20.
                _threadRandom = new System.Random();
                return;
            }

            _threadRandom = new System.Random(Mix(baseSeed, simulationIndex));
        }

        /// <summary>
        /// Combines two ints into a seed that changes a lot when either changes a little.
        /// </summary>
        /// <remarks>
        /// Consecutive seeds matter here: a batch uses indices 0, 1, 2, … and <c>System.Random</c>'s legacy
        /// seeding gives visibly similar early output for nearby seeds, so consecutive realizations would start
        /// out correlated. This is the 32-bit finalizer from MurmurHash3, used for scrambling rather than
        /// hashing.
        /// </remarks>
        private static int Mix(int a, int b)
        {
            unchecked
            {
                uint h = (uint)a * 2654435761u + (uint)b;
                h ^= h >> 16;
                h *= 0x85EBCA6Bu;
                h ^= h >> 13;
                h *= 0xC2B2AE35u;
                h ^= h >> 16;

                //int.MinValue has no positive counterpart, and System.Random rejects a negative seed.
                int seed = (int)(h & 0x7FFFFFFF);
                return seed == 0 ? 1 : seed;
            }
        }

        public static float Range(float minInclusive, float maxExlusive)
        {
            return (float)(minInclusive + Current.NextDouble() * (maxExlusive - minInclusive));
        }

        public static double Range(double minInclusive, double maxExlusive)
        {
            return minInclusive + Current.NextDouble() * (maxExlusive - minInclusive);
        }

        /// <summary>
        /// Will result in overflow if using full int range as input.
        /// </summary>
        public static int Range(int minInclusive, int maxExclusive)
        {
            return Current.Next(minInclusive, maxExclusive);
        }

        /// <summary>
        /// Random float from 0 (inclusive) to 1 (exclusive).
        /// </summary>
        public static float valueF
        {
            get { return (float)Current.NextDouble(); }
        }

        /// <summary>
        /// Random double from 0 (inclusive) to 1 (exclusive).
        /// </summary>
        public static double valueD
        {
            get { return Current.NextDouble(); }
        }
    }
}
