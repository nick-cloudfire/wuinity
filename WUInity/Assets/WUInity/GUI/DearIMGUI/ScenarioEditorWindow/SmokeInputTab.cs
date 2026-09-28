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
                //Inline: GlobalSmoke has one setting, and it used to take a window of its own (with a
                //"Create global smoke file" button that was permanently disabled) to reach it.
                GlobalSmokeInput smoke = input.SmokeModule.GlobalSmokeInput;
                Fields.Path(nameof(smoke.ExtinctionFile), () => smoke.ExtinctionFile, v => smoke.ExtinctionFile = v);
                Fields.Hint("The extinction coefficient ramp GlobalSmoke reads. Nothing here writes one, so it",
                            "is a file you bring.");
            }

            ImGui.EndDisabled();

            if (!input.WildfireModule.Enabled)
            {
                Fields.Hint("Needs the wildfire module: smoke is produced by the fire.");
            }
        }
    }
}
