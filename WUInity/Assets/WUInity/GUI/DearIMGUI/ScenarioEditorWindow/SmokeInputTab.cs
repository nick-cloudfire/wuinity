using System;
using ImGuiNET;
using PREACT.Input;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>Smoke, which is produced by the fire and so cannot be had without one.</summary>
    internal static class SmokeInputTab
    {
        private static readonly string[] SmokeModules = Enum.GetNames(typeof(SmokeInput.SmokeModules));

        public static void Draw(PREACTInput input)
        {
            //Greyed rather than hidden when there is no fire, so it stays visible that the scenario has smoke
            //settings and what is stopping them.
            ImGui.BeginDisabled(!input.WildfireModule.Enabled);

            Fields.Check("Simulate smoke", ref input.SmokeModule.Enabled);
            Fields.Choice("Module", ref input.SmokeModule.Module, SmokeModules);

            if (input.SmokeModule.Module == SmokeInput.SmokeModules.GlobalSmoke)
            {
                if (ImGui.Button("Module settings"))
                {
                    GlobalSmokeInputEditorWindow.Open(input.SmokeModule.GlobalSmokeInput);
                }
            }

            ImGui.EndDisabled();

            if (!input.WildfireModule.Enabled)
            {
                Fields.Hint("Needs the wildfire module: smoke is produced by the fire.");
            }
        }
    }
}
