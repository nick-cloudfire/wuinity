//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Numerics;

namespace PREACT.Input
{
    public class MacroHouseholdSimInput
    {
        public Vector2 WalkingSpeedMinMax = new Vector2(0.7f, 1.0f);
        public float WalkingSpeedModifier = 1.0f;
        public float WalkingDistanceModifier = 1.0f;

        /// <summary>
        /// Whether a household that has not left yet starts to leave when the fire front comes within
        /// <see cref="FireReactionDistance"/> of its home, instead of waiting for its drawn response time.
        /// On by default.
        /// </summary>
        public bool ReactToFire = true;

        /// <summary>Distance from home to the current fire front, in metres, at which a household leaves.</summary>
        public float FireReactionDistance = 500.0f;

        /// <summary>
        /// How often, in simulated seconds, the distance to the front is recomputed. The front moves slowly
        /// next to this; the computation is O(fire cells).
        /// </summary>
        public float FireReactionUpdateInterval = 300.0f;
        
        public MacroHouseholdSimInput()
        {

        }

        public static MacroHouseholdSimInput Parse(string[] inputLines, int startIndex, out bool success)
        {
            MacroHouseholdSimInput newInput = new MacroHouseholdSimInput();
            success = true;
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string input, userInput;

            //All optional with defaults; a value that cannot be read keeps its default and says so.
            input = nameof(WalkingDistanceModifier);
            if (inputToParse.TryGetValue(input, out userInput) && (!InputParse.Float(userInput, out newInput.WalkingDistanceModifier) || newInput.WalkingDistanceModifier <= 0f))
            {
                newInput.WalkingDistanceModifier = 1.0f;
                PREACTInput.CouldNotInterpretInputMessage(input, userInput, false, "1");
            }

            input = nameof(WalkingSpeedMinMax);
            if (inputToParse.TryGetValue(input, out userInput))
            {
                if (!InputParse.Vector2(userInput, out newInput.WalkingSpeedMinMax) || newInput.WalkingSpeedMinMax.X <= 0f || newInput.WalkingSpeedMinMax.Y < newInput.WalkingSpeedMinMax.X)
                {
                    newInput.WalkingSpeedMinMax = new Vector2(0.7f, 1.0f);
                    PREACTInput.CouldNotInterpretInputMessage(input, userInput, false, "0.7,1");
                }
            }

            input = nameof(WalkingSpeedModifier);
            if (inputToParse.TryGetValue(input, out userInput) && (!InputParse.Float(userInput, out newInput.WalkingSpeedModifier) || newInput.WalkingSpeedModifier <= 0f))
            {
                newInput.WalkingSpeedModifier = 1.0f;
                PREACTInput.CouldNotInterpretInputMessage(input, userInput, false, "1");
            }

            input = nameof(ReactToFire);
            if (inputToParse.TryGetValue(input, out userInput) && !InputParse.Bool(userInput, out newInput.ReactToFire))
            {
                newInput.ReactToFire = true;
                PREACTInput.CouldNotInterpretInputMessage(input, userInput, false, "true");
            }

            input = nameof(FireReactionDistance);
            if (inputToParse.TryGetValue(input, out userInput) && (!InputParse.Float(userInput, out newInput.FireReactionDistance) || newInput.FireReactionDistance < 0f))
            {
                newInput.FireReactionDistance = 500.0f;
                PREACTInput.CouldNotInterpretInputMessage(input, userInput, false, "500");
            }

            input = nameof(FireReactionUpdateInterval);
            if (inputToParse.TryGetValue(input, out userInput) && (!InputParse.Float(userInput, out newInput.FireReactionUpdateInterval) || newInput.FireReactionUpdateInterval <= 0f))
            {
                newInput.FireReactionUpdateInterval = 300.0f;
                PREACTInput.CouldNotInterpretInputMessage(input, userInput, false, "300");
            }

            return newInput;
        }
    }
}

    