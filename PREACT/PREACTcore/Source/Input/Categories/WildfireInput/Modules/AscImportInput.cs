//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System;

namespace PREACT.Input
{

    [System.Serializable]
    public class AscImportInput
    {
        /// <summary>The unit the time-of-arrival raster is in. There is no way to tell from the file.</summary>
        public enum TimeUnits { Minutes, Seconds }

        public DateTime StartDateTime;
        public string TimeOfArrivalFile = string.Empty;
        public string RateOfSpreadFile = string.Empty;
        public string SpreadDirectionFile = string.Empty;
        public string FirelineIntensityFile = string.Empty;

        /// <summary>
        /// Fuel model raster on the same grid, for display only. Optional, and read by nothing that computes.
        /// </summary>
        /// <remarks>
        /// The output window offers a "Fuel model" display mode which did nothing at all —
        /// <c>GetFuelModelNumberData()</c> returned null, so the renderer drew the previous frame's buffer. An
        /// imported fire brings its own behaviour and needs no fuel to spread, so nothing else wanted this
        /// raster and the display mode had no source. For an ELMFIRE fire it is set automatically from the
        /// case's own fuel layer, which is the raster the fire was actually computed against.
        /// </remarks>
        public string FuelModelFile = string.Empty;

        /// <summary>
        /// What the arrival times in <see cref="TimeOfArrivalFile"/> are measured in.
        /// </summary>
        /// <remarks>
        /// It cannot be inferred from the raster, and getting it wrong is silent: the fire simply arrives
        /// 60 times too early or too late, which looks like a fire that barely moves or one that has already
        /// swept the domain before the evacuation begins. The reader used to assume minutes unconditionally,
        /// which is right for FARSITE, FlamMap and Prometheus - the <c>.asc</c> products this module was
        /// written for - and wrong for ELMFIRE, whose <c>time_of_arrival</c> raster holds the simulation
        /// clock in seconds. Verified: a 600 s ELMFIRE run writes values up to 370.8.
        ///
        /// <b>Seconds is the default</b>, because ELMFIRE is what produces the fires this platform runs and
        /// seconds is the unit everything downstream works in - the simulation clock included. A scenario
        /// importing a FARSITE, FlamMap or Prometheus raster must say <c>Minutes</c>; the four examples that
        /// ship do, explicitly, rather than relying on a default that could move under them again.
        /// </remarks>
        public TimeUnits TimeOfArrivalUnits = TimeUnits.Seconds;

        public AscImportInput()
        {

        }

        public static AscImportInput Parse(string[] inputLines, int startIndex, string rootFolder, out bool success)
        {
            success = false;
            int issues = 0;            
            AscImportInput newInput = new AscImportInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            //critical
            nameOfInput = nameof(StartDateTime);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                 success = DateTime.TryParse(userInput, out newInput.StartDateTime);
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput);
            }
            if (!success)
            {
                return newInput;
            }

            //critical
            nameOfInput = nameof(TimeOfArrivalFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.TimeOfArrivalFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.TimeOfArrivalFile, rootFolder, out success);                
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput);
            }
            if (!success)
            {
                return newInput;
            }

            //critical only for k-PERIL
            nameOfInput = nameof(RateOfSpreadFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.RateOfSpreadFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.RateOfSpreadFile, rootFolder, out success);                
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput);
            }
            if (!success)
            {
                return newInput;
            }

            //critical only for k-PERIL
            nameOfInput = nameof(SpreadDirectionFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.SpreadDirectionFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.SpreadDirectionFile, rootFolder, out success);                
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput);
            }
            if (!success)
            {
                return newInput;
            }

            //Not critical: the default is seconds, which is what ELMFIRE writes and what the rest of the
            //platform works in. A scenario importing a .asc from FARSITE, FlamMap or Prometheus has to say
            //Minutes, and the reader logs whichever it used so the choice is never invisible.
            nameOfInput = nameof(TimeOfArrivalUnits);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
            {
                if (Enum.TryParse(userInput, ignoreCase: true, out TimeUnits parsedUnits))
                {
                    newInput.TimeOfArrivalUnits = parsedUnits;
                }
                else
                {
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }

            //not critical
            nameOfInput = nameof(FirelineIntensityFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                newInput.FirelineIntensityFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.FirelineIntensityFile, rootFolder,out success);
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput);
            }

            //Not critical, and silent when absent: it is a display layer, so its absence costs one output
            //display mode and nothing else. An ELMFIRE fire gets it set automatically.
            nameOfInput = nameof(FuelModelFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
            {
                newInput.FuelModelFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.FuelModelFile, rootFolder, out bool fuelExists);
                if (!fuelExists)
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        nameOfInput + " was specified but not found, so the fuel model display mode will be "
                        + "empty: " + userInput);
                }
            }

            success = true;
            return newInput;
        }
    }
}
