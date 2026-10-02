using System;
using System.Collections.Generic;
using System.IO;
using ImGuiNET;
using PREACT.Utility;
using UnityEngine;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Help &gt; External tools and keys: every program and key the platform depends on, which one is in use and
    /// where it came from, a field to name each program's path, and what is lost without it.
    /// </summary>
    /// <remarks>
    /// Replaces the welcome window that opened on every start and listed SUMO and PROJ only - not ELMFIRE,
    /// GDAL, WindNinja, or the OpenTopography and Mapbox keys, which are the ones that actually fail. The
    /// answers come from the cached probe (<see cref="ToolsService"/>), so opening this costs nothing.
    ///
    /// The paths are the user's tool settings (<see cref="ToolPaths"/>: one <c>tools.ini</c> per user), which
    /// the engine, PREACT.exe and PREACTcli all read - so what is saved here is also what a campaign uses. Save
    /// writes the file, re-applies what can change while running (PROJ; ELMFIRE, the GDAL tools and WindNinja
    /// are looked up at each use anyway) and looks again, so the "In use" lines show the result straight away.
    /// SUMO is read when WUInity starts and says so.
    /// </remarks>
    public static class ExternalToolsWindow
    {
        private static bool _isOpen;

        //What is being edited, and what the file held when it was loaded or saved.
        private static ToolPaths.Settings _edit;
        private static ToolPaths.Settings _saved;
        private static int _loadedVersion = -1;
        private static string _saveMessage = string.Empty;
        private static bool _saveFailed;

        //Validation of what is typed, per tool, recomputed only when the text changes (it touches the file system).
        private sealed class Checked
        {
            public string Value;
            public string Problem;
            public string Normalised;
        }
        private static readonly Dictionary<ToolPaths.Tool, Checked> _checked = new Dictionary<ToolPaths.Tool, Checked>();

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
            if (_edit == null || !Dirty)
            {
                Reload();
            }
        }

        private static bool Dirty => _edit != null && _saved != null && !_edit.SameAs(_saved);

        private static void Reload()
        {
            _saved = ToolPaths.Load();
            _edit = _saved.Clone();
            _loadedVersion = ToolPaths.Version;
            _checked.Clear();
        }

        private static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(720f, 640f));
            if (ImGui.Begin("External tools and keys###ExternalTools", ref _isOpen, PreactGUI.ToolWindowFlags))
            {
                DrawContents();
            }
            ImGui.End();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void DrawContents()
        {
            ExternalToolsSnapshot t = ToolsService.Current;

            //Changed on disk (a hand edit, another WUInity) while nothing is being edited here: show the file as it is.
            if (_edit == null || (!Dirty && ToolPaths.Version != _loadedVersion))
            {
                Reload();
            }

            if (ImGui.Button(ToolsService.Probing ? "Looking...###ToolsLook" : "Look again###ToolsLook"))
            {
                _checked.Clear();
                ToolsService.Refresh();
            }
            ImGui.SameLine();
            ImGui.TextDisabled(t.Probed ? $"last looked at {t.ProbedAt:HH:mm:ss}" : "not looked yet");
            if (!string.IsNullOrEmpty(t.ProbeError))
            {
                Fields.Warn("A probe failed: " + t.ProbeError);
            }
            if (!string.IsNullOrEmpty(t.GdalLibraryProblem))
            {
                ImGui.PushTextWrapPos(0f);
                Fields.Caution(t.GdalLibraryProblem);
                ImGui.PopTextWrapPos();
            }

            ImGui.PushTextWrapPos(0f);
            ImGui.TextWrapped("Leave a path empty to have the program found automatically. A path set here is used instead of "
                + "the search by WUInity, PREACT.exe and PREACTcli - so by trigger campaigns too. A scenario's own [ELMFIRE] "
                + "ElmfireExe, PathToGdal or WindNinjaExe still comes first.");
            ImGui.PopTextWrapPos();
            DrawSettingsFile(t);

            ImGui.SeparatorText("Fire");
            ToolRow(ToolPaths.Tool.Elmfire, "ELMFIRE", false, t.Elmfire, "ElmfireExe",
                "an elmfire folder beside this program, then the build under ThirdParty/elmfire",
                "No fire can be computed: the fire case cannot run, nor can a campaign.");
            ToolRow(ToolPaths.Tool.Gdal, "GDAL command-line tools", true, t.Gdal, "PathToGdal",
                "PATH, then a QGIS or OSGeo4W install, then SUMO_HOME\\bin",
                "ELMFIRE shells out to gdal_translate and gdalinfo, and without them fails its own DEM check - reporting a "
                + "problem with the DEM rather than with GDAL.");
            ToolRow(ToolPaths.Tool.WindNinja, "WindNinja", false, t.WindNinja, "WindNinjaExe",
                "WINDNINJA_CLI, PATH, then the installer's locations",
                "The case gets one wind value for the whole domain, so a trigger boundary comes out circular instead of wind-driven.");

            ImGui.SeparatorText("Traffic and projections");
            ToolRow(ToolPaths.Tool.Sumo, "SUMO", true, t.Sumo, null,
                "SUMO_HOME/bin, then the PATH folder holding sumo, then a PATH folder named like SUMO's bin",
                "The road network cannot be built (netconvert) and the traffic simulation cannot start. On Windows the "
                + "engine also loads GDAL's library (gdal.dll) from SUMO's bin.");
            ToolRow(ToolPaths.Tool.Proj, "PROJ data (proj.db)", true, t.Proj, null,
                "PROJ_DATA, then PROJ_LIB" + (Application.platform == RuntimePlatform.WindowsPlayer
                                              || Application.platform == RuntimePlatform.WindowsEditor ? "" : ", then /usr/share/proj"),
                "Coordinate transforms may fail; GDAL needs PROJ's database to reproject anything.");
            if (!string.IsNullOrEmpty(t.ProjLib) || !string.IsNullOrEmpty(t.ProjData))
            {
                ImGui.TextDisabled("PROJ_DATA = " + (string.IsNullOrEmpty(t.ProjData) ? "(not set)" : t.ProjData)
                                   + ", PROJ_LIB = " + (string.IsNullOrEmpty(t.ProjLib) ? "(not set)" : t.ProjLib));
            }

            ImGui.Separator();
            DrawSaveRow();

            ImGui.SeparatorText("Keys");
            DrawOpenTopographyKey();

            if (t.MapboxTokenValid == true)
            {
                Fields.Ok("Mapbox token: valid.");
            }
            else if (t.MapboxTokenValid == false)
            {
                Fields.Warn("Mapbox token: not valid. The map tiles cannot be loaded.");
                Fields.Hint(Application.isEditor
                    ? "Set it the way the Mapbox SDK expects: Assets/Resources/Mapbox/MapboxConfiguration.txt."
                    : "A standalone build has the token it was built with (Assets/Resources/Mapbox/MapboxConfiguration.txt "
                      + "when build-player.ps1 ran); build it again with a valid one.");
            }
            else
            {
                ImGui.TextDisabled("Mapbox token: not checked yet.");
            }
        }

        private static void DrawSettingsFile(ExternalToolsSnapshot t)
        {
            string file = string.IsNullOrEmpty(t.SettingsFile) ? ToolPaths.SettingsFile : t.SettingsFile;
            ImGui.TextDisabled("Saved in " + file);
            string folder = null;
            try
            {
                folder = Path.GetDirectoryName(Path.GetFullPath(file));
            }
            catch
            {
                folder = null;
            }

            ImGui.SameLine();
            bool exists = !string.IsNullOrEmpty(folder) && Directory.Exists(folder);
            ImGui.BeginDisabled(!exists);
            if (ImGui.SmallButton("Open folder###ToolsFolder"))
            {
                MainMenuBar.OpenInFileManager(folder);
            }
            ImGui.EndDisabled();
            Fields.Hint("One file per user, outside every scenario: a scenario is meant to be shared, and where a",
                        "program is installed is a property of this machine. It can be edited by hand; " + ToolPaths.FileVariable,
                        "names another file. Save creates it.");

            if (!string.IsNullOrEmpty(t.SettingsReadError))
            {
                Fields.Warn(t.SettingsReadError);
            }
        }

        /// <summary>
        /// One tool: the path field with a picker, whether what is typed holds the program, and which path is in use
        /// and where it came from.
        /// </summary>
        /// <param name="scenarioKey">The [ELMFIRE] key a scenario names it with, or null when a scenario cannot.</param>
        private static void ToolRow(ToolPaths.Tool tool, string label, bool folder, ToolStatus status, string scenarioKey,
            string where, string missing)
        {
            ImGui.PushID("tool" + (int)tool);

            ImGui.TextUnformatted(label);
            Fields.Hint("Takes " + ToolPaths.Describe(tool) + ".");

            string value = _edit.Get(tool);
            if (ImGui.Button("Browse..."))
            {
                Browse(tool, folder, value);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear"))
            {
                _edit.Set(tool, string.Empty);
                value = string.Empty;
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            if (ImGui.InputText("##path", ref value, 1024))
            {
                _edit.Set(tool, value);
            }

            //What is typed.
            if (string.IsNullOrWhiteSpace(value))
            {
                ImGui.TextDisabled("  Empty: found automatically.");
            }
            else
            {
                Checked c = Check(tool, value);
                if (c.Problem != null)
                {
                    Fields.Warn("  " + c.Problem);
                }
                else
                {
                    Fields.Ok("  Holds " + ToolPaths.ExpectedFile(tool)
                              + (ToolPaths.SamePath(c.Normalised, ToolPaths.Clean(value)) ? "." : ": " + c.Normalised));
                }
            }

            //What is in use, and why that one.
            DrawInUse(tool, status, scenarioKey, where, missing);

            ImGui.PopID();
            ImGui.Spacing();
        }

        private static void DrawInUse(ToolPaths.Tool tool, ToolStatus status, string scenarioKey, string where, string missing)
        {
            if (!ToolsService.Current.Probed)
            {
                ImGui.TextDisabled(ToolsService.Probing ? "  In use: looking..." : "  In use: not looked yet.");
                return;
            }

            if (status.Found)
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextUnformatted("  In use: " + status.InUse);
                ImGui.PopTextWrapPos();
                ImGui.TextDisabled("    from " + SourceText(tool, status, scenarioKey, where));
            }
            else
            {
                Fields.Warn("  In use: none - not found.");
                Fields.Hint("Looked for in " + where + ".", missing);
            }

            ImGui.PushTextWrapPos(0f);
            if (!string.IsNullOrEmpty(status.ExplicitProblem))
            {
                Fields.Warn("  " + status.ExplicitProblem);
            }
            if (!string.IsNullOrEmpty(status.SettingProblem))
            {
                Fields.Warn("  The saved path is not used: " + status.SettingProblem);
            }
            if (status.NeedsRestart)
            {
                Fields.Caution("  Saved: " + (string.IsNullOrEmpty(status.AfterRestart) ? "nothing that would be found" : status.AfterRestart)
                               + ". Applies after restarting WUInity.");
            }
            ImGui.PopTextWrapPos();

            if (tool == ToolPaths.Tool.Sumo)
            {
                Fields.Hint("SUMO is decided when WUInity starts: its bin goes on the library path before anything",
                            "is loaded (on Windows GDAL's gdal.dll comes from it, and SUMO's own library is loaded",
                            "by the first traffic run). netconvert, the traffic simulation and campaigns started",
                            "after a restart use the saved one.");
            }
        }

        private static string SourceText(ToolPaths.Tool tool, ToolStatus status, string scenarioKey, string where)
        {
            switch (status.Source)
            {
                case ToolPaths.Source.Explicit:
                    return "the open scenario's [ELMFIRE] " + scenarioKey + ", which comes before your setting";
                case ToolPaths.Source.UserSetting:
                    return "your setting";
                case ToolPaths.Source.Automatic:
                    if (tool == ToolPaths.Tool.Proj && !string.IsNullOrEmpty(status.Detail))
                    {
                        return "the automatic search (" + status.Detail + ")";
                    }
                    return "the automatic search (" + where + ")";
                default:
                    return "nowhere";
            }
        }

        private static Checked Check(ToolPaths.Tool tool, string value)
        {
            if (!_checked.TryGetValue(tool, out Checked c) || c.Value != value)
            {
                c = new Checked { Value = value };
                c.Problem = ToolPaths.Validate(tool, value, out c.Normalised);
                _checked[tool] = c;
            }
            return c;
        }

        private static void Browse(ToolPaths.Tool tool, bool folder, string current)
        {
            string initial = null;
            try
            {
                string cleaned = ToolPaths.Clean(current);
                if (cleaned.Length > 0 && Path.IsPathRooted(cleaned))
                {
                    initial = Directory.Exists(cleaned) ? cleaned : Path.GetDirectoryName(cleaned);
                    if (!Directory.Exists(initial)) initial = null;
                }
            }
            catch
            {
                initial = null;
            }

            bool windows = Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;
            if (!folder && windows)
            {
                SimpleFileBrowser.FileBrowser.SetFilters(true, ".exe");
            }
            else
            {
                SimpleFileBrowser.FileBrowser.SetFilters(true);
            }

            SimpleFileBrowser.FileBrowser.ShowLoadDialog(
                paths =>
                {
                    if (paths != null && paths.Length > 0 && !string.IsNullOrEmpty(paths[0]))
                    {
                        string picked = paths[0];
                        try
                        {
                            picked = Path.GetFullPath(picked);
                        }
                        catch
                        {
                            //kept as the browser gave it; validation says what is wrong with it
                        }
                        _edit?.Set(tool, picked);
                    }
                },
                () => { },
                folder ? SimpleFileBrowser.FileBrowser.PickMode.Folders : SimpleFileBrowser.FileBrowser.PickMode.Files,
                false, initial, null, "Select " + ToolPaths.Describe(tool), "Select");
        }

        private static void DrawSaveRow()
        {
            bool dirty = Dirty;
            var invalid = new List<string>();
            foreach (ToolPaths.Tool tool in ToolPaths.AllTools)
            {
                string value = _edit.Get(tool);
                if (!string.IsNullOrWhiteSpace(value) && Check(tool, value).Problem != null)
                {
                    invalid.Add(tool.ToString());
                }
            }

            ImGui.BeginDisabled(!dirty || invalid.Count > 0);
            if (ImGui.Button("Save###ToolsSave"))
            {
                Save();
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!dirty);
            if (ImGui.Button("Revert###ToolsRevert"))
            {
                Reload();
                _saveMessage = string.Empty;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();

            if (invalid.Count > 0)
            {
                Fields.Warn("Correct or clear the paths marked above to save (" + string.Join(", ", invalid) + ").");
            }
            else if (dirty)
            {
                ImGui.TextDisabled("Not saved yet: the In use lines show the saved settings.");
            }
            else if (!string.IsNullOrEmpty(_saveMessage))
            {
                if (_saveFailed)
                {
                    Fields.Warn(_saveMessage);
                }
                else
                {
                    Fields.Ok(_saveMessage);
                }
            }
            else
            {
                ImGui.TextDisabled("Saved settings shown.");
            }
        }

        private static void Save()
        {
            string file = ToolPaths.SettingsFile;
            try
            {
                ToolPaths.Save(_edit.Clone());
            }
            catch (Exception e)
            {
                _saveFailed = true;
                _saveMessage = "Could not save " + file + ": " + e.Message;
                return;
            }

            _saved = _edit.Clone();
            _loadedVersion = ToolPaths.Version;
            _saveFailed = false;
            _saveMessage = $"Saved at {DateTime.Now:HH:mm:ss}.";
            PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "External tool paths saved to " + file + ".");

            //Applies a changed PROJ to this process's GDAL, and looks again so every In use line shows the result.
            _checked.Clear();
            ToolsService.Refresh();
        }

        /// <summary>
        /// Where the OpenTopography key comes from, and somewhere to put one only when nothing supplies it.
        /// </summary>
        /// <remarks>
        /// The configuration file is the way this is meant to be set - the same mechanism as the Mapbox token,
        /// it survives restarts, and it is gitignored - so when it is doing its job no field is drawn. A box
        /// that is always there invites pasting a credential into somewhere it will be lost.
        /// </remarks>
        public static void DrawOpenTopographyKey()
        {
            string source = ScenarioDataSteps.OpenTopographyApiKeySource;

            if (!string.IsNullOrEmpty(source) && source != "typed in for this session only")
            {
                Fields.Ok("OpenTopography key: from " + source + ".");
                return;
            }

            if (string.IsNullOrEmpty(source))
            {
                Fields.Warn("No OpenTopography API key. The fire case's terrain (and Download DEM) come from OpenTopography.");
            }
            ImGui.TextWrapped(Application.isEditor
                ? "Set it the way the Mapbox token is set: copy "
                  + "Assets/Resources/OpenTopography/OpenTopographyConfigurationTemplate.txt to "
                  + "OpenTopographyConfiguration.txt beside it and paste a key in, or set OPENTOPOGRAPHY_API_KEY. That file is "
                  + "gitignored and outlives the session. A key is free from portal.opentopography.org."
                : "Set the OPENTOPOGRAPHY_API_KEY environment variable and start WUInity again; this build was made without a "
                  + "key in Assets/Resources/OpenTopography/OpenTopographyConfiguration.txt. A key is free from "
                  + "portal.opentopography.org.");

            ImGui.SetNextItemWidth(260);
            if (ImGui.InputText("Key for this session only###OpenTopoKey", ref ScenarioDataSteps.OpenTopographyApiKeyOverride, 128,
                ImGuiInputTextFlags.Password))
            {
                ToolsService.KeyChanged();
            }
            Fields.Hint("Used by the fire case build, a DEM download and a campaign started from here, until the",
                        "application closes. Nothing writes it to the scenario or to disk.");
        }
    }
}
