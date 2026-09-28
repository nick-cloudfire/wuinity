//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.IO;
using System.Collections.Generic;
using PREACT.Input;
using PREACT.Pedestrian;

namespace PREACT.Output
{
    public class SimulationOutput
    {
        private Simulation _simulation;


        Dictionary<int, float[,]> _triggerBuffers = new Dictionary<int, float[,]>();


        public SimulationOutput(Simulation simulation)
        {

            _simulation = simulation;
        }

        public void SaveOutput()
        {
            if (_simulation.Input.TrafficModule.Enabled)
            {
                Engine.Message(_simulation, Engine.LogType.Log, " Total cars in simulation: " + _simulation.Evacuation.TrafficModule.GetTotalCarsSimulated());
                _simulation.Evacuation.TrafficModule.SaveToFile(_simulation.SimulationIndex);
                SaveArrivalData();
            }
            if (_simulation.Input.PedestrianModule.Enabled)
            {
                if (_simulation.Input.PedestrianModule.Module == PedestrianModuleInput.PedestrianModules.MacroHouseholdSim)
                {
                    MacroHouseholdSim mHS = (MacroHouseholdSim)_simulation.Evacuation.PedestrianModule;
                    string file = Path.Combine(_simulation.Engine.OutputFolder, _simulation.Input.Simulation.Name + "_pedestrian_output_" + _simulation.SimulationIndex + ".csv");
                    mHS.SaveToFile(file);
                }
            }

        }

        private void SaveArrivalData()
        {
            string outputFilePath = Path.Combine(_simulation.Engine.OutputFolder, _simulation.Input.Simulation.Name + "_" + _simulation.SimulationIndex + "_arrivalData.csv");
            using (StreamWriter outputFile = new StreamWriter(outputFilePath))
            {
                List<double> data = _simulation.Evacuation.TrafficModule.GetArrivalData();
                foreach (double value in data)
                {
                    outputFile.WriteLine(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        List<double> _emptyArrivalData = new List<double>();
        public List<double> GetTrafficArrivalData()
        {
            if (_simulation.Evacuation.TrafficModule != null)
            {
                return _simulation.Evacuation.TrafficModule.GetArrivalData();
            }
            else
            {
                return _emptyArrivalData;
            }
        }

        public void AddTriggerBufferOutput(float[,] triggerBufferOutput, int simulationIndex)
        {
            _triggerBuffers.Add(simulationIndex, triggerBufferOutput);
        }

        /// <summary>
        /// Path includes filename
        /// </summary>
        /// <param name="log"></param>
        /// <param name="path"></param>
        public static void SaveLogToDisk(List<string> log, string path)
        {            
            File.WriteAllLines(path, log);
        }
    }

}

