//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.IO;

namespace PREACT
{
    public class EngineOutput
    {
        private Engine _engine;

        public EngineOutput(Engine engine)
        {
            _engine = engine;
        }

        public void SaveAverageCurve(double[] data)
        {
            string[] output = new string[data.Length + 2];
            output[0] = "Time [s],ArrivalIndex [-]";
            output[1] = "0.0, 0";
            for (int i = 0; i < data.Length; i++)
            {
                output[i + 2] = data[i].ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            string path = Path.Combine(_engine.OutputFolder, _engine.Simulation.Input.Simulation.Name + "_traffic_average.csv");
            File.WriteAllLines(path, output);
        }
    }
}