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
            //Written now rather than at quit: a crash or a stop from the editor's play button never
            //reaches OnApplicationQuit, and losing the one thing this exists to remember to that is
            //worse than the write costs.
            PlayerPrefs.Save();
        }

        public static void Forget()
        {
            PlayerPrefs.DeleteKey(Key);
            PlayerPrefs.Save();
        }
    }
}
