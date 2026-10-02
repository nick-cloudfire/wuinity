//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace PREACT.Utility
{
    /// <summary>
    /// The per-user tool paths: where ELMFIRE, GDAL's command-line tools, WindNinja, SUMO and PROJ's data are on
    /// this machine, when the automatic search does not find them or finds the wrong one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One small ini file per user, outside any scenario and outside the repository, so it is read by every
    /// program of the platform the same way - WUInity, <c>PREACT.exe</c> and <c>PREACTcli</c>, and so a campaign
    /// started from the GUI uses the tools the GUI does. A scenario is meant to be shared and a tool path is a
    /// property of a machine, which is why these are not scenario keys.
    /// </para>
    /// <para>
    /// Where: <see cref="FileVariable"/> when it is set (tests, or a second set of tools); otherwise
    /// <c>%APPDATA%\PREACT\tools.ini</c> on Windows, and <c>$XDG_CONFIG_HOME/PREACT/tools.ini</c> or
    /// <c>~/.config/PREACT/tools.ini</c> elsewhere.
    /// </para>
    /// <para>
    /// Priority, for every tool: a value the scenario or the command line names explicitly, then this file, then
    /// the automatic search. Each resolver asks <see cref="UserSetting"/> before it searches; the explicit values
    /// stay with their callers, which already put them first. A setting that does not hold what it should (the
    /// file was moved, the folder has no such program) is skipped, so the automatic search still gets a chance,
    /// and <see cref="UserSettingProblem"/> says why - Help &gt; External tools and keys shows it.
    /// </para>
    /// <para>
    /// The file is re-read when it changes on disk, so a save from the GUI reaches the resolvers of the running
    /// process at once, and a hand edit reaches them on the next lookup. Thread-safe.
    /// </para>
    /// </remarks>
    public static class ToolPaths
    {
        /// <summary>The environment variable that names the settings file instead of the per-user default.</summary>
        public const string FileVariable = "PREACT_TOOLS_FILE";

        /// <summary>The tools this file can name.</summary>
        public enum Tool
        {
            Elmfire,
            Gdal,
            WindNinja,
            Sumo,
            Proj,
        }

        /// <summary>Where the path a tool is used from came from.</summary>
        public enum Source
        {
            /// <summary>Nothing was found.</summary>
            None,

            /// <summary>Named by the scenario or on the command line.</summary>
            Explicit,

            /// <summary>This settings file.</summary>
            UserSetting,

            /// <summary>The tool's own search: environment variables, PATH, the usual install folders.</summary>
            Automatic,
        }

        public static readonly Tool[] AllTools = { Tool.Elmfire, Tool.Gdal, Tool.WindNinja, Tool.Sumo, Tool.Proj };

        private static readonly object _lock = new object();
        private static Settings _cached;
        private static string _cachedPath;
        private static DateTime _cachedWriteTime;
        private static long _cachedLength = -1;
        private static string _readError;
        private static int _version;

        private static bool OnWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // ------------------------------------------------------------------ the file

        /// <summary>The settings file this process reads and writes. It need not exist.</summary>
        public static string SettingsFile
        {
            get
            {
                string named = Environment.GetEnvironmentVariable(FileVariable);
                if (!string.IsNullOrWhiteSpace(named))
                {
                    return named.Trim().Trim('"');
                }

                string appData = null;
                try
                {
                    appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                }
                catch
                {
                    //an unusual profile; the environment below is the fallback
                }
                if (string.IsNullOrEmpty(appData))
                {
                    appData = Environment.GetEnvironmentVariable("APPDATA");
                }

                string home = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home))
                {
                    try
                    {
                        home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    }
                    catch
                    {
                        home = null;
                    }
                }

                return DefaultSettingsFile(OnWindows, appData, Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), home);
            }
        }

        /// <summary>The per-user default, from the given folders (parameters so the rule can be tested anywhere).</summary>
        internal static string DefaultSettingsFile(bool windows, string appData, string xdgConfigHome, string home)
        {
            string folder;
            if (windows)
            {
                folder = !string.IsNullOrEmpty(appData) ? appData : home;
            }
            else
            {
                //The XDG base directory rule: a relative XDG_CONFIG_HOME is invalid and is ignored.
                folder = !string.IsNullOrEmpty(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome)
                    ? xdgConfigHome
                    : !string.IsNullOrEmpty(home) ? Path.Combine(home, ".config") : null;
            }

            return Path.Combine(folder ?? ".", "PREACT", "tools.ini");
        }

        /// <summary>
        /// Increases whenever the settings in force change - a save, or the file changed on disk - so a cache built
        /// on them can tell it is stale.
        /// </summary>
        public static int Version
        {
            get
            {
                lock (_lock)
                {
                    Current();
                    return _version;
                }
            }
        }

        /// <summary>Why the settings file could not be read, or null. The file is then treated as empty.</summary>
        public static string ReadError
        {
            get
            {
                lock (_lock)
                {
                    Current();
                    return _readError;
                }
            }
        }

        /// <summary>A copy of the settings in force. Empty when there is no file.</summary>
        public static Settings Load()
        {
            lock (_lock)
            {
                return Current().Clone();
            }
        }

        /// <summary>
        /// Writes <paramref name="settings"/> to <see cref="SettingsFile"/>, creating its folder, and makes them the
        /// settings in force for this process. Throws when the file cannot be written.
        /// </summary>
        /// <remarks>
        /// Written beside the target and moved over it, so a reader in another process (a campaign's PREACT.exe
        /// starting at that moment) sees the old file or the new one, never half of one.
        /// </remarks>
        public static void Save(Settings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            lock (_lock)
            {
                string path = SettingsFile;
                string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                Settings cleaned = settings.Cleaned();
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, cleaned.Write(), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporary, path, null);
                    }
                    catch (Exception e) when (e is PlatformNotSupportedException || e is IOException || e is UnauthorizedAccessException)
                    {
                        //A file system without an atomic replace (some network shares, Mono off Windows).
                        File.Copy(temporary, path, true);
                        File.Delete(temporary);
                    }
                }
                else
                {
                    File.Move(temporary, path);
                }

                _cached = cleaned;
                _cachedPath = path;
                Stamp(path, out _cachedWriteTime, out _cachedLength);
                _readError = null;
                ++_version;
            }
        }

        /// <summary>The cached settings, re-read when the file named, its time or its size changed. Under the lock.</summary>
        private static Settings Current()
        {
            string path = SettingsFile;
            Stamp(path, out DateTime writeTime, out long length);
            if (_cached != null && path == _cachedPath && writeTime == _cachedWriteTime && length == _cachedLength)
            {
                return _cached;
            }

            Settings read = new Settings();
            string error = null;
            if (length >= 0)
            {
                try
                {
                    read = Settings.Parse(File.ReadAllLines(path, Encoding.UTF8));
                }
                catch (Exception e)
                {
                    error = $"{path} could not be read ({e.Message}); every tool is searched for automatically.";
                    read = new Settings();
                }
            }

            bool changed = _cached == null || !_cached.SameAs(read);
            _cached = read;
            _cachedPath = path;
            _cachedWriteTime = writeTime;
            _cachedLength = length;
            _readError = error;
            if (changed)
            {
                ++_version;
            }
            return _cached;
        }

        private static void Stamp(string path, out DateTime writeTime, out long length)
        {
            writeTime = DateTime.MinValue;
            length = -1;
            try
            {
                var info = new FileInfo(path);
                if (info.Exists)
                {
                    writeTime = info.LastWriteTimeUtc;
                    length = info.Length;
                }
            }
            catch
            {
                //an unusable path reads as "no file"
            }
        }

        // ------------------------------------------------------------------ what the resolvers ask

        /// <summary>
        /// The tool's path from the settings file, made usable - an executable's full path for ELMFIRE and
        /// WindNinja, the folder holding the programs for GDAL and SUMO, the folder holding <c>proj.db</c> for PROJ -
        /// or null when the file names none or names one that does not hold the tool.
        /// </summary>
        public static string UserSetting(Tool tool)
        {
            string value;
            lock (_lock)
            {
                value = Current().Get(tool);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Validate(tool, value, out string normalised) == null ? normalised : null;
        }

        /// <summary>Why the saved setting for <paramref name="tool"/> is not used, or null when it is (or there is none).</summary>
        public static string UserSettingProblem(Tool tool)
        {
            string value;
            lock (_lock)
            {
                value = Current().Get(tool);
            }

            return string.IsNullOrWhiteSpace(value) ? null : Validate(tool, value, out _);
        }

        /// <summary>
        /// Where a tool in use came from: <see cref="Source.Explicit"/> when the scenario or the command line named
        /// it (<paramref name="explicitValue"/> not empty), the settings file when <paramref name="inUse"/> is what
        /// it names, otherwise the automatic search. <see cref="Source.None"/> when nothing is in use.
        /// </summary>
        public static Source Classify(Tool tool, string inUse, string explicitValue)
        {
            if (!string.IsNullOrWhiteSpace(explicitValue))
            {
                return Source.Explicit;
            }
            if (string.IsNullOrWhiteSpace(inUse))
            {
                return Source.None;
            }

            string setting = UserSetting(tool);
            return setting != null && SamePath(setting, inUse) ? Source.UserSetting : Source.Automatic;
        }

        /// <summary>
        /// PROJ's search paths for this process's GDAL: the settings file's PROJ folder; else <c>PROJ_DATA</c> and
        /// <c>PROJ_LIB</c> (the folders that exist); else <c>/usr/share/proj</c> off Windows. Empty when there is none,
        /// which leaves GDAL's own compiled-in search alone.
        /// </summary>
        /// <param name="detail">Where the paths came from, for a message: "your setting", "PROJ_DATA", ...</param>
        public static string[] ProjSearchPaths(out Source source, out string detail)
        {
            string setting = UserSetting(Tool.Proj);
            if (setting != null)
            {
                source = Source.UserSetting;
                detail = "your setting";
                return new[] { setting };
            }

            var paths = new List<string>();
            var from = new List<string>();
            foreach (string variable in new[] { "PROJ_DATA", "PROJ_LIB" })
            {
                string candidate = Environment.GetEnvironmentVariable(variable);
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate.Trim()) && !paths.Contains(candidate.Trim()))
                {
                    paths.Add(candidate.Trim());
                    from.Add(variable);
                }
            }
            if (paths.Count == 0 && !OnWindows && Directory.Exists("/usr/share/proj"))
            {
                paths.Add("/usr/share/proj");
                from.Add("/usr/share/proj");
            }

            source = paths.Count > 0 ? Source.Automatic : Source.None;
            detail = string.Join(", ", from);
            return paths.ToArray();
        }

        // ------------------------------------------------------------------ validation

        /// <summary>The file each tool is recognised by, on this platform.</summary>
        public static string ExpectedFile(Tool tool)
        {
            string exe = OnWindows ? ".exe" : string.Empty;
            switch (tool)
            {
                case Tool.Elmfire: return "elmfire" + exe;
                case Tool.Gdal: return "gdal_translate" + exe;
                case Tool.WindNinja: return "WindNinja_cli" + exe;
                case Tool.Sumo: return "sumo" + exe;
                default: return "proj.db";
            }
        }

        /// <summary>What the field for <paramref name="tool"/> takes, for a label or a message.</summary>
        public static string Describe(Tool tool)
        {
            switch (tool)
            {
                case Tool.Elmfire: return $"the ELMFIRE executable ({ExpectedFile(tool)}), or the folder holding it";
                case Tool.Gdal: return $"the folder holding GDAL's command-line tools ({ExpectedFile(tool)}), e.g. a QGIS or OSGeo4W bin";
                case Tool.WindNinja: return $"WindNinja's command-line solver ({ExpectedFile(tool)}), or the folder holding it";
                case Tool.Sumo: return $"SUMO's install folder (SUMO_HOME) or its bin folder (holding {ExpectedFile(tool)})";
                default: return "PROJ's data folder, the one holding proj.db (a QGIS or OSGeo4W share\\proj)";
            }
        }

        /// <summary>
        /// Checks a path typed for <paramref name="tool"/>: null when it is usable (or empty, meaning "search
        /// automatically"), otherwise why not. <paramref name="normalised"/> is what the resolver will use.
        /// </summary>
        /// <remarks>
        /// Forgiving about which level was given - the program or its folder, an install root above a <c>bin</c>,
        /// SUMO's home or its <c>bin</c> - since each is an obvious thing to pick, and strict about the rest: the path
        /// must be absolute (relative to what?) and must hold the program, by name.
        /// </remarks>
        public static string Validate(Tool tool, string value, out string normalised)
        {
            normalised = null;
            string cleaned = Clean(value);
            if (cleaned.Length == 0)
            {
                return null;
            }

            if (!Path.IsPathRooted(cleaned))
            {
                return $"{cleaned} is not a full path. Give it from the drive{(OnWindows ? " (C:\\...)" : " (/...)")}.";
            }

            string full;
            try
            {
                full = Path.GetFullPath(cleaned);
            }
            catch (Exception e)
            {
                return $"{cleaned} is not a usable path: {e.Message}";
            }

            bool isFile = File.Exists(full);
            bool isFolder = !isFile && Directory.Exists(full);
            if (!isFile && !isFolder)
            {
                return $"{full} is not there.";
            }

            string expected = ExpectedFile(tool);
            switch (tool)
            {
                case Tool.Elmfire:
                    if (isFile)
                    {
                        if (OnWindows && !full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            return $"{Path.GetFileName(full)} is not a program; give elmfire.exe.";
                        }
                        normalised = full;
                        return null;
                    }
                    normalised = FirstFile(full, expected, "bin", Path.Combine("build", OnWindows ? "windows" : "linux", "bin"));
                    return normalised != null ? null : $"{full} holds no {expected} (nor its bin or build{Path.DirectorySeparatorChar}{(OnWindows ? "windows" : "linux")}{Path.DirectorySeparatorChar}bin).";

                case Tool.WindNinja:
                    if (isFile)
                    {
                        if (!string.Equals(Path.GetFileName(full), expected, StringComparison.OrdinalIgnoreCase))
                        {
                            return $"{Path.GetFileName(full)} is not WindNinja's command-line solver; give {expected} (WindNinja.exe is its window).";
                        }
                        normalised = full;
                        return null;
                    }
                    normalised = FirstFile(full, expected, "bin");
                    return normalised != null ? null : $"{full} holds no {expected} (nor its bin).";

                case Tool.Gdal:
                {
                    string folder = isFile ? Path.GetDirectoryName(full) : full;
                    string found = FirstFile(folder, expected, "bin");
                    if (found == null)
                    {
                        return $"{folder} holds no {expected} (nor its bin).";
                    }
                    normalised = Path.GetDirectoryName(found);
                    return null;
                }

                case Tool.Sumo:
                {
                    string folder = isFile ? Path.GetDirectoryName(full) : full;
                    string found = FirstFile(folder, expected, "bin");
                    if (found == null)
                    {
                        return $"{folder} holds no {expected} (nor its bin), so it is neither SUMO's bin nor SUMO_HOME.";
                    }
                    normalised = Path.GetDirectoryName(found);
                    return null;
                }

                default:
                {
                    string folder = isFile ? Path.GetDirectoryName(full) : full;
                    string found = FirstFile(folder, expected, Path.Combine("share", "proj"));
                    if (found == null)
                    {
                        return $"{folder} holds no proj.db (nor its share{Path.DirectorySeparatorChar}proj).";
                    }
                    normalised = Path.GetDirectoryName(found);
                    return null;
                }
            }
        }

        /// <summary><paramref name="name"/> in <paramref name="folder"/> or in one of its <paramref name="below"/> folders, or null.</summary>
        private static string FirstFile(string folder, string name, params string[] below)
        {
            var candidates = new List<string> { Path.Combine(folder, name) };
            foreach (string sub in below)
            {
                candidates.Add(Path.Combine(folder, sub, name));
            }

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    }
                }
                catch
                {
                    //an unusable combination is simply not the tool
                }
            }
            return null;
        }

        /// <summary>A pasted path without the white space and the quotes Explorer's "Copy as path" adds.</summary>
        public static string Clean(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            string trimmed = value.Trim();
            if (trimmed.Length >= 2 && ((trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
                                        || (trimmed[0] == '\'' && trimmed[trimmed.Length - 1] == '\'')))
            {
                trimmed = trimmed.Substring(1, trimmed.Length - 2).Trim();
            }
            return trimmed;
        }

        /// <summary>The same file or folder, by full path; case-insensitively on Windows.</summary>
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return false;
            }

            try
            {
                string fa = Path.GetFullPath(Clean(a)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fb = Path.GetFullPath(Clean(b)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(fa, fb, OnWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        // ------------------------------------------------------------------ the settings

        /// <summary>
        /// The values of the settings file (not validated). A value is kept as it was set - a field being typed into
        /// keeps its trailing space - and cleaned of white space and quotes when written and when compared.
        /// </summary>
        public sealed class Settings
        {
            //The keys, in the order they are written. Read case-insensitively.
            private static readonly string[] Keys = { "ElmfireExe", "GdalBin", "WindNinjaExe", "Sumo", "ProjData" };

            private static readonly string[] Comments =
            {
                "ELMFIRE: elmfire.exe, or the folder holding it.",
                "GDAL's command-line tools: the folder holding gdal_translate.exe (a QGIS or OSGeo4W bin).",
                "WindNinja: WindNinja_cli.exe, or the folder holding it.",
                "SUMO: its install folder (SUMO_HOME) or its bin. Read when WUInity or PREACT starts.",
                "PROJ: the folder holding proj.db, for the engine's GDAL (instead of PROJ_DATA / PROJ_LIB).",
            };

            private readonly string[] _values = { string.Empty, string.Empty, string.Empty, string.Empty, string.Empty };

            //Keys this version does not know, kept so a newer version's settings survive a save by this one.
            private readonly List<KeyValuePair<string, string>> _other = new List<KeyValuePair<string, string>>();

            public string Get(Tool tool)
            {
                return _values[(int)tool];
            }

            public void Set(Tool tool, string value)
            {
                _values[(int)tool] = value ?? string.Empty;
            }

            /// <summary>A copy with every value cleaned (<see cref="ToolPaths.Clean"/>), as it is written.</summary>
            public Settings Cleaned()
            {
                Settings copy = Clone();
                for (int i = 0; i < copy._values.Length; ++i)
                {
                    copy._values[i] = Clean(copy._values[i]);
                }
                return copy;
            }

            public Settings Clone()
            {
                var copy = new Settings();
                Array.Copy(_values, copy._values, _values.Length);
                copy._other.AddRange(_other);
                return copy;
            }

            /// <summary>The same tool values once cleaned (unknown keys aside).</summary>
            public bool SameAs(Settings other)
            {
                if (other == null) return false;
                for (int i = 0; i < _values.Length; ++i)
                {
                    if (!string.Equals(Clean(_values[i]), Clean(other._values[i]), StringComparison.Ordinal)) return false;
                }
                return true;
            }

            /// <summary>Reads <c>key = value</c> lines; <c>;</c> and <c>#</c> start a comment line, sections are ignored.</summary>
            internal static Settings Parse(IEnumerable<string> lines)
            {
                var s = new Settings();
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[')
                    {
                        continue;
                    }

                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    string key = line.Substring(0, equals).Trim();
                    string value = Clean(line.Substring(equals + 1));
                    int index = Array.FindIndex(Keys, k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                    if (index >= 0)
                    {
                        s._values[index] = value;
                    }
                    else
                    {
                        s._other.Add(new KeyValuePair<string, string>(key, value));
                    }
                }
                return s;
            }

            internal string Write()
            {
                var b = new StringBuilder();
                b.AppendLine("; PREACT external tools, one file per user. Read by WUInity, PREACT and PREACTcli; written by");
                b.AppendLine("; WUInity's Help > External tools and keys, and fine to edit by hand.");
                b.AppendLine("; A path here is used instead of the automatic search. A scenario's [ELMFIRE] ElmfireExe, PathToGdal");
                b.AppendLine("; or WindNinjaExe, and PREACTcli's --elmfire, --gdal or --windninja, still come first.");
                b.AppendLine("; Leave a value empty to have the tool found automatically.");
                b.AppendLine("[Tools]");
                for (int i = 0; i < Keys.Length; ++i)
                {
                    b.AppendLine("; " + Comments[i]);
                    b.AppendLine(Keys[i] + " = " + Clean(_values[i]));
                }
                foreach (KeyValuePair<string, string> kv in _other)
                {
                    b.AppendLine(kv.Key + " = " + kv.Value);
                }
                return b.ToString();
            }
        }
    }
}
