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
        private static string _toa = "time_of_arrival_{i}_0072000.tif";
        private static string _ros = "vs_{i}_0072000.tif";
        private static string _sd  = "spread_dir_{i}_0072000.tif";
        private static string _fi  = "flin_{i}_0072000.tif";
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
        private static string _elmfireExe = string.Empty;
        private static string _elmfireTemplate = string.Empty;
        private static string _elmfireInputs = string.Empty;
        private static string _gdalBin = string.Empty;
        private static bool _realizationWeather;

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

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.Begin("Probabilistic trigger boundary", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            ImGui.TextWrapped("Runs one evacuation + k-PERIL trigger boundary per fire realization, then aggregates them into a per-cell probability raster. Each realization is a full WUInity run, so this can take a long time.");

            ImGui.BeginDisabled(_running);

            FileRow("PREACTcli.exe", ref _cliExe, false, "Executable (*.exe)");
            FileRow("Base .wui", ref _baseWui, false, "WUInity input (*.wui)");

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
                    FileRow("elmfire.exe", ref _elmfireExe, false, "Executable (*.exe)");
                    FileRow("ELMFIRE namelist", ref _elmfireTemplate, false, "ELMFIRE data (*.data)");
                    FileRow("ELMFIRE inputs folder", ref _elmfireInputs, true, null);
                    // Effectively mandatory, not optional: without it ELMFIRE inherits the ambient
                    // PATH, and on any machine set up for WUInity that includes SUMO, whose bundled
                    // proj.db shadows GDAL's. The EPSG lookup then fails, every gdal_translate
                    // fails, and the run still exits 0 having deleted its own intermediates.
                    FileRow("GDAL bin folder (required)", ref _gdalBin, true, null);
                    ImGui.Checkbox("Draw a historical weather day per realization", ref _realizationWeather);
                }
                else
                {
                    FileRow("Realization folder", ref _rasterDir, true, null);
                }
            }
            else
            {
                FileRow("Realization folder", ref _rasterDir, true, null);
                ImGui.InputInt("Count", ref _count);
            }

            ImGui.InputInt("Start index", ref _start);
            ImGui.InputInt("Index zero-padding", ref _pad);

            ImGui.SeparatorText("Raster filename patterns ( {i} = index )");
            ImGui.InputText("Time of arrival", ref _toa, 128);
            ImGui.InputText("Rate of spread", ref _ros, 128);
            ImGui.InputText("Spread direction", ref _sd, 128);
            ImGui.InputText("Fireline intensity", ref _fi, 128);

            ImGui.Checkbox("Resume (skip realizations already computed)", ref _resume);
            FileRow("Output raster (optional)", ref _outPath, false, "Raster (*.asc *.tif)");

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

        private static void FileRow(string label, ref string value, bool folder, string filter)
        {
            ImGui.InputText(label, ref value, 512);
            ImGui.SameLine();
            if (ImGui.Button("..." + "##" + label))
            {
                BrowseInto(label, folder, filter);
            }
        }

        // SimpleFileBrowser uses a callback; route the result back to the right field by label.
        private static string _browseTarget;
        private static void BrowseInto(string label, bool folder, string filter)
        {
            _browseTarget = label;
            var mode = folder ? SimpleFileBrowser.FileBrowser.PickMode.Folders : SimpleFileBrowser.FileBrowser.PickMode.Files;
            string initial = !string.IsNullOrEmpty(_rasterDir) ? _rasterDir : (PreactGUI.Engine != null ? PreactGUI.Engine.WorkingFolder : null);
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(OnBrowsePicked, OnBrowseCancel, mode, false, initial, null, "Select", "Select");
        }

        private static void OnBrowsePicked(string[] paths)
        {
            if (paths == null || paths.Length == 0) return;
            string picked = paths[0];
            switch (_browseTarget)
            {
                case "PREACTcli.exe": _cliExe = picked; break;
                case "Base .wui": _baseWui = picked; break;
                case "Realization folder": _rasterDir = picked; break;
                case "Output raster (optional)": _outPath = picked; break;
            }
        }

        private static void OnBrowseCancel() { }

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
                    if (_realizationWeather) { psi.ArgumentList.Add("--realization-weather"); }
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
