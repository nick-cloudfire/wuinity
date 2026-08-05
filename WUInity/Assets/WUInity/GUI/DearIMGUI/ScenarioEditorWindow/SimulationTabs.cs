using ImGuiNET;
using PREACT.Input;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// What the scenario is: where and when it happens, what it is drawn on, and the weather over it.
    /// </summary>
    /// <remarks>
    /// The landscape sits here rather than under Hazards because it is not the fire module's property — it is
    /// what the map is drawn on, what painted areas are measured against, and the slope a trigger boundary is
    /// corrected by.
    /// </remarks>
    internal static class SimulationTabs
    {
        public static void Draw(PREACTInput input)
        {
            if (!ImGui.BeginTabBar("SimulationBar"))
            {
                return;
            }

            if (ImGui.BeginTabItem("General"))
            {
                SimulationInputTab.Draw(input.Simulation);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Map"))
            {
                MapInputTab.Draw(input.Map);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Terrain"))
            {
                LandscapeInputTab.Draw(input.Landscape);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Weather"))
            {
                WeatherInputTab.Draw(input.Weather);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }
    }
}
