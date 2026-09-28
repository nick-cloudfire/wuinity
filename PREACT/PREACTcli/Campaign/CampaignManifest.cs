using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using PREACT.Utility;

namespace PREACTcli.Campaigns
{
    /// <summary>
    /// <c>campaign.json</c>: the settings a campaign folder's realizations were computed with, their hash (which is
    /// in the folder's name), and information that does not affect them.
    /// </summary>
    /// <remarks>
    /// This is what makes resuming safe. <c>--resume</c> used to reuse any fire and boundary it found, after
    /// re-drawing the realization's weather, so a rerun with another seed and stop time aggregated the old fires
    /// under the new weather's name. A folder now only ever holds one set of settings, and resuming with
    /// different ones is refused with the differences listed.
    /// </remarks>
    internal sealed class CampaignManifest
    {
        public string Hash;
        public string Created;
        public Dictionary<string, string> Settings = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Information = new Dictionary<string, string>(StringComparer.Ordinal);
        public string Folder;

        public static void Write(Campaign c)
        {
            string path = Path.Combine(c.Folder, CampaignLayout.ManifestFile);
            using (var stream = File.Create(path))
            using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                json.WriteStartObject();
                json.WriteString("format", "wuinity-campaign-manifest-1");
                json.WriteString("hash", c.SettingsHash);
                json.WriteString("created", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                json.WriteStartObject("settings");
                foreach (KeyValuePair<string, string> kv in c.Settings) json.WriteString(kv.Key, kv.Value);
                json.WriteEndObject();
                json.WriteStartObject("information");
                foreach (KeyValuePair<string, string> kv in c.Information) json.WriteString(kv.Key, kv.Value);
                json.WriteEndObject();
                json.WriteEndObject();
            }
        }

        public static CampaignManifest Read(string folder)
        {
            string path = Path.Combine(folder, CampaignLayout.ManifestFile);
            if (!File.Exists(path)) return null;

            try
            {
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
                {
                    JsonElement root = doc.RootElement;
                    var m = new CampaignManifest
                    {
                        Folder = folder,
                        Hash = root.TryGetProperty("hash", out JsonElement h) ? h.GetString() : null,
                        Created = root.TryGetProperty("created", out JsonElement cr) ? cr.GetString() : null,
                    };
                    if (root.TryGetProperty("settings", out JsonElement s))
                    {
                        foreach (JsonProperty p in s.EnumerateObject()) m.Settings[p.Name] = p.Value.GetString();
                    }
                    if (root.TryGetProperty("information", out JsonElement i))
                    {
                        foreach (JsonProperty p in i.EnumerateObject()) m.Information[p.Name] = p.Value.GetString();
                    }
                    return m;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>The other campaigns of this scenario on disk, newest first.</summary>
        public static List<CampaignManifest> Others(Campaign c)
        {
            string output = Path.Combine(c.ScenarioDir, CampaignLayout.OutputFolder);
            var result = new List<CampaignManifest>();
            if (!Directory.Exists(output)) return result;

            string prefix = CampaignLayout.CampaignFolderName(c.ScenarioName, "");
            foreach (string folder in Directory.GetDirectories(output, prefix + "*"))
            {
                if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(c.Folder), StringComparison.OrdinalIgnoreCase)) continue;
                CampaignManifest m = Read(folder);
                if (m != null) result.Add(m);
            }

            return result.OrderByDescending(m => m.Created, StringComparer.Ordinal).ToList();
        }

        /// <summary>The settings that differ, as "key: old -> new".</summary>
        public List<string> DifferencesFrom(Campaign c)
        {
            var diffs = new List<string>();
            foreach (string key in Settings.Keys.Union(c.Settings.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                Settings.TryGetValue(key, out string was);
                c.Settings.TryGetValue(key, out string now);
                if (!string.Equals(was, now, StringComparison.Ordinal))
                {
                    diffs.Add($"{key}: {Short(was)} -> {Short(now)}");
                }
            }
            return diffs;
        }

        private static string Short(string v)
        {
            if (v == null) return "(absent)";
            return v.Length > 24 && v.IndexOf(' ') < 0 ? v.Substring(0, 12) + "..." : v;
        }

        /// <summary>What the realizations under a campaign folder amount to, from their records.</summary>
        public static (int Started, int Ok, int NotThreatened, int Failed) Count(string folder)
        {
            string dir = Path.Combine(folder, CampaignLayout.RealizationsFolder);
            int started = 0, ok = 0, not = 0, failed = 0;
            if (!Directory.Exists(dir)) return (0, 0, 0, 0);

            foreach (string r in Directory.GetDirectories(dir))
            {
                RealizationRecord rec = RealizationRecord.Load(r);
                if (rec == null) continue;
                ++started;
                if (rec.IsFinished && rec.Status == CampaignLayout.StatusOk) ++ok;
                else if (rec.IsFinished && rec.Status == CampaignLayout.StatusNotThreatened) ++not;
                else if (rec.Status == CampaignLayout.StatusFailed) ++failed;
            }
            return (started, ok, not, failed);
        }
    }

    /// <summary>
    /// One campaign process per campaign folder: two running at once would run the same realizations into the same
    /// directories.
    /// </summary>
    /// <remarks>
    /// The lock is <c>campaign.lock</c> held open for writing with no sharing for as long as the campaign runs, so the
    /// operating system releases it when the process ends in any way, a crash or a kill included, and a lock left
    /// behind is simply taken over. It used to be a pid and a start time compared as text, which on Linux never
    /// matched (the start time is recomputed from the boot time on each read), so every lock looked stale and two
    /// processes ran the same campaign side by side.
    /// </remarks>
    internal sealed class CampaignLock : IDisposable
    {
        private readonly string _path;
        private FileStream _stream;

        private CampaignLock(string path, FileStream stream)
        {
            _path = path;
            _stream = stream;
        }

        /// <summary>Why <paramref name="folder"/> cannot be taken over now, or null when no campaign process holds it.</summary>
        public static string HeldBy(string folder)
        {
            string path = Path.Combine(folder, CampaignLayout.LockFile);
            if (!File.Exists(path)) return null;
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    return null;
                }
            }
            catch (IOException)
            {
                return Describe(folder);
            }
            catch (UnauthorizedAccessException e)
            {
                return $"the lock {path} cannot be opened ({e.Message}).";
            }
        }

        public static CampaignLock Acquire(string folder, out string problem)
        {
            problem = null;
            string path = Path.Combine(folder, CampaignLayout.LockFile);
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                problem = Describe(folder);
                return null;
            }
            catch (UnauthorizedAccessException e)
            {
                problem = $"the lock {path} cannot be opened ({e.Message}).";
                return null;
            }

            //Who holds it, for a person looking at the folder; nothing reads this back.
            stream.SetLength(0);
            byte[] text = System.Text.Encoding.UTF8.GetBytes(
                "pid " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + " on " + Environment.MachineName
                + " since " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + Environment.NewLine);
            stream.Write(text, 0, text.Length);
            stream.Flush();
            return new CampaignLock(path, stream);
        }

        private static string Describe(string folder)
        {
            return $"another campaign process is running in {folder} (it holds {CampaignLayout.LockFile}). "
                   + "Stop it first, or wait for it to finish.";
        }

        public void Dispose()
        {
            if (_stream == null) return;
            _stream.Dispose();
            _stream = null;
            try { File.Delete(_path); } catch { }
        }
    }
}
