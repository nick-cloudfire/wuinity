using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace PREACTcli
{
    /// <summary>
    /// Turns a realization's filename pattern into a path that exists, tolerating the part of an ELMFIRE
    /// dump name nobody chooses: the run's stop time in seconds.
    /// </summary>
    /// <remarks>
    /// ELMFIRE names its dumps <c>&lt;stem&gt;_&lt;7-digit case&gt;_&lt;stop time in seconds&gt;.tif</c>, so an
    /// ensemble's filenames carry the duration it was run for — <c>_0072000</c> for 20 hours. Change how long
    /// the fires run and every filename changes with it, which used to mean editing four patterns by hand and,
    /// if you forgot, a campaign reporting "missing TOA/ROS/SD raster" for every realization about files that
    /// were sitting right there.
    ///
    /// Two ways out, both handled here: a pattern may contain <c>*</c> or <c>?</c> and is globbed, and a pattern
    /// whose last underscore-separated part is a literal number is retried with that number wildcarded.
    ///
    /// The relaxation is decided on the pattern <b>before</b> <c>{i}</c> is substituted, which is the whole
    /// safety of it: in <c>TOA_{i}.tif</c> the trailing digits are the realization index, and wildcarding those
    /// would happily match another realization's fire. Only digits the pattern itself spells out are given up.
    /// </remarks>
    internal static class EnsembleRasters
    {
        private static int _relaxedReported;
        private static int _ambiguousReported;

        /// <summary>
        /// The file for realization <paramref name="idx"/>, or the literal path when nothing matches — so the
        /// caller reports a missing raster by the name that was looked for.
        /// </summary>
        public static string Resolve(string directory, string pattern, string idx)
        {
            if (string.IsNullOrEmpty(pattern)) return null;

            string name = pattern.Replace("{i}", idx);
            string literal = Path.Combine(directory ?? string.Empty, name);

            bool explicitGlob = HasWildcard(name);
            if (!explicitGlob && File.Exists(literal)) return literal;
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return literal;

            //Relaxed from the pattern, then the index substituted into the result, so a wildcard can never
            //stand where the index belongs.
            string glob = explicitGlob ? name : Relax(pattern)?.Replace("{i}", idx);
            if (glob == null) return literal;

            string[] matches = Directory.GetFiles(directory, glob)
                //GetFiles' pattern matching is looser than it looks - "*.tif" also matches ".tiff" - and a
                //raster of the wrong type would be read as this realization's fire.
                .Where(p => string.Equals(Path.GetExtension(p), Path.GetExtension(name), StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length == 0) return literal;

            if (matches.Length > 1)
            {
                //The longest-lived dump, the same rule ElmfireRunner uses on a run directory: it is the fully
                //grown fire, and the shorter ones are earlier snapshots of it.
                matches = matches.OrderByDescending(TrailingNumber).ThenBy(p => p, StringComparer.Ordinal).ToArray();

                if (Interlocked.Exchange(ref _ambiguousReported, 1) == 0)
                {
                    Console.WriteLine($"Realization rasters: '{glob}' matches {matches.Length} files for "
                        + $"realization {idx}; taking the one with the largest trailing number "
                        + $"({Path.GetFileName(matches[0])}). Narrow the pattern if that is not the fire you mean.");
                }
            }
            else if (!explicitGlob && Interlocked.Exchange(ref _relaxedReported, 1) == 0)
            {
                //Said once, because it means the pattern names a duration the ensemble no longer has - worth
                //knowing, and not worth repeating a few hundred times.
                Console.WriteLine($"Realization rasters: '{name}' is not there, so the trailing number was "
                    + $"ignored and '{Path.GetFileName(matches[0])}' used instead.");
            }

            return matches[0];
        }

        private static bool HasWildcard(string name)
        {
            return name.IndexOf('*') >= 0 || name.IndexOf('?') >= 0;
        }

        /// <summary>
        /// The pattern with a trailing literal number replaced by <c>*</c>, or null when its last part is not
        /// one — <c>TOA_{i}.tif</c> has nothing to give up, and must not be relaxed into every realization.
        /// </summary>
        private static string Relax(string pattern)
        {
            string extension = Path.GetExtension(pattern);
            string stem = pattern.Substring(0, pattern.Length - extension.Length);

            int underscore = stem.LastIndexOf('_');
            if (underscore <= 0 || underscore == stem.Length - 1) return null;

            string last = stem.Substring(underscore + 1);
            return last.All(char.IsDigit) ? stem.Substring(0, underscore + 1) + "*" + extension : null;
        }

        /// <summary>The trailing <c>_&lt;digits&gt;</c> of a filename, or -1. Used only to order candidates.</summary>
        private static long TrailingNumber(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int underscore = name.LastIndexOf('_');
            if (underscore < 0 || underscore + 1 >= name.Length) return -1;
            return long.TryParse(name.Substring(underscore + 1), out long value) ? value : -1;
        }
    }
}
