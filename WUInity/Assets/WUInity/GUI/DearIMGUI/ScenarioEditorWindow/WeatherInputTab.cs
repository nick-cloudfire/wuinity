using ImGuiNET;
using PREACT.Input;

namespace Assets.WUInity.GUI.DearIMGUI.Input
{
    /// <summary>
    /// The weather record the run reads, where in it the run reads, and the fire-danger indices carried forward
    /// from it.
    /// </summary>
    /// <remarks>
    /// This showed the weather file and nothing else, so the six index seeds could only be set by editing the
    /// <c>.wui</c> by hand, and the anchor — the one thing that says which day a run's weather is actually from —
    /// was invisible.
    /// </remarks>
    public static class WeatherInputTab
    {
        public static void Draw(WeatherInput input)
        {
            ImGui.TextWrapped("An hourly weather record for the domain. Building an ELMFIRE case points this at "
                + "the case's own ERA5 archive and sets the anchor below, so the weather reported during a run "
                + "is the weather the fire was computed against.");

            ImGui.SeparatorText("Record");

            Fields.Path("WeatherFile", () => input.WeatherFile, v => input.WeatherFile = v);
            Fields.Hint("Hourly CSV in Open-Meteo's format. Downloaded automatically when the run needs a span",
                        "this does not cover.");

            ImGui.SeparatorText("Which day the run reads");

            if (input.HasWeatherAnchor)
            {
                Fields.Ok($"Anchored to {input.WeatherAnchorDateTime:yyyy-MM-dd HH:mm} in the record.");
                Fields.Hint("The simulation's start time reads from that moment, and time advances from there.",
                            "Set by the ELMFIRE case build to the historical peak fire-weather day it drew, so",
                            "the fire and the reported weather describe the same day.");

                if (ImGui.Button("Clear anchor"))
                {
                    //Offered because it is the only way back to reading the scenario's own dates, and because an
                    //anchor pointing into a record the scenario no longer has would otherwise be stuck.
                    input.WeatherAnchorDateTime = default;
                }
                ImGui.SameLine();
                Fields.Hint("(reverts to reading the weather at the scenario's own dates)");
            }
            else
            {
                Fields.Hint("Not anchored: weather is read at the scenario's own dates.",
                            "An ELMFIRE case build sets this. Until then, if the fire came from a case built",
                            "elsewhere, the weather reported here is not the weather it was computed with.");
            }

            ImGui.SeparatorText("Fire danger starting values");

            ImGui.TextWrapped("Seeds for the indices the run carries forward. Reported in the output window; "
                + "ELMFIRE computes spread from the case's own moisture rasters, not from these.");

            Fields.Real("StartFFMC", ref input.StartFFMC, "Fine fuel moisture code. Standard start is 85.");
            Fields.Real("StartDMC", ref input.StartDMC, "Duff moisture code. Standard start is 6.");
            Fields.Real("StartDC", ref input.StartDC, "Drought code. Standard start is 15.");
            Fields.Real("StartHourlyFFMC", ref input.StartHourlyFFMC, "Hourly FFMC. Standard start is 85.");
            Fields.Real("StartKBDI", ref input.StartKBDI, "Keetch-Byram drought index.");
            Fields.Real("MeanAnnualPrcp", ref input.MeanAnnualPrcp, "Mean annual precipitation in mm, for the KBDI.");

            //Said plainly because the seeds look more authoritative than they are: the drought codes in
            //particular are meant to accumulate over weeks, and nothing here does that.
            Fields.Hint("DMC and DC are meant to build up over weeks of antecedent weather. Nothing marches",
                        "them over the record before the run starts, so they begin at these values whatever",
                        "the preceding month was actually like.");
        }
    }
}
