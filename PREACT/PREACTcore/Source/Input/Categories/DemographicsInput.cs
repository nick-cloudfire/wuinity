using System.Collections.Generic;
using PREACT.Input;

namespace PREACT.Evacuation
{
    public class DemographicsInput
    {
        public string Name = string.Empty;
        public bool AllowMoreThanOneCar = true;
        public int MaxCars = 2;
        public float MaxCarsProbability = 0.3f;
        public bool Default = false;

        public DemographicsInput() 
        {
            
        }

        public static void Parse(Dictionary<string, DemographicsInput> newInputs, string[] inputLines, List<int> demographicsLineIndices, out bool success)
        {
            //Nothing here is critical: a group whose demographics are missing uses the default ones. Each
            //section is read on its own, so one without a name no longer discards every one after it.
            success = true;
            newInputs.Clear();

            for (int i = 0; i < demographicsLineIndices.Count; ++i)
            {
                DemographicsInput newInput = new DemographicsInput();
                Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, demographicsLineIndices[i]);
                string nameOfInput, userInput;

                nameOfInput = nameof(Name);
                if (!inputToParse.TryGetValue(nameOfInput, out userInput) || userInput.Length == 0)
                {
                    PREACTInput.InputWarning("Demographics", $"the section on line {demographicsLineIndices[i] + 1} has no Name, so nothing can refer to it; it is ignored.");
                    continue;
                }
                newInput.Name = userInput;
                if (newInputs.ContainsKey(newInput.Name))
                {
                    PREACTInput.InputWarning("Demographics", $"{newInput.Name} is defined more than once; the first definition is used.");
                    continue;
                }

                nameOfInput = nameof(AllowMoreThanOneCar);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Bool(userInput, out newInput.AllowMoreThanOneCar))
                    {
                        newInput.AllowMoreThanOneCar = true;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "true");
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, "true");
                }

                nameOfInput = nameof(MaxCars);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Int(userInput, out newInput.MaxCars) || newInput.MaxCars < 1)
                    {
                        newInput.MaxCars = 2;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "2");
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, "2");
                }

                nameOfInput = nameof(MaxCarsProbability);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Float(userInput, out newInput.MaxCarsProbability) || newInput.MaxCarsProbability < 0f || newInput.MaxCarsProbability > 1f)
                    {
                        newInput.MaxCarsProbability = 0.3f;
                        PREACTInput.CouldNotInterpretInputMessage(nameOfInput, userInput, false, "0.3");
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(nameOfInput, false, "0.3");
                }

                //not critical; the first one is the default unless another says it is
                if (newInputs.Count == 0)
                {
                    newInput.Default = true;
                }
                if (inputToParse.TryGetValue(nameof(Default), out userInput) && InputParse.Bool(userInput, out bool isDefault))
                {
                    if (isDefault)
                    {
                        foreach (DemographicsInput prevInput in newInputs.Values)
                        {
                            prevInput.Default = false;
                        }
                        newInput.Default = true;
                    }
                    else if (newInputs.Count > 0)
                    {
                        newInput.Default = false;
                    }
                }

                newInputs.Add(newInput.Name, newInput);
            }
        }
    }
}
