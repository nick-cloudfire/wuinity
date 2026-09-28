using PREACT.Math;
using System.Collections.Generic;
using System.IO;

namespace PREACT.Input
{
    public class EvacuationDestinationInput
    {
        public string Name = string.Empty;
        public Vector2d LatLon = Vector2d.zero;        
        public DestinationTypes Type = DestinationTypes.Exit;
        public float MaxFlow = -1f; //cars per hour
        public int MaxVehicles = -1;
        public int MaxPeople = -1;
        public bool Blocked = false;
        public PREACTColor Color = PREACTColor.Random();
        
        public EvacuationDestinationInput()
        {

        }

        public EvacuationDestinationInput(string name, Vector2d latLon, DestinationTypes type, PREACTColor color, float maxFlow, int maxCars, int maxPeople, bool blocked)
        {
            Name = name;
            LatLon = latLon;
            Type = type;            
            Color = color;
            MaxFlow = maxFlow;
            MaxVehicles = maxCars;
            MaxPeople = maxPeople;
            Blocked = blocked;
        }

        public static void Parse(Dictionary<string, EvacuationDestinationInput> newInputs, string[] inputLines, List<int> destinationLineIndices, out bool success)
        {
            //Each [Destination] is read on its own: one without a name or with a bad coordinate is reported and
            //the others are still read (this used to break out of the loop, losing every later destination).
            success = true;
            newInputs.Clear();

            for(int i = 0; i < destinationLineIndices.Count; ++i)
            {
                EvacuationDestinationInput newInput = new EvacuationDestinationInput();
                Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, destinationLineIndices[i]);
                string nameOfInput, userInput;

                nameOfInput = nameof(Name);
                if (!inputToParse.TryGetValue(nameOfInput, out userInput) || userInput.Length == 0)
                {
                    PREACTInput.InputProblem("Destination", $"the section on line {destinationLineIndices[i] + 1} has no Name, so nothing can refer to it; it is ignored.");
                    success = false;
                    continue;
                }
                newInput.Name = userInput;
                if (newInputs.ContainsKey(newInput.Name))
                {
                    PREACTInput.InputWarning("Destination", $"{newInput.Name} is defined more than once; the first definition is used.");
                    continue;
                }

                //critical: a destination with no position cannot be driven to
                nameOfInput = nameof(LatLon);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Vector2d(userInput, out newInput.LatLon))
                    {
                        success = false;
                        PREACTInput.CouldNotInterpretInputMessage(newInput.Name + " " + nameOfInput, userInput);
                    }
                }
                else
                {
                    success = false;
                    PREACTInput.InputNotFoundMessage(newInput.Name + " " + nameOfInput, true);
                }

                nameOfInput = nameof(Type);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Enum(userInput, out newInput.Type))
                    {
                        newInput.Type = DestinationTypes.Exit;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, nameof(DestinationTypes.Exit));
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, nameof(DestinationTypes.Exit));
                }

                nameOfInput = nameof(MaxFlow);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Float(userInput, out newInput.MaxFlow))
                    {
                        newInput.MaxFlow = -1f;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "-1 (unlimited)");
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, "-1 (unlimited)");
                }

                nameOfInput = nameof(MaxVehicles);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Int(userInput, out newInput.MaxVehicles))
                    {
                        newInput.MaxVehicles = -1;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "-1 (unlimited)");
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, "-1 (unlimited)");
                }

                nameOfInput = nameof(MaxPeople);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Int(userInput, out newInput.MaxPeople))
                    {
                        newInput.MaxPeople = -1;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "-1 (unlimited)");
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, "-1 (unlimited)");
                }

                nameOfInput = nameof(Blocked);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Bool(userInput, out newInput.Blocked))
                    {
                        newInput.Blocked = false;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "false");
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, "false");
                }

                //not critical: a colour is only for display
                nameOfInput = nameof(Color);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Color(userInput, out newInput.Color))
                    {
                        newInput.Color = PREACTColor.Random();
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "a random colour");
                    }
                }

                newInputs.Add(newInput.Name, newInput);
            }
        }
    }
}