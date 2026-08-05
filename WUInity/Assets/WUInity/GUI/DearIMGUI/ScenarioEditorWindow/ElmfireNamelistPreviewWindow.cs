using ImGuiNET;
using System;
using System.Collections.Generic;
using UnityEngine;
using PREACT.Input;
using PREACT.Utility;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// Shows the <c>elmfire.data</c> the current settings would produce.
    /// </summary>
    /// <remarks>
    /// Worth a window of its own because these settings are only meaningful as the file they become: ninety
    /// controls spread over nine collapsing headers give no sense of whether the result is a namelist
    /// ELMFIRE will accept, and the alternative way of finding out is to run a build and open the file.
    ///
    /// It is a preview of the <em>settings</em>, not of a particular case. The parts the case answers -
    /// which optional layers exist, the ignition coordinates in the case CRS - are stated as assumptions at
    /// the top rather than guessed at, because guessing them would produce a file that differs from the real
    /// one in exactly the places a reader would not think to check.
    /// </remarks>
    internal static class ElmfireNamelistPreviewWindow
    {
        private static bool _isOpen;
        private static string _text = string.Empty;

        public static void Open(ElmfireInput elmfire)
        {
            _text = Render(elmfire);

            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
                _isOpen = true;
            }
        }

        private static string Render(ElmfireInput elmfire)
        {
            var facts = new ElmfireNamelistBuilder.CaseFacts
            {
                Name = PreactGUI.Engine?.Simulation?.Input?.Simulation?.Name ?? "case",
                SimulationTstopSeconds = elmfire.TstopSeconds(),

                //Stated as assumptions below rather than discovered: the case may not have been built yet.
                FuelStem = "fbfm40",
                AvailableMeteorologyBands = 0,
                HasBuildingFuelModelFile = true,
            };

            foreach (string stem in new[] { "cc", "ch", "cbh", "cbd", "ignition_mask" })
            {
                facts.AvailableStems.Add(stem);
            }

            foreach ((string key, string stem) in new[]
                     {
                         ("BLDG_AREA_FILENAME", "baa"),
                         ("BLDG_SEPARATION_DIST_FILENAME", "ssd"),
                         ("BLDG_NONBURNABLE_FRAC_FILENAME", "nbf_h"),
                         ("BLDG_FOOTPRINT_FRAC_FILENAME", "ff_h"),
                         ("BLDG_FUEL_MODEL_FILENAME", "bfm_h"),
                     })
            {
                facts.BuildingLayers.Add((key, stem));
            }

            try
            {
                return string.Join("\n", ElmfireNamelistBuilder.Build(elmfire.Namelist, facts));
            }
            catch (Exception e)
            {
                return "Could not build the namelist from these settings: " + e.Message;
            }
        }

        private static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.SetNextWindowSize(new Vector2(720f, 620f), ImGuiCond.FirstUseEver);
            ImGui.Begin("ELMFIRE namelist preview", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            ImGui.TextWrapped("What these settings produce. The case supplies the rest at build time: which "
                + "optional layers it actually has, how many weather bands its rasters hold, and the ignition "
                + "points in its own CRS. This preview assumes a complete case and no placed ignitions.");
            ImGui.Separator();

            if (ImGui.Button("Copy to clipboard"))
            {
                ImGui.SetClipboardText(_text);
            }
            ImGui.SameLine();
            ImGui.TextDisabled("Paste it beside the case as a namelist template to pin these values.");

            ImGui.BeginChild("PreviewText", new Vector2(0f, -ImGui.GetFrameHeightWithSpacing()),
                (ImGuiChildFlags)0, ImGuiWindowFlags.HorizontalScrollbar);
            //Unwrapped, unlike the console: a namelist is a column of aligned key/value pairs and wrapping
            //it would break the alignment that makes it readable. Hence the horizontal scrollbar above.
            ImGui.TextUnformatted(_text);
            ImGui.EndChild();

            if (ImGui.Button("Close"))
            {
                _isOpen = false;
            }

            ImGui.End();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }
    }
}
