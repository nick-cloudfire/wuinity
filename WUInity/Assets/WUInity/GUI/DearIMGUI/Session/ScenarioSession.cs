using System;
using PREACT.Input;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The scenario the GUI is working on, and whether anything is currently working on it.
    /// </summary>
    /// <remarks>
    /// There was no single answer to "is it safe to load another scenario / edit this one / start a run". The
    /// menu tested <c>Simulation.IsRunning</c> (which a crashed run never resets, so the menus stayed disabled
    /// until restart), File &gt; Load tested nothing (so a scenario could be swapped out from under a running
    /// data step, which then wrote its paths into the new one), and the data steps and the campaign had flags
    /// of their own that nothing else read. This is the one place those are combined.
    /// </remarks>
    public static class ScenarioSession
    {
        private static PREACTInput _input;

        public static PREACTInput Input { get => _input; }
        public static bool HasInput { get => _input != null; }
        public static string RootFolder { get => _input?.RootFolder; }

        /// <summary>The .wui the scenario was read from or last written to.</summary>
        public static string FilePath { get => _input == null ? null : PreactGUI.Engine?.WorkingFile; }

        /// <summary>Raised on the main thread whenever a different scenario (or none) becomes the current one.</summary>
        public static event Action ScenarioChanged;

        /// <summary>
        /// Called by the manager whenever the engine takes on an input - a load, a new scenario, a reload.
        /// The engine calls back synchronously from those, which only the GUI's main thread initiates.
        /// </summary>
        public static void OnEngineInput(PREACTInput input)
        {
            _input = input;
            ScenarioChanged?.Invoke();
        }

        // ------------------------------------------------------------------ busy state

        /// <summary>A GUI-started simulation run has not finished (including ELMFIRE computing inside it).</summary>
        public static bool SimulationActive
        {
            get { return PreactGUI.WUInity != null && PreactGUI.WUInity.IsSimulationActive; }
        }

        /// <summary>A data-preparation step or chain is running on its worker.</summary>
        public static bool StepActive { get => ScenarioDataSteps.Busy; }

        /// <summary>A probabilistic trigger campaign process is running.</summary>
        public static bool CampaignActive { get => CampaignProbe.IsRunning; }

        /// <summary>Anything at all is running: no other scenario may be opened, created or closed.</summary>
        public static bool IsBusy { get => SimulationActive || StepActive || CampaignActive; }

        /// <summary>
        /// Something is running that reads or writes the scenario in memory, so it must not be edited. The
        /// campaign is not in this list: it runs as its own process on the .wui on disk.
        /// </summary>
        public static bool EditingLocked { get => SimulationActive || StepActive; }

        /// <summary>What is running, as a phrase ("a simulation is running"), or null when nothing is.</summary>
        public static string BusyReason
        {
            get
            {
                if (SimulationActive) return "a simulation is running";
                if (StepActive) return "\"" + ScenarioDataSteps.CurrentTitle + "\" is running";
                if (CampaignActive) return "a trigger campaign is running";
                return null;
            }
        }

        /// <summary>The tooltip for anything disabled because of <see cref="IsBusy"/>.</summary>
        public static string BusyTooltip
        {
            get
            {
                string reason = BusyReason;
                return reason == null ? string.Empty : "Not while " + reason + ". Wait for it to finish, or stop it.";
            }
        }
    }
}
