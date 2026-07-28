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

        //Feedback for the data-preparation steps. Without it even a working download looks like a
        //dead button: these take tens of seconds to minutes and the window would otherwise sit
        //silent throughout, which is indistinguishable from nothing having happened.
        private static string _stepStatus = string.Empty;
        private static volatile bool _stepBusy;

        //Steps run on a background thread, but the console is a LinkedList the GUI thread walks
        //while drawing - appending to it from another thread risks corrupting that walk. Messages
        //are therefore queued here and flushed into the engine log from Draw, on the GUI thread.
        private static readonly object _logSync = new object();
        private static readonly System.Collections.Generic.List<string> _pendingLog = new System.Collections.Generic.List<string>();

        //Progress popup. The fraction is negative while a step is running without a measurable
        //total, which the bar renders as a sweep rather than pretending to a percentage it does
        //not have.
        private static bool _progressPopupOpen;
        private static string _progressTitle = string.Empty;
        private static volatile float _progressFraction = -1f;
        private static string _progressDetail = string.Empty;
        private static readonly System.Collections.Generic.List<string> _progressLog = new System.Collections.Generic.List<string>();

        //Files the steps produce, relative to the scenario root so the generated .wui stays portable.
        private static string WorldPopFile => _input.Simulation.Name + "_worldpop.tif";
        private static string OsmFile => _input.Simulation.Name + ".osm.xml";
        private static string RouterDbFile => _input.Simulation.Name + ".routerdb";
        private static string PopulationFile => _input.Simulation.Name + "_population.csv";
        private static string WeatherFile => _input.Simulation.Name + "_weather.csv";

        //The SUMO network and configuration live in their own folder, since netconvert writes several
        //files beside the one named here.
        private const string SumoFolder = "sumo";
        private static string SumoConfigFile => Path.Combine(SumoFolder, PREACT.Utility.SumoNetworkBuilder.ConfigurationFileName);

        private static int _minHouseholdSize = 1;
        private static int _maxHouseholdSize = 5;
        private static bool _useAnderson13 = true;

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

            FlushStepLog();

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
                    ImGui.InputInt("Min household size", ref _minHouseholdSize);
                    ImGui.InputInt("Max household size", ref _maxHouseholdSize);

                    ImGui.BeginDisabled(_stepBusy);
                    if (StepButton("Step 1: Download WorldPop", WorldPopFile)) { DownloadWorldPop(); }
                    if (StepButton("Step 2: Download OSM data", OsmFile)) { DownloadOsm(); }
                    if (StepButton("Step 3: Build RouterDb", RouterDbFile)) { BuildRouterDb(); }
                    if (StepButton("Step 4: Generate population", PopulationFile)) { GeneratePopulation(); }
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
                    if (ImGui.Button("Set SUMO input file")) { FileBrowser.OpenSetFilePath(path => _input.TrafficModule.SumoInput.ConfigurationFile = path, "Select SUMOP input file", true); }
                }
                else
                {
                    ImGui.BeginDisabled(_stepBusy);
                    if (StepButton("Step 1: Download OSM data", OsmFile)) { DownloadOsm(); }
                    if (StepButton("Step 2: Build SUMO network", SumoConfigFile)) { BuildSumoNetwork(); }
                    ImGui.EndDisabled();
                    ImGui.TextWrapped("Step 2 runs SUMO's own netconvert on the downloaded OSM and writes the configuration, which is then set on the scenario. It needs SUMO installed.");
                }
            }

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
                    ImGui.Checkbox("Anderson 13 fuel models (otherwise Scott & Burgan 40)", ref _useAnderson13);
                    ImGui.BeginDisabled(_stepBusy);
                    if (ImGui.Button("Step 1: Download Landfire data")) { DownloadLandfire(); }
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
                    ImGui.BeginDisabled(_stepBusy);
                    if (StepButton("Step 1: Download weather file", WeatherFile)) { DownloadWeather(); }
                    ImGui.EndDisabled();
                }
            }

            if (!string.IsNullOrEmpty(_stepStatus))
            {
                ImGui.SeparatorText("Data preparation");
                ImGui.TextWrapped(_stepStatus);
                if (!_progressPopupOpen && ImGui.Button("Show progress"))
                {
                    _progressPopupOpen = true;
                }
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
            DrawProgressWindow();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        /// <summary>
        /// The AIO's north-east corner. The window stores the domain as a south-west corner plus a
        /// size in metres, while every downloader wants a lat/lon bounding box, so the size is
        /// converted with the same flat-earth approximation the population tools already use to
        /// interpret DomainSize.
        /// </summary>
        private static Vector2d UpperRightLatLon()
        {
            Vector2d ll = _input.Simulation.LowerLeftLatLon;
            //SizeToDegrees returns (lonDegrees, latDegrees) for a size given as (east, north)
            Vector2d deg = PREACT.Population.LocalGPWData.SizeToDegrees(ll, _input.Simulation.DomainSize);
            return new Vector2d(ll.x + deg.y, ll.y + deg.x);
        }

        /// <summary>
        /// Runs one data-preparation step off the UI thread, reporting start, success and failure.
        ///
        /// The reporting is the point: these steps take tens of seconds to minutes, and an
        /// unreported one is indistinguishable from a button that does nothing. Exceptions are
        /// caught and shown rather than left to vanish into a faulted Task, which is what happens
        /// by default with the fire-and-forget Task.Run used elsewhere in the GUI.
        /// </summary>
        private static void RunStep(string what, System.Func<System.Threading.Tasks.Task> work)
        {
            if (_stepBusy)
            {
                return;
            }

            if (!ValidateAio(out string problem))
            {
                _stepStatus = problem;
                LogStep(problem);
                return;
            }

            _stepBusy = true;
            _stepStatus = what + "...";
            _progressPopupOpen = true;
            _progressTitle = what;
            _progressDetail = string.Empty;
            _progressFraction = -1f;
            lock (_logSync) { _progressLog.Clear(); }
            LogStep(what + "...");

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await work();
                    _stepStatus = what + ": done.";
                    LogStep(what + ": done.");
                }
                catch (System.Exception e)
                {
                    _stepStatus = what + " FAILED: " + e.Message;
                    LogStep(what + " FAILED: " + e.Message);
                }
                finally
                {
                    _stepBusy = false;
                    //Completed steps show a full bar rather than freezing wherever they stopped.
                    _progressFraction = 1f;
                }
            });
        }

        /// <summary>
        /// A step button with a completion marker. Completion is judged by the output file
        /// existing rather than by a flag set when the button was pressed, so it stays correct
        /// across a restart, and after a step is re-run or its file deleted outside the editor.
        /// </summary>
        private static bool StepButton(string label, string producedFile)
        {
            bool done = !string.IsNullOrEmpty(_input.Simulation.Name) && File.Exists(InRoot(producedFile));

            bool pressed = ImGui.Button(done ? label + " (redo)" : label);

            ImGui.SameLine();
            if (done)
            {
                //ImGui has no tick glyph in the default font, so this uses text that renders in
                //any font rather than a symbol that might come out as a box.
                ImGui.TextColored(new Vector4(0.35f, 0.8f, 0.35f, 1f), "[done] " + producedFile);
            }
            else
            {
                ImGui.TextDisabled("[pending]");
            }

            return pressed;
        }

        /// <summary>
        /// The step progress window: what is running, how far along, and the messages it produced.
        /// Drawn as its own window rather than a modal so the map and console stay usable while a
        /// long download runs.
        /// </summary>
        private static void DrawProgressWindow()
        {
            if (!_progressPopupOpen)
            {
                return;
            }

            ImGui.Begin("Scenario data preparation", ref _progressPopupOpen, ImGuiWindowFlags.NoCollapse);

            ImGui.TextWrapped(_progressTitle);

            float fraction = _progressFraction;
            if (fraction >= 0f)
            {
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{fraction * 100f:F0} %");
            }
            else if (_stepBusy)
            {
                //No measurable total: a sweeping bar says "working" without inventing a
                //percentage. Driven by time so it animates regardless of what the step is doing.
                //Both qualified: PREACT.Math defines its own Mathf, and PREACT.Time is a namespace
                //that shadows UnityEngine.Time under this file's "using PREACT".
                float sweep = UnityEngine.Mathf.PingPong(UnityEngine.Time.realtimeSinceStartup * 0.6f, 1f);
                ImGui.ProgressBar(sweep, new Vector2(-1, 0), "working...");
            }
            else
            {
                ImGui.ProgressBar(1f, new Vector2(-1, 0), "idle");
            }

            if (!string.IsNullOrEmpty(_progressDetail))
            {
                ImGui.TextWrapped(_progressDetail);
            }

            ImGui.Separator();

            ImGui.BeginChild("step_log", new Vector2(0, 160), (ImGuiChildFlags)1, ImGuiWindowFlags.HorizontalScrollbar);
            lock (_logSync)
            {
                for (int i = 0; i < _progressLog.Count; ++i)
                {
                    ImGui.TextUnformatted(_progressLog[i]);
                }
            }
            if (_stepBusy)
            {
                ImGui.SetScrollHereY(1.0f);
            }
            ImGui.EndChild();

            ImGui.BeginDisabled(_stepBusy);
            if (ImGui.Button("Close"))
            {
                _progressPopupOpen = false;
            }
            ImGui.EndDisabled();

            ImGui.End();
        }

        /// <summary>Queues a message for the console and the progress window; safe to call from a
        /// step's worker thread.</summary>
        private static void LogStep(string message)
        {
            lock (_logSync)
            {
                _pendingLog.Add(message);
                _progressLog.Add(message);
                //bounded so a chatty step cannot grow this without limit
                if (_progressLog.Count > 200) _progressLog.RemoveAt(0);
            }
        }

        /// <summary>
        /// Reports download progress from a worker thread. Only the numbers are stored; they are
        /// formatted while drawing, so this stays cheap enough to call per buffer.
        /// </summary>
        private static void ReportBytes(long received, long total)
        {
            if (total > 0)
            {
                _progressFraction = (float)received / total;
                _progressDetail = $"{received / (1024.0 * 1024.0):F1} of {total / (1024.0 * 1024.0):F1} MB";
            }
            else
            {
                _progressFraction = -1f;
                _progressDetail = $"{received / (1024.0 * 1024.0):F1} MB received";
            }
        }

        /// <summary>
        /// Moves queued step messages into the engine log, from the GUI thread. Engine.Message
        /// ultimately appends to the console's LinkedList and calls Debug.Log, neither of which is
        /// safe to touch from the worker threads the steps run on.
        /// </summary>
        private static void FlushStepLog()
        {
            lock (_logSync)
            {
                for (int i = 0; i < _pendingLog.Count; ++i)
                {
                    Engine.Message(null, Engine.LogType.Log, _pendingLog[i]);
                }
                _pendingLog.Clear();
            }
        }

        /// <summary>Checked up front because every step depends on it, and an unset area of
        /// interest otherwise produces a confusing failure from deep inside a downloader.</summary>
        private static bool ValidateAio(out string problem)
        {
            problem = null;

            if (_input.Simulation.DomainSize.x <= 0.0 || _input.Simulation.DomainSize.y <= 0.0)
            {
                //Reports what was actually read rather than just asserting the area is unset - the
                //values are what distinguish "never picked" from "picked but not stored".
                problem = "Set the area of interest first. Currently lower-left " +
                          $"{_input.Simulation.LowerLeftLatLon.x:F5}, {_input.Simulation.LowerLeftLatLon.y:F5} " +
                          $"with domain {_input.Simulation.DomainSize.x:F0} x {_input.Simulation.DomainSize.y:F0} m " +
                          "(click two opposite corners on the map, or type the values in directly).";
                return false;
            }

            if (string.IsNullOrEmpty(_input.Simulation.Name))
            {
                problem = "Give the scenario a name first - it is used for the downloaded file names.";
                return false;
            }

            return true;
        }

        private static string InRoot(string fileName)
        {
            return Path.Combine(_input.RootFolder, fileName);
        }

        private static void DownloadWorldPop()
        {
            RunStep("Downloading WorldPop", async () =>
            {
                await PREACT.Tools.WorldPopDownloader.DownloadRegionUTM(
                    _input.Simulation.StartDateTime.Year,
                    _input.Simulation.LowerLeftLatLon, UpperRightLatLon(),
                    _input.RootFolder, Path.GetFileNameWithoutExtension(WorldPopFile),
                    ReportBytes);
            });
        }

        private static void DownloadOsm()
        {
            RunStep("Downloading OSM data", async () =>
            {
                await PREACT.Tools.OSMDownloader.Download(
                    _input.Simulation.LowerLeftLatLon, UpperRightLatLon(), InRoot(OsmFile));
            });
        }

        private static void BuildSumoNetwork()
        {
            RunStep("Building SUMO network", () =>
            {
                string osmPath = InRoot(OsmFile);
                if (!File.Exists(osmPath))
                {
                    throw new System.IO.FileNotFoundException("Download the OSM data first (step 1).", osmPath);
                }

                //The engine already locates SUMO's bin folder from the machine PATH, so the builder is
                //given that before it starts looking for netconvert itself.
                string configurationPath = PREACT.Utility.SumoNetworkBuilder.Build(
                    osmPath, InRoot(SumoFolder), PreactGUI.Engine.SumoPath, LogStep);

                if (configurationPath == null)
                {
                    throw new System.Exception("netconvert did not produce a network; see the messages above.");
                }

                //Stored relative to the scenario root, like every other generated path, so the
                //scenario stays portable. Setting it here is the point of automating the step: the
                //configuration is the thing the scenario actually refers to.
                _input.TrafficModule.SumoInput.ConfigurationFile = SumoConfigFile;
                _haveSumo = true;
                LogStep("Scenario now points at " + SumoConfigFile + ".");

                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        private static void BuildRouterDb()
        {
            RunStep("Building RouterDb", () =>
            {
                string osm = InRoot(OsmFile);
                if (!File.Exists(osm))
                {
                    throw new FileNotFoundException("Download the OSM data first (step 2).", osm);
                }

                PREACT.Tools.PopulationTools.CreateAndSaveRouterDb(osm, InRoot(RouterDbFile), out bool ok);
                if (!ok)
                {
                    throw new System.Exception("RouterDb creation failed - see the log.");
                }
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        private static void GeneratePopulation()
        {
            RunStep("Generating population", () =>
            {
                string worldPop = InRoot(WorldPopFile);
                string routerDb = InRoot(RouterDbFile);

                if (!File.Exists(worldPop)) throw new FileNotFoundException("Download WorldPop first (step 1).", worldPop);
                if (!File.Exists(routerDb)) throw new FileNotFoundException("Build the RouterDb first (step 3).", routerDb);

                PREACT.Tools.PopulationTools.CreatePopulationFromWorldPop(
                    _minHouseholdSize, _maxHouseholdSize, worldPop, routerDb, InRoot(PopulationFile), out bool ok);
                if (!ok)
                {
                    throw new System.Exception("Population generation failed - see the log.");
                }

                //Stored as a bare file name so the scenario folder stays relocatable.
                _input.Population.PopulationFile = PopulationFile;
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        private static void DownloadLandfire()
        {
            RunStep("Downloading LANDFIRE data", async () =>
            {
                await PREACT.Tools.LandfireLandscapeDownloader.Download(
                    _input.Simulation.StartDateTime.Year, _useAnderson13,
                    _input.Simulation.LowerLeftLatLon, UpperRightLatLon(), _input.RootFolder);
            });
        }

        private static void DownloadWeather()
        {
            RunStep("Downloading weather", async () =>
            {
                Vector2d ll = _input.Simulation.LowerLeftLatLon;
                Vector2d ur = UpperRightLatLon();
                await PREACT.Tools.OpenMeteoDownloader.Download(
                    new Vector2d(0.5 * (ll.x + ur.x), 0.5 * (ll.y + ur.y)),
                    _input.Simulation.StartDateTime, _input.Simulation.EndDateTime,
                    InRoot(WeatherFile));

                _input.Weather.WeatherFile = WeatherFile;
            });
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
