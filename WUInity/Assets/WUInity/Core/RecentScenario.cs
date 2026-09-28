//This file is part of WUIPlatform Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.IO;
using UnityEngine;

namespace WUInity
{
    /// <summary>
    /// The scenario file opened last, remembered across runs so a session resumes where the previous
    /// one left off instead of on a fixed example.
    ///
    /// In PlayerPrefs rather than in a file beside the application: it is a per-user editor
    /// preference, not part of any scenario, and it must not travel when a case folder is copied to
    /// another machine.
    /// </summary>
    public static class RecentScenario
    {
        private const string Key = "WUInity.LastScenarioFile";
        private const string ListKey = "WUInity.RecentScenarioFiles";
        private const int MaxRecent = 5;

        /// <summary>The remembered scenario, or an empty string when there is none that still exists.</summary>
        public static string Path
        {
            get
            {
                string path = PlayerPrefs.GetString(Key, string.Empty);
                //Checked here rather than by every caller: a case folder gets renamed, moved or
                //deleted between sessions, and a remembered path is a convenience - not a promise.
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? path : string.Empty;
            }
        }

        public static bool Have { get => !string.IsNullOrEmpty(Path); }

        public static void Remember(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            PlayerPrefs.SetString(Key, path);

            //Most recent first, each once, compared as full paths so two spellings of one file do not both
            //take a slot.
            string full = Full(path);
            var list = new System.Collections.Generic.List<string> { path };
            foreach (string known in ReadList())
            {
                if (list.Count >= MaxRecent) break;
                if (!string.Equals(Full(known), full, System.StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(known);
                }
            }
            PlayerPrefs.SetString(ListKey, string.Join("\n", list));

            //Written now rather than at quit: a crash or a stop from the editor's play button never
            //reaches OnApplicationQuit, and losing the one thing this exists to remember to that is
            //worse than the write costs.
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Stops the last scenario being reopened at start-up (File &gt; Close), without taking it off the
        /// recent list.
        /// </summary>
        public static void Forget()
        {
            PlayerPrefs.DeleteKey(Key);
            PlayerPrefs.Save();
        }

        /// <summary>The recently opened scenarios that still exist, most recent first.</summary>
        public static System.Collections.Generic.List<string> Recent
        {
            get
            {
                var existing = new System.Collections.Generic.List<string>();
                foreach (string path in ReadList())
                {
                    if (File.Exists(path)) existing.Add(path);
                }
                return existing;
            }
        }

        /// <summary>Empties the recent list (and forgets the last scenario).</summary>
        public static void ClearRecent()
        {
            PlayerPrefs.DeleteKey(ListKey);
            Forget();
        }

        private static string[] ReadList()
        {
            string raw = PlayerPrefs.GetString(ListKey, string.Empty);
            return string.IsNullOrEmpty(raw)
                ? new string[0]
                : raw.Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries);
        }

        private static string Full(string path)
        {
            try { return System.IO.Path.GetFullPath(path); } catch { return path; }
        }
    }
}
