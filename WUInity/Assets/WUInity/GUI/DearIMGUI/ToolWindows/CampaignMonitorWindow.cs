using ImGuiNET;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using PREACT.Utility;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Watches a trigger campaign as it runs: overall progress and convergence, every realization in flight with the
    /// stage it is at (its weather, ELMFIRE's simulated hours and tracked nodes, the evacuation's clock), the finished
    /// realizations newest first, and the campaign's own log.
    /// </summary>
    /// <remarks>
    /// Everything is read from the campaign folder - <c>status.json</c>, <c>realizations.csv</c>,
    /// <c>trigger_convergence.csv</c>, <c>campaign.log</c> - once a second on a worker thread, never per frame. So it
    /// shows a campaign started from the Trigger campaign window, one started from a command line, or one that finished
    /// before the GUI was opened alike. The campaign's chatter stays here: the main console only gets its start and end.
    /// </remarks>
    public static class CampaignMonitorWindow
    {
        /// <summary>How often the campaign folder is read, in seconds.</summary>
        private const float PollSeconds = 1.0f;

        /// <summary>Lines of the campaign's log kept for the log pane.</summary>
        public const int MaxLogLines = 2000;

        /// <summary>A running campaign that has not rewritten its status for this long is checked for being gone.</summary>
        private const double StaleSeconds = 10.0;

        private static bool _isOpen;
        private static float _nextPoll;
        private static int _polling;

        //The folder watched, and whether it was chosen here (Watch latest / the browse button) rather than followed.
        private static volatile string _folder;
        private static bool _chosen;

        private static string _note = string.Empty;
        private static bool _followLog = true;

        //What the last poll read; replaced whole by the worker, read whole while drawing.
        private static volatile Snapshot _snapshot;

        private sealed class Snapshot
        {
            public string Folder;
            public CampaignStatus Status;

            /// <summary>The status says running but nothing has rewritten it for a while and no process holds the lock.</summary>
            public bool Gone;
            public List<FinishedRealization> Finished = new List<FinishedRealization>();
            public ConvergenceSnapshot Convergence;
            public List<string> Log = new List<string>();
            public bool LogIsOwn;
            public DateTime ReadUtc;
        }

        public static bool IsOpen => _isOpen;

        /// <summary>Opens the monitor on the campaign being run from the GUI, or else the open scenario's latest one.</summary>
        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
            _nextPoll = 0f;
        }

        /// <summary>
        /// Follows <paramref name="folder"/> from now on - the campaign the Trigger campaign window has just started.
        /// Safe from any thread.
        /// </summary>
        public static void Watch(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            _folder = folder;
            _chosen = false;
            _nextPoll = 0f;
        }

        public static void Close()
        {
            if (_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
            _isOpen = false;
        }

        // ------------------------------------------------------------------ reading, once a second, off the main thread

        private static void Poll()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextPoll) return;
            _nextPoll = now + PollSeconds;

            string folder = ChooseFolder();
            if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;

            //What the Trigger campaign window captured from the process it started, when it is this campaign's: it also
            //holds what was printed before the campaign had a folder (a setup that failed, the settings check).
            var own = new List<string>();
            bool haveOwn = ProbabilisticTriggerWindow.CopyLog(folder, own);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    _snapshot = Read(folder, haveOwn ? own : null);
                }
                catch (Exception e)
                {
                    _snapshot = new Snapshot { Folder = folder, Log = new List<string> { "The monitor could not read the campaign: " + e.Message } };
                }
                finally
                {
                    Interlocked.Exchange(ref _polling, 0);
                }
            });
        }

        /// <summary>
        /// The campaign started from the GUI while it runs; otherwise the one chosen here; otherwise a campaign running
        /// from the open scenario's folder, or its latest one.
        /// </summary>
        private static string ChooseFolder()
        {
            string launched = ProbabilisticTriggerWindow.CampaignFolder;
            if (!_chosen && !string.IsNullOrEmpty(launched))
            {
                _folder = launched;
                return launched;
            }

            string folder = _folder;
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder)) return folder;

            folder = LatestOfScenario();
            _folder = folder;
            return folder;
        }

        private static string LatestOfScenario()
        {
            PREACT.Input.PREACTInput input = ScenarioSession.Input;
            if (input == null) return null;

            List<string> running = CampaignLayout.RunningCampaigns(input.RootFolder);
            if (running.Count > 0) return running[0];

            string name = CampaignLayout.CampaignScenarioName(input.Simulation?.Name, ScenarioSession.FilePath);
            return CampaignLayout.LatestCampaignFolder(input.RootFolder, name);
        }

        private static Snapshot Read(string folder, List<string> ownLog)
        {
            var s = new Snapshot { Folder = folder, ReadUtc = DateTime.UtcNow };
            if (ownLog != null)
            {
                s.Log = ownLog;
                s.LogIsOwn = true;
            }

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return s;

            s.Status = CampaignStatus.Read(folder);
            if (s.Status != null && !s.Status.IsFinished && s.Status.UpdatedUtc != default
                && (s.ReadUtc - s.Status.UpdatedUtc).TotalSeconds > StaleSeconds)
            {
                s.Gone = !CampaignLayout.IsLockHeld(folder);
            }

            s.Finished = CampaignFiles.ReadRealizations(folder);
            s.Finished.Reverse(); //newest first
            s.Convergence = CampaignFiles.ReadLatestConvergence(folder);

            if (ownLog == null)
            {
                s.Log = CampaignFiles.ReadTail(Path.Combine(folder, CampaignLayout.CampaignLogFile), MaxLogLines);
            }
            return s;
        }

        // ------------------------------------------------------------------ drawing

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            Poll();

            ImGui.SetNextWindowSize(new Vector2(900, 700), ImGuiCond.FirstUseEver);
            ImGui.Begin("Campaign monitor###CampaignMonitor", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            Snapshot s = _snapshot;
            DrawHeader(s);

            if (s != null && s.Status != null)
            {
                DrawOverall(s);
                DrawInFlight(s);
            }
            else if (s != null && !string.IsNullOrEmpty(s.Folder) && Directory.Exists(s.Folder))
            {
                ImGui.TextDisabled("This campaign has no status file: it was run before the monitor existed, or it has not started yet.");
            }

            if (s != null)
            {
                DrawConvergence(s);
                DrawFinished(s);
                DrawLog(s);
            }

            ImGui.End();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void DrawHeader(Snapshot s)
        {
            string folder = s?.Folder ?? _folder;
            if (string.IsNullOrEmpty(folder))
            {
                ImGui.TextWrapped("No campaign to show: the open scenario has none yet. Start one from the Trigger campaign window.");
            }
            else
            {
                ImGui.TextUnformatted("Campaign: " + Path.GetFileName(folder.TrimEnd('/', '\\')));
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(folder);
            }

            ImGui.BeginDisabled(string.IsNullOrEmpty(folder));
            if (ImGui.SmallButton("Open campaign folder"))
            {
                MainMenuBar.OpenInFileManager(folder);
            }
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.SmallButton("Latest campaign"))
            {
                _folder = LatestOfScenario();
                _chosen = true;
                _nextPoll = 0f;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The open scenario's campaign running now, or else its most recent one.");
            }

            if (ProbabilisticTriggerWindow.IsRunning)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("Cancel the campaign"))
                {
                    ProbabilisticTriggerWindow.Stop();
                }
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip("Stops every ELMFIRE, WindNinja and PREACT process of the campaign started from this GUI.\n"
                                     + "Finished realizations are kept; Run again offers to reuse them.");
                }
            }

            ImGui.SameLine();
            if (ImGui.SmallButton("Trigger campaign..."))
            {
                ProbabilisticTriggerWindow.Open();
            }

            if (!string.IsNullOrEmpty(_note))
            {
                ImGui.TextDisabled(_note);
            }
        }

        private static void DrawOverall(Snapshot s)
        {
            CampaignStatus st = s.Status;
            DateTime now = DateTime.UtcNow;

            Vector4 colour = st.Phase == CampaignStatus.PhaseFailed || s.Gone ? Fields.Alert
                : st.Phase == CampaignStatus.PhaseCancelled ? Fields.Warning
                : st.Phase == CampaignStatus.PhaseDone ? (st.Converged ? Fields.Good : Fields.Warning)
                : new Vector4(0.55f, 0.75f, 1f, 1f);

            string phase = s.Gone ? "stopped without finishing (its process is gone)" : DescribePhase(st);
            ImGui.TextColored(colour, phase);

            string started = st.StartedUtc == default ? string.Empty
                : "started " + st.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string running = st.IsFinished || s.Gone || st.StartedUtc == default ? string.Empty
                : ", running for " + CampaignStatus.DescribeDuration(now - st.StartedUtc);
            string updated = st.IsFinished || st.UpdatedUtc == default ? string.Empty
                : $", status {CampaignStatus.DescribeDuration(now - st.UpdatedUtc)} old";
            ImGui.SameLine();
            ImGui.TextDisabled(started + running + updated);

            if (!string.IsNullOrEmpty(st.Message))
            {
                ImGui.TextWrapped(st.Message);
            }

            float fraction = st.Max > 0 ? Mathf.Clamp01((float)st.Done / st.Max) : 0f;
            ImGui.ProgressBar(st.Converged ? 1f : fraction, new Vector2(-1, 0), $"{st.Done} / {st.Max}");
            ImGui.TextUnformatted(st.DescribeCounts());

            string fire = CampaignLayout.DescribeFireDuration(st.FireHours);
            ImGui.TextUnformatted($"Convergence: streak {st.Streak} of {st.StreakTarget}" + (st.Converged ? "  CONVERGED" : string.Empty)
                                  + $"    fire {fire} per realization, {st.Parallel} at a time");
            Fields.Hint("The campaign stops once every decile of the probability raster has moved by less than the",
                        "tolerance for that many boundaries in a row. Fires that never reach the WUI area and failed",
                        "realizations leave the streak as it is.");
        }

        private static string DescribePhase(CampaignStatus st)
        {
            switch (st.Phase)
            {
                case CampaignStatus.PhaseSetup: return "Setting up (the weather archive and the case's inputs)...";
                case CampaignStatus.PhaseRunning: return "Running realizations...";
                case CampaignStatus.PhaseAggregating: return "Writing the probability raster and the fire statistics...";
                case CampaignStatus.PhaseDone: return st.Converged ? "Done: converged." : "Done, without converging.";
                case CampaignStatus.PhaseFailed: return "Failed.";
                case CampaignStatus.PhaseCancelled: return "Cancelled.";
                default: return st.Phase;
            }
        }

        private static void DrawInFlight(Snapshot s)
        {
            CampaignStatus st = s.Status;
            ImGui.SeparatorText($"In flight ({st.Running.Count})");
            if (st.Running.Count == 0)
            {
                ImGui.TextDisabled(st.IsFinished || s.Gone ? "None." : st.Phase == CampaignStatus.PhaseSetup ? "None yet." : "None.");
                return;
            }

            DateTime now = DateTime.UtcNow;
            ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
            if (ImGui.BeginTable("inflight", 5, flags))
            {
                ImGui.TableSetupColumn("Realization", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("Where it is", ImGuiTableColumnFlags.WidthStretch, 4f);
                ImGui.TableSetupColumn("In this stage", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("Since start", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("Log", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableHeadersRow();

                foreach (RealizationProgress r in st.Running)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(r.Id);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted(r.Describe(st.UntilStopped));
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(s.Gone || r.StageStartedUtc == default ? "-" : CampaignStatus.DescribeDuration(now - r.StageStartedUtc));
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(s.Gone || r.StartedUtc == default ? "-" : CampaignStatus.DescribeDuration(now - r.StartedUtc));
                    ImGui.TableSetColumnIndex(4);
                    if (r.Stage == RealizationProgress.StageEvacuation)
                    {
                        OpenButton("preact.log##run" + r.Id, s.Folder, r.Id, CampaignLayout.RealizationPreactLog);
                    }
                    else if (r.Stage == RealizationProgress.StageElmfire)
                    {
                        OpenButton("elmfire.log##run" + r.Id, s.Folder, r.Id, "elmfire.log");
                    }
                    else
                    {
                        OpenButton("folder##run" + r.Id, s.Folder, r.Id, null);
                    }
                }
                ImGui.EndTable();
            }
        }

        private static void DrawConvergence(Snapshot s)
        {
            ConvergenceSnapshot c = s.Convergence;
            if (c == null || c.Area == null || c.Area.Length == 0) return;

            if (!ImGui.CollapsingHeader($"Probability field after {c.Boundaries} boundar{(c.Boundaries == 1 ? "y" : "ies")} (latest {c.RealizationId})"))
            {
                return;
            }

            ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp;
            if (ImGui.BeginTable("deciles", 3, flags))
            {
                ImGui.TableSetupColumn("Probability", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Area (ha)", ImGuiTableColumnFlags.WidthStretch, 1f);
                ImGui.TableSetupColumn("Change from the boundary before", ImGuiTableColumnFlags.WidthStretch, 2f);
                ImGui.TableHeadersRow();
                for (int i = 0; i < c.Area.Length; ++i)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    double decile = c.Deciles != null && i < c.Deciles.Length ? c.Deciles[i] : double.NaN;
                    ImGui.TextUnformatted("P >= " + decile.ToString("0.0", CultureInfo.InvariantCulture));
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextUnformatted((c.Area[i] / 10000.0).ToString("#,0.0", CultureInfo.InvariantCulture));
                    ImGui.TableSetColumnIndex(2);
                    //"has not moved" and "has nothing to move from" mean different things for the streak
                    double? d = c.Delta != null && i < c.Delta.Length ? c.Delta[i] : null;
                    ImGui.TextUnformatted(d.HasValue ? (d.Value * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " %" : "-");
                }
                ImGui.EndTable();
            }
        }

        private static void DrawFinished(Snapshot s)
        {
            ImGui.SeparatorText($"Finished ({s.Finished.Count}), newest first");
            if (s.Finished.Count == 0)
            {
                ImGui.TextDisabled("None yet.");
                return;
            }

            ImGuiTableFlags flags = ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp
                                    | ImGuiTableFlags.ScrollY;
            float height = Mathf.Min(260f, ImGui.GetTextLineHeightWithSpacing() * (s.Finished.Count + 1.5f) + 8f);
            if (ImGui.BeginTable("finished", 6, flags, new Vector2(0, height)))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Realization", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("Fire (acres)", ImGuiTableColumnFlags.WidthFixed, 90f);
                ImGui.TableSetupColumn("ELMFIRE (min)", ImGuiTableColumnFlags.WidthFixed, 95f);
                ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch, 4f);
                ImGui.TableSetupColumn("Logs", ImGuiTableColumnFlags.WidthFixed, 150f);
                ImGui.TableHeadersRow();

                foreach (FinishedRealization r in s.Finished)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(r.Id);
                    ImGui.TableSetColumnIndex(1);
                    Vector4 colour = r.Status == CampaignLayout.StatusOk ? Fields.Good
                        : r.Status == CampaignLayout.StatusFailed ? Fields.Alert
                        : new Vector4(0.7f, 0.7f, 0.7f, 1f);
                    ImGui.TextColored(colour, r.Status + (r.Reused ? " (reused)" : string.Empty));
                    ImGui.TableSetColumnIndex(2);
                    ImGui.TextUnformatted(double.IsNaN(r.FireAreaAcres) ? "-" : r.FireAreaAcres.ToString("#,0", CultureInfo.InvariantCulture));
                    ImGui.TableSetColumnIndex(3);
                    ImGui.TextUnformatted(double.IsNaN(r.ElmfireMinutes) ? "-" : r.ElmfireMinutes.ToString("0.0", CultureInfo.InvariantCulture));
                    ImGui.TableSetColumnIndex(4);
                    ImGui.TextUnformatted(string.IsNullOrEmpty(r.Message) ? string.Empty : r.Message);
                    if (!string.IsNullOrEmpty(r.Message) && ImGui.IsItemHovered())
                    {
                        ImGui.BeginTooltip();
                        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 40f);
                        ImGui.TextUnformatted(r.Message);
                        ImGui.PopTextWrapPos();
                        ImGui.EndTooltip();
                    }
                    ImGui.TableSetColumnIndex(5);
                    OpenButton("fire##" + r.Id, s.Folder, r.Id, "elmfire.log");
                    ImGui.SameLine();
                    OpenButton("evac##" + r.Id, s.Folder, r.Id, CampaignLayout.RealizationPreactLog);
                    ImGui.SameLine();
                    OpenButton("dir##" + r.Id, s.Folder, r.Id, null);
                }
                ImGui.EndTable();
            }
        }

        /// <summary>A small button that opens a realization's file (or its folder, for a null name).</summary>
        private static void OpenButton(string label, string campaignFolder, string id, string fileName)
        {
            if (ImGui.SmallButton(label))
            {
                string dir = Path.Combine(campaignFolder ?? string.Empty, CampaignLayout.RealizationsFolder, id ?? string.Empty);
                string path = fileName == null ? dir : Path.Combine(dir, fileName);
                if (fileName == null ? Directory.Exists(path) : File.Exists(path))
                {
                    _note = string.Empty;
                    MainMenuBar.OpenInFileManager(path);
                }
                else
                {
                    _note = $"Realization {id} has no {fileName ?? "folder"} (yet).";
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(fileName == null ? "Opens the realization's folder." : "Opens the realization's " + fileName + ".");
            }
        }

        private static void DrawLog(Snapshot s)
        {
            ImGui.SeparatorText(s.LogIsOwn ? "Log (the campaign's output)" : "Log (" + CampaignLayout.CampaignLogFile + ")");
            ImGui.Checkbox("Follow", ref _followLog);
            Fields.Hint("Everything the campaign printed: per realization its ignition, weather, fire area and outcome.",
                        "The same lines are in campaign.log in the campaign folder. ELMFIRE's and PREACT's own output is in",
                        "each realization's elmfire.log and preact.log (the buttons above).");

            // Third argument is ImGuiChildFlags; 1 is a bordered child in every ImGui.NET version (see the Trigger
            // campaign window).
            ImGui.BeginChild("campaign_monitor_log", new Vector2(0, 0), (ImGuiChildFlags)1, ImGuiWindowFlags.HorizontalScrollbar);
            //Only the lines in view are laid out (every line is one line high): 2000 of them each frame would cost more
            //than the rest of the window.
            List<string> log = s.Log;
            float line = ImGui.GetTextLineHeightWithSpacing();
            int first = Mathf.Clamp((int)(ImGui.GetScrollY() / line) - 1, 0, log.Count);
            int last = Mathf.Clamp(first + (int)(ImGui.GetWindowHeight() / line) + 3, first, log.Count);
            if (_followLog)
            {
                last = log.Count;
                first = Mathf.Max(0, last - (int)(ImGui.GetWindowHeight() / line) - 3);
            }
            if (first > 0) ImGui.Dummy(new Vector2(1f, first * line));
            for (int i = first; i < last; ++i)
            {
                ImGui.TextUnformatted(log[i]);
            }
            if (last < log.Count) ImGui.Dummy(new Vector2(1f, (log.Count - last) * line));
            if (_followLog)
            {
                ImGui.SetScrollHereY(1.0f);
            }
            ImGui.EndChild();
        }
    }
}
