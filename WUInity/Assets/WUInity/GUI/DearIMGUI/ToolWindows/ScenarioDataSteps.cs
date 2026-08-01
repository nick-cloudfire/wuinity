using ImGuiNET;
using PREACT;
using PREACT.Input;
using PREACT.Math;
using System.IO;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The data-preparation steps a scenario needs: WorldPop, OSM, the RouterDb, the population CSV,
    /// the SUMO network, LANDFIRE and weather.
    ///
    /// These used to live inside the new-scenario creator and were reachable only from it, which meant
    /// there was no way to build a missing RouterDb - or any other of these - for a scenario loaded
    /// from disk: opening the creator calls ScenarioEditorWindow.ClearInput and starts a fresh input,
    /// so the loaded scenario was discarded to get at the buttons. They are therefore separated from
    /// the creator's own state here and act on whichever input is assigned to <see cref="Input"/>.
    ///
    /// Every step names its output after the scenario, relative to the scenario root, so a folder
    /// prepared for a scenario stays portable and a step can tell whether it has already run.
    /// </summary>
    public static class ScenarioDataSteps
    {
        /// <summary>The scenario the steps read their area of interest from and write their paths into.</summary>
        public static PREACTInput Input;

        //Household sizes for the population step, and which fuel model set LANDFIRE is asked for.
        //Shared rather than duplicated per window so the value shown is the value used.
        public static int MinHouseholdSize = 1;
        public static int MaxHouseholdSize = 5;
        public static bool UseAnderson13 = true;

        //Feedback. Without it even a working download looks like a dead button: these steps take tens
        //of seconds to minutes and would otherwise sit silent throughout, which is indistinguishable
        //from nothing having happened.
        private static string _status = string.Empty;
        private static volatile bool _busy;
        public static string Status { get => _status; }
        public static bool Busy { get => _busy; }

        //Steps run on a background thread, but the console is a LinkedList the GUI thread walks while
        //drawing - appending to it from another thread risks corrupting that walk. Messages are
        //queued here and flushed into the engine log from the GUI thread instead.
        private static readonly object _logSync = new object();
        private static readonly System.Collections.Generic.List<string> _pendingLog = new System.Collections.Generic.List<string>();

        //Progress window. The fraction is negative while a step runs without a measurable total,
        //which the bar renders as a sweep rather than pretending to a percentage it does not have.
        private static bool _progressWindowOpen;
        private static string _progressTitle = string.Empty;
        private static volatile float _progressFraction = -1f;
        private static string _progressDetail = string.Empty;
        private static readonly System.Collections.Generic.List<string> _progressLog = new System.Collections.Generic.List<string>();
        public static bool ProgressWindowOpen { get => _progressWindowOpen; set => _progressWindowOpen = value; }

        //OpenTopography requires a key per request, and it comes from the same place the Mapbox token
        //does: a gitignored JSON file under Resources, read by OpenTopographyAccess. That is the answer
        //for a key that should outlive the session and never reach the scenario file, which is meant to
        //be shared.
        //
        //The two fallbacks below are for the case where that file has not been made yet, so the step is
        //not simply unusable until someone finds the template.
        public static string OpenTopographyApiKeyOverride = string.Empty;

        /// <summary>
        /// The key that will actually be used, and where it came from. The configuration file wins, so a
        /// key typed here once cannot quietly shadow the one the file supplies from then on.
        /// </summary>
        public static string EffectiveOpenTopographyApiKey
        {
            get
            {
                string fromFile = global::WUInity.OpenTopographyAccess.ApiKey;
                if (!string.IsNullOrWhiteSpace(fromFile))
                {
                    return fromFile.Trim();
                }

                string fromEnvironment = System.Environment.GetEnvironmentVariable("OPENTOPOGRAPHY_API_KEY");
                if (!string.IsNullOrWhiteSpace(fromEnvironment))
                {
                    return fromEnvironment.Trim();
                }

                return OpenTopographyApiKeyOverride.Trim();
            }
        }

        public static string OpenTopographyApiKeySource
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(global::WUInity.OpenTopographyAccess.ApiKey))
                {
                    return "Resources/OpenTopography/OpenTopographyConfiguration.txt";
                }
                if (!string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("OPENTOPOGRAPHY_API_KEY")))
                {
                    return "the OPENTOPOGRAPHY_API_KEY environment variable";
                }
                if (!string.IsNullOrWhiteSpace(OpenTopographyApiKeyOverride))
                {
                    return "typed in for this session only";
                }
                return string.Empty;
            }
        }

        //Copernicus GLO-30 by default: free, global, and 30 m, which is finer than the cell size any of
        //these scenarios run at.
        public static string DemType = PREACT.Tools.OpenTopographyDownloader.DemTypeCopernicus30;
        public static readonly string[] DemTypes =
        {
            PREACT.Tools.OpenTopographyDownloader.DemTypeCopernicus30,
            PREACT.Tools.OpenTopographyDownloader.DemTypeCopernicus90,
            PREACT.Tools.OpenTopographyDownloader.DemTypeSrtm30,
            PREACT.Tools.OpenTopographyDownloader.DemTypeSrtm90
        };

        //Where the steps write, relative to the scenario root.
        //
        //A scenario folder holds twenty-odd files by the time it runs, and every step used to drop its
        //output straight into the root beside the .wui. Sorting them afterwards broke the scenario, since
        //the paths recorded in it were bare file names - so the folders are the steps' own convention now,
        //and what they record is the path including the folder.
        //
        //Separators are forward slashes rather than Path.Combine's. These strings go into the .wui as well
        //as being resolved on disk, and a backslash written on Windows is not a separator anywhere else,
        //which would make the scenario unreadable on another machine. Windows accepts either.

        /// <summary>Raw downloads, as they arrive: before clipping, warping, or conversion.</summary>
        public const string DownloadsFolder = "downloads";

        /// <summary>The terrain rasters a scenario runs on, on the simulation's own grid.</summary>
        public const string LandscapeFolder = "elmfire/inputs";

        private static string Named(string suffix) => Input.Simulation.Name + suffix;

        public static string WorldPopBaseName => Named("_worldpop");
        public static string WorldPopFile => DownloadsFolder + "/" + WorldPopBaseName + ".tif";
        //The reprojected raster the download also writes, and the one the population step has to read:
        //PopulationMap.CreatePopulation treats the geotransform as UTM metres. Handed the WGS84 clip above
        //it computes cell centres in degrees, transforms them as though they were eastings, and finds no
        //road within a cell of anywhere - so it writes a CSV holding nothing but its header, and says
        //"0 people with access to road network" in a log line that is easy to miss. WorldPopDownloader
        //names it by appending _UTM, which is what is repeated here.
        public static string WorldPopUtmFile => DownloadsFolder + "/" + WorldPopBaseName + "_UTM.tif";
        public static string DemFile => LandscapeFolder + "/" + Named("_dem.tif");
        //Written out rather than only computed in memory. ELMFIRE takes all three as separate GeoTIFF
        //inputs, and a derived raster that exists only inside a load cannot be handed to anything else,
        //inspected in QGIS, or compared against what a previous run used.
        public static string SlopeFile => LandscapeFolder + "/" + Named("_slope.tif");
        public static string AspectFile => LandscapeFolder + "/" + Named("_aspect.tif");
        //The download as it arrives, in degrees, before it is warped into the simulation's zone. Kept
        //rather than deleted: it is the slow part to obtain, and a failed warp can be retried from it.
        public static string DemDownloadFile => DownloadsFolder + "/" + Named("_dem_wgs84.tif");
        public static string OsmFile => DownloadsFolder + "/" + Named(".osm.xml");

        //These three stay in the root: they are the scenario's own description of itself rather than
        //data fetched or derived for it, and they are what a person opening the folder looks for.
        public static string RouterDbFile => Named(".routerdb");
        public static string PopulationFile => Named("_population.csv");
        public static string WeatherFile => Named("_weather.csv");

        //The SUMO network and configuration live in their own folder, since netconvert writes several
        //files beside the one named here.
        public const string SumoFolder = PREACT.Utility.SumoNetworkBuilder.SumoFolderName;
        public static string SumoConfigFile => SumoFolder + "/" + PREACT.Utility.SumoNetworkBuilder.ConfigurationFileName;

        /// <summary>
        /// Set when the SUMO step succeeds, so a caller showing a "have SUMO input?" choice can follow
        /// what the step did rather than contradicting it.
        /// </summary>
        public static volatile bool SumoNetworkBuilt;

        /// <summary>
        /// A step button with a completion marker. Completion is judged by the output file existing
        /// rather than by a flag set when the button was pressed, so it stays correct across a restart,
        /// and after a step is re-run or its file deleted outside the editor.
        ///
        /// An existing file is not a reason to refuse: the button says "(redo)" and re-running
        /// overwrites, which is what a changed area of interest or a truncated download needs.
        /// </summary>
        public static bool StepButton(string label, string producedFile)
        {
            bool done = Input != null
                        && !string.IsNullOrEmpty(Input.Simulation.Name)
                        && File.Exists(FindInRoot(producedFile));

            bool pressed = ImGui.Button(done ? label + " (redo)" : label);

            ImGui.SameLine();
            if (done)
            {
                //ImGui has no tick glyph in the default font, so this uses text that renders in any
                //font rather than a symbol that might come out as a box.
                ImGui.TextColored(new Vector4(0.35f, 0.8f, 0.35f, 1f), "[done] " + producedFile);
                if (ImGui.IsItemHovered())
                {
                    FileInfo info = new FileInfo(FindInRoot(producedFile));
                    ImGui.SetTooltip($"{info.Length / (1024.0 * 1024.0):F2} MB, written {info.LastWriteTime:yyyy-MM-dd HH:mm}."
                        + "\nRunning the step again overwrites it.");
                }
            }
            else
            {
                ImGui.TextDisabled("[pending]");
            }

            return pressed;
        }

        /// <summary>
        /// Runs one step off the UI thread, reporting start, success and failure.
        ///
        /// Exceptions are caught and shown rather than left to vanish into a faulted Task, which is
        /// what happens by default with the fire-and-forget Task.Run used elsewhere in the GUI.
        /// </summary>
        public static void RunStep(string what, System.Func<System.Threading.Tasks.Task> work)
        {
            if (_busy)
            {
                return;
            }

            if (!ValidateAio(out string problem))
            {
                _status = problem;
                LogStep(problem);
                return;
            }

            _busy = true;
            _status = what + "...";
            _progressWindowOpen = true;
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
                    _status = what + ": done.";
                    LogStep(what + ": done.");
                }
                catch (System.Exception e)
                {
                    _status = what + " FAILED: " + e.Message;
                    LogStep(what + " FAILED: " + e.Message);
                }
                finally
                {
                    _busy = false;
                    //Completed steps show a full bar rather than freezing wherever they stopped.
                    _progressFraction = 1f;
                }
            });
        }

        /// <summary>
        /// The step progress window: what is running, how far along, and the messages it produced.
        /// Its own window rather than a modal, so the map and console stay usable during a long
        /// download. Call it from a window's Draw, after that window's End.
        /// </summary>
        private static int _progressDrawnOnFrame = -1;
        public static void DrawProgressWindow()
        {
            if (!_progressWindowOpen)
            {
                return;
            }

            //Once per frame, however many windows call it: both the creator and the loaded-scenario
            //window draw it, and with both open the same window would otherwise have its contents
            //appended twice.
            int frame = ImGui.GetFrameCount();
            if (_progressDrawnOnFrame == frame)
            {
                return;
            }
            _progressDrawnOnFrame = frame;

            ImGui.Begin("Scenario data preparation", ref _progressWindowOpen, ImGuiWindowFlags.NoCollapse);

            ImGui.TextWrapped(_progressTitle);

            float fraction = _progressFraction;
            if (fraction >= 0f)
            {
                ImGui.ProgressBar(fraction, new Vector2(-1, 0), $"{fraction * 100f:F0} %");
            }
            else if (_busy)
            {
                //No measurable total: a sweeping bar says "working" without inventing a percentage.
                //Driven by time so it animates regardless of what the step is doing. Both qualified:
                //PREACT.Math defines its own Mathf, and PREACT.Time is a namespace that shadows
                //UnityEngine.Time under this file's "using PREACT".
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
            if (_busy)
            {
                ImGui.SetScrollHereY(1.0f);
            }
            ImGui.EndChild();

            ImGui.BeginDisabled(_busy);
            if (ImGui.Button("Close"))
            {
                _progressWindowOpen = false;
            }
            ImGui.EndDisabled();

            ImGui.End();
        }

        /// <summary>
        /// Draws the status line and a way back to the progress window once it has been closed.
        /// </summary>
        public static void DrawStatus()
        {
            if (string.IsNullOrEmpty(_status))
            {
                return;
            }

            ImGui.TextWrapped(_status);
            if (!_progressWindowOpen && ImGui.Button("Show progress"))
            {
                _progressWindowOpen = true;
            }
        }

        /// <summary>Queues a message for the console and the progress window; safe to call from a
        /// step's worker thread.</summary>
        public static void LogStep(string message)
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
        /// Moves queued step messages into the engine log, from the GUI thread. Engine.Message
        /// ultimately appends to the console's LinkedList and calls Debug.Log, neither of which is
        /// safe to touch from the worker threads the steps run on. Call it from a window's Draw.
        /// </summary>
        public static void FlushStepLog()
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

        /// <summary>Checked up front because every step depends on it, and an unset area of interest
        /// otherwise produces a confusing failure from deep inside a downloader.</summary>
        public static bool ValidateAio(out string problem)
        {
            problem = null;

            if (Input == null)
            {
                problem = "No scenario is loaded, so there is nothing to prepare data for.";
                return false;
            }

            if (Input.Simulation.DomainSize.x <= 0.0 || Input.Simulation.DomainSize.y <= 0.0)
            {
                //Reports what was actually read rather than just asserting the area is unset - the
                //values are what distinguish "never picked" from "picked but not stored".
                problem = "Set the area of interest first. Currently lower-left " +
                          $"{Input.Simulation.LowerLeftLatLon.x:F5}, {Input.Simulation.LowerLeftLatLon.y:F5} " +
                          $"with domain {Input.Simulation.DomainSize.x:F0} x {Input.Simulation.DomainSize.y:F0} m " +
                          "(click two opposite corners on the map, or type the values in directly).";
                return false;
            }

            if (string.IsNullOrEmpty(Input.Simulation.Name))
            {
                problem = "Give the scenario a name first - it is used for the downloaded file names.";
                return false;
            }

            return true;
        }

        public static string InRoot(string fileName)
        {
            return Path.Combine(Input.RootFolder, fileName);
        }

        /// <summary>
        /// Where a step's output actually is: the folder it writes to now, or wherever the file has since
        /// been moved to among the scenario's own subfolders - including the scenario root, which is where
        /// every one of these lived before the steps started using subfolders.
        ///
        /// Reads and "has this step already run?" go through here while writes go to the new folders, so
        /// changing the layout does not make finished work look unfinished, or a prerequisite that is
        /// plainly on disk look missing.
        /// </summary>
        public static string FindInRoot(string fileName)
        {
            if (PREACT.Utility.ScenarioFileLocator.TryResolve(Input.RootFolder, fileName, out string resolved, out string _))
            {
                return InRoot(resolved);
            }

            //The path it would be written to, so a caller reporting "not found" names where it should be.
            return InRoot(fileName);
        }

        /// <summary>
        /// Where a step is about to write, with the folder created.
        ///
        /// Separate from <see cref="InRoot"/>, which is also used to ask whether a step has already run:
        /// creating folders as a side effect of that would leave an empty <c>downloads</c> beside every
        /// scenario that had never downloaded anything. Every writer goes through here, because the outputs
        /// now sit in subfolders and none of the downloaders or raster writers creates its own.
        /// </summary>
        public static string InRootForWriting(string fileName)
        {
            string path = InRoot(fileName);
            string folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }
            return path;
        }

        /// <summary>
        /// The area of interest's north-east corner. A scenario stores the domain as a south-west
        /// corner plus a size in metres, while every downloader wants a lat/lon bounding box, so the
        /// size is converted with the same flat-earth approximation the population tools already use
        /// to interpret DomainSize.
        /// </summary>
        public static Vector2d UpperRightLatLon()
        {
            Vector2d ll = Input.Simulation.LowerLeftLatLon;
            //SizeToDegrees returns (lonDegrees, latDegrees) for a size given as (east, north)
            Vector2d deg = PREACT.Population.LocalGPWData.SizeToDegrees(ll, Input.Simulation.DomainSize);
            return new Vector2d(ll.x + deg.y, ll.y + deg.x);
        }

        public static void DownloadWorldPop()
        {
            RunStep("Downloading WorldPop", async () =>
            {
                //The downloader takes a folder and a bare name, and writes both the clip and its _UTM
                //reprojection into that folder - so the folder is peeled off the path here rather than
                //passing the root and letting it decide.
                string folder = Path.GetDirectoryName(InRootForWriting(WorldPopFile));

                await PREACT.Tools.WorldPopDownloader.DownloadRegionUTM(
                    Input.Simulation.StartDateTime.Year,
                    Input.Simulation.LowerLeftLatLon, UpperRightLatLon(),
                    folder, WorldPopBaseName,
                    ReportBytes);
            });
        }

        public static void DownloadOsm()
        {
            RunStep("Downloading OSM data", async () =>
            {
                await PREACT.Tools.OSMDownloader.Download(
                    Input.Simulation.LowerLeftLatLon, UpperRightLatLon(), InRootForWriting(OsmFile));
            });
        }

        public static void BuildSumoNetwork()
        {
            RunStep("Building SUMO network", () =>
            {
                string osmPath = FindInRoot(OsmFile);
                if (!File.Exists(osmPath))
                {
                    throw new FileNotFoundException("Download the OSM data first.", osmPath);
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
                Input.TrafficModule.SumoInput.ConfigurationFile = SumoConfigFile;
                SumoNetworkBuilt = true;
                LogStep("Scenario now points at " + SumoConfigFile + ".");

                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        public static void BuildRouterDb()
        {
            RunStep("Building RouterDb", () =>
            {
                string osm = FindInRoot(OsmFile);
                if (!File.Exists(osm))
                {
                    throw new FileNotFoundException("Download the OSM data first.", osm);
                }

                PREACT.Tools.PopulationTools.CreateAndSaveRouterDb(osm, InRootForWriting(RouterDbFile), out bool ok);
                if (!ok)
                {
                    throw new System.Exception("RouterDb creation failed - see the log.");
                }
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        public static void GeneratePopulation()
        {
            RunStep("Generating population", () =>
            {
                //The UTM reprojection, not the WGS84 clip beside it - see WorldPopUtmFile.
                string worldPop = FindInRoot(WorldPopUtmFile);
                string routerDb = FindInRoot(RouterDbFile);

                if (!File.Exists(worldPop))
                {
                    throw new FileNotFoundException(
                        "The reprojected WorldPop raster is missing. Re-run the WorldPop download, which writes it "
                        + "beside the clip.", worldPop);
                }
                if (!File.Exists(routerDb)) throw new FileNotFoundException("Build the RouterDb first.", routerDb);

                PREACT.Tools.PopulationTools.CreatePopulationFromWorldPop(
                    MinHouseholdSize, MaxHouseholdSize, worldPop, routerDb, InRootForWriting(PopulationFile), out bool ok);
                if (!ok)
                {
                    throw new System.Exception("Population generation failed - see the log.");
                }

                //Reported because an empty result is otherwise indistinguishable from a full one: the
                //file is written either way, so the [done] marker appears for a CSV holding nothing but
                //its header - which is what a RouterDb with no reachable roads produces.
                int households = CountPopulationRows(FindInRoot(PopulationFile));
                LogStep($"Population file holds {households} households.");
                if (households == 0)
                {
                    throw new System.Exception("The population file came out empty - no household could be placed. "
                        + "Check that the RouterDb covers the area and that WorldPop has people in it.");
                }

                //Stored relative to the scenario folder, so it stays relocatable.
                Input.Population.PopulationFile = PopulationFile;
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        /// <summary>Data rows in a population CSV, excluding its header.</summary>
        private static int CountPopulationRows(string path)
        {
            int rows = 0;
            using (StreamReader reader = new StreamReader(path))
            {
                //header
                reader.ReadLine();
                while (reader.ReadLine() is string line)
                {
                    if (!string.IsNullOrWhiteSpace(line)) ++rows;
                }
            }
            return rows;
        }

        /// <summary>
        /// Downloads a DEM for the area of interest and warps it into the zone the simulation measures in.
        ///
        /// The warp is the part that matters. OpenTopography serves geographic coordinates - degrees, with
        /// a cell size of about 0.00028 - and everything here works in UTM metres: the landscape reader
        /// takes the cell size as metres, the painter places cells by it, k-PERIL measures distances with
        /// it. Handing a scenario a raster in degrees would put its cells 0.0003 m apart and its corner at
        /// an easting of 23.
        /// </summary>
        public static void DownloadDem()
        {
            RunStep("Downloading DEM", async () =>
            {
                string apiKey = EffectiveOpenTopographyApiKey;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new System.Exception("OpenTopography needs an API key, set the same way as the Mapbox "
                        + "token: copy Assets/Resources/OpenTopography/OpenTopographyConfigurationTemplate.txt to "
                        + "OpenTopographyConfiguration.txt beside it and paste a key into it. One is free from "
                        + "portal.opentopography.org.");
                }
                LogStep("Using the OpenTopography key from " + OpenTopographyApiKeySource + ".");

                Vector2d requestLowerLeft = Input.Simulation.LowerLeftLatLon;
                Vector2d requestUpperRight = UpperRightLatLon();

                //Asked for with a margin. A latitude/longitude box is not a rectangle in UTM, so the
                //bounding box the warp clips to reaches beyond the corners of what was downloaded - and
                //GDAL fills what the source does not cover, with zero when the source declares no nodata,
                //which Copernicus does not. Beside 400 m of hillside that fill reads as a cliff: measured
                //without the margin, Mati's downloaded DEM came out with slopes up to 86 degrees against
                //47 for the same ground from a DEM that covers it properly.
                //
                //Half a kilometre in degrees, which is a few cells at any DEM resolution offered here.
                const double marginDegrees = 0.005;
                Vector2d paddedLowerLeft = new Vector2d(requestLowerLeft.x - marginDegrees, requestLowerLeft.y - marginDegrees);
                Vector2d paddedUpperRight = new Vector2d(requestUpperRight.x + marginDegrees, requestUpperRight.y + marginDegrees);

                string downloaded = InRootForWriting(DemDownloadFile);
                await PREACT.Tools.OpenTopographyDownloader.Download(
                    paddedLowerLeft, paddedUpperRight, apiKey, downloaded, DemType);

                LogStep("Warping the DEM into the simulation's UTM zone...");

                //Clipped to the area of interest that was asked for, not to the padded box that was
                //downloaded: the margin exists to give the warp data to work with at the corners, not to
                //enlarge the scenario's domain.
                Vector2d ll = requestLowerLeft;
                Vector2d ur = requestUpperRight;

                //Clipped as well as reprojected: the download is cut to a lat/lon box, which is not a
                //rectangle in UTM, so the warped extent is the bounding box of the four transformed
                //corners rather than the box that was asked for.
                //
                //Into the zone the simulation measures in, named explicitly rather than recomputed from
                //the domain's centre. For a domain on a zone boundary the two differ, and a DEM in one
                //zone beside a fire in the next is half a million metres of disagreement.
                PREACT.Utility.MasterGrid grid = PREACT.Utility.RasterHarmonizer.BuildUtmMasterGrid(
                    downloaded, InRootForWriting(DemFile), ll.x, ll.y, ur.x, ur.y,
                    null, Input.Simulation.Data.UtmEpsgCode);

                LogStep($"DEM on the simulation's grid: {grid.Header.Ncols} x {grid.Header.Nrows} cells of "
                    + $"{grid.Header.CellSize:F1} m, in {grid.Epsg}.");

                WriteSlopeAndAspect(grid);

                //Set on the scenario, which is the point of the step: this is what makes terrain, slope
                //and aspect available to everything that wants them.
                Input.Landscape.ElevationFile = DemFile;
                LogStep("Scenario now points at " + DemFile + ", " + SlopeFile + " and " + AspectFile + ".");
            });
        }

        /// <summary>
        /// Derives slope and aspect from the DEM and writes both as GeoTIFFs on its grid.
        ///
        /// On disk rather than only in memory, because they are inputs in their own right: ELMFIRE takes
        /// elevation, slope and aspect as three separate GeoTIFFs, and a raster that exists only inside a
        /// scenario load cannot be handed to it, opened in QGIS, or compared against what an earlier run
        /// used. The scenario still recomputes them if the files are absent, so nothing depends on this
        /// having been run - it makes them available, it does not make them required.
        /// </summary>
        private static void WriteSlopeAndAspect(PREACT.Utility.MasterGrid grid)
        {
            float[,] elevation = PREACT.Utility.AscRaster.ReadGeoTiff(FindInRoot(DemFile),
                out PREACT.Utility.AscRaster.Header header, out bool ok);
            if (!ok || elevation == null)
            {
                LogStep("Could not read the DEM back, so no slope or aspect was written.");
                return;
            }

            PREACT.Utility.SlopeAspect.Compute(elevation, header.CellSize,
                out float[,] slope, out float[,] aspect);

            PREACT.Utility.GeoTiffRasterWriter.WriteBand(grid, slope, InRootForWriting(SlopeFile));
            PREACT.Utility.GeoTiffRasterWriter.WriteBand(grid, aspect, InRootForWriting(AspectFile));

            Input.Landscape.SlopeFile = SlopeFile;
            Input.Landscape.AspectFile = AspectFile;

            //Reported with their ranges, because a slope raster is the one of the three whose plausibility
            //can be judged at a glance: tens of degrees is terrain, ninety is a nodata edge.
            float slopeMax = 0f, slopeMean = 0f;
            int cells = 0;
            for (int y = 0; y < header.Nrows; ++y)
            {
                for (int x = 0; x < header.Ncols; ++x)
                {
                    slopeMax = System.Math.Max(slopeMax, slope[x, y]);
                    slopeMean += slope[x, y];
                    ++cells;
                }
            }
            if (cells > 0) slopeMean /= cells;

            LogStep($"Wrote {SlopeFile} and {AspectFile} (slope mean {slopeMean:F1} deg, max {slopeMax:F0} deg).");
        }

        public static void DownloadLandfire()
        {
            RunStep("Downloading LANDFIRE data", async () =>
            {
                await PREACT.Tools.LandfireLandscapeDownloader.Download(
                    Input.Simulation.StartDateTime.Year, UseAnderson13,
                    Input.Simulation.LowerLeftLatLon, UpperRightLatLon(), Input.RootFolder);
            });
        }

        public static void DownloadWeather()
        {
            RunStep("Downloading weather", async () =>
            {
                Vector2d ll = Input.Simulation.LowerLeftLatLon;
                Vector2d ur = UpperRightLatLon();
                await PREACT.Tools.OpenMeteoDownloader.Download(
                    new Vector2d(0.5 * (ll.x + ur.x), 0.5 * (ll.y + ur.y)),
                    Input.Simulation.StartDateTime, Input.Simulation.EndDateTime,
                    InRootForWriting(WeatherFile));

                Input.Weather.WeatherFile = WeatherFile;
            });
        }
    }
}
