using System;
using System.Collections.Generic;
using PREACT.Math;

namespace PREACT.Input
{
    public class WeatherInput
    {
        public string WeatherFile = string.Empty;
        public Vector2d DesiredLatLon = Vector2d.zero;

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

        public WeatherInput()
        {

        }

        public void Parse(string[] inputLines, int startIndex, string rootFolder, out bool success)
        {
            int issues = 0;
            Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
            string nameOfInput, userInput;

            //stream in if not exists
            nameOfInput = nameof(WeatherFile);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                WeatherFile = userInput;
                PREACTInput.CheckIfFileExist(nameOfInput, ref WeatherFile, rootFolder, out success);
            }
            else
            {
                success = false;
                PREACTInput.InputNotFoundMessage(nameOfInput);
            }
            if (success == false)
            {
                WeatherFile = string.Empty;
            }

            nameOfInput = nameof(DesiredLatLon);
            if (inputToParse.TryGetValue(nameOfInput, out userInput))
            {
                string[] data = userInput.Split(',');
                issues += double.TryParse(data[0], out DesiredLatLon.x) ? 0 : 1;
                issues += double.TryParse(data[1], out DesiredLatLon.y) ? 0 : 1;
                if (issues > 0)
                {
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }
            else
            {
                PREACTInput.InputNotFoundMessage(nameOfInput, false);
            }
            if (issues > 0)
            {
                success = false;
                return;
            }
            issues = 0;

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
                if (!DateTime.TryParse(userInput, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out WeatherAnchorDateTime))
                {
                    WeatherAnchorDateTime = default;
                    PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput);
                }
            }

            success = true;
        }

        private static void ReadDouble(Dictionary<string, string> input, string key, ref double field)
        {
            if (input.TryGetValue(key, out string userInput)
                && double.TryParse(userInput, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            {
                field = parsed;
            }
        }
    }
}
