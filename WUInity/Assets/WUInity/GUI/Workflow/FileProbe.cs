using System;
using System.Collections.Generic;
using System.IO;

namespace WUInity.Workflow
{
    /// <summary>
    /// Cached answers to "does this file exist, how big is it, when was it written", and to questions that
    /// can only be answered by reading it (a raster's dimensions, a CSV's row count) - the latter recomputed
    /// only when the file's size or time stamp changes.
    /// </summary>
    /// <remarks>
    /// The GUI used to ask the file system these questions from inside its draw calls, so every frame paid
    /// for them: a recursive <c>*.sumocfg</c> search of the scenario folder (which on Mati includes hundreds
    /// of campaign realizations), a recursive WindNinja search of the install roots, and a
    /// <c>File.Exists</c> per step button. Everything that wants to know goes through one of these instead,
    /// and the workflow panel refreshes at most once a second.
    ///
    /// No UnityEngine and no ImGui, so the workflow model can be exercised from a plain console program.
    /// Not thread-safe: one instance per thread, which in practice means the main thread's.
    /// </remarks>
    public sealed class FileProbe
    {
        /// <summary>How long a stat is trusted before the file system is asked again.</summary>
        public TimeSpan TimeToLive = TimeSpan.FromSeconds(1.0);

        private struct Stat
        {
            public DateTime CheckedAt;
            public bool Exists;
            public long Length;
            public DateTime LastWriteUtc;
        }

        private struct Derived
        {
            public long Length;
            public DateTime LastWriteUtc;
            public object Value;
        }

        private readonly Dictionary<string, Stat> _files = new Dictionary<string, Stat>(StringComparer.Ordinal);
        private readonly Dictionary<string, Stat> _folders = new Dictionary<string, Stat>(StringComparer.Ordinal);
        private readonly Dictionary<string, Derived> _derived = new Dictionary<string, Derived>(StringComparer.Ordinal);

        /// <summary>Forgets every stat (but keeps derived values, which carry their own time stamp).</summary>
        public void Invalidate()
        {
            _files.Clear();
            _folders.Clear();
        }

        private Stat GetStat(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return new Stat();
            }

            DateTime now = DateTime.UtcNow;
            if (_files.TryGetValue(path, out Stat cached) && now - cached.CheckedAt < TimeToLive)
            {
                return cached;
            }

            var stat = new Stat { CheckedAt = now };
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    stat.Exists = true;
                    stat.Length = info.Length;
                    stat.LastWriteUtc = info.LastWriteTimeUtc;
                }
            }
            catch
            {
                //An unreadable path is the same as a missing one to every caller.
            }

            _files[path] = stat;
            return stat;
        }

        public bool Exists(string path)
        {
            return GetStat(path).Exists;
        }

        /// <summary>The file's size in bytes, or -1 when it does not exist.</summary>
        public long Length(string path)
        {
            Stat s = GetStat(path);
            return s.Exists ? s.Length : -1;
        }

        /// <summary>When the file was last written, or null when it does not exist.</summary>
        public DateTime? LastWriteUtc(string path)
        {
            Stat s = GetStat(path);
            return s.Exists ? s.LastWriteUtc : (DateTime?)null;
        }

        /// <summary>True when <paramref name="a"/> exists and was written after <paramref name="b"/>, which must exist too.</summary>
        public bool IsNewer(string a, string b, double slackSeconds = 1.0)
        {
            DateTime? ta = LastWriteUtc(a);
            DateTime? tb = LastWriteUtc(b);
            return ta.HasValue && tb.HasValue && (ta.Value - tb.Value).TotalSeconds > slackSeconds;
        }

        public bool DirectoryExists(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if (_folders.TryGetValue(path, out Stat cached) && now - cached.CheckedAt < TimeToLive)
            {
                return cached.Exists;
            }

            bool exists = false;
            try { exists = Directory.Exists(path); } catch { }
            _folders[path] = new Stat { CheckedAt = now, Exists = exists };
            return exists;
        }

        /// <summary>
        /// A value computed from the file's contents, recomputed only when its size or time stamp changes.
        /// <paramref name="kind"/> separates different questions about the same file. Returns
        /// <paramref name="fallback"/> when the file does not exist or the computation throws.
        /// </summary>
        public T Read<T>(string path, string kind, Func<string, T> compute, T fallback = default)
        {
            Stat s = GetStat(path);
            if (!s.Exists)
            {
                return fallback;
            }

            string key = kind + "|" + path;
            if (_derived.TryGetValue(key, out Derived d) && d.Length == s.Length && d.LastWriteUtc == s.LastWriteUtc)
            {
                return d.Value is T typed ? typed : fallback;
            }

            object value;
            try
            {
                value = compute(path);
            }
            catch
            {
                value = fallback;
            }

            _derived[key] = new Derived { Length = s.Length, LastWriteUtc = s.LastWriteUtc, Value = value };
            return value is T result ? result : fallback;
        }
    }
}
