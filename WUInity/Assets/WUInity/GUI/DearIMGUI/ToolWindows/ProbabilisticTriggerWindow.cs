using ImGuiNET;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            FileRow("Realization folder", ref _rasterDir, true, null);

            ImGui.InputInt("Count", ref _count);
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
                _status = $"Processing realization {done + 1} of {total}...";
            }
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
            if (string.IsNullOrEmpty(_rasterDir) || !Directory.Exists(_rasterDir))
            {
                _status = "Realization folder not found — set its path.";
                return;
            }

            lock (_sync) { _log.Clear(); }
            _progress = 0f;
            _doneCount = 0;
            _totalCount = _count;
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
            psi.ArgumentList.Add("probabilistic-trigger");
            psi.ArgumentList.Add("--wui");   psi.ArgumentList.Add(_baseWui);
            psi.ArgumentList.Add("--dir");   psi.ArgumentList.Add(_rasterDir);
            psi.ArgumentList.Add("--count"); psi.ArgumentList.Add(_count.ToString());
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
                AppendLog("Launched: " + _cliExe + " probabilistic-trigger ...");
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
                _status = "Finished. Probability raster written to the case _output folder.";
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
