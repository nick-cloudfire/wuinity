//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Numerics;
using System.Collections.Generic;

namespace PREACT.Input
{
    public class GlobalSmokeInput
    {
        public string ExtinctionFile = string.Empty;

        public GlobalSmokeInput()
        {
        }

        public static GlobalSmokeInput Parse(string[] inputLines, int startIndex, string rootFolder, SmokeInput smokeInput, out bool success)
        {
            GlobalSmokeInput newInput = new GlobalSmokeInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);

            //critical when the module is in use (the caller makes it soft otherwise). It used to be reported as
            //a mere default, and the parse then failed without saying why.
            string nameOfInput = nameof(ExtinctionFile);
            if (inputToParse.TryGetValue(nameOfInput, out string userInput) && !string.IsNullOrWhiteSpace(userInput))
            {
                newInput.ExtinctionFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.ExtinctionFile, rootFolder, out success);
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
            }

            return newInput;
        }
    }
}

