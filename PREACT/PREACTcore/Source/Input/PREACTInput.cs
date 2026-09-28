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
    /// <summary>
    /// A scenario (<c>.wui</c>), and the reader that turns one into it.
    /// </summary>
    /// <remarks>
    /// <b>Format rules</b>, applied to every section by <see cref="NormaliseLine"/> and <see cref="GetHeaderInput"/>:
    /// <list type="bullet">
    /// <item>A line whose first non-blank character is <c>#</c> is a comment. A <c>#</c> preceded by whitespace
    /// starts a comment to the end of the line; a <c>#</c> inside a word (a URL, a file name) is kept.</item>
    /// <item><c>Key=Value</c> is split on the <b>first</b> <c>=</c>; spaces are removed from the key, the value is
    /// trimmed. A value may contain <c>=</c>.</item>
    /// <item>A key given twice in one section is reported and the first value is used (never thrown).</item>
    /// <item>A non-repeatable section given twice is reported and the first is read (never thrown).</item>
    /// <item>Every section is read on its own: an exception in one is reported against that section and the
    /// rest of the file is still read.</item>
    /// <item>Paths are stored with forward slashes and resolved through <see cref="Utility.ScenarioFileLocator"/>
    /// relative to the scenario's folder, on every platform.</item>
    /// <item>Numbers and dates are read with the invariant culture (<see cref="InputParse"/>).</item>
    /// </list>
    /// <b>One criticality rule</b>: a requirement is critical if and only if the run cannot proceed without it.
    /// Anything the run can do without - a default, an optional file, a module that is switched off - is on the
    /// checklist as a non-critical item. Sections belonging to a disabled module are still read in full (so a
    /// save keeps them), but nothing in them is critical.
    /// </remarks>
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
            Evacuation = new EvacuationInput();
            PedestrianModule = new PedestrianModuleInput();
            TrafficModule = new TrafficModuleInput();
            WildfireModule = new WildfireModuleInput();
            SmokeModule = new SmokeInput();
            TriggerBufferModule = new TriggerBufferModuleInput();
        }

        public static PREACTInput LoadFromDisk(string filePath, out bool success)
        {
            success = false;
            PREACTInput input = null;
            if(!File.Exists(filePath))
            {
                Engine.Message(null, Engine.LogType.InputError, " Input file " + filePath + " does not exist.");
            }
            else
            {
                //Absolute, so everything resolved against it means the same whatever the working directory
                //is when a relative path is used later.
                string rootFolder = Path.GetDirectoryName(Path.GetFullPath(filePath));
                Engine.Message(null, Engine.LogType.Log, " Reading input file " + filePath + ".");
                input = LoadFromLines(File.ReadAllLines(filePath), rootFolder, out success);
                if (success)
                {
                    Engine.Message(null, Engine.LogType.Log, " Input file " + filePath + " loaded.");
                }
                else
                {
                    //Loaded, just not finished. Saying "could not be loaded" was misleading once the
                    //parse started returning what it managed to read.
                    int outstanding = 0;
                    foreach (InputRequirement requirement in Requirements)
                    {
                        if (requirement.Critical) ++outstanding;
                    }
                    Engine.Message(null, Engine.LogType.Log, $" Input file {filePath} loaded with {outstanding} item(s) still required; see the scenario checklist.");
                }
            }

            return input;
        }

        /// <summary>
        /// Reads a scenario from its lines, resolving paths against <paramref name="rootFolder"/>. The lines are
        /// not modified. <paramref name="success"/> is <see cref="RequirementsMet"/> for this parse.
        /// </summary>
        /// <remarks>
        /// The way to validate a scenario held in memory, or one written somewhere other than its own folder,
        /// without writing a file into the scenario folder (see <see cref="Revalidate"/>).
        /// </remarks>
        public static PREACTInput LoadFromLines(string[] lines, string rootFolder, out bool success)
        {
            string[] copy = (string[])(lines ?? System.Array.Empty<string>()).Clone();
            //One parse at a time: the checklist is built in a per-thread list and published at the end, but the
            //parsers themselves share a little static state (the ELMFIRE namelist's reflection cache, the
            //section marker), and nothing is gained by interleaving two loads.
            lock (_parseLock)
            {
                return ParseInput(rootFolder, copy, out success);
            }
        }

        public const string pleaseCheckInput = " Please check your input file.";

        //Section headers the parser knows. Repeated ones are collected in order; the rest are one-off.
        private static readonly string[] RepeatableSections = { "Destination", "ResponseCurve", "EvacuationGroup", "Demographics", "IgnitionPoint" };

        //Sections that used to exist and are now ignored. Tolerated on read, said once, never written.
        private static readonly Dictionary<string, string> RetiredSections = new Dictionary<string, string>
        {
            { "Events", "The events feature (BlockGoalEventFiles) was never implemented - its file reader was an empty stub - and has been removed." },
            { "WUIShow", "WUIShow streaming has been removed." },
            { "SimpleWildfireCA", RemovedFireModel }, { "ElmClone", RemovedFireModel }, { "CellParticleHybrid", RemovedFireModel },
            { "FireCell", RemovedFireModel },
            { "Behave", RemovedSpreadTable }, { "Rothermel", RemovedSpreadTable },
            { "AdvectDiffuse3D", RemovedSmokeModel }, { "AdvectDiffuseMixingLayer", RemovedSmokeModel },
            { "CityFlow", RemovedTrafficModel }, { "MacroTrafficSim", RemovedTrafficModel },
        };

        private const string RemovedFireModel = "It configured a cell-based fire spread model, which has been removed; use ELMFIRE, or AscImport for a fire computed elsewhere.";
        private const string RemovedSpreadTable = "The rate of spread now always comes from the fire module.";
        private const string RemovedSmokeModel = "That smoke model has been removed; GlobalSmoke is the one smoke module.";
        private const string RemovedTrafficModel = "That traffic model has been removed; SUMO is the one traffic module.";

        /// <summary>
        /// The sections the parser reads, each with the input type whose writable members are its keys
        /// (<see cref="PREACTInputWriter.FileKeys"/>): what the file may say is what a save writes. Anything else in a
        /// section, and any other section, is reported once when the scenario is read (<see cref="ReportIgnored"/>),
        /// because a save does not write it - a typo in a hand edit used to vanish without a word.
        /// </summary>
        private static readonly Dictionary<string, System.Type> SectionTypes = new Dictionary<string, System.Type>
        {
            { nameof(Simulation), typeof(SimulationInput) },
            { nameof(Map), typeof(MapInput) },
            { nameof(Landscape), typeof(LandscapeInput) },
            { nameof(Weather), typeof(WeatherInput) },
            { nameof(Population), typeof(PopulationInput) },
            { "Demographics", typeof(DemographicsInput) },
            { nameof(Evacuation), typeof(EvacuationInput) },
            { "ResponseCurve", typeof(ResponseCurve) },
            { "Destination", typeof(EvacuationDestinationInput) },
            { "EvacuationGroup", typeof(EvacuationGroupInput) },
            { nameof(PedestrianModule), typeof(PedestrianModuleInput) },
            { nameof(PedestrianModuleInput.PedestrianModules.MacroHouseholdSim), typeof(MacroHouseholdSimInput) },
            { nameof(TrafficModule), typeof(TrafficModuleInput) },
            { nameof(TrafficModuleInput.TrafficModules.SUMO), typeof(SUMOInput) },
            { nameof(WildfireModule), typeof(WildfireModuleInput) },
            { nameof(WildfireModuleInput.WildfireModules.AscImport), typeof(AscImportInput) },
            { nameof(WildfireModuleInput.WildfireModules.ELMFIRE), typeof(ElmfireInput) },
            { ElmfireInput.NamelistSection, typeof(ElmfireNamelistInput) },
            { "IgnitionPoint", typeof(Wildfire.IgnitionPointInput) },
            { nameof(SmokeModule), typeof(SmokeInput) },
            { nameof(SmokeInput.SmokeModules.GlobalSmoke), typeof(GlobalSmokeInput) },
            { nameof(TriggerBufferModule), typeof(TriggerBufferModuleInput) },
            { nameof(TriggerBufferModuleInput.TriggerBufferModules.kPERIL), typeof(kPERILInput) },
        };

        /// <summary>Keys a parser reads under an older name and writes under the new one (it says so itself).</summary>
        private static readonly HashSet<string> ConvertedKeys = new HashSet<string>
        {
            "ELMFIRE|SimulationTstopSeconds",
        };

        /// <summary>Keys that used to be part of the format and are now ignored, <c>Section|Key</c> to what became of them.</summary>
        private static readonly Dictionary<string, string> RetiredKeys = new Dictionary<string, string>
        {
            { "Evacuation|UseTriggerBufferEvacuation", "it was never read by the engine." },
            { "Evacuation|TriggerBufferFile", "it was never read by the engine." },
            { "Evacuation|EvacuationOrderStart", "it was never read by the engine; each [EvacuationGroup] has its own EvacuationOrderDateTime." },
            { "Weather|DesiredLatLon", "it was read but never used." },
            { "Weather|HasWeatherAnchor", "it only restated whether WeatherAnchorDateTime is set." },
            { "TrafficModule|VisibilityAffectsSpeed", "smoke acts on traffic through [SUMO] SmokeAlpha and SmokeBeta." },
            { "SUMO|UTMoffset", "the SUMO network carries its own offset." },
            { "kPERIL|WindBand", "each cell takes the band covering the time the fire reached it." },
            { "kPERIL|MidflameWindspeed", "the rate of spread comes from the fire module, and the midflame wind from the fire." },
            { "kPERIL|CalculateROSFromBehave", "the rate of spread always comes from the fire module." },
            { "kPERIL|InitialFuelMoistureFile", "the rate of spread always comes from the fire module." },
            { "kPERIL|FuelModelsFile", "the rate of spread always comes from the fire module." },
            { "ELMFIRE|IgnitionPointsFile", "ignition points are [IgnitionPoint] sections now (Fire > Ignition points); the CSV is not read." },
        };

        private static string RemoveSpace(string input)
        {
            var chars = new System.Text.StringBuilder(input.Length);
            for(int i = 0; i < input.Length; ++i)
            {
                if (input[i] != ' ' && input[i] != '\t')
                {
                    chars.Append(input[i]);
                }
            }

            return chars.ToString();
        }

        /// <summary>
        /// The line without its comment: from a <c>#</c> that starts the line or follows whitespace, to the end.
        /// A <c>#</c> inside a word is kept, so a URL fragment or a file name containing one survives.
        /// </summary>
        private static string StripComment(string line)
        {
            for (int i = 0; i < line.Length; ++i)
            {
                if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1])))
                {
                    return line.Substring(0, i);
                }
            }
            return line;
        }

        /// <summary>
        /// Tidies a line for the parsers without destroying what it says.
        ///
        /// Spaces used to be stripped from the whole line, which is fine for a key and for a row of numbers
        /// and quietly ruinous for a value: <c>C:/Program Files/QGIS 3.44.2/bin</c> was read back as
        /// <c>C:/ProgramFiles/QGIS3.44.2/bin</c>, so any path with a space in it - which on Windows means
        /// most absolute paths - named something that does not exist.
        ///
        /// So the comment is removed first (it needs the whitespace to be recognised), then the key is
        /// stripped, the value is only trimmed, and anything that is not a key/value pair - a section
        /// header, a bare row of data - is stripped as before. The value used to keep a trailing space when
        /// it was followed by a comment (<c>Module=AscImport #note</c> read as <c>"AscImport "</c>).
        /// </summary>
        private static string NormaliseLine(string input)
        {
            string line = StripComment(input).Trim();
            int equals = line.IndexOf('=');
            if (equals <= 0 || line.StartsWith("["))
            {
                return RemoveSpace(line);
            }

            return RemoveSpace(line.Substring(0, equals)) + "=" + line.Substring(equals + 1).Trim();
        }

        private static readonly object _parseLock = new object();

        private static PREACTInput ParseInput(string rootFolder, string[] inputLines, out bool success)
        {
            success = false;
            PREACTInput newInput = new PREACTInput(rootFolder);
            Dictionary<string, int> headerLineIndices = new Dictionary<string, int>();

            //The checklist describes the file being read now, not whatever was read before it. Collected per
            //parse and published when the parse is done, so a reader of Requirements never sees a half-built
            //list, and two parses on two threads cannot mix their items.
            _collecting = new List<InputRequirement>();
            _currentSection = string.Empty;
            _softDepth = 0;
            _duplicateKeysReported = new HashSet<string>();

            //Every header in file order, and the ones given twice (reported as duplicates, never read), for the
            //report of what no parser read.
            var allHeaders = new List<int>();
            var duplicateHeaders = new HashSet<int>();
            _sectionsRead = new HashSet<int>();
            _parsingLines = inputLines;

            List<int> destinationLineIndices = new List<int>();
            List<int> responseLineIndices = new List<int>();
            List<int> groupLineIndices = new List<int>();
            List<int> demographicsLineIndices = new List<int>();
            List<int> ignitionPointLineIndices = new List<int>();

            //As the file says them, for quoting a line nothing reads: normalising takes the spaces out of any line that
            //is not Key=Value ("stray text" was quoted as "straytext", e2e N4).
            string[] asWritten = (string[])inputLines.Clone();

            try
            {
                //first index all headers
                for (int i = 0; i < inputLines.Length; ++i)
                {
                    inputLines[i] = NormaliseLine(inputLines[i] ?? string.Empty);
                    string line = inputLines[i];
                    if (!line.StartsWith("["))
                    {
                        continue;
                    }

                    string name = line.Trim('[', ']');
                    allHeaders.Add(i);
                    switch (name)
                    {
                        case "Destination": destinationLineIndices.Add(i); break;
                        case "ResponseCurve": responseLineIndices.Add(i); break;
                        case "EvacuationGroup": groupLineIndices.Add(i); break;
                        case "Demographics": demographicsLineIndices.Add(i); break;
                        case "IgnitionPoint": ignitionPointLineIndices.Add(i); break;
                        default:
                            if (headerLineIndices.TryGetValue(name, out int first))
                            {
                                //Reported, not thrown: this used to be a Dictionary.Add outside any try, so a
                                //section pasted twice crashed whatever was loading the file.
                                _currentSection = name;
                                Engine.Message(null, Engine.LogType.Warning,
                                    $"[{name}] appears more than once (lines {first + 1} and {i + 1}); only the first is read.");
                                AddRequirement("[" + name + "]",
                                    $"Given more than once (lines {first + 1} and {i + 1}); only the first is read and saved.", false);
                                duplicateHeaders.Add(i);
                            }
                            else
                            {
                                headerLineIndices.Add(name, i);
                            }
                            break;
                    }
                }

                foreach (KeyValuePair<string, string> retired in RetiredSections)
                {
                    if (headerLineIndices.ContainsKey(retired.Key))
                    {
                        _currentSection = retired.Key;
                        Engine.Message(null, Engine.LogType.Warning, $"[{retired.Key}] is ignored. {retired.Value} It will not be written when the scenario is saved.");
                        AddRequirement("[" + retired.Key + "]", "No longer supported and ignored; it is dropped on the next save. " + retired.Value, false);
                    }
                }

                //Each section is read in its own guard: an exception in one is reported against it and the
                //rest are still read. There used to be one try around all of them, so a single bad value
                //discarded every later section - and the GUI, which accepts incomplete scenarios, would then
                //save the gutted scenario back over the original.

                //simulation
                ReadSection(nameof(Simulation), true, () =>
                {
                    if (headerLineIndices.TryGetValue(nameof(Simulation), out int lineindex))
                    {
                        newInput.Simulation.Parse(inputLines, lineindex, out bool ok);
                        return ok;
                    }
                    Engine.Message(null, Engine.LogType.InputError, nameof(Simulation) + " header not found." + pleaseCheckInput);
                    AddRequirement("[Simulation]", "Required, and not in the file.", true);
                    return false;
                });

                //landscape
                //Read before the zone is pinned, because it is one of the things that can supply the zone -
                //and read here, before any section that converts a coordinate, for the same reason the
                //pinning is done here. No message when absent: a scenario is allowed to have no landscape
                //section at all.
                ReadSection(nameof(Landscape), false, () =>
                {
                    if (headerLineIndices.TryGetValue(nameof(Landscape), out int lineindex))
                    {
                        newInput.Landscape.Parse(inputLines, lineindex, rootFolder, out bool ok);
                        return ok;
                    }
                    return true;
                });

                //Done here, immediately after the simulation section and before any section that reads the
                //origin, because pinning changes what every simulation coordinate means. Doing it later
                //would leave whatever had already been converted measured in the old zone.
                ReadSection(nameof(Simulation), false, () =>
                {
                    PinSimulationZoneToGeoreferencedData(newInput, inputLines, headerLineIndices, rootFolder);
                    return true;
                });

                //map
                ReadSection(nameof(Map), false, () =>
                {
                    if (headerLineIndices.TryGetValue(nameof(Map), out int lineindex))
                    {
                        newInput.Map.Parse(inputLines, lineindex, out bool ok);
                        return ok;
                    }
                    return true; //defaults are fine
                });

                //weather
                ReadSection(nameof(Weather), false, () =>
                {
                    if (headerLineIndices.TryGetValue(nameof(Weather), out int lineindex))
                    {
                        newInput.Weather.Parse(inputLines, lineindex, rootFolder, out bool ok);
                        return ok;
                    }
                    //Not critical: the weather is reported, not used by the evacuation or the trigger boundary.
                    //The same item a [Weather] without a WeatherFile gives, so the two read alike on the checklist.
                    OptionalInputMissing(nameof(WeatherInput.WeatherFile), WeatherInput.NoWeatherFileConsequence);
                    return true;
                });

                //pedestrian module
                ReadSection(nameof(PedestrianModule), false, () =>
                {
                    if (ModuleHeader(headerLineIndices, nameof(PedestrianModule), out int lineindex,
                        nameof(PedestrianModuleInput.PedestrianModules.MacroHouseholdSim)))
                    {
                        newInput.PedestrianModule.Parse(inputLines, lineindex, headerLineIndices, out bool ok);
                        return ok;
                    }
                    Engine.Message(null, Engine.LogType.Log, "No pedestrian module defined.");
                    return true;
                });

                //traffic module
                ReadSection(nameof(TrafficModule), false, () =>
                {
                    if (ModuleHeader(headerLineIndices, nameof(TrafficModule), out int lineindex,
                        nameof(TrafficModuleInput.TrafficModules.SUMO)))
                    {
                        newInput.TrafficModule.Parse(inputLines, lineindex, headerLineIndices, rootFolder, out bool ok);
                        return ok;
                    }
                    Engine.Message(null, Engine.LogType.Log, "No traffic module defined.");
                    return true;
                });

                //wildfire module
                ReadSection(nameof(WildfireModule), false, () =>
                {
                    if (ModuleHeader(headerLineIndices, nameof(WildfireModule), out int lineindex,
                            nameof(WildfireModuleInput.WildfireModules.AscImport), nameof(WildfireModuleInput.WildfireModules.ELMFIRE),
                            ElmfireInput.NamelistSection)
                        || ignitionPointLineIndices.Count > 0)
                    {
                        //A switched-off fire module cannot make the run fail, so nothing it reports is critical.
                        using (SoftRequirements(!PeekBool(inputLines, lineindex, nameof(WildfireModuleInput.Enabled))))
                        {
                            newInput.WildfireModule.Parse(inputLines, lineindex, newInput.Simulation, newInput.Weather, newInput.Landscape, headerLineIndices, ignitionPointLineIndices, rootFolder, out bool ok);
                            return ok;
                        }
                    }
                    Engine.Message(null, Engine.LogType.Log, "No wildfire module defined.");
                    return true;
                });

                //smoke module
                ReadSection(nameof(SmokeModule), false, () =>
                {
                    if (ModuleHeader(headerLineIndices, nameof(SmokeModule), out int lineindex,
                        nameof(SmokeInput.SmokeModules.GlobalSmoke)))
                    {
                        newInput.SmokeModule.Parse(inputLines, lineindex, headerLineIndices, newInput.Weather, rootFolder, out bool ok);
                        return ok;
                    }
                    Engine.Message(null, Engine.LogType.Log, "No smoke module defined.");
                    return true;
                });

                //trigger buffer
                ReadSection(nameof(TriggerBufferModule), false, () =>
                {
                    if (ModuleHeader(headerLineIndices, nameof(TriggerBufferModule), out int lineindex,
                        nameof(TriggerBufferModuleInput.TriggerBufferModules.kPERIL)))
                    {
                        newInput.TriggerBufferModule.Parse(inputLines, lineindex, headerLineIndices, newInput.Simulation, rootFolder, out bool ok);
                        return ok;
                    }
                    //does not matter, not active per default
                    Engine.Message(null, Engine.LogType.Log, nameof(TriggerBufferModule) + " header not found, the trigger boundary is off.");
                    return true;
                });

                //population, must be before evacuation due to dependence on demographics
                ReadSection(nameof(Population), false, () =>
                {
                    bool needed = newInput.PedestrianModule.Enabled;
                    if (headerLineIndices.TryGetValue(nameof(Population), out int lineindex))
                    {
                        newInput.Population.Parse(inputLines, lineindex, demographicsLineIndices, newInput.PedestrianModule, rootFolder, out bool ok);
                        return ok;
                    }
                    //Demographics are their own sections; read them even without a [Population] header (the writer
                    //omits one that holds only defaults), so groups can still refer to them and a save keeps them.
                    using (SoftRequirements(!needed))
                    {
                        PREACT.Evacuation.DemographicsInput.Parse(newInput.Population.Demographics, inputLines, demographicsLineIndices, out bool _);
                    }
                    if (needed)
                    {
                        Engine.Message(null, Engine.LogType.InputError, nameof(Population) + " header not found but the pedestrian module is enabled." + pleaseCheckInput);
                        AddRequirement("[Population]", "Required by the pedestrian module, and not in the file.", true);
                        return false;
                    }
                    return true;
                });

                //evacuation
                ReadSection(nameof(Evacuation), false, () =>
                {
                    //Destinations, response curves and groups are their own sections, and the [Evacuation] header
                    //holds nothing current - the writer omits it - so its absence is not a problem, and the
                    //lists are read either way; Parse names whichever of them a module needs and lacks. Returning
                    //early here when there were no destinations or groups yet (every new scenario) skipped the
                    //response curves too, so the first save deleted the standard curve a new scenario starts with.
                    if (!headerLineIndices.TryGetValue(nameof(Evacuation), out int lineindex))
                    {
                        lineindex = -1;
                    }
                    newInput.Evacuation.Parse(inputLines, lineindex, newInput.Simulation, newInput.Population, newInput.PedestrianModule, newInput.TrafficModule, destinationLineIndices, responseLineIndices, groupLineIndices, rootFolder, out bool ok);
                    return ok;
                });

                //Last, once every parser has had its sections: what the file says that none of them read.
                ReadSection("Scenario", false, () =>
                {
                    ReportIgnored(inputLines, asWritten, allHeaders, duplicateHeaders);
                    return true;
                });
            }
            catch (System.Exception e)
            {
                //Only reachable from the header scan itself, which touches nothing but strings.
                Engine.Message(null, Engine.LogType.Exception, "Reading the scenario threw: " + e.Message);
                _currentSection = "Scenario";
                AddRequirement("Scenario", "Could not be read: " + e.Message, true);
            }

            //Every section is now read whatever the ones before it did, and what is missing is
            //reported as a checklist instead of stopping the load. success still means "complete enough
            //to run", so nothing downstream starts a simulation on a scenario with holes in it.
            _published = _collecting;
            _collecting = null;
            _duplicateKeysReported = null;
            _sectionsRead = null;
            _parsingLines = null;
            success = RequirementsMet;
            return newInput;
        }

        /// <summary>
        /// Runs one section's parser, so that whatever it throws is reported against that section and the
        /// next section is still read.
        /// </summary>
        /// <param name="alwaysCritical">Whether an unexplained failure of this section stops the run.</param>
        private static void ReadSection(string name, bool alwaysCritical, System.Func<bool> parse)
        {
            _currentSection = name;
            bool ok;
            try
            {
                ok = parse();
            }
            catch (System.Exception e)
            {
                _currentSection = name;
                Engine.Message(null, Engine.LogType.Exception, $"Reading section {name} threw: {e.Message}. The other sections were still read.");
                AddRequirement(name, "Could not be read: " + e.Message, true);
                _softDepth = 0;
                return;
            }

            if (!ok)
            {
                SectionIncomplete(name, alwaysCritical);
            }
        }

        /// <summary>
        /// Whether a module has anything in the file: its own header (<paramref name="headerIndex"/> is its line) or,
        /// without one, any of its <paramref name="subSections"/> (<paramref name="headerIndex"/> is -1, and the module
        /// reads as switched off).
        /// </summary>
        /// <remarks>
        /// A <c>[SUMO]</c> or an <c>[ELMFIRE]</c> under no module header used to be skipped, and the next save
        /// dropped it without a word. It is now read as the settings of a module that is off, and kept.
        /// </remarks>
        private static bool ModuleHeader(Dictionary<string, int> headerLineIndices, string module, out int headerIndex,
            params string[] subSections)
        {
            if (headerLineIndices.TryGetValue(module, out headerIndex))
            {
                return true;
            }

            headerIndex = -1;
            foreach (string sub in subSections)
            {
                if (headerLineIndices.ContainsKey(sub))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether <paramref name="key"/> in the section starting at <paramref name="headerIndex"/> reads as true.
        /// For deciding, before a parser runs, whether what it reports can stop the run.
        /// </summary>
        private static bool PeekBool(string[] inputLines, int headerIndex, string key)
        {
            Dictionary<string, string> values = GetHeaderInput(inputLines, headerIndex, false, false);
            return values.TryGetValue(key, out string text) && InputParse.Bool(text, out bool value) && value;
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

            //Through the locator like every other file, so a backslash path or a raster moved into a
            //subfolder is found here too - this used to be a bare Path.Combine.
            string path = ResolvePath(rootFolder, source);
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
                _currentSection = nameof(Simulation);
                AddRequirement("UTM zone",
                    $"{System.IO.Path.GetFileName(path)} is in EPSG:{header.EpsgCode}, which the simulation cannot "
                    + "measure in. Everything read from that raster will be misplaced.", true);
            }
        }

        /// <summary>
        /// The imported fire's arrival time raster, read straight from the lines rather than from the
        /// parsed input because the wildfire section has not been read yet at the point this is needed.
        /// </summary>
        /// <remarks>
        /// Only when the fire module is <c>AscImport</c>: a scenario that switched to ELMFIRE keeps its old
        /// <c>[AscImport]</c> section, and that raster says nothing about the grid the scenario is on now.
        /// </remarks>
        private static string FireDataReferenceFile(string[] inputLines, Dictionary<string, int> headerLineIndices)
        {
            if (!headerLineIndices.TryGetValue(nameof(WildfireModule), out int moduleIndex)
                || !GetHeaderInput(inputLines, moduleIndex, false, false).TryGetValue(nameof(WildfireModuleInput.Module), out string module)
                || module != nameof(WildfireModuleInput.WildfireModules.AscImport))
            {
                return string.Empty;
            }

            if (!headerLineIndices.TryGetValue("AscImport", out int lineIndex))
            {
                return string.Empty;
            }

            Dictionary<string, string> ascInput = GetHeaderInput(inputLines, lineIndex, false, false);
            return ascInput.TryGetValue("TimeOfArrivalFile", out string arrivalFile) ? arrivalFile : string.Empty;
        }

        /// <summary>
        /// Reads all input under header until next header is found.
        /// </summary>
        /// <remarks>
        /// Split on the first <c>=</c> only, so a value may contain one. A key given twice is reported once per
        /// parse and the first value is kept - which is also what the campaign tools' key editor changes, so
        /// an edited file reads the edited value. This used to be a <c>Dictionary.Add</c>, which threw and,
        /// before sections were isolated, lost the rest of the file.
        /// </remarks>
        /// <param name="collectPureDataLines">Also return lines without <c>=</c> that contain a comma (the data
        /// rows of a ramp or a response curve) under the key <c>dataLine&lt;index&gt;</c>.</param>
        public static Dictionary<string, string> GetHeaderInput(string[] inputLines, int startIndex, bool collectPureDataLines = false)
        {
            //A section a parser asked for is a section that was read (see ReportIgnored).
            if (_sectionsRead != null && ReferenceEquals(inputLines, _parsingLines))
            {
                _sectionsRead.Add(startIndex);
            }
            return GetHeaderInput(inputLines, startIndex, collectPureDataLines, true);
        }

        /// <summary>
        /// Reports, once each, what the file says that no parser read - so a save will not write it: a section the
        /// parser does not know, a known section nothing read, a key that is not one of its section's, a retired key,
        /// and a line that is not <c>Key=Value</c>. Never critical: the scenario is what the rest of the file says.
        /// </summary>
        /// <remarks>
        /// These used to be dropped without a word, so a mistyped key in a hand edit - <c>enabled=true</c>,
        /// <c>SimulationTstopHour=24</c> - simply did not happen, and the next save from the GUI deleted it. A key is
        /// known when the writer writes it (<see cref="PREACTInputWriter.FileKeys"/> of the section's type, the members
        /// every parser looks its keys up by), so the two cannot disagree about what the format holds.
        /// </remarks>
        private static void ReportIgnored(string[] inputLines, string[] asWritten, List<int> headers, HashSet<int> duplicates)
        {
            var keyCache = new Dictionary<string, HashSet<string>>();
            //section|key (or section| for a whole section) -> first line and how many times, in file order.
            var found = new Dictionary<string, int[]>();
            var order = new List<string>();
            var messages = new Dictionary<string, string[]>();

            //What is reported, and where the first one is: "[Weather] DesiredLatLon (line 16) is no longer used: ...".
            void Note(string id, int line, string section, string key, string subject, string predicate)
            {
                if (found.TryGetValue(id, out int[] seen))
                {
                    ++seen[1];
                    return;
                }
                found[id] = new[] { line, 1 };
                order.Add(id);
                messages[id] = new[] { section, key, subject, predicate };
            }

            int firstHeader = headers.Count > 0 ? headers[0] : inputLines.Length;
            for (int i = 0; i < firstHeader; ++i)
            {
                if ((inputLines[i] ?? string.Empty).Length > 0)
                {
                    Note("|line" + i, i, "Scenario", "line " + (i + 1), $"\"{Shorten(Verbatim(asWritten, i))}\"",
                        "comes before the first [section], so nothing reads it.");
                }
            }

            for (int h = 0; h < headers.Count; ++h)
            {
                int start = headers[h];
                int end = h + 1 < headers.Count ? headers[h + 1] : inputLines.Length;
                string name = inputLines[start].Trim('[', ']');

                if (duplicates.Contains(start) || RetiredSections.ContainsKey(name))
                {
                    continue; //said when the headers were read
                }

                if (!SectionTypes.TryGetValue(name, out System.Type type))
                {
                    Note(name + "|", start, name, "[" + name + "]", "[" + name + "]",
                        "is not a section PREACT reads" + Suggest(name, SectionTypes.Keys, "[{0}]"));
                    continue;
                }

                if (!_sectionsRead.Contains(start))
                {
                    Note(name + "|", start, name, "[" + name + "]", "[" + name + "]",
                        "was not read by any part of the scenario.");
                    continue;
                }

                if (!keyCache.TryGetValue(name, out HashSet<string> known))
                {
                    known = new HashSet<string>(PREACTInputWriter.FileKeys(type));
                    keyCache[name] = known;
                }

                for (int i = start + 1; i < end; ++i)
                {
                    string line = inputLines[i] ?? string.Empty;
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    int equals = line.IndexOf('=');
                    if (equals <= 0)
                    {
                        //A response curve's rows are its data.
                        if (name == "ResponseCurve" && line.IndexOf(',') > 0)
                        {
                            continue;
                        }
                        Note(name + "|line" + i, i, name, "line " + (i + 1), $"[{name}] \"{Shorten(Verbatim(asWritten, i))}\"",
                            "is not a Key=Value line.");
                        continue;
                    }

                    string key = line.Substring(0, equals);
                    if (known.Contains(key) || ConvertedKeys.Contains(name + "|" + key))
                    {
                        continue;
                    }

                    if (RetiredKeys.TryGetValue(name + "|" + key, out string reason))
                    {
                        Note(name + "|" + key, i, name, key, $"[{name}] {key}", "is no longer used: " + reason);
                    }
                    else
                    {
                        Note(name + "|" + key, i, name, key, $"[{name}] {key}", $"is not a key of [{name}]" + Suggest(key, known, "{0}"));
                    }
                }
            }

            foreach (string id in order)
            {
                int[] where = found[id];
                string[] m = messages[id];
                string times = where[1] > 1 ? $" (line {where[0] + 1}, and {where[1] - 1} more)" : $" (line {where[0] + 1})";
                string text = m[2] + times + " " + m[3] + " It is ignored, and saving the scenario does not write it.";
                Engine.Message(null, Engine.LogType.Warning, text);
                _currentSection = m[0];
                AddRequirement(m[1], text, false);
            }
        }

        /// <summary>Line <paramref name="i"/> as the file has it, without its comment and surrounding whitespace.</summary>
        private static string Verbatim(string[] asWritten, int i)
        {
            return StripComment(asWritten[i] ?? string.Empty).Trim();
        }

        private static string Shorten(string line)
        {
            return line.Length <= 40 ? line : line.Substring(0, 37) + "...";
        }

        /// <summary>
        /// " - did you mean X?" for the known name closest to <paramref name="name"/> (written with
        /// <paramref name="format"/>), or ".".
        /// </summary>
        /// <remarks>The keys are case-sensitive, so a key that differs only in case is the likeliest typo of all.</remarks>
        private static string Suggest(string name, IEnumerable<string> known, string format)
        {
            string best = null;
            int bestDistance = int.MaxValue;
            foreach (string candidate in known)
            {
                int distance = string.Equals(candidate, name, System.StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : EditDistance(name.ToLowerInvariant(), candidate.ToLowerInvariant());
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            int allowed = System.Math.Max(1, System.Math.Min(3, name.Length / 4));
            return best != null && bestDistance <= allowed
                ? " - did you mean " + string.Format(System.Globalization.CultureInfo.InvariantCulture, format, best) + "?"
                : ".";
        }

        private static int EditDistance(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; ++j) previous[j] = j;
            for (int i = 1; i <= a.Length; ++i)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; ++j)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = System.Math.Min(System.Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                int[] swap = previous;
                previous = current;
                current = swap;
            }
            return previous[b.Length];
        }

        private static Dictionary<string, string> GetHeaderInput(string[] inputLines, int startIndex, bool collectPureDataLines, bool reportDuplicates)
        {
            Dictionary<string, string> inputToParse = new Dictionary<string, string>();
            if (inputLines == null || startIndex < 0 || startIndex >= inputLines.Length)
            {
                return inputToParse;
            }

            string header = inputLines[startIndex];
            //first line is header
            int lineIndex = startIndex + 1;

            while (lineIndex < inputLines.Length)
            {
                //Normalised again: harmless for a line the header scan already normalised, and it makes this
                //correct for callers (the CLI, the ELMFIRE readers) that pass raw lines.
                string line = NormaliseLine(inputLines[lineIndex] ?? string.Empty);
                //we have found next header, exit
                if (line.StartsWith("["))
                {
                    break;
                }
                //empty or comment
                if (line.Length == 0)
                {
                    ++lineIndex;
                    continue;
                }

                int equals = line.IndexOf('=');
                if (equals > 0)
                {
                    string key = line.Substring(0, equals);
                    string value = line.Substring(equals + 1);
                    if (!inputToParse.ContainsKey(key))
                    {
                        inputToParse.Add(key, value);
                    }
                    else if (reportDuplicates)
                    {
                        ReportDuplicateKey(header, key, inputToParse[key], value, lineIndex);
                    }
                }
                //this is e.g. ramps that have no Variable=value structure
                else if (collectPureDataLines && line.IndexOf(',') > 0)
                {
                    inputToParse["dataLine" + lineIndex] = line;
                }

                ++lineIndex;
            }

            return inputToParse;
        }

        private static void ReportDuplicateKey(string header, string key, string kept, string ignored, int lineIndex)
        {
            //Once per section and key, because the same section is often read by more than one parser.
            string id = header + "|" + key + "|" + lineIndex;
            if (_duplicateKeysReported != null && !_duplicateKeysReported.Add(id))
            {
                return;
            }

            Engine.Message(null, Engine.LogType.Warning,
                $"{header} gives {key} more than once; the first value ({kept}) is used and the one on line {lineIndex + 1} ({ignored}) is ignored.");
            string previousSection = _currentSection;
            _currentSection = header.Trim('[', ']');
            AddRequirement(key, $"Given more than once; the first value ({kept}) is used, the one on line {lineIndex + 1} ({ignored}) is ignored and will not be saved.", false);
            _currentSection = previousSection;
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

            /// <summary>
            /// Not required, but it changes what the scenario does in a way its file does not say - a module switched off
            /// because its section has no <c>Enabled</c> line. A GUI shows it where it is seen, not only among the defaults.
            /// </summary>
            public bool Notice;

            public override string ToString()
            {
                return string.IsNullOrEmpty(Section) ? Key : Section + " / " + Key;
            }
        }

        //The list being built by the parse running on this thread, and the last finished one.
        [System.ThreadStatic] private static List<InputRequirement> _collecting;
        [System.ThreadStatic] private static string _currentSection;
        [System.ThreadStatic] private static int _softDepth;
        [System.ThreadStatic] private static HashSet<string> _duplicateKeysReported;
        //The header lines a parser asked for during this parse, and the lines being parsed (see ReportIgnored).
        [System.ThreadStatic] private static HashSet<int> _sectionsRead;
        [System.ThreadStatic] private static string[] _parsingLines;
        private static volatile List<InputRequirement> _published = new List<InputRequirement>();

        /// <summary>What the last read scenario still needs. Rebuilt (replaced) by every load.</summary>
        public static List<InputRequirement> Requirements { get => _published; }

        /// <summary>
        /// Re-runs the checks against a scenario as it currently stands in memory, rebuilding
        /// <see cref="Requirements"/>, and returns whether anything critical is still outstanding.
        ///
        /// Done by writing the scenario out and reading it back, because the parsers <em>are</em> the
        /// validator - every requirement on the checklist is produced by one of them while reading a key.
        /// There is no separate set of rules to run instead, and inventing one would be a second place to
        /// forget a field. The round trip also checks something worth checking on its own: that what would
        /// be saved can be loaded back.
        ///
        /// In memory, against the scenario's own folder (half the checks resolve paths relative to it). This
        /// used to write a temporary .wui into the scenario folder and delete it afterwards.
        ///
        /// The parsed copy is discarded: only the requirements it produced are wanted. The scenario the
        /// caller holds is untouched.
        /// </summary>
        public static bool Revalidate(PREACTInput input)
        {
            if (input == null)
            {
                return false;
            }

            try
            {
                LoadFromLines(PREACTInputWriter.Write(input), input.RootFolder, out bool met);
                return met;
            }
            catch (System.Exception e)
            {
                Engine.Message(null, Engine.LogType.Exception, "Could not re-check the scenario: " + e.Message);
                return false;
            }
        }

        /// <summary>True when nothing critical is outstanding, so the scenario can actually be run.</summary>
        public static bool RequirementsMet
        {
            get
            {
                foreach (InputRequirement requirement in _published)
                {
                    if (requirement.Critical) return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Makes everything reported while the returned scope is open non-critical: the part of the scenario
        /// being read belongs to something that is switched off, so it cannot stop the run. Still read and
        /// kept in full, so a save does not drop it.
        /// </summary>
        public static System.IDisposable SoftRequirements(bool soft)
        {
            return new SoftScope(soft);
        }

        private sealed class SoftScope : System.IDisposable
        {
            private bool _active;
            public SoftScope(bool soft)
            {
                _active = soft;
                if (_active) ++_softDepth;
            }
            public void Dispose()
            {
                if (_active && _softDepth > 0) --_softDepth;
                _active = false;
            }
        }

        /// <summary>Whether a requirement reported now would be critical if it asked to be.</summary>
        private static bool InSoftScope { get => _softDepth > 0; }

        /// <summary>
        /// True while a section of a switched-off module is being read (<see cref="SoftRequirements"/>), so a parser
        /// can keep quiet about what only matters when the module runs.
        /// </summary>
        public static bool ReadingSwitchedOffSection { get => InSoftScope; }

        private static void AddRequirement(string key, string message, bool critical, bool notice = false)
        {
            critical &= !InSoftScope;
            string section = _currentSection ?? string.Empty;

            //Outside any parse (a check made at run time): recorded against the last published list, which is
            //what the checklist shows, so it is not lost.
            List<InputRequirement> target = _collecting;
            bool publishing = target == null;
            if (publishing)
            {
                target = new List<InputRequirement>(_published);
            }

            //Deduplicated: several parsers report the same missing key by way of both their own check
            //and CheckIfFileExist, and a checklist that lists an item twice reads as two problems.
            bool merged = false;
            for (int i = 0; i < target.Count; ++i)
            {
                if (target[i].Section == section && target[i].Key == key)
                {
                    //Critical wins, so a hard requirement is never masked by a softer duplicate.
                    target[i].Critical |= critical;
                    target[i].Notice |= notice;
                    merged = true;
                    break;
                }
            }

            if (!merged)
            {
                target.Add(new InputRequirement
                {
                    Section = section,
                    Key = key,
                    Message = message,
                    Critical = critical,
                    Notice = notice,
                });
            }

            if (publishing)
            {
                _published = target;
            }
        }

        public static void ReadingInputMessage(string nameOfInput)
        {
            //Doubles as the section marker for anything reported while this section is being read,
            //so requirements can name where they came from without every parser passing it along.
            _currentSection = nameOfInput;
            Engine.Message(null, Engine.LogType.Log, nameOfInput + " input is being read...");
        }

        /// <summary>
        /// A key that is not in the file. Critical: the run cannot proceed without it. Otherwise
        /// <paramref name="defaultValue"/> is used and said.
        /// </summary>
        public static void InputNotFoundMessage(string nameOfInput, bool critical = false, string defaultValue = null)
        {
            if(critical && !InSoftScope)
            {
                Engine.Message(null, Engine.LogType.InputError, nameOfInput + " was not found, this value is critical for the simulation to function based on the given input parameters." + pleaseCheckInput);
                AddRequirement(nameOfInput, "Required, and not set.", true);
            }
            else if (critical)
            {
                //Would be required, but belongs to something switched off.
                Engine.Message(null, Engine.LogType.Log, nameOfInput + " is not set; it is only needed when its module is enabled.");
                AddRequirement(nameOfInput, "Not set; needed only when its module is enabled.", false);
            }
            else if (defaultValue == null)
            {
                //No default to name: the absence itself is the whole story. This used to say "defaulted to VALUE".
                Engine.Message(null, Engine.LogType.Warning, $"{nameOfInput} was not found; its default is used.");
                AddRequirement(nameOfInput, "Not set; its default is used.", false);
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, $"{nameOfInput} was not found, default value {defaultValue} has been used.");
                AddRequirement(nameOfInput, $"Not set; defaulted to {defaultValue}.", false);
            }
        }

        /// <summary>
        /// A module section with no <c>Enabled</c> line: the module is off, which is the default, and it is said as a
        /// <see cref="InputRequirement.Notice"/> - the section is there, so the file may well mean the module to run, and
        /// a save writes <c>Enabled=false</c> (e2e N5: a scenario meant to run ELMFIRE silently ran without a fire).
        /// </summary>
        /// <param name="module">What is off, as a noun phrase ("the fire module").</param>
        /// <param name="consequence">What that means for a run ("the scenario runs without a fire").</param>
        public static void ModuleOffForWantOfEnabled(string module, string consequence)
        {
            string section = _currentSection ?? string.Empty;
            string message = $"Not set, so {module} is off and {consequence}. Add Enabled=true to [{section}] to run it, or "
                             + "Enabled=false to say it is meant to be off.";
            Engine.Message(null, Engine.LogType.Warning, $"[{section}] Enabled: " + message);
            AddRequirement("Enabled", message, false, notice: true);
        }

        /// <summary>
        /// Records something the scenario could have but has not, in the reader's own words.
        ///
        /// Distinct from <see cref="InputNotFoundMessage"/>: for a key whose absence is not a value at all but a
        /// step nobody has taken yet. Never critical: the scenario runs, it just runs without this.
        /// </summary>
        public static void OptionalInputMissing(string nameOfInput, string consequence)
        {
            Engine.Message(null, Engine.LogType.Log, nameOfInput + " is not set. " + consequence);
            AddRequirement(nameOfInput, consequence, false);
        }

        /// <summary>A non-critical note on the checklist, with a warning in the log.</summary>
        public static void InputWarning(string nameOfInput, string message)
        {
            Engine.Message(null, Engine.LogType.Warning, nameOfInput + ": " + message);
            AddRequirement(nameOfInput, message, false);
        }

        /// <summary>A problem the run cannot proceed with (unless its module is off).</summary>
        public static void InputProblem(string nameOfInput, string message)
        {
            Engine.Message(null, InSoftScope ? Engine.LogType.Warning : Engine.LogType.InputError, nameOfInput + ": " + message);
            AddRequirement(nameOfInput, message, true);
        }

        public static void CriticalDependency(string missingDependency)
        {
            Engine.Message(null, Engine.LogType.InputError, $"Current module requires {missingDependency} to be set." + pleaseCheckInput);
            AddRequirement(missingDependency, "Required by this module.", true);
        }

        public static void MissingReferenceToOtherInput(string nameOfInput, string missingReference)
        {
            MissingReferenceToOtherInput(nameOfInput, missingReference, true);
        }

        public static void MissingReferenceToOtherInput(string nameOfInput, string missingReference, bool critical)
        {
            Engine.Message(null, critical && !InSoftScope ? Engine.LogType.InputError : Engine.LogType.Warning,
                nameOfInput + " reference another input (" + missingReference + ") that could not be found." + pleaseCheckInput);
            AddRequirement(nameOfInput, $"Refers to \"{missingReference}\", which does not exist.", critical);
        }

        public static void IncorrectInputCount(string nameOfInput)
        {
            Engine.Message(null, InSoftScope ? Engine.LogType.Warning : Engine.LogType.InputError, nameOfInput + " does not contain the expected number of inputs." + pleaseCheckInput);
            AddRequirement(nameOfInput, "Does not have the expected number of values.", true);
        }

        public static void CouldNotInterpretInputMessage(string nameOfInput, string userInput)
        {
            CouldNotInterpretInputMessage(nameOfInput, userInput, true);
        }

        /// <summary>
        /// A value that is there but cannot be read. Critical unless the key has a usable default, in which
        /// case the default is kept and said.
        /// </summary>
        public static void CouldNotInterpretInputMessage(string nameOfInput, string userInput, bool critical, string defaultValue = null)
        {
            if (critical)
            {
                Engine.Message(null, InSoftScope ? Engine.LogType.Warning : Engine.LogType.InputError,
                    "Could not interpret user input " + userInput + " for " + nameOfInput + ".");
                AddRequirement(nameOfInput, $"Value \"{userInput}\" could not be interpreted.", true);
            }
            else
            {
                string kept = defaultValue == null ? "its default is used" : $"{defaultValue} is used instead";
                Engine.Message(null, Engine.LogType.Warning, $"Could not interpret user input {userInput} for {nameOfInput}; {kept}.");
                AddRequirement(nameOfInput, $"Value \"{userInput}\" could not be interpreted; {kept}.", false);
            }
        }

        /// <summary>
        /// Records that a section could not be read through to the end, so the checklist says which
        /// part of the file is incomplete even when the parser stopped before naming a specific key.
        /// </summary>
        private static void SectionIncomplete(string nameOfInput, bool critical)
        {
            //Only worth saying when nothing more specific was already reported. A section that failed
            //because one named key is missing does not also need "this section is incomplete" beside
            //it - that reads as two problems where there is one.
            foreach (InputRequirement requirement in _collecting ?? _published)
            {
                if (requirement.Section == nameOfInput)
                {
                    return;
                }
            }

            _currentSection = nameOfInput;
            AddRequirement(nameOfInput, "This section could not be read completely.", critical);
        }

        /// <summary>
        /// A path as the scenario should store it: trimmed, unquoted, with forward slashes. A backslash is a
        /// separator only on Windows, and a .wui is meant to open anywhere; forward slashes work on all of them.
        /// </summary>
        public static string NormalisePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            string trimmed = path.Trim().Trim('"').Trim();
            return trimmed.Replace('\\', '/');
        }

        /// <summary>
        /// The full path of a file the scenario names, resolved the way the checklist resolves it: relative to
        /// <paramref name="rootFolder"/>, searched in the scenario's subfolders when it has moved, forward or
        /// back slashes alike. When it cannot be found the plain combination is returned, so the caller's own
        /// "not found" names the path the scenario gave.
        /// </summary>
        public static string ResolvePath(string rootFolder, string recorded)
        {
            string normalised = NormalisePath(recorded);
            if (normalised.Length == 0)
            {
                return string.Empty;
            }

            if (Utility.ScenarioFileLocator.TryResolve(rootFolder, normalised, out string resolved, out string _))
            {
                return Path.IsPathRooted(resolved) || string.IsNullOrEmpty(rootFolder)
                    ? resolved
                    : Path.Combine(rootFolder, resolved);
            }

            return Path.IsPathRooted(normalised) || string.IsNullOrEmpty(rootFolder)
                ? normalised
                : Path.Combine(rootFolder, normalised);
        }

        /// <summary>
        /// Checks a scenario file is there, looking in the scenario's own subfolders when it is not, and
        /// correcting <paramref name="inputData"/> to where it was actually found.
        ///
        /// The correction is the point of the <c>ref</c>: resolving for the sake of the checklist alone
        /// would report the scenario as complete and then load nothing, because every reader goes on to
        /// use the field, not this. Since the field is what the writer writes, a scenario saved after
        /// this has run records the corrected path and stops needing the search.
        ///
        /// The path is always normalised to forward slashes, found or not, so a Windows-written scenario
        /// resolves on every platform and is saved in the portable form. A key that is set but names a file
        /// that is not there is reported as "not found" (it used to say "Required, and not set").
        /// </summary>
        public static void CheckIfFileExist(string nameOfInput, ref string inputData, string rootFolder, out bool success, bool critical = true)
        {
            string recorded = NormalisePath(inputData);
            inputData = recorded;
            if (recorded.Length == 0)
            {
                success = false;
                InputNotFoundMessage(nameOfInput, critical);
                return;
            }

            success = Utility.ScenarioFileLocator.TryResolve(rootFolder, recorded, out string resolved, out string explanation);

            if (!success)
            {
                string message = $"The file {recorded} was not found (relative paths are read from the scenario's folder and its subfolders).";
                if (critical && !InSoftScope)
                {
                    Engine.Message(null, Engine.LogType.InputError, nameOfInput + ": " + message);
                }
                else
                {
                    Engine.Message(null, Engine.LogType.Warning, nameOfInput + ": " + message);
                }
                AddRequirement(nameOfInput, message, critical);
                return;
            }

            if (resolved == recorded)
            {
                return;
            }

            Engine.Message(null, Engine.LogType.Warning, explanation + " Save the scenario to record the new path.");
            //Non-critical, and recorded, because the scenario as it stands on disk is still wrong: this
            //load works, and the next one works only because the same search runs again.
            AddRequirement(nameOfInput, "Found at " + resolved.Replace('\\', '/')
                + " rather than " + recorded + ". Save the scenario to record it.", false);
            inputData = NormalisePath(resolved);
        }

        /// <summary>
        /// For callers with nothing to correct - a path held somewhere this cannot assign to. Still
        /// searches, so the checklist agrees with the ref version about whether the file exists.
        /// </summary>
        public static void CheckIfFileExist(string nameOfInput, string inputData, string rootFolder, out bool success, bool critical = true)
        {
            string copy = inputData;
            CheckIfFileExist(nameOfInput, ref copy, rootFolder, out success, critical);
        }
    }
}
