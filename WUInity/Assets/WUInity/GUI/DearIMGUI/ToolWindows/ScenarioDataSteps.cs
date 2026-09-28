using ImGuiNET;
using PREACT;
using PREACT.Input;
using PREACT.Math;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The data-preparation steps a scenario needs - OSM, the RouterDb, the SUMO network, WorldPop, the
    /// population, LANDFIRE fuels and canopy, a DEM, weather, the ELMFIRE case - and the chains the workflow
    /// runs them in.
    /// </summary>
    /// <remarks>
    /// Every step names its output after the scenario, relative to the scenario root, so a folder prepared
    /// for a scenario stays portable and a step can tell whether it has already run.
    ///
    /// A step works on the scenario it was started for. Its area, name, dates and folder are captured when
    /// it starts (<see cref="StepContext"/>), it runs on a worker thread, and the paths it produces are set
    /// on the scenario on the main thread once it has succeeded - and only if that scenario is still the one
    /// open. These closures used to read a static <c>Input</c> when they finished, so a step that took
    /// minutes wrote its paths into whichever scenario happened to be loaded by then, from a worker thread.
    /// </remarks>
    public static class ScenarioDataSteps
    {
        /// <summary>The scenario the step buttons describe: always the open one.</summary>
        public static PREACTInput Input { get => ScenarioSession.Input; }

        //Household sizes for the population step, and which fuel model set LANDFIRE is asked for.
        //Shared rather than duplicated per window so the value shown is the value used. Scott & Burgan 40 by
        //default: it is what ELMFIRE and the LANDFIRE products are normally run with.
        public static int MinHouseholdSize = 1;
        public static int MaxHouseholdSize = 5;
        public static bool UseAnderson13 = false;

        //Feedback. Without it even a working download looks like a dead button: these steps take tens
        //of seconds to minutes and would otherwise sit silent throughout, which is indistinguishable
        //from nothing having happened.
        private static string _status = string.Empty;
        private static volatile bool _busy;
        private static volatile bool _lastFailed;
        private static volatile bool _stopRequested;
        public static string Status { get => _status; }
        public static bool Busy { get => _busy; }
        /// <summary>The step or chain running now, or last run.</summary>
        public static string CurrentTitle { get => _progressTitle; }

        /// <summary>A stop was asked for the running step; it ends after the link it is in.</summary>
        public static bool StopRequested { get => _stopRequested; }

        /// <summary>
        /// Stops the running step or chain: no further link starts, and an ELMFIRE or WindNinja process it started is
        /// killed now. A download or a GDAL warp under way finishes first - they cannot be interrupted - so
        /// <see cref="Busy"/> stays true until it has. A case build then stops at its next safe point (before the
        /// grid, before the weather, or before the next WindNinja solve) and reports that it was stopped; it never
        /// goes on with a uniform wind in place of the solve that was killed.
        /// </summary>
        public static void RequestStop()
        {
            if (!_busy || _stopRequested)
            {
                return;
            }

            _stopRequested = true;
            _status = "Stopping " + _progressTitle + "...";
            LogStep("Stop requested: nothing further starts; ELMFIRE and WindNinja are stopped at once, a download under way "
                + "finishes first, and a case build stops at its next safe point without writing any wind.");
            PREACT.Utility.ElmfireRunner.CancelAll();
        }

        /// <summary>The workflow step the running (or last) chain belongs to.</summary>
        public static WorkflowStepId Owner { get; private set; }

        /// <summary>Raised on the main thread when a step or chain has finished, after its results were applied.</summary>
        public static event Action<WorkflowStepId, bool> StepFinished;

        //The progress window's own copy of what the running step said. Written from the step's worker
        //thread and read while drawing, hence the lock; the console gets the same lines through
        //PreactGUI.Post, which delivers them on the main thread.
        private static readonly object _logSync = new object();

        //Progress window. The fraction is negative while a step runs without a measurable total,
        //which the bar renders as a sweep rather than pretending to a percentage it does not have.
        private static bool _progressWindowOpen;
        private static string _progressTitle = string.Empty;
        private static volatile string _progressLink = string.Empty;
        private static volatile float _progressFraction = -1f;
        private static volatile string _progressDetail = string.Empty;
        private static readonly List<string> _progressLog = new List<string>();
        public static bool ProgressWindowOpen { get => _progressWindowOpen; set => _progressWindowOpen = value; }
        public static float ProgressFraction { get => _progressFraction; }

        //OpenTopography requires a key per request, and it comes from the same place the Mapbox token
        //does: a gitignored JSON file under Resources, read by OpenTopographyAccess. That is the answer
        //for a key that should outlive the session and never reach the scenario file, which is meant to
        //be shared.
        //
        //The fallback below is for the case where that file has not been made yet, so the step is not
        //simply unusable until someone finds the template. The case build does not take a key from the
        //GUI - the engine finds its own (PREACT.Utility.OpenTopographyKey: the environment, then the same
        //file) - so a key typed here is put into this process's environment, where the build, and a
        //campaign CLI started from here, find it. It never leaves the process.
        public static string OpenTopographyApiKeyOverride = string.Empty;

        private const string OpenTopographyVariable = "OPENTOPOGRAPHY_API_KEY";

        //What the environment held before anything typed here was put into it, and what was put in.
        private static readonly string _environmentKeyAtStart = System.Environment.GetEnvironmentVariable(OpenTopographyVariable);
        private static string _sessionKeyInEnvironment;

        /// <summary>The environment's key, unless it is only the one typed in for this session.</summary>
        private static string EnvironmentKey
        {
            get
            {
                string value = System.Environment.GetEnvironmentVariable(OpenTopographyVariable);
                if (string.IsNullOrWhiteSpace(value)) return null;
                if (_sessionKeyInEnvironment != null && value.Trim() == _sessionKeyInEnvironment) return null;
                return value.Trim();
            }
        }

        /// <summary>
        /// The key that will actually be used, and where it came from, in the engine's order: the environment,
        /// then the configuration file, then a key typed in for this session. Main thread only (it reads a
        /// Unity resource).
        /// </summary>
        public static string EffectiveOpenTopographyApiKey
        {
            get
            {
                string fromEnvironment = EnvironmentKey;
                if (fromEnvironment != null)
                {
                    return fromEnvironment;
                }

                string fromFile = global::WUInity.OpenTopographyAccess.ApiKey;
                if (!string.IsNullOrWhiteSpace(fromFile))
                {
                    return fromFile.Trim();
                }

                return OpenTopographyApiKeyOverride.Trim();
            }
        }

        public static string OpenTopographyApiKeySource
        {
            get
            {
                if (EnvironmentKey != null)
                {
                    return "the OPENTOPOGRAPHY_API_KEY environment variable";
                }
                if (!string.IsNullOrWhiteSpace(global::WUInity.OpenTopographyAccess.ApiKey))
                {
                    return "Resources/OpenTopography/OpenTopographyConfiguration.txt";
                }
                if (!string.IsNullOrWhiteSpace(OpenTopographyApiKeyOverride))
                {
                    return "typed in for this session only";
                }
                return string.Empty;
            }
        }

        /// <summary>
        /// Puts the key typed in for this session where the engine looks for one - this process's environment -
        /// when neither the environment nor the configuration file supplies one; clears it again when the field
        /// is emptied. Called when the field changes and before a step that downloads terrain.
        /// </summary>
        public static void ApplySessionOpenTopographyKey()
        {
            string typed = OpenTopographyApiKeyOverride.Trim();
            bool suppliedElsewhere = !string.IsNullOrWhiteSpace(_environmentKeyAtStart)
                                     || !string.IsNullOrWhiteSpace(global::WUInity.OpenTopographyAccess.ApiKey);
            if (suppliedElsewhere)
            {
                return;
            }

            string value = typed.Length == 0 ? null : typed;
            if (value == _sessionKeyInEnvironment)
            {
                return;
            }

            try
            {
                System.Environment.SetEnvironmentVariable(OpenTopographyVariable, value);
                _sessionKeyInEnvironment = value;
            }
            catch (Exception e)
            {
                Engine.Message(null, Engine.LogType.Warning, "Could not hand the OpenTopography key to the case build: " + e.Message);
            }
        }

        //Copernicus GLO-30 by default: free, global, and 30 m, which is finer than the cell size any of
        //these scenarios run at.
        public static string DemType = PREACT.Tools.OpenTopographyDownloader.DemTypeCopernicus30;

        // ------------------------------------------------------------------ the captured scenario

        /// <summary>
        /// What a step knows about the scenario it was started for, captured on the main thread before the
        /// worker starts, and the path writes it wants made once it has succeeded.
        /// </summary>
        public sealed class StepContext
        {
            public PREACTInput Input;
            public string Root;
            public string Name;
            public Vector2d LowerLeft;
            public Vector2d UpperRight;
            public Vector2d DomainSize;
            public DateTime Start;
            public DateTime End;
            public int UtmEpsg;
            public bool PedestrianEnabled;
            public bool TrafficEnabled;
            public string OpenTopographyKey;
            public string OpenTopographyKeySource;
            public string DemType;
            public bool UseAnderson13;
            public int MinHouseholdSize, MaxHouseholdSize;

            internal readonly List<Action<PREACTInput>> Writes = new List<Action<PREACTInput>>();
            internal readonly List<Action> AfterApply = new List<Action>();

            /// <summary>The step changed the scenario itself (the ELMFIRE case build does, in place).</summary>
            public bool ChangedInPlace;

            /// <summary>Queues a change to the scenario, made on the main thread if the step succeeds.</summary>
            public void Set(Action<PREACTInput> write) { Writes.Add(write); }

            /// <summary>Queues main-thread work to do after the writes, if the step succeeds.</summary>
            public void Then(Action work) { AfterApply.Add(work); }

            public string InRoot(string relative) => Path.Combine(Root, relative);

            /// <summary>Where a step is about to write, with its folder created.</summary>
            public string InRootForWriting(string relative)
            {
                string path = InRoot(relative);
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                return path;
            }

            /// <summary>Where a step's earlier output actually is (it may have been moved to a subfolder).</summary>
            public string FindInRoot(string relative)
            {
                if (PREACT.Utility.ScenarioFileLocator.TryResolve(Root, relative, out string resolved, out string _))
                {
                    return Path.IsPathRooted(resolved) ? resolved : InRoot(resolved);
                }
                return InRoot(relative);
            }

            public bool Has(string relative) => File.Exists(FindInRoot(relative));
        }

        /// <summary>One link of a chain: skipped when <see cref="Needed"/> says its output is already there.</summary>
        public sealed class ChainLink
        {
            public string Title;
            public Func<StepContext, bool> Needed;
            public Func<StepContext, Task> Work;

            public ChainLink(string title, Func<StepContext, bool> needed, Func<StepContext, Task> work)
            {
                Title = title;
                Needed = needed;
                Work = work;
            }
        }

        private static StepContext Capture()
        {
            PREACTInput input = Input;
            if (input == null)
            {
                return null;
            }

            return new StepContext
            {
                Input = input,
                Root = input.RootFolder,
                Name = input.Simulation.Name,
                LowerLeft = input.Simulation.LowerLeftLatLon,
                UpperRight = UpperRightLatLon(input),
                DomainSize = input.Simulation.DomainSize,
                Start = input.Simulation.StartDateTime,
                End = input.Simulation.EndDateTime,
                UtmEpsg = input.Simulation.Data.UtmEpsgCode,
                PedestrianEnabled = input.PedestrianModule.Enabled,
                TrafficEnabled = input.TrafficModule.Enabled,
                OpenTopographyKey = EffectiveOpenTopographyApiKey,
                OpenTopographyKeySource = OpenTopographyApiKeySource,
                DemType = DemType,
                UseAnderson13 = UseAnderson13,
                MinHouseholdSize = MinHouseholdSize,
                MaxHouseholdSize = MaxHouseholdSize,
            };
        }

        // ------------------------------------------------------------------ running

        /// <summary>Runs one step for the workflow step <paramref name="owner"/>.</summary>
        public static void RunStep(string what, WorkflowStepId owner, Func<StepContext, Task> work)
        {
            RunChain(what, owner, new ChainLink(what, _ => true, work));
        }

        /// <summary>
        /// Runs the links in order on one worker, skipping those whose output already exists, stopping at the
        /// first that fails. Paths each link produced are applied to the scenario when the whole chain has
        /// finished - also after a failure, for the links that did succeed, since those files are real.
        /// </summary>
        public static void RunChain(string title, WorkflowStepId owner, params ChainLink[] links)
        {
            if (_busy)
            {
                return;
            }

            if (ScenarioSession.IsBusy)
            {
                _status = "Not started: " + ScenarioSession.BusyReason + ".";
                return;
            }

            StepContext ctx = Capture();
            if (!ValidateAio(ctx, out string problem))
            {
                _status = problem;
                LogStep(problem);
                return;
            }

            _busy = true;
            _lastFailed = false;
            _stopRequested = false;
            Owner = owner;
            _status = title + "...";
            _progressWindowOpen = true;
            _progressTitle = title;
            _progressLink = string.Empty;
            _progressDetail = string.Empty;
            _progressFraction = -1f;
            lock (_logSync) { _progressLog.Clear(); }
            LogStep(title + "...");

            Task.Run(async () =>
            {
                bool ok = true;
                string failure = null;
                int ran = 0;
                foreach (ChainLink link in links)
                {
                    if (_stopRequested)
                    {
                        ok = false;
                        failure = title + " STOPPED before " + link.Title.ToLowerInvariant() + ".";
                        LogStep(failure);
                        break;
                    }

                    bool needed;
                    try { needed = link.Needed(ctx); }
                    catch { needed = true; }

                    if (!needed)
                    {
                        LogStep(link.Title + ": already done, skipped.");
                        continue;
                    }

                    _progressLink = link.Title;
                    _progressDetail = string.Empty;
                    _progressFraction = -1f;
                    if (links.Length > 1) LogStep(link.Title + "...");

                    try
                    {
                        await link.Work(ctx);
                        ++ran;
                        if (links.Length > 1) LogStep(link.Title + ": done.");
                    }
                    catch (Exception e)
                    {
                        ok = false;
                        failure = (_stopRequested ? link.Title + " STOPPED: " : link.Title + " FAILED: ")
                                  + (e is AggregateException ae ? ae.GetBaseException().Message : e.Message);
                        LogStep(failure);
                        break;
                    }

                    //A link that finished although it was asked to stop is not a result to trust: a download or a warp
                    //that could not be interrupted, or a case build the stop reached only after its weather was written.
                    if (_stopRequested)
                    {
                        ok = false;
                        failure = link.Title + " STOPPED: it finished after the stop was asked for, so what it made may be "
                                  + "incomplete; run it again.";
                        LogStep(failure);
                        break;
                    }
                }

                string summary = ok
                    ? (ran == 0 ? title + ": nothing to do, everything was already there." : title + ": done.")
                    : failure;
                if (ok) LogStep(summary);

                PreactGUI.Post(() => Finish(ctx, owner, ok, summary));
            });
        }

        /// <summary>On the main thread: applies what the step produced, then lets everything else know.</summary>
        private static void Finish(StepContext ctx, WorkflowStepId owner, bool ok, string summary)
        {
            bool stillOpen = ScenarioSession.Input == ctx.Input;

            if (stillOpen)
            {
                foreach (Action<PREACTInput> write in ctx.Writes)
                {
                    try { write(ctx.Input); }
                    catch (Exception e) { LogStep("Could not set a produced path on the scenario: " + e.Message); }
                }

                foreach (Action after in ctx.AfterApply)
                {
                    try { after(); }
                    catch (Exception e) { LogStep("After the step: " + e.Message); }
                }
            }
            else if (ctx.Writes.Count > 0)
            {
                //Cannot happen through the GUI - opening another scenario waits for the step - but said if it
                //does, rather than writing into a scenario the step was not run for.
                LogStep("The scenario was closed while this ran, so the paths it produced were not set on any scenario.");
            }

            _status = summary;
            _lastFailed = !ok;
            _busy = false;
            _stopRequested = false;
            _progressFraction = 1f;

            if (stillOpen && (ctx.Writes.Count > 0 || ctx.ChangedInPlace))
            {
                ScenarioSession.NotifyEdited(ctx.Name);
            }

            GuiFiles.Probe.Invalidate();
            StepFinished?.Invoke(owner, ok);
        }

        /// <summary>
        /// Runs a job that is not about the scenario's data (copying a folder) with the same progress window
        /// and the same busy state, handing its result to <paramref name="done"/> on the main thread.
        /// </summary>
        public static void RunUtility(string title, Func<string> work, Action<string> done)
        {
            if (_busy || ScenarioSession.IsBusy)
            {
                return;
            }

            _busy = true;
            _lastFailed = false;
            _stopRequested = false;
            Owner = WorkflowStepId.None;
            _status = title + "...";
            _progressWindowOpen = true;
            _progressTitle = title;
            _progressLink = string.Empty;
            _progressDetail = string.Empty;
            _progressFraction = -1f;
            lock (_logSync) { _progressLog.Clear(); }
            LogStep(title + "...");

            Task.Run(() =>
            {
                string result = null;
                string failure = null;
                try { result = work(); }
                catch (Exception e) { failure = title + " FAILED: " + e.Message; LogStep(failure); }

                PreactGUI.Post(() =>
                {
                    _busy = false;
                    _stopRequested = false;
                    _lastFailed = failure != null;
                    _progressFraction = 1f;
                    _status = failure ?? title + ": done.";
                    if (failure == null)
                    {
                        done?.Invoke(result);
                    }
                });
            });
        }

        // ------------------------------------------------------------------ progress

        /// <summary>
        /// The step progress window: what is running, how far along, and the messages it produced. Its own
        /// window rather than a modal, so the map and console stay usable during a long download. Drawn from
        /// the GUI's layout every frame, so it no longer disappears (and stops updating) when whichever window
        /// started the step is closed.
        /// </summary>
        public static void DrawProgressWindow()
        {
            if (!_progressWindowOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(520f, 320f));
            if (!ImGui.Begin("Data preparation###StepProgress", ref _progressWindowOpen, PreactGUI.ToolWindowFlags))
            {
                ImGui.End();
                return;
            }

            ImGui.TextWrapped(_progressTitle);
            string link = _progressLink;
            if (_busy && !string.IsNullOrEmpty(link) && link != _progressTitle)
            {
                ImGui.TextDisabled("Now: " + link);
            }

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
                ImGui.ProgressBar(1f, new Vector2(-1, 0), _lastFailed ? "failed" : "finished");
            }

            string detail = _progressDetail;
            if (!string.IsNullOrEmpty(detail))
            {
                ImGui.TextWrapped(detail);
            }

            ImGui.Separator();

            ImGui.BeginChild("step_log", new Vector2(0, -ImGui.GetFrameHeightWithSpacing()), (ImGuiChildFlags)1, ImGuiWindowFlags.HorizontalScrollbar);
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

            if (_busy)
            {
                ImGui.BeginDisabled(_stopRequested);
                if (ImGui.Button(_stopRequested ? "Stopping..." : "Stop"))
                {
                    RequestStop();
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip("Nothing further starts; ELMFIRE and WindNinja are stopped at once. A download or a raster "
                        + "warp under way finishes first. A case build keeps what it finished and writes no wind; the next "
                        + "build carries on from there.");
                }
                ImGui.SameLine();
            }

            ImGui.BeginDisabled(_busy);
            if (ImGui.Button("Close"))
            {
                _progressWindowOpen = false;
            }
            ImGui.EndDisabled();

            ImGui.End();
        }

        /// <summary>Draws the status line and a way back to the progress window once it has been closed.</summary>
        public static void DrawStatus()
        {
            if (string.IsNullOrEmpty(_status))
            {
                return;
            }

            if (_lastFailed) ImGui.TextColored(Fields.Alert, _status);
            else ImGui.TextWrapped(_status);

            if (!_progressWindowOpen && ImGui.SmallButton("Show progress###ShowStepProgress"))
            {
                _progressWindowOpen = true;
            }
        }

        /// <summary>Records a message in the progress window and the console; safe to call from a step's worker thread.</summary>
        public static void LogStep(string message)
        {
            lock (_logSync)
            {
                _progressLog.Add(message);
                //bounded so a chatty step cannot grow this without limit
                if (_progressLog.Count > 200) _progressLog.RemoveAt(0);
            }

            //Engine.Message appends to the engine's own log and calls back into the GUI, so it is sent to
            //the main thread rather than called from the worker.
            PreactGUI.Post(() => Engine.Message(null, Engine.LogType.Log, message));
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

        // ------------------------------------------------------------------ checks and helpers

        /// <summary>Checked up front because every step depends on it, and an unset area of interest
        /// otherwise produces a confusing failure from deep inside a downloader.</summary>
        public static bool ValidateAio(out string problem)
        {
            return ValidateAio(Capture(), out problem);
        }

        private static bool ValidateAio(StepContext ctx, out string problem)
        {
            problem = null;

            if (ctx == null)
            {
                problem = "No scenario is open, so there is nothing to prepare data for.";
                return false;
            }

            if (ctx.DomainSize.x <= 0.0 || ctx.DomainSize.y <= 0.0)
            {
                //Reports what was actually read rather than just asserting the area is unset - the
                //values are what distinguish "never picked" from "picked but not stored".
                problem = "Set the area of interest first (step 1, Place and time). Currently lower-left " +
                          $"{ctx.LowerLeft.x:F5}, {ctx.LowerLeft.y:F5} " +
                          $"with domain {ctx.DomainSize.x:F0} x {ctx.DomainSize.y:F0} m.";
                return false;
            }

            if (string.IsNullOrEmpty(ctx.Name))
            {
                problem = "Give the scenario a name first - it is used for the downloaded file names.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// The area of interest's north-east corner. A scenario stores the domain as a south-west
        /// corner plus a size in metres, while every downloader wants a lat/lon bounding box, so the
        /// size is converted with the same flat-earth approximation the population tools already use
        /// to interpret DomainSize.
        /// </summary>
        public static Vector2d UpperRightLatLon(PREACTInput input)
        {
            Vector2d ll = input.Simulation.LowerLeftLatLon;
            //SizeToDegrees returns (lonDegrees, latDegrees) for a size given as (east, north)
            Vector2d deg = PREACT.Population.LocalGPWData.SizeToDegrees(ll, input.Simulation.DomainSize);
            return new Vector2d(ll.x + deg.y, ll.y + deg.x);
        }

        // ------------------------------------------------------------------ the steps

        private static Task DoDownloadWorldPop(StepContext ctx)
        {
            //The downloader takes a folder and a bare name, and writes both the clip and its _UTM
            //reprojection into that folder - so the folder is peeled off the path here rather than
            //passing the root and letting it decide.
            string folder = Path.GetDirectoryName(ctx.InRootForWriting(ScenarioFiles.WorldPop(ctx.Name)));
            return PREACT.Tools.WorldPopDownloader.DownloadRegionUTM(ctx.Start.Year, ctx.LowerLeft, ctx.UpperRight,
                folder, ScenarioFiles.WorldPopBaseName(ctx.Name), ReportBytes);
        }

        private static Task DoDownloadOsm(StepContext ctx)
        {
            return PREACT.Tools.OSMDownloader.Download(ctx.LowerLeft, ctx.UpperRight, ctx.InRootForWriting(ScenarioFiles.Osm(ctx.Name)));
        }

        private static Task DoBuildSumoNetwork(StepContext ctx, string sumoBin)
        {
            string osmPath = ctx.FindInRoot(ScenarioFiles.Osm(ctx.Name));
            if (!File.Exists(osmPath))
            {
                throw new FileNotFoundException("Download the OSM data first.", osmPath);
            }

            //The engine already locates SUMO's bin folder (SUMO_HOME, then PATH), so the builder is
            //given that before it starts looking for netconvert itself. The network is projected into the
            //simulation's own UTM zone (netconvert --proj): left to itself netconvert picks the zone of the
            //OSM data's centre, which for a domain pinned to a neighbouring zone, or straddling a boundary, is
            //another frame than the one the simulation measures in (SumoParser warns about the mismatch).
            string configurationPath = PREACT.Utility.SumoNetworkBuilder.Build(osmPath, ctx.InRoot(ScenarioFiles.SumoFolder), sumoBin, LogStep,
                utmEpsgCode: ctx.UtmEpsg);
            if (configurationPath == null)
            {
                throw new Exception("netconvert did not produce a network; see the messages above.");
            }

            //Stored relative to the scenario root, like every other generated path, so the scenario stays
            //portable. Setting it is the point of automating the step: the configuration is the thing the
            //scenario actually refers to.
            ctx.Set(i => i.TrafficModule.SumoInput.ConfigurationFile = ScenarioFiles.SumoConfig);
            LogStep("The scenario will point at " + ScenarioFiles.SumoConfig + ".");
            return Task.CompletedTask;
        }

        private static Task DoBuildRouterDb(StepContext ctx)
        {
            string osm = ctx.FindInRoot(ScenarioFiles.Osm(ctx.Name));
            if (!File.Exists(osm))
            {
                throw new FileNotFoundException("Download the OSM data first.", osm);
            }

            PREACT.Tools.PopulationTools.CreateAndSaveRouterDb(osm, ctx.InRootForWriting(ScenarioFiles.RouterDb(ctx.Name)), out bool ok);
            if (!ok)
            {
                throw new Exception("RouterDb creation failed - see the log.");
            }
            return Task.CompletedTask;
        }

        private static Task DoGeneratePopulation(StepContext ctx)
        {
            //The UTM reprojection, not the WGS84 clip beside it: PopulationMap.CreatePopulation treats the
            //geotransform as UTM metres, and handed the clip it finds no road within a cell of anywhere.
            string worldPop = ctx.FindInRoot(ScenarioFiles.WorldPopUtm(ctx.Name));
            string routerDb = ctx.FindInRoot(ScenarioFiles.RouterDb(ctx.Name));

            if (!File.Exists(worldPop))
            {
                throw new FileNotFoundException(
                    "The reprojected WorldPop raster is missing. Re-run the WorldPop download, which writes it "
                    + "beside the clip.", worldPop);
            }
            if (!File.Exists(routerDb)) throw new FileNotFoundException("Build the RouterDb first.", routerDb);

            string output = ctx.InRootForWriting(ScenarioFiles.Population(ctx.Name));
            PREACT.Tools.PopulationTools.CreatePopulationFromWorldPop(
                ctx.MinHouseholdSize, ctx.MaxHouseholdSize, worldPop, routerDb, output, out bool ok);
            if (!ok)
            {
                throw new Exception("Population generation failed - see the log.");
            }

            //Reported because an empty result is otherwise indistinguishable from a full one: the file is
            //written either way - which is what a RouterDb with no reachable roads produces.
            int households = ScenarioFiles.CountPopulationRows(output);
            LogStep($"Population file holds {households} households.");
            if (households == 0)
            {
                throw new Exception("The population file came out empty - no household could be placed. "
                    + "Check that the RouterDb covers the area and that WorldPop has people in it.");
            }

            ctx.Set(i => i.Population.PopulationFile = ScenarioFiles.Population(ctx.Name));
            return Task.CompletedTask;
        }

        /// <summary>
        /// Downloads a DEM for the area of interest and warps it into the zone the simulation measures in.
        /// For a scenario without an ELMFIRE fire: an ELMFIRE scenario's terrain is its case's dem.tif.
        /// </summary>
        private static async Task DoDownloadDem(StepContext ctx)
        {
            if (string.IsNullOrWhiteSpace(ctx.OpenTopographyKey))
            {
                throw new Exception("OpenTopography needs an API key, set the same way as the Mapbox "
                    + "token: copy Assets/Resources/OpenTopography/OpenTopographyConfigurationTemplate.txt to "
                    + "OpenTopographyConfiguration.txt beside it and paste a key into it. One is free from "
                    + "portal.opentopography.org. Help > External tools and keys says where it is looked for.");
            }
            LogStep("Using the OpenTopography key from " + ctx.OpenTopographyKeySource + ".");

            //Asked for with a margin. A latitude/longitude box is not a rectangle in UTM, so the bounding box
            //the warp clips to reaches beyond the corners of what was downloaded - and GDAL fills what the
            //source does not cover with zero, which beside a hillside reads as a cliff.
            const double marginDegrees = 0.005;
            Vector2d paddedLowerLeft = new Vector2d(ctx.LowerLeft.x - marginDegrees, ctx.LowerLeft.y - marginDegrees);
            Vector2d paddedUpperRight = new Vector2d(ctx.UpperRight.x + marginDegrees, ctx.UpperRight.y + marginDegrees);

            string downloaded = ctx.InRootForWriting(ScenarioFiles.DemDownload(ctx.Name));
            await PREACT.Tools.OpenTopographyDownloader.Download(paddedLowerLeft, paddedUpperRight, ctx.OpenTopographyKey, downloaded, ctx.DemType);

            LogStep("Warping the DEM into the simulation's UTM zone...");

            //Into the zone the simulation measures in, named explicitly rather than recomputed from the
            //domain's centre: on a zone boundary the two differ by half a million metres.
            PREACT.Utility.MasterGrid grid = PREACT.Utility.RasterHarmonizer.BuildUtmMasterGrid(
                downloaded, ctx.InRootForWriting(ScenarioFiles.Dem(ctx.Name)),
                ctx.LowerLeft.x, ctx.LowerLeft.y, ctx.UpperRight.x, ctx.UpperRight.y, null, ctx.UtmEpsg);

            LogStep($"DEM on the simulation's grid: {grid.Header.Ncols} x {grid.Header.Nrows} cells of "
                + $"{grid.Header.CellSize:F1} m, in {grid.Epsg}.");

            WriteSlopeAndAspect(ctx, grid);

            ctx.Set(i =>
            {
                i.Landscape.ElevationFile = ScenarioFiles.Dem(ctx.Name);
                i.Landscape.SlopeFile = ScenarioFiles.Slope(ctx.Name);
                i.Landscape.AspectFile = ScenarioFiles.Aspect(ctx.Name);
            });
        }

        /// <summary>Derives slope and aspect from the DEM and writes both as GeoTIFFs on its grid.</summary>
        private static void WriteSlopeAndAspect(StepContext ctx, PREACT.Utility.MasterGrid grid)
        {
            float[,] elevation = PREACT.Utility.AscRaster.ReadGeoTiff(ctx.FindInRoot(ScenarioFiles.Dem(ctx.Name)),
                out PREACT.Utility.AscRaster.Header header, out bool ok);
            if (!ok || elevation == null)
            {
                throw new Exception("Could not read the DEM back, so no slope or aspect was written.");
            }

            PREACT.Utility.SlopeAspect.Compute(elevation, header.CellSize, out float[,] slope, out float[,] aspect);
            PREACT.Utility.GeoTiffRasterWriter.WriteBand(grid, slope, ctx.InRootForWriting(ScenarioFiles.Slope(ctx.Name)));
            PREACT.Utility.GeoTiffRasterWriter.WriteBand(grid, aspect, ctx.InRootForWriting(ScenarioFiles.Aspect(ctx.Name)));

            //Reported with their ranges, because slope is the raster whose plausibility can be judged at a
            //glance: tens of degrees is terrain, ninety is a nodata edge.
            float slopeMax = 0f, slopeMean = 0f;
            int cells = 0;
            for (int y = 0; y < header.Nrows; ++y)
            {
                for (int x = 0; x < header.Ncols; ++x)
                {
                    slopeMax = Math.Max(slopeMax, slope[x, y]);
                    slopeMean += slope[x, y];
                    ++cells;
                }
            }
            if (cells > 0) slopeMean /= cells;

            LogStep($"Wrote {ScenarioFiles.Slope(ctx.Name)} and {ScenarioFiles.Aspect(ctx.Name)} (slope mean {slopeMean:F1} deg, max {slopeMax:F0} deg).");
        }

        private static async Task DoDownloadWeather(StepContext ctx)
        {
            await PREACT.Tools.OpenMeteoDownloader.Download(
                new Vector2d(0.5 * (ctx.LowerLeft.x + ctx.UpperRight.x), 0.5 * (ctx.LowerLeft.y + ctx.UpperRight.y)),
                ctx.Start, ctx.End, ctx.InRootForWriting(ScenarioFiles.Weather(ctx.Name)));

            ctx.Set(i => i.Weather.WeatherFile = ScenarioFiles.Weather(ctx.Name));
        }

        /// <summary>
        /// LANDFIRE's fuel model and four canopy layers for the area, split into the single-band rasters the
        /// ELMFIRE case builder warps, and named as the scenario's ELMFIRE source layers.
        /// </summary>
        /// <remarks>
        /// The download used to set nothing at all: it left a zip and a multi-band GeoTIFF in the scenario
        /// folder, the scenario did not refer to them, and ELMFIRE - which takes fuel and canopy only from the
        /// [ELMFIRE] source layers - never saw them. The LFPS job returns one GeoTIFF with a band per requested
        /// product, in request order (elevation, slope, aspect, fuel model, CC, CH, CBH, CBD, FCCS); the bands
        /// are matched by their descriptions where LFPS supplies them and by that order otherwise.
        ///
        /// LANDFIRE stores canopy height and base height in metres x 10 and bulk density in kg/m3 x 100, so the
        /// namelist's CH_TIMES_10 / CBH_TIMES_10 / CBD_TIMES_100 are switched on to match.
        /// </remarks>
        private static async Task DoDownloadLandfire(StepContext ctx)
        {
            Vector2d centre = new Vector2d(0.5 * (ctx.LowerLeft.x + ctx.UpperRight.x), 0.5 * (ctx.LowerLeft.y + ctx.UpperRight.y));
            if (!ScenarioFiles.IsInLandfireCoverage(ctx.LowerLeft, ctx.UpperRight))
            {
                throw new Exception("LANDFIRE covers the United States only, and this domain is outside it. Name a fuel "
                    + "model raster of your own, and the FIRE-RES canopy folder for Europe, instead.");
            }

            try
            {
                string iso3 = await PREACT.Tools.WorldPopDownloader.LatLonToISO3(centre.x, centre.y);
                if (!string.IsNullOrEmpty(iso3) && iso3 != "USA")
                {
                    throw new Exception("LANDFIRE covers the United States only, and this domain is in " + iso3 + ".");
                }
            }
            catch (Exception e) when (!e.Message.StartsWith("LANDFIRE"))
            {
                //The country lookup is a courtesy; the bounding box already said this is plausibly US ground.
                LogStep("Could not confirm the country (" + e.Message + "); asking LANDFIRE anyway.");
            }

            string folder = ctx.InRoot(ScenarioFiles.LandfireFolder);
            Directory.CreateDirectory(folder);
            DateTime started = DateTime.UtcNow.AddSeconds(-5);

            await PREACT.Tools.LandfireLandscapeDownloader.Download(ctx.Start.Year, ctx.UseAnderson13, ctx.LowerLeft, ctx.UpperRight, folder);

            //The downloader reports a failed status check by returning, so what arrived is the only evidence.
            string multiband = null;
            DateTime newest = DateTime.MinValue;
            foreach (string tif in Directory.GetFiles(folder, "*.tif"))
            {
                DateTime written = File.GetLastWriteTimeUtc(tif);
                if (written >= started && written > newest && !Path.GetFileName(tif).StartsWith(ctx.Name + "_lf_"))
                {
                    newest = written;
                    multiband = tif;
                }
            }

            if (multiband == null)
            {
                throw new Exception("LANDFIRE returned nothing usable - the job failed, timed out or the download was cut "
                    + "short. See the messages above; the LFPS service is sometimes simply busy, and trying again later works.");
            }

            LogStep("Splitting " + Path.GetFileName(multiband) + " into the layers ELMFIRE takes...");
            string fuelStem = ctx.UseAnderson13 ? "fbfm13" : "fbfm40";
            var wanted = new List<(string stem, string key, int fallbackBand)>
            {
                (fuelStem, "FBFM", 4), ("cc", "CC", 5), ("ch", "CH", 6), ("cbh", "CBH", 7), ("cbd", "CBD", 8),
            };

            var layers = new Dictionary<string, string>();
            OSGeo.GDAL.Gdal.AllRegister();
            using (OSGeo.GDAL.Dataset source = OSGeo.GDAL.Gdal.Open(multiband, OSGeo.GDAL.Access.GA_ReadOnly))
            {
                if (source == null)
                {
                    throw new Exception("Could not open " + multiband + ".");
                }

                int bands = source.RasterCount;
                var descriptions = new string[bands + 1];
                for (int b = 1; b <= bands; ++b)
                {
                    using (OSGeo.GDAL.Band band = source.GetRasterBand(b))
                    {
                        descriptions[b] = (band.GetDescription() ?? string.Empty).ToUpperInvariant();
                    }
                }

                foreach ((string stem, string key, int fallbackBand) in wanted)
                {
                    int bandIndex = ScenarioFiles.FindLandfireBand(descriptions, key, fallbackBand);
                    if (bandIndex < 1 || bandIndex > bands)
                    {
                        LogStep($"No {key} band in the download ({bands} band(s)); {stem} is left unset.");
                        continue;
                    }

                    string relative = ScenarioFiles.LandfireLayer(ctx.Name, stem);
                    string destination = ctx.InRootForWriting(relative);
                    var options = new OSGeo.GDAL.GDALTranslateOptions(new[] { "-b", bandIndex.ToString(), "-of", "GTiff", "-co", "COMPRESS=DEFLATE" });
                    using (OSGeo.GDAL.Dataset band = OSGeo.GDAL.Gdal.wrapper_GDALTranslate(destination, source, options, null, null))
                    {
                        if (band == null)
                        {
                            throw new Exception($"Could not write band {bandIndex} ({key}) to {destination}.");
                        }
                        band.FlushCache();
                    }
                    layers[stem] = relative;
                    LogStep($"  band {bandIndex} ({(string.IsNullOrEmpty(descriptions[bandIndex]) ? key : descriptions[bandIndex])}) -> {relative}");
                }
            }

            if (!layers.ContainsKey(fuelStem))
            {
                throw new Exception("The download holds no fuel model band, so it cannot supply the case's fuel.");
            }

            bool anderson = ctx.UseAnderson13;
            ctx.Set(i =>
            {
                ElmfireInput e = i.WildfireModule.ElmfireInput;
                e.FuelModelFile = layers[fuelStem];
                e.FuelModelStandard = anderson ? ElmfireInput.FuelModelStandards.FBFM13 : ElmfireInput.FuelModelStandards.FBFM40;
                if (layers.TryGetValue("cc", out string cc)) e.CanopyCoverFile = cc;
                if (layers.TryGetValue("ch", out string ch)) e.CanopyHeightFile = ch;
                if (layers.TryGetValue("cbh", out string cbh)) e.CanopyBaseHeightFile = cbh;
                if (layers.TryGetValue("cbd", out string cbd)) e.CanopyBulkDensityFile = cbd;
                //LANDFIRE's scaled integers.
                e.Namelist.CC_IN_PERCENT = true;
                e.Namelist.CH_TIMES_10 = true;
                e.Namelist.CBH_TIMES_10 = true;
                e.Namelist.CBD_TIMES_100 = true;
            });
            LogStep("The scenario's ELMFIRE source layers will name these. Build (or rebuild) the fire case to warp them onto its grid.");
        }

        /// <summary>
        /// Builds the ELMFIRE case: the rasters, the weather series and the namelist. With
        /// <paramref name="rebuildExisting"/> every layer is made again, as for a changed domain or cell size.
        /// </summary>
        private static Task DoBuildElmfireCase(StepContext ctx, bool rebuildExisting, PaintingFacts painting = null)
        {
            ElmfireInput settings = ctx.Input.WildfireModule.ElmfireInput;
            bool previous = settings.RebuildExistingLayers;
            settings.RebuildExistingLayers = rebuildExisting || previous;

            bool ok;
            string problem;
            DateTime started = DateTime.Now;
            try
            {
                //Contract C1: on success this points [Landscape] at the case's dem/slp/asp, [kPERIL] WuiAreaFile at
                //the wui_area.tif it wrote, and [Weather] at the day and archive the fire was computed against - in
                //place. It runs on the worker while editing is locked; Finish then marks the scenario changed.
                ok = PREACT.Utility.ElmfireCoupling.BuildCaseOnly(ctx.Input, LogStep, out problem);
            }
            finally
            {
                settings.RebuildExistingLayers = previous;
            }

            if (!ok)
            {
                //Thrown rather than returned: RunChain reports a faulted step, and a build that failed must not
                //read as one that succeeded just because it was the last thing to run. A stopped build says so in
                //the problem, and leaves the scenario as it was (nothing changed in place).
                throw new Exception(problem ?? "the case could not be built");
            }
            ctx.ChangedInPlace = true;

            //The terrain the build pointed [Landscape] at is read, and the painter goes onto the case grid; on the
            //main thread, after Finish has applied the step.
            ctx.Then(() => PreactGUI.WUInity?.ReloadLandscape());

            //Every build writes the namelist again from the scenario. One edited by hand is set aside first, which the
            //builder only says in its log; said where it is seen as well.
            string setAside = NamelistSetAsideSince(GuiFiles.Resolve(ctx.Root, ScenarioFiles.CaseDirectory(ctx.Input)), started);
            if (setAside != null)
            {
                string message = "A hand-edited namelist was set aside as " + setAside + ": the build wrote elmfire.data again from "
                    + "the scenario's settings. To run the hand-edited one as it is, name it as [ELMFIRE] NamelistTemplate "
                    + "(workflow step 5 offers to).";
                LogStep(message);
                ctx.Then(() => Engine.Message(null, Engine.LogType.Warning, message));
            }

            //The build has just placed the painting on the case grid as ignition_mask.tif and wui_area.tif, through the
            //grid it was painted on; the painting file itself follows it there, or the next build (and the painter)
            //would be left with a painting on a grid nothing names any more.
            if (painting != null)
            {
                CarryPaintingOntoCaseGrid(ctx, painting);
            }

            //Strokes not saved (the save question was answered "Don't save") are in the scenario's masks on the grid the
            //painter had. A re-cut grid would drop them; they are carried onto it too, and stay unsaved.
            if (painting != null && painting.UnsavedStrokeGrid.x > 0)
            {
                Vector2int strokes = painting.UnsavedStrokeGrid;
                string setAsideGrid = ScenarioFiles.CaseInput(ctx.Input, PREACT.Utility.ElmfireCaseBuilder.PreviousGridFolder + "/dem.tif");
                string caseGrid = ScenarioFiles.CaseInput(ctx.Input, "dem.tif");
                ctx.Then(() => CarryUnsavedStrokes(ctx.Input, strokes, setAsideGrid, caseGrid));
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// On the main thread after a build: when the case grid changed under unsaved fire strokes, and the grid they
        /// were painted on is the one the build set aside, moves them onto the new grid in memory.
        /// </summary>
        private static void CarryUnsavedStrokes(PREACTInput input, Vector2int strokeGrid, string previousGrid, string currentGrid)
        {
            global::WUInity.Painter painter = PreactGUI.WUInity?.Painter;
            if (input == null || input != ScenarioSession.Input || painter == null || !painter.UnsavedFireStrokes) return;

            PREACT.Input.WildfireData data = input.WildfireModule.Data;
            int cells = strokeGrid.x * strokeGrid.y;
            if (data.WuiArea == null || data.WuiArea.Length != cells) return;

            try
            {
                var to = PREACT.Utility.PaintedMaskResampler.Grid.FromRaster(GuiFiles.Resolve(input.RootFolder, currentGrid));
                //Nothing to carry when the strokes are on this grid: its size, and - when they record one - its place.
                if (to.Ncols == strokeGrid.x && to.Nrows == strokeGrid.y && to.DescribeMismatch(data.PaintedGrid) == null) return;

                string previousPath = GuiFiles.Resolve(input.RootFolder, previousGrid);
                if (previousPath == null || !File.Exists(previousPath))
                {
                    return;
                }
                var from = PREACT.Utility.PaintedMaskResampler.Grid.FromRaster(previousPath);
                if (from.Ncols != strokeGrid.x || from.Nrows != strokeGrid.y || from.DescribeMismatch(data.PaintedGrid) != null) return;

                var masks = new PREACT.Utility.PaintedMaskResampler.Masks
                {
                    Ncols = from.Ncols, Nrows = from.Nrows, WuiArea = data.WuiArea, RandomIgnition = data.RandomIgnition,
                    InitialIgnition = data.InitialIgnition, ManualTriggerBuffer = data.ManualTriggerBuffer,
                };
                var moved = PREACT.Utility.PaintedMaskResampler.Resample(masks, from, to, out bool _);
                data.WuiArea = moved.WuiArea;
                data.RandomIgnition = moved.RandomIgnition;
                data.InitialIgnition = moved.InitialIgnition;
                data.ManualTriggerBuffer = moved.ManualTriggerBuffer;
                data.PaintedCellCount = new Vector2int(to.Ncols, to.Nrows);
                data.PaintedGrid = to.ToPaintedGrid();
                Engine.Message(null, Engine.LogType.Log, $"The fire areas painted and not saved were carried from the old "
                    + $"{from.Ncols} x {from.Nrows} case grid onto the new {to.Ncols} x {to.Nrows} one; they are still unsaved.");
            }
            catch (Exception e)
            {
                Engine.Message(null, Engine.LogType.Warning, "Could not carry the unsaved fire strokes onto the new case grid: " + e.Message);
            }
        }

        /// <summary>The name of an elmfire.data.kept-&lt;time&gt; the builder set aside at or after <paramref name="since"/>, or null.</summary>
        private static string NamelistSetAsideSince(string caseFolder, DateTime since)
        {
            if (string.IsNullOrEmpty(caseFolder) || !Directory.Exists(caseFolder)) return null;

            string found = null;
            DateTime foundAt = DateTime.MinValue;
            foreach (string kept in Directory.GetFiles(caseFolder, "elmfire.data.kept-*"))
            {
                string stamp = Path.GetFileName(kept).Substring("elmfire.data.kept-".Length);
                if (DateTime.TryParseExact(stamp, "yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out DateTime at)
                    && at >= since.AddSeconds(-2) && at > foundAt)
                {
                    found = Path.GetFileName(kept);
                    foundAt = at;
                }
            }
            return found;
        }

        /// <summary>
        /// What is known about the painting before a build changes the scenario: its file, and the grids it may
        /// have been painted on - the terrain [Landscape] names before the build points it at the case, and the one
        /// the workflow found. Captured on the main thread.
        /// </summary>
        public sealed class PaintingFacts
        {
            public string File;
            public readonly List<string> Grids = new List<string>();

            /// <summary>The paint grid's size when fire strokes are unsaved, else (0, 0).</summary>
            public Vector2int UnsavedStrokeGrid;

            public static PaintingFacts Capture(PREACTInput input)
            {
                if (input == null) return null;
                global::WUInity.Painter painter = PreactGUI.WUInity?.Painter;
                var facts = new PaintingFacts
                {
                    File = input.WildfireModule?.GraphicalFireInputFile,
                    UnsavedStrokeGrid = painter != null && painter.UnsavedFireStrokes ? painter.PaintGridSize : new Vector2int(0, 0),
                };
                string file = facts.File;
                if (string.IsNullOrEmpty(file)) return facts;

                //The grid a re-cut sets aside is the newest, so it comes first.
                facts.Grids.Add(ScenarioFiles.CaseInput(input, PREACT.Utility.ElmfireCaseBuilder.PreviousGridFolder + "/dem.tif"));
                string landscape = input.Landscape?.GetReferenceFile();
                if (!string.IsNullOrEmpty(landscape)) facts.Grids.Add(landscape);
                string found = WorkflowService.Model.PaintedOnReference;
                if (!string.IsNullOrEmpty(found)) facts.Grids.Add(found);
                return facts;
            }
        }

        /// <summary>
        /// On the worker, after a successful build: when the painting is not on the case's (possibly re-cut) grid
        /// but on one of the grids it may have been painted on, moves it onto the case grid as a new file.
        /// </summary>
        private static void CarryPaintingOntoCaseGrid(StepContext ctx, PaintingFacts painting)
        {
            if (string.IsNullOrEmpty(painting.File)) return;
            string gfi = GuiFiles.Resolve(ctx.Root, painting.File);
            string caseDem = GuiFiles.Resolve(ctx.Root, ScenarioFiles.CaseInput(ctx.Input, "dem.tif"));
            if (gfi == null || !File.Exists(gfi) || caseDem == null || !File.Exists(caseDem))
            {
                return;
            }

            try
            {
                PaintedAreasInfo header = PaintedAreasInfo.Read(gfi);
                PREACT.Utility.PaintedMaskResampler.Grid caseGrid = PREACT.Utility.PaintedMaskResampler.Grid.FromRaster(caseDem);
                if (header.Width == caseGrid.Ncols && header.Height == caseGrid.Nrows)
                {
                    return;
                }

                foreach (string candidate in painting.Grids)
                {
                    string path = GuiFiles.Resolve(ctx.Root, candidate);
                    if (path == null || !File.Exists(path)) continue;
                    PREACT.Utility.PaintedMaskResampler.Grid grid = PREACT.Utility.PaintedMaskResampler.Grid.FromRaster(path);
                    if (grid.Ncols != header.Width || grid.Nrows != header.Height) continue;

                    LogStep($"The painting ({header.Width} x {header.Height}) was on the grid of {candidate}, and the case is now "
                        + $"{caseGrid.Ncols} x {caseGrid.Nrows}; moving it onto the case grid too.");
                    //Unsaved strokes are newer than the file, and are carried on their own (CarryUnsavedStrokes).
                    MovePainting(ctx, painting.File, candidate, ScenarioFiles.CaseInput(ctx.Input, "dem.tif"),
                        replaceUnsavedStrokes: false);
                    return;
                }

                LogStep($"The painting in {painting.File} is {header.Width} x {header.Height} cells and the case grid "
                    + $"{caseGrid.Ncols} x {caseGrid.Nrows}; none of the grids this build knows has its size, so it was not "
                    + "moved. Step 6 (Fire areas) says what to do.");
            }
            catch (Exception e)
            {
                //The build itself succeeded; the painting file is only not carried along, and step 6 says so.
                LogStep("Could not move the painting onto the case grid: " + e.Message);
            }
        }

        /// <summary>
        /// Moves the scenario's painting from the grid of <paramref name="sourceGrid"/> onto that of
        /// <paramref name="targetGrid"/> (both scenario-relative), as a new file beside it; the original is kept.
        /// The scenario then names the new file (unsaved until the scenario is saved).
        /// </summary>
        public static void MovePaintingToGrid(string sourceGrid, string targetGrid)
        {
            string painting = Input?.WildfireModule?.GraphicalFireInputFile;
            if (string.IsNullOrEmpty(painting) || string.IsNullOrEmpty(sourceGrid) || string.IsNullOrEmpty(targetGrid))
            {
                return;
            }

            RunStep("Moving the painting onto the fire-case grid", WorkflowStepId.FireAreas, c =>
            {
                MovePainting(c, painting, sourceGrid, targetGrid, replaceUnsavedStrokes: true);
                return Task.CompletedTask;
            });
        }

        /// <summary>On the worker: the move itself, and the scenario's reference to the new file once it has succeeded.</summary>
        private static void MovePainting(StepContext ctx, string painting, string sourceGrid, string targetGrid, bool replaceUnsavedStrokes)
        {
            string gfi = GuiFiles.Resolve(ctx.Root, painting);
            string target = GuiFiles.Resolve(ctx.Root, targetGrid);
            PREACT.Utility.PaintedMaskResampler.Grid grid = PREACT.Utility.PaintedMaskResampler.Grid.FromRaster(target);
            string output = PREACT.Utility.PaintedMaskResampler.NewFileName(gfi, grid.Ncols, grid.Nrows);

            PREACT.Utility.PaintedMaskResampler.Result moved = PREACT.Utility.PaintedMaskResampler.ResampleFile(
                gfi, GuiFiles.Resolve(ctx.Root, sourceGrid), target, output);
            foreach (string line in moved.Describe())
            {
                LogStep(line);
            }

            //Beside the original, in the same (relative) folder.
            string folder = Path.GetDirectoryName(painting.Replace('\\', '/'))?.Replace('\\', '/');
            string relative = string.IsNullOrEmpty(folder) ? Path.GetFileName(output) : folder + "/" + Path.GetFileName(output);
            ctx.Set(i => i.WildfireModule.GraphicalFireInputFile = relative);
            ctx.Then(() => ReloadPaintedAreas(ctx.Input, replaceUnsavedStrokes));
            LogStep("The scenario now names " + relative + " as its painted areas; save the scenario to keep that.");
        }

        /// <summary>
        /// On the main thread: the scenario's painted areas read again from the file it names, and the painter's
        /// fire textures dropped so they are drawn from them.
        /// </summary>
        private static void ReloadPaintedAreas(PREACTInput input, bool replaceUnsavedStrokes)
        {
            if (input == null || input != ScenarioSession.Input) return;
            global::WUInity.Painter painter = PreactGUI.WUInity?.Painter;
            if (!replaceUnsavedStrokes && painter != null && painter.UnsavedFireStrokes) return;

            string path = GuiFiles.Resolve(input.RootFolder, input.WildfireModule.GraphicalFireInputFile);
            if (path == null || !File.Exists(path)) return;

            input.WildfireModule.Data.LoadGraphicalFireInput(input.WildfireModule, path, false, out bool _);
            PreactGUI.WUInity?.Painter?.ReloadFireAreas();
        }

        /// <summary>
        /// [Landscape] elevation, slope and aspect become the case's dem/slp/asp.tif, when the case has them, and
        /// the terrain is read again. A case build does this itself; this is for a scenario whose case was built
        /// before it did (the workflow's "Use the case terrain").
        /// </summary>
        public static void AdoptCaseTerrain(PREACTInput input)
        {
            if (input == null || !string.IsNullOrEmpty(input.Landscape.LandscapeFile)) return;

            string dem = ScenarioFiles.CaseInput(input, "dem.tif");
            string slp = ScenarioFiles.CaseInput(input, "slp.tif");
            string asp = ScenarioFiles.CaseInput(input, "asp.tif");

            if (!File.Exists(Path.Combine(input.RootFolder, dem))) return;

            if (input.Landscape.ElevationFile != dem)
            {
                LogStep($"[Landscape] now uses the fire case's terrain ({dem}) instead of {Show(input.Landscape.ElevationFile)}, "
                    + "so the map, painting, the evacuation groups and k-PERIL's slope are all on the case grid.");
                input.Landscape.ElevationFile = dem;
            }
            if (File.Exists(Path.Combine(input.RootFolder, slp))) input.Landscape.SlopeFile = slp;
            if (File.Exists(Path.Combine(input.RootFolder, asp))) input.Landscape.AspectFile = asp;

            if (input == ScenarioSession.Input)
            {
                PreactGUI.WUInity?.ReloadLandscape();
            }
        }

        /// <summary>
        /// [kPERIL] WuiAreaFile becomes the case's wui_area.tif - only one written since
        /// <paramref name="builtSinceUtc"/>, since an older one may be from masks painted since replaced. A case
        /// build sets it itself; this is for a scenario whose case was built before it did.
        /// </summary>
        public static void AdoptCaseWuiArea(PREACTInput input, DateTime builtSinceUtc = default)
        {
            kPERILInput peril = input?.TriggerBufferModule?.kPERILInput;
            if (peril == null || peril.WuiAreaSource != kPERILInput.WuiAreaSources.Raster) return;

            string wui = ScenarioFiles.CaseInput(input, "wui_area.tif");
            string wuiPath = Path.Combine(input.RootFolder, wui);
            //default means "whatever the case has" (asked for by hand); DateTime.MinValue cannot be moved back 5 s.
            bool recent = builtSinceUtc == default || File.GetLastWriteTimeUtc(wuiPath) >= builtSinceUtc.AddSeconds(-5);
            if (File.Exists(wuiPath) && recent && peril.WuiAreaFile != wui)
            {
                LogStep($"[kPERIL] WuiAreaFile now names {wui}, the painted WUI area the case build put on its grid.");
                peril.WuiAreaFile = wui;
            }
        }

        private static string Show(string path) => string.IsNullOrEmpty(path) ? "nothing" : path;

        // ------------------------------------------------------------------ what the workflow and windows call

        /// <summary>Everything the road network needs, skipping what exists: OSM, the RouterDb, and the SUMO network.</summary>
        public static void PrepareRoads(bool redo)
        {
            string sumo = PreactGUI.Engine?.SumoPath;
            RunChain(redo ? "Rebuilding the road network" : "Preparing the road network", WorkflowStepId.Roads,
                new ChainLink("Downloading OSM roads", c => redo || !c.Has(ScenarioFiles.Osm(c.Name)), DoDownloadOsm),
                new ChainLink("Building the RouterDb", c => redo || !c.Has(ScenarioFiles.RouterDb(c.Name)), DoBuildRouterDb),
                new ChainLink("Building the SUMO network", c => c.TrafficEnabled && (redo || !c.Has(ScenarioFiles.SumoConfig)),
                    c => DoBuildSumoNetwork(c, sumo)));
        }

        /// <summary>Everything households need, skipping what exists: WorldPop, the roads they leave by, and the population.</summary>
        public static void PreparePopulation(bool redo)
        {
            RunChain(redo ? "Regenerating the population" : "Preparing the population", WorkflowStepId.Population,
                new ChainLink("Downloading WorldPop", c => redo || !c.Has(ScenarioFiles.WorldPopUtm(c.Name)), DoDownloadWorldPop),
                new ChainLink("Downloading OSM roads", c => !c.Has(ScenarioFiles.RouterDb(c.Name)) && !c.Has(ScenarioFiles.Osm(c.Name)), DoDownloadOsm),
                new ChainLink("Building the RouterDb", c => !c.Has(ScenarioFiles.RouterDb(c.Name)), DoBuildRouterDb),
                new ChainLink("Generating households", c => redo || !PopulationIsSet(c), DoGeneratePopulation));
        }

        private static bool PopulationIsSet(StepContext c)
        {
            string recorded = c.Input.Population.PopulationFile;
            string path = GuiFiles.Resolve(c.Root, recorded);
            return !string.IsNullOrEmpty(recorded) && File.Exists(path) && ScenarioFiles.CountPopulationRows(path) > 0;
        }

        public static void DownloadLandfireFuels()
        {
            RunStep("Downloading LANDFIRE fuels and canopy", WorkflowStepId.Fuels, DoDownloadLandfire);
        }

        public static void BuildElmfireCase(bool rebuildExisting = false)
        {
            //The build downloads its terrain with the engine's key; a key typed in for this session is handed to it.
            ApplySessionOpenTopographyKey();
            PaintingFacts painting = PaintingFacts.Capture(Input);
            RunStep(rebuildExisting ? "Rebuilding the fire case" : "Building the fire case", WorkflowStepId.FireCase,
                c => DoBuildElmfireCase(c, rebuildExisting, painting));
        }

        /// <summary>
        /// Builds the case again, keeping its layers (unless its grid has to be re-cut), so newly painted masks
        /// become its ignition_mask.tif and wui_area.tif ("Apply to case"). The namelist is written again too.
        /// </summary>
        public static void ApplyPaintedAreasToCase()
        {
            ApplySessionOpenTopographyKey();
            PaintingFacts painting = PaintingFacts.Capture(Input);
            RunStep("Applying the painted areas to the fire case", WorkflowStepId.FireAreas, c => DoBuildElmfireCase(c, false, painting));
        }

        public static void DownloadDemOnly()
        {
            RunStep("Downloading a DEM", WorkflowStepId.FireCase, DoDownloadDem);
        }

        public static void DownloadWeatherOnly()
        {
            RunStep("Downloading Open-Meteo weather", WorkflowStepId.PlaceAndTime, DoDownloadWeather);
        }
    }
}
