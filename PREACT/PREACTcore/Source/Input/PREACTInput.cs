//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.IO;
using PREACT.Evacuation;

namespace PREACT.Input
{
    [System.Serializable]
    public class PREACTInput
    {
        public string RootFolder;
        public SimulationInput Simulation;
        public MapInput Map;
        //Terrain, separately from the fire module that consumes most of it: a scenario has ground
        //before it has fuels, and the elevation alone is enough to georeference a cell grid, correct
        //spread for slope, and give something to paint on.
        public LandscapeInput Landscape;
        public WeatherInput Weather;
        public PopulationInput Population;
        public EventsInput Events;
        public EvacuationInput Evacuation;
        public PedestrianModuleInput PedestrianModule;
        public TrafficModuleInput TrafficModule;    
        public WildfireModuleInput WildfireModule;        
        public SmokeInput SmokeModule;
        public TriggerBufferModuleInput TriggerBufferModule;

        public PREACTInput(string rootFolder)
        {
            RootFolder = rootFolder;

            Simulation = new SimulationInput();
            Map = new MapInput();
            Landscape = new LandscapeInput();
            Weather = new WeatherInput();
            Population = new PopulationInput();
            Events = new EventsInput();
            Evacuation = new EvacuationInput();
            PedestrianModule = new PedestrianModuleInput();
            TrafficModule = new TrafficModuleInput();                
            WildfireModule = new WildfireModuleInput();            
            SmokeModule = new SmokeInput();
            TriggerBufferModule = new TriggerBufferModuleInput();
        }

        public static void SaveToDisk(PREACTInput input, string saveFilePath)
        {
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(saveFilePath));
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                File.WriteAllLines(saveFilePath, PREACTInputWriter.Write(input));
                Engine.Message(null, Engine.LogType.Log, " Saved input file " + saveFilePath + ".");
            }
            catch (System.Exception e)
            {
                Engine.Message(null, Engine.LogType.SimulationError, " Could not save " + saveFilePath + ": " + e.Message);
            }
        }

        public static PREACTInput LoadFromDisk(string filePath, out bool success)
        {
            success = false;
            string rootFolder = Path.GetDirectoryName(filePath);
            PREACTInput input = null;
            if(!File.Exists(filePath))
            {
                Engine.Message(null, Engine.LogType.InputError, " Input file " + filePath + " does not exist.");
            }
            else
            {
                Engine.Message(null, Engine.LogType.Log, " Reading input file " + filePath + ".");
                input = ParseInput(rootFolder, File.ReadAllLines(filePath), out success);
                if (success)
                {      
                    Engine.Message(null, Engine.LogType.Log, " Input file " + filePath + " loaded.");
                }
                else
                {
                    //Loaded, just not finished. Saying "could not be loaded" was misleading once the
                    //parse started returning what it managed to read.
                    int outstanding = 0;
                    for (int i = 0; i < _requirements.Count; ++i)
                    {
                        if (_requirements[i].Critical) ++outstanding;
                    }
                    Engine.Message(null, Engine.LogType.Log, $" Input file {filePath} loaded with {outstanding} item(s) still required; see the scenario checklist.");
                }
            }

            return input;
        }

        public static readonly char[] inputSplit = { '=', '#' };
        static readonly char[] headerBrackets = new char[] { '[', ']' };
        public const string pleaseCheckInput = " Please check your input file.";  
        
        private static string RemoveSpace(string input)
        {
            List<char> chars = new List<char>();
            for(int i = 0; i < input.Length; ++i)
            {
                if (input[i] != ' ')
                {
                    chars.Add(input[i]);
                }
            }

            return new string(chars.ToArray());
        }

        private static PREACTInput ParseInput(string rootFolder, string[] inputLines, out bool success)
        {
            success = false;
            PREACTInput newInput = new PREACTInput(rootFolder);
            Dictionary<string, int> headerLineIndices = new Dictionary<string, int>();

            //The checklist describes the file being read now, not whatever was read before it.
            _requirements.Clear();
            _currentSection = string.Empty;

            List<int> destinationLineIndices = new List<int>();            
            List<int> responseLineIndices = new List<int>();
            List<int> groupLineIndices = new List<int>();
            List<int> demographicsLineIndices = new List<int>();

            //first index all headers
            for (int i = 0; i < inputLines.Length; ++i)
            {
                if (string.IsNullOrWhiteSpace(inputLines[i]))
                {
                    continue;
                }

                //inputLines[i] = inputLines[i].Trim();
                inputLines[i] = RemoveSpace(inputLines[i]);
                string line = inputLines[i];
                if (line.StartsWith("["))
                {      
                    line = line.Trim(headerBrackets);
                    if (line.Equals("Destination"))
                    {
                        destinationLineIndices.Add(i);
                    }                    
                    else if (line.Equals("ResponseCurve"))
                    {
                        responseLineIndices.Add(i);
                    }
                    else if (line.Equals("EvacuationGroup"))
                    {
                        groupLineIndices.Add(i);
                    }
                    else if (line.Equals("Demographics"))
                    {
                        demographicsLineIndices.Add(i);
                    }
                    else
                    {
                        headerLineIndices.Add(line, i);
                    }                       
                }
            }

            //now see if we have what we need
            int lineindex;
            string nameOfInput = string.Empty;

            //Reading on past a gap means a later parser can meet state an earlier one would have
            //stopped before producing. That is worth catching rather than risking: a throw here used
            //to be impossible because parsing gave up first, and losing the whole scenario to one
            //would be a poor trade for being able to edit it.
            try
            {

            //simulation
            success = true; //each section is judged on its own
            nameOfInput = nameof(Simulation);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);                
                newInput.Simulation.Parse(inputLines, lineindex, out success);
            }
            else
            {
                //critical
                Engine.Message(null, Engine.LogType.InputError, nameOfInput + " header not found." + pleaseCheckInput);
                SectionIncomplete(nameOfInput);
            }
            if(!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //landscape
            //Read before the zone is pinned, because it is one of the things that can supply the zone -
            //and read here, before any section that converts a coordinate, for the same reason the
            //pinning is done here.
            success = true;
            nameOfInput = nameof(Landscape);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.Landscape.Parse(inputLines, lineindex, rootFolder, out success);
            }
            //No message when absent: a scenario is allowed to have no landscape section at all, which
            //is the case for every scenario written before this existed.
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //Done here, immediately after the simulation section and before any section that reads the
            //origin, because pinning changes what every simulation coordinate means. Doing it later
            //would leave whatever had already been converted measured in the old zone.
            _currentSection = nameof(Simulation);
            PinSimulationZoneToGeoreferencedData(newInput, inputLines, headerLineIndices, rootFolder);

            //map
            success = true; //each section is judged on its own
            nameOfInput = nameof(Map);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.Map.Parse(inputLines, lineindex, out success);
            }
            else
            {
                //does not matter
                Engine.Message(null, Engine.LogType.Warning, nameOfInput + " header not found, using defaults.");
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //weather
            success = true; //each section is judged on its own
            nameOfInput = nameof(Weather);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.Weather.Parse(inputLines, lineindex, rootFolder, out success);
            }
            else
            {
                //might not matter
                Engine.Message(null, Engine.LogType.Warning, nameOfInput + " header not found, no weather will be loaded.");
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //pedestrian module
            success = true; //each section is judged on its own
            nameOfInput = nameof(PedestrianModule);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.PedestrianModule.Parse(inputLines, lineindex, headerLineIndices, out success);
            }
            else
            {
                Engine.Message(null, Engine.LogType.Log, "No pedestrian module defined.");
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //traffic module
            success = true; //each section is judged on its own
            nameOfInput = nameof(TrafficModule);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.TrafficModule.Parse(inputLines, lineindex, headerLineIndices, rootFolder, out success);
            }
            else
            {
                Engine.Message(null, Engine.LogType.Log, "No traffic module defined.");
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //wildfire module
            success = true; //each section is judged on its own
            nameOfInput = nameof(WildfireModule);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.WildfireModule.Parse(inputLines, lineindex, newInput.Simulation, newInput.Weather, newInput.Landscape, headerLineIndices, rootFolder, out success);
            }
            else
            {               
                Engine.Message(null, Engine.LogType.Log, "No wildfire module defined.");
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //smoke module
            success = true; //each section is judged on its own
            nameOfInput = nameof(SmokeModule);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.SmokeModule.Parse(inputLines, lineindex, headerLineIndices, newInput.Weather, rootFolder, out success);
            }
            else
            {
                Engine.Message(null, Engine.LogType.Log, "No smoke module defined.");
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //trigger buffer
            success = true; //each section is judged on its own
            nameOfInput = nameof(TriggerBufferModule);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.TriggerBufferModule.Parse(inputLines, lineindex, headerLineIndices, rootFolder, out success);
            }
            else
            {
                //does not matter, not active per default
                newInput.TriggerBufferModule = new TriggerBufferModuleInput();
                Engine.Message(null, Engine.LogType.Warning, nameOfInput + " header not found, using defaults (disabled).");
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //population, must be before evacuation due to dependence on demographics      
            success = true; //each section is judged on its own
            nameOfInput = nameof(Population);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.Population.Parse(inputLines, lineindex, demographicsLineIndices, newInput.PedestrianModule, rootFolder, out success);
            }
            else if(newInput.PedestrianModule.Enabled)
            {
                //critical
                Engine.Message(null, Engine.LogType.InputError, nameOfInput + " header not found but user has requested pedestrian module." + pleaseCheckInput);
                SectionIncomplete(nameOfInput);
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //events
            success = true; //each section is judged on its own
            nameOfInput = nameof(Events);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {
                ReadingInputMessage(nameOfInput);
                newInput.Events.Parse(inputLines, lineindex, rootFolder, out success);
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, nameOfInput + " header not found, no events will be added.");
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }

            //evacuation            
            success = true; //each section is judged on its own
            nameOfInput = nameof(Evacuation);
            if (headerLineIndices.TryGetValue(nameOfInput, out lineindex))
            {                    
                ReadingInputMessage(nameOfInput);
                newInput.Evacuation.Parse(inputLines, lineindex, newInput.Simulation, newInput.Events, newInput.Population, newInput.PedestrianModule, newInput.TrafficModule, destinationLineIndices, responseLineIndices, groupLineIndices, rootFolder, out success);
            }
            else if(newInput.PedestrianModule.Enabled || newInput.TrafficModule.Enabled)
            {
                //critical
                success = false;
                Engine.Message(null, Engine.LogType.InputError, nameOfInput + " header not found but user has requested pedestrian and/or traffic modules." + pleaseCheckInput);
            }
            if (!success)
            {
                SectionIncomplete(nameOfInput);
            }            

            }
            catch (System.Exception e)
            {
                Engine.Message(null, Engine.LogType.Exception, $"Reading section {nameOfInput} threw: {e.Message}. The rest of the scenario was still loaded.");
                AddRequirement(string.IsNullOrEmpty(nameOfInput) ? "Scenario" : nameOfInput, "Could not be read: " + e.Message, true);
            }

            //Every section is now read whatever the ones before it did, and what is missing is
            //reported as a checklist instead of stopping the load. A scenario is built up over
            //several sittings, so half-finished is its normal state - abandoning the parse at the
            //first gap threw away everything already parsed and left nothing to carry on editing.
            //success still means "complete enough to run", so nothing downstream starts a simulation
            //on a scenario with holes in it.
            success = RequirementsMet;
            return newInput;
        }

        /// <summary>
        /// Measures the simulation in the UTM zone the fire data is in, rather than the one the
        /// domain's own south-west corner falls in.
        ///
        /// A raster is placed in the scene by subtracting the simulation's origin from the raster's
        /// corner, which only means anything if both are measured in the same zone. Nothing checked
        /// that, and a domain near a zone boundary can easily disagree with its own data: Mati's corner
        /// is at 23.93 E with the boundary at 24 E, its ELMFIRE output is in zone 35 while the corner is
        /// in zone 34, and the fire was consequently placed 526 km west of the town.
        ///
        /// The fire's arrival time raster is preferred, because that is the grid the fire is placed on
        /// and the one k-PERIL computes on, so agreeing with it matters most. Failing that, the
        /// landscape - which for a scenario with no fire data at all may be nothing but a DEM, and a DEM
        /// can be had for anywhere on Earth. A FARSITE .lcp is skipped: the format has no field for a
        /// CRS, so there is nothing in it to read.
        /// </summary>
        private static void PinSimulationZoneToGeoreferencedData(PREACTInput input, string[] inputLines,
            Dictionary<string, int> headerLineIndices, string rootFolder)
        {
            string source = FireDataReferenceFile(inputLines, headerLineIndices);
            if (string.IsNullOrEmpty(source))
            {
                source = input.Landscape.GetReferenceFile();
            }

            if (string.IsNullOrEmpty(source))
            {
                //Nothing georeferenced to agree with, so the domain's own zone stands. That is correct
                //rather than a compromise: with no raster there is nothing whose easting could be
                //misread.
                return;
            }

            //A .lcp keeps no CRS, so reading one would only produce a spurious "does not say" warning.
            if (source.ToLowerInvariant().EndsWith(".lcp"))
            {
                return;
            }

            string path = System.IO.Path.Combine(rootFolder, source);
            if (!System.IO.File.Exists(path))
            {
                //Reported by whichever section requires it; nothing to add here beyond not pinning.
                return;
            }

            Utility.AscRaster.Header header = Utility.AscRaster.ReadHeader(path, out bool ok);
            if (!ok)
            {
                return;
            }

            if (header.EpsgCode == 0)
            {
                //Worth saying, because the consequence is silent: the raster's easting is then used as
                //if it were in the simulation's zone, and if it is not, everything from that raster
                //lands somewhere else.
                Engine.Message(null, Engine.LogType.Warning,
                    System.IO.Path.GetFileName(path) + " does not say which coordinate system it is in, so it is "
                    + "assumed to be in the simulation's own UTM zone. Give it a .prj, or use a GeoTIFF, if it is not.");
                return;
            }

            input.Simulation.Data.PinToUtmEpsg(header.EpsgCode, out bool pinned);
            if (!pinned)
            {
                //_currentSection is Simulation here, which is where this belongs on the checklist.
                AddRequirement("UTM zone",
                    $"{System.IO.Path.GetFileName(path)} is in EPSG:{header.EpsgCode}, which the simulation cannot "
                    + "measure in. Everything read from that raster will be misplaced.", true);
            }
        }

        /// <summary>
        /// The imported fire's arrival time raster, read straight from the lines rather than from the
        /// parsed input because the wildfire section has not been read yet at the point this is needed.
        /// </summary>
        private static string FireDataReferenceFile(string[] inputLines, Dictionary<string, int> headerLineIndices)
        {
            if (!headerLineIndices.TryGetValue("AscImport", out int lineIndex))
            {
                return string.Empty;
            }

            Dictionary<string, string> ascInput = GetHeaderInput(inputLines, lineIndex);
            return ascInput.TryGetValue("TimeOfArrivalFile", out string arrivalFile) ? arrivalFile : string.Empty;
        }

        /// <summary>
        /// Reads all input under header until next header is found.
        /// </summary>
        /// <param name="inputLines"></param>
        /// <param name="startIndex"></param>
        /// <returns></returns>
        public static Dictionary<string, string> GetHeaderInput(string[] inputLines, int startIndex, bool collectPureDataLines = false)
        {
            Dictionary<string, string> inputToParse = new Dictionary<string, string>();
            //first line is header
            int lineIndex = startIndex + 1;

            while (true)
            {
                if (lineIndex >= inputLines.Length)
                {
                    break;
                }

                string line = inputLines[lineIndex];
                //we have found next header, exit
                if (line.StartsWith('['))
                {
                    break;
                }
                //empty or comment
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    ++lineIndex;
                    continue;
                }

                string[] input = line.Split(inputSplit);
                if (input.Length >= 2)
                {
                    inputToParse.Add(input[0], input[1]);
                }

                //this is e.g. ramps that have no Variable=value structure
                if(collectPureDataLines)
                {                    
                    input = line.Split(',');
                    if (input.Length >= 2)
                    {
                        inputToParse.Add("dataLine" + lineIndex, line);
                    }
                }

                ++lineIndex;
            }

            return inputToParse;
        }

        /// <summary>
        /// One thing a scenario still needs before it can run.
        ///
        /// These are gathered while reading so an incomplete scenario can be presented as a checklist
        /// rather than as a wall of log lines. A scenario under construction is the normal case, not
        /// an error: it is built up over several sittings, and the parts arrive in whatever order
        /// they are produced.
        /// </summary>
        public class InputRequirement
        {
            public string Section = string.Empty;
            public string Key = string.Empty;
            public string Message = string.Empty;
            public bool Critical;

            public override string ToString()
            {
                return string.IsNullOrEmpty(Section) ? Key : Section + " / " + Key;
            }
        }

        private static readonly List<InputRequirement> _requirements = new List<InputRequirement>();
        private static string _currentSection = string.Empty;

        /// <summary>What the last read scenario still needs. Rebuilt by every load.</summary>
        public static List<InputRequirement> Requirements { get => _requirements; }

        /// <summary>True when nothing critical is outstanding, so the scenario can actually be run.</summary>
        public static bool RequirementsMet
        {
            get
            {
                for (int i = 0; i < _requirements.Count; ++i)
                {
                    if (_requirements[i].Critical) return false;
                }
                return true;
            }
        }

        private static void AddRequirement(string key, string message, bool critical)
        {
            //Deduplicated: several parsers report the same missing key by way of both their own check
            //and CheckIfFileExist, and a checklist that lists an item twice reads as two problems.
            for (int i = 0; i < _requirements.Count; ++i)
            {
                if (_requirements[i].Section == _currentSection && _requirements[i].Key == key)
                {
                    //Critical wins, so a hard requirement is never masked by a softer duplicate.
                    _requirements[i].Critical |= critical;
                    return;
                }
            }

            _requirements.Add(new InputRequirement
            {
                Section = _currentSection,
                Key = key,
                Message = message,
                Critical = critical
            });
        }

        public static void ReadingInputMessage(string nameOfInput)
        {
            //Doubles as the section marker for anything reported while this section is being read,
            //so requirements can name where they came from without every parser passing it along.
            _currentSection = nameOfInput;
            Engine.Message(null, Engine.LogType.Log, nameOfInput + " input is being read...");
        }

        public static void InputNotFoundMessage(string nameOfInput, bool critical = false, string defaultValue = "VALUE")
        {
            if(critical)
            {
                Engine.Message(null, Engine.LogType.InputError, nameOfInput + " was not found, this value is critical for the simulation to function based on the given input parameters." + pleaseCheckInput);
                AddRequirement(nameOfInput, "Required, and not set.", true);
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, $"{nameOfInput} was not found, default value {defaultValue} has been used.");
                AddRequirement(nameOfInput, $"Not set; defaulted to {defaultValue}.", false);
            }
        }

        public static void CriticalDependency(string missingDependency)
        {
            Engine.Message(null, Engine.LogType.InputError, $"Current module requires {missingDependency} to be set." + pleaseCheckInput);
            AddRequirement(missingDependency, "Required by this module.", true);
        }

        public static void MissingReferenceToOtherInput(string nameOfInput, string missingReference)
        {
            Engine.Message(null, Engine.LogType.InputError, nameOfInput + " reference another input (" + missingReference + ") that could not be found." + pleaseCheckInput);
            AddRequirement(nameOfInput, $"Refers to \"{missingReference}\", which does not exist.", true);
        }

        public static void IncorrectInputCount(string nameOfInput)
        {
            Engine.Message(null, Engine.LogType.InputError, nameOfInput + " does not contain the expected number of inputs." + pleaseCheckInput);
            AddRequirement(nameOfInput, "Does not have the expected number of values.", true);
        }

        public static void CouldNotInterpretInputMessage(string nameOfInput, string userInput)
        {
            Engine.Message(null, Engine.LogType.InputError, "Could not interpret user input " + userInput + " for " + nameOfInput + ".");
            AddRequirement(nameOfInput, $"Value \"{userInput}\" could not be interpreted.", true);
        }

        /// <summary>
        /// Records that a section could not be read through to the end, so the checklist says which
        /// part of the file is incomplete even when the parser stopped before naming a specific key.
        /// </summary>
        private static void SectionIncomplete(string nameOfInput)
        {
            //Only worth saying when nothing more specific was already reported. A section that failed
            //because one named key is missing does not also need "this section is incomplete" beside
            //it - that reads as two problems where there is one.
            for (int i = 0; i < _requirements.Count; ++i)
            {
                if (_requirements[i].Critical && _requirements[i].Section == nameOfInput)
                {
                    return;
                }
            }

            _currentSection = nameOfInput;
            AddRequirement(nameOfInput, "This section could not be read completely.", true);
        }

        public static void CheckIfFileExist(string nameOfInput, string inputData, string rootFolder, out bool success, bool critical = true)
        {
            success = true;
            string filePath = Path.Combine(rootFolder, inputData);

            if (!File.Exists(filePath))
            {
                success = false;
                nameOfInput += "(" + inputData + ")";
                PREACTInput.InputNotFoundMessage(nameOfInput, critical);
            }
        }

        public static void CheckIfFilesExists(string nameOfInput, string[] inputData, string rootFolder, out bool success)
        {
            success = true;
            int issues = 0;

            for (int i = 0; i < inputData.Length; ++i)
            {                
                CheckIfFileExist(nameOfInput, inputData[i], rootFolder, out success);
                issues += success ? 0 : 1;
            }

            if (issues > 0)
            {
                success = false;
            }
        }
    }
}

