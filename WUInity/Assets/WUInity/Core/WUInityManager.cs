//This file is part of WUIPlatform Copyright (C) 2024 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;       
using UnityEngine;
using PREACT.Input;                    
using PREACT.Traffic;                          
using System.IO;
using PREACT;
using PREACT.Population;
using WUInity.Visualization;
using Assets.WUInity.GUI.DearIMGUI;
using Mapbox.Utils;
using ImGuiNET;

namespace WUInity
{
    [RequireComponent(typeof(EvacuationRenderer))]
    [RequireComponent(typeof(FireRenderer))]
    public class WUInityManager : MonoBehaviour, IExternalManager                     
    {
        public EvacuationRenderer EvacuationRenderer
        {
            get
            {
                if (_evacuationRenderer == null)
                {
                    _evacuationRenderer = GetComponent<EvacuationRenderer>();
                    if(_evacuationRenderer == null)
                    {
                        _evacuationRenderer = gameObject.AddComponent<EvacuationRenderer>();
                    }
                }
                return _evacuationRenderer;
            }
        }

        public FireRenderer FireRenderer
        {
            get
            {
                if (_fireRenderer == null)
                {
                    _fireRenderer = GetComponent<FireRenderer>();
                    if (_fireRenderer == null)
                    {
                        _fireRenderer = gameObject.AddComponent<FireRenderer>();
                    }
                }
                return _fireRenderer;
            }
        }

        private Painter _painter;
        public Painter Painter{ get => _painter; }

        [SerializeField] private OverviewCamera _godCamera;

        [Header("Options")]
        public bool SuppressMessages = false;

        [Header("Prefabs")]        
        [SerializeField] private GameObject _destinationMarkerPrefab;
        [SerializeField] private GameObject _wildfireIgnitionMarkerPrefab;

        [Header("References")]
        [SerializeField] private Mapbox.Examples.QuadTreeCameraMovement _webMercatorCameraMovement;
        [SerializeField] private Mapbox.Unity.Map.AbstractMap _utmMap;
        public Mapbox.Unity.Map.AbstractMap UTMMap { get => _utmMap; }
        [SerializeField] private Mapbox.Unity.Map.AbstractMap _webMercatorMap;
        [SerializeField] private LineRenderer _simBorder;
        [SerializeField] private LineRenderer _boundingBoxRenderer;

        private PreactGUI _wuiGUI;
        PREACTInput _input;
        public PREACTInput PREACTInput { get => _input; }


        private FireRenderer _fireRenderer;
        private EvacuationRenderer _evacuationRenderer;
        private SimulationDomainVisualizerUnity _simulationDomainVisualizer;
        private FireDomainVisualizerUnity _fireDomainVisualizer;
        private RoadNetworkVisualizerUnity _roadNetworkVisualizer;
        //Read once and kept: parsing a town's net.xml takes a moment, and both the drawing and the
        //destination snapping want the same lanes. Dropped when the scenario changes.
        private PREACT.Utility.SumoNetworkGeometry _roadNetwork;
        private bool _roadNetworkBuilt;

        public SimulationDomainVisualizerUnity SimulationDomainVisualizer { get => _simulationDomainVisualizer; }
        public FireDomainVisualizerUnity FireDomainVisualizer { get => _fireDomainVisualizer; }
        
        bool _renderHouseholds = false;
        bool _renderTraffic = false;
        bool _renderSmokeDispersion = false;
        bool _renderFireSpread = false;        

        Engine _engine;
        public Engine Engine { get => _engine; }
        private void Awake()
        {
            //Checked before anything is dereferenced. A missing reference here used to throw a bare
            //NullReferenceException part-way through Awake, which left _engine unassigned and made
            //Update() throw on every frame from then on - so the visible error was dozens of lines
            //away from the cause, repeated forever, and it silently disabled everything Update
            //drives, including picking the area of interest on the map.
            if (!ValidateSceneReferences())
            {
                return;
            }

            _simBorder.gameObject.SetActive(false);
            _boundingBoxRenderer.gameObject.SetActive(false);

            //gui            
            _wuiGUI = FindAnyObjectByType<PreactGUI>();
            if (_wuiGUI == null)
            {
                //Found rather than assigned, so it goes missing if the GUI object is absent or
                //disabled. Reported here for the same reason as the references above: otherwise it
                //surfaces as an unexplained NullReferenceException several lines later.
                Debug.LogError($"{nameof(WUInityManager)} cannot start: no active {nameof(PreactGUI)} was found in the scene.", this);
                return;
            }

            //map
            _utmMap.gameObject.SetActive(false);
            _webMercatorMap.gameObject.SetActive(true);
            SetWebMercatorMapInteraction(false);

            _engine = new Engine(this);
            _wuiGUI.SetManager(this, _engine);

            _painter = FindFirstObjectByType<Painter>();
            if (_painter == null)
            {
                GameObject g = new GameObject();
                g.transform.parent = transform;
                g.name = "WUI Painter";
                _painter = g.AddComponent<Painter>();
                g.SetActive(false);
            }
            //Never called before, so the painter's _manager stayed null and every path through
            //CheckDataResources threw a NullReferenceException on the first thing it reads from it -
            //which is what happened as soon as a paint mode needed a texture built.
            _painter.SetManager(this);

            _godCamera = FindFirstObjectByType<OverviewCamera>();
            if (_godCamera == null)
            {
                GameObject g = new GameObject();
                g.transform.parent = transform;
                g.name = "GodCamera";
                _godCamera = g.AddComponent<OverviewCamera>();
            }
            _godCamera.SetManager(this);

            _simulationDomainVisualizer = new SimulationDomainVisualizerUnity(transform);
            _fireDomainVisualizer = new FireDomainVisualizerUnity(transform);
            _roadNetworkVisualizer = new RoadNetworkVisualizerUnity(transform);
        }

        /// <summary>
        /// Verifies the scene references Awake depends on, naming any that are unassigned rather
        /// than failing with a NullReferenceException that points at whichever line happened to
        /// touch one first. Unity drops these silently when a component is reserialized, so this
        /// is a realistic failure rather than a theoretical one.
        /// </summary>
        private bool ValidateSceneReferences()
        {
            string missing = null;

            void Require(object reference, string name)
            {
                if (reference == null || reference.Equals(null))
                {
                    missing = missing == null ? name : missing + ", " + name;
                }
            }

            Require(_simBorder, nameof(_simBorder));
            Require(_boundingBoxRenderer, nameof(_boundingBoxRenderer));
            Require(_utmMap, nameof(_utmMap));
            Require(_webMercatorMap, nameof(_webMercatorMap));
            Require(_webMercatorCameraMovement, nameof(_webMercatorCameraMovement));
            //_wuiGUI is deliberately not checked here: it is found at runtime further down in
            //Awake, so it is legitimately null at this point.

            if (missing == null)
            {
                return true;
            }

            Debug.LogError(
                $"{nameof(WUInityManager)} cannot start: the following are not assigned in the scene: {missing}. " +
                "Select the object holding this component and set them in the Inspector. " +
                "Nothing else in this component will run until they are.", this);
            return false;
        }

        private void Start()
        {
            //The scenario from last time. Opening a session on the case that was being worked on is nearly
            //always what is wanted. Nothing else is opened automatically: the example this used to fall back
            //to (Examples/Development) no longer exists, and every editor window used to pop up on a load too.
            if (_engine != null && RecentScenario.Have)
            {
                string recent = RecentScenario.Path;
                if (ScenarioSession.Load(recent))
                {
                    Engine.Message(null, Engine.LogType.Log, "Reopened " + recent
                        + ". File > Open recent lists the others; File > Close stops it being reopened next time.");
                }
            }
        }

        private void OnApplicationQuit()
        {
            if (_engine != null)
            {
                _engine.CloseSimulations(false);
            }            
        }

        void Update()
        {
            //Awake bailed out (it logs why), so there is nothing to drive. Returning keeps the
            //console readable instead of repeating the same NullReferenceException every frame.
            if (_engine == null)
            {
                return;
            }

            UpdateWebMercatorMapInteraction();
            WatchRunTask();
            UpdateFireGridOutline();

            //always update visuals, even when paused
            if (_engine.Simulation != null)
            {
                if (_engine.Simulation.State == Simulation.SimulationState.Running)
                {
                    if (!_visualsExist)
                    {
                        CreateVisualizers();
                    }
                    EvacuationRenderer.UpdateEvacuationRenderer(_renderHouseholds, _renderTraffic, _engine.Simulation.Evacuation.PedestrianModule, _engine.Simulation.Evacuation.TrafficModule);
                    FireRenderer.UpdateFireRenderer(_renderFireSpread, _renderSmokeDispersion, _engine.Simulation);
                }
            }   

            //The map is panned by left-dragging it, so a press that moved is a pan and not a pick.
            //Both picking modes therefore act on release, and only when the pointer stayed put.
            if (Input.GetMouseButtonDown(0))
            {
                _mouseDownPos = Input.mousePosition;
            }
            bool clickedOnMap = Input.GetMouseButtonUp(0)
                                && !ImGui.GetIO().WantCaptureMouse
                                && (Input.mousePosition - _mouseDownPos).sqrMagnitude < _clickSlop * _clickSlop;

            if(_pickingPos)
            {
                //Abandoning the pick has to be possible: otherwise the next click anywhere on the map
                //moves whatever was being placed, with no way back.
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    _pickingPos = false;
                    _onClick = null;
                    NewLogMessage("Picking a position on the map was cancelled.");
                    //The window that asked closed itself to get out of the way; this brings it back, with
                    //whatever was being edited, instead of leaving it gone.
                    System.Action cancelled = _onPickCancelled;
                    _onPickCancelled = null;
                    cancelled?.Invoke();
                }
                //collect click
                else if (clickedOnMap)
                {
                    Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                    if (_yPlane.Raycast(ray, out float enter))
                    {
                        Vector3 pos = ray.GetPoint(enter);
                        FinishPickPosOnMap(pos);
                    }
                }
            }
            else if(_pickingBoundingBox)
            {
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    _boundingBoxRenderer.gameObject.SetActive(false);
                    _pickingBoundingBox = false;
                    _onClicks = null;
                    NewLogMessage("Picking the area of interest was cancelled.");
                    System.Action cancelled = _onPickCancelled;
                    _onPickCancelled = null;
                    cancelled?.Invoke();
                    return;
                }

                //collect clicks
                if(clickedOnMap)
                {
                    Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                    if (_yPlane.Raycast(ray, out float enter))
                    {
                        Vector3 pos = ray.GetPoint(enter);
                        var clickLatLon = _webMercatorMap.WorldToGeoPosition(pos);
                        //A second corner that makes no area - the same spot clicked twice, a double click - is not taken
                        //(it was, on Auburn2, and the pick ended with an area of nothing); the pick says why and waits.
                        //Reported either way, because a bounding box that never completes is otherwise silent.
                        PREACT.Utility.AreaOfInterestPick.Outcome outcome = _areaPick.Click(
                            new PREACT.Math.Vector2d(clickLatLon.x, clickLatLon.y), UnityEngine.Time.realtimeSinceStartupAsDouble);
                        NewLogMessage(_areaPick.Message);
                        if (outcome == PREACT.Utility.AreaOfInterestPick.Outcome.Done)
                        {
                            FinishPickBoundingBoxOnMap();
                        }
                    }
                }                

                //update bounding box
                if (_areaPick.Corners == 1)
                {
                    Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
                    float enter;
                    if (_yPlane.Raycast(ray, out enter))
                    {
                        Vector3 pos = ray.GetPoint(enter);
                        //The first corner is re-derived from its latitude and longitude every frame
                        //rather than remembered as a world position, because the map may be panned or
                        //zoomed between the two clicks, which moves world space under the geography.
                        //Freezing the map after the first click is what this used to do instead.
                        Vector3 corner = _webMercatorMap.GeoToWorldPosition(
                            new Vector2d(_areaPick.First.x, _areaPick.First.y), false);
                        _boundingBoxRenderer.SetPosition(0, new Vector3(corner.x, 10f, corner.z));
                        _boundingBoxRenderer.SetPosition(1, new Vector3(pos.x, 10f, corner.z));
                        _boundingBoxRenderer.SetPosition(2, new Vector3(pos.x, 10f, pos.z));
                        _boundingBoxRenderer.SetPosition(3, new Vector3(corner.x, 10f, pos.z));
                    }
                }
            }            
        }

        Plane _yPlane = new Plane(Vector3.up, 0f);

        public void UpdateDestinationForVehicles(Vector3 boundingBoxPoint1, Vector3 boundingBoxPoint2, Vector3 manualDestination)
        {
            PREACT.Math.Vector2d lowerLeft = new PREACT.Math.Vector2d(Mathf.Min(boundingBoxPoint1.x, boundingBoxPoint2.x), Mathf.Min(boundingBoxPoint1.z, boundingBoxPoint2.z));
            PREACT.Math.Vector2d upperRight = new PREACT.Math.Vector2d(Mathf.Max(boundingBoxPoint1.x, boundingBoxPoint2.x), Mathf.Max(boundingBoxPoint1.z, boundingBoxPoint2.z));
            PREACT.Math.Vector2d simulationPos = new PREACT.Math.Vector2d(manualDestination.x, manualDestination.z);

            List<TrafficModuleVehicle> vehicles = _engine.Simulation.Evacuation.TrafficModule.GetVehiclesInBoundingBox(lowerLeft, upperRight);
            if(vehicles.Count > 0)
            {
                PREACT.Math.Vector2d wgs84 = _engine.Simulation.Input.Simulation.Data.GetWGS84FromSimulationPosition(simulationPos);
                PREACT.Evacuation.EvacuationDestination eD = _engine.Simulation.Evacuation.AddRuntimeDestination(wgs84);
                _engine.Simulation.Evacuation.TrafficModule.SetManualDestination(vehicles, simulationPos, eD);
            }           
        }

        //The task RunSimulations returns, which is what the GUI follows to know a run is going on. It used to
        //be discarded, so an exception from a module was lost with it, and Simulation.IsRunning - which a run
        //that throws never resets - was left to say whether one was still going: after a crash, forever.
        private System.Threading.Tasks.Task _runTask;
        private bool _runTaskReported = true;

        /// <summary>A run started from the GUI has not finished yet, however it is going to finish.</summary>
        public bool IsSimulationActive { get => _runTask != null && !_runTask.IsCompleted; }

        /// <summary>
        /// The engine's worker is still running the simulation - the part of <see cref="IsSimulationActive"/> that reads
        /// the scenario. Set and cleared by the worker, so it also turns false while the main thread is blocked, when the
        /// run's task cannot complete (its completion waits for Unity's player loop); see
        /// <see cref="Engine.IsRunningSimulations"/>.
        /// </summary>
        public bool IsSimulationWorking { get => IsSimulationActive && _engine != null && _engine.IsRunningSimulations; }

        /// <summary>Whether the last GUI run ended in an error, or null when there has been none.</summary>
        public bool? LastRunFailed { get; private set; }

        /// <summary>
        /// The last GUI run never started: the engine refused the scenario (its checklist had a critical item, or
        /// there was no input). Also counted in <see cref="LastRunFailed"/>.
        /// </summary>
        public bool LastRunRefused { get; private set; }

        /// <summary>What the engine was still missing when it refused the last run, as the parser words it.</summary>
        public System.Collections.Generic.IReadOnlyList<string> LastRunRefusalReasons { get => _refusalReasons; }
        private readonly List<string> _refusalReasons = new List<string>();

        //The simulation the engine held before the run, to tell a run that never made one; and a stop asked for.
        private Simulation _simulationBeforeRun;
        private bool _stopRequestedThisRun;

        /// <summary>Raised on the main thread when a GUI-started run has finished, however it finished.</summary>
        public event System.Action RunFinished;

        public void RunSimulation(EngineTask engineTask)
        {
            if (IsSimulationActive)
            {
                Engine.Message(null, Engine.LogType.Warning, "A simulation is already running.");
                return;
            }

            _visualsExist = false;
            _runTaskReported = false;
            LastRunFailed = null;
            LastRunRefused = false;
            _refusalReasons.Clear();
            _stopRequestedThisRun = false;
            _simulationBeforeRun = _engine.Simulation;
            _runTask = _engine.RunSimulations(engineTask);

            //The engine refuses before its first await, so a refused run is already complete here - and the
            //checklist it judged is still the one published.
            if (_runTask.IsCompleted && !_engine.LastRunSucceeded && ReferenceEquals(_engine.Simulation, _simulationBeforeRun))
            {
                CaptureRefusalReasons();
            }
        }

        private void CaptureRefusalReasons()
        {
            _refusalReasons.Clear();
            foreach (PREACTInput.InputRequirement requirement in PREACTInput.Requirements)
            {
                if (requirement.Critical)
                {
                    _refusalReasons.Add(requirement.ToString() + (string.IsNullOrEmpty(requirement.Message) ? "" : " - " + requirement.Message));
                }
            }
            if (_refusalReasons.Count == 0)
            {
                _refusalReasons.Add(_input == null ? "No scenario was handed to the engine." : "The console says why.");
            }
        }

        /// <summary>
        /// Notices a run finishing: reports an exception it ended with, and tells whoever is listening.
        /// Polled from Update, which is the main thread, so listeners can touch the GUI and the scenario.
        /// </summary>
        private void WatchRunTask()
        {
            if (_runTaskReported || _runTask == null || !_runTask.IsCompleted)
            {
                return;
            }
            _runTaskReported = true;

            bool failed = false;
            if (_runTask.IsFaulted)
            {
                failed = true;
                System.Exception e = _runTask.Exception != null ? _runTask.Exception.GetBaseException() : null;
                Engine.Message(null, Engine.LogType.Exception, "The run stopped with an error"
                    + (e != null ? ": " + e.GetType().Name + ": " + e.Message : ".")
                    + " The GUI is usable again; see the console for what led up to it.");
                if (e != null)
                {
                    Debug.LogException(e);
                }
            }
            else if (!_engine.LastRunSucceeded && ReferenceEquals(_engine.Simulation, _simulationBeforeRun))
            {
                //No simulation was made: the engine refused the scenario before starting, and returned normally.
                //Reported as what it is, not as a run that "finished in 0.0 min".
                failed = true;
                LastRunRefused = true;
                if (_refusalReasons.Count == 0) CaptureRefusalReasons();
                Engine.Message(null, Engine.LogType.Warning, "The run did not start: the engine refused the scenario ("
                    + string.Join("; ", _refusalReasons) + ").");
            }
            else if (_engine.Simulation != null
                     && (_engine.Simulation.State == Simulation.SimulationState.Error || _engine.Simulation.StoppedDueToError))
            {
                failed = true;
            }
            else if (!_engine.LastRunSucceeded && !_stopRequestedThisRun)
            {
                //Ran, but reported errors (LastRunErrorCount) without a simulation in the Error state.
                failed = true;
            }

            _simulationBeforeRun = null;
            LastRunFailed = failed;
            RunFinished?.Invoke();
        }

        bool _visualsExist = false;
        public void CreateVisualizers()
        {
            //this needs to be done AFTER simulation has started since we need some data from the sim
            //fix everything for evac rendering
            EvacuationRenderer.CreateBuffers(_input.PedestrianModule.Enabled, _input.TrafficModule.Enabled, _input.Simulation.DomainSize, _engine.Simulation.Evacuation.PedestrianModule);            

            _renderHouseholds = _input.PedestrianModule.Enabled;
            _renderTraffic = _input.TrafficModule.Enabled;

            //and then for fire rendering
            FireRenderer.CreateBuffers(_engine.Simulation);
            _renderFireSpread = _input.WildfireModule.Enabled;
            _renderSmokeDispersion = _input.SmokeModule.Enabled;

            _visualsExist = true;

            ActivateSuitableVisuals();
        }

        public void StopSimulations()
        {
            _stopRequestedThisRun = IsSimulationActive;
            HideAllRuntimeVisuals();
            _engine.CloseSimulations(false);
        }

        public void UpdateSimBorders()
        {
            _simBorder.gameObject.SetActive(true);
            Vector3 upOffset = Vector3.up * 50f;
            _simBorder.SetPosition(0, Vector3.zero + upOffset);
            _simBorder.SetPosition(1, _simBorder.GetPosition(0) + Vector3.right * (float)_input.Simulation.DomainSize.x);
            _simBorder.SetPosition(2, _simBorder.GetPosition(1) + Vector3.forward * (float)_input.Simulation.DomainSize.y);
            _simBorder.SetPosition(3, _simBorder.GetPosition(2) - Vector3.right * (float)_input.Simulation.DomainSize.x);
            _simBorder.SetPosition(4, _simBorder.GetPosition(0));   
        }

        /// <summary>
        /// Whether the brush is live. The single source of truth for it.
        /// </summary>
        /// <remarks>
        /// The two paint windows each kept their own <c>_painting</c> bool as well, so the app had three
        /// answers to "is painting on?" and nothing reconciled them: painting in both windows and closing one
        /// left the other still offering "Stop painting" for a brush that was already down, and pressing it
        /// stopped a brush that was not running. Both windows now read this instead of remembering.
        /// </remarks>
        public bool IsPainterActive()
        {
            return _painter != null && _painter.gameObject.activeSelf;
        }

        /// <summary>True when the brush is live and writing the mask this mode names.</summary>
        public bool IsPaintingMode(Painter.PaintMode mode)
        {
            return IsPainterActive() && _painter.GetPaintMode() == mode;
        }

        /// <summary>
        /// True when the brush is live in any of the fire-grid area modes — what the fire paint window owns,
        /// as against the evacuation-group window.
        /// </summary>
        public bool IsPaintingFireArea
        {
            get
            {
                if (!IsPainterActive()) return false;

                return _painter.GetPaintMode() == Painter.PaintMode.RandomIgnitionArea;
            }
        }

        public void StartPainter(Painter.PaintMode paintMode)
        {
            Painter.gameObject.SetActive(true);
            Painter.SetPainterMode(paintMode);
            bool fireEdit = false;
            if (paintMode == Painter.PaintMode.RandomIgnitionArea)
            {
                fireEdit = true;
                DisplayRandomIgnitionAreaMap();
            }
            //Groups are painted on the fire grid, the same grid the other three use, so this belongs
            //with them. It used to fall through to the warning below, which meant the painter was
            //never switched on for it and the map plane was never shown.
            else if (paintMode == Painter.PaintMode.EvacGroup)
            {
                fireEdit = true;
                DisplayEvacGroupMap();
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, "Paint mode not set correctly.");
            }
            if(fireEdit)
            {
                _simulationDomainVisualizer.SetVisibility(false);
                _fireDomainVisualizer.SetVisibility(true);
            }
            else
            {
                _simulationDomainVisualizer.SetVisibility(true);
                _fireDomainVisualizer.SetVisibility(false);
            }
        }

        public void StopPainter()
        {
            Painter.gameObject.SetActive(false);
            _simulationDomainVisualizer.SetVisibility(false);
            _fireDomainVisualizer.SetVisibility(false);
        }
        
                
        public void ActivateSuitableVisuals()
        {
            if(_input.PedestrianModule.Enabled)
            {
                SetHouseholdRendering(true);
            }

            if (_input.TrafficModule.Enabled)
            {
                SetTrafficRendering(true);
            }

            if (_input.WildfireModule.Enabled)
            {
                SetFireSpreadRendering(true);
            }

            if (_input.SmokeModule.Enabled)
            {
                SetSootRendering(true);
            }
        }

        public void HideAllRuntimeVisuals()
        {
            SetHouseholdRendering(false);
            SetTrafficRendering(false);
            SetFireSpreadRendering(false);
            SetSootRendering(false);
        }

        /// <summary>
        /// Leaves the painted evacuation groups on the map, dimmed, with the brush switched off.
        ///
        /// Wanted after painting stops: the areas are the point of the exercise, and having them vanish the
        /// moment the brush is put down means checking them against roads, the fire, or each other requires
        /// picking the brush back up. Dimmed rather than at painting opacity so what is underneath stays
        /// readable.
        /// </summary>
        public bool ShowEvacGroupOverlay(float opacity = 0.3f)
        {
            Texture2D overlay = Painter.BuildEvacGroupOverlayTexture(opacity);
            if (overlay == null)
            {
                return false;
            }

            if (!Painter.TryGetPaintGrid(out PREACT.Math.Vector2d gridSize, out PREACT.Math.Vector2d gridOrigin))
            {
                return false;
            }

            _fireDomainVisualizer.EnsurePlane(gridSize, gridOrigin);
            _fireDomainVisualizer.SetLCPPlaneTexture(overlay);
            _fireDomainVisualizer.SetVisibility(true);
            return true;
        }

        public void HideEvacGroupOverlay()
        {
            _fireDomainVisualizer.SetVisibility(false);
        }

        public void DisplayEvacGroupMap()
        {
            //On the wildfire domain plane with the other three, not the simulation domain plane it used to
            //go to. Groups are painted on the fire grid, and StartPainter shows the wildfire plane and hides
            //the simulation one for every fire-grid paint mode - so the texture was being put on the plane
            //that had just been hidden, on a plane sized for the population map, which itself only exists
            //once a population map has been displayed.
            ShowPaintedTexture(Painter.GetEvacGroupTexture(), "the evacuation group areas");
        }

        /// <summary>
        /// Shows population density over the domain. The renderer for this already existed, complete
        /// with its density colour ramp; nothing ever called it, because WorkingData.PopulationMap is
        /// not assigned anywhere - so the map is loaded here from the scenario's population file on
        /// first use, and cached on WorkingData by the renderer itself.
        /// </summary>
        public bool DisplayPopulationDensityMap()
        {
            PREACT.Population.PopulationMap map = _engine.WorkingData.PopulationMap;

            if (map == null || !map.HaveData)
            {
                if (PREACTInput == null || string.IsNullOrEmpty(PREACTInput.Population.PopulationFile))
                {
                    PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning, "No population file is set for this scenario, so there is no population to show.");
                    return false;
                }

                string path = System.IO.Path.Combine(PREACTInput.RootFolder, PREACTInput.Population.PopulationFile);
                if (!System.IO.File.Exists(path))
                {
                    PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning, "Population file not found: " + path);
                    return false;
                }

                map = LoadPopulationMapForDisplay(path);
                if (map == null)
                {
                    return false;
                }
            }

            _simulationDomainVisualizer.SetAndDisplayPopulationMapTexture(map, _engine.WorkingData);

            //The UTM map, not the web mercator one. The population plane is built in simulation
            //coordinates - metres from the simulation's UTM origin - and ShowWebMercatorMap hides the UTM
            //map, moves the camera into mercator mode and leaves the plane sitting in a frame nothing else
            //is in. The density was being drawn correctly and shown off the edge of the world.
            ShowUTMMap();
            _simulationDomainVisualizer.SetVisibility(true);
            return true;
        }

        /// <summary>
        /// Reads whichever of the two population formats the scenario's file actually is, and returns a
        /// density map, or null with the reason reported.
        ///
        /// A generated scenario's PopulationFile is a household CSV - the format the population step writes
        /// and the pedestrian module reads. The grid format PopulationMap.LoadFromFile expects is an older,
        /// separate thing whose only check is that the file has exactly nine lines, so handed a household
        /// CSV it says "Population data not valid for current map" and nothing appears. Every scenario built
        /// through the new scenario window landed in exactly that case.
        /// </summary>
        private PREACT.Population.PopulationMap LoadPopulationMapForDisplay(string path)
        {
            var map = new PREACT.Population.PopulationMap();

            //The grid format first, so scenarios that carry one keep behaving as they did.
            map.LoadFromFile(path, out bool loadedGrid);
            if (loadedGrid)
            {
                return map;
            }

            PREACT.Input.PopulationData.HouseholdData[] households =
                PREACT.Input.PopulationData.LoadPopulation(path, out int totalPopulation, out bool loadedHouseholds);

            if (!loadedHouseholds || households == null || households.Length == 0)
            {
                //LoadPopulation reports an empty file itself, but not what to do about it. This is a real
                //state: the population step used to write the CSV whether or not it could place anybody, so
                //a scenario can be pointing at a file holding nothing but its header.
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning,
                    "The population file holds no households: " + path + ". Re-run the population step "
                    + "(WorldPop, then RouterDb, then Generate population) - it now refuses to leave an empty "
                    + "file behind, so a file with only a header was written before that check existed.");
                return null;
            }

            //Cells of about a hectare, but never more than 256 to a side: a 16 km domain at 100 m is fine,
            //and the same code on a 100 km domain would otherwise ask for a million-cell texture.
            double longestSide = System.Math.Max(PREACTInput.Simulation.DomainSize.x, PREACTInput.Simulation.DomainSize.y);
            float cellSize = (float)System.Math.Max(100.0, longestSide / 256.0);

            map.CreateFromHouseholds(households, PREACTInput.Simulation.Data,
                PREACTInput.Simulation.LowerLeftLatLon, PREACTInput.Simulation.DomainSize, cellSize, out bool built);

            if (!built)
            {
                return null;
            }

            PREACT.Engine.Message(null, PREACT.Engine.LogType.Log,
                $"Showing {totalPopulation} people from {households.Length} households.");
            return map;
        }

        public void DisplayRandomIgnitionAreaMap()
        {
            ShowPaintedTexture(Painter.GetRandomIgnitionTexture(), "the random ignition area");
        }

        /// <summary>
        /// Shows a painted texture on the wildfire domain plane, making that plane cover the grid the
        /// texture is on first.
        ///
        /// Both paint modes work on the grid the painter resolved - the landscape's, or a DEM's, or the
        /// imported arrival times' - so all four are shown here, on the plane paint mode makes visible.
        /// The plane has to be built from that grid rather than from an LCP: a scenario whose terrain is
        /// only a DEM has no LCP, so there was no plane, and painting worked while showing nothing.
        /// </summary>
        private void ShowPaintedTexture(Texture2D texture, string what)
        {
            if (texture == null)
            {
                //The painter has said why there is no grid (once); saying it again here for every plane was the noise.
                if (!Painter.HasGridProblem)
                {
                    Engine.Message(null, Engine.LogType.Warning,
                        "Cannot show " + what + ": there is no grid to paint on. A landscape, a DEM or an imported "
                        + "fire's arrival times gives one.");
                }
                return;
            }

            //Fully qualified: Mapbox.Utils is imported here too, and has a Vector2d of its own.
            if (!Painter.TryGetPaintGrid(out PREACT.Math.Vector2d gridSize, out PREACT.Math.Vector2d gridOrigin))
            {
                Engine.Message(null, Engine.LogType.Warning, "Cannot show " + what + ": the paint grid is not known.");
                return;
            }

            _fireDomainVisualizer.EnsurePlane(gridSize, gridOrigin);
            _fireDomainVisualizer.SetLCPPlaneTexture(texture);
        }

        public void SetHouseholdRendering(bool enable)
        {
            if (_renderHouseholds != enable)
            {
                ToggleHouseholdRendering();
            }
        }

        public bool ToggleHouseholdRendering()
        {
            _renderHouseholds = !_renderHouseholds;
            return _renderHouseholds;
        }

        public void SetTrafficRendering(bool enable)
        {
            if(_renderTraffic != enable)
            {
                ToggleTrafficRendering();
            }
        }

        public bool ToggleTrafficRendering()
        {
            _renderTraffic = !_renderTraffic;
            return _renderTraffic;
        }

        public void SetSootRendering(bool enable)
        {
            if(enable != _renderSmokeDispersion)
            {
                ToggleSootRendering();
            }
        }

        public bool ToggleSootRendering()
        {
            _renderSmokeDispersion = FireRenderer.ToggleSoot(_input);
            return _renderSmokeDispersion;
        }

        public void SetFireSpreadRendering(bool enable)
        {
            if(enable != _renderFireSpread)
            {
                ToggleFireSpreadRendering();
            }
        }

        public bool ToggleFireSpreadRendering()
        {
            _renderFireSpread = FireRenderer.ToggleFire(_input);
            return _renderFireSpread;
        }        

        public void UpdateInput(PREACTInput input)
        {
            _input = input;
            _painter.SetLCPData(_input.WildfireModule.Data.LandscapeData);

            //Every route into a scenario passes through here - loading a file, saving a new one, the
            //engine handing one over - and the engine records the path before it calls this, so this is
            //the one place that has to remember it.
            RecentScenario.Remember(_engine.WorkingFile);

            RefreshScenarioView();

            ScenarioSession.OnEngineInput(_input);
            //A scenario can name its own ELMFIRE, GDAL and WindNinja, so the tools are looked for again.
            ToolsService.Refresh();
        }

        /// <summary>
        /// Reads the scenario's terrain again from its <c>[Landscape]</c> section, after that was re-pointed in
        /// memory - a fire case build sets it to the case's dem/slp/asp, "Use the case terrain" does the same -
        /// and resets the painter, whose grid may be another one now.
        /// </summary>
        /// <remarks>
        /// The terrain is loaded when the scenario is parsed. Without this the painter kept drawing on, and a
        /// run straight after the build sampled k-PERIL's slope from, the terrain the scenario named before.
        /// </remarks>
        public void ReloadLandscape()
        {
            if (_input == null)
            {
                return;
            }

            _input.WildfireModule.Data.ReloadLandscape(_input.Simulation, _input.WildfireModule, _input.Landscape, _input.RootFolder);
            _painter.SetLCPData(_input.WildfireModule.Data.LandscapeData);
            //The same scenario: strokes not saved yet stay unsaved (and are asked about), whatever the grid does.
            _painter.ResetGrid();
        }

        /// <summary>
        /// Redraws everything placed in the scenario's simulation coordinates: the UTM map, the domain border,
        /// the camera, the markers, the road network and the painter's grid. Needed whenever the origin may
        /// have moved - a load, or the area of interest changed in Place and time, which used to move the
        /// simulation grid and leave all of these where they were.
        /// </summary>
        public void RefreshScenarioView(bool sameScenario = false)
        {
            if (_input == null)
            {
                ShowWebMercatorMap();
                return;
            }

            //A different scenario is a different grid. The painter used to keep the first grid it resolved
            //for the whole session, textures and group ownership included, and wrote the next scenario's
            //masks with the previous one's cell count. The same scenario with its area moved keeps what was
            //painted and not saved.
            if (sameScenario) _painter.ResetGrid();
            else _painter.ResetForScenario();

            //A different scenario is a different network, in a different frame. Dropped rather than reused,
            //which would draw the previous scenario's roads at this one's origin.
            _roadNetwork = null;
            _roadNetworkBuilt = false;
            _roadNetworkWarning = null;
            _roadNetworkVisualizer.SetVisibility(false);
            _godCamera.SetInput(_input);
            //this needs map and evac goals
            _simulationDomainVisualizer.SpawnEvacuationGoalMarkers(_input, _destinationMarkerPrefab);
            _simulationDomainVisualizer.SpawnWildfireIgnitionMarkers(_input, _wildfireIgnitionMarkerPrefab);

            //map stuff
            ShowUTMMap();
            LoadUTMMap(_input);
            UpdateSimBorders();
        }

        /// <summary>
        /// Back to the open scenario's map after the world map was put up for a pick that was then abandoned,
        /// or for the new-scenario dialog that was then closed. Nothing moved, so unlike
        /// <see cref="RefreshScenarioView"/> this leaves the painter (and any unsaved strokes), the markers and
        /// the road network alone.
        /// </summary>
        public void RestoreScenarioMap()
        {
            //The session, not _input: closing a scenario leaves the engine's (and so this) reference in place.
            if (_input == null || !ScenarioSession.HasInput)
            {
                ShowWebMercatorMap();
                return;
            }

            ShowUTMMap();
            _godCamera.SetInput(_input);
            UpdateSimBorders();
        }

        /// <summary>The road network has been read already (the workflow never makes it parse one).</summary>
        public bool HasRoadNetworkLoaded { get => _roadNetwork != null; }

        public void UpdateDestinations(List<PREACT.Evacuation.EvacuationDestination> destinations)
        {
            _simulationDomainVisualizer.SpawnEvacuationGoalMarkers(_input, destinations, _destinationMarkerPrefab);
        }

        /// <summary>
        /// Rebuilds the destination markers from the scenario as it now stands.
        ///
        /// Needed after any edit to a destination. The markers were only ever spawned when a scenario was
        /// loaded and when the running simulation reported its destinations, so moving, adding, renaming,
        /// recolouring or removing one changed the scenario and left the map showing where it used to be -
        /// which reads as an edit that did not take.
        /// </summary>
        public void RefreshDestinationMarkers()
        {
            if (_input == null)
            {
                return;
            }

            _simulationDomainVisualizer.SpawnEvacuationGoalMarkers(_input, _destinationMarkerPrefab);
        }

        /// <summary>
        /// Rebuilds the wildfire ignition markers from the scenario as it now stands, for the same reason
        /// the destination markers need it: they were only ever spawned on load, so a point added or moved
        /// in the editor left the map marking where it used to be.
        /// </summary>
        public void RefreshWildfireIgnitionMarkers()
        {
            if (_input == null)
            {
                return;
            }

            _simulationDomainVisualizer.SpawnWildfireIgnitionMarkers(_input, _wildfireIgnitionMarkerPrefab);
        }

        /// <summary>
        /// Moves a position to the centre of the fire grid cell containing it, and says which cell that
        /// is. False when there is no fire grid, or when the position is off it.
        ///
        /// This is the snap that means something for an ignition. ELMFIRE resolves X_IGN/Y_IGN to a cell
        /// and ignites the whole of it, so a point placed anywhere in a cell is the same ignition as the
        /// cell's centre - and seeing which cell it landed in is how an ignition that is one cell into the
        /// sea, or one cell outside the domain, becomes visible before the run rather than after it.
        /// </summary>
        public bool TrySnapToFireGridCell(PREACT.Math.Vector2d simulationPos,
            out PREACT.Math.Vector2d snapped, out PREACT.Math.Vector2int cell, out double cellSize)
        {
            snapped = simulationPos;
            cell = new PREACT.Math.Vector2int(0, 0);
            cellSize = 0.0;

            if (!Painter.TryGetPaintGrid(out PREACT.Math.Vector2d gridSize, out PREACT.Math.Vector2d gridOrigin,
                    out PREACT.Math.Vector2int cellCount, out cellSize)
                || cellSize <= 0.0)
            {
                return false;
            }

            double localX = simulationPos.x - gridOrigin.x;
            double localY = simulationPos.y - gridOrigin.y;

            if (localX < 0.0 || localY < 0.0 || localX >= gridSize.x || localY >= gridSize.y)
            {
                return false;
            }

            int x = (int)(localX / cellSize);
            int y = (int)(localY / cellSize);
            if (x < 0 || y < 0 || x >= cellCount.x || y >= cellCount.y)
            {
                return false;
            }

            cell = new PREACT.Math.Vector2int(x, y);
            snapped = new PREACT.Math.Vector2d(
                gridOrigin.x + (x + 0.5) * cellSize,
                gridOrigin.y + (y + 0.5) * cellSize);
            return true;
        }

        /// <summary>
        /// The lanes of the scenario's SUMO network, in simulation coordinates, or null when there is no
        /// network to read. Read once and kept.
        /// </summary>
        public PREACT.Utility.SumoNetworkGeometry GetRoadNetwork()
        {
            if (_roadNetwork != null)
            {
                return _roadNetwork;
            }

            if (_input == null)
            {
                Engine.Message(null, Engine.LogType.Warning, "Load a scenario before asking for its road network.");
                return null;
            }

            //The same resolver the engine starts SUMO with, so what is drawn is what will be run - or, when
            //the scenario's path is wrong, the same substitution with the same warning rather than a second
            //opinion about where the network is. Geometry can come from a bare .net.xml too, hence false.
            string path = PREACT.Utility.SumoConfigurationLocator.Resolve(
                _input.RootFolder, _input.TrafficModule.SumoInput.ConfigurationFile,
                false, out bool corrected, out string explanation);

            //Said once, not on every click that asks: a destination click asks to snap, and each one repeated "This scenario
            //names no SUMO configuration ..." (Auburn2). Said again when the answer changes, or for another scenario.
            if (path == null)
            {
                WarnRoadNetworkOnce(explanation);
                return null;
            }

            if (corrected)
            {
                WarnRoadNetworkOnce(explanation);
            }

            _roadNetwork = PREACT.Utility.SumoNetworkGeometry.Load(path, _input.Simulation.Data.UTMOrigin);
            return _roadNetwork;
        }

        private string _roadNetworkWarning;

        private void WarnRoadNetworkOnce(string explanation)
        {
            if (explanation == _roadNetworkWarning) return;
            _roadNetworkWarning = explanation;
            Engine.Message(null, Engine.LogType.Warning, explanation);
        }

        /// <summary>
        /// Forgets the road network read so far, so the next use reads the one on disk - after the roads step has built
        /// it again. Redrawn at once when it is on show.
        /// </summary>
        public void ForgetRoadNetwork()
        {
            bool shown = IsRoadNetworkVisible;
            _roadNetwork = null;
            _roadNetworkBuilt = false;
            _roadNetworkWarning = null;
            if (shown)
            {
                _roadNetworkVisualizer.SetVisibility(false);
                ShowRoadNetwork(true);
            }
        }

        /// <summary>
        /// Puts a position onto the nearest lane of the road network, reporting which lane and how far it
        /// moved. False when there is no network to snap to.
        /// </summary>
        public bool TrySnapToRoadNetwork(PREACT.Math.Vector2d simulationPos, out PREACT.Utility.SumoNetworkGeometry.Snap snap)
        {
            snap = new PREACT.Utility.SumoNetworkGeometry.Snap();
            PREACT.Utility.SumoNetworkGeometry network = GetRoadNetwork();
            return network != null && network.TrySnap(simulationPos, out snap);
        }

        public bool IsRoadNetworkVisible { get => _roadNetworkVisualizer != null && _roadNetworkVisualizer.IsVisible; }

        //View > Map layers > Fire grid outline: the edge of the grid the fire, the painting and k-PERIL share (an ELMFIRE
        //case's dem.tif), drawn as a copy of the domain border in another colour. Where the case's padding reaches, and
        //whether a painting can cover the domain, is otherwise invisible.
        private LineRenderer _fireGridBorder;
        private bool _fireGridOutline;
        private double _outlineX, _outlineY, _outlineW, _outlineH;

        public bool IsFireGridOutlineVisible { get => _fireGridOutline; }

        public void ShowFireGridOutline(bool show)
        {
            _fireGridOutline = show;
            if (_fireGridBorder != null) _fireGridBorder.gameObject.SetActive(false);
            _outlineW = _outlineH = 0.0;
            //Resolved here once, so a scenario without a paint grid says why (the painter's own message) - but not while
            //anything runs, when a build may be writing the dem.tif it is read from (review R4); UpdateFireGridOutline
            //resolves it once that is done.
            if (show && _painter != null && !ScenarioSession.IsBusy) _painter.TryGetPaintGrid(out PREACT.Math.Vector2d _, out PREACT.Math.Vector2d _);
        }

        /// <summary>
        /// Keeps the outline on the paint grid while it is shown, and off the world map. The grid is resolved again only
        /// when its raster exists and nothing is being built, so a build that re-cut the grid moves the outline without
        /// the painter reading a raster that is being written, or saying every frame that there is none.
        /// </summary>
        /// <remarks>
        /// One rule for the two conditions (review R4): the world map hides it whatever is running - it is drawn in UTM
        /// metres - and while anything runs it stays where it was last resolved, on the UTM map only. It used to skip the
        /// hide whenever something was busy, so the outline showed on the world map during a run or a build.
        /// </remarks>
        private void UpdateFireGridOutline()
        {
            if (!_fireGridOutline || _painter == null || _input == null) return;

            if (!_utmMap.gameObject.activeSelf)
            {
                if (_fireGridBorder != null) _fireGridBorder.gameObject.SetActive(false);
                return;
            }

            if (ScenarioSession.IsBusy)
            {
                if (_fireGridBorder != null && _outlineW > 0.0 && !_fireGridBorder.gameObject.activeSelf)
                {
                    _fireGridBorder.gameObject.SetActive(true);
                }
                return;
            }

            string reference = Painter.ExpectedGridReference(_input);
            bool haveRaster = !string.IsNullOrEmpty(reference) && GuiFiles.Exists(GuiFiles.Resolve(_input.RootFolder, reference));
            PREACT.Math.Vector2d size = default, origin = default;
            bool show = haveRaster && _painter.TryGetPaintGrid(out size, out origin);
            if (!show)
            {
                if (_fireGridBorder != null) _fireGridBorder.gameObject.SetActive(false);
                _outlineW = _outlineH = 0.0;
                return;
            }

            if (_fireGridBorder == null)
            {
                _fireGridBorder = Instantiate(_simBorder, _simBorder.transform.parent);
                _fireGridBorder.name = "FireGridBorder";
                _fireGridBorder.startColor = _fireGridBorder.endColor = new Color(1f, 0.55f, 0.1f, 1f);
            }

            if (_fireGridBorder.gameObject.activeSelf && origin.x == _outlineX && origin.y == _outlineY
                && size.x == _outlineW && size.y == _outlineH) return;

            _outlineX = origin.x; _outlineY = origin.y; _outlineW = size.x; _outlineH = size.y;
            Vector3 corner = new Vector3((float)origin.x, 55f, (float)origin.y);
            _fireGridBorder.positionCount = 5;
            _fireGridBorder.SetPosition(0, corner);
            _fireGridBorder.SetPosition(1, corner + Vector3.right * (float)size.x);
            _fireGridBorder.SetPosition(2, corner + new Vector3((float)size.x, 0f, (float)size.y));
            _fireGridBorder.SetPosition(3, corner + Vector3.forward * (float)size.y);
            _fireGridBorder.SetPosition(4, corner);
            _fireGridBorder.gameObject.SetActive(true);
        }

        /// <summary>
        /// Shows or hides the road network. Building the mesh is deferred to the first time it is shown,
        /// since most sessions never ask for it.
        /// </summary>
        public bool ShowRoadNetwork(bool show)
        {
            if (!show)
            {
                _roadNetworkVisualizer.SetVisibility(false);
                return false;
            }

            if (!_roadNetworkBuilt)
            {
                PREACT.Utility.SumoNetworkGeometry network = GetRoadNetwork();
                if (network == null)
                {
                    return false;
                }

                //Dark enough to read against a satellite image, which is what is usually underneath.
                if (!_roadNetworkVisualizer.Build(network, new Color(0.1f, 0.85f, 1f, 1f)))
                {
                    Engine.Message(null, Engine.LogType.Warning, "The road network had no lanes to draw.");
                    return false;
                }
                _roadNetworkBuilt = true;
            }

            //The network is measured in simulation coordinates, like everything else placed by hand, so it
            //only lines up on the UTM map.
            ShowUTMMap();
            _roadNetworkVisualizer.SetVisibility(true);
            return true;
        }

        public void LoadUTMMap(PREACTInput input)
        {
            //Mapbox: calculate the amount of grids needed based on zoom level, coord and size
            Mapbox.Unity.Map.MapOptions mOptions = _utmMap.Options; // new Mapbox.Unity.Map.MapOptions();

            mOptions.locationOptions.latitudeLongitude = "" + input.Simulation.LowerLeftLatLon.x + "," + input.Simulation.LowerLeftLatLon.y;
            mOptions.locationOptions.zoom = input.Map.ZoomLevel;
            mOptions.extentOptions.extentType = Mapbox.Unity.Map.MapExtentType.CameraBounds;// Mapbox.Unity.Map.MapExtentType.RangeAroundCenter;
            /*mOptions.extentOptions.defaultExtents.rangeAroundCenterOptions.west = 0;
            mOptions.extentOptions.defaultExtents.rangeAroundCenterOptions.south = 0;
            //https://wiki.openstreetmap.org/wiki/Zoom_levels
            double degreesPerTile = 360.0 / (Mathf.Pow(2.0f, mOptions.locationOptions.zoom));
            PREACT.Math.Vector2d mapDegrees = LocalGPWData.SizeToDegrees(input.Simulation.LowerLeftLatLon, input.Simulation.DomainSize);
            int tilesX = (int)(mapDegrees.x / degreesPerTile) + 1;
            int tilesY = (int)(mapDegrees.y / (degreesPerTile * Mathf.Cos((Mathf.PI / 180.0f) * (float)input.Simulation.LowerLeftLatLon.x))) + 1;
            mOptions.extentOptions.defaultExtents.rangeAroundCenterOptions.east = tilesX;
            mOptions.extentOptions.defaultExtents.rangeAroundCenterOptions.north = tilesY;*/
            mOptions.placementOptions.placementType = Mapbox.Unity.Map.MapPlacementType.AtLocationCenter;
            mOptions.placementOptions.snapMapToZero = false;
            mOptions.scalingOptions.scalingType = Mapbox.Unity.Map.MapScalingType.WorldScale;

            if (!_utmMap.IsAccessTokenValid)
            {
                Engine.Message(null, Engine.LogType.SimulationError, "Mapbox token not valid.");
                return;
            }

            Engine.Message(null, Engine.LogType.Log, "Starting to load Mapbox map.");
            _utmMap.Initialize(new Mapbox.Utils.Vector2d(input.Simulation.LowerLeftLatLon.x, input.Simulation.LowerLeftLatLon.y), input.Map.ZoomLevel);
            Engine.Message(null, Engine.LogType.Log, "Map loaded succesfully.");

            //do warping to better fit UTM
            for (int i = 0; i < _utmMap.transform.childCount; ++i)
            {
                Mapbox.Unity.MeshGeneration.Data.UnityTile tile = _utmMap.transform.GetChild(i).GetComponent<Mapbox.Unity.MeshGeneration.Data.UnityTile>();
                if (tile != null)
                {
                    Vector3[] vertices = tile.GetComponent<MeshFilter>().mesh.vertices;
                    for (int v = 0; v < vertices.Length; ++v)
                    {
                        Vector3 worldPos = tile.transform.TransformPoint(vertices[v]);
                        Vector2d wgs84Pos = _utmMap.WorldToGeoPosition(worldPos); //GeoConversions.MetersToLatLon(new Vector2d(worldPos.x, worldPos.z) + WUIEngine.RUNTIME_DATA.Simulation.CenterMercator);
                        PREACT.Math.Vector2d utmSimPos = input.Simulation.Data.GetSimulationPosition(new PREACT.Math.Vector2d(wgs84Pos.x, wgs84Pos.y));
                        Vector3 newWorldPos = new Vector3((float)utmSimPos.x, 0f, (float)utmSimPos.y);
                        vertices[v] = tile.transform.InverseTransformPoint(newWorldPos);
                    }
                    tile.GetComponent<MeshFilter>().mesh.SetVertices(vertices);
                    tile.GetComponent<MeshFilter>().mesh.RecalculateBounds();
                }
            }
            //MAP.transform.localScale = new Vector3((float)WUIEngine.RUNTIME_DATA.Simulation.MercatorToUtmScale.x, 1.0f, (float)WUIEngine.RUNTIME_DATA.Simulation.MercatorToUtmScale.y);  
        }

        public void NewLogMessage(string message)
        {
            if (Application.isEditor && !SuppressMessages)
            {
                Debug.Log(message);
            }
            _wuiGUI.NewMessage(message);
        }
        //Required by IExternalManager. It is called on the run's thread as the run ends; the GUI follows the
        //run's task from Update instead (WatchRunTask), on the main thread.
        public void SimulationsFinished()
        {
        }

        public string WorkingFolder { get => _engine.WorkingFolder; }

        private bool _pickingBoundingBox;
        private bool _pickingPos;
        //Where the left button went down, and how far it may travel before the press counts as a drag
        //of the map rather than a click on it.
        private Vector3 _mouseDownPos;
        private const float _clickSlop = 6f;
        private readonly PREACT.Utility.AreaOfInterestPick _areaPick = new PREACT.Utility.AreaOfInterestPick();
        private System.Action<PREACT.Math.Vector2d[]> _onClicks;
        private System.Action<PREACT.Math.Vector2d> _onClick;
        private System.Action _onPickCancelled;

        /// <summary>A position or an area of interest is being picked on the map, so a click there is the pick's.</summary>
        public bool IsPicking { get => _pickingPos || _pickingBoundingBox; }

        /// <summary>The scenario's own map (UTM, simulation coordinates) is the one on screen, not the world map.</summary>
        public bool IsUTMMapShown { get => _utmMap != null && _utmMap.gameObject.activeSelf; }

        /// <summary>True while the map is waiting for a click (a position, or the corners of an area).</summary>
        public void PickBoundingBoxOnMap(System.Action<PREACT.Math.Vector2d[]> clicks, System.Action cancelled = null)
        {
            _onClicks = clicks;
            _onPickCancelled = cancelled;
            _areaPick.Reset();
            SetWebMercatorMapInteraction(true);
            _pickingBoundingBox = true;
            NewLogMessage("Pick the area of interest: click two opposite corners on the map.");
            _boundingBoxRenderer.gameObject.SetActive(true);
            _boundingBoxRenderer.startWidth = 0.5f;
            _boundingBoxRenderer.endWidth = 0.5f;
            for (int i = 0; i < _boundingBoxRenderer.positionCount; ++i)
            {
                _boundingBoxRenderer.SetPosition(i, Vector3.zero - Vector3.down * 100);
            }
        }
        private void FinishPickBoundingBoxOnMap()
        {
            _boundingBoxRenderer.gameObject.SetActive(false);
            _pickingBoundingBox = false;
            _onPickCancelled = null;
            //South-west first, north-east second, whichever way round they were clicked.
            System.Action<PREACT.Math.Vector2d[]> done = _onClicks;
            _onClicks = null;
            done?.Invoke(new[] { _areaPick.LowerLeft, _areaPick.UpperRight });
        }

        /// <summary>
        /// Abandons a position or area being picked, without calling back: the scenario it was for is no longer the
        /// one open (another was opened meanwhile), so neither placing it nor reopening its editor would be right.
        /// </summary>
        public void CancelPick()
        {
            if (!_pickingPos && !_pickingBoundingBox)
            {
                return;
            }

            _pickingPos = false;
            _onClick = null;
            _pickingBoundingBox = false;
            _onClicks = null;
            _onPickCancelled = null;
            if (_boundingBoxRenderer != null) _boundingBoxRenderer.gameObject.SetActive(false);
            NewLogMessage("Picking on the map was cancelled: another scenario was opened.");
        }

        public void PickPosOnMap(System.Action<PREACT.Math.Vector2d> onClick, System.Action cancelled = null)
        {
            _pickingPos = true;
            _onClick = onClick;
            _onPickCancelled = cancelled;
            //The editor window closes to get out of the way, so without this nothing on screen says
            //the application is waiting for a click, or how to move the map while looking for the spot.
            NewLogMessage("Click the map to place. Drag to pan, scroll to zoom, arrow keys to move, Escape to cancel.");
        }

        private void FinishPickPosOnMap(Vector3 clickPos)
        {
            _pickingPos = false;
            _onPickCancelled = null;
            _onClick(new PREACT.Math.Vector2d(clickPos.x, clickPos.z));
            _onClick = null;
        }


        public void SetWebMercatorMapInteraction(bool canInteract)
        {
            _webMercatorCameraMovement.enabled = canInteract;
        }

        /// <summary>
        /// The world map is navigable for as long as it is on screen. It used to be movable only while
        /// two corners of an area of interest were being clicked, and frozen at every other moment -
        /// including while the scenario creator was open on top of it - which made finding a place on
        /// it a matter of luck.
        ///
        /// Movement is suspended while the pointer is over the GUI, because Mapbox's camera does not
        /// know ImGui exists and would otherwise zoom the map when a menu is scrolled.
        /// </summary>
        private void UpdateWebMercatorMapInteraction()
        {
            if (_webMercatorMap == null || !_webMercatorMap.gameObject.activeInHierarchy)
            {
                return;
            }

            //WantTextInput, not WantCaptureKeyboard: the latter is true whenever any window has focus,
            //which is most of the time, and Mapbox's camera pans on the keyboard axes. Only an active
            //text field should stop it.
            bool guiOwnsInput = ImGui.GetIO().WantCaptureMouse || ImGui.GetIO().WantTextInput;
            _webMercatorCameraMovement.enabled = !guiOwnsInput;
        }

        public void ShowUTMMap()
        {
            _boundingBoxRenderer.gameObject.SetActive(false);
            _webMercatorMap.gameObject.SetActive(false);
            _utmMap.gameObject.SetActive(true);
        }

        public void ShowWebMercatorMap()
        {
            _boundingBoxRenderer.gameObject.SetActive(false);
            _simBorder.gameObject.SetActive(false);
            _godCamera.SetToWebMercatorMode();
            _webMercatorMap.gameObject.SetActive(true);
            _utmMap.gameObject.SetActive(false);
        }
    }
}