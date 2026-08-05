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

        public void Parse(string[] inputLines, int startIndex, SimulationInput simulationInput, WeatherInput weatherInput, LandscapeInput landscapeInput, Dictionary<string, int> headerLineIndex, List<int> ignitionPointLineIndices, string rootFolder, out bool success)
        {
            success = false;
            int issues = 0;
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            nameOfInput = nameof(Enabled);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                success = bool.TryParse(userInput, out Enabled);
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput);
            }
            if (!success)
            {
                return;
            }

            //Optional, and read before the module is: the masks are painted on the fire grid but they are
            //not the fire module's alone - k-PERIL protects the painted WUI area, and the painter needs
            //them back to carry on painting - so a scenario keeps them whether or not it spreads a fire.
            ParseGraphicalFireInputFile(inputToParse, rootFolder);

            if (!Enabled)
            {
                //Still through LoadAll, which loads the landscape for a scenario with no fire: it is
                //terrain, and there is somewhere to paint and a surface to draw the map on either way.
                _data.LoadAll(simulationInput, this, landscapeInput, rootFolder, out success);
                LoadIgnitionPoints(inputLines, ignitionPointLineIndices, simulationInput);
                return;
            }

            nameOfInput = nameof(Module);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                success = true;
                switch (userInput)
                {
                    case nameof(WildfireModules.AscImport):
                        Module = WildfireModules.AscImport;
                        break;
                    //A real state, not a typo: the module combo offers None, and a scenario saved with the
                    //fire switched on but no module chosen says exactly this. It used to fall through to
                    //"could not interpret user input None", which reads as a corrupt file.
                    case nameof(WildfireModules.None):
                        Module = WildfireModules.None;
                        success = false;
                        Engine.Message(null, Engine.LogType.InputError,
                            "The wildfire module is enabled but set to None, so there is no fire to simulate. "
                            + $"Choose {nameof(WildfireModules.ELMFIRE)} to run ELMFIRE, or "
                            + $"{nameof(WildfireModules.AscImport)} to read a fire computed elsewhere - or switch the "
                            + "wildfire module off.");
                        PREACTInput.InputNotFoundMessage(nameOfInput, true);
                        break;
                    case nameof(WildfireModules.ELMFIRE):
                        Module = WildfireModules.ELMFIRE;
                        break;
                    //Named rather than falling through to "could not interpret", which would leave the user
                    //guessing whether they had mistyped. Not silently promoted to ELMFIRE either: that model
                    //produced different results, and a scenario should not change what it does on load.
                    case RemovedCellModelName:
                    case "CellSpread":
                        success = false;
                        Engine.Message(null, Engine.LogType.InputError,
                            $"Module={userInput} is the cell-based spread model, which has been removed - only "
                            + "LookupROS was ever implemented in it. Use ELMFIRE to run ELMFIRE for this scenario, "
                            + "or AscImport to read a fire computed elsewhere.");
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                        break;
                    default:
                        success = false;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                        break;
                }
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput, true);
                success = false;
            }
            if (!success)
            {
                return;
            }

            //check if weather exists, may be critical
            /*if (weatherInput.WeatherFile == string.Empty && (Module != WildfireModules.AscImport || Module != WildfireModules.None))
            {
                success = false;
                PREACTInput.CriticalDependency(nameof(weatherInput.WeatherFile));
                return;
            }*/         

            //now check modules that have been selected
            //
            //A module named with no section to configure it is critical and says so, and the data is loaded
            //anyway. It used to be a non-critical warning followed by a bare return, which skipped LoadAll
            //entirely - so the scenario loaded "successfully" with no landscape, no fuel models and no
            //painted masks, and the run then handed a null landscape to a module that spreads fire across
            //its cells. Selecting a module in the GUI is exactly how a scenario reaches this state.
            if (Module == WildfireModules.AscImport)
            {
                nameOfInput = nameof(WildfireModules.AscImport);
                PREACTInput.ReadingInputMessage(nameOfInput);
                if (headerLineIndex.TryGetValue(nameOfInput, out int lineindex))
                {
                    _ascImportInput = AscImportInput.Parse(inputLines, lineindex, rootFolder, out success);
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, true);
                    success = false;
                }
            }
            else if (Module == WildfireModules.ELMFIRE)
            {
                nameOfInput = nameof(WildfireModules.ELMFIRE);
                PREACTInput.ReadingInputMessage(nameOfInput);
                //Not critical when absent, unlike the others: every ELMFIRE setting has a workable default -
                //the case folder is "elmfire", the executable is the vendored one - so a scenario that only
                //says "run ELMFIRE" is a complete instruction. What has to exist is checked when the module
                //is created, where the paths are resolved and the reason can name a file.
                if (headerLineIndex.TryGetValue(nameOfInput, out int lineindex))
                {
                    _elmfireInput = ElmfireInput.Parse(inputLines, lineindex, headerLineIndex, rootFolder, out success);
                }
                else
                {
                    success = true;
                }
            }
            bool moduleSectionRead = success;

            _data.LoadAll(simulationInput, this, landscapeInput, rootFolder, out bool dataLoaded);
            //Both have to hold. LoadAll succeeding says the landscape and fuels are there; it says nothing
            //about the module's own section, which is what configures how the fire spreads through them.
            success = dataLoaded && moduleSectionRead;

            //After LoadAll, which is where the legacy IgnitionPointsFile CSV is read: the scenario's own
            //[IgnitionPoint] sections are the supported form and replace what that read, rather than
            //being appended to it, so which of the two is in effect is never in question.
            LoadIgnitionPoints(inputLines, ignitionPointLineIndices, simulationInput);
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
                    + "ignition. Paint them under Run/edit > Hazards > Paint fire areas and save.");
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