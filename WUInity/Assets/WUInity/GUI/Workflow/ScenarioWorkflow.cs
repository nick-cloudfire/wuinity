using System;
using System.Collections.Generic;
using System.IO;
using PREACT.Evacuation;
using PREACT.Input;
using PREACT.Math;

namespace WUInity.Workflow
{
    /// <summary>
    /// The thirteen steps from an empty folder to a trigger campaign, each with a status computed from the
    /// scenario and its files, what is wrong, and what to do about it.
    /// </summary>
    /// <remarks>
    /// Status used to be three systems that never agreed: a checklist that knew only what the parser had
    /// complained about when the file was read, "[done]" markers that knew only whether a file of the right
    /// name existed, and warnings scattered through the editor tabs. None of them knew "painted on the wrong
    /// grid", "the case is older than its fuel layer", "k-PERIL will fall back to a mask it then refuses" or
    /// "unsaved". The rules below are the one place those questions are answered, and the workflow panel and
    /// the menus both read them.
    ///
    /// Pure: no ImGui, no UnityEngine. Everything it reads comes through <see cref="WorkflowContext"/> and a
    /// <see cref="FileProbe"/>, so a console program can compute the same statuses the GUI shows. Refreshing
    /// costs a few dozen cached stats and header reads; the GUI does it at most once a second, and when
    /// something happened.
    /// </remarks>
    public sealed class ScenarioWorkflow
    {
        private readonly FileProbe _files;
        private readonly List<WorkflowStep> _steps = new List<WorkflowStep>();

        public ScenarioWorkflow(FileProbe files = null)
        {
            _files = files ?? new FileProbe();
        }

        public IReadOnlyList<WorkflowStep> Steps { get => _steps; }

        public WorkflowStep this[WorkflowStepId id]
        {
            get
            {
                foreach (WorkflowStep s in _steps) if (s.Id == id) return s;
                return null;
            }
        }

        public DateTime RefreshedAt { get; private set; }

        /// <summary>The grid painting, the groups and k-PERIL share, or null when it does not exist yet.</summary>
        public RasterInfo PaintGrid { get; private set; }

        /// <summary>Scenario-relative path of the raster <see cref="PaintGrid"/> comes from.</summary>
        public string PaintGridReference { get; private set; }

        /// <summary>What stands between the scenario and a run, one line each; empty when it can run.</summary>
        public readonly List<string> RunBlockers = new List<string>();

        /// <summary>Things that will not stop a run but are worth reading first.</summary>
        public readonly List<string> RunWarnings = new List<string>();

        public bool IsElmfire { get; private set; }
        public bool IsImportedFire { get; private set; }
        public bool FireOn { get; private set; }
        public bool EvacuationOn { get; private set; }
        public bool TriggerOn { get; private set; }

        // ------------------------------------------------------------------ facts shared by several rules

        private WorkflowContext _ctx;
        private PREACTInput _in;
        private string _root;
        private string _name;
        private string _case;
        private string _campaignFolder;
        private RasterInfo _caseGrid;
        private PaintedAreasInfo _painted;
        private string _paintedPath;
        private int _households = -1;

        private string Abs(string recorded) => ScenarioFiles.Resolve(_root, recorded);
        private bool Exists(string recorded) => !string.IsNullOrWhiteSpace(recorded) && _files.Exists(Abs(recorded));

        private RasterInfo Raster(string recorded)
        {
            string path = Abs(recorded);
            return path == null ? null : _files.Read(path, "raster", RasterInfo.Read, null);
        }

        private static string Plural(int n, string one, string many = null) => n + " " + (n == 1 ? one : (many ?? one + "s"));

        // ------------------------------------------------------------------ refresh

        /// <summary>Recomputes every step from the scenario and its files.</summary>
        public void Refresh(WorkflowContext ctx)
        {
            _ctx = ctx ?? new WorkflowContext();
            _in = _ctx.Input;
            _steps.Clear();
            RunBlockers.Clear();
            RunWarnings.Clear();
            PaintGrid = null;
            PaintGridReference = null;
            RefreshedAt = DateTime.Now;

            if (_in == null)
            {
                NoScenario();
                return;
            }

            _root = _in.RootFolder;
            _name = _in.Simulation?.Name ?? string.Empty;
            _case = ScenarioFiles.CaseDirectory(_in);
            _campaignFolder = CampaignFolderOf(_in, _ctx.ScenarioPath);

            FireOn = _in.WildfireModule.Enabled;
            IsElmfire = FireOn && _in.WildfireModule.Module == WildfireModuleInput.WildfireModules.ELMFIRE;
            IsImportedFire = FireOn && _in.WildfireModule.Module == WildfireModuleInput.WildfireModules.AscImport;
            EvacuationOn = _in.PedestrianModule.Enabled || _in.TrafficModule.Enabled;
            TriggerOn = FireOn && _in.TriggerBufferModule.Enabled
                        && _in.TriggerBufferModule.Module == TriggerBufferModuleInput.TriggerBufferModules.kPERIL;

            _caseGrid = IsElmfire || _in.WildfireModule.Module == WildfireModuleInput.WildfireModules.ELMFIRE
                ? Raster(ScenarioFiles.CaseInput(_in, "dem.tif"))
                : null;

            if (IsElmfire)
            {
                PaintGrid = _caseGrid;
                PaintGridReference = ScenarioFiles.CaseInput(_in, "dem.tif");
            }
            else if (IsImportedFire)
            {
                PaintGridReference = _in.WildfireModule.AscImportInput.TimeOfArrivalFile;
                PaintGrid = Exists(PaintGridReference) ? Raster(PaintGridReference) : null;
            }
            else
            {
                PaintGridReference = _in.Landscape.GetReferenceFile();
                PaintGrid = Exists(PaintGridReference) ? Raster(PaintGridReference) : null;
            }

            _paintedPath = _in.WildfireModule.GraphicalFireInputFile;
            _painted = Exists(_paintedPath) ? _files.Read(Abs(_paintedPath), "gfi", PaintedAreasInfo.Read, null) : null;

            string population = _in.Population.PopulationFile;
            _households = Exists(population) ? _files.Read(Abs(population), "households", ScenarioFiles.CountPopulationRows, -1) : -1;

            _steps.Add(PlaceAndTime());
            _steps.Add(Roads());
            _steps.Add(Population());
            _steps.Add(Fuels());
            _steps.Add(FireCase());
            _steps.Add(FireAreas());
            _steps.Add(Destinations());
            _steps.Add(CurvesAndDemographics());
            _steps.Add(EvacuationGroups());
            _steps.Add(TriggerBoundary());

            AddCheckFindings();

            _steps.Add(RunSimulation());
            _steps.Add(Results());
            _steps.Add(Campaign());

            foreach (WorkflowStep s in _steps)
            {
                Finalise(s);
            }
        }

        private void NoScenario()
        {
            FireOn = IsElmfire = IsImportedFire = EvacuationOn = TriggerOn = false;
            foreach (WorkflowStepId id in Enum.GetValues(typeof(WorkflowStepId)))
            {
                if (id == WorkflowStepId.None) continue;
                var s = new WorkflowStep { Id = id, Title = TitleOf(id), Status = StepStatus.Blocked,
                    BlockedBy = "No scenario is open: File > New scenario, or File > Open scenario.",
                    Summary = "No scenario open." };
                _steps.Add(s);
            }
            RunBlockers.Add("No scenario is open.");
        }

        public static string TitleOf(WorkflowStepId id)
        {
            switch (id)
            {
                case WorkflowStepId.PlaceAndTime: return "Place and time";
                case WorkflowStepId.Roads: return "Roads";
                case WorkflowStepId.Population: return "Population";
                case WorkflowStepId.Fuels: return "Fuels, canopy and buildings";
                case WorkflowStepId.FireCase: return "Fire case (ELMFIRE)";
                case WorkflowStepId.FireAreas: return "Fire areas and ignition";
                case WorkflowStepId.Destinations: return "Destinations";
                case WorkflowStepId.CurvesAndDemographics: return "Response curves and demographics";
                case WorkflowStepId.EvacuationGroups: return "Evacuation groups";
                case WorkflowStepId.TriggerBoundary: return "Trigger boundary (k-PERIL)";
                case WorkflowStepId.RunSimulation: return "Run one simulation";
                case WorkflowStepId.Results: return "Results";
                case WorkflowStepId.Campaign: return "Probabilistic campaign";
                default: return id.ToString();
            }
        }

        /// <summary>Turns a step's facts into its status: Running beats Optional beats Blocked beats the rest.</summary>
        private void Finalise(WorkflowStep s)
        {
            bool running = (_ctx.StepActive && _ctx.StepOwner == s.Id)
                           || (_ctx.SimulationActive && s.Id == WorkflowStepId.RunSimulation)
                           || (_ctx.CampaignActive && s.Id == WorkflowStepId.Campaign);

            if (running)
            {
                s.Status = StepStatus.Running;
                if (s.Id != WorkflowStepId.RunSimulation && s.Id != WorkflowStepId.Campaign && !string.IsNullOrEmpty(_ctx.StepTitle))
                {
                    s.Summary = _ctx.StepTitle + "...";
                }
            }
            else if (!s.Applicable)
            {
                s.Status = StepStatus.Optional;
            }
            else if (!string.IsNullOrEmpty(s.BlockedBy))
            {
                s.Status = StepStatus.Blocked;
            }
            //Status was set by the rule as Done or ToDo; issues move it to Needs attention.
            else if (s.Status == StepStatus.Done)
            {
                s.Status = s.HasWarnings ? StepStatus.NeedsAttention : StepStatus.Done;
            }
            else
            {
                s.Status = s.HasErrors ? StepStatus.NeedsAttention : StepStatus.ToDo;
            }

            //A blocked step's action cannot be taken; the tooltip names what is in the way.
            if (s.Status == StepStatus.Blocked && s.Primary != null && s.Primary.Enabled && !KeepEnabledWhenBlocked(s))
            {
                s.Primary.Disable(s.BlockedBy);
            }

            //Nothing that starts work can be started while work is running.
            if (_ctx.SimulationActive || _ctx.StepActive)
            {
                foreach (StepAction a in Actions(s))
                {
                    if (a.Enabled && StartsWork(a.Id))
                    {
                        a.Disable(_ctx.SimulationActive ? "Not while a simulation is running." : "Not while \"" + _ctx.StepTitle + "\" is running.");
                    }
                }
            }
        }

        private static IEnumerable<StepAction> Actions(WorkflowStep s)
        {
            if (s.Primary != null) yield return s.Primary;
            foreach (StepAction a in s.Secondary) yield return a;
        }

        /// <summary>Opening an editor is fine for a blocked step - it is where the blocking thing is often fixed.</summary>
        private static bool KeepEnabledWhenBlocked(WorkflowStep s)
        {
            switch (s.Primary.Id)
            {
                case WorkflowAction.EditPlaceAndTime:
                case WorkflowAction.OpenGroups:
                case WorkflowAction.OpenTriggerBoundary:
                case WorkflowAction.OpenResults:
                case WorkflowAction.OpenSourceLayers:
                case WorkflowAction.OpenFireModelSettings:
                //Its ignition points need no grid, and it offers to build the case that gives one.
                case WorkflowAction.OpenFireAreas:
                //The run window is where the blockers are listed; it will not start while they stand.
                case WorkflowAction.OpenRun:
                    return true;
                default:
                    return false;
            }
        }

        private static bool StartsWork(WorkflowAction a)
        {
            switch (a)
            {
                case WorkflowAction.PrepareRoads:
                case WorkflowAction.RedoRoads:
                case WorkflowAction.PreparePopulation:
                case WorkflowAction.RedoPopulation:
                case WorkflowAction.DownloadLandfire:
                case WorkflowAction.BuildFireCase:
                case WorkflowAction.RebuildFireCase:
                case WorkflowAction.DownloadDemOnly:
                case WorkflowAction.ApplyFireAreasToCase:
                case WorkflowAction.OpenCampaign:
                    return true;
                default:
                    return false;
            }
        }

        private WorkflowStep New(WorkflowStepId id, string title = null)
        {
            return new WorkflowStep { Id = id, Title = title ?? TitleOf(id), Status = StepStatus.ToDo };
        }

        private static void BlockBy(WorkflowStep s, WorkflowStepId by, string why)
        {
            if (!string.IsNullOrEmpty(s.BlockedBy)) return;
            s.BlockedByStep = by;
            s.BlockedBy = by == WorkflowStepId.None ? why : $"Blocked by step {(int)by} ({TitleOf(by)}): {why}";
        }

        private bool PlaceIsUsable
        {
            get
            {
                return !string.IsNullOrWhiteSpace(_name)
                       && _name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                       && _in.Simulation.DomainSize.x > 0 && _in.Simulation.DomainSize.y > 0
                       && !(_in.Simulation.LowerLeftLatLon.x == 0.0 && _in.Simulation.LowerLeftLatLon.y == 0.0);
            }
        }

        // ------------------------------------------------------------------ 1. place and time

        private WorkflowStep PlaceAndTime()
        {
            WorkflowStep s = New(WorkflowStepId.PlaceAndTime);
            s.Required = true;
            s.Primary = new StepAction(WorkflowAction.EditPlaceAndTime, "Edit place and time");

            SimulationInput sim = _in.Simulation;
            bool ok = true;

            if (string.IsNullOrWhiteSpace(_name))
            {
                s.Error("The scenario has no name. Every prepared file is named after it.", WorkflowAction.EditPlaceAndTime, "Name it");
                ok = false;
            }
            else if (_name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                s.Error($"\"{_name}\" cannot be used in a file name, and every prepared file is named after the scenario.",
                    WorkflowAction.EditPlaceAndTime, "Rename it");
                ok = false;
            }

            if (sim.DomainSize.x <= 0 || sim.DomainSize.y <= 0 || (sim.LowerLeftLatLon.x == 0.0 && sim.LowerLeftLatLon.y == 0.0))
            {
                s.Error("No area of interest: pick it on the map.", WorkflowAction.EditPlaceAndTime, "Pick the area");
                ok = false;
            }
            else if (sim.DomainSize.x > 50000 || sim.DomainSize.y > 50000)
            {
                s.Warn($"The domain is {sim.DomainSize.x / 1000:0.#} x {sim.DomainSize.y / 1000:0.#} km. Downloads, the fire case and "
                    + "the traffic network all grow with it; most towns fit in 20 km.");
            }

            if (sim.StartDateTime >= sim.EndDateTime)
            {
                s.Error($"The simulation starts ({sim.StartDateTime:yyyy-MM-dd HH:mm}) at or after it ends ({sim.EndDateTime:yyyy-MM-dd HH:mm}).",
                    WorkflowAction.EditPlaceAndTime, "Fix the dates");
                ok = false;
            }

            if (string.IsNullOrEmpty(_ctx.ScenarioPath) || !_files.Exists(_ctx.ScenarioPath))
            {
                s.Warn("The scenario has not been written to a file yet, so nothing that reads the .wui (a campaign) can use it.",
                    WorkflowAction.Save, "Save");
            }

            if (_ctx.IsDirty)
            {
                s.Warn("Unsaved changes: " + _ctx.DirtySummary + ".", WorkflowAction.Save, "Save");
            }

            s.Status = ok ? StepStatus.Done : StepStatus.ToDo;
            double hours = (sim.EndDateTime - sim.StartDateTime).TotalHours;
            s.Summary = PlaceIsUsable
                ? $"{sim.DomainSize.x / 1000:0.0} x {sim.DomainSize.y / 1000:0.0} km at {sim.LowerLeftLatLon.x:F4}, {sim.LowerLeftLatLon.y:F4}; "
                  + $"from {sim.StartDateTime:yyyy-MM-dd HH:mm} for {hours:0.#} h"
                : "Name and area of interest needed.";
            return s;
        }

        // ------------------------------------------------------------------ 2. roads

        private WorkflowStep Roads()
        {
            WorkflowStep s = New(WorkflowStepId.Roads);
            bool ped = _in.PedestrianModule.Enabled, traffic = _in.TrafficModule.Enabled;
            s.Applicable = ped || traffic;
            s.Required = traffic;

            string routerDb = ScenarioFiles.RouterDb(_name);
            string osm = ScenarioFiles.Osm(_name);
            string recordedSumo = _in.TrafficModule.SumoInput?.ConfigurationFile ?? string.Empty;
            bool haveRouterDb = Exists(routerDb);
            bool haveOsm = Exists(osm);
            bool sumoOk = PREACT.Utility.SumoConfigurationLocator.IsConfiguration(recordedSumo) && Exists(recordedSumo);
            bool builtSumo = Exists(ScenarioFiles.SumoConfig);

            if (!s.Applicable)
            {
                s.Summary = "No pedestrian or traffic evacuation in this scenario.";
                s.Primary = new StepAction(WorkflowAction.OpenEvacuationModules, "Evacuation modules");
                return s;
            }

            if (!PlaceIsUsable) BlockBy(s, WorkflowStepId.PlaceAndTime, "the downloads need a name and an area of interest.");

            bool done = true;
            if (traffic && !sumoOk)
            {
                done = false;
                if (builtSumo && recordedSumo.Replace('\\', '/') != ScenarioFiles.SumoConfig)
                {
                    s.Error($"A SUMO network is built ({ScenarioFiles.SumoConfig}) but the scenario names "
                        + (string.IsNullOrEmpty(recordedSumo) ? "none" : recordedSumo) + ".",
                        WorkflowAction.UseSumoNetwork, "Use " + ScenarioFiles.SumoConfig);
                }
                else if (!string.IsNullOrEmpty(recordedSumo))
                {
                    s.Error($"ConfigurationFile names {recordedSumo}, which "
                        + (Exists(recordedSumo) ? "is not a .sumocfg" : "does not exist") + ".",
                        WorkflowAction.PrepareRoads, "Build the network");
                }
                else
                {
                    s.Info("No SUMO network yet: the traffic simulation drives on it.");
                }

                if (_ctx.Tools.Probed && !_ctx.Tools.HaveSumo)
                {
                    s.Warn("SUMO was not found on the machine PATH; building the network needs its netconvert.",
                        WorkflowAction.OpenExternalTools, "External tools");
                }
            }

            if (recordedSumo.IndexOf('\\') >= 0)
            {
                s.Warn($"ConfigurationFile is written with backslashes ({recordedSumo}), which only Windows reads as a separator.",
                    WorkflowAction.UseSumoNetwork, "Normalise it");
            }

            if (ped && !haveRouterDb && _households <= 0)
            {
                done = false;
                s.Info("No RouterDb yet: households are placed on the roads it describes.");
            }

            if (haveOsm && sumoOk && _files.IsNewer(Abs(osm), Abs(recordedSumo)))
            {
                s.Warn("The OSM extract is newer than the SUMO network built from it.", WorkflowAction.RedoRoads, "Rebuild");
            }

            s.Status = done ? StepStatus.Done : StepStatus.ToDo;

            var parts = new List<string>();
            if (ped || haveRouterDb) parts.Add("RouterDb " + (haveRouterDb ? "built" : "missing"));
            if (traffic) parts.Add("SUMO network " + (sumoOk ? "set" : builtSumo ? "built, not set" : "missing"));
            s.Summary = string.Join(", ", parts);

            s.Primary = done
                ? new StepAction(WorkflowAction.ShowRoadNetwork, "Show on map", "Draws the lanes traffic is routed on.")
                : new StepAction(WorkflowAction.PrepareRoads, "Prepare missing",
                    "Downloads the OSM roads and builds the RouterDb" + (traffic ? " and the SUMO network" : "") + ", skipping what exists.");
            if (!traffic && done) s.Primary = new StepAction(WorkflowAction.RedoRoads, "Rebuild");
            s.Secondary.Add(new StepAction(WorkflowAction.RedoRoads, "Rebuild everything", "Downloads the roads again and rebuilds everything from them."));
            s.Secondary.Add(new StepAction(WorkflowAction.OpenEvacuationModules, "Evacuation modules"));
            if (!sumoOk) s.Secondary.Add(new StepAction(WorkflowAction.ShowRoadNetwork, "Show on map").Disable("There is no SUMO network to draw yet."));
            return s;
        }

        // ------------------------------------------------------------------ 3. population

        private WorkflowStep Population()
        {
            WorkflowStep s = New(WorkflowStepId.Population);
            s.Applicable = _in.PedestrianModule.Enabled;
            s.Required = s.Applicable;

            if (!s.Applicable)
            {
                s.Summary = "Pedestrian module off: nobody leaves home, so no households are needed.";
                s.Primary = new StepAction(WorkflowAction.OpenEvacuationModules, "Evacuation modules");
                return s;
            }

            if (!PlaceIsUsable) BlockBy(s, WorkflowStepId.PlaceAndTime, "the downloads need a name and an area of interest.");

            string recorded = _in.Population.PopulationFile;
            bool haveWorldPop = Exists(ScenarioFiles.WorldPopUtm(_name));
            bool done = _households > 0;

            if (string.IsNullOrEmpty(recorded))
            {
                if (Exists(ScenarioFiles.Population(_name)))
                {
                    s.Error($"{ScenarioFiles.Population(_name)} was generated, but the scenario does not name it.",
                        WorkflowAction.UseGeneratedPopulation, "Use it");
                }
                else
                {
                    s.Info("No population yet: WorldPop people are placed on the road network as households.");
                }
            }
            else if (!Exists(recorded))
            {
                s.Error($"PopulationFile names {recorded}, which does not exist.", WorkflowAction.PreparePopulation, "Generate it");
            }
            else if (_households == 0)
            {
                s.Error($"{recorded} holds no households - the RouterDb reached no roads, or WorldPop had nobody there.",
                    WorkflowAction.RedoPopulation, "Regenerate");
            }

            s.Status = done ? StepStatus.Done : StepStatus.ToDo;
            s.Summary = done
                ? $"{Plural(_households, "household")} ({recorded})"
                : "WorldPop " + (haveWorldPop ? "downloaded" : "missing") + ", households missing";

            s.Primary = done
                ? new StepAction(WorkflowAction.RedoPopulation, "Regenerate", "Generates the households again from WorldPop and the RouterDb.")
                : new StepAction(WorkflowAction.PreparePopulation, "Prepare missing",
                    "Downloads WorldPop, builds the RouterDb if there is none, and places households, skipping what exists.");
            s.Secondary.Add(new StepAction(WorkflowAction.OpenEvacuationModules, "Population settings"));
            return s;
        }

        // ------------------------------------------------------------------ 4. fuels (or the imported fire)

        private static readonly string[] BuildingStems = { "baa", "ssd", "nbf_h", "ff_h", "bfm_h" };

        private WorkflowStep Fuels()
        {
            if (IsImportedFire)
            {
                return ImportedFire();
            }

            WorkflowStep s = New(WorkflowStepId.Fuels);
            s.Primary = new StepAction(WorkflowAction.OpenSourceLayers, "Source layers");

            if (!FireOn)
            {
                s.Applicable = false;
                s.Summary = "No fire in this scenario.";
                s.Primary = new StepAction(WorkflowAction.OpenFireModelSettings, "Fire model settings");
                return s;
            }

            if (!IsElmfire)
            {
                s.Error("The fire is switched on but no fire module is chosen.", WorkflowAction.OpenFireModelSettings, "Choose ELMFIRE");
                s.Primary = new StepAction(WorkflowAction.OpenFireModelSettings, "Fire model settings");
                s.Required = true;
                return s;
            }

            s.Required = true;
            ElmfireInput e = _in.WildfireModule.ElmfireInput;

            bool fuelNamed = !string.IsNullOrEmpty(e.FuelModelFile);
            bool fuelSource = fuelNamed && Exists(e.FuelModelFile);
            string caseFuel = Exists(ScenarioFiles.CaseInput(_in, "fbfm40.tif")) ? "fbfm40.tif"
                : Exists(ScenarioFiles.CaseInput(_in, "fbfm13.tif")) ? "fbfm13.tif" : null;

            if (fuelNamed && !fuelSource)
            {
                s.Error($"FuelModelFile names {e.FuelModelFile}, which does not exist.", WorkflowAction.OpenSourceLayers, "Source layers");
            }
            else if (!fuelSource && caseFuel == null)
            {
                s.Info("Name a fuel model raster (any CRS; it is warped onto the case grid), or download LANDFIRE's for a US domain.");
            }

            //Canopy: a source, or a case that already has it.
            string[] canopy = { e.CanopyCoverFile, e.CanopyHeightFile, e.CanopyBaseHeightFile, e.CanopyBulkDensityFile };
            bool canopyNamed = !string.IsNullOrEmpty(e.CanopyDatasetFolder);
            foreach (string c in canopy)
            {
                if (string.IsNullOrEmpty(c)) continue;
                canopyNamed = true;
                if (!Exists(c)) s.Warn($"A canopy layer names {c}, which does not exist.", WorkflowAction.OpenSourceLayers, "Source layers");
            }
            if (!string.IsNullOrEmpty(e.CanopyDatasetFolder) && !_files.DirectoryExists(Abs(e.CanopyDatasetFolder)))
            {
                s.Warn($"CanopyDatasetFolder names {e.CanopyDatasetFolder}, which does not exist.", WorkflowAction.OpenSourceLayers, "Source layers");
            }
            bool caseCanopy = Exists(ScenarioFiles.CaseInput(_in, "cc.tif"));
            if (!canopyNamed && !caseCanopy)
            {
                s.Warn("No canopy: it is filled with zeros, so the fire is surface fire only - no crown fire.",
                    WorkflowAction.OpenSourceLayers, "Add canopy");
            }

            //Buildings: all five or none.
            string[] buildings = { e.BuildingAreaFile, e.BuildingSeparationFile, e.BuildingNonBurnableFractionFile,
                e.BuildingFootprintFractionFile, e.BuildingFuelModelFile };
            int namedBuildings = 0;
            foreach (string b in buildings) if (!string.IsNullOrEmpty(b)) ++namedBuildings;
            int caseBuildings = 0;
            foreach (string stem in BuildingStems) if (Exists(ScenarioFiles.CaseInput(_in, stem + ".tif"))) ++caseBuildings;
            int effectiveBuildings = Math.Max(namedBuildings, caseBuildings);
            if (effectiveBuildings > 0 && effectiveBuildings < 5)
            {
                s.Warn($"{effectiveBuildings} of the 5 building layers: building-to-building spread needs all five, so these are unused.",
                    WorkflowAction.OpenSourceLayers, "Source layers");
            }

            bool done = fuelSource || caseFuel != null;
            s.Status = done ? StepStatus.Done : StepStatus.ToDo;

            string fuelText = fuelSource ? Path.GetFileName(e.FuelModelFile) : caseFuel != null ? caseFuel + " (in the case)" : "no fuel";
            string canopyText = canopyNamed ? "canopy named" : caseCanopy ? "canopy in the case" : "no canopy";
            string buildingText = effectiveBuildings == 0 ? "no buildings" : $"buildings {effectiveBuildings}/5";
            s.Summary = $"{fuelText}; {canopyText}; {buildingText}";

            //LANDFIRE, for US ground only.
            Vector2d ll = _in.Simulation.LowerLeftLatLon;
            Vector2d deg = PREACT.Population.LocalGPWData.SizeToDegrees(ll, _in.Simulation.DomainSize);
            Vector2d ur = new Vector2d(ll.x + deg.y, ll.y + deg.x);
            var landfire = new StepAction(WorkflowAction.DownloadLandfire, "Get LANDFIRE fuels and canopy (US)",
                "Downloads LANDFIRE's fuel model and canopy for the domain and names them as the source layers.");
            if (!PlaceIsUsable) landfire.Disable("Blocked by step 1 (Place and time): needs the area of interest.");
            else if (!ScenarioFiles.IsInLandfireCoverage(ll, ur)) landfire.Disable("LANDFIRE covers the United States only; this domain is outside it.");
            if (!done && landfire.Enabled)
            {
                s.Primary = landfire;
                s.Secondary.Add(new StepAction(WorkflowAction.OpenSourceLayers, "Source layers"));
            }
            else
            {
                s.Secondary.Add(landfire);
            }

            if (_files.Exists(Abs(_case + "/" + PREACT.Utility.ElmfireCaseBuilder.SourceManifestName)))
            {
                s.Secondary.Add(new StepAction(WorkflowAction.ReadSourcesFromCase, "Read sources from the case",
                    "Restores the source layer paths the case was last built from (" + PREACT.Utility.ElmfireCaseBuilder.SourceManifestName + ")."));
            }
            return s;
        }

        private WorkflowStep ImportedFire()
        {
            WorkflowStep s = New(WorkflowStepId.Fuels, "Imported fire rasters");
            s.Required = true;
            s.Primary = new StepAction(WorkflowAction.OpenImportedFire, "Imported fire settings");
            AscImportInput a = _in.WildfireModule.AscImportInput;

            int present = 0;
            foreach ((string label, string path) in new[] { ("TimeOfArrivalFile", a.TimeOfArrivalFile),
                ("RateOfSpreadFile", a.RateOfSpreadFile), ("SpreadDirectionFile", a.SpreadDirectionFile) })
            {
                if (string.IsNullOrEmpty(path)) s.Info(label + " is not set.");
                else if (!Exists(path)) s.Error($"{label} names {path}, which does not exist.", WorkflowAction.OpenImportedFire, "Fix");
                else ++present;
            }

            s.Status = present == 3 ? StepStatus.Done : StepStatus.ToDo;
            s.Summary = present == 3 ? "Arrival time, spread rate and direction: " + (PaintGrid != null ? PaintGrid.Describe() : "set")
                : $"{present} of the 3 required rasters";
            return s;
        }

        // ------------------------------------------------------------------ 5. the fire case (or terrain)

        private static readonly string[] WeatherStems = { "ws", "wd", "m1", "m10", "m100" };

        private WorkflowStep FireCase()
        {
            if (IsImportedFire || !FireOn || !IsElmfire)
            {
                return Terrain();
            }

            WorkflowStep s = New(WorkflowStepId.FireCase);
            s.Required = true;
            ElmfireInput e = _in.WildfireModule.ElmfireInput;

            if (!PlaceIsUsable) BlockBy(s, WorkflowStepId.PlaceAndTime, "the case is cut to the area of interest.");

            var missing = new List<string>();
            if (_caseGrid == null) missing.Add("dem");
            bool haveFuel = Exists(ScenarioFiles.CaseInput(_in, "fbfm40.tif")) || Exists(ScenarioFiles.CaseInput(_in, "fbfm13.tif"));
            if (!haveFuel) missing.Add("fuel model");
            foreach (string stem in WeatherStems) if (!Exists(ScenarioFiles.CaseInput(_in, stem + ".tif"))) missing.Add(stem);
            string namelist = ScenarioFiles.ElmfireNamelist(_in);
            if (!Exists(namelist)) missing.Add("elmfire.data");

            bool caseExists = _files.DirectoryExists(Abs(_case));
            bool done = missing.Count == 0;

            if (!done)
            {
                if (!caseExists) s.Info($"No fire case yet ({_case}). Building it downloads the terrain, warps the source layers, "
                    + "draws a historical fire-weather day and writes the namelist. It takes minutes.");
                else s.Info("The case is missing: " + string.Join(", ", missing) + ".");
            }

            //What the build needs.
            if (!haveFuel && string.IsNullOrEmpty(e.FuelModelFile))
            {
                BlockBy(s, WorkflowStepId.Fuels, "the case needs a fuel model raster to warp.");
            }
            if (_caseGrid == null && _ctx.Tools.Probed && !_ctx.Tools.HaveOpenTopographyKey)
            {
                BlockBy(s, WorkflowStepId.None, "The case's terrain is downloaded from OpenTopography, and no API key is set "
                    + "(Help > External tools and keys says where one goes).");
                s.Error("No OpenTopography API key.", WorkflowAction.OpenExternalTools, "Where it goes");
            }

            if (_ctx.Tools.Probed)
            {
                if (!_ctx.Tools.HaveElmfire) s.Error("No ELMFIRE executable was found, so no fire can be computed.", WorkflowAction.OpenExternalTools, "External tools");
                if (!_ctx.Tools.HaveGdal) s.Warn("No GDAL command-line tools were found. ELMFIRE shells out to them, and fails its own DEM check without them.",
                    WorkflowAction.OpenExternalTools, "External tools");
                if (!_ctx.Tools.HaveWindNinja) s.Warn("No WindNinja: the case gets one wind value for the whole domain, so a trigger boundary comes out circular.",
                    WorkflowAction.OpenExternalTools, "External tools");
            }

            if (done)
            {
                //Weather long enough for the fire.
                RasterInfo ws = Raster(ScenarioFiles.CaseInput(_in, "ws.tif"));
                double dt = e.Namelist.DT_METEOROLOGY > 0 ? e.Namelist.DT_METEOROLOGY : 3600.0;
                int needed = (int)Math.Ceiling(e.SimulationTstopHours * 3600.0 / dt) + 1;
                if (ws != null && ws.Bands < needed)
                {
                    s.Warn($"ws.tif holds {ws.Bands} weather band(s) but a {e.SimulationTstopHours:0.#} h fire needs {needed}; ELMFIRE refuses the run.",
                        WorkflowAction.RebuildFireCase, "Rebuild the weather");
                }

                //Sources changed since the build.
                string built = Abs(namelist);
                foreach (KeyValuePair<string, string> layer in e.GetSourceRasters())
                {
                    if (!string.IsNullOrEmpty(layer.Value) && Exists(layer.Value) && _files.IsNewer(Abs(layer.Value), built))
                    {
                        s.Warn($"{Path.GetFileName(layer.Value)} ({layer.Key}) changed after the case was built.",
                            WorkflowAction.RebuildFireCase, "Rebuild the case");
                    }
                }

                //The scenario's own terrain should be the case's, or the map and the groups are on another grid.
                string elevation = _in.Landscape.ElevationFile;
                string caseDem = ScenarioFiles.CaseInput(_in, "dem.tif");
                if (string.IsNullOrEmpty(_in.Landscape.LandscapeFile) && elevation?.Replace('\\', '/') != caseDem)
                {
                    RasterInfo own = Exists(elevation) ? Raster(elevation) : null;
                    // V1-INTEGRATION: C1 - once BuildCaseOnly sets [Landscape] to the case terrain, a fresh build never shows this.
                    s.Warn("[Landscape] " + (string.IsNullOrEmpty(elevation) ? "names no terrain" : $"uses {elevation}"
                        + (own != null && !own.SameSize(_caseGrid) ? $" ({own.Width} x {own.Height})" : ""))
                        + $", not the fire case's {caseDem} ({_caseGrid.Width} x {_caseGrid.Height}).",
                        WorkflowAction.AdoptCaseTerrain, "Use the case terrain");
                }
            }

            s.Status = done ? StepStatus.Done : StepStatus.ToDo;
            if (done)
            {
                RasterInfo ws = Raster(ScenarioFiles.CaseInput(_in, "ws.tif"));
                s.Summary = $"{_case}: {_caseGrid.Describe()}" + (ws != null ? $", {ws.Bands} weather band(s)" : "");
            }
            else
            {
                s.Summary = caseExists ? "Incomplete: " + string.Join(", ", missing) + " missing" : "Not built";
            }

            s.Primary = new StepAction(WorkflowAction.BuildFireCase, done ? "Build missing layers" : "Build fire case",
                "Builds only what the case does not already have. Takes minutes: a DEM, weather through WindNinja and Nelson, and the namelist.");
            s.Secondary.Add(new StepAction(WorkflowAction.RebuildFireCase, "Rebuild everything",
                "Replaces every layer, the weather and the namelist - for a changed domain, cell size or source layer."));
            s.Secondary.Add(new StepAction(WorkflowAction.OpenFireModelSettings, "Fire model settings"));
            s.Secondary.Add(new StepAction(WorkflowAction.OpenFireBehaviour, "Fire behaviour"));
            s.Secondary.Add(new StepAction(WorkflowAction.PreviewNamelist, "Preview namelist"));
            return s;
        }

        private WorkflowStep Terrain()
        {
            WorkflowStep s = New(WorkflowStepId.FireCase, "Terrain (DEM)");
            s.Applicable = false;
            string reference = _in.Landscape.GetReferenceFile();
            bool have = Exists(reference);
            s.Summary = have ? $"{reference} ({Raster(reference)?.Describe()})" : "No terrain; optional without an ELMFIRE fire.";
            s.Primary = new StepAction(WorkflowAction.DownloadDemOnly, have ? "Download again" : "Download a DEM",
                "Downloads a DEM from OpenTopography, warps it into the simulation's UTM zone, derives slope and aspect.");
            if (!PlaceIsUsable) s.Primary.Disable("Blocked by step 1 (Place and time): needs the area of interest.");
            else if (_ctx.Tools.Probed && !_ctx.Tools.HaveOpenTopographyKey) s.Primary.Disable("No OpenTopography API key (Help > External tools and keys).");
            if (FireOn && !IsElmfire && !IsImportedFire)
            {
                s.Applicable = true;
                s.Error("The fire is switched on but no fire module is chosen.", WorkflowAction.OpenFireModelSettings, "Choose ELMFIRE");
            }
            return s;
        }

        // ------------------------------------------------------------------ 6. fire areas

        private WorkflowStep FireAreas()
        {
            WorkflowStep s = New(WorkflowStepId.FireAreas);
            s.Primary = new StepAction(WorkflowAction.OpenFireAreas, "Fire areas");

            if (!FireOn)
            {
                s.Applicable = false;
                s.Summary = "No fire in this scenario.";
                return s;
            }
            s.Required = true;

            int points = _in.WildfireModule.Data.IgnitionPoints.Count;

            if (PaintGrid == null)
            {
                if (IsElmfire) BlockBy(s, WorkflowStepId.FireCase, "an ELMFIRE scenario is painted on the case's dem.tif, which does not exist yet.");
                else if (IsImportedFire) BlockBy(s, WorkflowStepId.Fuels, "an imported fire is painted on its arrival-time raster.");
            }

            bool wuiNeeded = TriggerOn && _in.TriggerBufferModule.kPERILInput.WuiAreaSource == kPERILInput.WuiAreaSources.Raster
                             && string.IsNullOrEmpty(_in.TriggerBufferModule.kPERILInput.WuiAreaFile);
            bool onGrid = false;

            if (!string.IsNullOrEmpty(_paintedPath) && _painted == null)
            {
                s.Error($"GraphicalFireInputFile names {_paintedPath}, which " + (Exists(_paintedPath) ? "cannot be read." : "does not exist."),
                    WorkflowAction.OpenFireAreas, "Paint again");
            }
            else if (_painted != null && PaintGrid != null && !_painted.SameSize(PaintGrid))
            {
                s.Error($"The areas were painted on a {_painted.Width} x {_painted.Height} grid; the fire grid ({PaintGridReference}) is "
                    + $"{PaintGrid.Width} x {PaintGrid.Height}. They cannot be applied to it - repaint them on the fire grid.",
                    WorkflowAction.OpenFireAreas, "Repaint");
            }
            else if (_painted != null)
            {
                onGrid = PaintGrid != null;
            }

            int wui = _painted?.WuiCells ?? 0;
            int area = _painted?.IgnitionAreaCells ?? 0;
            int initial = _painted?.InitialIgnitionCells ?? 0;

            if (wuiNeeded && wui == 0)
            {
                s.Info("Paint the WUI area: it is what the trigger boundary protects.");
            }

            bool hasIgnition = points > 0 || (onGrid && (area > 0 || initial > 0));
            if (!hasIgnition)
            {
                s.Info("No ignition: place an ignition point, or paint an initial ignition or an area fires may start in.");
            }
            else if (IsElmfire && area == 0 && points > 0)
            {
                s.Info("No ignition area: a campaign then draws its ignitions from anywhere burnable in the domain.");
            }

            if (IsElmfire && onGrid)
            {
                string mask = ScenarioFiles.CaseInput(_in, "ignition_mask.tif");
                string wuiRaster = ScenarioFiles.CaseInput(_in, "wui_area.tif");
                bool stale = (area > 0 && (!Exists(mask) || _files.IsNewer(Abs(_paintedPath), Abs(mask))))
                             || (wui > 0 && (!Exists(wuiRaster) || _files.IsNewer(Abs(_paintedPath), Abs(wuiRaster))));
                if (stale)
                {
                    s.Warn("The painted areas are newer than the case's ignition_mask.tif / wui_area.tif: apply them to the case.",
                        WorkflowAction.ApplyFireAreasToCase, "Apply to case");
                }
            }

            if (_ctx.UnsavedFireStrokes)
            {
                s.Warn("Painted strokes not saved yet.", WorkflowAction.Save, "Save");
            }

            bool done = hasIgnition && (!wuiNeeded || (onGrid && wui > 0)) && (string.IsNullOrEmpty(_paintedPath) || onGrid);
            s.Status = done ? StepStatus.Done : StepStatus.ToDo;
            s.Summary = $"WUI {wui} cells, ignition area {area} cells, {Plural(points, "ignition point")}"
                        + (PaintGrid != null ? "; grid " + PaintGrid.Width + " x " + PaintGrid.Height : "");

            if (IsElmfire)
            {
                var apply = new StepAction(WorkflowAction.ApplyFireAreasToCase, "Apply to case",
                    "Rebuilds the case keeping its layers, so the painted areas become its ignition_mask.tif and wui_area.tif. "
                    + "The build reads the painted areas from their file, so unsaved strokes are offered a save first.");
                if (_painted == null && !_ctx.UnsavedFireStrokes) apply.Disable("Nothing painted yet.");
                else if (_painted != null && !onGrid) apply.Disable("The painted areas are not on the case grid; repaint them first.");
                s.Secondary.Add(apply);
            }
            return s;
        }

        // ------------------------------------------------------------------ 7. destinations

        private WorkflowStep Destinations()
        {
            WorkflowStep s = New(WorkflowStepId.Destinations);
            s.Applicable = EvacuationOn;
            s.Required = EvacuationOn;
            s.Primary = new StepAction(WorkflowAction.OpenDestinations, "Destinations");
            if (!s.Applicable)
            {
                s.Summary = "No evacuation in this scenario.";
                return s;
            }

            int count = _in.Evacuation.EvacuationDestinationInputs.Count;
            int far = 0;
            foreach (EvacuationDestinationInput d in _in.Evacuation.EvacuationDestinationInputs.Values)
            {
                if (d.LatLon.x == 0.0 && d.LatLon.y == 0.0)
                {
                    s.Error($"{d.Name} has no position.", WorkflowAction.OpenDestinations, "Place it");
                    continue;
                }

                double? distance = _ctx.DistanceToLane?.Invoke(d.LatLon);
                if (distance.HasValue && distance.Value > 25.0)
                {
                    ++far;
                    s.Warn($"{d.Name} is {distance.Value:F0} m from the nearest lane; SUMO silently uses whichever road is nearest.",
                        WorkflowAction.OpenDestinations, "Snap it");
                }
            }

            if (count == 0) s.Info("Add at least one place people evacuate to - an exit from the domain, or a shelter.");
            s.Status = count > 0 ? StepStatus.Done : StepStatus.ToDo;
            s.Summary = Plural(count, "destination") + (far > 0 ? $", {far} off the road network" : "");
            return s;
        }

        // ------------------------------------------------------------------ 8. curves and demographics

        private WorkflowStep CurvesAndDemographics()
        {
            WorkflowStep s = New(WorkflowStepId.CurvesAndDemographics);
            s.Applicable = EvacuationOn;
            s.Required = EvacuationOn;
            s.Primary = new StepAction(WorkflowAction.OpenCurves, "Response curves");
            s.Secondary.Add(new StepAction(WorkflowAction.OpenDemographics, "Demographics"));
            if (!s.Applicable)
            {
                s.Summary = "No evacuation in this scenario.";
                return s;
            }

            int validCurves = 0;
            foreach (ResponseCurve c in _in.Evacuation.ResponseCurves.Values)
            {
                ResponseDataPoint[] p = c.DataPoints;
                if (p == null || p.Length < 2)
                {
                    s.Error($"Response curve {c.Name} has fewer than two points.", WorkflowAction.OpenCurves, "Edit");
                    continue;
                }

                bool monotone = true;
                for (int i = 1; i < p.Length; ++i)
                {
                    if (p[i].Time <= p[i - 1].Time || p[i].Probability < p[i - 1].Probability) monotone = false;
                }
                if (!monotone) s.Warn($"Response curve {c.Name} is not rising in time and probability.", WorkflowAction.OpenCurves, "Edit");
                if (p[p.Length - 1].Probability < 0.999f)
                {
                    s.Warn($"Response curve {c.Name} ends at {p[p.Length - 1].Probability:F2}, so some households never leave.",
                        WorkflowAction.OpenCurves, "Edit");
                }
                ++validCurves;
            }

            int demographics = _in.Population.Demographics.Count;
            bool haveDefault = false;
            foreach (DemographicsInput d in _in.Population.Demographics.Values) haveDefault |= d.Default;

            if (_in.Evacuation.ResponseCurves.Count == 0) s.Info("Add a response curve: when households set off after the order.");
            if (demographics == 0) s.Info("Add demographics: how many cars a household takes.");
            else if (!haveDefault) s.Warn("No demographics is marked Default, which households whose group names none get.",
                WorkflowAction.OpenDemographics, "Choose one");

            s.Status = validCurves > 0 && demographics > 0 ? StepStatus.Done : StepStatus.ToDo;
            s.Summary = $"{Plural(_in.Evacuation.ResponseCurves.Count, "curve")}, {Plural(demographics, "demographics", "demographics")}";
            return s;
        }

        // ------------------------------------------------------------------ 9. evacuation groups

        private WorkflowStep EvacuationGroups()
        {
            WorkflowStep s = New(WorkflowStepId.EvacuationGroups);
            s.Applicable = EvacuationOn;
            s.Required = EvacuationOn;
            s.Primary = new StepAction(WorkflowAction.OpenGroups, "Evacuation groups");
            if (!s.Applicable)
            {
                s.Summary = "No evacuation in this scenario.";
                return s;
            }

            var paint = new StepAction(WorkflowAction.PaintGroups, "Paint group areas");
            if (PaintGrid == null)
            {
                paint.Disable(IsElmfire
                    ? "Blocked by step 5 (Fire case): groups are painted on the case's dem.tif, which does not exist yet."
                    : "There is no grid to paint on yet.");
            }
            s.Secondary.Add(paint);

            var groups = _in.Evacuation.EvacuationGroupInputs;
            var destinations = _in.Evacuation.EvacuationDestinationInputs;
            var curves = _in.Evacuation.ResponseCurves;
            var demographics = _in.Population.Demographics;

            if (groups.Count == 0)
            {
                if (destinations.Count == 0) BlockBy(s, WorkflowStepId.Destinations, "a group needs somewhere to go.");
                else if (curves.Count == 0) BlockBy(s, WorkflowStepId.CurvesAndDemographics, "a group needs a response curve.");
                s.Info("Add a group: who is ordered to leave, when, and where to.");
            }

            Vector2d origin = _in.Simulation.Data.UTMOrigin;
            foreach (EvacuationGroupInput g in groups.Values)
            {
                if (!string.IsNullOrEmpty(g.MaskFile))
                {
                    if (!Exists(g.MaskFile))
                    {
                        s.Error($"Group {g.Name}: its mask {g.MaskFile} does not exist.", WorkflowAction.PaintGroups, "Paint it");
                    }
                    else if (PaintGrid != null)
                    {
                        RasterInfo mask = Raster(g.MaskFile);
                        //Masks are written in simulation space (the grid's corner minus the UTM origin), which is
                        //the frame EvacuationGroup.LoadMask reads them in.
                        if (mask == null || !PaintGrid.SameGrid(mask, origin.x, origin.y))
                        {
                            s.Error($"Group {g.Name}: its mask is " + (mask == null ? "unreadable" : $"{mask.Width} x {mask.Height}")
                                + $", not on the fire grid ({PaintGrid.Width} x {PaintGrid.Height}). Paint it again.",
                                WorkflowAction.PaintGroups, "Repaint");
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(g.ShapeFile))
                {
                    if (!Exists(g.ShapeFile)) s.Error($"Group {g.Name}: its shapefile {g.ShapeFile} does not exist.", WorkflowAction.OpenGroups, "Fix");
                }
                else
                {
                    s.Error($"Group {g.Name} has no area: paint it, or name a shapefile.", WorkflowAction.PaintGroups, "Paint it");
                }

                foreach (string d in g.Destinations)
                {
                    if (!destinations.ContainsKey(d)) s.Error($"Group {g.Name} names destination \"{d}\", which does not exist.", WorkflowAction.OpenGroups, "Fix");
                }
                foreach (string c in g.ResponseCurves)
                {
                    if (!curves.ContainsKey(c)) s.Error($"Group {g.Name} names response curve \"{c}\", which does not exist.", WorkflowAction.OpenGroups, "Fix");
                }
                if (!string.IsNullOrEmpty(g.Demographics) && !demographics.ContainsKey(g.Demographics))
                {
                    s.Error($"Group {g.Name} names demographics \"{g.Demographics}\", which does not exist.", WorkflowAction.OpenGroups, "Fix");
                }
                if (g.Destinations.Count == 0) s.Error($"Group {g.Name} has no destinations.", WorkflowAction.OpenGroups, "Fix");
                if (g.ResponseCurves.Count == 0) s.Error($"Group {g.Name} has no response curve.", WorkflowAction.OpenGroups, "Fix");
                if (g.DestinationsCDF.Count > 0 && g.DestinationsCDF[g.DestinationsCDF.Count - 1] < 0.999)
                    s.Warn($"Group {g.Name}: its destinations' cumulative distribution ends below 1.", WorkflowAction.OpenGroups, "Fix");
                if (g.ResponseCurvesCDF.Count > 0 && g.ResponseCurvesCDF[g.ResponseCurvesCDF.Count - 1] < 0.999)
                    s.Warn($"Group {g.Name}: its response curves' cumulative distribution ends below 1.", WorkflowAction.OpenGroups, "Fix");
            }

            if (_ctx.UnsavedGroupStrokes) s.Warn("Painted group strokes not saved yet.", WorkflowAction.Save, "Save");

            s.Status = groups.Count > 0 && !s.HasErrors ? StepStatus.Done : StepStatus.ToDo;
            s.Summary = Plural(groups.Count, "group");
            return s;
        }

        // ------------------------------------------------------------------ 10. trigger boundary

        private WorkflowStep TriggerBoundary()
        {
            WorkflowStep s = New(WorkflowStepId.TriggerBoundary);
            s.Primary = new StepAction(WorkflowAction.OpenTriggerBoundary, "Trigger boundary");

            if (!FireOn)
            {
                s.Applicable = false;
                s.Summary = "Needs a fire: the boundary is worked back from its arrival times.";
                return s;
            }
            if (!TriggerOn)
            {
                s.Applicable = false;
                s.Summary = "Off.";
                return s;
            }
            s.Required = true;

            kPERILInput k = _in.TriggerBufferModule.kPERILInput;
            string caseWui = ScenarioFiles.CaseInput(_in, "wui_area.tif");
            bool caseWuiExists = IsElmfire && Exists(caseWui);
            bool protectedOk = false;

            if (IsElmfire && _caseGrid == null) BlockBy(s, WorkflowStepId.FireCase, "k-PERIL computes on the case grid.");

            if (k.WuiAreaSource == kPERILInput.WuiAreaSources.Raster)
            {
                if (!string.IsNullOrEmpty(k.WuiAreaFile))
                {
                    RasterInfo wui = Exists(k.WuiAreaFile) ? Raster(k.WuiAreaFile) : null;
                    if (wui == null) s.Error($"WuiAreaFile names {k.WuiAreaFile}, which " + (Exists(k.WuiAreaFile) ? "cannot be read." : "does not exist."),
                        caseWuiExists ? WorkflowAction.UseCaseWuiArea : WorkflowAction.OpenTriggerBoundary, caseWuiExists ? "Use " + caseWui : "Fix");
                    else if (PaintGrid != null && !wui.SameSize(PaintGrid)) s.Error($"WuiAreaFile is {wui.Width} x {wui.Height}, not on the fire grid "
                        + $"({PaintGrid.Width} x {PaintGrid.Height}); k-PERIL refuses it and the run stops.",
                        caseWuiExists ? WorkflowAction.UseCaseWuiArea : WorkflowAction.OpenTriggerBoundary, caseWuiExists ? "Use " + caseWui : "Fix");
                    else protectedOk = true;
                }
                else if (_painted != null && _painted.WuiCells > 0)
                {
                    if (PaintGrid != null && !_painted.SameSize(PaintGrid))
                    {
                        s.Error("No WuiAreaFile, so k-PERIL falls back to the painted WUI area - which is on another grid "
                            + $"({_painted.Width} x {_painted.Height}), so it refuses it and the run stops.",
                            caseWuiExists ? WorkflowAction.UseCaseWuiArea : WorkflowAction.OpenFireAreas,
                            caseWuiExists ? "Use " + caseWui : "Repaint");
                    }
                    else
                    {
                        protectedOk = true;
                        // V1-INTEGRATION: C1 - a case build will set WuiAreaFile to the case's wui_area.tif itself.
                        if (caseWuiExists) s.Warn($"k-PERIL is reading the painted mask directly; the case build rasterised it as {caseWui}.",
                            WorkflowAction.UseCaseWuiArea, "Use " + caseWui);
                    }
                }
                else
                {
                    s.Info("Nothing to protect yet: paint the WUI area (step 6), or name a WuiAreaFile.");
                }
            }
            else
            {
                bool anyArea = false;
                foreach (EvacuationGroupInput g in _in.Evacuation.EvacuationGroupInputs.Values)
                {
                    anyArea |= !string.IsNullOrEmpty(g.MaskFile) || !string.IsNullOrEmpty(g.ShapeFile);
                }
                if (!anyArea) s.Error("The protected area comes from the evacuation groups, and no group has an area.",
                    WorkflowAction.PaintGroups, "Paint groups");
                else protectedOk = true;
            }

            //The wind k-PERIL takes. For an ELMFIRE fire it is the fire's own; a path pinned into the scenario by a
            //run that was then saved overrides that.
            if (IsElmfire && !string.IsNullOrEmpty(k.WindSpeedFile))
            {
                bool pinned = k.WindSpeedFile.Replace('\\', '/').EndsWith("inputs/ws.tif");
                // V1-INTEGRATION: C4 - a run no longer writes these into the scenario once the engine stops mutating its input.
                s.Warn(pinned
                    ? $"WindSpeedFile names {k.WindSpeedFile} - pinned by an earlier run that was then saved. It overrides the fire's own wind; clear it."
                    : $"WindSpeedFile ({k.WindSpeedFile}) overrides the wind of the fire itself.",
                    WorkflowAction.ClearPinnedWind, "Clear it");
            }

            if (IsElmfire && _in.WildfireModule.ElmfireInput.SimulationTstopHours < 24.0)
            {
                s.Warn($"The fire stops after {_in.WildfireModule.ElmfireInput.SimulationTstopHours:0.#} h; no boundary is computed "
                    + "for an area the fire has not reached by then.", WorkflowAction.OpenFireModelSettings, "Fire duration");
            }

            s.Status = protectedOk ? StepStatus.Done : StepStatus.ToDo;
            s.Summary = "k-PERIL, protecting " + (k.WuiAreaSource == kPERILInput.WuiAreaSources.Raster
                ? (string.IsNullOrEmpty(k.WuiAreaFile) ? "the painted WUI area" : Path.GetFileName(k.WuiAreaFile))
                : "the evacuation groups (" + k.WuiAreaSource + ")");
            return s;
        }

        // ------------------------------------------------------------------ the scenario check

        /// <summary>
        /// What the parser reported when the scenario was last read or checked, filed under the step it is
        /// about. Only counted against a step when it is still about the scenario as it is now.
        /// </summary>
        private void AddCheckFindings()
        {
            if (_ctx.Requirements == null) return;

            foreach (PREACTInput.InputRequirement r in _ctx.Requirements)
            {
                if (!r.Critical) continue;
                WorkflowStep s = this[StepFor(r)] ?? this[WorkflowStepId.PlaceAndTime];
                if (s == null || !s.Applicable) continue;

                string text = $"Scenario check: [{(string.IsNullOrEmpty(r.Section) ? "Scenario" : r.Section)}] {r.Key} - {r.Message}";

                //A value the parser could not read is wrong whatever else is true, and stops a run. A missing
                //input restates what an unfinished step already says, so it only counts against a step whose
                //own rules think it is finished - that disagreement is worth surfacing.
                bool unreadable = (r.Message ?? string.Empty).IndexOf("could not be interpreted", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!_ctx.RequirementsFresh) s.Info(text + " (found before your latest edits)", WorkflowAction.CheckScenario, "Check again");
                else if (unreadable || s.Status == StepStatus.Done) s.Error(text, WorkflowAction.CheckScenario, "Check again");
                else s.Info(text, WorkflowAction.CheckScenario, "Check again");
            }
        }

        /// <summary>Which step a parser finding belongs to.</summary>
        public static WorkflowStepId StepFor(PREACTInput.InputRequirement r)
        {
            string section = (r.Section ?? string.Empty).ToLowerInvariant();
            string key = (r.Key ?? string.Empty).ToLowerInvariant();

            if (key.Contains("destination")) return WorkflowStepId.Destinations;
            if (key.Contains("responsecurve") || key.Contains("demographic")) return WorkflowStepId.CurvesAndDemographics;
            if (key.Contains("evacuationgroup") || key.Contains("maskfile") || key.Contains("shapefile")) return WorkflowStepId.EvacuationGroups;

            switch (section)
            {
                case "trafficmodule":
                case "sumo":
                    return WorkflowStepId.Roads;
                case "population":
                case "pedestrianmodule":
                case "macrohouseholdsim":
                    return WorkflowStepId.Population;
                case "ascimport":
                    return WorkflowStepId.Fuels;
                case "elmfire":
                    return key.Contains("fuel") || key.Contains("canopy") || key.Contains("building")
                        ? WorkflowStepId.Fuels : WorkflowStepId.FireCase;
                case "wildfiremodule":
                    return key.Contains("graphical") || key.Contains("ignition") ? WorkflowStepId.FireAreas : WorkflowStepId.FireCase;
                case "evacuation":
                    return WorkflowStepId.EvacuationGroups;
                case "triggerbuffermodule":
                case "kperil":
                    return WorkflowStepId.TriggerBoundary;
                case "smokemodule":
                case "globalsmoke":
                    return WorkflowStepId.FireCase;
                default:
                    return WorkflowStepId.PlaceAndTime;
            }
        }

        // ------------------------------------------------------------------ 11. run

        private string OutputFolder => Path.Combine(_root, "_output");

        /// <summary>
        /// Where the scenario's campaign results are: its newest <c>_output/campaign_&lt;name&gt;_&lt;hash&gt;</c>
        /// folder, or <c>_output</c> itself for a campaign run before campaigns had folders of their own.
        /// </summary>
        public static string CampaignFolderOf(PREACTInput input, string scenarioPath)
        {
            if (input == null) return null;
            string name = PREACT.Utility.CampaignLayout.CampaignScenarioName(input.Simulation?.Name, scenarioPath);
            return PREACT.Utility.CampaignLayout.LatestCampaignFolder(input.RootFolder, name)
                   ?? Path.Combine(input.RootFolder, PREACT.Utility.CampaignLayout.OutputFolder);
        }

        private WorkflowStep RunSimulation()
        {
            WorkflowStep s = New(WorkflowStepId.RunSimulation);
            s.Primary = new StepAction(WorkflowAction.OpenRun, "Run simulation...");

            foreach (WorkflowStep step in _steps)
            {
                if (!step.Applicable || !step.Required) continue;
                //Statuses are finalised after every rule has run, so read the facts rather than the status.
                bool blocked = !string.IsNullOrEmpty(step.BlockedBy);
                bool notDone = step.Status != StepStatus.Done;
                if (blocked || notDone || step.HasErrors)
                {
                    RunBlockers.Add($"Step {step.Number} ({step.Title}): " + FirstProblem(step));
                }
                else if (step.HasWarnings)
                {
                    RunWarnings.Add($"Step {step.Number} ({step.Title}): " + FirstProblem(step));
                }
            }

            if (_ctx.CampaignActive) RunBlockers.Add("A trigger campaign is running on the same case.");

            string log = Path.Combine(OutputFolder, _name + ".log");
            DateTime? scenarioWritten = string.IsNullOrEmpty(_ctx.ScenarioPath) ? null : _files.LastWriteUtc(_ctx.ScenarioPath);
            DateTime? logWritten = _files.LastWriteUtc(log);
            bool ranSinceSaved = logWritten.HasValue && (!scenarioWritten.HasValue || logWritten.Value >= scenarioWritten.Value);

            if (_ctx.LastRunFailed == true)
            {
                s.Warn("The last run ended in an error; the console says why.", WorkflowAction.OpenRun, "Run again");
            }

            if (RunBlockers.Count > 0)
            {
                BlockBy(s, WorkflowStepId.None, RunBlockers[0] + (RunBlockers.Count > 1 ? $" (and {RunBlockers.Count - 1} more)" : ""));
                s.Primary = new StepAction(WorkflowAction.OpenRun, "Run simulation...");
            }

            s.Status = ranSinceSaved ? StepStatus.Done : StepStatus.ToDo;
            s.Summary = logWritten.HasValue
                ? $"Last run {logWritten.Value.ToLocalTime():yyyy-MM-dd HH:mm}" + (ranSinceSaved ? "" : " (before the scenario was last saved)")
                : "Not run yet";
            return s;
        }

        private static string FirstProblem(WorkflowStep step)
        {
            if (!string.IsNullOrEmpty(step.BlockedBy)) return step.BlockedBy;
            foreach (StepIssue i in step.Issues) if (i.Level == IssueLevel.Error) return i.Text;
            //A step that simply has not been done is blocked by that, not by whichever warning comes first.
            if (step.Status != StepStatus.Done)
            {
                return "not done yet" + (string.IsNullOrEmpty(step.Summary) ? "." : " (" + step.Summary + ").");
            }
            foreach (StepIssue i in step.Issues) if (i.Level == IssueLevel.Warning) return i.Text;
            foreach (StepIssue i in step.Issues) return i.Text;
            return "not done.";
        }

        // ------------------------------------------------------------------ 12. results

        /// <summary>The recognised result files in _output, newest first, recomputed when the folder changes.</summary>
        public IReadOnlyList<string> ResultFiles { get; private set; } = new List<string>();

        private DateTime _outputListedAt;
        private string _outputListedFor;
        private List<string> _outputListing = new List<string>();

        /// <summary>
        /// The run's results in <c>_output</c> and the campaign's in its own folder (when that is not
        /// <c>_output</c> itself), newest first.
        /// </summary>
        private List<string> ListOutput()
        {
            string folder = OutputFolder;
            if (!_files.DirectoryExists(folder)) return new List<string>();

            string campaign = _campaignFolder != null && !SameFolder(_campaignFolder, folder) ? _campaignFolder : null;
            DateTime written;
            try
            {
                written = Directory.GetLastWriteTimeUtc(folder);
                if (campaign != null)
                {
                    DateTime campaignWritten = Directory.GetLastWriteTimeUtc(campaign);
                    if (campaignWritten > written) written = campaignWritten;
                }
            }
            catch { return new List<string>(); }

            string key = folder + "|" + campaign;
            if (_outputListedFor == key && written == _outputListedAt) return _outputListing;

            var found = new List<string>();
            try
            {
                foreach (string f in Directory.GetFiles(folder))
                {
                    if (IsResult(Path.GetFileName(f), campaign == null)) found.Add(f);
                }
                if (campaign != null)
                {
                    foreach (string f in Directory.GetFiles(campaign))
                    {
                        if (IsCampaignResult(Path.GetFileName(f).ToLowerInvariant())) found.Add(f);
                    }
                }
            }
            catch { }

            found.Sort((a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            _outputListing = found;
            _outputListedAt = written;
            _outputListedFor = key;
            return found;
        }

        /// <summary>
        /// A run result in <c>_output</c>; with <paramref name="legacyCampaign"/> also the campaign files an
        /// older campaign wrote there, before campaigns had folders of their own.
        /// </summary>
        private bool IsResult(string file, bool legacyCampaign)
        {
            string lower = file.ToLowerInvariant();
            string name = _name.ToLowerInvariant();
            string output = (_in.TriggerBufferModule?.kPERILInput?.OutputName ?? "trigger_boundary").ToLowerInvariant();
            return lower == name + ".log"
                   || (lower.StartsWith(name + "_") && lower.EndsWith("_arrivaldata.csv") && !lower.Contains("_prob_"))
                   || (legacyCampaign && IsCampaignResult(lower))
                   || lower.Contains("_" + output);
        }

        /// <summary>A file a campaign writes that is worth showing: its rasters and its convergence CSV.</summary>
        public static bool IsCampaignResult(string lower)
        {
            return lower.StartsWith("trigger_probability")
                   || lower.StartsWith(PREACT.Utility.CampaignLayout.EnsemblePrefix + "_")
                   || lower == PREACT.Utility.CampaignLayout.ConvergenceCsv;
        }

        private static bool SameFolder(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private WorkflowStep Results()
        {
            WorkflowStep s = New(WorkflowStepId.Results);
            s.Primary = new StepAction(WorkflowAction.OpenResults, "Results");

            List<string> files = ListOutput();
            ResultFiles = files;
            if (files.Count == 0)
            {
                BlockBy(s, WorkflowStepId.RunSimulation, "nothing has been run yet.");
                s.Summary = "No results yet.";
                return s;
            }

            int boundaries = 0, campaign = 0;
            bool log = false;
            string output = (_in.TriggerBufferModule?.kPERILInput?.OutputName ?? "trigger_boundary").ToLowerInvariant();
            foreach (string f in files)
            {
                string lower = Path.GetFileName(f).ToLowerInvariant();
                if (lower.Contains("_" + output)) ++boundaries;
                else if (lower.StartsWith("trigger_probability") || lower.StartsWith("ensemble_")) ++campaign;
                else if (lower.EndsWith(".log")) log = true;
            }

            s.Status = StepStatus.Done;
            var parts = new List<string>();
            if (log) parts.Add("run log");
            if (boundaries > 0) parts.Add(Plural(boundaries, "trigger boundary", "trigger boundaries"));
            if (campaign > 0) parts.Add(Plural(campaign, "campaign raster"));
            s.Summary = parts.Count > 0 ? string.Join(", ", parts) : Plural(files.Count, "file");
            return s;
        }

        // ------------------------------------------------------------------ 13. campaign

        /// <summary>What trigger_convergence.csv says: realizations done, and the last row's streak.</summary>
        private sealed class Convergence
        {
            public int Rows;
            public int Streak;
        }

        private static Convergence ReadConvergence(string path)
        {
            var c = new Convergence();
            string last = null;
            int streakColumn = -1;
            using (var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
            {
                //By name: the campaign CLI's columns are "boundaries,realization_id,streak,...", an older one's
                //"run,realization_id,nSuccess,streak,...".
                string header = reader.ReadLine();
                if (header != null) streakColumn = Array.IndexOf(header.Split(','), "streak");
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ++c.Rows;
                    last = line;
                }
            }
            if (last != null && streakColumn >= 0)
            {
                string[] parts = last.Split(',');
                if (parts.Length > streakColumn) int.TryParse(parts[streakColumn], out c.Streak);
            }
            return c;
        }

        private WorkflowStep Campaign()
        {
            WorkflowStep s = New(WorkflowStepId.Campaign);
            s.Primary = new StepAction(WorkflowAction.OpenCampaign, "Campaign...");

            if (!IsElmfire || !TriggerOn)
            {
                s.Applicable = false;
                s.Summary = "Needs an ELMFIRE fire and a trigger boundary.";
                return s;
            }

            string folder = _campaignFolder ?? OutputFolder;
            string final = Path.Combine(folder, PREACT.Utility.CampaignLayout.ProbabilityRaster);
            string live = Path.Combine(folder, PREACT.Utility.CampaignLayout.LiveProbabilityRaster);
            string csv = Path.Combine(folder, PREACT.Utility.CampaignLayout.ConvergenceCsv);
            Convergence conv = _files.Read(csv, "convergence", ReadConvergence, null);

            WorkflowStep fireCase = this[WorkflowStepId.FireCase];
            WorkflowStep areas = this[WorkflowStepId.FireAreas];
            if (fireCase != null && (fireCase.Status != StepStatus.Done || !string.IsNullOrEmpty(fireCase.BlockedBy)))
            {
                BlockBy(s, WorkflowStepId.FireCase, "every realization runs on the case, which is not complete.");
            }
            else if (areas != null && areas.Issues.Exists(i => i.Fix == WorkflowAction.ApplyFireAreasToCase || i.Fix == WorkflowAction.OpenFireAreas && i.Level == IssueLevel.Error))
            {
                BlockBy(s, WorkflowStepId.FireAreas, "the painted areas are not applied to the case (or not on its grid).");
            }
            else if (_ctx.SimulationActive)
            {
                BlockBy(s, WorkflowStepId.None, "A simulation started from the GUI is using the case.");
            }

            if (_ctx.IsDirty && !_ctx.CampaignActive)
            {
                //Not a blocker: the campaign window asks to save when Run is pressed.
                s.Info("Unsaved changes: the campaign reads the scenario from its .wui, so Run asks to save them first.");
            }

            bool done = _files.Exists(final);
            if (!done && _files.Exists(live))
            {
                s.Warn("Only the live probability raster exists: the last campaign stopped or is still running.",
                    WorkflowAction.OpenCampaign, "Campaign");
            }

            s.Status = done ? StepStatus.Done : StepStatus.ToDo;
            string where = SameFolder(folder, OutputFolder) ? "trigger_convergence.csv" : Path.GetFileName(folder);
            s.Summary = conv != null && conv.Rows > 0
                ? $"{Plural(conv.Rows, "boundary", "boundaries")} in {where}, streak {conv.Streak}" + (done ? "" : "; no final raster")
                : "Not run";

            //Whatever blocks a new campaign, its results can still be read.
            s.Secondary.Add(new StepAction(WorkflowAction.OpenResults, "Results"));
            return s;
        }
    }
}
