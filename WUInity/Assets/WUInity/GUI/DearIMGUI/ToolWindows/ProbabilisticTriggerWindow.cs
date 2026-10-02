using ImGuiNET;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using PREACT.Utility;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Runs a trigger campaign, <c>PREACTcli converge-trigger</c>: ELMFIRE fires from ignitions drawn over the
    /// case, each with its own weather, one evacuation and k-PERIL trigger boundary per fire that reaches the
    /// WUI area, aggregated into a probability raster until it stops moving. Mirrors the CLI's progress and log.
    /// </summary>
    /// <remarks>
    /// Only the command line is decided here. What a campaign reads (namelist, case inputs, executables, fuel
    /// tables) the CLI resolves from the scenario itself, so this window and a campaign started by hand cannot
    /// disagree about it; the overrides under "Advanced" are for running something else on purpose.
    ///
    /// The CLI keeps each set of settings in its own folder, <c>_output/campaign_&lt;scenario&gt;_&lt;hash&gt;</c>,
    /// and reuses realizations only when every setting matches. So Run first asks it (<c>--inspect</c>) whether
    /// such a campaign exists, and only reuses one after the question "reuse N realizations?" has been answered
    /// here: reusing was the default, and a changed stop time or seed silently aggregated the old fires.
    ///
    /// The CLI reads the .wui from disk, and the campaign's identity is the hash of that file, so Run asks to
    /// save the open scenario first when it has unsaved changes. The fields follow the scenario session: a
    /// different scenario opened reseeds them.
    /// </remarks>
    public static class ProbabilisticTriggerWindow
    {
        private static bool _isOpen;
        private static bool _quitHooked;
        private static bool _sessionHooked;

        private static string _cliExe = string.Empty;
        private static string _baseWui = string.Empty;
        private static string _outPath = string.Empty;

        /// <summary>The scenario file the fields were last filled from; a different one refills them.</summary>
        private static string _seededFrom;

        private static int _max = 200;
        private static int _parallel;                   // 0 = the CLI's default (half the cores)
        private static int _streakTarget = 20;
        private static float _tolerancePercent = 2.0f;  // shown as %, sent as a fraction

        /// <summary>Hours of fire per realization (contract C6: hours wherever a person sees it).</summary>
        private static int _hours = (int)CampaignLayout.DefaultFireHours;

        private static bool _fittedWeather = true;
        private static int _candidateDaysPerYear = 10;
        private static bool _fitLiveFuelMoisture = true;
        private static bool _windToWui = true;
        private static bool _allowUniformWeather;

        // Advanced: blank means "what the scenario says".
        private static string _templateOverride = string.Empty;
        private static string _inputsOverride = string.Empty;

        // What the scenario resolves to, shown so the campaign's inputs are visible without being asked for.
        private static string _elmfireExe = string.Empty;
        private static string _gdalBin = string.Empty;
        private static string _scenarioTemplate = string.Empty;

        // Live convergence state, parsed from the CLI's PROGRESS_JSON lines. Guarded by _sync because it is
        // written on the process's output thread and read while drawing.
        private static int _nSuccess, _nNotThreatened, _nFailed, _streak;
        private static bool _converged;
        private static double[] _deciles;
        private static double[] _area;
        private static double?[] _delta;
        private static string _liveRasterPath;
        private static string _campaignDir;

        private enum Phase { Idle, Inspecting, Confirm, Running, Cancelling }

        /// <summary>
        /// A campaign process is running: the settings check, the campaign itself, or one being cancelled. While
        /// it is, the GUI starts no run or data step and opens no other scenario (<see cref="ScenarioSession.IsBusy"/>).
        /// </summary>
        public static bool IsRunning
        {
            get
            {
                Phase phase = _phase;
                return phase == Phase.Inspecting || phase == Phase.Running || phase == Phase.Cancelling;
            }
        }

        // ---- run state (written from the process reader threads, read on the UI thread) ----
        private static readonly object _sync = new object();
        private static readonly List<string> _log = new List<string>();
        private static Process _process;
        private static volatile Phase _phase = Phase.Idle;

        //A stop was asked for (Cancel, File > Quit, Play-stop) since the user last pressed Run. Kept apart from _phase
        //because the settings check cannot be cancelled half-way: its answer still arrives, and must not start a campaign.
        private static volatile bool _stopAsked;
        private static volatile float _progress;   // 0..1
        private static volatile int _doneCount;
        private static volatile int _totalCount;
        private static string _status = "Idle.";

        // The answer to --inspect.
        private static bool _inspectMatch, _inspectRunning;
        private static int _inspectOk, _inspectNotThreatened, _inspectFailed;
        private static string _inspectFolder;
        private static readonly List<string> _inspectOthers = new List<string>();

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
            HookQuit();
            HookSession();
            GuessCli();

            //Every time the window opens: a campaign set up for one scenario and then run on another is the
            //failure this prevents. Fields typed for the same scenario are kept.
            SeedFromScenario(false);
        }

        public static void Close()
        {
            if (_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
            _isOpen = false;
        }

        /// <summary>
        /// The scenario session's "scenario changed" event: refills everything that came from the scenario. A
        /// "reuse the existing campaign?" question still open was asked about the previous scenario, so it is
        /// dropped.
        /// </summary>
        public static void OnScenarioChanged()
        {
            if (_phase == Phase.Confirm)
            {
                _phase = Phase.Idle;
                _status = "Idle (the scenario changed).";
            }
            SeedFromScenario(true);
        }

        /// <summary>Stops the running campaign and everything it started, as the Cancel button does.</summary>
        public static void Stop()
        {
            CancelRun();
        }

        /// <summary>
        /// A campaign started from here stops with the application (or, in the editor, with play mode). The CLI
        /// would also stop by itself once this process is gone and its stdin closes, but the editor outlives
        /// play mode.
        /// </summary>
        private static void HookQuit()
        {
            if (_quitHooked) return;
            _quitHooked = true;
            Application.quitting += StopForQuit;
        }

        private static void HookSession()
        {
            if (_sessionHooked) return;
            _sessionHooked = true;
            ScenarioSession.ScenarioChanged += OnScenarioChanged;
        }

        /// <summary>
        /// For quitting: closes the CLI's stdin so it kills every ELMFIRE, WindNinja and PREACT tree it started,
        /// waits a few seconds for it to do so, then kills the CLI's own tree. Blocks, because once the
        /// application has gone nothing would be left to finish a cancel running in the background.
        /// </summary>
        public static void StopForQuit()
        {
            _stopAsked = true;
            Process process = _process;
            if (process == null) return;

            try
            {
                if (process.HasExited) return;
                _phase = Phase.Cancelling;
                try { process.StandardInput.Close(); } catch { }
                if (!process.WaitForExit(5000))
                {
                    ElmfireProcesses.KillTree(process);
                }
            }
            catch
            {
                //Quitting goes ahead whatever happens here.
            }
        }

        private static void GuessCli()
        {
            if (!string.IsNullOrEmpty(_cliExe) && File.Exists(_cliExe)) return;

            //A standalone build (build-player.ps1) has it in PREACT/ beside WUInity.exe; the developer layout has the
            //CLI's own build output next to the Unity project.
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName; // WUInity/, or the player's folder
                string name = Application.platform == RuntimePlatform.WindowsEditor
                              || Application.platform == RuntimePlatform.WindowsPlayer ? "PREACTcli.exe" : "PREACTcli";
                string shipped = Path.Combine(projectRoot, "PREACT", name);
                if (!Application.isEditor && File.Exists(shipped))
                {
                    _cliExe = shipped;
                    return;
                }
                foreach (string config in new[] { "Release", "Debug" })
                {
                    string guess = Path.GetFullPath(Path.Combine(projectRoot, "..", "PREACT", "PREACTcli", "bin", config, "net8.0", name));
                    if (File.Exists(guess))
                    {
                        _cliExe = guess;
                        return;
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Takes the base .wui from the loaded scenario and shows what it resolves to. With <paramref name="force"/>
        /// false only when the loaded scenario is not the one the fields were filled from.
        /// </summary>
        private static void SeedFromScenario(bool force)
        {
            string current = ScenarioSession.FilePath;
            if (!string.IsNullOrEmpty(current) && (force || !SamePath(current, _seededFrom)))
            {
                _baseWui = current;
                _templateOverride = string.Empty;
                _inputsOverride = string.Empty;
                _outPath = string.Empty;
                _seededFrom = current;
            }

            PREACT.Input.PREACTInput input = ScenarioSession.Input;
            PREACT.Input.ElmfireInput elmfire = input?.WildfireModule?.ElmfireInput;

            _elmfireExe = ElmfireCoupling.ResolveExecutable(input?.RootFolder, elmfire?.ElmfireExe) ?? string.Empty;
            _gdalBin = GdalTools.FindBinDirectory() ?? string.Empty;
            _scenarioTemplate = string.Empty;

            if (input == null || elmfire == null) return;

            string named = Resolve(input.RootFolder, elmfire.PathToGdal);
            if (!string.IsNullOrEmpty(named) && Directory.Exists(named)) _gdalBin = named;

            try
            {
                //The engine's rule, as the campaign CLI's: an empty CaseDirectory is "elmfire", not "no case".
                string caseDir = ElmfireCoupling.CaseDirectoryPath(input.RootFolder, elmfire);
                if (!string.IsNullOrEmpty(caseDir))
                {
                    _scenarioTemplate = ElmfireCoupling.ResolveNamelist(caseDir, input.RootFolder, elmfire, null, out _) ?? string.Empty;
                }
            }
            catch
            {
                _scenarioTemplate = string.Empty;
            }
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>A scenario-relative path made absolute, since the CLI runs from its own directory.</summary>
        private static string Resolve(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (Path.IsPathRooted(path)) return path;
            if (string.IsNullOrEmpty(root)) return path;

            try { return Path.GetFullPath(Path.Combine(root, path)); }
            catch { return null; }
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.Begin("Trigger campaign###TriggerCampaign", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            ImGui.TextWrapped("Runs ELMFIRE fires from ignitions drawn over the case, each under its own weather, then an "
                              + "evacuation and a k-PERIL trigger boundary for every fire that reaches the WUI area, and "
                              + "aggregates the boundaries into a per-cell probability raster until it stops changing. "
                              + "Each realization is a fire and a full evacuation run, so this takes a long time.");

            Phase phase = _phase;
            ImGui.BeginDisabled(phase != Phase.Idle);

            FileRow("PREACTcli", () => _cliExe, v => _cliExe = v, false);
            FileRow("Base .wui", () => _baseWui, v => _baseWui = v, false);
            Fields.Hint("The campaign runs this scenario as it is saved on disk. Run asks to save the open scenario",
                        "first when it has unsaved changes.");

            ImGui.SeparatorText("Convergence");
            ImGui.InputInt("Maximum realizations", ref _max);
            if (_max < 1) _max = 1;
            ImGui.InputInt("Parallel realizations (0 = half the cores)", ref _parallel);
            if (_parallel < 0) _parallel = 0;
            ImGui.InputInt("Consecutive stable realizations", ref _streakTarget);
            if (_streakTarget < 1) _streakTarget = 1;
            ImGui.InputFloat("Per-decile tolerance (%)", ref _tolerancePercent);
            if (_tolerancePercent < 0.01f) _tolerancePercent = 0.01f;
            Fields.Hint("Stops once every decile of the probability raster has changed by less than the tolerance for",
                        "that many realizations in a row. The maximum is a ceiling, not a target.");
            Fields.Hint("Every realization can be run again exactly: its ignition, weather, ELMFIRE SEED and evacuation",
                        "([Simulation] RandomSeed, recorded in its realization.txt) are seeded from the campaign's seed and",
                        "its index. The scenario's own RandomSeed is used by single runs only.");

            ImGui.SeparatorText("Fire");
            DrawHours();

            ImGui.SeparatorText("Weather per realization");
            DrawWeather();

            if (ImGui.CollapsingHeader("Advanced"))
            {
                ImGui.TextDisabled("elmfire: " + (string.IsNullOrEmpty(_elmfireExe) ? "not found" : _elmfireExe));
                ImGui.TextDisabled("GDAL: " + (string.IsNullOrEmpty(_gdalBin) ? "not found" : _gdalBin));
                ImGui.TextDisabled("Namelist: " + (string.IsNullOrEmpty(_scenarioTemplate) ? "not built yet" : _scenarioTemplate));
                FileRow("Other namelist", () => _templateOverride, v => _templateOverride = v, false);
                FileRow("Other inputs folder", () => _inputsOverride, v => _inputsOverride = v, true);
                Fields.Hint("Blank: the scenario's own namelist and the inputs folder it names. A different one",
                            "makes a different campaign (its hash is part of the campaign folder's name).");
                FileRow("Copy the probability raster to", () => _outPath, v => _outPath = v, false);
            }

            if (string.IsNullOrEmpty(_elmfireExe))
            {
                Fields.Warn("No ELMFIRE executable found, so no realization can compute a fire.",
                            "Set its path under Help > External tools and keys; the campaign's PREACTcli reads it there too.");
            }
            if (string.IsNullOrEmpty(_gdalBin))
            {
                Fields.Warn("No GDAL tools found. Without them ELMFIRE writes no rasters, so every realization fails.",
                            "Set their folder under Help > External tools and keys.");
            }
            if (ToolsService.Current.Probed && string.IsNullOrEmpty(ToolsService.Current.WindNinjaExe) && !_allowUniformWeather)
            {
                Fields.Warn("No WindNinja found: the campaign stops before its first fire unless \"Allow uniform weather\" is",
                            "ticked above - and then every fire runs under one wind for the whole domain. Set WindNinja's",
                            "path under Help > External tools and keys.");
            }

            ImGui.EndDisabled();

            ImGui.Separator();
            DrawControls(phase);

            float p = _progress;
            string overlay = _totalCount > 0 ? $"{_doneCount} / {_totalCount}" : (phase == Phase.Running ? "starting..." : "");
            ImGui.ProgressBar(p, new Vector2(-1, 0), overlay);
            ImGui.TextWrapped(_status);

            DrawConvergence();

            ImGui.SeparatorText("Log");
            // Third argument is ImGuiChildFlags, not the bool 'border' of older bindings. The member for a
            // bordered child was renamed across ImGui.NET versions (Border -> Borders), so the value is written
            // numerically: it is 1 in both, and this file has to compile against whatever the uimgui package pins.
            ImGui.BeginChild("prob_log", new Vector2(0, 220), (ImGuiChildFlags)1, ImGuiWindowFlags.HorizontalScrollbar);
            lock (_sync)
            {
                // show the tail to keep the widget light on very long runs
                int from = Mathf.Max(0, _log.Count - 500);
                for (int i = from; i < _log.Count; ++i)
                {
                    ImGui.TextUnformatted(_log[i]);
                }
            }
            if (phase == Phase.Running || phase == Phase.Inspecting)
            {
                ImGui.SetScrollHereY(1.0f); // auto-scroll while running
            }
            ImGui.EndChild();

            ImGui.End();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        /// <summary>
        /// The fire duration in hours, held to what the CLI accepts, with what it turns into beside it.
        /// </summary>
        private static void DrawHours()
        {
            ImGui.InputInt("Fire duration (hours)", ref _hours);
            _hours = Mathf.Clamp(_hours, (int)CampaignLayout.MinFireHours, (int)CampaignLayout.MaxFireHours);

            double tstop = CampaignLayout.TstopSeconds(_hours);
            double limitMinutes = CampaignLayout.DefaultMaxRuntimeSeconds(_hours) / 60.0;
            double binHours = CampaignLayout.StatisticsBinSeconds(_hours) / 3600.0;
            string bands = _fittedWeather
                ? "one weather band held for the whole fire"
                : $"{_hours} hourly weather bands, so {_hours} WindNinja solves per realization";

            Fields.Hint($"SIMULATION_TSTOP = {tstop.ToString("0", CultureInfo.InvariantCulture)} s; each ELMFIRE run is stopped",
                        $"after {limitMinutes:0} min of wall clock and counted as failed; {bands};",
                        $"arrival-time percentiles in {binHours:0} h bins.",
                        "",
                        $"{CampaignLayout.DefaultFireHours:0} h is the default: ignitions are drawn from the whole domain, and",
                        "the distant ones decide how far out the boundary sits. A fire that has not reached",
                        "the WUI area by then counts as not threatening it.");

            if (_hours > 168)
            {
                Fields.Warn("More than a week of fire: a spreading fire has usually burned the domain long before,",
                            "and every realization then costs many times longer for the same boundary.");
            }
        }

        private static void DrawWeather()
        {
            ImGui.Checkbox("Draw each parameter from a fitted distribution", ref _fittedWeather);
            Fields.Hint("On (default): a normal is fitted to each weather parameter over the worst fire-weather days",
                        "on record, and each realization draws one value of each - wind speed, temperature,",
                        "humidity - held for the whole fire: one weather band and one WindNinja solve. The ensemble",
                        "is continuous in weather rather than confined to the days that happened, at two costs: the",
                        "parameters are drawn independently, and dead fuel moisture comes from the drawn temperature",
                        "and humidity (no drying history, uniform over the domain) because Nelson needs real hours.",
                        "",
                        "Off: each realization replays one actual peak fire-weather day, hour by hour, with WindNinja",
                        "per hour and Nelson marched over the days before it.");

            if (_fittedWeather)
            {
                ImGui.Indent();
                ImGui.InputInt("Days per year in the fitted pool", ref _candidateDaysPerYear);
                _candidateDaysPerYear = Mathf.Clamp(_candidateDaysPerYear, 1, 366);
                Fields.Hint($"The {_candidateDaysPerYear} highest-FWI days of each year on record. A severity dial as much as a",
                            "sample size: a deeper pool reaches into milder days and pulls the fitted means with it.");

                ImGui.Checkbox("Draw live fuel moisture too", ref _fitLiveFuelMoisture);
                Fields.Hint("On (default): live herbaceous and woody moisture are taken per realization from the NFDRS4",
                            "growing-season model marched over the record. Off, they are the case's two constants in",
                            "every realization.");
                ImGui.Unindent();
            }

            ImGui.Checkbox("Aim the wind from the ignition at the WUI area", ref _windToWui);
            Fields.Hint("On (default): each realization's wind blows from its ignition towards the WUI area's centroid,",
                        "and WindNinja solves from that direction. A fire blown away from town answers nothing about",
                        "how much warning it needs. Off: the direction is one candidate day's own.");

            ImGui.Checkbox("Allow uniform weather", ref _allowUniformWeather);
            Fields.Hint("Off (default): the campaign stops before its first fire when WindNinja, the live fuel moisture",
                        "march or Nelson cannot run, rather than quietly running every fire under uniform wind and fixed",
                        "moisture. On: it goes ahead and says so.");
        }

        private static void DrawControls(Phase phase)
        {
            switch (phase)
            {
                case Phase.Idle:
                {
                    string gate = Gate();
                    ImGui.BeginDisabled(gate != null);
                    if (ImGui.Button("Run"))
                    {
                        RequestRun();
                    }
                    ImGui.EndDisabled();
                    if (gate != null)
                    {
                        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(gate);
                        ImGui.SameLine();
                        ImGui.TextDisabled(gate);
                    }
                    break;
                }

                case Phase.Inspecting:
                    ImGui.TextDisabled("Checking for an earlier campaign with these settings...");
                    break;

                case Phase.Confirm:
                    DrawConfirm();
                    break;

                case Phase.Running:
                    if (ImGui.Button("Cancel"))
                    {
                        CancelRun();
                    }
                    break;

                case Phase.Cancelling:
                    ImGui.TextDisabled("Stopping every ELMFIRE, WindNinja and PREACT process of the campaign...");
                    break;
            }
        }

        /// <summary>
        /// A campaign with exactly these settings exists: reuse it, or move it aside and start again. Nothing is
        /// the default.
        /// </summary>
        private static void DrawConfirm()
        {
            int finished = _inspectOk + _inspectNotThreatened;
            Fields.Warn($"A campaign with these settings exists: {Path.GetFileName(_inspectFolder ?? string.Empty)}",
                        $"{finished} finished realization(s) ({_inspectOk} boundaries, {_inspectNotThreatened} fires that never",
                        $"reached the WUI area), {_inspectFailed} failed.");

            if (ImGui.Button($"Reuse {finished} realization(s)"))
            {
                StartRun(true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Start over"))
            {
                StartRun(false);
            }
            ImGui.SameLine();
            if (ImGui.Button("Back"))
            {
                _phase = Phase.Idle;
                _status = "Idle.";
            }
            Fields.Hint("Reuse runs only the realizations still missing (failed ones again). Start over moves the old",
                        "folder aside as ..._replaced_<time>; nothing is deleted.");
        }

        /// <summary>
        /// A path with a text box and a browse button.
        /// </summary>
        /// <remarks>
        /// The picked path is routed back through a setter rather than by matching the label against a switch,
        /// which is how this worked and why the browse button did nothing on four of the eight fields it had.
        /// </remarks>
        private static void FileRow(string label, Func<string> get, Action<string> set, bool folder)
        {
            string value = get() ?? string.Empty;
            if (ImGui.InputText(label, ref value, 512))
            {
                set(value);
            }

            ImGui.SameLine();
            if (ImGui.Button("..." + "##" + label))
            {
                Browse(set, folder);
            }
        }

        private static void Browse(Action<string> set, bool folder)
        {
            SimpleFileBrowser.FileBrowser.PickMode mode = folder
                ? SimpleFileBrowser.FileBrowser.PickMode.Folders
                : SimpleFileBrowser.FileBrowser.PickMode.Files;

            string initial = PreactGUI.Engine != null ? PreactGUI.Engine.WorkingFolder : null;

            SimpleFileBrowser.FileBrowser.ShowLoadDialog(
                paths =>
                {
                    if (paths != null && paths.Length > 0) set(paths[0]);
                },
                () => { },
                mode, false, initial, null, "Select", "Select");
        }

        /// <summary>
        /// Shows the convergence state the CLI reports, so the run can be judged while it is still going rather
        /// than only from the CSV afterwards. Plain text rather than an ImGui table: the table API has moved
        /// between ImGui.NET versions and this file has to compile against whatever the uimgui package pins.
        /// </summary>
        private static void DrawConvergence()
        {
            double[] deciles, area;
            double?[] delta;
            int nSuccess, nNotThreatened, nFailed, streak;
            bool converged;
            string live, folder;

            lock (_sync)
            {
                deciles = _deciles;
                area = _area;
                delta = _delta;
                nSuccess = _nSuccess;
                nNotThreatened = _nNotThreatened;
                nFailed = _nFailed;
                streak = _streak;
                converged = _converged;
                live = _liveRasterPath;
                folder = _campaignDir;
            }

            if (!string.IsNullOrEmpty(folder))
            {
                ImGui.TextWrapped("Campaign folder: " + folder);
            }

            if (deciles == null || area == null)
            {
                return;
            }

            ImGui.SeparatorText("Convergence");
            ImGui.Text($"{nSuccess} boundaries, {nNotThreatened} not threatened, {nFailed} failed    streak {streak} / {_streakTarget}"
                       + (converged ? "    CONVERGED" : string.Empty));

            ImGui.Text("  decile        area (m2)      change");
            for (int i = 0; i < deciles.Length && i < area.Length; ++i)
            {
                //a decile with no baseline yet is shown as "-" rather than 0%, because "has not moved" and "has
                //nothing to move from" mean very different things for the streak
                string change = (delta != null && i < delta.Length && delta[i].HasValue)
                    ? (delta[i].Value * 100.0).ToString("F2") + " %"
                    : "-";

                ImGui.Text($"  P >= {deciles[i]:F1}   {area[i],14:N0}   {change,10}");
            }

            if (!string.IsNullOrEmpty(live))
            {
                ImGui.TextWrapped("Live raster: " + live);
            }
        }

        private static void AppendLog(string line)
        {
            if (line == null) return;
            lock (_sync)
            {
                _log.Add(line);
            }
            ParseProgress(line);
        }

        private static void ParseProgress(string line)
        {
            if (ParseProgressJson(line) || ParseTagged(line, CampaignLayout.ProgressRasterTag, v => _liveRasterPath = v)
                || ParseTagged(line, CampaignLayout.CampaignDirTag, v => _campaignDir = v)
                || ParseInspect(line))
            {
                return;
            }

            // "PROGRESS <done>/<total> ..."
            int idx = line.IndexOf(CampaignLayout.ProgressTag, StringComparison.Ordinal);
            if (idx < 0) return;
            string rest = line.Substring(idx + CampaignLayout.ProgressTag.Length).Trim();
            string token = rest.Split(' ')[0]; // "<done>/<total>"
            string[] parts = token.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out int done) && int.TryParse(parts[1], out int total) && total > 0)
            {
                _doneCount = done;
                _totalCount = total;
                _progress = Mathf.Clamp01((float)done / total);
                _status = $"{done} of at most {total} realizations...";
            }
        }

        private static bool ParseTagged(string line, string tag, Action<string> set)
        {
            if (!line.StartsWith(tag, StringComparison.Ordinal)) return false;
            string value = line.Substring(tag.Length).Trim();
            lock (_sync)
            {
                set(value);
            }
            return true;
        }

        /// <summary>
        /// Reads one PROGRESS_JSON line from converge-trigger.
        ///
        /// Hand-parsed rather than run through a JSON library: the shape is fixed and emitted by code in this
        /// same repository, and this runs on the child process's output thread where an exception would be
        /// swallowed and simply stop the display updating. Anything unrecognised is ignored rather than throwing.
        /// </summary>
        private static bool ParseProgressJson(string line)
        {
            if (!line.StartsWith(CampaignLayout.ProgressJsonTag, StringComparison.Ordinal)) return false;
            string json = line.Substring(CampaignLayout.ProgressJsonTag.Length);

            try
            {
                lock (_sync)
                {
                    if (TryGetInt(json, "nSuccess", out int ok)) _nSuccess = ok;
                    if (TryGetInt(json, "nNotThreatened", out int not)) _nNotThreatened = not;
                    if (TryGetInt(json, "nFailed", out int failed)) _nFailed = failed;
                    if (TryGetInt(json, "streak", out int streak)) _streak = streak;
                    if (TryGetInt(json, "streakTarget", out int target) && target > 0) _streakTarget = target;
                    _converged = json.Contains("\"converged\":true");

                    double[] deciles = GetArray(json, "deciles");
                    double[] area = GetArray(json, "area");
                    if (deciles != null) _deciles = deciles;
                    if (area != null) _area = area;
                    double?[] delta = GetNullableArray(json, "delta");
                    if (delta != null) _delta = delta;
                }
            }
            catch
            {
                //a malformed progress line must never take down the reader thread
            }
            return true;
        }

        /// <summary>The CAMPAIGN_INSPECT line <c>--inspect</c> prints.</summary>
        private static bool ParseInspect(string line)
        {
            if (!line.StartsWith(CampaignLayout.InspectTag, StringComparison.Ordinal)) return false;
            string json = line.Substring(CampaignLayout.InspectTag.Length);

            try
            {
                lock (_sync)
                {
                    _inspectMatch = json.Contains("\"match\":true");
                    _inspectRunning = json.Contains("\"running\":true");
                    TryGetInt(json, "ok", out _inspectOk);
                    TryGetInt(json, "notThreatened", out _inspectNotThreatened);
                    TryGetInt(json, "failed", out _inspectFailed);
                    _inspectFolder = GetString(json, "folder");
                }
            }
            catch
            {
                //as above
            }
            return true;
        }

        private static bool TryGetInt(string json, string key, out int value)
        {
            value = 0;
            string token = "\"" + key + "\":";
            int at = json.IndexOf(token, StringComparison.Ordinal);
            if (at < 0) return false;

            at += token.Length;
            int end = at;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) ++end;
            return end > at && int.TryParse(json.Substring(at, end - at), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>The first string value of <paramref name="key"/>, with \" and \\ unescaped.</summary>
        private static string GetString(string json, string key)
        {
            string token = "\"" + key + "\":\"";
            int at = json.IndexOf(token, StringComparison.Ordinal);
            if (at < 0) return null;

            var sb = new System.Text.StringBuilder();
            for (int i = at + token.Length; i < json.Length; ++i)
            {
                char ch = json[i];
                if (ch == '\\' && i + 1 < json.Length) { sb.Append(json[++i]); continue; }
                if (ch == '"') break;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string GetArrayBody(string json, string key)
        {
            string token = "\"" + key + "\":[";
            int at = json.IndexOf(token, StringComparison.Ordinal);
            if (at < 0) return null;

            at += token.Length;
            int end = json.IndexOf(']', at);
            return end < 0 ? null : json.Substring(at, end - at);
        }

        private static double[] GetArray(string json, string key)
        {
            string body = GetArrayBody(json, key);
            if (body == null) return null;

            string[] parts = body.Split(',');
            var result = new double[parts.Length];
            for (int i = 0; i < parts.Length; ++i)
            {
                double.TryParse(parts[i].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out result[i]);
            }
            return result;
        }

        /// <summary>As <see cref="GetArray"/>, but keeps JSON null distinct from 0 — the CLI uses null for a
        /// decile that has no baseline to compare against yet.</summary>
        private static double?[] GetNullableArray(string json, string key)
        {
            string body = GetArrayBody(json, key);
            if (body == null) return null;

            string[] parts = body.Split(',');
            var result = new double?[parts.Length];
            for (int i = 0; i < parts.Length; ++i)
            {
                string p = parts[i].Trim();
                if (p == "null") { result[i] = null; continue; }
                result[i] = double.TryParse(p, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? (double?)v : null;
            }
            return result;
        }

        /// <summary>
        /// Why a campaign cannot start now because of what the GUI is doing, or null. A GUI run computes its fire
        /// in the same case, and a data step may be rebuilding the case every realization reads; the GUI in turn
        /// starts neither while a campaign runs (<see cref="IsRunning"/> is part of its busy state).
        /// </summary>
        private static string Gate()
        {
            if (ScenarioSession.SimulationActive)
            {
                return "Not while a simulation is running in the GUI: it computes its fire on the same case.";
            }
            if (ScenarioSession.StepActive)
            {
                return "Not while " + (ScenarioSession.BusyReason ?? "a data step is running")
                    + ": it may be rewriting the files every realization reads.";
            }

            //What step 13 of the workflow says stands in the way, for the scenario that is open - the window opens
            //whatever the workflow says, so a running campaign can always be watched and cancelled; this is where
            //the workflow's verdict applies. A base .wui picked by hand is another scenario, and the CLI checks it.
            if (ScenarioSession.HasInput && SamePath(_baseWui, ScenarioSession.FilePath))
            {
                global::WUInity.Workflow.WorkflowStep step = WorkflowService.Step(global::WUInity.Workflow.WorkflowStepId.Campaign);
                if (step != null && !step.Applicable)
                {
                    return step.Summary;
                }
                if (step != null && !string.IsNullOrEmpty(step.BlockedBy))
                {
                    return step.BlockedBy;
                }
            }
            return null;
        }

        /// <summary>
        /// Run: asks to save the open scenario when the campaign is about to read it from disk with changes
        /// that are only in the GUI, then goes on to <see cref="StartInspect"/>.
        /// </summary>
        private static void RequestRun()
        {
            string stop = Gate() ?? Preflight();
            if (stop != null)
            {
                _status = stop;
                return;
            }

            if (ScenarioSession.HasInput && SamePath(_baseWui, ScenarioSession.FilePath) && ScenarioSession.IsDirty)
            {
                ConfirmPrompt.AskToSave("starting the campaign (it runs the scenario as saved on disk; without saving, "
                                        + "the changes are not in it)", StartInspect);
                return;
            }

            StartInspect();
        }

        /// <summary>Why a campaign cannot start with the fields as they are, or null.</summary>
        private static string Preflight()
        {
            if (string.IsNullOrEmpty(_cliExe) || !File.Exists(_cliExe)) return "PREACTcli not found: set its path.";
            if (string.IsNullOrEmpty(_baseWui) || !File.Exists(_baseWui)) return "Base .wui not found: set its path.";
            if (!string.IsNullOrEmpty(_templateOverride) && !File.Exists(_templateOverride)) return "The other namelist is not there.";
            if (!string.IsNullOrEmpty(_inputsOverride) && !Directory.Exists(_inputsOverride)) return "The other inputs folder is not there.";
            return CampaignLayout.ValidateFireHours(_hours);
        }

        /// <summary>
        /// The converge-trigger command line for the fields as they are. <c>--inspect</c> gets the same settings
        /// as the run, or it would answer for another campaign.
        /// </summary>
        private static List<string> Arguments(bool inspect, bool resume)
        {
            string I(int v) => v.ToString(CultureInfo.InvariantCulture);

            var a = new List<string> { "converge-trigger", "--wui", _baseWui };
            a.Add("--hours"); a.Add(I(_hours));
            if (!_fittedWeather) a.Add("--historical-day-weather");
            if (_fittedWeather)
            {
                a.Add("--candidate-days-per-year"); a.Add(I(_candidateDaysPerYear));
                if (!_fitLiveFuelMoisture) a.Add("--no-live-fuel-moisture");
            }
            if (!_windToWui) a.Add("--no-wind-to-wui");
            if (_allowUniformWeather) a.Add("--allow-uniform-weather");
            if (!string.IsNullOrEmpty(_elmfireExe)) { a.Add("--elmfire"); a.Add(_elmfireExe); }
            if (!string.IsNullOrEmpty(_gdalBin)) { a.Add("--gdal"); a.Add(_gdalBin); }
            if (!string.IsNullOrEmpty(_templateOverride)) { a.Add("--elmfire-template"); a.Add(_templateOverride); }
            if (!string.IsNullOrEmpty(_inputsOverride)) { a.Add("--elmfire-inputs"); a.Add(_inputsOverride); }

            //Closing the CLI's stdin is how Cancel stops it, and it also stops by itself when this process ends
            //and the pipe closes - so no campaign outlives the window that started it. The settings check too: it
            //hashes the case's inputs, which is not instant, and a quit waits for it.
            a.Add("--cancel-on-stdin-close");

            if (inspect)
            {
                a.Add("--inspect");
                return a;
            }

            a.Add("--max"); a.Add(I(_max));
            a.Add("--streak"); a.Add(I(_streakTarget));
            //shown as a percentage, sent as the fraction the CLI expects
            a.Add("--tolerance"); a.Add((_tolerancePercent / 100f).ToString("R", CultureInfo.InvariantCulture));
            if (_parallel > 0) { a.Add("--parallel"); a.Add(I(_parallel)); }
            if (resume) a.Add("--resume");
            if (!string.IsNullOrEmpty(_outPath)) { a.Add("--out"); a.Add(_outPath); }
            return a;
        }

        private static void ResetRunState()
        {
            lock (_sync)
            {
                _log.Clear();
                _deciles = null;
                _area = null;
                _delta = null;
                _liveRasterPath = null;
                _campaignDir = null;
                _nSuccess = 0;
                _nNotThreatened = 0;
                _nFailed = 0;
                _streak = 0;
                _converged = false;
                _inspectMatch = false;
                _inspectRunning = false;
                _inspectOk = _inspectNotThreatened = _inspectFailed = 0;
                _inspectFolder = null;
            }
            _progress = 0f;
            _doneCount = 0;
            _totalCount = 0;
        }

        /// <summary>Run, first half: asks the CLI whether a campaign with these settings exists.</summary>
        private static void StartInspect()
        {
            //Again: the save question may have been answered frames after Run was pressed.
            string stop = Gate() ?? Preflight();
            if (stop != null)
            {
                _status = stop;
                return;
            }

            SeedFromScenario(false);
            ResetRunState();
            _stopAsked = false;
            _phase = Phase.Inspecting;
            _status = "Checking for an earlier campaign with these settings...";

            Launch(Arguments(true, false), code =>
            {
                //Cancel, File > Quit or Play-stop while the check ran: its answer is not a go-ahead. Launching the
                //campaign here is what made a quit wait for a campaign nobody asked for, and a Play-stop leave one
                //running in the editor (review R2).
                if (_stopAsked || _phase == Phase.Cancelling)
                {
                    _phase = Phase.Idle;
                    _status = "Stopped before the campaign started.";
                    AppendLog(_status);
                    return;
                }

                bool match, running;
                int finished, failed;
                lock (_sync)
                {
                    match = _inspectMatch;
                    running = _inspectRunning;
                    finished = _inspectOk + _inspectNotThreatened;
                    failed = _inspectFailed;
                }

                if (code != 0)
                {
                    _phase = Phase.Idle;
                    _status = "The campaign cannot start with these settings (see the log).";
                }
                else if (running)
                {
                    _phase = Phase.Idle;
                    _status = "A campaign with these settings is running already, from another window or a command line.";
                }
                else if (match && finished + failed > 0)
                {
                    _phase = Phase.Confirm;
                    _status = "Reuse the existing campaign, or start over?";
                }
                else
                {
                    NoteEarlierCampaign();
                    StartRun(false);
                }
            });
        }

        /// <summary>
        /// Before a new campaign starts where no campaign with these settings exists: when the scenario's latest campaign
        /// was made by an earlier version, it is said why that one is not offered for reuse.
        /// </summary>
        private static void NoteEarlierCampaign()
        {
            PREACT.Input.PREACTInput input = ScenarioSession.Input;
            if (input == null || !SamePath(_baseWui, ScenarioSession.FilePath)) return;

            string latest = global::WUInity.Workflow.ScenarioWorkflow.CampaignFolderOf(input, ScenarioSession.FilePath);
            if (CampaignLayout.PredatesEvacuationSeeds(latest))
            {
                AppendLog(CampaignLayout.DescribeEarlierCampaign(latest));
            }
        }

        /// <summary>Run, second half: the campaign itself.</summary>
        private static void StartRun(bool resume)
        {
            string stop = Gate();
            if (stop != null)
            {
                _phase = Phase.Idle;
                _status = stop;
                return;
            }
            if (_stopAsked)
            {
                _phase = Phase.Idle;
                _status = "Stopped before the campaign started.";
                return;
            }

            _phase = Phase.Running;
            _totalCount = _max;
            _status = resume ? "Resuming..." : "Starting...";

            Launch(Arguments(false, resume), code =>
            {
                bool converged;
                lock (_sync) { converged = _converged; }

                _phase = Phase.Idle;
                switch (code)
                {
                    case 0:
                        _progress = 1f;
                        //A run that reaches --max without converging also exits 0, so the exit code alone would
                        //report an unconverged result as a clean success.
                        _status = converged
                            ? "Converged. The probability raster is in the campaign folder."
                            : "Reached the maximum without converging: the probability raster is not yet stable.";
                        break;
                    case 3:
                        _status = "Cancelled. Finished realizations are kept; Run again offers to reuse them.";
                        break;
                    default:
                        _status = "The campaign stopped with exit code " + code + " (see the log).";
                        break;
                }
                AppendLog(_status);
            });
        }

        private static void Launch(List<string> arguments, Action<int> exited)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _cliExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                WorkingDirectory = Path.GetDirectoryName(_cliExe),
            };
            foreach (string arg in arguments) psi.ArgumentList.Add(arg);

            try
            {
                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (s, e) => AppendLog(e.Data);
                process.ErrorDataReceived += (s, e) => AppendLog(e.Data);
                process.Exited += (s, e) =>
                {
                    int code = -1;
                    try
                    {
                        //the reader threads may still hold the last lines; ExitCode is valid either way
                        process.WaitForExit();
                        code = process.ExitCode;
                    }
                    catch { }

                    if (ReferenceEquals(_process, process)) _process = null;

                    //On the main thread: the callbacks read the workflow model, which the main thread rebuilds every
                    //second, and start the next process. From this pool thread a collision threw there and left the
                    //phase at Inspecting - the GUI busy for the rest of the session (review R2).
                    PreactGUI.Post(() => Finished(exited, code));
                };

                _process = process;
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                AppendLog("Launched: " + _cliExe + " " + string.Join(" ", arguments));
            }
            catch (Exception ex)
            {
                _process = null;
                _phase = Phase.Idle;
                _status = "Failed to start: " + ex.Message;
                AppendLog(_status);
            }
        }

        /// <summary>
        /// A launched process's exit, on the main thread. Whatever the callback does, the window is never left busy
        /// by it: an exception ends in Idle, with the reason in the log.
        /// </summary>
        private static void Finished(Action<int> exited, int code)
        {
            try
            {
                exited(code);
            }
            catch (Exception e)
            {
                _phase = Phase.Idle;
                _status = "The campaign window stopped on an error: " + e.Message;
                AppendLog(_status);
            }
        }

        /// <summary>
        /// Stops the campaign and everything it started. Closing the CLI's stdin makes it kill every ELMFIRE,
        /// WindNinja and PREACT process tree it launched and exit; if it has not gone within 15 s its own tree is
        /// killed (taskkill /T on Windows). Killing only the CLI, as this did, left the fires running.
        /// </summary>
        private static void CancelRun()
        {
            //Before anything else: the settings check may have exited already, with its answer still on its way.
            _stopAsked = true;
            Process process = _process;
            if (process == null) return;

            try
            {
                if (process.HasExited) return;
            }
            catch
            {
                return;
            }

            _phase = Phase.Cancelling;
            _status = "Cancelling...";
            AppendLog("Cancel requested: the campaign stops every process it started.");

            try { process.StandardInput.Close(); } catch { }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (!process.WaitForExit(15000))
                    {
                        AppendLog("The campaign did not stop within 15 s; killing its process tree.");
                        ElmfireProcesses.KillTree(process);
                    }
                }
                catch (Exception e)
                {
                    AppendLog("Cancel error: " + e.Message);
                }
            });
        }
    }
}
