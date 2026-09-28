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
        /// Midflame wind speed in <b>ft/min</b> on the fire grid, as ELMFIRE writes it to <c>mfws_*.tif</c>
        /// (<c>DUMP_MIDFLAME_WINDSPEED</c>): valid where the fire spread, nodata elsewhere. Optional.
        /// </summary>
        /// <remarks>
        /// This is the wind k-PERIL's length-to-breadth ratio is defined for, and when it is set the trigger
        /// boundary uses it instead of <c>[kPERIL] WindSpeedFile</c>. A campaign realization gets it from its own
        /// ELMFIRE run. A fire imported from elsewhere usually has none, and then k-PERIL falls back to the 10 m
        /// wind and says so loudly, because 10 m wind read as midflame over-elongates every spread ellipse.
        /// </remarks>
        public string MidflameWindSpeedFile = string.Empty;

        /// <summary>
        /// Fuel model raster on the same grid, for display only. Optional, and read by nothing that computes.
        /// </summary>
        /// <remarks>
        /// Read and kept on save, but nothing uses it now: it fed the output window's "Fuel model" display mode,
        /// which is gone (an imported fire brings its own behaviour and needs no fuel to spread).
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

        /// <summary>A copy, so a run can fill in the rasters it produced without touching the scenario's own.</summary>
        public AscImportInput Clone()
        {
            return (AscImportInput)MemberwiseClone();
        }

        public static AscImportInput Parse(string[] inputLines, int startIndex, string rootFolder, out bool success)
        {
            AscImportInput newInput = new AscImportInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            //Every key is read even after a problem, and the section fails at the end: returning at the first
            //problem dropped every key after it, and saving the scenario then wrote the section without them.
            bool ok = true;

            //critical
            nameOfInput = nameof(StartDateTime);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (!DateTime.TryParse(userInput, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out newInput.StartDateTime))
                {
                    ok = false;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }
            else
            {
                ok = false;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
            }

            //critical: the fire itself; rate of spread and spread direction are what k-PERIL runs on
            ok &= ReadRequiredFile(inputToParse, nameof(TimeOfArrivalFile), ref newInput.TimeOfArrivalFile, rootFolder);
            ok &= ReadRequiredFile(inputToParse, nameof(RateOfSpreadFile), ref newInput.RateOfSpreadFile, rootFolder);
            ok &= ReadRequiredFile(inputToParse, nameof(SpreadDirectionFile), ref newInput.SpreadDirectionFile, rootFolder);

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

            //not critical: without it fireline intensity reads as 0
            nameOfInput = nameof(FirelineIntensityFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
            {
                newInput.FirelineIntensityFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.FirelineIntensityFile, rootFolder, out _, critical: false);
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput);
            }

            //Not critical: without it k-PERIL falls back to [kPERIL] WindSpeedFile and warns. Named but missing is
            //said here, since the fallback would otherwise hide a wrong path.
            nameOfInput = nameof(MidflameWindSpeedFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
            {
                newInput.MidflameWindSpeedFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.MidflameWindSpeedFile, rootFolder,
                    out bool midflameExists, critical: false);
                if (!midflameExists)
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        nameOfInput + " was specified but not found; k-PERIL will fall back to the 10 m wind: " + userInput);
                    newInput.MidflameWindSpeedFile = string.Empty;
                }
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

            success = ok;
            return newInput;
        }

        private static bool ReadRequiredFile(Dictionary<string, string> inputToParse, string nameOfInput, ref string field,
                                             string rootFolder)
        {
            if (!inputToParse.TryGetValue(nameOfInput, out string userInput) || userInput.Length == 0)
            {
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
                return false;
            }

            field = userInput;
            PREACTInput.CheckIfFileExist(nameOfInput, ref field, rootFolder, out bool exists);
            return exists;
        }
    }
}
