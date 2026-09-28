using System;
using System.Collections.Generic;
using PREACT.Math;

namespace PREACT.Input
{
    public class WeatherInput
    {
        /// <summary>
        /// The hourly weather CSV (the format the Open-Meteo downloader writes). Optional: the weather is
        /// reported during a run - temperature, humidity, wind, fire-danger indices - but neither the evacuation
        /// nor the trigger boundary uses it, so a scenario without one runs and simply reports no weather.
        /// When the file is set but does not cover the run, a year is downloaded from Open-Meteo at run time
        /// and cached beside the scenario; when it is not set, nothing is downloaded.
        /// </summary>
        public string WeatherFile = string.Empty;

        /// <summary>
        /// The moment in the weather record that the simulation's own start time reads from. Unset means the
        /// simulation's clock is looked up directly, which is the behaviour every scenario had before this key.
        /// </summary>
        /// <remarks>
        /// This exists because a scenario's calendar date and its weather are two different things once the
        /// fire comes from ELMFIRE. The case builder draws a historical peak fire-weather day out of the ERA5
        /// record — 2001-08-09, say — and computes the fire against that day's hours, while the scenario is
        /// dated whenever the evacuation is being modelled. So the fire had one weather and everything the
        /// platform reported about the weather had another: the temperature and humidity on screen were from a
        /// date the fire knew nothing about.
        ///
        /// Set by the case build to the same anchor the weather rasters were written from, so one number ties
        /// the two together. Saved in the <c>.wui</c> rather than recomputed, which also makes it the record of
        /// which day a case was built for.
        ///
        /// An <b>offset</b> rather than moving the simulation's own dates: response curves with absolute times,
        /// evacuation orders and timed ignitions are all stated on the scenario's calendar, and shifting that
        /// to the sampled day would move all of them.
        /// </remarks>
        public DateTime WeatherAnchorDateTime = default;

        /// <summary>Whether an anchor was given at all.</summary>
        public bool HasWeatherAnchor => WeatherAnchorDateTime != default;

        //Starting values for the fire weather indices the weather manager carries forward: the Canadian
        //FFMC/DMC/DC, its hourly FFMC, and the Keetch-Byram drought index. They live here because they are
        //weather, computed from the weather series whatever fire module is running - and because they used
        //to live on the cell-based spread model's settings, which meant they were only read when that module
        //was selected and silently defaulted for every other scenario.
        public double StartFFMC = 85.0;
        public double StartDMC = 6.0;
        public double StartDC = 15.0;
        public double StartHourlyFFMC = 85.0;
        public double StartKBDI = 100.0;
        public double MeanAnnualPrcp = 1000.0;

        internal const string NoWeatherFileConsequence = "No weather file, so no temperature, humidity, wind or "
            + "fire-danger indices are reported for the run. Nothing is downloaded in its place.";

        public WeatherInput()
        {

        }

        public void Parse(string[] inputLines, int startIndex, string rootFolder, out bool success)
        {
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            //Not critical either way: the weather is reported, not used by anything that decides the result.
            //A set-but-missing file used to be critical (blocking the run) while an unset one silently
            //downloaded a full year at run time; and a missing file's name was cleared, so a save dropped it.
            nameOfInput = nameof(WeatherFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && !string.IsNullOrWhiteSpace(userInput))
            {
                WeatherFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref WeatherFile, rootFolder, out bool exists, false);
                if (!exists)
                {
                    PREACTInput.InputWarning(nameOfInput, "is not there; a run will try to download the weather it needs from Open-Meteo.");
                }
            }
            else
            {
                PREACTInput.OptionalInputMissing(nameOfInput, NoWeatherFileConsequence);
            }

            //All optional and silent when absent: an index has a standard starting value, and asking every
            //scenario to state six of them would bury the keys that matter.
            ReadDouble(inputToParse, nameof(StartFFMC), ref StartFFMC);
            ReadDouble(inputToParse, nameof(StartDMC), ref StartDMC);
            ReadDouble(inputToParse, nameof(StartDC), ref StartDC);
            ReadDouble(inputToParse, nameof(StartHourlyFFMC), ref StartHourlyFFMC);
            ReadDouble(inputToParse, nameof(StartKBDI), ref StartKBDI);
            ReadDouble(inputToParse, nameof(MeanAnnualPrcp), ref MeanAnnualPrcp);

            //Optional, and unset means "no offset" - which is what every scenario written before this key did.
            nameOfInput = nameof(WeatherAnchorDateTime);
            if (inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
            {
                if (!InputParse.DateTime(userInput, out WeatherAnchorDateTime))
                {
                    WeatherAnchorDateTime = default;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "no anchor");
                }
            }

            //DesiredLatLon used to be read here and never used by anything; it is ignored now (and not written).
            success = true;
        }

        private static void ReadDouble(Dictionary<string, string> input, string key, ref double field)
        {
            if (!input.TryGetValue(key, out string userInput))
            {
                return;
            }

            if (InputParse.Double(userInput, out double parsed))
            {
                field = parsed;
            }
            else
            {
                PREACTInput.CouldNotInterpretInputMessage(key, userInput, false, InputParse.Format(field));
            }
        }
    }
}
