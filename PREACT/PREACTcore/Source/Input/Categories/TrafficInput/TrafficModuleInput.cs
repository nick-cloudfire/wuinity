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
    public class TrafficModuleInput
    {
        public enum TrafficModules { None, SUMO }

        private SUMOInput _sumoInput;

        public bool Enabled = false;
        public SUMOInput SumoInput { get { return _sumoInput; } }
        public TrafficModules Module = TrafficModules.SUMO;

        public TrafficModuleInput()
        {
            _sumoInput = new SUMOInput();
        }

        public void Parse(string[] inputLines, int startIndex, Dictionary<string, int> headerLineIndex, string rootFolder, out bool success)
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
                PREACTInput.ModuleOffForWantOfEnabled("traffic", "nobody drives out: no car is simulated, and a trigger boundary gets no evacuation time");
            }

            //Read whether or not traffic is on, so a save keeps the SUMO settings (this used to return when it
            //was off, and the writer then dropped the [SUMO] section); nothing is critical when it is off.
            using (PREACTInput.SoftRequirements(!Enabled))
            {
                nameOfInput = nameof(Module);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Enum(userInput, out Module) || Module == TrafficModules.None)
                    {
                        Module = TrafficModules.SUMO;
                        success &= !Enabled;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, nameof(TrafficModules.SUMO));
                }

                //VisibilityAffectsSpeed used to be read here and never used; smoke acts through
                //[SUMO] SmokeAlpha/SmokeBeta. The key is ignored and not written.

                nameOfInput = nameof(TrafficModules.SUMO);
                if (headerLineIndex.TryGetValue(nameOfInput, out int lineIndex))
                {
                    PREACTInput.ReadingInputMessage(nameOfInput);
                    _sumoInput = SUMOInput.Parse(inputLines, lineIndex, rootFolder, out bool ok);
                    success &= ok || !Enabled;
                }
                else
                {
                    PREACTInput.InputProblem("[" + nameOfInput + "]", "the traffic module needs a [SUMO] section naming its ConfigurationFile.");
                    success &= !Enabled;
                }
            }
        }
    }
}
    