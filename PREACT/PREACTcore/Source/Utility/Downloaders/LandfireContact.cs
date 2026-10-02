using System;
using System.IO;
using PREACT.Utility;

namespace PREACT.Tools
{
    /// <summary>
    /// The contact e-mail LANDFIRE's product service asks every request for.
    /// </summary>
    /// <remarks>
    /// Personal, so never in the scenario (a <c>.wui</c> is meant to be shared): kept per user, in the one per-user
    /// settings file every program of the platform reads (<see cref="ToolPaths.SettingsFile"/>, <c>tools.ini</c>, beside
    /// the tool paths), and read by the GUI and the command line alike. It used to be a hard-coded address belonging to
    /// one of the original developers, which every user's jobs were then filed under.
    ///
    /// Looked for in this order: the caller's own value (a command-line option), the <c>LANDFIRE_EMAIL</c> environment
    /// variable, then the settings file. The first v1.1 builds kept it in a file of its own,
    /// <c>%APPDATA%\WUInity\user-settings.txt</c> (<see cref="LegacySettingsPath"/>); an e-mail found there is moved into
    /// the settings file the first time one is looked for, and removed from the old file.
    /// </remarks>
    public static class LandfireContact
    {
        public const string Variable = "LANDFIRE_EMAIL";

        private const string LegacyKey = "LandfireEmail";

        /// <summary>For the tests, which must not touch the real user's old settings.</summary>
        internal static string LegacySettingsPathOverride;

        /// <summary>The per-user settings file the e-mail is kept in: <see cref="ToolPaths.SettingsFile"/>.</summary>
        public static string SettingsPath => ToolPaths.SettingsFile;

        /// <summary>Where the first v1.1 builds kept it, a <c>Key=Value</c> file: %APPDATA%\WUInity\user-settings.txt on Windows.</summary>
        public static string LegacySettingsPath
        {
            get
            {
                if (!string.IsNullOrEmpty(LegacySettingsPathOverride)) return LegacySettingsPathOverride;
                string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(root))
                {
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                }
                return Path.Combine(root, "WUInity", "user-settings.txt");
            }
        }

        /// <summary>The e-mail to send, and where it came from; null when none is set anywhere.</summary>
        public static string Resolve(string explicitValue, out string source)
        {
            if (IsPlausible(explicitValue))
            {
                source = "given for this run";
                return explicitValue.Trim();
            }

            string fromEnvironment = Environment.GetEnvironmentVariable(Variable);
            if (IsPlausible(fromEnvironment))
            {
                source = "the " + Variable + " environment variable";
                return fromEnvironment.Trim();
            }

            string fromFile = Saved();
            if (IsPlausible(fromFile))
            {
                source = SettingsPath;
                return fromFile.Trim();
            }

            source = null;
            return null;
        }

        /// <summary>The e-mail kept in the per-user settings file (moved there from the old file first, once), or empty.</summary>
        public static string Saved()
        {
            string saved = ToolPaths.LandfireEmail;
            if (string.IsNullOrWhiteSpace(saved)) saved = MigrateLegacy();
            return saved ?? string.Empty;
        }

        /// <summary>Keeps <paramref name="email"/> in the per-user settings file (empty removes it). False when it could not be written.</summary>
        public static bool Save(string email, out string problem)
        {
            problem = null;
            try
            {
                ToolPaths.SaveLandfireEmail((email ?? string.Empty).Trim());
                return true;
            }
            catch (Exception e)
            {
                problem = e.Message;
                return false;
            }
        }

        private static readonly object _migrationLock = new object();
        private static string _legacyLookedAt;

        /// <summary>
        /// The e-mail the old <c>user-settings.txt</c> holds, moved into the settings file and removed from the old one
        /// (the file itself goes when nothing else is left in it). Looked at once per process and file: an e-mail cleared
        /// later is not brought back. When the settings file cannot be written the old e-mail is still returned, for this
        /// session, and the old file is left as it is.
        /// </summary>
        internal static string MigrateLegacy()
        {
            string legacy = LegacySettingsPath;
            lock (_migrationLock)
            {
                if (string.Equals(_legacyLookedAt, legacy, StringComparison.Ordinal)) return null;
                _legacyLookedAt = legacy;
            }

            string old = ReadSetting(legacy, LegacyKey);
            if (!IsPlausible(old)) return null;
            old = old.Trim();

            try
            {
                ToolPaths.SaveLandfireEmail(old);
            }
            catch (Exception)
            {
                return old;
            }

            try
            {
                WriteSetting(legacy, LegacyKey, string.Empty);
                bool empty = true;
                foreach (string line in File.ReadAllLines(legacy))
                {
                    string t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith("#")) empty = false;
                }
                if (empty) File.Delete(legacy);
            }
            catch (Exception)
            {
                //Moved, and only the old copy is left behind; the settings file is read first from now on.
            }

            Engine.Message(null, Engine.LogType.Log, $"The LANDFIRE e-mail was moved from {legacy} into {ToolPaths.SettingsFile}.");
            return old;
        }

        /// <summary>For the tests: look at the old file again.</summary>
        internal static void ForgetMigration()
        {
            lock (_migrationLock) _legacyLookedAt = null;
        }

        /// <summary>Something that can be an address: text, one @, a dot after it, no spaces.</summary>
        public static bool IsPlausible(string email)
        {
            if (string.IsNullOrWhiteSpace(email)) return false;
            string e = email.Trim();
            int at = e.IndexOf('@');
            return at > 0 && at == e.LastIndexOf('@') && e.IndexOf('.', at) > at + 1 && !e.EndsWith(".")
                   && e.IndexOf(' ') < 0;
        }

        /// <summary>What LFPS needs to be told when none is set, naming the three places one can be.</summary>
        public static string MissingMessage =>
            "LANDFIRE asks every download for a contact e-mail, and none is set. Enter yours under Fuels, canopy and "
            + "buildings > LANDFIRE (it is kept for your user, in " + SettingsPath + ", never in the scenario), or set "
            + Variable + ".";

        internal static string ReadSetting(string path, string key)
        {
            try
            {
                if (!File.Exists(path)) return null;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        return line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        /// <summary>Sets one key in a Key=Value file, keeping every other line as it is.</summary>
        internal static void WriteSetting(string path, string key, string value)
        {
            var lines = new System.Collections.Generic.List<string>();
            bool found = false;
            if (File.Exists(path))
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq > 0 && !line.StartsWith("#") && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!found && value.Length > 0) lines.Add(key + "=" + value);
                        found = true;
                        continue;
                    }
                    lines.Add(raw);
                }
            }
            else
            {
                lines.Add("# WUInity settings for this user. Not part of any scenario.");
            }
            if (!found && value.Length > 0) lines.Add(key + "=" + value);

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, lines);
        }
    }
}
