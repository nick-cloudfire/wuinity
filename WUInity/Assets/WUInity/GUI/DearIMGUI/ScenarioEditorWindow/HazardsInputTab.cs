using ImGuiNET;
using PREACT.Input;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// The hazard side of a scenario, as a bar of sub-tabs matching Simulation and Evacuation.
    /// </summary>
    /// <remarks>
    /// This was one flat page carrying the fire module and its settings, ELMFIRE's twelve source layers, the
    /// hundred-key namelist panel, smoke, painting, ignition points and the trigger boundary — everything in
    /// one scroll, while the two neighbouring tabs both had sub-tabs. Finding a setting meant knowing roughly
    /// how far down it was.
    ///
    /// The split is by question rather than by module: what computes the fire, what to tell it, what it makes,
    /// where it starts, and what the answer protects. ELMFIRE's behaviour settings get a tab of their own
    /// because there are a hundred of them in nine groups — appending that to the module's own settings is
    /// what made the page unnavigable.
    /// </remarks>
    internal static class HazardsInputTab
    {
        public static void Draw(PREACTInput input)
        {
            if (!ImGui.BeginTabBar("HazardsBar"))
            {
                return;
            }

            if (ImGui.BeginTabItem("Fire"))
            {
                FireInputTab.Draw(input);
                ImGui.EndTabItem();
            }

            //Only meaningful for ELMFIRE, and shown regardless so that the settings a scenario carries stay
            //visible - a scenario switched to an imported fire still has them, and switching back should not
            //feel like they were lost.
            if (ImGui.BeginTabItem("Fire behaviour"))
            {
                DrawBehaviour(input);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Ignitions and areas"))
            {
                FireAreasInputTab.Draw(input);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Smoke"))
            {
                SmokeInputTab.Draw(input);
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Trigger boundary"))
            {
                TriggerBoundaryInputTab.Draw(input);
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
            return;
        }

        /// <summary>The generated <c>elmfire.data</c>: what the fire is told, as against how it is run.</summary>
        private static void DrawBehaviour(PREACTInput input)
        {
            bool isElmfire = input.WildfireModule.Module == WildfireModuleInput.WildfireModules.ELMFIRE;

            if (!isElmfire)
            {
                ImGui.TextWrapped("These settings become the generated elmfire.data, so they only apply when the "
                    + "fire module is ELMFIRE. They are kept either way.");
                ImGui.Separator();
            }

            ElmfireInput elmfire = input.WildfireModule.ElmfireInput;

            //A named template replaces the whole generated namelist, so every control below stops mattering.
            //Disabling them says that far more clearly than a line of text under a hundred live widgets.
            bool overridden = !string.IsNullOrEmpty(elmfire.NamelistTemplate);
            if (overridden)
            {
                Fields.Warn("NamelistTemplate is set to " + elmfire.NamelistTemplate + ",",
                            "so the file is used as it stands and nothing here reaches ELMFIRE.");
                Fields.Hint("Clear it under Fire > Case and executables to generate a namelist instead.");
                ImGui.Separator();
            }

            ImGui.BeginDisabled(!isElmfire || overridden);
            ElmfireNamelistPanel.Draw(elmfire);
            ImGui.EndDisabled();
        }
    }
}
