//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PREACT.Population;
using PREACT.Input;
using PREACT.Math;

namespace PREACT.Input
{
    public class PopulationData
    {    
        public struct HouseholdData
        {
            public Vector2d originLatLon;
            public Vector2d roadAccessLatLon;
            public int peopleCount;

            public HouseholdData(Vector2d originLatLon, Vector2d roadAccessLatLon, int peopleCount)
            {
                this.originLatLon = originLatLon;
                this.roadAccessLatLon = roadAccessLatLon;
                this.peopleCount = peopleCount;
            }
        }

        private HouseholdData[] _householdData = System.Array.Empty<HouseholdData>();
        private int _totalPopulation;

        public HouseholdData[] Households { get => _householdData; }        
        public int TotalPopulation { get => _totalPopulation; }
                
        public PopulationData()
        {
            _totalPopulation = 0;
        }

        public void LoadAll(PedestrianModuleInput pedestrianInput, PopulationInput populationInput, string rootFolder, out bool success)
        {
            success = false;
            
            if(pedestrianInput.Enabled)
            {
                string filePath = PREACTInput.ResolvePath(rootFolder, populationInput.PopulationFile);
                HouseholdData[] households = LoadPopulation(filePath, out _totalPopulation, out success);
                _householdData = households ?? System.Array.Empty<HouseholdData>();
                if(!success)
                {
                    return;
                }
            }

            success = true;
        }
        
        /// <summary>
        /// Reads a population CSV: a header, then <c>lat,lon,carLat,carLon,people</c> per household.
        /// </summary>
        /// <remarks>
        /// Blank lines anywhere (a trailing newline, an editor's empty last line) are skipped; they used to reach
        /// <c>double.Parse("")</c> and throw, which took the rest of the scenario with it. A malformed line is
        /// reported with its line number and skipped, and the load fails only if nothing usable is left or more
        /// than a handful of lines are bad - a file that is mostly garbage is the wrong file.
        /// </remarks>
        public static HouseholdData[] LoadPopulation(string filePath, out int totalPopulation, out bool success)
        {
            success = false;
            totalPopulation = 0;
            HouseholdData[] householdData = null;

            if (!File.Exists(filePath))
            {
                Engine.Message(null, Engine.LogType.InputError, "Population file " + filePath + " could not be found.");
                return householdData;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(filePath);
            }
            catch (System.Exception e)
            {
                Engine.Message(null, Engine.LogType.InputError, "Population file " + filePath + " could not be read: " + e.Message);
                return householdData;
            }

            var households = new List<HouseholdData>(lines.Length);
            int badLines = 0;
            //first row is the header
            for (int i = 1; i < lines.Length; ++i)
            {
                string text = lines[i];
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                string[] line = text.Split(',');
                //Invariant, matching how the file is written. Parsing with the current culture reads
                //"38.05" as 3805 wherever the decimal separator is a comma.
                if (line.Length >= 5
                    && InputParse.Double(line[0], out double lat)
                    && InputParse.Double(line[1], out double lon)
                    && InputParse.Double(line[2], out double carLat)
                    && InputParse.Double(line[3], out double carLon)
                    && InputParse.Int(line[4], out int people)
                    && people >= 0)
                {
                    households.Add(new HouseholdData(new Vector2d(lat, lon), new Vector2d(carLat, carLon), people));
                    totalPopulation += people;
                }
                else
                {
                    ++badLines;
                    if (badLines <= 5)
                    {
                        Engine.Message(null, Engine.LogType.Warning, $"Population file {Path.GetFileName(filePath)} line {i + 1} is not lat,lon,carLat,carLon,people and was skipped: {text}");
                    }
                }
            }

            householdData = households.ToArray();
            if (badLines > 0)
            {
                Engine.Message(null, Engine.LogType.Warning, $"Population file {Path.GetFileName(filePath)}: {badLines} malformed line(s) skipped.");
            }

            if (totalPopulation > 0 && badLines <= System.Math.Max(5, households.Count / 100))
            {
                success = true;
                Engine.Message(null, Engine.LogType.Log, "Loaded population " + Path.GetFileNameWithoutExtension(filePath) + " containing " + totalPopulation + " people and " + householdData.Length + " households.");
            }
            else if (totalPopulation <= 0)
            {
                Engine.Message(null, Engine.LogType.InputError, "Population file found but did not contain any population.");
            }
            else
            {
                Engine.Message(null, Engine.LogType.InputError, $"Population file {Path.GetFileName(filePath)} has {badLines} malformed lines out of {lines.Length - 1}; it does not look like a population file.");
            }

            return householdData;
        }
    }
}