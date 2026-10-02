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

namespace PREACT.Utility
{
    /// <summary>
    /// Finds the directory holding GDAL's command-line tools.
    /// </summary>
    /// <remarks>
    /// The tools, not the library. PREACT itself talks to GDAL through the C# bindings, and what ships under
    /// <c>Runtimes/Native/GDAL</c> is only the SWIG glue for those - <c>gdal_wrap.dll</c> and friends, no
    /// executables. ELMFIRE is different: it shells out to <c>gdal_translate</c>, <c>gdalinfo</c> and
    /// <c>gdalsrsinfo</c> as programs, so it needs a folder with those in it, and no such folder ships with
    /// WUInity.
    ///
    /// ELMFIRE does find them itself - <c>PATH_TO_GDAL='auto'</c> runs <c>where gdal_translate</c> - but only
    /// if they are on the PATH of the process running it. So the useful thing to do is locate them and put
    /// them on that PATH, leaving ELMFIRE's own detection to succeed and report what it found, rather than
    /// writing an explicit PATH_TO_GDAL that overrides it.
    /// </remarks>
    public static class GdalTools
    {
        /// <summary>The tool ELMFIRE's own auto-detection looks for, so this agrees with it.</summary>
        private const string ProbeTool = "gdal_translate";

        /// <summary>
        /// The automatic search's answer. Cached, because the search touches the file system and its answer only
        /// changes when something is installed - which is what <see cref="SearchAgain"/> is for.
        /// </summary>
        private static string _cached;
        private static bool _searched;
        private static readonly object _lock = new object();

        /// <summary>
        /// Where GDAL's tools are, or null: the folder named in the user's tool settings (<see cref="ToolPaths"/>)
        /// when it holds them, else the automatic search. A scenario's <c>[ELMFIRE] PathToGdal</c> and
        /// <c>--gdal</c> are applied by the callers, before this.
        /// </summary>
        /// <remarks>
        /// The setting is looked at on every call, not cached with the search, so saving it in the GUI takes effect
        /// for the next case build, run or campaign without a restart.
        /// </remarks>
        public static string FindBinDirectory()
        {
            string setting = ToolPaths.UserSetting(ToolPaths.Tool.Gdal);
            if (setting != null)
            {
                return setting;
            }

            lock (_lock)
            {
                if (!_searched)
                {
                    _cached = Search();
                    _searched = true;
                }
                return _cached;
            }
        }

        /// <summary>Forgets the automatic search's answer, so the next lookup searches again (after an install).</summary>
        public static void SearchAgain()
        {
            lock (_lock)
            {
                _searched = false;
                _cached = null;
            }
        }

        private static string Search()
        {
            bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            string exe = windows ? ProbeTool + ".exe" : ProbeTool;

            //PATH first, because that is what ELMFIRE would have found on its own - agreeing with it means
            //the tools used are the ones it would have picked.
            string fromPath = FindOnPath(exe);
            if (fromPath != null)
            {
                return fromPath;
            }

            foreach (string candidate in LikelyDirectories(windows))
            {
                if (!string.IsNullOrEmpty(candidate) && File.Exists(Path.Combine(candidate, exe)))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string FindOnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (string dir in path.Split(Path.PathSeparator))
            {
                string trimmed = dir.Trim().Trim('"');
                if (trimmed.Length == 0)
                {
                    continue;
                }

                try
                {
                    if (File.Exists(Path.Combine(trimmed, exe)))
                    {
                        return trimmed;
                    }
                }
                catch
                {
                    //An unusable PATH entry - a bad character, a dead drive - is not worth failing over.
                }
            }

            return null;
        }

        /// <summary>
        /// The installs that ship GDAL's tools on this platform. QGIS and OSGeo4W first: they are how GDAL
        /// arrives on a Windows GIS machine, and one of them is already needed for the PROJ database.
        /// </summary>
        private static IEnumerable<string> LikelyDirectories(bool windows)
        {
            if (!windows)
            {
                yield return "/usr/bin";
                yield return "/usr/local/bin";
                yield return "/opt/homebrew/bin";
                yield break;
            }

            //Newest first, so a machine with several QGIS versions uses the most recent.
            foreach (string root in new[] { @"C:\Program Files", @"C:\Program Files (x86)" })
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                string[] qgis;
                try
                {
                    qgis = Directory.GetDirectories(root, "QGIS*");
                }
                catch
                {
                    continue;
                }

                Array.Sort(qgis, StringComparer.OrdinalIgnoreCase);
                for (int i = qgis.Length - 1; i >= 0; --i)
                {
                    yield return Path.Combine(qgis[i], "bin");
                }
            }

            yield return @"C:\OSGeo4W\bin";
            yield return @"C:\OSGeo4W64\bin";

            //SUMO ships a GDAL build too, and a machine set up for the traffic module has it.
            string sumoHome = Environment.GetEnvironmentVariable("SUMO_HOME");
            if (!string.IsNullOrEmpty(sumoHome))
            {
                yield return Path.Combine(sumoHome, "bin");
            }
        }
    }
}
