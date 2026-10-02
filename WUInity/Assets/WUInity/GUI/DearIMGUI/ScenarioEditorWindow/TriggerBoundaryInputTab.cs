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

        //The groups first: they are what is protected. A mask of one's own is the exception, and says so.
        private static readonly kPERILInput.WuiAreaSources[] Sources =
        {
            kPERILInput.WuiAreaSources.EvacuationGroupsCombined,
            kPERILInput.WuiAreaSources.EvacuationGroupsSeparate,
            kPERILInput.WuiAreaSources.Raster,
        };

        private static readonly string[] SourceLabels =
        {
            "The evacuation groups, together (one boundary)",
            "The evacuation groups, one boundary each",
            "A WUI mask of my own (WuiAreaFile)",
        };

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
            ImGui.SeparatorText("What is protected: the WUI area");

            int index = Array.IndexOf(Sources, peril.WuiAreaSource);
            if (index < 0) index = 0;
            //Raster without a file is the groups together (Mati's scenario says so), which "a mask of my own" hid.
            string[] labels = SourceLabels;
            if (peril.WuiAreaSource == kPERILInput.WuiAreaSources.Raster && string.IsNullOrWhiteSpace(peril.WuiAreaFile))
            {
                labels = (string[])SourceLabels.Clone();
                labels[2] = "A WUI mask of my own - none named, so the evacuation groups, together";
            }
            if (ImGui.Combo("WUI area###WuiAreaSource", ref index, labels, labels.Length))
            {
                peril.WuiAreaSource = Sources[index];
            }
            Fields.Hint("The WUI area is the evacuation groups' area (step 9) - painted, or from shapefiles - so",
                        "the area protected and the area evacuated are one definition. The case build writes",
                        "their union as wui_area.tif, which a campaign protects. One boundary each is for",
                        "groups that leave on different orders; a campaign needs one, and refuses it.");

            if (peril.WuiAreaSource == kPERILInput.WuiAreaSources.Raster)
            {
                Fields.Path("WuiAreaFile", () => peril.WuiAreaFile, v => peril.WuiAreaFile = v);
                Fields.Hint("A mask on the fire grid, 1 = protected. Left empty, the evacuation groups together.");
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
