using System.Collections.Generic;
using ImGuiNET;
using PREACT.Input;
using IgnitionPointInput = PREACT.Wildfire.IgnitionPointInput;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// Where the fire starts and which ground matters: ignition points, the paintable areas, and the WUI area
    /// a trigger boundary protects.
    /// </summary>
    /// <remarks>
    /// Not gated on the wildfire module being enabled, unlike smoke and the trigger boundary. The WUI area is
    /// what a boundary protects and the ignition area is what an ELMFIRE ensemble draws from, so both are worth
    /// painting for a scenario whose fire is imported, or not switched on yet.
    /// </remarks>
    internal static class FireAreasInputTab
    {
        public static void Draw(PREACTInput input)
        {
            ImGui.SeparatorText("Painted areas");

            if (ImGui.Button("Paint fire areas"))
            {
                Editors.FirePaintWindow.Open();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("The WUI area the trigger boundary protects, the area a fire may start in, "
                    + "and an initial ignition - painted on the fire grid and saved beside the scenario.");
            }

            if (string.IsNullOrEmpty(input.WildfireModule.GraphicalFireInputFile))
            {
                Fields.Hint("Nothing painted yet.");
            }
            else
            {
                ImGui.Text("Painted areas: " + input.WildfireModule.GraphicalFireInputFile);
            }

            ImGui.SeparatorText("Ignition points");

            if (ImGui.Button("New ignition point"))
            {
                Editors.IgnitionPointEditWindow.Open(input.WildfireModule.Data.IgnitionPoints, -1);
            }

            List<IgnitionPointInput> ignitions = input.WildfireModule.Data.IgnitionPoints;

            if (ignitions.Count == 0)
            {
                Fields.Hint("None. ELMFIRE then draws its own from the ignition mask, if the case has one.");
            }

            int removeIgnition = -1;
            for (int i = 0; i < ignitions.Count; ++i)
            {
                IgnitionPointInput point = ignitions[i];

                //Its position as the label, since that is the only thing distinguishing one from another - an
                //ignition point has no name.
                if (ImGui.TreeNode($"Ignition {i + 1}: {point.LatLon.x:F5}, {point.LatLon.y:F5}"))
                {
                    if (ImGui.Button($"Edit###ign{i}")) { Editors.IgnitionPointEditWindow.Open(ignitions, i); }
                    ImGui.SameLine();
                    if (ImGui.Button($"Remove###ignrm{i}")) { removeIgnition = i; }

                    ImGui.Text(point.AbsoluteTime
                        ? $"Starts {point.IgnitionDateTime:yyyy-MM-dd HH:mm} ({point.IgnitionTime:F0} s in)"
                        : $"Starts {point.IgnitionTime:F0} s into the simulation");

                    ImGui.TreePop();
                }
            }

            if (removeIgnition >= 0)
            {
                ignitions.RemoveAt(removeIgnition);
                //Or its marker stays on the map, marking an ignition the scenario no longer has.
                PreactGUI.WUInity.RefreshWildfireIgnitionMarkers();
            }
        }
    }
}
