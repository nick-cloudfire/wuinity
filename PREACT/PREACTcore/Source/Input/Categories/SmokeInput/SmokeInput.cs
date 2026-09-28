//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using PREACT.Input;

namespace PREACT.Input
{
    [System.Serializable]
    public class SmokeInput
    {
        public enum SmokeModules { None, GlobalSmoke }

        private SmokeData _data;
        private GlobalSmokeInput _globalSmokeInput;

        public bool Enabled = false;
        public SmokeData Data { get =>  _data; }
        public SmokeModules Module = SmokeModules.None;
        public GlobalSmokeInput GlobalSmokeInput { get => _globalSmokeInput; }

        public SmokeInput()
        {
            _data = new SmokeData();
            _globalSmokeInput = new GlobalSmokeInput();
        }

        public void Parse(string[] inputLines, int startIndex, Dictionary<string, int> headerLineIndex, WeatherInput weatherInput, string rootFolder, out bool success)
        {
            success = true;
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            nameOfInput = nameof(Enabled);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (!InputParse.Bool(userInput, out Enabled))
                {
                    Enabled = false;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "false");
                }
            }
            else
            {
                PREACTInput.ModuleOffForWantOfEnabled("smoke", "the run has no smoke");
            }

            //Read in full whether or not smoke is on, so a save keeps the settings; nothing is critical when off.
            using (PREACTInput.SoftRequirements(!Enabled))
            {
                nameOfInput = nameof(Module);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Enum(userInput, out Module))
                    {
                        Module = SmokeModules.None;
                        success &= !Enabled;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                    }
                }
                else if (Enabled)
                {
                    success = false;
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                }

                if (Enabled && Module == SmokeModules.None)
                {
                    success = false;
                    PREACTInput.InputProblem(nameOfInput, "smoke is enabled but no smoke module is chosen; choose GlobalSmoke or switch smoke off.");
                }

                if (headerLineIndex.TryGetValue(nameof(SmokeModules.GlobalSmoke), out int lineIndex))
                {
                    using (PREACTInput.SoftRequirements(Module != SmokeModules.GlobalSmoke))
                    {
                        PREACTInput.ReadingInputMessage(nameof(SmokeModules.GlobalSmoke));
                        _globalSmokeInput = GlobalSmokeInput.Parse(inputLines, lineIndex, rootFolder, this, out bool ok);
                        success &= ok || !Enabled || Module != SmokeModules.GlobalSmoke;
                    }
                }
                else if (Enabled && Module == SmokeModules.GlobalSmoke)
                {
                    success = false;
                    PREACTInput.InputNotFoundMessage("[" + nameof(SmokeModules.GlobalSmoke) + "]", true);
                }

                if (Enabled && success)
                {
                    _data.LoadAll(this, rootFolder, out bool loaded);
                    if (!loaded)
                    {
                        success = false;
                        PREACTInput.InputProblem(nameof(GlobalSmokeInput.ExtinctionFile), "could not be read as a time,extinction coefficient ramp with at least two rows.");
                    }
                }
            }
        }
    }
}
