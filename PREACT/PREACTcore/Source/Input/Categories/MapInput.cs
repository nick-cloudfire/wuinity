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
    public class MapInput
    {
        public enum MapServiceProvider { Mapbox, Bing, OSM };

        public MapServiceProvider MapProvider = MapServiceProvider.Mapbox;
        public int ZoomLevel = 13;

        public MapInput() 
        { 
        }

        public void Parse(string[] inputLines, int startIndex, out bool success)
        {
            //Nothing here can stop a run: the map is a backdrop. An unknown provider or zoom level keeps the
            //default and says so (the unknown provider used to be logged as a SimulationError, which stopped
            //whatever simulation happened to be running).
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string input, userInput;
            
            input = nameof(MapProvider);
            if (inputToParse.TryGetValue(input, out userInput))
            {
                if (InputParse.Enum(userInput, out MapServiceProvider provider))
                {
                    MapProvider = provider;
                }
                else
                {
                    PREACTInput.CouldNotInterpretInputMessage(input, userInput, false, MapProvider.ToString());
                }
            }

            input = nameof(ZoomLevel);
            if (inputToParse.TryGetValue(input, out userInput))
            {
                if (!InputParse.Int(userInput, out int zoom) || zoom < 0 || zoom > 20)
                {
                    PREACTInput.CouldNotInterpretInputMessage(input, userInput, false, "13");
                    ZoomLevel = 13;
                }
                else
                {
                    ZoomLevel = zoom;
                }
            }

            success = true;
        }
    }
}