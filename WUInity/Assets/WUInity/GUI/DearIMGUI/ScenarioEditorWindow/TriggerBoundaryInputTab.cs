using System;
using ImGuiNET;
using PREACT.Input;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// The trigger boundary: how far out the fire must be when an area is told to leave.
    /// </summary>
    /// <remarks>
    /// k-PERIL's inputs existed only in the new-scenario creator, so a scenario that gained a trigger boundary
    /// afterwards — the normal way round, since the wind rasters come out of the ELMFIRE case build — had no
    /// way to be given the files it then reported as missing.
    /// </remarks>
    internal static class TriggerBoundaryInputTab
    {
        private static readonly string[] Modules =
            Enum.GetNames(typeof(TriggerBufferModuleInput.TriggerBufferModules));

        private static readonly string[] WuiAreaSources =
            Enum.GetNames(typeof(kPERILInput.WuiAreaSources));

        public static void Draw(PREACTInput input)
        {
            ImGui.BeginDisabled(!input.WildfireModule.Enabled);

            Fields.Check("Compute a trigger boundary", ref input.TriggerBufferModule.Enabled);
            Fields.Choice("Module", ref input.TriggerBufferModule.Module, Modules);

            if (input.TriggerBufferModule.Module == TriggerBufferModuleInput.TriggerBufferModules.kPERIL)
            {
                //A boundary is only produced for an area the fire actually reaches, so the fire's stop time and
                //the trigger boundary are not independent settings. Said where it can be acted on, because
                //otherwise the failure arrives at the end of a long run as "the fire never reached" - true, and
                //easy to read as a finding about the scenario rather than about the stop time.
                if (input.WildfireModule.Enabled
                    && input.WildfireModule.Module == WildfireModuleInput.WildfireModules.ELMFIRE
                    && input.WildfireModule.ElmfireInput.SimulationTstopHours < 24.0)
                {
                    double hours = input.WildfireModule.ElmfireInput.SimulationTstopHours;
                    Fields.Warn($"The fire stops after {hours:F1} h (Fire > Timing and reuse). No boundary is",
                                "computed for an area the fire never reaches, so a short run can produce none.");
                }

                ImGui.Separator();
                DrawKPeril(input.TriggerBufferModule.kPERILInput);
            }

            ImGui.EndDisabled();

            if (!input.WildfireModule.Enabled)
            {
                Fields.Hint("Needs the wildfire module: the boundary is back-propagated from the fire's",
                            "arrival times, on the fire's own grid.");
            }
        }

        private static void DrawKPeril(kPERILInput peril)
        {
            ImGui.SeparatorText("What is protected");

            Fields.Choice("WuiAreaSource", ref peril.WuiAreaSource, WuiAreaSources);
            Fields.Hint("Raster reads the mask below. The group options rasterise the evacuation groups' own",
                        "polygons instead, which keeps the area protected and the area evacuated as one",
                        "definition. Separate gives each group its own boundary, for groups that leave on",
                        "different orders or to different destinations.");

            if (peril.WuiAreaSource == kPERILInput.WuiAreaSources.Raster)
            {
                Fields.Path("WuiAreaFile", () => peril.WuiAreaFile, v => peril.WuiAreaFile = v);
                Fields.Hint("Left empty, the painted WUI area is used instead (Ignitions and areas).");
            }

            ImGui.SeparatorText("Wind");

            //Not marked required: running ELMFIRE fills these in from the case's own ws/wd, which is the
            //arrangement that cannot disagree with the fire.
            Fields.Path("WindSpeedFile", () => peril.WindSpeedFile, v => peril.WindSpeedFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("WindDirectionFile", () => peril.WindDirectionFile, v => peril.WindDirectionFile = v,
                filter: FileBrowser.geoTiffFilter);

            if (string.IsNullOrEmpty(peril.WindSpeedFile) || string.IsNullOrEmpty(peril.WindDirectionFile))
            {
                Fields.Hint("Left empty, the ELMFIRE case's own ws.tif / wd.tif are used - the same wind the",
                            "fire was computed with. Set them only to override that.");
            }

            Fields.Hint("Speed in MILES PER HOUR, direction in degrees, on the fire grid. The unit matters:",
                        "the speed goes into Anderson's length-to-breadth correlation, defined for mi/h.");

            ImGui.SeparatorText("What is not set here");
            Fields.Hint("Wind band: the rasters hold one band per hour, and each cell takes the band covering",
                        "the hour the fire actually reached it. The console reports the distribution.",
                        "Rate of spread: from the fire module's own output - for ELMFIRE its vs and spread_dir.",
                        "Required egress time: measured from the run, as the last-arrival evacuation time.");
        }
    }
}
