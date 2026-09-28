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
    public class PedestrianModuleInput
    {
        private MacroHouseholdSimInput _macroHouseholdSimInput;

        public bool Enabled = false;
        public enum PedestrianModules { None, MacroHouseholdSim }
        public PedestrianModules Module = PedestrianModules.MacroHouseholdSim;

        //module inputs
        public MacroHouseholdSimInput MacroHouseholdSimInput { get => _macroHouseholdSimInput; }

        public PedestrianModuleInput()
        {
            _macroHouseholdSimInput = new MacroHouseholdSimInput();
        }

        public void Parse(string[] inputLines, int startIndex, Dictionary<string, int> headerLineIndex, out bool success)
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
                PREACTInput.InputNotFoundMessage(nameOfInput, false, "false");
            }

            //The rest is read whether or not the module is on, so a save keeps it; nothing is critical when off.
            using (PREACTInput.SoftRequirements(!Enabled))
            {
                nameOfInput = nameof(Module);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Enum(userInput, out Module) || Module == PedestrianModules.None)
                    {
                        Module = PedestrianModules.MacroHouseholdSim;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, nameof(PedestrianModules.MacroHouseholdSim));
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, nameof(PedestrianModules.MacroHouseholdSim));
                }

                if (headerLineIndex.TryGetValue(nameof(PedestrianModules.MacroHouseholdSim), out int lineIndex))
                {
                    PREACTInput.ReadingInputMessage(nameof(PedestrianModules.MacroHouseholdSim));
                    _macroHouseholdSimInput = MacroHouseholdSimInput.Parse(inputLines, lineIndex, out bool ok);
                    success &= ok || !Enabled;
                }
                else if (Enabled)
                {
                    Engine.Message(null, Engine.LogType.Warning, nameof(PedestrianModules.MacroHouseholdSim) + " input was not found, using defaults.");
                }
            }
        }
    }
}
   