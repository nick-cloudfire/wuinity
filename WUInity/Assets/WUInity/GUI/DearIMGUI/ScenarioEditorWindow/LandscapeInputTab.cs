using ImGuiNET;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// The scenario's terrain: what the map is drawn on, what areas are painted against, and the slope and
    /// aspect a trigger boundary is corrected by.
    /// </summary>
    /// <remarks>
    /// This used to offer the fuel model and the four canopy bands as well, and no longer does — **nothing
    /// reads them.** They existed for the in-process spread models (BEHAVE, Rothermel), which have been
    /// removed; the bands were still parsed into <c>LandscapeCellData.fuel_model</c> and
    /// <c>canopy_cover</c> and then never queried by anything. Once ELMFIRE gained its own source layers, the
    /// two sets sat in different tabs looking like alternatives, so setting either was a coin flip and one of
    /// the two did nothing at all.
    ///
    /// The fields remain on <see cref="PREACT.Input.LandscapeInput"/>: a LANDFIRE <c>.lcp</c> genuinely
    /// carries those bands, the band order depends on them, and an existing <c>.wui</c> that names them still
    /// loads. What is gone is the invitation to set them here as though it changed the fire.
    /// </remarks>
    public static class LandscapeInputTab
    {
        public static void Draw(PREACT.Input.LandscapeInput input)
        {
            ImGui.TextWrapped("Elevation is the one that matters: it is what the domain is drawn on, what painted "
                + "areas are measured against, and where slope and aspect come from when they are not given "
                + "separately. Everything here is optional.");

            ImGui.SeparatorText("All bands in one file");
            Fields.Path("LandscapeFile", () => input.LandscapeFile, v => input.LandscapeFile = v,
                filter: FileBrowser.lcpFilter);
            Fields.Hint("A multiband GeoTIFF in LANDFIRE band order, or a FARSITE .lcp. When this is set the",
                        "individual bands below are ignored, since it already defines all of them.");

            bool haveCombined = !string.IsNullOrEmpty(input.LandscapeFile);
            ImGui.BeginDisabled(haveCombined);

            ImGui.SeparatorText("Terrain");
            Fields.Path("ElevationFile", () => input.ElevationFile, v => input.ElevationFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("SlopeFile", () => input.SlopeFile, v => input.SlopeFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("AspectFile", () => input.AspectFile, v => input.AspectFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Hint("Slope in degrees, aspect in degrees clockwise from north. Both are computed from the",
                        "elevation when not given; Prepare data writes them out. k-PERIL corrects its spread",
                        "ellipse with the slope, so a scenario with terrain gets a better trigger boundary.");

            ImGui.EndDisabled();

            if (haveCombined)
            {
                Fields.Hint("Clear LandscapeFile to set the bands individually.");
            }

            //Said here because this is where someone will come looking for them, having previously set them
            //here. Naming the tab and the section is the whole point - "somewhere else" would not help.
            ImGui.SeparatorText("Fuel and canopy");
            ImGui.TextWrapped("Set under Hazards > Fire > Source layers, not here. They are inputs to the fire "
                + "model rather than properties of the terrain, and ELMFIRE warps them onto its own grid when "
                + "the case is built.");
        }
    }
}
