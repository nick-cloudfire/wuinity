using System;
using System.Collections.Generic;

namespace WUInity.Workflow
{
    /// <summary>Where a workflow step stands.</summary>
    public enum StepStatus
    {
        /// <summary>Everything this step is for exists and checks out.</summary>
        Done,
        /// <summary>It will run, or has run, but something is stale, suboptimal or wrong - see its issues.</summary>
        NeedsAttention,
        /// <summary>A prerequisite is missing; the step's action is disabled and says which.</summary>
        Blocked,
        /// <summary>Nothing is in the way; it just has not been done.</summary>
        ToDo,
        /// <summary>Not needed for this scenario as configured.</summary>
        Optional,
        /// <summary>A data step, the simulation or the campaign is working on it now.</summary>
        Running,
    }

    public enum IssueLevel { Info, Warning, Error }

    /// <summary>
    /// Everything a workflow row, an issue's fix button or a menu item can ask the GUI to do. The model names
    /// them; the GUI decides what each one opens or runs, so the model stays free of windows.
    /// </summary>
    public enum WorkflowAction
    {
        None,
        Save,
        CheckScenario,
        OpenExternalTools,

        EditPlaceAndTime,

        PrepareRoads,
        RedoRoads,
        UseSumoNetwork,
        ShowRoadNetwork,
        OpenEvacuationModules,

        PreparePopulation,
        RedoPopulation,
        UseGeneratedPopulation,

        OpenSourceLayers,
        DownloadLandfire,
        BurnRoadsIntoFuel,
        ReadSourcesFromCase,
        OpenImportedFire,

        BuildFireCase,
        RebuildFireCase,
        AdoptCaseTerrain,
        OpenFireModelSettings,
        OpenFireBehaviour,
        PreviewNamelist,
        DownloadDemOnly,
        KeepCaseNamelist,
        UseSetAsideNamelist,

        OpenFireAreas,
        ApplyFireAreasToCase,
        MovePaintingToCaseGrid,

        OpenDestinations,
        OpenCurves,
        OpenDemographics,
        OpenGroups,
        PaintGroups,

        OpenTriggerBoundary,
        UseCaseWuiArea,
        ClearPinnedWind,

        OpenRun,
        OpenResults,
        OpenCampaign,
    }

    /// <summary>A button: what it does, what it says, and - when it cannot be pressed - why not.</summary>
    public sealed class StepAction
    {
        public WorkflowAction Id;
        public string Label;
        public bool Enabled = true;

        /// <summary>Why it is disabled, naming the blocking step, or (when enabled) what it does.</summary>
        public string Tooltip = string.Empty;

        public StepAction(WorkflowAction id, string label, string tooltip = null)
        {
            Id = id;
            Label = label;
            Tooltip = tooltip ?? string.Empty;
        }

        public StepAction Disable(string why)
        {
            Enabled = false;
            Tooltip = why;
            return this;
        }
    }

    /// <summary>Something wrong or worth knowing about a step, with the action that fixes it if there is one.</summary>
    public sealed class StepIssue
    {
        public IssueLevel Level;
        public string Text;
        public WorkflowAction Fix;
        public string FixLabel;

        public StepIssue(IssueLevel level, string text, WorkflowAction fix = WorkflowAction.None, string fixLabel = null)
        {
            Level = level;
            Text = text;
            Fix = fix;
            FixLabel = fixLabel;
        }
    }

    /// <summary>One row of the workflow: status, one-line summary, what is wrong, and what to do.</summary>
    public sealed class WorkflowStep
    {
        public WorkflowStepId Id;
        public int Number => (int)Id;
        public string Title;
        public StepStatus Status;
        public string Summary = string.Empty;

        /// <summary>A run needs this step done (for the scenario as configured).</summary>
        public bool Required;

        /// <summary>The step applies to this scenario at all.</summary>
        public bool Applicable = true;

        /// <summary>What is in the way, naming the step to go to, when <see cref="Status"/> is Blocked.</summary>
        public string BlockedBy;
        public WorkflowStepId BlockedByStep;

        public StepAction Primary;
        public readonly List<StepAction> Secondary = new List<StepAction>();
        public readonly List<StepIssue> Issues = new List<StepIssue>();

        public bool HasErrors
        {
            get
            {
                foreach (StepIssue i in Issues) if (i.Level == IssueLevel.Error) return true;
                return false;
            }
        }

        public bool HasWarnings
        {
            get
            {
                foreach (StepIssue i in Issues) if (i.Level != IssueLevel.Info) return true;
                return false;
            }
        }

        public void Error(string text, WorkflowAction fix = WorkflowAction.None, string fixLabel = null)
            => Issues.Add(new StepIssue(IssueLevel.Error, text, fix, fixLabel));

        public void Warn(string text, WorkflowAction fix = WorkflowAction.None, string fixLabel = null)
            => Issues.Add(new StepIssue(IssueLevel.Warning, text, fix, fixLabel));

        public void Info(string text, WorkflowAction fix = WorkflowAction.None, string fixLabel = null)
            => Issues.Add(new StepIssue(IssueLevel.Info, text, fix, fixLabel));

        public string StatusLabel
        {
            get
            {
                switch (Status)
                {
                    case StepStatus.Done: return "Done";
                    case StepStatus.NeedsAttention: return "Needs attention";
                    case StepStatus.Blocked: return "Blocked";
                    case StepStatus.ToDo: return "To do";
                    case StepStatus.Optional: return "Optional";
                    case StepStatus.Running: return "Running";
                    default: return Status.ToString();
                }
            }
        }
    }

    /// <summary>
    /// Everything the workflow is computed from besides the scenario's files: the scenario itself and the
    /// GUI's state. Filled by the GUI on the main thread; a console test fills it by hand.
    /// </summary>
    public sealed class WorkflowContext
    {
        public PREACT.Input.PREACTInput Input;

        /// <summary>The .wui the scenario was read from or last saved to.</summary>
        public string ScenarioPath;

        public bool IsDirty;
        public string DirtySummary = string.Empty;
        public bool UnsavedFireStrokes;
        public bool UnsavedGroupStrokes;

        public bool SimulationActive;
        public bool StepActive;
        public WorkflowStepId StepOwner;
        public string StepTitle = string.Empty;
        public bool CampaignActive;

        public ExternalToolsSnapshot Tools = new ExternalToolsSnapshot();

        /// <summary>Whether the last GUI run failed; null when there has been none this session.</summary>
        public bool? LastRunFailed;

        /// <summary>The last GUI run never started: the engine refused the scenario. Its first reason, or null.</summary>
        public string LastRunRefusedBecause;

        /// <summary>The scenario check's findings (PREACTInput.Requirements), copied when it last ran.</summary>
        public IList<PREACT.Input.PREACTInput.InputRequirement> Requirements;

        /// <summary>True when those findings are for the scenario as it is now (no edits since).</summary>
        public bool RequirementsFresh;

        /// <summary>
        /// Distance in metres from a WGS84 point to the nearest road lane, when the road network is loaded;
        /// null (the delegate or its answer) when there is no network to measure against.
        /// </summary>
        public Func<PREACT.Math.Vector2d, double?> DistanceToLane;
    }
}
