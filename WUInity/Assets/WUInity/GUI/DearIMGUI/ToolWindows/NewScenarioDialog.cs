using System;
using ImGuiNET;
using PREACT;
using PREACT.Input;
using PREACT.Math;
using UnityEngine;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// File &gt; New scenario: a folder, a name, an area on the map, a time window and the modules - then the
    /// .wui is written and opened, and the workflow panel takes it from there.
    /// </summary>
    /// <remarks>
    /// Replaces the scenario creator, which ran data steps of its own against an input that had no folder yet
    /// (so every file it picked was stored relative to the wrong place), defaulted the fire to an imported one
    /// rather than ELMFIRE, wrote nothing until the very end, and - because picking the area closed it and
    /// reopened it from scratch - could lose what had been typed. The values live here now, not in the window,
    /// so hiding the dialog for the pick keeps them; Escape during the pick brings it back unchanged.
    /// </remarks>
    public static class NewScenarioDialog
    {
        private static bool _isOpen;
        private static bool _registered;
        private static bool _hiddenForPick;

        private static NewScenarioSettings _s = new NewScenarioSettings();
        private static string _problem;

        private static readonly WildfireModuleInput.WildfireModules[] FireModules =
        {
            WildfireModuleInput.WildfireModules.ELMFIRE,
            WildfireModuleInput.WildfireModules.AscImport,
        };
        private static readonly string[] FireModuleNames =
        {
            "ELMFIRE (computed here: terrain, fuels and weather are prepared for it)",
            "Imported rasters (arrival times computed elsewhere)",
        };

        public static bool IsOpen { get => _isOpen; }

        /// <summary>Shows the dialog, starting from a fresh set of values unless <paramref name="keepValues"/>.</summary>
        public static void Open(bool keepValues = false)
        {
            if (!_registered)
            {
                PreactGUI.DrawWindow(Draw);
                _registered = true;
            }

            if (!keepValues || !_isOpen)
            {
                string folder = _s.Folder;
                _s = new NewScenarioSettings();
                //The last folder used is the likeliest next one; otherwise, beside the open scenario's folder.
                _s.Folder = !string.IsNullOrEmpty(folder) ? folder : SuggestedFolder();
                _problem = null;
            }

            _isOpen = true;
            _hiddenForPick = false;
            //The world map, since the area is picked on it; navigable while the dialog is up.
            PreactGUI.WUInity.ShowWebMercatorMap();
        }

        public static void Close()
        {
            if (!_isOpen) return;
            _isOpen = false;
            _hiddenForPick = false;
            //Back to whatever was open before, untouched.
            PreactGUI.WUInity.RestoreScenarioMap();
        }

        private static string SuggestedFolder()
        {
            string root = ScenarioSession.RootFolder;
            if (string.IsNullOrEmpty(root)) return string.Empty;
            try
            {
                return System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(root).TrimEnd('/', '\\')) ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static void Draw()
        {
            if (!_isOpen || _hiddenForPick)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(560f, 620f));
            bool open = true;
            if (ImGui.Begin("New scenario###NewScenario", ref open, PreactGUI.ToolWindowFlags))
            {
                DrawContents();
            }
            ImGui.End();

            if (!open)
            {
                Close();
            }
        }

        private static void DrawContents()
        {
            ImGui.TextWrapped("A scenario is a folder with a .wui file in it. This writes the file straight away; roads, "
                + "population, fuels and the fire case are prepared afterwards, from the workflow panel.");

            // ---------------------------------------------------------------- where
            ImGui.SeparatorText("Where");

            ImGui.SetNextItemWidth(-90f);
            ImGui.InputText("###NewFolder", ref _s.Folder, 512);
            ImGui.SameLine();
            if (ImGui.Button("Choose...###NewFolderPick"))
            {
                FileBrowser.OpenSetFolderPath(path => _s.Folder = path, "Folder for the new scenario", null);
            }
            Fields.Hint("The folder the scenario goes in.");

            ImGui.SetNextItemWidth(240f);
            ImGui.InputText("Name###NewName", ref _s.Name, 128);
            Fields.Hint("Every prepared file is named after it, so it cannot contain spaces. Rename early, if at all.");

            ImGui.Checkbox("Make a folder for it, named after it###NewSubfolder", ref _s.CreateSubfolder);

            if (!string.IsNullOrWhiteSpace(_s.Folder) && !string.IsNullOrWhiteSpace(_s.Name))
            {
                ImGui.PushTextWrapPos(0f);
                ImGui.TextDisabled("Will write " + NewScenarioFactory.ScenarioPath(_s).Replace('\\', '/'));
                ImGui.PopTextWrapPos();
            }

            // ---------------------------------------------------------------- area
            ImGui.SeparatorText("Area of interest");

            if (ImGui.Button("Pick on map###NewPick"))
            {
                PickArea();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Hides this dialog while you click two opposite corners on the world map. "
                    + "Escape brings it back as it was.");
            }
            ImGui.SameLine();
            bool haveArea = !(_s.LowerLeftLatLon.x == 0.0 && _s.LowerLeftLatLon.y == 0.0)
                && _s.UpperRightLatLon.x > _s.LowerLeftLatLon.x && _s.UpperRightLatLon.y > _s.LowerLeftLatLon.y;
            if (haveArea)
            {
                Vector2d size = NewScenarioFactory.MeasureDomain(_s.LowerLeftLatLon, _s.UpperRightLatLon);
                ImGui.TextUnformatted($"{size.x / 1000.0:F1} x {size.y / 1000.0:F1} km");
                if (size.x > 50000.0 || size.y > 50000.0)
                {
                    Fields.Warn("Larger than 50 km across: every download and the fire case get slow, and the traffic model may not keep up.");
                }
            }
            else
            {
                ImGui.TextDisabled("not picked yet");
            }

            if (ImGui.TreeNode("Type the corners in###NewCorners"))
            {
                CustomTypes.InputDouble2("South-west (lat, lon)###NewLL", ref _s.LowerLeftLatLon);
                CustomTypes.InputDouble2("North-east (lat, lon)###NewUR", ref _s.UpperRightLatLon);
                ImGui.TreePop();
            }

            if (haveArea)
            {
                bool us = ScenarioFiles.IsInLandfireCoverage(_s.LowerLeftLatLon, _s.UpperRightLatLon);
                ImGui.TextDisabled(us
                    ? "Inside LANDFIRE's coverage: fuels and canopy can be downloaded for it."
                    : "Outside LANDFIRE's coverage (US only): fuels and canopy will have to come from your own rasters.");
            }

            // ---------------------------------------------------------------- when
            ImGui.SeparatorText("When");
            CustomTypes.InputDateTimePopup("Starts", ref _s.Start);
            CustomTypes.InputDateTimePopup("Ends", ref _s.End);
            Fields.Hint("The fire case's weather is fetched for this window, so give it the whole event.");

            // ---------------------------------------------------------------- what
            ImGui.SeparatorText("What it simulates");

            ImGui.Checkbox("Wildfire###NewFire", ref _s.Wildfire);
            if (_s.Wildfire)
            {
                ImGui.Indent();
                int module = Array.IndexOf(FireModules, _s.FireModule);
                if (module < 0) module = 0;
                ImGui.SetNextItemWidth(-1f);
                if (ImGui.Combo("###NewFireModule", ref module, FireModuleNames, FireModuleNames.Length))
                {
                    _s.FireModule = FireModules[module];
                }
                ImGui.Checkbox("Trigger boundary (k-PERIL)###NewTrigger", ref _s.TriggerBoundary);
                Fields.Hint("Works back from the fire's arrival to where an evacuation order has to be given.");
                ImGui.Checkbox("Smoke###NewSmoke", ref _s.Smoke);
                ImGui.Unindent();
            }

            ImGui.Checkbox("People on foot###NewPedestrian", ref _s.Pedestrian);
            ImGui.SameLine();
            ImGui.Checkbox("Vehicles (SUMO)###NewTraffic", ref _s.Traffic);
            if (_s.Pedestrian || _s.Traffic)
            {
                ImGui.Checkbox("Start with a standard response curve and demographics###NewStandard", ref _s.StandardEvacuationInputs);
                Fields.Hint("A starting point to replace with the town's own, not a finding.");
            }

            // ---------------------------------------------------------------- create
            ImGui.Separator();
            string problem = NewScenarioFactory.Validate(_s);
            ImGui.BeginDisabled(problem != null);
            if (ImGui.Button("Create scenario###NewCreate"))
            {
                Create();
            }
            ImGui.EndDisabled();
            if (problem != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(problem);
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel###NewCancel"))
            {
                Close();
                return;
            }

            if (problem != null)
            {
                Fields.Hint(problem);
            }
            if (!string.IsNullOrEmpty(_problem))
            {
                Fields.Warn(_problem);
            }
        }

        private static void PickArea()
        {
            _hiddenForPick = true;
            PreactGUI.WUInity.ShowWebMercatorMap();
            PreactGUI.WUInity.PickBoundingBoxOnMap(corners =>
            {
                //Whichever corners were clicked first.
                _s.LowerLeftLatLon = new Vector2d(Math.Min(corners[0].x, corners[1].x), Math.Min(corners[0].y, corners[1].y));
                _s.UpperRightLatLon = new Vector2d(Math.Max(corners[0].x, corners[1].x), Math.Max(corners[0].y, corners[1].y));
                _hiddenForPick = false;
            },
            () => _hiddenForPick = false);
        }

        private static void Create()
        {
            _problem = null;
            string problem = NewScenarioFactory.Validate(_s);
            if (problem != null)
            {
                _problem = problem;
                return;
            }

            PREACTInput input;
            try
            {
                input = NewScenarioFactory.Create(_s);
            }
            catch (Exception e)
            {
                _problem = "Could not make the scenario: " + e.Message;
                return;
            }

            string path = NewScenarioFactory.ScenarioPath(_s);
            if (!ScenarioSession.CreateAndOpen(input, path))
            {
                _problem = "Could not write or open " + path + "; see the console.";
                return;
            }

            Engine.Message(null, Engine.LogType.Log, "Created " + path + ". The workflow panel lists what to prepare next.");
            _isOpen = false;
            ScenarioWorkflowWindow.Focus(WorkflowStepId.None);
        }
    }
}
