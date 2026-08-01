using System;
using System.Collections.Generic;
using PREACT.Math;

namespace PREACT.Input
{
    public class WeatherInput
    {
        public string WeatherFile = string.Empty;
        public Vector2d DesiredLatLon = Vector2d.zero;

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
