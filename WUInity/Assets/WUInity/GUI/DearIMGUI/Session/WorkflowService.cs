using System;
using System.Collections.Generic;
using PREACT.Input;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The GUI's copy of the workflow: refreshed when something happens (a scenario opened, an edit, a step
    /// or run finished, a probe landed) and otherwise at most once a second, never per frame.
    /// </summary>
    /// <remarks>
    /// Also where a workflow action becomes something the GUI does: the model names actions, this opens the
    /// window or starts the step. The workflow panel, the menus and the issue fix buttons all go through
    /// <see cref="Perform"/>, so a menu item and its row cannot do different things.
    /// </remarks>
    public static class WorkflowService
    {
        private static readonly ScenarioWorkflow _model = new ScenarioWorkflow(GuiFiles.Probe);
        private static bool _invalid = true;
        private static float _nextRefresh;
        private static bool _subscribed;

        //The scenario check's findings, copied when they were produced, and the edit generation they are for.
        private static readonly List<PREACTInput.InputRequirement> _requirements = new List<PREACTInput.InputRequirement>();
        private static int _requirementsGeneration = -1;

        public static ScenarioWorkflow Model { get => _model; }

        public static WorkflowStep Step(WorkflowStepId id) => _model[id];

        public static IReadOnlyList<PREACTInput.InputRequirement> Requirements { get => _requirements; }
        public static bool RequirementsFresh { get => _requirementsGeneration == ScenarioSession.EditGeneration; }

        /// <summary>Recompute at the next opportunity.</summary>
        public static void Invalidate()
        {
            _invalid = true;
        }

        /// <summary>Takes a copy of PREACTInput.Requirements as they stand, for the scenario as it is now.</summary>
        public static void TakeRequirements(IEnumerable<PREACTInput.InputRequirement> requirements)
        {
            TakeRequirements(requirements, ScenarioSession.EditGeneration);
        }

        /// <summary>Findings for the scenario as it was at <paramref name="generation"/> (a check that ran on a copy).</summary>
        public static void TakeRequirements(IEnumerable<PREACTInput.InputRequirement> requirements, int generation)
        {
            _requirements.Clear();
            if (requirements != null) _requirements.AddRange(requirements);
            _requirementsGeneration = generation;
            Invalidate();
        }

        private static void Subscribe()
        {
            if (_subscribed) return;
            _subscribed = true;

            ScenarioSession.ScenarioChanged += () =>
            {
                //What the parser said while reading this scenario - read right after the load, before anything
                //else parses. The findings are for the scenario exactly as loaded.
                TakeRequirements(ScenarioSession.HasInput ? PREACTInput.Requirements : null);
                Invalidate();
            };
            ScenarioSession.Edited += Invalidate;
            ScenarioSession.Saved += Invalidate;
            ScenarioDataSteps.StepFinished += (step, ok) => Invalidate();
            ToolsService.Changed += Invalidate;
            if (PreactGUI.WUInity != null)
            {
                PreactGUI.WUInity.RunFinished += Invalidate;
            }
        }

        /// <summary>Once per frame: refreshes the model when it is stale.</summary>
        public static void Tick()
        {
            Subscribe();

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!_invalid && now < _nextRefresh)
            {
                return;
            }

            _invalid = false;
            _nextRefresh = now + 1f;
            Refresh();
        }

        private static void Refresh()
        {
            global::WUInity.Painter painter = PreactGUI.WUInity?.Painter;
            var ctx = new WorkflowContext
            {
                Input = ScenarioSession.Input,
                ScenarioPath = ScenarioSession.FilePath,
                IsDirty = ScenarioSession.IsDirty,
                DirtySummary = ScenarioSession.DirtySummary,
                UnsavedFireStrokes = painter != null && painter.UnsavedFireStrokes,
                UnsavedGroupStrokes = painter != null && painter.UnsavedGroupStrokes,
                SimulationActive = ScenarioSession.SimulationActive,
                StepActive = ScenarioSession.StepActive,
                StepOwner = ScenarioDataSteps.Owner,
                StepTitle = ScenarioDataSteps.CurrentTitle,
                CampaignActive = ScenarioSession.CampaignActive,
                Tools = ToolsService.Current,
                LastRunFailed = PreactGUI.WUInity?.LastRunFailed,
                LastRunRefusedBecause = PreactGUI.WUInity != null && PreactGUI.WUInity.LastRunRefused
                                        && PreactGUI.WUInity.LastRunRefusalReasons.Count > 0
                    ? PreactGUI.WUInity.LastRunRefusalReasons[0] : null,
                Requirements = _requirements,
                RequirementsFresh = RequirementsFresh,
                DistanceToLane = DistanceToLane,
            };

            try
            {
                _model.Refresh(ctx);
            }
            catch (Exception e)
            {
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning, "Could not compute the workflow: " + e.Message);
            }
        }

        /// <summary>Only when the road network has already been read; the workflow never makes it parse one.</summary>
        private static double? DistanceToLane(PREACT.Math.Vector2d latLon)
        {
            if (PreactGUI.WUInity == null || !PreactGUI.WUInity.HasRoadNetworkLoaded || !ScenarioSession.HasInput)
            {
                return null;
            }

            PREACT.Math.Vector2d pos = ScenarioSession.Input.Simulation.Data.GetSimulationPosition(latLon);
            return PreactGUI.WUInity.TrySnapToRoadNetwork(pos, out PREACT.Utility.SumoNetworkGeometry.Snap snap) ? snap.Distance : (double?)null;
        }

        /// <summary>Why a step's action is unavailable, for a menu item's tooltip; empty when it is available.</summary>
        public static string WhyNot(WorkflowStepId id)
        {
            WorkflowStep s = Step(id);
            if (s == null) return ScenarioSession.HasInput ? string.Empty : "No scenario is open.";
            if (s.Primary != null && !s.Primary.Enabled) return s.Primary.Tooltip;
            return s.Status == StepStatus.Blocked ? s.BlockedBy ?? string.Empty : string.Empty;
        }

        /// <summary>
        /// Why <paramref name="action"/> cannot be done now, naming the step in the way - the same words the
        /// workflow row shows; empty when it can. <paramref name="id"/> is the step the action belongs to.
        /// </summary>
        public static string WhyNot(WorkflowAction action, WorkflowStepId id)
        {
            if (!ScenarioSession.HasInput && action != WorkflowAction.OpenExternalTools)
            {
                return "No scenario is open.";
            }
            if (ScenarioSession.EditingLocked && ChangesScenario(action))
            {
                return ScenarioSession.BusyTooltip;
            }

            WorkflowStep s = Step(id);
            if (s == null)
            {
                return string.Empty;
            }

            StepAction a = Find(s, action);
            if (a != null)
            {
                return a.Enabled ? string.Empty : a.Tooltip ?? string.Empty;
            }

            //Not one of the row's buttons: opening a window to look is always fine, starting work is not.
            return s.Status == StepStatus.Blocked && ChangesScenario(action) ? s.BlockedBy ?? string.Empty : string.Empty;
        }

        /// <summary>What the model says an action does (its enabled tooltip), for a menu item.</summary>
        public static string Describe(WorkflowAction action, WorkflowStepId id)
        {
            WorkflowStep s = Step(id);
            StepAction a = s == null ? null : Find(s, action);
            return a != null && a.Enabled ? a.Tooltip ?? string.Empty : string.Empty;
        }

        private static StepAction Find(WorkflowStep s, WorkflowAction action)
        {
            if (s.Primary != null && s.Primary.Id == action) return s.Primary;
            foreach (StepAction a in s.Secondary)
            {
                if (a.Id == action) return a;
            }
            return null;
        }

        /// <summary>
        /// Actions that start work or change the scenario, and so wait while a simulation or a data step holds
        /// it. Opening a window to look is always allowed; the windows lock their own fields.
        /// </summary>
        public static bool ChangesScenario(WorkflowAction action)
        {
            switch (action)
            {
                case WorkflowAction.Save:
                case WorkflowAction.PrepareRoads:
                case WorkflowAction.RedoRoads:
                case WorkflowAction.UseSumoNetwork:
                case WorkflowAction.PreparePopulation:
                case WorkflowAction.RedoPopulation:
                case WorkflowAction.UseGeneratedPopulation:
                case WorkflowAction.DownloadLandfire:
                case WorkflowAction.ReadSourcesFromCase:
                case WorkflowAction.BuildFireCase:
                case WorkflowAction.RebuildFireCase:
                case WorkflowAction.AdoptCaseTerrain:
                case WorkflowAction.KeepCaseNamelist:
                case WorkflowAction.UseSetAsideNamelist:
                case WorkflowAction.DownloadDemOnly:
                case WorkflowAction.ApplyFireAreasToCase:
                case WorkflowAction.MovePaintingToCaseGrid:
                case WorkflowAction.UseCaseWuiArea:
                case WorkflowAction.ClearPinnedWind:
                case WorkflowAction.OpenCampaign:
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ doing things

        /// <summary>
        /// Moves the painting onto the paint grid, from the raster the model found it was painted on. Strokes made
        /// on the paint grid since and not saved would be replaced by it, so that is asked first.
        /// </summary>
        private static void MovePainting()
        {
            string source = _model.PaintedOnReference;
            if (!_model.PaintingOnOtherGrid || string.IsNullOrEmpty(source))
            {
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning, "The painting is not on another grid the scenario "
                    + "knows of, so there is nothing to move.");
                return;
            }

            string paintedOn = $"{_model.PaintedWidth} x {_model.PaintedHeight} ({source})";
            string target = _model.PaintGridReference;
            global::WUInity.Painter painter = PreactGUI.WUInity?.Painter;
            if (painter != null && painter.UnsavedFireStrokes)
            {
                ConfirmPrompt.AskToConfirm("Some areas were painted on " + target + " since the scenario was opened, and are "
                    + "not saved. Moving the saved painting from " + paintedOn + " onto that grid replaces them.",
                    "Move it, replacing them", () => ScenarioDataSteps.MovePaintingToGrid(source, target));
                return;
            }

            ScenarioDataSteps.MovePaintingToGrid(source, target);
        }

        /// <summary>Carries out a workflow action: opens the window it names or starts the work.</summary>
        public static void Perform(WorkflowAction action)
        {
            PREACTInput input = ScenarioSession.Input;
            switch (action)
            {
                case WorkflowAction.None:
                    return;
                case WorkflowAction.Save:
                    ScenarioSession.Save();
                    return;
                case WorkflowAction.CheckScenario:
                    ScenarioCheckWindow.OpenAndCheck();
                    return;
                case WorkflowAction.OpenExternalTools:
                    ExternalToolsWindow.Open();
                    return;
            }

            if (input == null)
            {
                return;
            }

            switch (action)
            {
                case WorkflowAction.EditPlaceAndTime: SettingsPageWindow.Open(SettingsPage.PlaceAndTime); break;

                case WorkflowAction.PrepareRoads: ScenarioDataSteps.PrepareRoads(false); break;
                case WorkflowAction.RedoRoads:
                    ConfirmPrompt.AskToConfirm("Download the roads again and rebuild the RouterDb and the SUMO network from them? "
                        + "This replaces the files the scenario has.", "Rebuild", () => ScenarioDataSteps.PrepareRoads(true));
                    break;
                case WorkflowAction.UseSumoNetwork:
                    {
                        SUMOInput sumo = input.TrafficModule.SumoInput;
                        string built = ScenarioFiles.SumoConfig;
                        sumo.ConfigurationFile = GuiFiles.Exists(GuiFiles.Resolve(input.RootFolder, built))
                            ? built
                            : (sumo.ConfigurationFile ?? string.Empty).Replace('\\', '/');
                        ScenarioSession.NotifyEdited("SUMO configuration");
                        break;
                    }
                case WorkflowAction.ShowRoadNetwork: PreactGUI.WUInity.ShowRoadNetwork(true); break;
                case WorkflowAction.OpenEvacuationModules: SettingsPageWindow.Open(SettingsPage.EvacuationModules); break;

                case WorkflowAction.PreparePopulation: ScenarioDataSteps.PreparePopulation(false); break;
                case WorkflowAction.RedoPopulation:
                    ConfirmPrompt.AskToConfirm("Generate the households again from WorldPop and the RouterDb? This replaces "
                        + ScenarioFiles.Population(input.Simulation.Name) + ".", "Regenerate", () => ScenarioDataSteps.PreparePopulation(true));
                    break;
                case WorkflowAction.UseGeneratedPopulation:
                    input.Population.PopulationFile = ScenarioFiles.Population(input.Simulation.Name);
                    ScenarioSession.NotifyEdited("population file");
                    break;

                case WorkflowAction.OpenSourceLayers: SourceLayersPanel.Open(); break;
                case WorkflowAction.DownloadLandfire: ScenarioDataSteps.DownloadLandfireFuels(); break;
                case WorkflowAction.ReadSourcesFromCase: SourceLayersPanel.ReadSourcesFromCase(input); break;
                case WorkflowAction.OpenImportedFire: SettingsPageWindow.Open(SettingsPage.FireModel); break;

                case WorkflowAction.BuildFireCase:
                    //The build reads the painted areas from their file, and records the case's sources against the
                    //scenario as it is; unsaved work is saved first, if wanted.
                    ConfirmPrompt.AskToSave("building the fire case", () => ScenarioDataSteps.BuildElmfireCase(false));
                    break;
                case WorkflowAction.RebuildFireCase:
                    ConfirmPrompt.AskToConfirm("Rebuild the fire case from scratch? Every layer, the weather and the namelist are "
                        + "made again - which takes minutes - and canopy with no source is refilled with zeros.", "Rebuild",
                        () => ConfirmPrompt.AskToSave("rebuilding the fire case", () => ScenarioDataSteps.BuildElmfireCase(true)));
                    break;
                case WorkflowAction.AdoptCaseTerrain:
                    ScenarioDataSteps.AdoptCaseTerrain(input);
                    ScenarioSession.NotifyEdited("case terrain");
                    break;
                case WorkflowAction.KeepCaseNamelist:
                    //Resolved against the case folder first, and a template that is the case's own namelist is left
                    //alone by every build.
                    input.WildfireModule.ElmfireInput.NamelistTemplate = "elmfire.data";
                    PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "[ELMFIRE] NamelistTemplate now names the case's own "
                        + "elmfire.data, so builds keep it as it is and the Fire behaviour settings no longer reach it. Clear "
                        + "NamelistTemplate (Fire model settings) to have it written from the scenario again.");
                    ScenarioSession.NotifyEdited("namelist template");
                    break;
                case WorkflowAction.UseSetAsideNamelist:
                    {
                        string kept = _model.SetAsideNamelist;
                        if (string.IsNullOrEmpty(kept)) break;
                        input.WildfireModule.ElmfireInput.NamelistTemplate = kept;
                        PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "[ELMFIRE] NamelistTemplate now names " + kept
                            + ": runs use it as it is (only the grid, weather and ignition are filled in), and the Fire behaviour "
                            + "settings no longer reach it.");
                        ScenarioSession.NotifyEdited("namelist template");
                        break;
                    }
                case WorkflowAction.OpenFireModelSettings: SettingsPageWindow.Open(SettingsPage.FireModel); break;
                case WorkflowAction.OpenFireBehaviour: SettingsPageWindow.Open(SettingsPage.FireBehaviour); break;
                case WorkflowAction.PreviewNamelist: Input.ElmfireNamelistPreviewWindow.Open(input.WildfireModule.ElmfireInput); break;
                case WorkflowAction.DownloadDemOnly: ScenarioDataSteps.DownloadDemOnly(); break;

                case WorkflowAction.OpenFireAreas: Editors.FireAreasWindow.Open(); break;
                case WorkflowAction.MovePaintingToCaseGrid: MovePainting(); break;
                case WorkflowAction.ApplyFireAreasToCase:
                    {
                        //The build reads the painted areas from their file, so applying strokes that are not saved would
                        //apply the old ones: here the only choice is to save first, or not to apply.
                        global::WUInity.Painter painter = PreactGUI.WUInity?.Painter;
                        if (painter != null && painter.UnsavedFireStrokes)
                        {
                            ConfirmPrompt.AskToConfirm("The painted areas have strokes that are not saved, and the case is built from "
                                + "the saved file. Save the scenario (and the painted areas) and apply them?", "Save and apply", () =>
                                {
                                    if (ScenarioSession.Save()) ScenarioDataSteps.ApplyPaintedAreasToCase();
                                });
                        }
                        else
                        {
                            ConfirmPrompt.AskToSave("applying the painted areas to the case", ScenarioDataSteps.ApplyPaintedAreasToCase);
                        }
                        break;
                    }

                case WorkflowAction.OpenDestinations: SettingsPageWindow.Open(SettingsPage.Destinations); break;
                case WorkflowAction.OpenCurves: SettingsPageWindow.Open(SettingsPage.ResponseCurves); break;
                case WorkflowAction.OpenDemographics: SettingsPageWindow.Open(SettingsPage.Demographics); break;
                case WorkflowAction.OpenGroups: SettingsPageWindow.Open(SettingsPage.EvacuationGroups); break;
                case WorkflowAction.PaintGroups: Editors.EvacuationGroupPaintWindow.Open(input.Evacuation.EvacuationGroupInputs); break;

                case WorkflowAction.OpenTriggerBoundary: SettingsPageWindow.Open(SettingsPage.TriggerBoundary); break;
                case WorkflowAction.UseCaseWuiArea:
                    ScenarioDataSteps.AdoptCaseWuiArea(input);
                    ScenarioSession.NotifyEdited("WUI area file");
                    break;
                case WorkflowAction.ClearPinnedWind:
                    input.TriggerBufferModule.kPERILInput.WindSpeedFile = string.Empty;
                    input.TriggerBufferModule.kPERILInput.WindDirectionFile = string.Empty;
                    ScenarioSession.NotifyEdited("k-PERIL wind");
                    break;

                case WorkflowAction.OpenRun: RunSimulationWindow.Open(); break;
                case WorkflowAction.OpenResults: ResultsWindow.Open(); break;
                case WorkflowAction.OpenCampaign:
                    //The window reseeds from the session and asks to save when Run is pressed: the campaign reads
                    //the .wui on disk, so that is when unsaved edits matter.
                    ProbabilisticTriggerWindow.Open();
                    break;
            }
        }
    }
}
