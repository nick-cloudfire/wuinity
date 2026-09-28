using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PREACT.Utility
{
    /// <summary>
    /// A short identity for "what an ELMFIRE result was computed from", so outputs are reused only when nothing
    /// that decides them has changed.
    /// </summary>
    /// <remarks>
    /// Reuse used to be "there is a time-of-arrival raster in outputs/", which reused a stale fire after the
    /// ignition, the stop time, the fuel or the namelist had changed, with a log line as the only signal. The
    /// fingerprint covers the namelist text, the executable, and every file in the directories the namelist
    /// reads from - by name, size and modification time rather than content, which is what a build system
    /// would do: touching a file invalidates, copying a case to another disk usually does too, and neither
    /// failure mode is dangerous (it only costs a rerun).
    /// </remarks>
    public static class ElmfireFingerprint
    {
        /// <summary>Bumped whenever what goes into the fingerprint changes, so old ones never match.</summary>
        private const string Version = "elmfire-run-v1";

        /// <summary>Intermediates GDAL and ELMFIRE leave beside inputs, which change without the data changing.</summary>
        private static readonly string[] IgnoredExtensions = { ".aux.xml", ".bsq", ".hdr", ".bil", ".ovr", ".xml" };

        /// <summary>
        /// The fingerprint of a single run: its namelist, the executable, and the inputs, weather and
        /// miscellaneous directories the namelist names, resolved against <paramref name="runDirectory"/>.
        /// </summary>
        public static string ForRun(string[] namelistLines, string runDirectory, string elmfireExe)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Version);

            foreach (string line in namelistLines ?? new string[0])
            {
                sb.AppendLine(line.TrimEnd());
            }

            sb.AppendLine("exe " + DescribeFile(elmfireExe));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string group, string key) in new[]
                     {
                         (ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.FuelsAndTopographyDirectory),
                         (ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WeatherDirectory),
                         (ElmfireNamelistKeys.MiscellaneousGroup, ElmfireNamelistKeys.MiscellaneousInputsDirectory),
                     })
            {
                string directory = ElmfireStems.ResolveDirectory(namelistLines, group, key, runDirectory);
                if (directory == null || !seen.Add(directory)) continue;
                AppendDirectory(sb, key, directory, null);
            }

            return Hash(sb.ToString());
        }

        /// <summary>
        /// Appends every relevant file of <paramref name="directory"/> (not recursive) as name, size and time.
        /// </summary>
        /// <param name="excludeStems">Stems (file names without extension) to leave out, or null.</param>
        public static void AppendDirectory(StringBuilder sb, string label, string directory, ICollection<string> excludeStems)
        {
            sb.AppendLine("dir " + label);
            if (!Directory.Exists(directory))
            {
                sb.AppendLine("  (missing)");
                return;
            }

            string[] files = Directory.GetFiles(directory);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            foreach (string path in files)
            {
                string name = Path.GetFileName(path);
                if (IsIgnored(name)) continue;
                if (excludeStems != null && excludeStems.Contains(Path.GetFileNameWithoutExtension(name))) continue;
                sb.AppendLine("  " + name + " " + DescribeFile(path));
            }
        }

        /// <summary>A file's size and modification time, or "(missing)".</summary>
        public static string DescribeFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(missing)";
            var info = new FileInfo(path);
            return info.Length.ToString(CultureInfo.InvariantCulture) + " "
                   + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>SHA-256 of a file's content, lower-case hex, or "(missing)".</summary>
        public static string HashFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(missing)";
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return Hex(sha.ComputeHash(stream));
            }
        }

        /// <summary>SHA-256 of a string (UTF-8), lower-case hex.</summary>
        public static string Hash(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty)));
            }
        }

        private static bool IsIgnored(string name)
        {
            foreach (string ext in IgnoredExtensions)
            {
                if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
