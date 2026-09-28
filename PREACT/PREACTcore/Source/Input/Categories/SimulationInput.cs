//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System;
using PREACT.Math;

namespace PREACT.Input
{
    public class SimulationInput
    {
        private SimulationData _data;
        private Vector2d _lowerLeftLatLon;

        public SimulationData Data { get => _data; }
        public string Name = string.Empty;
        public Vector2d LowerLeftLatLon { get => _lowerLeftLatLon; set { _lowerLeftLatLon = value; _data.UpdateData(LowerLeftLatLon); } }
        public Vector2d DomainSize;
        public float DeltaTime = 1.0f;
        public DateTime StartDateTime = DateTime.Now;
        public DateTime EndDateTime = DateTime.Now;
        public bool StopWhenEvacuated = false;

        /// <summary>
        /// Seeds everything stochastic in the run, so it can be repeated. 0 means draw from the clock.
        /// </summary>
        /// <remarks>
        /// The seed is combined with the simulation index, so run <i>n</i> of a batch is reproducible on its own
        /// rather than every run of a batch being identical — which would make a multi-run convergence check
        /// meaningless. Departure times, walking speeds, household sizes and destination choice all come from
        /// here.
        ///
        /// Defaults to 0, keeping the behaviour a scenario had before this key existed.
        /// </remarks>
        public int RandomSeed = 0;

        public SimulationInput()
        {
            _data = new SimulationData(_lowerLeftLatLon);
        }

        public void Parse(string[] inputLines, int startIndex, out bool success)
        {
            //Every key is read whatever happened to the ones before it. This used to return at the first
            //problem, so one bad value (say LowerLeftLatLon without its comma) left the rest of the section
            //at its defaults - and a save then wrote those defaults back over the user's values.
            int issues = 0;
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            //critical: every output file is named after it
            nameOfInput = nameof(Name);
            if(inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
            {
                Name = userInput;
            }
            else if (userInput != null)
            {
                ++issues;
                PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
            }
            else
            {
                ++issues;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
            }

            //critical
            nameOfInput = nameof(LowerLeftLatLon);
            if(inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (InputParse.Vector2d(userInput, out Vector2d latLon))
                {
                    _lowerLeftLatLon = latLon;
                }
                else
                {
                    ++issues;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }
            else
            {
                ++issues;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
            }

            //critical
            nameOfInput = nameof(DomainSize);
            if(inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (InputParse.Vector2d(userInput, out Vector2d size) && size.x > 0.0 && size.y > 0.0)
                {
                    DomainSize = size;
                }
                else
                {
                    ++issues;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }
            else
            {
                ++issues;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
            }

            //not critical, but a step of zero or less never advances the clock
            nameOfInput = nameof(DeltaTime);
            if(inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (!InputParse.Float(userInput, out float deltaTime) || deltaTime <= 0f)
                {
                    ++issues;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
                else
                {
                    DeltaTime = deltaTime;
                }
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput, false, InputParse.Format(DeltaTime));
            }

            nameOfInput = nameof(StartDateTime);
            if(inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if(!InputParse.DateTime(userInput, out StartDateTime))
                {
                    ++issues;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }
            else
            {
                ++issues;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
            }

            nameOfInput = nameof(EndDateTime);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (!InputParse.DateTime(userInput, out EndDateTime))
                {
                    ++issues;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
                else if (EndDateTime <= StartDateTime)
                {
                    PREACTInput.InputWarning(nameOfInput, $"{userInput} is not after the start ({StartDateTime:yyyy-MM-ddTHH:mm:ss}); the run ends immediately.");
                }
            }
            else
            {
                ++issues;
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
            }

            nameOfInput = nameof(StopWhenEvacuated);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                if (!InputParse.Bool(userInput, out StopWhenEvacuated))
                {
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "false");
                }
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput, false, "false");
            }

            //Optional and silent when absent: 0 is the documented default and means the same thing every
            //scenario written before this key existed already did.
            nameOfInput = nameof(RandomSeed);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && !InputParse.Int(userInput, out RandomSeed))
            {
                RandomSeed = 0;
                PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "0 (clock-seeded)");
            }

            _data.UpdateData(LowerLeftLatLon);
            success = issues == 0;
        }
    }
}    