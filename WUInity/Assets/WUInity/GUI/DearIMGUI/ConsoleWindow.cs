using ImGuiNET;
using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class ConsoleWindow
    {
        private static bool _open = true;

        /// <summary>
        /// How many of the most recent lines are drawn. ImGui lays out every visible line every
        /// frame and wrapped text cannot be clipped cheaply, so the whole session is kept in
        /// <see cref="PreactGUI.Messages"/> and only the tail is shown. "Save and open log" is
        /// what reaches the rest, which is why the toolbar says how many lines are not on screen.
        /// </summary>
        private const int MaxDrawnLines = 100;

        /// <summary>Result of the last button press, shown at the foot of the window. Buttons that
        /// write a file or fill the clipboard otherwise do their work with no visible effect at all.</summary>
        private static string _status;

        //What is shown: everything, or only the lines that say something went wrong. The first error of a
        //failed run is usually buried under hundreds of ordinary lines, so this is the quickest way to it.
        private enum Filter { All, WarningsAndErrors, Errors }
        private static Filter _filter = Filter.All;
        private static readonly string[] FilterNames = { "All messages", "Warnings and errors", "Errors only" };

        //Indices into the message list by severity, extended as lines arrive rather than rebuilt: scanning
        //the whole session every frame to filter it would cost more the longer the session ran.
        private static readonly List<int> _warningOrWorse = new List<int>();
        private static readonly List<int> _errors = new List<int>();
        private static int _indexed;

        public static bool IsOpen { get => _open; }

        public static void Open()
        {
            _open = true;
        }

        public static void Toggle()
        {
            _open = !_open;
        }

        private static bool IsError(string line)
        {
            return line.Contains("ERROR:") || line.Contains("EXCEPTION:") || line.Contains("FAILED");
        }

        private static bool IsWarning(string line)
        {
            return line.Contains("WARNING:");
        }

        private static void IndexNewLines(IReadOnlyList<string> messages)
        {
            //Cleared underneath us.
            if (messages.Count < _indexed)
            {
                _indexed = 0;
                _warningOrWorse.Clear();
                _errors.Clear();
            }

            for (; _indexed < messages.Count; ++_indexed)
            {
                string line = messages[_indexed] ?? string.Empty;
                bool error = IsError(line);
                if (error)
                {
                    _errors.Add(_indexed);
                }
                if (error || IsWarning(line))
                {
                    _warningOrWorse.Add(_indexed);
                }
            }
        }

        public static void Draw(IReadOnlyList<string> messages)
        {
            if (!_open)
            {
                return;
            }

            IndexNewLines(messages);

            ImGui.SetNextWindowSize(new Vector2(700f, 220f), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Console###Console", ref _open, ImGuiWindowFlags.NoCollapse))
            {
                ImGui.End();
                return;
            }

            if (ImGui.Button("Copy all"))
            {
                ImGui.SetClipboardText(Join(messages));
                _status = $"Copied {messages.Count} line(s) to the clipboard.";
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The whole session, oldest first - not just the lines on screen.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Save and open log"))
            {
                _status = SaveAndOpenLog(messages);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Writes the whole session to console.log in the output folder and opens it.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                PreactGUI.ClearMessages();
                _status = null;
            }

            ImGui.SameLine();
            int filterIndex = (int)_filter;
            ImGui.SetNextItemWidth(170f);
            if (ImGui.Combo("###ConsoleFilter", ref filterIndex, FilterNames, FilterNames.Length))
            {
                _filter = (Filter)filterIndex;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip($"{_errors.Count} error line(s), {_warningOrWorse.Count - _errors.Count} warning(s) this session.");
            }

            IReadOnlyList<int> subset = _filter == Filter.Errors ? _errors
                : _filter == Filter.WarningsAndErrors ? _warningOrWorse
                : null;
            int available = subset != null ? subset.Count : messages.Count;

            ImGui.SameLine();
            int hidden = available - MaxDrawnLines;
            ImGui.TextDisabled(hidden > 0
                ? $"right-click a line to copy it - {hidden} older line(s) not shown"
                : "right-click a line to copy it");

            ImGui.Separator();

            //The status line is drawn after the log, so the log's child region has to leave room for
            //it - otherwise it takes the full height and pushes the status out of the window.
            float footer = string.IsNullOrEmpty(_status) ? 0f : ImGui.GetTextLineHeightWithSpacing();
            ImGui.BeginChild("ConsoleLines", new Vector2(0f, -footer), (ImGuiChildFlags)0);

            //Newest first, as this console has always shown them.
            int oldest = Math.Max(0, available - MaxDrawnLines);
            for (int k = available - 1; k >= oldest; --k)
            {
                int i = subset != null ? subset[k] : k;
                string line = messages[i] ?? string.Empty;

                //Coloured by severity so the eye finds the problem without filtering. Wrapped rather than
                //ImGui.Text: an unwrapped line ran off the right edge with no way to read the end of it.
                bool error = IsError(line);
                bool warning = !error && IsWarning(line);
                if (error) ImGui.PushStyleColor(ImGuiCol.Text, Fields.Alert);
                else if (warning) ImGui.PushStyleColor(ImGuiCol.Text, Fields.Warning);
                ImGui.TextWrapped(line);
                if (error || warning) ImGui.PopStyleColor();

                if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    ImGui.SetClipboardText(line);
                    _status = "Copied that line to the clipboard.";
                }
            }

            ImGui.EndChild();

            if (!string.IsNullOrEmpty(_status))
            {
                ImGui.TextDisabled(_status);
            }

            ImGui.End();
        }

        private static string Join(IReadOnlyList<string> messages)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < messages.Count; ++i)
            {
                sb.AppendLine(messages[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Writes the session to a file and hands it to the OS.
        /// </summary>
        /// <remarks>
        /// Deliberately not a link to the log the engine already writes. That one
        /// (<c>_output/&lt;simulation&gt;.log</c>) is written once, after a run finishes, so it does not
        /// exist for anything that failed before then - which is exactly when the console is being read.
        /// It also holds only what went through <c>Engine.Message</c>, while several GUI-side messages
        /// reach the console directly. This writes what the user is actually looking at.
        /// </remarks>
        private static string SaveAndOpenLog(IReadOnlyList<string> messages)
        {
            string path = null;
            try
            {
                //OutputFolder creates the folder if it is missing, and falls back to the executable's
                //own directory when no scenario is loaded - so this works before any case is opened.
                string folder = PreactGUI.Engine != null
                    ? PreactGUI.Engine.OutputFolder
                    : Application.persistentDataPath;

                path = Path.Combine(folder, "console.log");
                File.WriteAllLines(path, messages);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = true
                });

                return "Wrote and opened " + path;
            }
            catch (Exception e)
            {
                //A missing file association is the likely failure, and the file is written by then, so
                //naming it still leaves the user somewhere to go.
                return path != null && File.Exists(path)
                    ? $"Wrote {path}, but could not open it: {e.Message}"
                    : "Could not write the log: " + e.Message;
            }
        }
    }
}
