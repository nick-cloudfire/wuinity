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
    public class TriggerBufferModuleInput
    {
        public enum TriggerBufferModules { None, kPERIL }

        private kPERILInput _kPERILInput;

        public bool Enabled = false;
        public TriggerBufferModules Module = TriggerBufferModules.None;
        public kPERILInput kPERILInput { get => _kPERILInput; }

        //No Data here, unlike the other modules: the only thing this one ever loaded was the fuel moisture
        //and fuel model table BEHAVE needed, and the rate of spread now always comes from the fire module.

        public TriggerBufferModuleInput()
        {
            _kPERILInput = new kPERILInput();
        }

        public void Parse(string[] inputLines, int startIndex, Dictionary<string, int> headerLineIndex, string rootFolder, out bool success)
        {
            Parse(inputLines, startIndex, headerLineIndex, null, rootFolder, out success);
        }

        public void Parse(string[] inputLines, int startIndex, Dictionary<string, int> headerLineIndex, SimulationInput simulationInput, string rootFolder, out bool success)
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
                PREACTInput.ModuleOffForWantOfEnabled("the trigger boundary", "no trigger boundary is computed");
            }

            //Read whether or not the boundary is on, so a save keeps the k-PERIL settings (they used to be
            //skipped when it was off, and the writer dropped them); nothing is critical when off.
            using (PREACTInput.SoftRequirements(!Enabled))
            {
                nameOfInput = nameof(Module);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Enum(userInput, out Module))
                    {
                        Module = TriggerBufferModules.None;
                        success &= !Enabled;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                    }
                }
                else if (Enabled)
                {
                    success = false;
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                }

                if (Enabled && Module == TriggerBufferModules.None)
                {
                    success = false;
                    PREACTInput.InputProblem(nameOfInput, "the trigger boundary is enabled but no module is chosen; choose kPERIL or switch it off.");
                }

                nameOfInput = nameof(TriggerBufferModules.kPERIL);
                if (headerLineIndex.TryGetValue(nameOfInput, out int lineindex))
                {
                    using (PREACTInput.SoftRequirements(Module != TriggerBufferModules.kPERIL))
                    {
                        PREACTInput.ReadingInputMessage(nameOfInput);
                        _kPERILInput = kPERILInput.Parse(inputLines, lineindex, rootFolder, out bool ok);
                        success &= ok || !Enabled || Module != TriggerBufferModules.kPERIL;
                    }
                }
                else if (Enabled && Module == TriggerBufferModules.kPERIL)
                {
                    success = false;
                    PREACTInput.InputNotFoundMessage("[" + nameOfInput + "]", true);
                }
            }
        }
    }     
}