//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// Finds a file a scenario refers to, after it has been moved into one of the scenario's own
    /// subfolders.
    ///
    /// A scenario folder accumulates a lot of files, and sorting them into subfolders is the obvious
    /// thing to do - but every path in the <c>.wui</c> is resolved by a bare
    /// <c>Path.Combine(rootFolder, name)</c>, so tidying up broke the scenario. What the user sees is a
    /// checklist claiming the landscape has no elevation, slope or aspect at all, naming files that are
    /// plainly there, one folder down.
    ///
    /// The search is one level deep into a fixed, ordered list of folders - not a recursive sweep. A
    /// scenario folder holds several copies of the same raster at different stages (a downloaded DEM, a
    /// warped one, a generated case's own), plus whatever is in <c>_output</c> and <c>cache</c>, so
    /// "first file with this name anywhere underneath" is as likely to find the wrong one as the right
    /// one. When more than one candidate does turn up, all of them are named, so the choice is visible
    /// rather than quietly made.
    /// </summary>
    public static class ScenarioFileLocator
    {
        /// <summary>
        /// Where to look, in the order a candidate is preferred.
        ///
        /// Ordered by how specific the folder is to a finished input. <c>elmfire/inputs</c> first because
        /// that is where the harmonized rasters a scenario actually runs on end up; <c>downloads</c> after
        /// it, since what is in there is generally the same layer at an earlier stage - in the wrong
        /// projection, or not yet clipped - and picking that one silently would be the worse mistake.
        /// Deliberately absent: <c>_output</c>, <c>scratch</c> and <c>cache</c>, which hold results and
        /// intermediates rather than inputs.
        /// </summary>
        public static readonly string[] SearchFolders =
        {
            "elmfire/inputs",
            "landscape",
            "downloads",
            "canopy",
            "sumo",
            "elmfire",
            //The root, last. It is where a bare name is looked for first anyway, so this entry only ever
            //matters in the other direction: a path naming a subfolder for a file that is still in the
            //root, which is every scenario written before the steps started using subfolders.
            "",
        };

        /// <summary>
        /// Resolves what the scenario recorded into a path that exists, relative to the scenario root
        /// (or absolute, if that is what was recorded and it exists).
        ///
        /// <paramref name="resolved"/> equals <paramref name="recorded"/> when the file was where the
        /// scenario said, which is how a caller tells "found" from "found somewhere else": only in the
        /// second case is there anything to report or to write back.
        /// </summary>
        public static bool TryResolve(string rootFolder, string recorded, out string resolved, out string explanation)
        {
            resolved = recorded;
            explanation = string.Empty;

            if (string.IsNullOrWhiteSpace(recorded))
            {
                return false;
            }

            //An absolute path is the user naming a file outside the scenario, which is allowed. Nothing to
            //search for: there is no subfolder of the scenario it could have moved to.
            if (Path.IsPathRooted(recorded))
            {
                return File.Exists(recorded);
            }

            if (string.IsNullOrEmpty(rootFolder))
            {
                return File.Exists(recorded);
            }

            if (File.Exists(Path.Combine(rootFolder, recorded)))
            {
                return true;
            }

            string fileName = Path.GetFileName(recorded);
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            var candidates = new List<string>();
            for (int i = 0; i < SearchFolders.Length; ++i)
            {
                //Forward slashes in the table, platform separators on disk. Combine takes either on
                //Windows but only one of them elsewhere, so the table is split rather than trusted.
                string folder = SearchFolders[i].Length == 0
                    ? rootFolder
                    : Path.Combine(rootFolder, Path.Combine(SearchFolders[i].Split('/')));

                if (!Directory.Exists(folder))
                {
                    continue;
                }

                if (File.Exists(Path.Combine(folder, fileName)))
                {
                    candidates.Add(SearchFolders[i]);
                }
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            //Recorded with forward slashes, because this string goes back into the .wui: a backslash
            //written on Windows is not a separator anywhere else.
            resolved = candidates[0].Length == 0 ? fileName : candidates[0] + "/" + fileName;

            explanation = fileName + " is not where the scenario says it is (" + recorded.Replace('\\', '/')
                          + "); using " + resolved + ".";

            if (candidates.Count > 1)
            {
                //Named rather than resolved by preference alone: two copies of a raster at different
                //stages of preparation look identical from here, and the one that is wanted is not
                //something this can know.
                var others = new List<string>();
                for (int i = 1; i < candidates.Count; ++i)
                {
                    others.Add(candidates[i].Length == 0 ? "the scenario root" : candidates[i]);
                }
                explanation += " It is also in " + string.Join(", ", others) + "; that copy is not the one being used.";
            }

            return true;
        }
    }
}
