//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;

namespace PREACT.Input
{
    public class PopulationInput
    {
        private PopulationData _data;

        public PopulationData Data { get => _data; }
        public string PopulationFile = string.Empty;
        public Dictionary<string, Evacuation.DemographicsInput> Demographics = new Dictionary<string, Evacuation.DemographicsInput>(5);
        public bool CullOutsideGroups = false;

        public PopulationInput()
        {
            _data = new PopulationData();
        }

        public void Parse(string[] inputLines, int startIndex, List<int> demographicsLinesIndices, PedestrianModuleInput pedestrianInput, string rootFolder, out bool success)
        {
            //Read in full whether or not the pedestrian module is on, so a scenario saved with it switched off
            //keeps its population and demographics (this used to return straight away, and the writer then
            //dropped both). Only the CSV load, and anything critical, depends on the module being enabled.
            bool needed = pedestrianInput.Enabled;
            success = true;
            using (PREACTInput.SoftRequirements(!needed))
            {
                Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
                string nameOfInput, userInput;

                Evacuation.DemographicsInput.Parse(Demographics, inputLines, demographicsLinesIndices, out bool demographicsOk);

                nameOfInput = nameof(PopulationFile);
                bool haveFile = false;
                if (inputToParse.TryGetValue(nameOfInput, out userInput) && !string.IsNullOrWhiteSpace(userInput))
                {
                    PopulationFile = userInput;
                    PREACTInput.CheckIfFileExist(nameOfInput, ref PopulationFile, rootFolder, out haveFile);
                }
                else
                {
                    //Critical when the pedestrian module is on: there is nobody to evacuate without it.
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                }

                nameOfInput = nameof(CullOutsideGroups);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Bool(userInput, out CullOutsideGroups))
                    {
                        CullOutsideGroups = false;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "false");
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, "false");
                }

                if (needed && haveFile)
                {
                    Data.LoadAll(pedestrianInput, this, rootFolder, out bool loaded);
                    if (!loaded)
                    {
                        PREACTInput.InputProblem(nameof(PopulationFile), "could not be read; see the log for the line at fault.");
                    }
                    success = loaded;
                }

                success &= demographicsOk && (haveFile || !needed);
            }
        }
    }
}

