//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;

namespace PREACT.Input
{
    [System.Serializable]
    public class kPERILInput
    {
        /// <summary>
        /// Where the area k-PERIL protects comes from.
        ///
        /// Raster reads <see cref="WuiAreaFile"/>. The two group options rasterise the evacuation
        /// groups' own polygons onto the fire grid instead, which keeps the area being protected and
        /// the area being evacuated as one definition rather than two that can drift apart.
        ///
        /// Combined unions every group into a single boundary: one answer for the whole community.
        /// Separate computes a boundary per group, which is what you want when the groups evacuate
        /// on different orders or to different destinations, because each then has its own required
        /// egress time and so its own trigger.
        /// </summary>
        public enum WuiAreaSources { Raster, EvacuationGroupsCombined, EvacuationGroupsSeparate }

        //No WindBand. The wind rasters hold one band per hour, and k-PERIL's solver has no time axis - the
        //wind enters it once, as the length-to-breadth ratio of the Huygens ellipse at each cell. Rather than
        //choose one hour for the whole domain, each cell now takes the band covering the hour the fire
        //actually reached it, which is a property of the fire rather than a preference. See
        //EvacuationManager.ComposeWindAtArrivalTime.

        /// <summary>What the trigger boundary's output files are named after. Only a label.</summary>
        public string OutputName = DefaultOutputName;

        public const string DefaultOutputName = "trigger_boundary";
        public string WuiAreaFile = string.Empty; //.asc mask, 1 = protected WUI cell
        public WuiAreaSources WuiAreaSource = WuiAreaSources.Raster;

        /// <summary>
        /// Wind speed raster in MILES PER HOUR, used by k-PERIL <b>only when the fire brings no midflame wind of
        /// its own</b>.
        ///
        /// k-PERIL spends wind in one place - the Anderson (1983) length-to-breadth ratio of the Huygens
        /// ellipse - and that correlation is defined for <b>midflame</b> wind in mi/h. An ELMFIRE fire supplies
        /// exactly that (its <c>mfws_*.tif</c>, see <c>[AscImport] MidflameWindSpeedFile</c>), and then this key is
        /// not used. For a fire imported without one, this raster is taken <i>as</i> midflame wind and the run
        /// warns: the 10 m wind WindNinja writes to ws.tif is several times the midflame wind (realization 13 of
        /// the Mati campaign: 14.4 mi/h at 10 m, 2.75 mi/h midflame), and L/B grows exponentially with it.
        /// </summary>
        public string WindSpeedFile = string.Empty;

        /// <summary>
        /// Seconds each band of <see cref="WindDirectionFile"/> (and <see cref="WindSpeedFile"/>) covers, so each
        /// cell can take the band covering the hour the fire reached it. The fire's DT_METEOROLOGY; 3600 unless
        /// said. An ELMFIRE fire run by the simulation supplies its own and this is not read.
        /// </summary>
        public double WindBandSeconds = DefaultWindBandSeconds;

        public const double DefaultWindBandSeconds = 3600.0;

        /// <summary>
        /// Wind direction raster, in DEGREES. As written by WindNinjaRunner to wd.tif.
        ///
        /// k-PERIL only ever uses this to take the angle between the wind and the upslope
        /// direction, and it applies the same trigonometric convention to both, so the resulting
        /// effective wind magnitude is the same whether these are compass bearings or mathematical
        /// angles. The convention therefore does not need pinning down; the unit does.
        /// </summary>
        public string WindDirectionFile = string.Empty;

        public kPERILInput()
        {
        }

        public static kPERILInput Parse(string[] inputLines, int startIndex, string rootFolder, out bool success)
        {
            kPERILInput newInput = new kPERILInput();
            success = false;
            int issues = 0;            
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            //A wind field is required, but not necessarily from here: the ELMFIRE module hands over the case's
            //own ws/wd when this is unset, which is the arrangement that cannot disagree with the fire. So an
            //absent key is not a defect - it is left to be filled in, and the run says so if nothing does.
            //These replace the single MidflameWindspeed scalar this section used to carry: k-PERIL accepts a
            //full wind field and the weather pipeline produces one with WindNinja, so representing the whole
            //domain by one number threw away exactly the terrain-driven variation WindNinja exists to resolve.
            nameOfInput = nameof(WindSpeedFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
            {
                newInput.WindSpeedFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.WindSpeedFile, rootFolder, out bool windSpeedExists);
                if (!windSpeedExists)
                {
                    success = false;
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                    return newInput;
                }
            }
            else
            {
                PREACTInput.OptionalInputMissing(nameOfInput,
                    "No wind field named for the trigger boundary. Running ELMFIRE supplies the case's own; "
                    + "otherwise set it here, or the run stops when it needs one.");
            }

            nameOfInput = nameof(WindDirectionFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
            {
                newInput.WindDirectionFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.WindDirectionFile, rootFolder, out bool windDirectionExists);
                if (!windDirectionExists)
                {
                    success = false;
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                    return newInput;
                }
            }

            //A WindBand key from an older scenario is simply ignored - the band is now per cell, from the
            //fire's arrival times.
            nameOfInput = nameof(WindBandSeconds);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
            {
                if (double.TryParse(userInput, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double seconds) && seconds > 0.0)
                {
                    newInput.WindBandSeconds = seconds;
                }
                else
                {
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }

            //Optional. It names the output files and nothing reads it back, so its absence cannot make a
            //run wrong - yet a missing key used to abort the whole section, which presented as "this
            //section could not be read completely" with no indication that the thing missing was a label.
            //A scenario that gains a trigger boundary from the GUI has never had one, so this was every
            //such scenario.
            nameOfInput = nameof(OutputName);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.OutputName = userInput;
            }
            else
            {
                newInput.OutputName = DefaultOutputName;
            }

            //optional: defaults to reading the raster below, which is how existing scenarios behave.
            nameOfInput = nameof(WuiAreaSource);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (Enum.TryParse(userInput, true, out WuiAreaSources parsedSource))
                {
                    newInput.WuiAreaSource = parsedSource;
                }
                else
                {
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }

            //Only needed when the area comes from a raster; the group options derive it instead, so
            //demanding a file there would reject a perfectly good scenario.
            if (newInput.WuiAreaSource != WuiAreaSources.Raster)
            {
                success = true;
                return newInput;
            }

            //optional: a .asc mask marking the WUI area to protect (1 = WUI). Without it,
            //k-PERIL has no community to back-propagate from and the trigger boundary is empty.
            nameOfInput = nameof(WuiAreaFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.WuiAreaFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.WuiAreaFile, rootFolder, out bool wuiExists);
                if (!wuiExists)
                {
                    Engine.Message(null, Engine.LogType.Warning, nameOfInput + " was specified but not found: " + userInput);
                }
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, nameOfInput + " was not specified; k-PERIL needs a WUI area to compute a trigger boundary.");
            }

            success = true;
            return newInput;
        }

    }
}
