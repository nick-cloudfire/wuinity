using ImGuiNET;
using System;
using PREACT.Input;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    internal class HazardsInputTab
    {
        static string[] WildfireModulesStrings;
        static int wildfireModuleIndex = 0;

        static string[] SmokeModulesStrings;
        static int smokeModuleIndex = 0;

        static string[] TriggerBufferModulesStrings;
        static int triggerBufferModuleIndex = 0;

        static HazardsInputTab()
        {
            WildfireModulesStrings = Enum.GetNames(typeof(WildfireModuleInput.WildfireModules));
            SmokeModulesStrings = Enum.GetNames(typeof(SmokeInput.SmokeModules));
            TriggerBufferModulesStrings = Enum.GetNames(typeof(TriggerBufferModuleInput.TriggerBufferModules));
        }


        public static void Draw(PREACTInput input)
        {
            ImGui.SeparatorText("Wildfire spread");

            //Whether a module runs is its Enabled flag, and until now that could only be set when the
            //scenario was created - the combo below chooses which module, which is a different question.
            //So a finished scenario could not be run without its fire, or with a hazard added, without
            //editing the .wui by hand.
            if (ImGui.Checkbox("Simulate wildfire spread", ref input.WildfireModule.Enabled))
            {
                if (!input.WildfireModule.Enabled)
                {
                    //Both of these read the fire: smoke is produced by it, and k-PERIL back-propagates from
                    //its arrival times and asks the wildfire module for the grid to work on. Left enabled
                    //with no fire, the trigger boundary run dereferences a module that was never created.
                    //Turned off here rather than merely warned about, since neither can do anything.
                    if (input.SmokeModule.Enabled || input.TriggerBufferModule.Enabled)
                    {
                        PREACT.Engine.Message(null, PREACT.Engine.LogType.Log,
                            "Smoke and trigger boundaries need the fire, so they were switched off with it. "
                            + "Their settings are kept and come back when the fire does.");
                    }
                    input.SmokeModule.Enabled = false;
                    input.TriggerBufferModule.Enabled = false;
                }
            }

            if (input.WildfireModule.Enabled && input.WildfireModule.Module == WildfireModuleInput.WildfireModules.None)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                    "Pick a module below, or the run aborts on \"could not initiate wildfire module\".");
            }

            ImGui.BeginDisabled(!input.WildfireModule.Enabled);

            //No settings window exists for either wildfire module, so this says so instead of
            //offering a button with an empty body - which is what was here, and was
            //indistinguishable from the button being broken.
            if (input.WildfireModule.Module != WildfireModuleInput.WildfireModules.None)
            {
                ImGui.BeginDisabled();
                ImGui.Button("Module settings###1");
                ImGui.EndDisabled();
                ImGui.SameLine();
                ImGui.TextDisabled("(edit in the .wui file)");
                ImGui.SameLine();
            }


            wildfireModuleIndex = (int)input.WildfireModule.Module;
            ImGui.Combo(nameof(input.WildfireModule), ref wildfireModuleIndex, WildfireModulesStrings, WildfireModulesStrings.Length);
            input.WildfireModule.Module = (WildfireModuleInput.WildfireModules)wildfireModuleIndex;
            ImGui.EndDisabled();

            ImGui.SeparatorText("Wildfire smoke");

            //Greyed rather than hidden when there is no fire, so it stays visible that the scenario has
            //smoke settings and what is stopping them.
            ImGui.BeginDisabled(!input.WildfireModule.Enabled);
            ImGui.Checkbox("Simulate smoke", ref input.SmokeModule.Enabled);
            if (input.SmokeModule.Module != SmokeInput.SmokeModules.None)
            {
                if (ImGui.Button("Module settings###2"))
                {
                    if (input.SmokeModule.Module == SmokeInput.SmokeModules.GlobalSmoke) { GlobalSmokeInputEditorWindow.Open(input.SmokeModule.GlobalSmokeInput); }
                }
                ImGui.SameLine();
            }
            smokeModuleIndex = (int)input.SmokeModule.Module;
            ImGui.Combo(nameof(input.SmokeModule), ref smokeModuleIndex, SmokeModulesStrings, SmokeModulesStrings.Length);
            input.SmokeModule.Module = (SmokeInput.SmokeModules)smokeModuleIndex;
            ImGui.EndDisabled();
            if (!input.WildfireModule.Enabled)
            {
                ImGui.TextDisabled("Needs the wildfire module: smoke is produced by the fire.");
            }

            //Its own section here rather than only in the new scenario window, which is where it used to be
            //set and therefore the only place it could be turned on.
            ImGui.SeparatorText("Trigger boundary");
            ImGui.BeginDisabled(!input.WildfireModule.Enabled);
            ImGui.Checkbox("Compute a trigger boundary", ref input.TriggerBufferModule.Enabled);
            triggerBufferModuleIndex = (int)input.TriggerBufferModule.Module;
            ImGui.Combo(nameof(input.TriggerBufferModule), ref triggerBufferModuleIndex, TriggerBufferModulesStrings, TriggerBufferModulesStrings.Length);
            input.TriggerBufferModule.Module = (TriggerBufferModuleInput.TriggerBufferModules)triggerBufferModuleIndex;
            ImGui.EndDisabled();
            if (!input.WildfireModule.Enabled)
            {
                ImGui.TextDisabled("Needs the wildfire module: the boundary is back-propagated from the fire's");
                ImGui.TextDisabled("arrival times, on the fire's own grid.");
            }
        }
    }
}
