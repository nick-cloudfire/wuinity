using ImGuiNET;
using UnityEngine;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Help &gt; External tools and keys: every program and key the platform depends on, what was found, and
    /// what is lost without it.
    /// </summary>
    /// <remarks>
    /// Replaces the welcome window that opened on every start and listed SUMO and PROJ only - not ELMFIRE,
    /// GDAL, WindNinja, or the OpenTopography and Mapbox keys, which are the ones that actually fail. The
    /// answers come from the cached probe (<see cref="ToolsService"/>), so opening this costs nothing.
    /// </remarks>
    public static class ExternalToolsWindow
    {
        private static bool _isOpen;

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
        }

        private static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            PreactGUI.PlaceNextWindow(new Vector2(640f, 460f));
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

            if (ImGui.Button(ToolsService.Probing ? "Looking...###ToolsLook" : "Look again###ToolsLook"))
            {
                ToolsService.Refresh();
            }
            ImGui.SameLine();
            ImGui.TextDisabled(t.Probed ? $"last looked at {t.ProbedAt:HH:mm:ss}" : "not looked yet");
            if (!string.IsNullOrEmpty(t.ProbeError))
            {
                Fields.Warn("A probe failed: " + t.ProbeError);
            }

            ImGui.SeparatorText("Fire");
            Tool("ELMFIRE", t.ElmfireExe, t.ElmfireOverride, "the vendored build under ThirdParty/elmfire, or [ELMFIRE] ElmfireExe",
                "No fire can be computed: the fire case cannot run, nor can a campaign.");
            Tool("GDAL command-line tools", t.GdalBin, t.GdalOverride, "PATH, then a QGIS or OSGeo4W install, or [ELMFIRE] PathToGdal",
                "ELMFIRE shells out to gdal_translate and gdalinfo, and without them fails its own DEM check - reporting a "
                + "problem with the DEM rather than with GDAL.");
            Tool("WindNinja", t.WindNinjaExe, t.WindNinjaOverride, "WINDNINJA_CLI, PATH, then the installer's locations",
                "The case gets one wind value for the whole domain, so a trigger boundary comes out circular instead of wind-driven.");

            ImGui.SeparatorText("Traffic and projections");
            Tool("SUMO (bin folder)", t.SumoBin, null, "the machine PATH entry containing 'Sumo' and 'bin'",
                "The road network cannot be built (netconvert) and the traffic simulation cannot start.");
            Tool("PROJ_LIB", t.ProjLib, null, "the PROJ_LIB machine environment variable",
                "Coordinate transforms may fail; GDAL needs PROJ's database to reproject anything.");
            Tool("PROJ_DATA", t.ProjData, null, "the PROJ_DATA machine environment variable", "As PROJ_LIB, for newer PROJ versions.");

            ImGui.SeparatorText("Keys");
            DrawOpenTopographyKey();

            if (t.MapboxTokenValid == true)
            {
                Fields.Ok("Mapbox token: valid.");
            }
            else if (t.MapboxTokenValid == false)
            {
                Fields.Warn("Mapbox token: not valid. The map tiles cannot be loaded.");
                Fields.Hint("Set it the way the Mapbox SDK expects: Assets/Resources/Mapbox/MapboxConfiguration.txt.");
            }
            else
            {
                ImGui.TextDisabled("Mapbox token: not checked yet.");
            }
        }

        private static void Tool(string label, string found, string overridden, string where, string missing)
        {
            if (string.IsNullOrEmpty(found))
            {
                Fields.Warn(label + ": not found.");
                Fields.Hint("Looked for in " + where + ".", missing);
                return;
            }

            ImGui.TextUnformatted(label + ": " + found);
            Fields.Hint(string.IsNullOrWhiteSpace(overridden)
                ? "Found automatically (" + where + ")."
                : "Named by the open scenario.");
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
            ImGui.TextWrapped("Set it the way the Mapbox token is set: copy "
                + "Assets/Resources/OpenTopography/OpenTopographyConfigurationTemplate.txt to "
                + "OpenTopographyConfiguration.txt beside it and paste a key in, or set OPENTOPOGRAPHY_API_KEY. That file is "
                + "gitignored and outlives the session. A key is free from portal.opentopography.org.");

            ImGui.SetNextItemWidth(260);
            if (ImGui.InputText("Key for this session only###OpenTopoKey", ref ScenarioDataSteps.OpenTopographyApiKeyOverride, 128,
                ImGuiInputTextFlags.Password))
            {
                ToolsService.KeyChanged();
            }
        }
    }
}
