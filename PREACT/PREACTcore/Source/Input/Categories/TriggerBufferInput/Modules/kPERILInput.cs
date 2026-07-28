//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;

namespace PREACT.Input
{
    [System.Serializable]
    public class kPERILInput
    {
        public bool CalculateROSFromBehave = true;
        public string InitialFuelMoistureFile = string.Empty;
        public string OutputName = string.Empty;
        public string WuiAreaFile = string.Empty; //.asc mask, 1 = protected WUI cell

        /// <summary>
        /// Mid-flame wind speed raster, in MILES PER HOUR.
        ///
        /// The unit is not a free choice. k-PERIL spends this value in one place - the Anderson
        /// (1983) length-to-breadth ratio of the Huygens ellipse - and that correlation is defined
        /// for mid-flame wind in mi/h. It is also what WindNinjaRunner already writes to ws.tif, so
        /// the pipeline output drops straight in. Feeding m/s instead silently produces
        /// grossly over-elongated spread ellipses: 5 mi/h gives L/B of about 3.2, while the same
        /// figure read as m/s (11.2 mi/h) gives about 16.
        /// </summary>
        public string WindSpeedFile = string.Empty;

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

            //critical, both of them. These replace the single MidflameWindspeed scalar this section
            //used to carry: k-PERIL accepts a full wind field and the weather pipeline already
            //produces one with WindNinja, so representing the whole domain by one number threw away
            //exactly the terrain-driven variation WindNinja exists to resolve.
            nameOfInput = nameof(WindSpeedFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.WindSpeedFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, userInput, rootFolder, out bool windSpeedExists);
                if (!windSpeedExists)
                {
                    success = false;
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                    return newInput;
                }
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
                return newInput;
            }

            nameOfInput = nameof(WindDirectionFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.WindDirectionFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, userInput, rootFolder, out bool windDirectionExists);
                if (!windDirectionExists)
                {
                    success = false;
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                    return newInput;
                }
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
                return newInput;
            }

            //not critical
            nameOfInput = nameof(CalculateROSFromBehave);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                issues += bool.TryParse(userInput, out newInput.CalculateROSFromBehave) ? 0 : 1;
                if (issues > 0)
                {
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, nameOfInput + " was not found, defaulting to " + newInput.CalculateROSFromBehave.ToString() + ".");
            }


            //critical only when the rate of spread is computed with Behave. When ROS is supplied
            //externally (CalculateROSFromBehave=false, which is how the ELMFIRE-driven
            //probabilistic trigger pipeline runs) the moisture raster is never read, so a missing
            //entry must not fail the load. The guard below always said as much, but the
            //missing-key branch set success=false unconditionally and the next critical check
            //returned on it, so an irrelevant key still aborted the whole .wui.
            nameOfInput = nameof(InitialFuelMoistureFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.InitialFuelMoistureFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, userInput, rootFolder, out bool moistureFileExists);
                if (!moistureFileExists && newInput.CalculateROSFromBehave)
                {
                    success = false;
                    return newInput;
                }
            }
            else if (newInput.CalculateROSFromBehave)
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
                return newInput;
            }

            //critical. Returns directly rather than falling through to a shared "if (!success)"
            //check: success is still false at this point in every path (it is only set true at the
            //end), because the checks above signal failure by returning, not by leaving the flag
            //set. Testing the flag here would reject a perfectly valid input.
            nameOfInput = nameof(OutputName);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.OutputName = userInput;
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput);
                return newInput;
            }

            //optional: a .asc mask marking the WUI area to protect (1 = WUI). Without it,
            //k-PERIL has no community to back-propagate from and the trigger boundary is empty.
            nameOfInput = nameof(WuiAreaFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.WuiAreaFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, userInput, rootFolder, out bool wuiExists);
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