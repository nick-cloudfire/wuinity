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

        //Files the steps produce, relative to the scenario root so the generated .wui stays portable.
        private static string WorldPopFile => _input.Simulation.Name + "_worldpop.tif";
        private static string OsmFile => _input.Simulation.Name + ".osm.xml";
        private static string RouterDbFile => _input.Simulation.Name + ".routerdb";
        private static string PopulationFile => _input.Simulation.Name + "_population.csv";
        private static string WeatherFile => _input.Simulation.Name + "_weather.csv";

        private static int _minHouseholdSize = 1;
        private static int _maxHouseholdSize = 5;
        private static bool _useAnderson13 = true;

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

            ImGui.Begin("New scenario creator", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            if(!_folderSet)
            {
                if (ImGui.Button("Set root folder")) 
                {
                    OpenSetRootFolder();                    
                }
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
                    if (ImGui.Button("Step 1: Download WorldPop")) { DownloadWorldPop(); }
                    if (ImGui.Button("Step 2: Download OSM data")) { DownloadOsm(); }
                    if (ImGui.Button("Step 3: Build RouterDb")) { BuildRouterDb(); }
                    if (ImGui.Button("Step 4: Generate population")) { GeneratePopulation(); }
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
                    if (ImGui.Button("Step 1: Download OSM data")) { DownloadOsm(); }
                    ImGui.EndDisabled();
                    //No SUMO network builder exists on this side yet - the .osm.xml above is the
                    //input to SUMO's own netconvert/osmWebWizard, which has to be run externally.
                    //Saying so beats a button that silently does nothing.
                    ImGui.TextWrapped("Step 2: build the SUMO network from the downloaded .osm.xml with SUMO's own netconvert / osmWebWizard, then tick \"Have SUMO input?\" and select the .sumocfg.");
                }
            }

            ImGui.SeparatorText("Hazards");
            ImGui.Checkbox("Wildfire spread?", ref _wantWildfire);
            if (_wantWildfire)
            {
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
                    if (ImGui.Button("Step 1: Download weather file")) { DownloadWeather(); }
                    ImGui.EndDisabled();
                }
            }

            if (!string.IsNullOrEmpty(_stepStatus))
            {
                ImGui.SeparatorText("Data preparation");
                ImGui.TextWrapped(_stepStatus);
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

            ImGui.SeparatorText("Finished?");

            if (ImGui.Button("Generate scenario")) { GenerateScenario(); }

            ImGui.End();
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
                return;
            }

            _stepBusy = true;
            _stepStatus = what + "...";

            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    await work();
                    _stepStatus = what + ": done.";
                }
                catch (System.Exception e)
                {
                    _stepStatus = what + " FAILED: " + e.Message;
                }
                finally
                {
                    _stepBusy = false;
                }
            });
        }

        /// <summary>Checked up front because every step depends on it, and an unset area of
        /// interest otherwise produces a confusing failure from deep inside a downloader.</summary>
        private static bool ValidateAio(out string problem)
        {
            problem = null;

            if (_input.Simulation.DomainSize.x <= 0.0 || _input.Simulation.DomainSize.y <= 0.0)
            {
                problem = "Set the area of interest first (its domain size is zero).";
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
                    _input.RootFolder, Path.GetFileNameWithoutExtension(WorldPopFile));
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

        private static void SetAIO(Vector2d[] latLons)
        {
            _latLon = new Vector2d(Mathd.Min(latLons[0].x, latLons[1].x), Mathd.Min(latLons[0].y, latLons[1].y));
            _input.Simulation.LowerLeftLatLon = _latLon;
            Vector2d _upperRightLatLon = new Vector2d(Mathd.Max(latLons[0].x, latLons[1].x), Mathd.Max(latLons[0].y, latLons[1].y));
            var lower = PREACT.Utility.LatLngUTMConverter.WGS84.convertLatLngToUtm(_latLon.x, _latLon.y);
            var upper = PREACT.Utility.LatLngUTMConverter.WGS84.convertLatLngToUtm(_upperRightLatLon.x, _upperRightLatLon.y);
            _input.Simulation.DomainSize = new Vector2d(upper.Easting - lower.Easting, upper.Northing - lower.Northing); 
            Open(false);
        }

        private static void GenerateScenario()
        {
            if(_input.Simulation.Name == string.Empty)
            {
                Engine.Message(null, Engine.LogType.InputError, $"Parameter {nameof(_input.Simulation.Name)} needs to be properly set.");
                return;
            }
            _isOpen = false;
            _folderSet = false;
            string filePath = Path.Combine(_input.RootFolder, _input.Simulation.Name, ".wui");
            PREACTInput.SaveToDisk(_input, filePath);
            PreactGUI.Engine.SetInput(_input, filePath);     
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
