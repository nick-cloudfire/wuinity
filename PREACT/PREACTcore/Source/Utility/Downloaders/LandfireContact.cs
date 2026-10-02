using System;
using System.IO;

namespace PREACT.Tools
{
    /// <summary>
    /// The contact e-mail LANDFIRE's product service asks every request for.
    /// </summary>
    /// <remarks>
    /// Personal, so never in the scenario (a <c>.wui</c> is meant to be shared): kept per user, beside the user's other
    /// WUInity settings, and read by the GUI and the command line alike. It used to be a hard-coded address belonging
    /// to one of the original developers, which every user's jobs were then filed under.
    ///
    /// Looked for in this order: the caller's own value (a command-line option), the <c>LANDFIRE_EMAIL</c> environment
    /// variable, then the per-user file <see cref="SettingsPath"/>.
    /// </remarks>
    public static class LandfireContact
    {
        public const string Variable = "LANDFIRE_EMAIL";

        private const string Key = "LandfireEmail";

        /// <summary>For the tests, which must not touch the real user's settings.</summary>
        internal static string SettingsPathOverride;

        /// <summary>The per-user file, a <c>Key=Value</c> text file: %APPDATA%\WUInity\user-settings.txt on Windows.</summary>
        public static string SettingsPath
        {
            get
            {
                if (!string.IsNullOrEmpty(SettingsPathOverride)) return SettingsPathOverride;
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

            string fromFile = ReadSetting(SettingsPath, Key);
            if (IsPlausible(fromFile))
            {
                source = SettingsPath;
                return fromFile.Trim();
            }

            source = null;
            return null;
        }

        /// <summary>The e-mail kept in the per-user file, or empty.</summary>
        public static string Saved()
        {
            return ReadSetting(SettingsPath, Key) ?? string.Empty;
        }

        /// <summary>Keeps <paramref name="email"/> in the per-user file (empty removes it). False when it could not be written.</summary>
        public static bool Save(string email, out string problem)
        {
            problem = null;
            try
            {
                WriteSetting(SettingsPath, Key, (email ?? string.Empty).Trim());
                return true;
            }
            catch (Exception e)
            {
                problem = e.Message;
                return false;
            }
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
