using ImGuiNET;
using PREACT;
using PREACT.Input;
using PREACT.Math;
using SimpleFileBrowser;
using System.IO;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class NewScenarioWindow
    {
        private static bool _isOpen;

        private static Vector2d _latLon;
        private static PREACTInput _input;
        private static bool _folderSet;
        private static bool _havePopulation, _haveSumo, _haveWildfireLandscape, _haveWeather;
        private static bool _wantPedestrian, _wantTraffic, _wantWildfire, _wantSmoke;

        //The data-preparation steps, their progress reporting and the names of the files they produce
        //all live in ScenarioDataSteps, because they are equally needed for a scenario loaded from
        //disk and this window cannot serve that case: opening it clears the loaded scenario.
        //The reprojected raster, since that is the one the population step reads: a scenario holding only
        //the WGS84 clip is not ready for step 4, and marking the download done would say it was.
        private static string WorldPopFile => ScenarioDataSteps.WorldPopUtmFile;
        private static string OsmFile => ScenarioDataSteps.OsmFile;
        private static string RouterDbFile => ScenarioDataSteps.RouterDbFile;
        private static string PopulationFile => ScenarioDataSteps.PopulationFile;
        private static string WeatherFile => ScenarioDataSteps.WeatherFile;
        private static string SumoConfigFile => ScenarioDataSteps.SumoConfigFile;

        //Which spread module the wildfire hazard uses. This has to be chosen explicitly: the field
        //defaults to None, and a scenario that enables the module while leaving it None fails to
        //load outright with "Could not interpret user input None for Module". AscImport is the
        //default here because it is what the ELMFIRE-driven trigger pipeline feeds - precomputed
        //arrival time rasters - and what the working Mati case uses.
        private static readonly string[] WildfireModulesStrings = System.Enum.GetNames(typeof(WildfireModuleInput.WildfireModules));
        private static int _wildfireModuleIndex = (int)WildfireModuleInput.WildfireModules.AscImport;

        //Trigger buffer. The rate of spread deliberately defaults to coming from the fire module
        //rather than from Behave, because that is what the ELMFIRE-driven trigger campaign does,
        //and because computing it with Behave makes an initial fuel moisture file mandatory - the
        //field default of true would otherwise produce a scenario that cannot be loaded back.
        private static bool _wantTriggerBuffer;
        private static bool _calculateRosFromBehave;

        //Wind fields for k-PERIL, as written by the weather pipeline's WindNinja step. Both are
        //required, and both come from the same place, so one button sets the pair from a folder
        //rather than making the user find two files with fixed names.
        private const string WindSpeedRaster = "ws.tif";
        private const string WindDirectionRaster = "wd.tif";

        //Amber, for inputs that are required but not yet given. Matches how StepButton colours its
        //own marker rather than introducing a theme dependency.
        private static Vector4 WarningColor => new Vector4(0.9f, 0.7f, 0.2f, 1f);

        public static void Open(bool resetInput)
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
            if(resetInput)
            {
                _folderSet = false;
                _latLon = Vector2d.zero;
            }
            ScenarioEditorWindow.ClearInput();
            PreactGUI.WUInity.ShowWebMercatorMap();
        }
        public static void Close()
        {
            if (_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
            _isOpen = false;            
        }

        public static void Draw()
        {
            if(!_isOpen)
            {
                return;
            }

            ScenarioDataSteps.FlushStepLog();

            ImGui.Begin("New scenario creator", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            if(!_folderSet)
            {
                if (ImGui.Button("Set root folder"))
                {
                    OpenSetRootFolder();
                }
                //Begin must always be matched by End, even on the early-out path. Returning here
                //without it left ImGui's window stack unbalanced for the rest of the frame.
                ImGui.End();
                return;
            }
            ImGui.Text($"{nameof(_input.RootFolder)}: {_input.RootFolder}");

            //Re-pointed every frame rather than once, since this window and the one for an already
            //loaded scenario share the steps and either may have run last.
            ScenarioDataSteps.Input = _input;

            //The SUMO step sets the configuration on the scenario, so the choice above it follows.
            if (ScenarioDataSteps.SumoNetworkBuilt)
            {
                _haveSumo = true;
                ScenarioDataSteps.SumoNetworkBuilt = false;
            }

            SimulationInput simIn = _input.Simulation;

            ImGui.SeparatorText("Basic scenario data");
            ImGui.InputText(nameof(simIn.Name), ref simIn.Name, 128);

            ImGui.SeparatorText("Area of interest (AIO)");
            if (ImGui.Button("Set AIO on map"))
            {
                Close();
                PreactGUI.WUInity.PickBoundingBoxOnMap(SetAIO);
            }
            //There is no separate confirm step - the second click commits the box and reopens this
            //window with the fields filled in. Saying so avoids hunting for a save button.
            ImGui.TextWrapped("Click two opposite corners on the map. The second click applies the box and fills in the fields below; there is nothing further to confirm.");
            if(CustomTypes.InputDouble2(nameof(simIn.LowerLeftLatLon), ref _latLon))
            {
                simIn.LowerLeftLatLon = _latLon;
            }
            CustomTypes.InputDouble2(nameof(simIn.DomainSize), ref simIn.DomainSize);

            CustomTypes.InputDateTimePopup(nameof(simIn.StartDateTime), ref simIn.StartDateTime);
            CustomTypes.InputDateTimePopup(nameof(simIn.EndDateTime), ref simIn.EndDateTime);

            ImGui.SeparatorText("Evacuation");
            ImGui.Checkbox("Pedestrian evacuation?", ref _wantPedestrian);
            if(_wantPedestrian)
            {
                ImGui.Checkbox("Have population?", ref _havePopulation);
                if(_havePopulation)
                {
                    if (ImGui.Button("Select population file"))
                    {
                        FileBrowser.OpenSetFilePath(path => _input.Population.PopulationFile = path, "Select population file", true);
                    }
                    ImGui.Text($"{nameof(_input.Population.PopulationFile)}: {_input.Population.PopulationFile}");
                }
                else
                {
                    ImGui.InputInt("Min household size", ref ScenarioDataSteps.MinHouseholdSize);
                    ImGui.InputInt("Max household size", ref ScenarioDataSteps.MaxHouseholdSize);

                    ImGui.BeginDisabled(ScenarioDataSteps.Busy);
                    if (ScenarioDataSteps.StepButton("Step 1: Download WorldPop", WorldPopFile)) { ScenarioDataSteps.DownloadWorldPop(); }
                    if (ScenarioDataSteps.StepButton("Step 2: Download OSM data", OsmFile)) { ScenarioDataSteps.DownloadOsm(); }
                    if (ScenarioDataSteps.StepButton("Step 3: Build RouterDb", RouterDbFile)) { ScenarioDataSteps.BuildRouterDb(); }
                    if (ScenarioDataSteps.StepButton("Step 4: Generate population", PopulationFile)) { ScenarioDataSteps.GeneratePopulation(); }
                    ImGui.EndDisabled();
                }
            }

            ImGui.Separator();

            ImGui.Checkbox("Vehicle evacuation?", ref _wantTraffic);
            if (_wantTraffic)
            {
                ImGui.Checkbox("Have SUMO input?", ref _haveSumo);
                if (_haveSumo)
                {
                    //Named for what it has to be. "SUMO input file" reads as "the file SUMO takes as input",
                    //which is how the OSM extract ends up here - and it is accepted silently, leaving a
                    //scenario that cannot draw or route on its network.
                    if (ImGui.Button("Set SUMO configuration file (.sumocfg)"))
                    {
                        FileBrowser.OpenSetFilePath(path =>
                        {
                            if (!EvacuationTabs.LooksLikeSumoConfiguration(path))
                            {
                                Engine.Message(null, Engine.LogType.Warning,
                                    System.IO.Path.GetFileName(path) + " is not a SUMO configuration. It should be a "
                                    + ".sumocfg or a .net.xml. To make one from an OSM extract, untick \"Have SUMO "
                                    + "input?\" and use the build step instead.");
                                return;
                            }
                            _input.TrafficModule.SumoInput.ConfigurationFile = path;
                        }, "Select SUMO configuration file (.sumocfg)", true);
                    }
                    ImGui.SameLine();
                    ImGui.TextDisabled(string.IsNullOrEmpty(_input.TrafficModule.SumoInput.ConfigurationFile)
                        ? "none set" : _input.TrafficModule.SumoInput.ConfigurationFile);
                }
                else
                {
                    ImGui.BeginDisabled(ScenarioDataSteps.Busy);
                    if (ScenarioDataSteps.StepButton("Step 1: Download OSM data", OsmFile)) { ScenarioDataSteps.DownloadOsm(); }
                    if (ScenarioDataSteps.StepButton("Step 2: Build SUMO network", SumoConfigFile)) { ScenarioDataSteps.BuildSumoNetwork(); }
                    ImGui.EndDisabled();
                    ImGui.TextWrapped("Step 2 runs SUMO's own netconvert on the downloaded OSM and writes the configuration, which is then set on the scenario. It needs SUMO installed.");
                }
            }

            //Terrain, on its own and before the hazards, because it is not a hazard: the elevation gives
            //the scenario a cell grid to paint on, a slope for k-PERIL to correct spread by, and the UTM
            //zone to measure in - none of which need a fire.
            ImGui.SeparatorText("Terrain");
            if (ImGui.Button("Select elevation raster"))
            {
                FileBrowser.OpenSetFilePath(path => _input.Landscape.ElevationFile = path,
                    "Select elevation raster (DEM)", true, FileBrowser.geoTiffFilter);
            }
            ImGui.SameLine();
            ImGui.Text($"{nameof(_input.Landscape.ElevationFile)}: {_input.Landscape.ElevationFile}");

            ScenarioDataWindow.DrawOpenTopographyKey();
            ImGui.BeginDisabled(ScenarioDataSteps.Busy);
            if (ScenarioDataSteps.StepButton("Or download a DEM", ScenarioDataSteps.DemFile)) { ScenarioDataSteps.DownloadDem(); }
            ImGui.EndDisabled();
            ImGui.TextWrapped("A DEM can be had for anywhere on Earth, and slope and aspect are computed from it, "
                + "so this alone is enough terrain to paint on and to give k-PERIL a slope. The download is "
                + "reprojected into the simulation's UTM zone.");

            ImGui.SeparatorText("Hazards");
            ImGui.Checkbox("Wildfire spread?", ref _wantWildfire);
            if (_wantWildfire)
            {
                ImGui.Combo("Spread module", ref _wildfireModuleIndex, WildfireModulesStrings, WildfireModulesStrings.Length);
                if (_wildfireModuleIndex == (int)WildfireModuleInput.WildfireModules.None)
                {
                    ImGui.TextColored(WarningColor, "Pick a module: an enabled wildfire hazard set to None cannot be loaded back.");
                }

                ImGui.Checkbox("Have wildfire landscape?", ref _haveWildfireLandscape);
                if(_haveWildfireLandscape)
                {
                    if (ImGui.Button("Set landscape file"))
                    {
                        FileBrowser.OpenSetFilePath(path => _input.WildfireModule.FireCellInput.LandscapeFile = path, "Select landscape (.lcp) file", true);
                    }
                    ImGui.Text($"LandscapeFile: {_input.WildfireModule.FireCellInput.LandscapeFile}");
                }
                else
                {
                    ImGui.Checkbox("Anderson 13 fuel models (otherwise Scott & Burgan 40)", ref ScenarioDataSteps.UseAnderson13);
                    ImGui.BeginDisabled(ScenarioDataSteps.Busy);
                    if (ImGui.Button("Step 1: Download Landfire data")) { ScenarioDataSteps.DownloadLandfire(); }
                    ImGui.EndDisabled();
                    //LANDFIRE is US-only; outside it the request simply returns nothing useful.
                    ImGui.TextWrapped("LANDFIRE covers the United States only. Elsewhere, supply the landscape file yourself.");
                }

                ImGui.Checkbox("Have weather?", ref _haveWeather);
                if (_haveWeather)
                {
                    if (ImGui.Button("Select weather file")) { FileBrowser.OpenSetFilePath(path => _input.Weather.WeatherFile = path, "Select weather CSV file", true); }
                }
                else
                {
                    ImGui.BeginDisabled(ScenarioDataSteps.Busy);
                    if (ScenarioDataSteps.StepButton("Step 1: Download weather file", WeatherFile)) { ScenarioDataSteps.DownloadWeather(); }
                    ImGui.EndDisabled();
                }
            }

            if (!string.IsNullOrEmpty(ScenarioDataSteps.Status))
            {
                ImGui.SeparatorText("Data preparation");
                ScenarioDataSteps.DrawStatus();
            }

            ImGui.Separator();

            ImGui.Checkbox("Smoke spread?", ref _wantSmoke);
            if (_wantSmoke)
            {
                if(!_wantWildfire)
                {
                    ImGui.Text("Smoke dispersion needs wildfire spread active as source term.");
                }
            }

            ImGui.SeparatorText("Trigger buffer");
            ImGui.Checkbox("Trigger boundary (k-PERIL)?", ref _wantTriggerBuffer);
            if (_wantTriggerBuffer)
            {
                //The property is read-only but hands back the instance, so its fields are set here
                //and the section is written out once Module is set to kPERIL in GenerateScenario.
                kPERILInput peril = _input.TriggerBufferModule.kPERILInput;

                //Picked individually rather than derived from one folder: a case can easily hold
                //several candidates for each (a raw WindNinja output and a warped copy beside it),
                //so guessing by filename picks the wrong one silently.
                if (ImGui.Button($"Select wind speed raster ({WindSpeedRaster})"))
                {
                    FileBrowser.OpenSetFilePath(path => peril.WindSpeedFile = path, "Select wind speed raster", true, FileBrowser.geoTiffFilter);
                }
                ImGui.Text($"{nameof(peril.WindSpeedFile)}: {peril.WindSpeedFile}");

                if (ImGui.Button($"Select wind direction raster ({WindDirectionRaster})"))
                {
                    FileBrowser.OpenSetFilePath(path => peril.WindDirectionFile = path, "Select wind direction raster", true, FileBrowser.geoTiffFilter);
                }
                ImGui.Text($"{nameof(peril.WindDirectionFile)}: {peril.WindDirectionFile}");

                ImGui.TextWrapped($"Wind speed in MILES PER HOUR and direction in degrees, on the fire grid - what the weather pipeline's WindNinja step writes as {WindSpeedRaster} / {WindDirectionRaster}. The unit matters: k-PERIL reads the speed into Anderson's length-to-breadth correlation, which is defined for mi/h.");
                if (string.IsNullOrEmpty(peril.WindSpeedFile) || string.IsNullOrEmpty(peril.WindDirectionFile))
                {
                    ImGui.TextColored(WarningColor, "Required: k-PERIL needs a wind field to derive how elongated fire spread is.");
                }

                ImGui.Checkbox("Compute rate of spread with Behave", ref _calculateRosFromBehave);
                if (_calculateRosFromBehave)
                {
                    if (ImGui.Button("Select initial fuel moisture file"))
                    {
                        FileBrowser.OpenSetFilePath(path => peril.InitialFuelMoistureFile = path, "Select initial fuel moisture file", true);
                    }
                    ImGui.Text($"{nameof(peril.InitialFuelMoistureFile)}: {peril.InitialFuelMoistureFile}");
                    //Required in this mode, and a missing one fails the load rather than degrading.
                    if (string.IsNullOrEmpty(peril.InitialFuelMoistureFile))
                    {
                        ImGui.TextColored(WarningColor, "Required when the rate of spread is computed with Behave.");
                    }
                }
                else
                {
                    ImGui.TextWrapped("Rate of spread comes from the fire module's own output, which is what the ELMFIRE-driven trigger campaign uses.");
                }

                if (ImGui.Button("Select WUI area mask"))
                {
                    FileBrowser.OpenSetFilePath(path => peril.WuiAreaFile = path, "Select WUI area mask", true);
                }
                ImGui.Text($"{nameof(peril.WuiAreaFile)}: {peril.WuiAreaFile}");
                ImGui.TextWrapped("The area being protected, one per cell inside it. This is what the painted WUI selection exports.");
                if (string.IsNullOrEmpty(peril.WuiAreaFile))
                {
                    ImGui.TextColored(WarningColor, "Required: without it there is no area to compute a trigger boundary around.");
                }

                if (!_wantWildfire)
                {
                    ImGui.TextColored(WarningColor, "Needs wildfire spread active to have an arrival time to work back from.");
                }
            }

            ImGui.SeparatorText("Finished?");

            if (ImGui.Button("Generate scenario")) { GenerateScenario(); }

            ImGui.End();

            //Drawn after the main window is closed off, so it is a sibling window rather than
            //nested inside the creator. It is still tied to the creator's lifetime - Draw returns
            //early once that closes - which is why a running step keeps the creator open.
            ScenarioDataSteps.DrawProgressWindow();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        /// <summary>
        /// Domain size in metres between two already-sorted corners.
        ///
        /// UTM is used when both corners fall in the same zone, because it is the more accurate
        /// measure and matches how the rest of the pipeline treats the domain. It cannot be used
        /// across a zone boundary: <c>convertLatLngToUtm</c> picks the zone per point, so eastings
        /// either side of a boundary are measured from different origins and subtracting them is
        /// meaningless - it can even come out negative for a perfectly valid box. Mati sits right
        /// on the 24 deg E zone 34/35 boundary, so this is a real case rather than a hypothetical.
        ///
        /// Straddling boxes therefore fall back to the flat-earth conversion the population tools
        /// already use to interpret DomainSize, which has no concept of zones. Both paths return
        /// magnitudes, so the result is positive whichever corners were clicked first.
        /// </summary>
        private static Vector2d MeasureDomain(Vector2d lowerLeft, Vector2d upperRight)
        {
            var lower = PREACT.Utility.LatLngUTMConverter.WGS84.convertLatLngToUtm(lowerLeft.x, lowerLeft.y);
            var upper = PREACT.Utility.LatLngUTMConverter.WGS84.convertLatLngToUtm(upperRight.x, upperRight.y);

            if (lower.ZoneNumber == upper.ZoneNumber)
            {
                return new Vector2d(
                    Mathd.Abs(upper.Easting - lower.Easting),
                    Mathd.Abs(upper.Northing - lower.Northing));
            }

            Vector2d degrees = new Vector2d(
                Mathd.Abs(upperRight.y - lowerLeft.y),   //longitude span
                Mathd.Abs(upperRight.x - lowerLeft.x));  //latitude span

            Vector2d size = PREACT.Population.LocalGPWData.DegreesToSize(lowerLeft, degrees);
            Engine.Message(null, Engine.LogType.Log,
                $"Area of interest crosses UTM zones {lower.ZoneNumber} and {upper.ZoneNumber}; " +
                "measuring the domain geographically instead.");
            return size;
        }

        private static void SetAIO(Vector2d[] latLons)
        {
            //Corner order is whatever the user clicked, so the box is normalised here - clicking
            //top-right then bottom-left is just as natural as the other way round.
            _latLon = new Vector2d(Mathd.Min(latLons[0].x, latLons[1].x), Mathd.Min(latLons[0].y, latLons[1].y));
            _input.Simulation.LowerLeftLatLon = _latLon;
            Vector2d _upperRightLatLon = new Vector2d(Mathd.Max(latLons[0].x, latLons[1].x), Mathd.Max(latLons[0].y, latLons[1].y));

            _input.Simulation.DomainSize = MeasureDomain(_latLon, _upperRightLatLon);
            Engine.Message(null, Engine.LogType.Log,
                $"Area of interest set: lower-left {_latLon.x:F5}, {_latLon.y:F5}, domain " +
                $"{_input.Simulation.DomainSize.x:F0} x {_input.Simulation.DomainSize.y:F0} m.");
            Open(false);
        }

        private static void GenerateScenario()
        {
            if(_input.Simulation.Name == string.Empty)
            {
                Engine.Message(null, Engine.LogType.InputError, $"Parameter {nameof(_input.Simulation.Name)} needs to be properly set.");
                return;
            }
            //The module choices were only ever driving which controls were shown; without this the
            //generated scenario would not actually enable what was ticked.
            _input.PedestrianModule.Enabled = _wantPedestrian;
            _input.TrafficModule.Enabled = _wantTraffic;
            _input.WildfireModule.Enabled = _wantWildfire;
            _input.SmokeModule.Enabled = _wantSmoke && _wantWildfire;

            //Selecting the module matters as much as enabling it. Both of these default to None,
            //and the parsers reject None for a module that is switched on, so a scenario generated
            //with either hazard ticked could not be loaded back at all until this was set.
            //GlobalSmoke is simply the only smoke module there is.
            _input.WildfireModule.Module = _wantWildfire
                ? (WildfireModuleInput.WildfireModules)_wildfireModuleIndex
                : WildfireModuleInput.WildfireModules.None;
            _input.SmokeModule.Module = _input.SmokeModule.Enabled
                ? SmokeInput.SmokeModules.GlobalSmoke
                : SmokeInput.SmokeModules.None;

            //Module has to be set, not just Enabled: it is what tells the writer which sub-section
            //to emit, so leaving it at None produces a scenario with no [kPERIL] section at all.
            //The trigger campaign then finds nothing to configure and quietly computes no boundary.
            _input.TriggerBufferModule.Enabled = _wantTriggerBuffer && _wantWildfire;
            _input.TriggerBufferModule.Module = _wantTriggerBuffer
                ? TriggerBufferModuleInput.TriggerBufferModules.kPERIL
                : TriggerBufferModuleInput.TriggerBufferModules.None;

            if (_wantTriggerBuffer)
            {
                kPERILInput peril = _input.TriggerBufferModule.kPERILInput;
                peril.CalculateROSFromBehave = _calculateRosFromBehave;

                //A required key, and an empty value is omitted rather than written, so the scenario
                //would fail to load without a name here. The campaign overwrites it per realization.
                if (string.IsNullOrEmpty(peril.OutputName))
                {
                    peril.OutputName = _input.Simulation.Name + "_trigger";
                }
            }

            _isOpen = false;
            _folderSet = false;

            //Path.Combine treats each argument as a path segment, so this used to build
            //"<root>\<name>\.wui" - a directory named after the scenario containing a file called
            //".wui" - rather than "<root>\<name>.wui".
            string filePath = Path.Combine(_input.RootFolder, _input.Simulation.Name + ".wui");

            PREACTInput.SaveToDisk(_input, filePath);
            PreactGUI.Engine.SetInput(_input, filePath);

            if (File.Exists(filePath))
            {
                Engine.Message(null, Engine.LogType.Log, "Scenario written to " + filePath);
            }
        }
        private static void OpenSetRootFolder()
        {
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(SetRootFolder, FileBrowser.Cancel, SimpleFileBrowser.FileBrowser.PickMode.Folders, false, null, null, "Set root folder", "Set");
        }
        private static void SetRootFolder(string[] paths)
        {
            _folderSet = true;
            _input = new PREACTInput(string.Empty);
            _input.RootFolder = paths[0];
        }
    }
}
