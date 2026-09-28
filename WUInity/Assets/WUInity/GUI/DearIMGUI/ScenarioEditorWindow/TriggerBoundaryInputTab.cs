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
                    Fields.Warn($"The fire stops after {hours:F1} h (Fire > Fire model settings, Timing and reuse). No boundary is",
                                "computed for an area the fire never reaches, so a short run can produce none.",
                                $"A trigger campaign sets its own fire duration ({PREACT.Utility.CampaignLayout.DefaultFireHours:0} h by default).");
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
                Fields.Hint("Left empty, the painted WUI area is used instead (Fire > Fire areas, step 6).");
            }

            ImGui.SeparatorText("Wind");

            //What EvacuationManager.ResolveTriggerWind does: the fire's own wind whenever it has one.
            Fields.Hint("An ELMFIRE fire brings its own wind, and k-PERIL takes it: the midflame wind speed ELMFIRE",
                        "writes (mfws, ft/min, converted to mi/h) and the wind direction the fire ran on. The two",
                        "files below are then not used, and the console says so.");

            Fields.Path("WindSpeedFile", () => peril.WindSpeedFile, v => peril.WindSpeedFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Path("WindDirectionFile", () => peril.WindDirectionFile, v => peril.WindDirectionFile = v,
                filter: FileBrowser.geoTiffFilter);
            Fields.Hint("Only for a fire with no wind of its own - an imported fire without [AscImport]",
                        "MidflameWindSpeedFile. Speed in MILES PER HOUR, read AS midflame wind: k-PERIL's",
                        "length-to-breadth correlation is defined for it, and a 10 m wind makes every ellipse too",
                        "long. Direction in degrees. Both on the fire grid.");

            Fields.Real("WindBandSeconds", ref peril.WindBandSeconds,
                "Seconds each band of those two rasters covers (3600 for hourly). An ELMFIRE fire's own rasters come "
                + "with their own band length.");
            if (peril.WindBandSeconds <= 0.0) peril.WindBandSeconds = kPERILInput.DefaultWindBandSeconds;

            ImGui.SeparatorText("What is not set here");
            Fields.Hint("Wind band: each cell takes the band covering the time the fire actually reached it.",
                        "Rate of spread: from the fire module's own output - for ELMFIRE its vs and spread_dir.",
                        "Required egress time: measured from the run, as the last-arrival evacuation time.");
        }
    }
}
