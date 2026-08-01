using ImGuiNET;
using System;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// The landscape bands of a loaded scenario.
    ///
    /// These could only be set while creating a scenario, and only the elevation at that - so a scenario
    /// that turned out to need a fuel model band, which is what any module spreading its own fire needs,
    /// could only get one by editing the .wui by hand. The terrain has its own tab rather than sitting
    /// under Hazards because it is not the fire module's property: it is what the map is drawn on, what
    /// areas are painted against, and the slope a trigger boundary is corrected by.
    /// </summary>
    public static class LandscapeInputTab
    {
        private static readonly Vector4 WarningColour = new Vector4(0.9f, 0.7f, 0.2f, 1f);

        public static void Draw(PREACT.Input.LandscapeInput input)
        {
            ImGui.TextWrapped("Every band is optional, and what is present decides what can be done. Elevation on "
                + "its own is enough to paint on and to draw the map against - slope and aspect are derived from "
                + "it when they are absent. A fuel model band is what a module that spreads its own fire needs.");

            ImGui.SeparatorText("All bands in one file");
            Band(input, "LandscapeFile", () => input.LandscapeFile, v => input.LandscapeFile = v,
                FileBrowser.lcpFilter);
            ImGui.TextDisabled("A multiband GeoTIFF in LANDFIRE band order, or a FARSITE .lcp. When this is set");
            ImGui.TextDisabled("the individual bands below are ignored, since it already defines all of them.");

            bool haveCombined = !string.IsNullOrEmpty(input.LandscapeFile);
            ImGui.BeginDisabled(haveCombined);

            ImGui.SeparatorText("Terrain");
            Band(input, "ElevationFile", () => input.ElevationFile, v => input.ElevationFile = v,
                FileBrowser.geoTiffFilter);
            Band(input, "SlopeFile", () => input.SlopeFile, v => input.SlopeFile = v,
                FileBrowser.geoTiffFilter);
            Band(input, "AspectFile", () => input.AspectFile, v => input.AspectFile = v,
                FileBrowser.geoTiffFilter);
            ImGui.TextDisabled("Slope in degrees, aspect in degrees clockwise from north. Both are computed from");
            ImGui.TextDisabled("the elevation when not given; the Prepare data step writes them out.");

            ImGui.SeparatorText("Fuel");
            Band(input, "FuelModelFile", () => input.FuelModelFile, v => input.FuelModelFile = v,
                FileBrowser.geoTiffFilter);
            if (string.IsNullOrEmpty(input.FuelModelFile))
            {
                ImGui.TextColored(WarningColour,
                    "No fuel model band. A fire module that spreads fire itself cannot run without one;");
                ImGui.TextColored(WarningColour,
                    "an imported fire (AscImport) brings its own behaviour and does not need it.");
            }

            ImGui.SeparatorText("Canopy");
            Band(input, "CanopyCoverFile", () => input.CanopyCoverFile, v => input.CanopyCoverFile = v,
                FileBrowser.geoTiffFilter);
            Band(input, "CanopyHeightFile", () => input.CanopyHeightFile, v => input.CanopyHeightFile = v,
                FileBrowser.geoTiffFilter);
            Band(input, "CanopyBaseHeightFile", () => input.CanopyBaseHeightFile, v => input.CanopyBaseHeightFile = v,
                FileBrowser.geoTiffFilter);
            Band(input, "CanopyBulkDensityFile", () => input.CanopyBulkDensityFile, v => input.CanopyBulkDensityFile = v,
                FileBrowser.geoTiffFilter);

            //The same check the parser makes, said here where it can be acted on: two of the three crown
            //bands models no crown fire at all, which is easy to mistake for a result.
            int crownBands = (string.IsNullOrEmpty(input.CanopyHeightFile) ? 0 : 1)
                             + (string.IsNullOrEmpty(input.CanopyBaseHeightFile) ? 0 : 1)
                             + (string.IsNullOrEmpty(input.CanopyBulkDensityFile) ? 0 : 1);
            if (crownBands > 0 && crownBands < 3)
            {
                ImGui.TextColored(WarningColour,
                    $"Only {crownBands} of the three crown fuel bands are set. All three are needed together,");
                ImGui.TextColored(WarningColour, "so none of them will be used and no crown fire will be modelled.");
            }

            ImGui.EndDisabled();

            if (haveCombined)
            {
                ImGui.TextDisabled("Clear LandscapeFile to set the bands individually.");
            }
        }

        /// <summary>
        /// One band: a picker, and the path as text so it can be typed, pasted or cleared.
        ///
        /// The picker returns a path relative to the scenario folder, which is what keeps a scenario
        /// portable - normalised to forward slashes here, because the path is written into the .wui and a
        /// backslash is not a separator anywhere but Windows.
        /// </summary>
        private static void Band(PREACT.Input.LandscapeInput input, string label, Func<string> get, Action<string> set,
            string[] filter)
        {
            if (ImGui.Button("Set###set" + label))
            {
                FileBrowser.OpenSetFilePath(path => set(Normalise(path)), "Select " + label, true, filter);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear###clear" + label))
            {
                set(string.Empty);
            }
            ImGui.SameLine();

            string value = get() ?? string.Empty;
            if (ImGui.InputText(label, ref value, 256))
            {
                set(value);
            }
        }

        private static string Normalise(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }
    }
}
