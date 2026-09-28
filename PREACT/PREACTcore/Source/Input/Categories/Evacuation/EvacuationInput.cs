//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using PREACT.Evacuation;

namespace PREACT.Input
{
    [System.Serializable]
    public class EvacuationInput
    {
        private EvacuationData _data;

        public EvacuationData Data { get => _data; }
        public Dictionary<string, EvacuationDestinationInput> EvacuationDestinationInputs = new Dictionary<string, EvacuationDestinationInput>(5);
        public Dictionary<string, ResponseCurve> ResponseCurves = new Dictionary<string, ResponseCurve>(5);        
        public Dictionary<string, EvacuationGroupInput> EvacuationGroupInputs = new Dictionary<string, EvacuationGroupInput>(5);

        /// <summary>
        /// No longer part of the format: never consumed by the engine. Kept only because the Unity scenario
        /// editor still binds to it; not read, not written. Remove together with the GUI checkbox.
        /// </summary>
        [NotInFile] public bool UseTriggerBufferEvacuation = false;
        /// <summary>See <see cref="UseTriggerBufferEvacuation"/>.</summary>
        [NotInFile] public string TriggerBufferFile = string.Empty;

        public EvacuationInput()
        {
            _data = new EvacuationData();
        }

        /// <summary>
        /// Reads the destinations, response curves and evacuation groups (each its own repeated section) and
        /// the <c>[Evacuation]</c> header, which holds nothing current any more.
        /// </summary>
        /// <remarks>
        /// Read in full whether or not any module uses them, so a save keeps them: this used to return at once
        /// when both the pedestrian and the traffic module were off, and the writer then dropped every
        /// destination, curve and group. What is critical depends on what is on: destinations for the traffic
        /// module, response curves and groups for the pedestrian module.
        /// </remarks>
        /// <param name="startIndex">Line of the <c>[Evacuation]</c> header, or -1 when the file has none.</param>
        public void Parse(string[] inputLines, int startIndex, SimulationInput simulationInput, PopulationInput population, PedestrianModuleInput pedestrianInput, TrafficModuleInput trafficInput, List<int> destinationLineIndices, List<int> responseCurveLineIndices, List<int> evacuationGroupLineIndices, string rootFolder, out bool success)
        {
            bool pedestrian = pedestrianInput.Enabled;
            bool traffic = trafficInput.Enabled;
            success = true;

            if (startIndex >= 0)
            {
                Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, startIndex);
                foreach (string retired in new[] { nameof(UseTriggerBufferEvacuation), nameof(TriggerBufferFile), "EvacuationOrderStart" })
                {
                    if (inputToParse.ContainsKey(retired))
                    {
                        PREACTInput.InputWarning(retired, "is no longer used and is ignored; it is not written when the scenario is saved.");
                    }
                }
            }

            using (PREACTInput.SoftRequirements(!traffic))
            {
                EvacuationDestinationInput.Parse(EvacuationDestinationInputs, inputLines, destinationLineIndices, out bool ok);
                success &= ok;
                if (EvacuationDestinationInputs.Count == 0)
                {
                    PREACTInput.InputProblem("Destination", "the traffic module needs at least one [Destination] to drive to.");
                    success &= !traffic;
                }
            }

            using (PREACTInput.SoftRequirements(!pedestrian))
            {
                ResponseCurve.Parse(ResponseCurves, inputLines, responseCurveLineIndices, simulationInput, out bool ok);
                success &= ok;
                if (ResponseCurves.Count == 0)
                {
                    PREACTInput.InputProblem("ResponseCurve", "the pedestrian module needs at least one [ResponseCurve] to decide when households leave.");
                    success &= !pedestrian;
                }
            }

            //must be done after response curves and destinations
            using (PREACTInput.SoftRequirements(!pedestrian && !traffic))
            {
                EvacuationGroupInput.Parse(EvacuationGroupInputs, inputLines, evacuationGroupLineIndices, EvacuationDestinationInputs, ResponseCurves, simulationInput, population, rootFolder, pedestrian, traffic, out bool ok);
                success &= ok;
                if (EvacuationGroupInputs.Count == 0 && pedestrian)
                {
                    PREACTInput.InputProblem("EvacuationGroup", "the pedestrian module needs at least one [EvacuationGroup]: every household belongs to one.");
                    success = false;
                }
            }

            _data.LoadAll(rootFolder, out bool _);
        }
    }
}
