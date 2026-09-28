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
    public class WildfireModuleInput
    {
        /// <summary>
        /// <c>ELMFIRE</c> runs ELMFIRE itself and reads the fire back. <c>AscImport</c> reads a fire computed
        /// anywhere else - ELMFIRE run by hand, FARSITE, FlamMap, anything that writes the four rasters.
        /// </summary>
        public enum WildfireModules { None, AscImport, ELMFIRE }

        /// <summary>
        /// The cell-based spread model this platform used to carry, removed in favour of running ELMFIRE.
        /// Only ever matched when reading, so a scenario naming it says so plainly instead of failing on an
        /// unrecognised value.
        /// </summary>
        public const string RemovedCellModelName = "ElmClone";

        private WildfireData _data;
        private AscImportInput _ascImportInput;
        private ElmfireInput _elmfireInput;

        public bool Enabled = false;
        public WildfireData Data { get => _data; }
        public AscImportInput AscImportInput { get => _ascImportInput; }

        /// <summary>
        /// Settings for running ELMFIRE. Named to match the module, which is how the writer finds the
        /// section to emit for it.
        /// </summary>
        public ElmfireInput ElmfireInput { get => _elmfireInput; }
        public WildfireModules Module = WildfireModules.None;

        /// <summary>
        /// The masks painted on the fire grid - WUI area, random ignition area, initial ignition - as
        /// written by the fire paint window. Optional: a scenario with nothing painted simply has none.
        /// </summary>
        public string GraphicalFireInputFile = string.Empty;


        public WildfireModuleInput() 
        {
            _data = new WildfireData();
            _ascImportInput = new AscImportInput();
            _elmfireInput = new ElmfireInput();
        }

        public void Parse(string[] inputLines, int startIndex, SimulationInput simulationInput, WeatherInput weatherInput, Dictionary<string, int> headerLineIndex, string rootFolder, out bool success)
        {
            Parse(inputLines, startIndex, simulationInput, weatherInput, null, headerLineIndex, null, rootFolder, out success);
        }

        public void Parse(string[] inputLines, int startIndex, SimulationInput simulationInput, WeatherInput weatherInput, LandscapeInput landscapeInput, Dictionary<string, int> headerLineIndex, string rootFolder, out bool success)
        {
            Parse(inputLines, startIndex, simulationInput, weatherInput, landscapeInput, headerLineIndex, null, rootFolder, out success);
        }

        /// <summary>
        /// Reads <c>[WildfireModule]</c> and everything that belongs to it: the painted areas, the module's own
        /// section, <c>[ELMFIRE]</c>, the <c>[IgnitionPoint]</c>s and the landscape.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="startIndex"/> is the header's line, or -1 for a scenario that has fire sections but no
        /// <c>[WildfireModule]</c>: the fire is then off, and the sections are still read so a save keeps them.
        /// </para>
        /// <para>
        /// Read in full whether or not the fire is on, like the other modules: <c>Enabled</c> missing or unreadable
        /// means off, with a note, and nothing read while the fire is off can stop the run. This used to return at
        /// once when <c>Enabled</c> was missing, and to skip <c>Module</c> and every sub-section when it was false,
        /// so the ignition points, the painted areas' reference and the <c>[ELMFIRE]</c> settings of such a file
        /// were lost, and the next save deleted them.
        /// </para>
        /// <para>
        /// <c>[ELMFIRE]</c> (with <c>[ElmfireNamelist]</c>) is read whenever the file has it, whichever module is
        /// selected: it also describes the case the Data menu, <c>PREACTcli build-case</c> and a campaign build,
        /// and those read it from here. <c>[AscImport]</c> is read whenever the file has it too. Only the selected
        /// module's section can be critical, and only while the fire is on.
        /// </para>
        /// </remarks>
        public void Parse(string[] inputLines, int startIndex, SimulationInput simulationInput, WeatherInput weatherInput, LandscapeInput landscapeInput, Dictionary<string, int> headerLineIndex, List<int> ignitionPointLineIndices, string rootFolder, out bool success)
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
                PREACTInput.ModuleOffForWantOfEnabled("the fire module", "the scenario runs without a fire");
            }

            bool moduleSectionRead = true;
            using (PREACTInput.SoftRequirements(!Enabled))
            {
                //Optional: the masks are painted on the fire grid but they are not the fire module's alone - k-PERIL
                //protects the painted WUI area, and the painter needs them back to carry on painting - so a scenario
                //keeps them whether or not it spreads a fire.
                ParseGraphicalFireInputFile(inputToParse, rootFolder);

                nameOfInput = nameof(Module);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    switch (userInput)
                    {
                        case nameof(WildfireModules.AscImport):
                            Module = WildfireModules.AscImport;
                            break;
                        case nameof(WildfireModules.ELMFIRE):
                            Module = WildfireModules.ELMFIRE;
                            break;
                        //A real state, not a typo: the module combo offers None, and a scenario saved with the fire
                        //switched on but no module chosen says exactly this. It used to fall through to "could not
                        //interpret user input None", which reads as a corrupt file. With the fire off it is simply
                        //the scenario's choice.
                        case nameof(WildfireModules.None):
                            Module = WildfireModules.None;
                            if (Enabled)
                            {
                                moduleSectionRead = false;
                                Engine.Message(null, Engine.LogType.InputError,
                                    "The wildfire module is enabled but set to None, so there is no fire to simulate. "
                                    + $"Choose {nameof(WildfireModules.ELMFIRE)} to run ELMFIRE, or "
                                    + $"{nameof(WildfireModules.AscImport)} to read a fire computed elsewhere - or switch the "
                                    + "wildfire module off.");
                                PREACTInput.InputNotFoundMessage(nameOfInput, true);
                            }
                            break;
                        //Named rather than falling through to "could not interpret", which would leave the user
                        //guessing whether they had mistyped. Not silently promoted to ELMFIRE either: that model
                        //produced different results, and a scenario should not change what it does on load.
                        case RemovedCellModelName:
                        case "CellSpread":
                            moduleSectionRead = !Enabled;
                            Engine.Message(null, Enabled ? Engine.LogType.InputError : Engine.LogType.Warning,
                                $"Module={userInput} is the cell-based spread model, which has been removed - only "
                                + "LookupROS was ever implemented in it. Use ELMFIRE to run ELMFIRE for this scenario, "
                                + "or AscImport to read a fire computed elsewhere.");
                            PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                            break;
                        default:
                            moduleSectionRead = !Enabled;
                            PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                            break;
                    }
                }
                else if (Enabled)
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                    moduleSectionRead = false;
                }

                //A module named with no section to configure it is critical and says so, and the data is loaded
                //anyway. It used to be a non-critical warning followed by a bare return, which skipped LoadAll
                //entirely - so the scenario loaded "successfully" with no landscape, no fuel models and no painted
                //masks. Selecting a module in the GUI is exactly how a scenario reaches this state.
                nameOfInput = nameof(WildfireModules.AscImport);
                if (headerLineIndex.TryGetValue(nameOfInput, out int ascIndex))
                {
                    using (PREACTInput.SoftRequirements(Module != WildfireModules.AscImport))
                    {
                        PREACTInput.ReadingInputMessage(nameOfInput);
                        _ascImportInput = AscImportInput.Parse(inputLines, ascIndex, rootFolder, out bool ok);
                        moduleSectionRead &= ok || !Enabled || Module != WildfireModules.AscImport;
                    }
                }
                else if (Module == WildfireModules.AscImport)
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                    moduleSectionRead &= !Enabled;
                }

                //Not critical when absent, unlike the others: every ELMFIRE setting has a workable default - the
                //case folder is "elmfire", the executable is the vendored one - so a scenario that only says "run
                //ELMFIRE" is a complete instruction. What has to exist is checked when the module is created, where
                //the paths are resolved and the reason can name a file.
                nameOfInput = nameof(WildfireModules.ELMFIRE);
                if (headerLineIndex.TryGetValue(nameOfInput, out int elmfireIndex))
                {
                    using (PREACTInput.SoftRequirements(Module != WildfireModules.ELMFIRE))
                    {
                        PREACTInput.ReadingInputMessage(nameOfInput);
                        _elmfireInput = ElmfireInput.Parse(inputLines, elmfireIndex, headerLineIndex, rootFolder, out bool ok);
                        moduleSectionRead &= ok || !Enabled || Module != WildfireModules.ELMFIRE;
                    }
                }
                else if (headerLineIndex.TryGetValue(ElmfireInput.NamelistSection, out int namelistIndex))
                {
                    //The namelist settings without [ELMFIRE]: still ELMFIRE's, and a save writes both.
                    using (PREACTInput.SoftRequirements(Module != WildfireModules.ELMFIRE))
                    {
                        PREACTInput.ReadingInputMessage(ElmfireInput.NamelistSection);
                        _elmfireInput.Namelist = ElmfireNamelistInput.Parse(inputLines, namelistIndex);
                    }
                }

                //Before the landscape and the painted masks, which read files: the ignition points are the scenario's
                //own text, and nothing that goes wrong with a raster may cost them. They were read last, so a missing
                //landscape file (whose exception ended this section) dropped them, and the next save deleted them.
                LoadIgnitionPoints(inputLines, ignitionPointLineIndices, simulationInput);

                bool dataLoaded;
                try
                {
                    //Through LoadAll whatever the module, which loads the landscape for a scenario with no fire too:
                    //it is terrain, and there is somewhere to paint and a surface to draw the map on either way.
                    _data.LoadAll(simulationInput, this, landscapeInput, rootFolder, out dataLoaded);
                }
                catch (System.Exception e)
                {
                    PREACTInput.InputWarning(nameof(WildfireData), "the fire data could not be loaded (" + e.Message
                        + "); the section's own settings and ignition points were still read.");
                    dataLoaded = false;
                }

                //Both have to hold. LoadAll succeeding says the landscape and fuels are there; it says nothing
                //about the module's own section, which is what configures how the fire spreads through them.
                success = (dataLoaded || !Enabled) && moduleSectionRead;
            }
        }

        private void ParseGraphicalFireInputFile(Dictionary<string, string> inputToParse, string rootFolder)
        {
            string nameOfInput = nameof(GraphicalFireInputFile);
            if (!inputToParse.TryGetValue(nameOfInput, out string userInput) || string.IsNullOrEmpty(userInput))
            {
                //Not a defect: most scenarios have nothing painted. Recorded on the checklist all the
                //same, without being critical, because "nothing is painted" is invisible otherwise - the
                //case builder then falls back to an ignite-anywhere mask and k-PERIL to no WUI area at
                //all, both of which look like deliberate choices in the output.
                PREACTInput.OptionalInputMissing(nameOfInput,
                    "Nothing has been painted for this scenario - no WUI area, no ignition area, no initial "
                    + "ignition. Paint them with Fire > Fire areas (workflow step 6) and save.");
                return;
            }

            GraphicalFireInputFile = userInput;

            //Kept even when the file is not there, unlike every other path in these parsers, which clear
            //the name. Clearing it is silent data loss here: the writer omits an empty value, so opening a
            //scenario whose masks were moved and then saving it would drop the reference for good - and
            //the masks themselves are not recoverable, they were painted by hand. A name pointing at
            //nothing costs one warning when the masks are loaded.
            PREACTInput.CheckIfFileExist(nameOfInput, ref GraphicalFireInputFile, rootFolder, out bool exists, false);
            if (!exists)
            {
                Engine.Message(null, Engine.LogType.Warning,
                    "The scenario's painted fire areas (" + userInput + ") are not where it says they are. The "
                    + "reference is kept so it is not lost on the next save; move the file back, or paint and "
                    + "save the areas again.");
            }
        }

        /// <summary>
        /// Reads the scenario's <c>[IgnitionPoint]</c> sections, each one point.
        ///
        /// Kept on the module rather than in the ELMFIRE sub-section because an ignition point is not
        /// that module's property: ELMFIRE needs the same point for <c>X_IGN</c>/<c>Y_IGN</c>, and it is
        /// a different module again. The legacy path - a CSV named by <c>[ELMFIRE] IgnitionPointsFile</c>
        /// - only ever loaded for ELMFIRE, which is why an ignition could not be given to anything else.
        /// </summary>
        private void LoadIgnitionPoints(string[] inputLines, List<int> ignitionPointLineIndices, SimulationInput simulationInput)
        {
            if (ignitionPointLineIndices == null || ignitionPointLineIndices.Count == 0)
            {
                return;
            }

            var points = new List<Wildfire.IgnitionPointInput>();
            for (int i = 0; i < ignitionPointLineIndices.Count; ++i)
            {
                Wildfire.IgnitionPointInput point = Wildfire.IgnitionPointInput.Parse(
                    inputLines, ignitionPointLineIndices[i], simulationInput, out bool parsed);
                if (parsed)
                {
                    points.Add(point);
                }
            }

            if (points.Count == 0)
            {
                return;
            }

            _data.IgnitionPoints.Clear();
            _data.IgnitionPoints.AddRange(points);
            Engine.Message(null, Engine.LogType.Log, $"Read {points.Count} ignition point(s) from the scenario.");
        }
    }
}  