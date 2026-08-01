//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// Finds the OpenTopography API key, which is needed to download a DEM.
    ///
    /// In core rather than in either front end, because both need it and the key must not travel with the
    /// scenario: a <c>.wui</c> is meant to be shared, and a key in one is a key published. It lives in a
    /// gitignored Unity resource beside the Mapbox token, or in the environment.
    /// </summary>
    public static class OpenTopographyKey
    {
        /// <summary>The gitignored resource, relative to the repository root, as the Unity side reads it.</summary>
        private static readonly string[] ResourceRelativePaths =
        {
            "WUInity/Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt",
            "Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt",
        };

        /// <summary>
        /// The key, or null. Environment first, then the resource file - the same order the scenario data
        /// steps use, so the command line and the editor cannot disagree about which key is in force.
        /// </summary>
        public static string Resolve()
        {
            string fromEnvironment = Environment.GetEnvironmentVariable("OPENTOPOGRAPHY_API_KEY");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment.Trim();
            }

            return ReadResourceFile();
        }

        /// <summary>
        /// Walks up from the running assembly and from the working directory looking for the resource, since
        /// the same file sits at different relative depths for the editor, a player build and the CLI.
        /// </summary>
        private static string ReadResourceFile()
        {
            foreach (string start in new[] { AssemblyDirectory(), Directory.GetCurrentDirectory() })
            {
                string dir = start;
                for (int up = 0; up < 8 && !string.IsNullOrEmpty(dir); ++up, dir = Path.GetDirectoryName(dir))
                {
                    foreach (string relative in ResourceRelativePaths)
                    {
                        string candidate = Path.Combine(dir, Path.Combine(relative.Split('/')));
                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        string key = ExtractKey(candidate);
                        if (!string.IsNullOrEmpty(key))
                        {
                            return key;
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Pulls the value out of <c>{"ApiKey":"..."}</c>. Split on quotes rather than parsed as JSON: core
        /// has no JSON dependency, and the file has exactly one field.
        /// </summary>
        private static string ExtractKey(string path)
        {
            try
            {
                foreach (string part in File.ReadAllText(path).Split('"'))
                {
                    string token = part.Trim();
                    if (token.Length > 0 && token != "ApiKey"
                        && !token.StartsWith("{") && !token.StartsWith(":") && !token.StartsWith("}"))
                    {
                        return token;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string AssemblyDirectory()
        {
            try
            {
                return Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            }
            catch
            {
                return null;
            }
        }
    }
}
