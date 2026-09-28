using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using PREACT;
using PREACT.Input;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The scenario the GUI is working on: which one, whether it has unsaved changes, whether anything is
    /// working on it, and the only way to open, create, save or close one.
    /// </summary>
    /// <remarks>
    /// Before this, the loaded input was a static field of the scenario editor, nine different actions
    /// mutated it and printed "save the scenario to keep it", nothing knew whether it had been, Ctrl+S was
    /// shown in the menu and implemented nowhere, and loading or quitting discarded edits silently.
    ///
    /// Unsaved changes are found by comparison, not by bookkeeping: the scenario is serialised exactly as
    /// Save would write it, and compared with what was last loaded or saved. That catches an edit made
    /// anywhere - a tab, an editor, a data step setting a path - without every one of them having to
    /// remember to say so. Painted strokes, which live outside the .wui until their own files are written,
    /// are asked of the painter.
    /// </remarks>
    public static class ScenarioSession
    {
        private static PREACTInput _input;

        //The scenario as Save would write it, when it was last loaded or saved.
        private static string _savedSnapshot = string.Empty;
        private static bool _settingsDirty;
        private static float _nextPoll;
        private const float PollSeconds = 0.5f;
        private static bool _quitConfirmed;

        public static PREACTInput Input { get => _input; }
        public static bool HasInput { get => _input != null; }
        public static string RootFolder { get => _input?.RootFolder; }

        /// <summary>The .wui the scenario was read from or last written to.</summary>
        public static string FilePath { get => _input == null ? null : PreactGUI.Engine?.WorkingFile; }

        /// <summary>The scenario's name, or its file name when it has none.</summary>
        public static string DisplayName
        {
            get
            {
                if (_input == null) return "(no scenario)";
                if (!string.IsNullOrWhiteSpace(_input.Simulation?.Name)) return _input.Simulation.Name;
                return Path.GetFileNameWithoutExtension(FilePath ?? "scenario");
            }
        }

        /// <summary>Counts every detected change to the scenario, so a cached answer can tell it is stale.</summary>
        public static int EditGeneration { get; private set; }

        /// <summary>Raised on the main thread whenever a different scenario (or none) becomes the current one.</summary>
        public static event Action ScenarioChanged;

        /// <summary>Raised on the main thread when the scenario was changed, or when it was saved.</summary>
        public static event Action Edited;

        public static event Action Saved;

        // ------------------------------------------------------------------ engine callback

        /// <summary>
        /// Called by the manager whenever the engine takes on an input - a load, a new scenario, a reload.
        /// The engine calls back synchronously from those, which only the GUI's main thread initiates.
        /// </summary>
        public static void OnEngineInput(PREACTInput input)
        {
            _input = input;
            _savedSnapshot = Serialise(input);
            _settingsDirty = false;
            ++EditGeneration;
            ScenarioChanged?.Invoke();
            Edited?.Invoke();
        }

        // ------------------------------------------------------------------ unsaved changes

        /// <summary>Anything that would be lost by closing now.</summary>
        public static bool IsDirty
        {
            get
            {
                if (_input == null) return false;
                global::WUInity.Painter painter = PreactGUI.WUInity?.Painter;
                return _settingsDirty
                       || (painter != null && (painter.UnsavedFireStrokes || painter.UnsavedGroupStrokes));
            }
        }

        /// <summary>What is unsaved, as a short list for a tooltip or a prompt.</summary>
        public static string DirtySummary
        {
            get
            {
                var parts = new List<string>();
                if (_settingsDirty) parts.Add("scenario settings");
                global::WUInity.Painter painter = PreactGUI.WUInity?.Painter;
                if (painter != null && painter.UnsavedFireStrokes) parts.Add("painted fire areas");
                if (painter != null && painter.UnsavedGroupStrokes) parts.Add("painted group areas");
                return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
            }
        }

        /// <summary>
        /// Something changed the scenario: look now rather than at the next poll, and tell whoever caches
        /// answers about it. <paramref name="reason"/> is for the log only.
        /// </summary>
        public static void NotifyEdited(string reason = null)
        {
            _nextPoll = 0f;
            Poll(true);
            ++EditGeneration;
            Edited?.Invoke();
        }

        /// <summary>
        /// Re-serialises the scenario and compares it with the saved one. Twice a second at most, and not
        /// while a run or a data step holds the scenario - they change it from another thread, and what they
        /// change is theirs to report when they finish.
        /// </summary>
        private static void Poll(bool force)
        {
            if (_input == null || EditingLocked)
            {
                return;
            }

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (!force && now < _nextPoll)
            {
                return;
            }
            _nextPoll = now + PollSeconds;

            bool dirty = Serialise(_input) != _savedSnapshot;
            if (dirty != _settingsDirty)
            {
                _settingsDirty = dirty;
                ++EditGeneration;
                Edited?.Invoke();
            }
        }

        private static string _lastSerialiseError;

        /// <summary>The scenario exactly as Save would write it.</summary>
        public static string Serialise(PREACTInput input)
        {
            if (input == null) return string.Empty;
            try
            {
                return string.Join("\n", PREACTInputWriter.Write(input));
            }
            catch (Exception e)
            {
                //Reported once, not twice a second.
                if (_lastSerialiseError != e.Message)
                {
                    _lastSerialiseError = e.Message;
                    Engine.Message(null, Engine.LogType.Warning, "Could not serialise the scenario to check for unsaved changes: " + e.Message);
                }
                return "\u0000unserialisable";
            }
        }

        // ------------------------------------------------------------------ open / new / save / close

        /// <summary>File &gt; Open: asks to save first, then picks a file.</summary>
        public static void RequestOpen()
        {
            if (!CheckNotBusy("open another scenario")) return;
            ConfirmPrompt.AskToSave("opening another scenario", FileBrowser.OpenLoadInput);
        }

        /// <summary>Opens a scenario by path (the recent list, the picker), asking to save first.</summary>
        public static void RequestOpen(string path)
        {
            if (!CheckNotBusy("open another scenario")) return;
            ConfirmPrompt.AskToSave("opening another scenario", () => Load(path));
        }

        /// <summary>File &gt; New: asks to save first, then shows the new-scenario dialog.</summary>
        public static void RequestNew()
        {
            if (!CheckNotBusy("create a scenario")) return;
            ConfirmPrompt.AskToSave("creating a new scenario", () => NewScenarioDialog.Open());
        }

        public static void RequestClose()
        {
            if (!CheckNotBusy("close the scenario")) return;
            ConfirmPrompt.AskToSave("closing it", Close);
        }

        /// <summary>
        /// Reads a scenario and makes it current. Incomplete scenarios are opened too - building one up over
        /// several sittings is the normal way to make one.
        /// </summary>
        public static bool Load(string path)
        {
            if (!CheckNotBusy("open another scenario")) return false;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Engine.Message(null, Engine.LogType.Warning, "There is no scenario at " + path + ".");
                return false;
            }

            PREACTInput before = _input;
            PreactGUI.Engine.LoadInputFromFile(path, out bool complete);

            //LoadInputFromFile calls back into OnEngineInput when it takes the input on; if it did not, the
            //file could not be parsed at all and the previous scenario is still the current one.
            if (_input == null || _input == before)
            {
                Engine.Message(null, Engine.LogType.Warning, "Could not read " + path + "; see the messages above.");
                return false;
            }

            ScenarioCheckWindow.NoteLoaded(Path.GetFileName(path));
            return true;
        }

        /// <summary>Writes a freshly made scenario to <paramref name="path"/> and opens it from there.</summary>
        public static bool CreateAndOpen(PREACTInput input, string path)
        {
            if (!CheckNotBusy("create a scenario")) return false;
            if (!WriteFile(input, path)) return false;
            return Load(path);
        }

        /// <summary>
        /// Saves everything: painted areas first (they are files the .wui refers to), then the scenario.
        /// </summary>
        public static bool Save()
        {
            if (_input == null)
            {
                return false;
            }

            if (EditingLocked)
            {
                Engine.Message(null, Engine.LogType.Warning, "Not saved: " + BusyReason + ". "
                    + "Saving now would also write whatever the run has changed in the scenario.");
                return false;
            }

            string path = FilePath;
            if (string.IsNullOrEmpty(path))
            {
                Engine.Message(null, Engine.LogType.Warning, "The scenario has no file yet; use File > Save as.");
                return false;
            }

            SavePaintings();

            if (!WriteFile(_input, path))
            {
                return false;
            }

            _savedSnapshot = Serialise(_input);
            _settingsDirty = false;
            ++EditGeneration;
            global::WUInity.RecentScenario.Remember(path);
            Saved?.Invoke();
            Edited?.Invoke();
            return true;
        }

        /// <summary>
        /// Writes painted fire areas and group masks that have strokes nobody saved, and points the scenario
        /// at them - so Save saves what is on screen, rather than only what the .wui holds.
        /// </summary>
        private static void SavePaintings()
        {
            global::WUInity.Painter painter = PreactGUI.WUInity?.Painter;
            if (painter == null)
            {
                return;
            }

            if (painter.UnsavedFireStrokes)
            {
                string written = painter.SavePaintedFireAreas(_input.RootFolder);
                if (!string.IsNullOrEmpty(written))
                {
                    _input.WildfireModule.GraphicalFireInputFile = written;
                }
            }

            if (painter.UnsavedGroupStrokes)
            {
                Editors.EvacuationGroupPaintWindow.SaveMasksFor(_input);
            }
        }

        /// <summary>
        /// Save as, into the scenario's own folder only.
        /// </summary>
        /// <remarks>
        /// Every path in a scenario is relative to its folder. Writing it into another folder and reopening
        /// it from there - which is what this did - silently re-pointed every one of them: population, SUMO,
        /// case, painted areas and masks all resolved into the new folder, where none of them were. A copy
        /// elsewhere is <see cref="CopyTo"/>, which takes the files along.
        /// </remarks>
        public static bool SaveAs(string path)
        {
            if (_input == null || string.IsNullOrEmpty(path)) return false;
            if (!CheckNotBusy("save as")) return false;

            if (!Path.GetExtension(path).Equals(".wui", StringComparison.OrdinalIgnoreCase))
            {
                path += ".wui";
            }

            if (!SameFolder(Path.GetDirectoryName(Path.GetFullPath(path)), _input.RootFolder))
            {
                Engine.Message(null, Engine.LogType.Warning, "Save as writes into the scenario's own folder ("
                    + _input.RootFolder + "), because every path in it is relative to that folder. To put the "
                    + "scenario somewhere else, use File > Copy scenario to..., which copies its files too.");
                return false;
            }

            SavePaintings();
            if (!WriteFile(_input, path)) return false;

            //Rebinds the engine to the new file; the scenario object itself is the same one.
            PreactGUI.Engine.SetInput(_input, path);
            global::WUInity.RecentScenario.Remember(path);
            Saved?.Invoke();
            return true;
        }

        /// <summary>
        /// Copies the scenario's folder - without its outputs and campaign realizations - to
        /// <paramref name="destinationParent"/>, writes the scenario as it is now into the copy, and opens it.
        /// </summary>
        public static void CopyTo(string destinationParent, bool includeOutputs)
        {
            if (_input == null || string.IsNullOrEmpty(destinationParent)) return;
            if (!CheckNotBusy("copy the scenario")) return;

            string source = Path.GetFullPath(_input.RootFolder);
            string destination = Path.Combine(destinationParent, new DirectoryInfo(source).Name);
            if (SameFolder(destination, source) || Path.GetFullPath(destination).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                Engine.Message(null, Engine.LogType.Warning, "Pick a folder outside the scenario to copy it into.");
                return;
            }
            if (Directory.Exists(destination) && Directory.GetFileSystemEntries(destination).Length > 0)
            {
                Engine.Message(null, Engine.LogType.Warning, destination + " already exists and is not empty; pick another folder.");
                return;
            }

            //What the copy's .wui will say: the scenario as it stands, unsaved edits included, serialised
            //here on the main thread so the worker never reads the live scenario.
            string[] lines = PREACTInputWriter.Write(_input);
            string fileName = Path.GetFileName(FilePath ?? (DisplayName + ".wui"));

            foreach (string path in AbsoluteOrParentPaths(lines))
            {
                Engine.Message(null, Engine.LogType.Warning, "The copy still refers to " + path
                    + ", which is outside the scenario folder and is not copied.");
            }

            ScenarioDataSteps.RunUtility("Copying the scenario", () =>
            {
                int files = CopyFolder(source, destination, includeOutputs);
                File.WriteAllLines(Path.Combine(destination, fileName), lines);
                ScenarioDataSteps.LogStep($"Copied {files} file(s) into {destination}.");
                return Path.Combine(destination, fileName);
            }, copied =>
            {
                //The original's unsaved state went with the copy, so nothing is lost by switching to it.
                _settingsDirty = false;
                Load(copied);
            });
        }

        private static readonly string[] NotCopied = { "_output", "_elmfire", "scratch", ".checklist-recheck.wui.tmp" };

        private static int CopyFolder(string from, string to, bool includeOutputs)
        {
            Directory.CreateDirectory(to);
            int count = 0;
            foreach (string file in Directory.GetFiles(from))
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), false);
                ++count;
            }
            foreach (string dir in Directory.GetDirectories(from))
            {
                string name = Path.GetFileName(dir);
                if (!includeOutputs && Array.IndexOf(NotCopied, name) >= 0)
                {
                    continue;
                }
                count += CopyFolder(dir, Path.Combine(to, name), includeOutputs);
            }
            return count;
        }

        /// <summary>Values in a serialised scenario that are absolute paths or climb out of its folder.</summary>
        private static IEnumerable<string> AbsoluteOrParentPaths(string[] lines)
        {
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string value = line.Substring(eq + 1).Trim();
                if (value.StartsWith("..") || (value.Length > 2 && value[1] == ':' && (value[2] == '\\' || value[2] == '/'))
                    || value.StartsWith("/"))
                {
                    yield return value;
                }
            }
        }

        /// <summary>Stops working on the scenario. The engine keeps its copy until the next one is opened.</summary>
        public static void Close()
        {
            if (_input == null) return;

            global::WUInity.RecentScenario.Forget();
            PreactGUI.WUInity?.Painter?.ResetForScenario();
            _input = null;
            _savedSnapshot = string.Empty;
            _settingsDirty = false;
            ++EditGeneration;
            PreactGUI.WUInity?.ShowWebMercatorMap();
            ScenarioChanged?.Invoke();
            Edited?.Invoke();
        }

        private static bool WriteFile(PREACTInput input, string path)
        {
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                File.WriteAllLines(path, PREACTInputWriter.Write(input));
                Engine.Message(null, Engine.LogType.Log, "Saved " + path + ".");
                return true;
            }
            catch (Exception e)
            {
                //Not through PREACTInput.SaveToDisk, which reports a failure as a SimulationError - the log type
                //that also stops any running simulation.
                Engine.Message(null, Engine.LogType.Warning, "Could not save " + path + ": " + e.Message);
                return false;
            }
        }

        private static bool SameFolder(string a, string b)
        {
            try
            {
                string fa = Path.GetFullPath(a).TrimEnd('\\', '/');
                string fb = Path.GetFullPath(b).TrimEnd('\\', '/');
                return string.Equals(fa, fb, Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static bool CheckNotBusy(string what)
        {
            if (!IsBusy) return true;
            Engine.Message(null, Engine.LogType.Warning, "Cannot " + what + " while " + BusyReason + ".");
            return false;
        }

        // ------------------------------------------------------------------ quitting

        /// <summary>
        /// Unity asks before quitting a player; returning false keeps it open while the prompt is answered.
        /// </summary>
        public static bool OnWantsToQuit()
        {
            if (_quitConfirmed || (!IsDirty && !IsBusy))
            {
                StopRunningWork();
                return true;
            }

            RequestQuit();
            return false;
        }

        /// <summary>File &gt; Quit, and the application's own quit, once unsaved work and running work are settled.</summary>
        public static void RequestQuit()
        {
            Action quit = () =>
            {
                ConfirmPrompt.AskToSave("quitting", () =>
                {
                    _quitConfirmed = true;
                    StopRunningWork();
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#endif
                    Application.Quit();
                });
            };

            if (IsBusy)
            {
                ConfirmPrompt.AskToConfirm(char.ToUpper(BusyReason[0]) + BusyReason.Substring(1)
                    + ". Quitting stops it.", "Stop it and quit", quit);
                return;
            }

            quit();
        }

        private static void StopRunningWork()
        {
            if (SimulationActive)
            {
                // V1-INTEGRATION: C3 - Engine.CloseSimulations is wired to ElmfireRunner.CancelAll by the lead, which
                // also stops an ELMFIRE computation; until then a run inside ELMFIRE finishes that step first.
                PreactGUI.Engine?.CloseSimulations(false);
            }
            // V1-INTEGRATION: the campaign process (ProbabilisticTriggerWindow, WP1) has no public stop; it keeps
            // running after the GUI quits until it gains one this can call.
        }

        // ------------------------------------------------------------------ per frame

        /// <summary>Called once per frame from the GUI's layout: polls for changes and handles the shortcuts.</summary>
        public static void Update()
        {
            Poll(false);
            HandleShortcuts();
        }

        private static void HandleShortcuts()
        {
            //Not while a modal question or a file dialog is up: those own the keyboard.
            if (ConfirmPrompt.IsOpen || SimpleFileBrowser.FileBrowser.IsOpen)
            {
                return;
            }

            ImGuiIOPtr io = ImGui.GetIO();
            bool ctrl = io.KeyCtrl || io.KeySuper;

            if (ctrl && ImGui.IsKeyPressed(ImGuiKey.S, false))
            {
                if (HasInput) Save();
            }
            else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.O, false))
            {
                RequestOpen();
            }
            else if (ctrl && ImGui.IsKeyPressed(ImGuiKey.N, false))
            {
                RequestNew();
            }
            else if (!ctrl && ImGui.IsKeyPressed(ImGuiKey.F5, false) && !io.WantTextInput)
            {
                RunShortcut?.Invoke();
            }
        }

        /// <summary>What F5 does; set by the run window.</summary>
        public static Action RunShortcut;

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
        public static bool IsBusy { get => SimulationActive || StepActive || CampaignActive || ScenarioCheckWindow.Checking; }

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
                if (ScenarioCheckWindow.Checking) return "the scenario is being checked";
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
