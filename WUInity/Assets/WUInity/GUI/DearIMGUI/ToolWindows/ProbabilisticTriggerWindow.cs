using ImGuiNET;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Runs the head-less `PREACTcli probabilistic-trigger` batch (one evacuation + k-PERIL
    /// trigger boundary per fire realization, aggregated into a probability raster) and
    /// mirrors its progress and console output in the visualizer.
    /// </summary>
    public static class ProbabilisticTriggerWindow
    {
        private static bool _isOpen;
        private static bool _initialized;

        private static string _cliExe = string.Empty;
        private static string _baseWui = string.Empty;
        private static string _rasterDir = string.Empty;
        private static string _outPath = string.Empty;
        private static int _count = 994;
        private static int _start = 1;
        private static int _pad = 7;
        //The trailing number in an ELMFIRE dump name is the run's stop time in seconds, so it changes with
        //the hours the fires were run for. Left as a wildcard rather than spelled out: these defaults used to
        //carry _0072000 from one particular ensemble, and every later one had to be typed in four times.
        private static string _toa = "time_of_arrival_{i}_*.tif";
        private static string _ros = "vs_{i}_*.tif";
        private static string _sd  = "spread_dir_{i}_*.tif";
        private static string _fi  = "flin_{i}_*.tif";
        private static bool _resume = true;

        // Convergence-driven mode (converge-trigger). The fixed-count mode below stays available
        // because it is still the right tool when the ensemble already exists and the question is
        // "aggregate exactly these N", rather than "run until the boundary stops moving".
        private static bool _convergeMode = true;
        private static int _max = 200;
        private static int _parallel = 0;               // 0 = let the CLI pick (CPU count)
        private static int _streakTarget = 20;
        private static float _tolerancePercent = 2.0f;  // shown as %, sent as a fraction

        // ELMFIRE realization generation. Without these converge-trigger reads a pre-generated
        // ensemble from the realization folder instead of producing one.
        private static bool _generateWithElmfire;

        /// <summary>Whether the generate-with-ELMFIRE box has been set from the scenario once already.</summary>
        private static bool _seededGenerateFlag;
        private static string _elmfireExe = string.Empty;
        private static string _elmfireTemplate = string.Empty;
        private static string _elmfireInputs = string.Empty;
        private static string _gdalBin = string.Empty;
        /// <summary>Hours of fire per realization. 72 h (three days) matches the CLI default; see the
        /// tooltip where it is drawn for why it is that long.</summary>
        private static int _tstopHours = 72;

        /// <summary>
        /// Each realization gets its own weather rather than the case's one series. On by default: a
        /// realization is a draw of the whole scenario, and sharing one series makes every realization burn
        /// under the same wind.
        /// </summary>
        private static bool _realizationWeather = true;

        /// <summary>
        /// Draw each weather parameter from a normal fitted to the record's worst fire-weather days, rather
        /// than replaying one whole historical day. On by default — see the hint beside the box for what the
        /// two modes each give up.
        /// </summary>
        private static bool _fittedWeather = true;

        /// <summary>Days per year of record in the pool the distributions are fitted to, highest FWI first.</summary>
        private static int _candidateDaysPerYear = 10;

        /// <summary>
        /// Also draw live herbaceous and live woody moisture per realization, from the NFDRS4 GSI model
        /// marched over the record. Off, they stay the case's two fixed constants for every realization.
        /// </summary>
        private static bool _fitLiveFuelMoisture = true;

        /// <summary>
        /// One weather band, held for the whole fire — of the realization's own drawn day when the box above
        /// is ticked, otherwise of the case's rasters. One WindNinja solve per realization instead of one per
        /// hour of the run, and it is what makes a run longer than its weather series legal.
        /// </summary>
        private static bool _singleBandWeather;

        /// <summary>
        /// Aim each realization's wind from its ignition at the WUI area centroid. On by default — a
        /// realization whose fire is blown away from the community says nothing about how much warning it
        /// needs.
        /// </summary>
        private static bool _windToWui = true;

        // Live convergence state, parsed from the CLI's PROGRESS_JSON lines. Guarded by _sync
        // because it is written on the process's output thread and read while drawing.
        private static int _nSuccess, _nFailed, _streak;
        private static bool _converged;
        private static double[] _deciles;
        private static double[] _area;
        private static double?[] _delta;
        private static string _liveRasterPath;

        // ---- live run state (written from the process reader thread, read on the UI thread) ----
        private static readonly object _sync = new object();
        private static readonly List<string> _log = new List<string>();
        private static Process _process;
        private static volatile bool _running;
        private static volatile float _progress;   // 0..1
        private static volatile int _doneCount;
        private static volatile int _totalCount;
        private static string _status = "Idle.";

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
            InitDefaults();

            //Every time the window opens, not once per session. It only fills fields that are still empty, so
            //it cannot overwrite anything typed - and opening this before loading a scenario used to mean the
            //seeding never ran at all, since the one-shot guard had already been spent.
            SeedFromScenario();
        }

        public static void Close()
        {
            if (_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
            _isOpen = false;
        }

        private static void InitDefaults()
        {
            if (_initialized)
            {
                return;
            }
            _initialized = true;

            // Default the base .wui to the currently loaded scenario.
            if (PreactGUI.Engine != null && !string.IsNullOrEmpty(PreactGUI.Engine.WorkingFile))
            {
                _baseWui = PreactGUI.Engine.WorkingFile;
            }

            // Best-effort guess of the PREACTcli location (developer layout).
            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName; // WUInity/
                string guess = Path.GetFullPath(Path.Combine(projectRoot, "..", "PREACT", "PREACTcli", "bin", "Release", "net8.0", "PREACTcli.exe"));
                if (File.Exists(guess))
                {
                    _cliExe = guess;
                }
            }
            catch { }
        }

        /// <summary>
        /// Fills the ELMFIRE fields from the loaded scenario's own <c>[ELMFIRE]</c> settings.
        /// </summary>
        /// <remarks>
        /// This window asked for the executable, the namelist, the inputs folder and the GDAL bin directory by
        /// hand, immediately below a "Base .wui" field naming a scenario that already states all four. So the
        /// campaign could be pointed at one scenario's namelist while running another's, and the way to notice
        /// was that the answer looked wrong.
        ///
        /// Seeded rather than locked: a campaign legitimately runs a different namelist from the one the
        /// scenario runs interactively — a longer <c>tstop</c>, perturbation blocks — so the fields stay
        /// editable and anything already typed is left alone.
        /// </remarks>
        private static void SeedFromScenario()
        {
            PREACT.Input.PREACTInput input = ScenarioEditorWindow.Input;

            //Neither the executable nor GDAL needs a scenario - one is the vendored build found by walking up
            //from the assembly, the other is on PATH or under a QGIS/OSGeo4W install. Resolved before the
            //scenario check so both fields are filled even with nothing loaded.
            if (string.IsNullOrEmpty(_elmfireExe))
            {
                _elmfireExe = PREACT.Utility.ElmfireCoupling.ResolveExecutable(
                    input?.RootFolder, input?.WildfireModule?.ElmfireInput?.ElmfireExe) ?? string.Empty;
            }

            if (string.IsNullOrEmpty(_gdalBin))
            {
                _gdalBin = PREACT.Utility.GdalTools.FindBinDirectory() ?? string.Empty;
            }

            if (input == null || input.WildfireModule == null)
            {
                return;
            }

            PREACT.Input.ElmfireInput elmfire = input.WildfireModule.ElmfireInput;
            if (elmfire == null)
            {
                return;
            }

            string root = input.RootFolder;

            //Turning generation on when the scenario runs ELMFIRE: the alternative is a folder of
            //pre-generated rasters, which a scenario that computes its own fire does not have. Once only, so
            //reopening the window does not re-tick a box that was deliberately unticked.
            if (!_seededGenerateFlag
                && input.WildfireModule.Module == PREACT.Input.WildfireModuleInput.WildfireModules.ELMFIRE)
            {
                _generateWithElmfire = true;
                _seededGenerateFlag = true;
            }

            string caseDir = Resolve(root, elmfire.CaseDirectory);

            if (string.IsNullOrEmpty(_elmfireInputs) && !string.IsNullOrEmpty(caseDir))
            {
                string inputs = Path.Combine(caseDir, "inputs");
                if (Directory.Exists(inputs)) { _elmfireInputs = inputs; }
            }

            if (string.IsNullOrEmpty(_elmfireTemplate) && !string.IsNullOrEmpty(caseDir))
            {
                //The scenario's own template if it names one, otherwise the namelist the case build wrote -
                //which is what an ELMFIRE scenario normally has instead.
                string named = Resolve(root, elmfire.NamelistTemplate);
                string generated = Path.Combine(caseDir, "elmfire.data");

                if (!string.IsNullOrEmpty(named) && File.Exists(named)) { _elmfireTemplate = named; }
                else if (File.Exists(generated)) { _elmfireTemplate = generated; }
            }


            //The scenario's own override wins over the probe above, since naming it is a deliberate choice to
            //use a particular GDAL rather than whatever is first on PATH.
            string namedGdal = Resolve(root, elmfire.PathToGdal);
            if (!string.IsNullOrEmpty(namedGdal) && Directory.Exists(namedGdal))
            {
                _gdalBin = namedGdal;
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

            ImGui.Begin("Probabilistic trigger boundary", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            ImGui.TextWrapped("Runs one evacuation + k-PERIL trigger boundary per fire realization, then aggregates them into a per-cell probability raster. Each realization is a full WUInity run, so this can take a long time.");

            ImGui.BeginDisabled(_running);

            FileRow("PREACTcli.exe", () => _cliExe, v => _cliExe = v, false, "Executable (*.exe)");
            FileRow("Base .wui", () => _baseWui, v => _baseWui = v, false, "WUInity input (*.wui)");

            ImGui.Checkbox("Run until converged (instead of a fixed count)", ref _convergeMode);

            if (_convergeMode)
            {
                ImGui.TextWrapped("Stops once every decile of the probability raster changes by less than the tolerance for the required number of consecutive realizations. The maximum is a ceiling, not a target.");
                ImGui.InputInt("Maximum realizations", ref _max);
                ImGui.InputInt("Parallel realizations (0 = CPU count)", ref _parallel);
                ImGui.InputInt("Consecutive stable realizations", ref _streakTarget);
                ImGui.InputFloat("Per-decile tolerance (%)", ref _tolerancePercent);

                ImGui.Checkbox("Generate realizations with ELMFIRE", ref _generateWithElmfire);
                if (_generateWithElmfire)
                {
                    //Seeded from the loaded scenario's [ELMFIRE] section, and left editable: a campaign
                    //legitimately runs a different namelist from the one the scenario runs interactively.
                    Fields.Hint("Prefilled from the loaded scenario where it says. Edit to run a different",
                                "namelist or case than the scenario itself uses.");

                    //The two the case actually decides. The executable and GDAL are not asked for: both resolve
                    //themselves, and asking made this window refuse to start on a machine where everything was
                    //installed - the fields were seeded only from scenario keys nobody sets, precisely because
                    //everything else finds them automatically.
                    FileRow("ELMFIRE namelist", () => _elmfireTemplate, v => _elmfireTemplate = v, false, "ELMFIRE data (*.data)");
                    FileRow("ELMFIRE inputs folder", () => _elmfireInputs, v => _elmfireInputs = v, true, null);

                    //Said here because the campaign deliberately overrides something the scenario sets. A fixed
                    //[IgnitionPoint] is for running one named fire through WUInity; a probability raster needs
                    //the ignition to move, or every realization is the same fire and the result is that one
                    //boundary at probability 1.
                    ImGui.TextWrapped("Each realization ignites somewhere the case's ignition mask allows, "
                        + "weighted by the mask. The scenario's fixed ignition point is not used in a campaign.");

                    ImGui.TextDisabled("elmfire.exe: " + (string.IsNullOrEmpty(_elmfireExe) ? "not found" : _elmfireExe));
                    ImGui.TextDisabled("GDAL: " + (string.IsNullOrEmpty(_gdalBin) ? "not found" : _gdalBin));

                    //Exposed because it is the setting that decides whether the campaign means anything, and
                    //it was previously fixed at the CLI's default with no way to see or change it. Too short
                    //and distant ignitions never arrive - they are then counted as fires that did not threaten
                    //the town, and the boundary comes out too tight. It also has to be covered by the case's
                    //weather: ELMFIRE refuses a run with fewer bands than hours.
                    ImGui.InputInt("Hours per realization", ref _tstopHours);
                    if (_tstopHours < 1) _tstopHours = 1;
                    Fields.Hint($"{_tstopHours} h of fire per realization. Three days (72 h) is the default:",
                                "ignitions are drawn from the whole domain, and the ones furthest away decide",
                                "how far out the boundary must sit.",
                                "",
                                $"A multi-band weather series must cover the run - {_tstopHours} hourly bands",
                                "here - or ELMFIRE refuses every realization. A one-band series is exempt and",
                                "is held for the whole fire, which is what the box below writes.");
                    if (string.IsNullOrEmpty(_elmfireExe))
                    {
                        Fields.Warn("No elmfire.exe found, so no realization can compute a fire.");
                    }

                    if (string.IsNullOrEmpty(_gdalBin))
                    {
                        //Only when the probe found nothing, which means QGIS/OSGeo4W is not installed or is not
                        //on PATH. Stating the consequence, because the failure it prevents looks like success.
                        Fields.Warn("No GDAL tools found. Required: without them ELMFIRE writes no rasters and",
                                    "still exits 0, so the campaign would run to completion producing nothing.");
                    }
                    ImGui.Checkbox("Give each realization its own weather", ref _realizationWeather);
                    Fields.Hint("On by default: each realization gets its own weather rasters, in its own",
                                "weather folder, so the ensemble varies in weather and not only in where the",
                                "fire starts. Terrain and fuels stay shared and read-only.",
                                "",
                                "Off, every realization reads the one series the case was built with and",
                                "they all burn under the same wind. That is the right choice when replaying",
                                "a known day.");

                    if (_realizationWeather)
                    {
                        ImGui.Indent();
                        ImGui.Checkbox("Draw each parameter from a fitted distribution", ref _fittedWeather);
                        Fields.Hint("On by default. A normal distribution is fitted to each weather parameter",
                                    "over the worst fire-weather days in the record, and each realization draws",
                                    "one value of each - wind speed, temperature, humidity - held constant for",
                                    "the whole fire. So one weather band, and one WindNinja solve.",
                                    "",
                                    "The ensemble is then continuous in weather instead of confined to the days",
                                    "that happened. That matters: a 25-year record holds 25 peak days, so a",
                                    "500-realization campaign replaying days draws each about twenty times and",
                                    "can never produce a day worse than the worst on file - while the boundary",
                                    "is meant to hold for longer than the record is.",
                                    "",
                                    "Two costs, both from there being no historical day any more. The",
                                    "parameters are drawn independently, so a realization can come out hot and",
                                    "humid at once in a way the record never is. And Nelson cannot run - it",
                                    "needs real antecedent hours - so dead fuel moisture is derived from the",
                                    "drawn temperature and humidity by Simard's equilibrium content: no drying",
                                    "history, and uniform over the domain instead of varying with slope,",
                                    "aspect and canopy.",
                                    "",
                                    "Off, each realization replays one actual peak fire-weather day: WindNinja",
                                    "per band over that day's own hours, and Nelson marched over the 20 real",
                                    "days before it. Every parameter is consistent with every other and the",
                                    "moisture varies with the terrain. Turn it off when the heavy fuels are",
                                    "what the fire turns on.");

                        if (_fittedWeather)
                        {
                            ImGui.InputInt("Days per year in the fitted pool", ref _candidateDaysPerYear);
                            if (_candidateDaysPerYear < 1) _candidateDaysPerYear = 1;
                            Fields.Hint($"The {_candidateDaysPerYear} highest-FWI days of each year on record are what the",
                                        "distributions are fitted to. Ten a year over 26 years is 260 days, where",
                                        "one a year would be 26 - a thin sample to take a standard deviation from.",
                                        "",
                                        "This is a severity dial as much as a sample size, and the direction is",
                                        "worth knowing. A deeper pool reaches down into milder days and pulls the",
                                        "fitted mean with it: on the Mati archive, going from 1 a year to 10 moves",
                                        "the mean from 36.1 to 34.2 C and the humidity from 16.5 to 20.7 %. The",
                                        "spread hardly moves either way.",
                                        "",
                                        "So 1 a year fits 'the worst day of the year' on a sample of 26, and 10",
                                        "fits 'a bad day' on a sample of 260. Vary it on your own domain and",
                                        "watch the fitted numbers in the campaign's first log lines.");

                            ImGui.Checkbox("Draw live fuel moisture too", ref _fitLiveFuelMoisture);
                            Fields.Hint("On by default. The NFDRS4 Growing Season Index model is marched over the",
                                        "whole record once, and each realization takes a live herbaceous and live",
                                        "woody moisture from it.",
                                        "",
                                        "These two are the one exception to the fitted-normal draw above: the pair",
                                        "is resampled from one candidate day, unchanged. Their values pile up on",
                                        "the fully-cured floor - on the Mati record the herbaceous median and 5th",
                                        "percentile are both exactly 30 % - and a normal fitted to that loses its",
                                        "whole lower half to the floor, coming out 6 points wetter than the record",
                                        "it was fitted to. Resampling reproduces the record's own mean, and takes",
                                        "both fuels from the same day so they stay on one point of one season.",
                                        "",
                                        "This matters more than it sounds. Off, live moisture is two fixed numbers",
                                        "in the case's namelist - 60 % and 90 % - identical in every realization of",
                                        "every campaign, whatever the drawn weather says. In a grass or shrub fuel",
                                        "model that constant does a lot of the work in setting the spread rate.",
                                        "",
                                        "It is a seasonal state, driven by day length, night temperature, vapour",
                                        "pressure deficit and antecedent rain over weeks - which is why it has to",
                                        "be marched over the record rather than read off the fire's own hour.",
                                        "",
                                        "Two honest limits. The model takes latitude and weather and no terrain, so",
                                        "the value is one number for the whole domain - correct for this model,",
                                        "not a shortcut, which is why it is written as a namelist scalar and not",
                                        "as an MLH/MLW raster. And because the pair is resampled, the ensemble",
                                        "cannot reach a live moisture the record does not contain - unlike",
                                        "temperature, humidity and wind, which their fitted normals can extend",
                                        "past the record. That is the right trade here: these are a bounded",
                                        "seasonal state whose extremes are the model's own limits, not a tail.",
                                        "",
                                        "Reported with everything else in weather_distributions.csv.");
                        }
                        ImGui.Unindent();
                    }

                    //Disabled rather than merely warned about: aiming the wind needs the wind rasters written
                    //per realization, so the CLI rejects the pair outright - and it does so after the campaign
                    //has been launched, which is a slow way to find out about a box that could not be ticked.
                    if (!_realizationWeather)
                    {
                        _windToWui = false;
                        ImGui.BeginDisabled();
                    }
                    ImGui.Checkbox("Aim the wind from the ignition at the WUI area", ref _windToWui);
                    if (!_realizationWeather)
                    {
                        ImGui.EndDisabled();
                        Fields.Hint("Needs per-realization weather: the bearing is a property of the",
                                    "realization's own ignition, and one shared series has a single wind field",
                                    "for all of them. Tick the box above to enable this.");
                    }
                    else
                    {
                        Fields.Hint("On by default. Each realization's wind direction is set to blow from its own",
                                    "ignition towards the centroid of the case's painted WUI area, and that",
                                    "direction is what WindNinja solves from - so what the fire reads is the",
                                    "terrain's answer to a wind aimed at town, not a uniform field.",
                                    "",
                                    "A trigger boundary asks how much warning the community needs, and a",
                                    "realization whose fire is blown away from town answers nothing while still",
                                    "counting as a draw. The variety then lives in where the fire starts, how far",
                                    "it has to travel, and the realization's own drawn speed and moisture - only",
                                    "the direction is chosen rather than drawn.",
                                    "",
                                    "This is not ELMFIRE's POINT_WIND_TO_CENTER, which aims at the centre of the",
                                    "domain - a different place, 3.4 km from the WUI area on the Mati case.",
                                    "",
                                    "Off, the direction is resampled from the pool instead - one candidate day's",
                                    "actual direction, unchanged. It is not fitted like the other parameters:",
                                    "direction is circular, and a normal fitted to degrees is not imprecise but",
                                    "wrong (10 and 350 degrees average to 180, the opposite of both).",
                                    "",
                                    "Draws the ignition on this side rather than in ELMFIRE (same mask, same",
                                    "weighting), since the bearing needs it first.");
                    }

                    //Hidden rather than disabled when the fitted draw is on: it is not a choice being denied,
                    //it is a question that mode does not have - the draw is one value held for the whole fire,
                    //so a second band could only be a copy of the first.
                    if (!(_realizationWeather && _fittedWeather))
                    {
                        ImGui.Checkbox("One weather band, held for the whole fire", ref _singleBandWeather);
                        Fields.Hint("Combines with the boxes above, and changes what they cost.",
                                    "",
                                    "With per-realization weather: the realization still draws its own day, but",
                                    "that day's wind is solved once instead of once per hour of the run - 1 solve",
                                    $"rather than {_tstopHours} per realization.",
                                    "",
                                    "Without it: a one-band copy of the case's own rasters.",
                                    "",
                                    "Either way it is what makes a fire longer than its weather series legal.",
                                    "ELMFIRE exempts a one-band series from having to cover the run and holds it;",
                                    "a series of two or more bands that falls short is refused outright.",
                                    "",
                                    "The cost: one hour's wind and moisture from start to finish, so the diurnal",
                                    "cycle is gone. A three-day fire under a fixed afternoon wind is not the same",
                                    $"fire as one that calms overnight. For the full picture, {_tstopHours} bands",
                                    "per realization - and that many WindNinja solves.");
                    }
                }
                else
                {
                    FileRow("Realization folder", () => _rasterDir, v => _rasterDir = v, true, null);
                }
            }
            else
            {
                FileRow("Realization folder", () => _rasterDir, v => _rasterDir = v, true, null);
                ImGui.InputInt("Count", ref _count);
            }

            ImGui.InputInt("Start index", ref _start);
            ImGui.InputInt("Index zero-padding", ref _pad);

            //Only used when reading a pre-generated ensemble. Generating with ELMFIRE names the rasters itself,
            //so leaving these alone there is correct rather than an oversight.
            bool patternsUsed = !_convergeMode || !_generateWithElmfire;

            ImGui.SeparatorText("Raster filename patterns ( {i} = index, * = anything )");
            ImGui.BeginDisabled(!patternsUsed);
            ImGui.InputText("Time of arrival", ref _toa, 128);
            ImGui.InputText("Rate of spread", ref _ros, 128);
            ImGui.InputText("Spread direction", ref _sd, 128);
            ImGui.InputText("Fireline intensity", ref _fi, 128);
            ImGui.EndDisabled();

            if (patternsUsed)
            {
                //ELMFIRE names its output after the run's stop time in seconds, so an ensemble's filenames
                //carry the duration it was run for and change whenever that changes.
                Fields.Hint($"ELMFIRE ends these with the run's stop time in seconds - _{_tstopHours * 3600:0000000}",
                            $"for a {_tstopHours} h ensemble - so the trailing number changes whenever the",
                            "hours do. The '*' above absorbs it, and a pattern that does spell a number out",
                            "is retried with it wildcarded when no such file exists.",
                            "",
                            "Where several files then match one realization, the largest trailing number wins:",
                            "the fully grown fire rather than an earlier snapshot of it.");
            }
            else
            {
                Fields.Hint("Not used: ELMFIRE names its own output when generating realizations.");
            }

            ImGui.Checkbox("Resume (skip realizations already computed)", ref _resume);
            Fields.Hint("On: realizations that already have a trigger boundary are reused, and only the",
                        "missing ones run.",
                        "",
                        "Off: everything is computed again. The previous campaign's boundaries, probability",
                        "raster and ensemble rasters are moved into _output/previous_campaign_<timestamp>",
                        "rather than deleted, and each realization's ELMFIRE outputs and scratch are emptied",
                        "before it runs - otherwise a file from last time can be read back as this run's",
                        "result without anything saying so.");
            FileRow("Output raster (optional)", () => _outPath, v => _outPath = v, false, "Raster (*.asc *.tif)");

            ImGui.EndDisabled();

            ImGui.Separator();

            if (!_running)
            {
                if (ImGui.Button("Run"))
                {
                    StartRun();
                }
            }
            else
            {
                if (ImGui.Button("Cancel"))
                {
                    CancelRun();
                }
            }

            // Progress
            float p = _progress;
            string overlay = _totalCount > 0 ? $"{_doneCount} / {_totalCount}" : (_running ? "starting..." : "");
            ImGui.ProgressBar(p, new Vector2(-1, 0), overlay);
            ImGui.Text(_status);

            DrawConvergence();

            // Log
            ImGui.SeparatorText("Log");
            // Third argument is ImGuiChildFlags, not the bool 'border' of older bindings. The
            // member for a bordered child was renamed across ImGui.NET versions (Border -> Borders),
            // so the value is written numerically: it is 1 in both, and this file has to compile
            // against whatever version the uimgui package pins.
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
            if (_running)
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
        /// A path with a text box and a browse button.
        /// </summary>
        /// <remarks>
        /// The picked path is routed back through a setter rather than by matching the label against a switch,
        /// which is how this worked and why <b>the browse button did nothing on four of the eight fields</b>:
        /// the switch listed <c>PREACTcli.exe</c>, <c>Base .wui</c>, <c>Realization folder</c> and
        /// <c>Output raster</c>, and the four ELMFIRE rows added later were never added to it. The dialog
        /// opened, a file was picked, and the field stayed empty — the field could still be typed into, so it
        /// looked like a fussy button rather than a broken one.
        ///
        /// A <c>ref</c> parameter cannot be captured by the callback (it outlives the frame), so each row
        /// passes an explicit setter and the compiler is what now ensures one exists.
        /// </remarks>
        private static void FileRow(string label, Func<string> get, Action<string> set, bool folder, string filter)
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

            string initial = !string.IsNullOrEmpty(_rasterDir)
                ? _rasterDir
                : (PreactGUI.Engine != null ? PreactGUI.Engine.WorkingFolder : null);

            SimpleFileBrowser.FileBrowser.ShowLoadDialog(
                paths =>
                {
                    if (paths != null && paths.Length > 0) set(paths[0]);
                },
                () => { },
                mode, false, initial, null, "Select", "Select");
        }

        /// <summary>
        /// Shows the convergence state the CLI reports, so the run can be judged while it is still
        /// going rather than only from the CSV afterwards. Deliberately plain text rather than an
        /// ImGui table: the table API has moved between ImGui.NET versions and this file has to
        /// compile against whatever the uimgui package pins.
        /// </summary>
        private static void DrawConvergence()
        {
            double[] deciles, area;
            double?[] delta;
            int nSuccess, nFailed, streak;
            bool converged;
            string live;

            lock (_sync)
            {
                deciles = _deciles;
                area = _area;
                delta = _delta;
                nSuccess = _nSuccess;
                nFailed = _nFailed;
                streak = _streak;
                converged = _converged;
                live = _liveRasterPath;
            }

            if (deciles == null || area == null)
            {
                return;
            }

            ImGui.SeparatorText("Convergence");
            ImGui.Text($"{nSuccess} succeeded, {nFailed} failed    streak {streak} / {_streakTarget}" +
                       (converged ? "    CONVERGED" : string.Empty));

            ImGui.Text("  decile        area (m2)      change");
            for (int i = 0; i < deciles.Length && i < area.Length; ++i)
            {
                //a decile with no baseline yet is shown as "-" rather than 0%, because "has not
                //moved" and "has nothing to move from" mean very different things for the streak
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
            if (ParseProgressJson(line) || ParseProgressRaster(line))
            {
                return;
            }

            // "PROGRESS <done>/<total> ..."
            const string tag = "PROGRESS ";
            int idx = line.IndexOf(tag, StringComparison.Ordinal);
            if (idx < 0) return;
            string rest = line.Substring(idx + tag.Length).Trim();
            string token = rest.Split(' ')[0]; // "<done>/<total>"
            string[] parts = token.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out int done) && int.TryParse(parts[1], out int total) && total > 0)
            {
                _doneCount = done;
                _totalCount = total;
                _progress = Mathf.Clamp01((float)done / total);
                _status = _convergeMode
                    ? $"{done} of at most {total} realizations..."
                    : $"Processing realization {done + 1} of {total}...";
            }
        }

        private static bool ParseProgressRaster(string line)
        {
            const string tag = "PROGRESS_RASTER ";
            int idx = line.IndexOf(tag, StringComparison.Ordinal);
            if (idx < 0) return false;

            lock (_sync)
            {
                _liveRasterPath = line.Substring(idx + tag.Length).Trim();
            }
            return true;
        }

        /// <summary>
        /// Reads one PROGRESS_JSON line from converge-trigger.
        ///
        /// Hand-parsed rather than run through a JSON library: the shape is fixed and emitted by
        /// code in this same repository, and this runs on the child process's output thread where
        /// an exception would be swallowed and simply stop the display updating. Anything
        /// unrecognised is ignored rather than throwing.
        /// </summary>
        private static bool ParseProgressJson(string line)
        {
            const string tag = "PROGRESS_JSON ";
            int idx = line.IndexOf(tag, StringComparison.Ordinal);
            if (idx < 0) return false;

            string json = line.Substring(idx + tag.Length);

            try
            {
                lock (_sync)
                {
                    if (TryGetInt(json, "nSuccess", out int ok)) _nSuccess = ok;
                    if (TryGetInt(json, "nFailed", out int failed)) _nFailed = failed;
                    if (TryGetInt(json, "streak", out int streak)) _streak = streak;
                    if (TryGetInt(json, "streakTarget", out int target) && target > 0) _streakTarget = target;
                    _converged = json.Contains("\"converged\":true");

                    double[] deciles = GetArray(json, "deciles");
                    double[] area = GetArray(json, "area");
                    if (deciles != null) _deciles = deciles;
                    if (area != null) _area = area;
                    _delta = GetNullableArray(json, "delta");
                }
            }
            catch
            {
                //a malformed progress line must never take down the reader thread
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

        /// <summary>As <see cref="GetArray"/>, but keeps JSON null distinct from 0 — the CLI uses
        /// null for a decile that has no baseline to compare against yet.</summary>
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

        private static void StartRun()
        {
            if (string.IsNullOrEmpty(_cliExe) || !File.Exists(_cliExe))
            {
                _status = "PREACTcli.exe not found — set its path.";
                return;
            }
            if (string.IsNullOrEmpty(_baseWui) || !File.Exists(_baseWui))
            {
                _status = "Base .wui not found — set its path.";
                return;
            }
            bool needRasterDir = !_convergeMode || !_generateWithElmfire;
            if (needRasterDir && (string.IsNullOrEmpty(_rasterDir) || !Directory.Exists(_rasterDir)))
            {
                _status = "Realization folder not found — set its path.";
                return;
            }

            if (_convergeMode && _generateWithElmfire)
            {
                if (string.IsNullOrEmpty(_elmfireExe) || !File.Exists(_elmfireExe))
                {
                    _status = "elmfire.exe not found — set its path.";
                    return;
                }
                if (string.IsNullOrEmpty(_elmfireTemplate) || !File.Exists(_elmfireTemplate))
                {
                    _status = "ELMFIRE namelist not found — set its path.";
                    return;
                }
                if (string.IsNullOrEmpty(_elmfireInputs) || !Directory.Exists(_elmfireInputs))
                {
                    _status = "ELMFIRE inputs folder not found — set its path.";
                    return;
                }
                if (string.IsNullOrEmpty(_gdalBin) || !Directory.Exists(_gdalBin))
                {
                    //Refused rather than warned about: without it the run does not fail, it
                    //silently produces nothing while still exiting 0.
                    _status = "GDAL bin folder is required when generating realizations — set its path.";
                    return;
                }
            }

            lock (_sync)
            {
                _log.Clear();
                _deciles = null;
                _area = null;
                _delta = null;
                _liveRasterPath = null;
                _nSuccess = 0;
                _nFailed = 0;
                _streak = 0;
                _converged = false;
            }

            _progress = 0f;
            _doneCount = 0;
            _totalCount = _convergeMode ? _max : _count;
            _status = "Starting...";

            var psi = new ProcessStartInfo
            {
                FileName = _cliExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_cliExe),
            };
            psi.ArgumentList.Add(_convergeMode ? "converge-trigger" : "probabilistic-trigger");
            psi.ArgumentList.Add("--wui");   psi.ArgumentList.Add(_baseWui);

            if (_convergeMode)
            {
                psi.ArgumentList.Add("--max"); psi.ArgumentList.Add(_max.ToString(CultureInfo.InvariantCulture));
                psi.ArgumentList.Add("--streak"); psi.ArgumentList.Add(_streakTarget.ToString(CultureInfo.InvariantCulture));
                //shown as a percentage, sent as the fraction the CLI expects
                psi.ArgumentList.Add("--tolerance");
                psi.ArgumentList.Add((_tolerancePercent / 100f).ToString("R", CultureInfo.InvariantCulture));
                if (_parallel > 0)
                {
                    psi.ArgumentList.Add("--parallel"); psi.ArgumentList.Add(_parallel.ToString(CultureInfo.InvariantCulture));
                }
                //this is what drives the convergence display above
                psi.ArgumentList.Add("--progress-json");

                if (_generateWithElmfire)
                {
                    psi.ArgumentList.Add("--elmfire");          psi.ArgumentList.Add(_elmfireExe);
                    psi.ArgumentList.Add("--elmfire-template"); psi.ArgumentList.Add(_elmfireTemplate);
                    psi.ArgumentList.Add("--elmfire-inputs");   psi.ArgumentList.Add(_elmfireInputs);
                    psi.ArgumentList.Add("--gdal");             psi.ArgumentList.Add(_gdalBin);
                    psi.ArgumentList.Add("--tstop");
                    psi.ArgumentList.Add((_tstopHours * 3600).ToString(CultureInfo.InvariantCulture));
                    //Sent explicitly either way rather than relying on the CLI's default, so what the window
                    //shows is what runs even if that default moves again.
                    psi.ArgumentList.Add(_realizationWeather ? "--realization-weather" : "--shared-weather");
                    if (_realizationWeather)
                    {
                        psi.ArgumentList.Add(_fittedWeather ? "--fitted-weather" : "--historical-day-weather");
                        if (_fittedWeather)
                        {
                            psi.ArgumentList.Add("--candidate-days-per-year");
                            psi.ArgumentList.Add(_candidateDaysPerYear.ToString(CultureInfo.InvariantCulture));
                            if (!_fitLiveFuelMoisture) { psi.ArgumentList.Add("--no-live-fuel-moisture"); }
                        }
                    }
                    //Not sent with the fitted draw: that mode is one band by construction, and passing the
                    //flag would suggest the two settings interact when the window has hidden the box.
                    if (_singleBandWeather && !(_realizationWeather && _fittedWeather))
                    {
                        psi.ArgumentList.Add("--single-band-weather");
                    }
                    psi.ArgumentList.Add(_windToWui ? "--wind-to-wui" : "--no-wind-to-wui");
                }
                else
                {
                    psi.ArgumentList.Add("--dir"); psi.ArgumentList.Add(_rasterDir);
                }
            }
            else
            {
                psi.ArgumentList.Add("--dir");   psi.ArgumentList.Add(_rasterDir);
                psi.ArgumentList.Add("--count"); psi.ArgumentList.Add(_count.ToString());
            }

            psi.ArgumentList.Add("--start"); psi.ArgumentList.Add(_start.ToString());
            psi.ArgumentList.Add("--pad");   psi.ArgumentList.Add(_pad.ToString());
            psi.ArgumentList.Add("--toa");   psi.ArgumentList.Add(_toa);
            psi.ArgumentList.Add("--ros");   psi.ArgumentList.Add(_ros);
            psi.ArgumentList.Add("--sd");    psi.ArgumentList.Add(_sd);
            psi.ArgumentList.Add("--fi");    psi.ArgumentList.Add(_fi);
            if (_resume) { psi.ArgumentList.Add("--resume"); }
            if (!string.IsNullOrEmpty(_outPath)) { psi.ArgumentList.Add("--out"); psi.ArgumentList.Add(_outPath); }

            try
            {
                _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                _process.OutputDataReceived += (s, e) => AppendLog(e.Data);
                _process.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLog(e.Data); };
                _process.Exited += (s, e) => OnProcessExited();

                _running = true;
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                AppendLog("Launched: " + _cliExe + (_convergeMode ? " converge-trigger ..." : " probabilistic-trigger ..."));
            }
            catch (Exception ex)
            {
                _running = false;
                _status = "Failed to start: " + ex.Message;
                AppendLog(_status);
            }
        }

        private static void OnProcessExited()
        {
            int code = -1;
            try { code = _process != null ? _process.ExitCode : -1; } catch { }
            _running = false;
            if (code == 0)
            {
                _progress = 1f;
                bool converged;
                lock (_sync) { converged = _converged; }

                //A converge run that hits --max without converging still exits 0, so the exit code
                //alone would report an unconverged result as a clean success.
                _status = !_convergeMode
                    ? "Finished. Probability raster written to the case _output folder."
                    : converged
                        ? "Converged. Probability raster written to the case _output folder."
                        : "Reached the maximum without converging — the probability raster is not yet stable.";
            }
            else
            {
                _status = "Process exited with code " + code + " (see log).";
            }
            AppendLog(_status);
        }

        private static void CancelRun()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.Kill(); // best effort; a child PREACT.exe may need a moment to stop
                    AppendLog("Cancel requested — killing process.");
                }
            }
            catch (Exception ex)
            {
                AppendLog("Cancel error: " + ex.Message);
            }
            _running = false;
            _status = "Cancelled.";
        }
    }
}
