//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.IO;
using PREACT.Math;

namespace PREACT.Input
{  
    [System.Serializable]
    public class SUMOInput
    {
        public enum SmokeSpeedReductionModels { Exponential, Smokanzo};

        public string ConfigurationFile = string.Empty;
        public double OutputRasterSize = 25.0;        
        public float SmokeAlpha = 0f;
        public float SmokeBeta = 0f;

        public SUMOInput()
        {

        }

        public static SUMOInput Parse(string[] inputLines, int startIndex, string rootFolder, out bool success)
        {
            success = true;
            SUMOInput newInput = new SUMOInput();
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            //critical: SUMO cannot start without it. Every other key is still read when it is missing (this
            //used to return, so a missing .sumocfg also reset the raster size and smoke factors on save).
            nameOfInput = nameof(ConfigurationFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && !string.IsNullOrWhiteSpace(userInput))
            {
                newInput.ConfigurationFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref newInput.ConfigurationFile, rootFolder, out bool found);
                success &= found;
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
            }

            nameOfInput = nameof(OutputRasterSize);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (!InputParse.Double(userInput, out newInput.OutputRasterSize) || newInput.OutputRasterSize <= 0.0)
                {
                    newInput.OutputRasterSize = 25.0;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "25");
                }
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput, false, "25");
            }

            nameOfInput = nameof(SmokeAlpha);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (!InputParse.Float(userInput, out newInput.SmokeAlpha) || newInput.SmokeAlpha < 0f || newInput.SmokeAlpha > 1f)
                {
                    newInput.SmokeAlpha = 0f;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput + " (must be between 0 and 1)", false, "0 (no smoke effect)");
                }
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput, false, "0 (no smoke effect)");
            }

            nameOfInput = nameof(SmokeBeta);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (!InputParse.Float(userInput, out newInput.SmokeBeta))
                {
                    newInput.SmokeBeta = 0f;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "0");
                }
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput, false, "0");
            }

            return newInput;
        }
    }
}
