using PREACT.Input;
using PREACT.Math;
using System;
using System.Collections.Generic;
using System.IO;

namespace PREACT.Evacuation
{
    public enum DestinationChoices { Random, ClosestEuclidean, EvacGroupCDF, EvacGroupClosestEuclidean };

    public class EvacuationGroupInput
    {
        public string Name = string.Empty;
        public DateTime EvacuationOrderDateTime;
        public PREACTColor Color = PREACTColor.white;
        public DestinationChoices DestinationChoice = DestinationChoices.EvacGroupCDF;
        public List<string> Destinations = new List<string>(16);
        public List<double> DestinationsCDF = new List<double>(16);
        public List<string> ResponseCurves = new List<string>(16);
        public List<double> ResponseCurvesCDF = new List<double>(16);
        public string Demographics = string.Empty;
        public string ShapeFile = string.Empty;

        /// <summary>
        /// A raster mask of the group's area, any positive value marking a cell that belongs to it.
        /// An alternative to <see cref="ShapeFile"/>, and what painting a group on the map produces:
        /// a painted area is a set of cells, and round-tripping that through a polygon would only
        /// lose fidelity. Takes precedence when both are given.
        /// </summary>
        public string MaskFile = string.Empty;

        public bool Default = false;

        public EvacuationGroupInput()
        {

        }

        public static void Parse(Dictionary<string, EvacuationGroupInput> newInputs, string[] inputLines, List<int> evacGroupLineIndices,
            Dictionary<string, EvacuationDestinationInput> destinationInputs, Dictionary<string, ResponseCurve> responseCurves, SimulationInput simulation, PopulationInput population, string rootFolder, out bool success)
        {
            Parse(newInputs, inputLines, evacGroupLineIndices, destinationInputs, responseCurves, simulation, population, rootFolder, true, true, out success);
        }

        /// <summary>
        /// Reads every <c>[EvacuationGroup]</c>, each on its own: a problem in one is reported and the others are
        /// still read (a failure used to break out of the loop and lose every later group).
        /// </summary>
        /// <remarks>
        /// What is critical follows what the run needs. Response curves are needed by the pedestrian module;
        /// the group's destinations only by the traffic module, and only for the destination choices that draw
        /// from the group's own list. <c>EvacuationOrderDateTime</c> (default: the simulation start) and
        /// <c>Demographics</c> (default: the default demographics) are optional, as documented - both used to be
        /// reported as critical, which is why no shipped example loaded as runnable. References to destinations
        /// or curves that do not exist are kept as written, so a save does not silently drop a typo.
        /// </remarks>
        public static void Parse(Dictionary<string, EvacuationGroupInput> newInputs, string[] inputLines, List<int> evacGroupLineIndices,
            Dictionary<string, EvacuationDestinationInput> destinationInputs, Dictionary<string, ResponseCurve> responseCurves, SimulationInput simulation, PopulationInput population, string rootFolder,
            bool pedestrianEnabled, bool trafficEnabled, out bool success)
        {            
            success = true;
            newInputs.Clear();

            for (int i = 0; i < evacGroupLineIndices.Count; ++i)
            {
                EvacuationGroupInput newInput = new EvacuationGroupInput();
                Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, evacGroupLineIndices[i]);
                string nameOfInput, userInput;
                bool groupOk = true;

                nameOfInput = nameof(Name);
                if (!inputToParse.TryGetValue(nameOfInput, out userInput) || userInput.Length == 0)
                {
                    PREACTInput.InputProblem("EvacuationGroup", $"the section on line {evacGroupLineIndices[i] + 1} has no Name; it is ignored.");
                    success = false;
                    continue;
                }
                newInput.Name = userInput;
                if (newInputs.ContainsKey(newInput.Name))
                {
                    PREACTInput.InputWarning("EvacuationGroup", $"{newInput.Name} is defined more than once; the first definition is used.");
                    continue;
                }
                string prefix = newInput.Name + " ";

                //not critical: defaults to the simulation start, as documented
                nameOfInput = nameof(EvacuationOrderDateTime);
                newInput.EvacuationOrderDateTime = simulation.StartDateTime;
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.DateTime(userInput, out newInput.EvacuationOrderDateTime))
                    {
                        newInput.EvacuationOrderDateTime = simulation.StartDateTime;
                        PREACTInput.CouldNotInterpretInputMessage(prefix + nameOfInput, userInput, false, "the simulation start");
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(prefix + nameOfInput, false, "the simulation start");
                }

                //not critical
                nameOfInput = nameof(DestinationChoice);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Enum(userInput, out newInput.DestinationChoice))
                    {
                        newInput.DestinationChoice = DestinationChoices.EvacGroupCDF;
                        PREACTInput.CouldNotInterpretInputMessage(prefix + nameOfInput, userInput, false, nameof(DestinationChoices.EvacGroupCDF));
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(prefix + nameOfInput, false, nameof(DestinationChoices.EvacGroupCDF));
                }
                bool usesGroupDestinations = newInput.DestinationChoice == DestinationChoices.EvacGroupCDF
                                             || newInput.DestinationChoice == DestinationChoices.EvacGroupClosestEuclidean;

                //not critical: the default demographics are used
                nameOfInput = nameof(Demographics);
                if (inputToParse.TryGetValue(nameOfInput, out userInput) && userInput.Length > 0)
                {
                    newInput.Demographics = userInput;
                    if (!population.Demographics.ContainsKey(userInput))
                    {
                        PREACTInput.MissingReferenceToOtherInput(prefix + nameOfInput, userInput, false);
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(prefix + nameOfInput, false, "the default demographics");
                }

                //critical only for the traffic module, and only for a choice that draws from the group's own list
                using (PREACTInput.SoftRequirements(!trafficEnabled || !usesGroupDestinations))
                {
                    nameOfInput = nameof(Destinations);
                    if (inputToParse.TryGetValue(nameOfInput, out userInput))
                    {
                        newInput.Destinations.AddRange(InputParse.List(userInput));
                        foreach (string destination in newInput.Destinations)
                        {
                            if (!destinationInputs.ContainsKey(destination))
                            {
                                groupOk &= !trafficEnabled || !usesGroupDestinations;
                                PREACTInput.MissingReferenceToOtherInput(prefix + nameOfInput, destination);
                            }
                        }
                    }
                    if (newInput.Destinations.Count == 0)
                    {
                        groupOk &= !trafficEnabled || !usesGroupDestinations;
                        PREACTInput.InputNotFoundMessage(prefix + nameOfInput, true);
                    }

                    if (newInput.Destinations.Count == 1)
                    {
                        newInput.DestinationsCDF.Add(1.0);
                    }
                    else if (newInput.Destinations.Count > 1)
                    {
                        groupOk &= ReadCdf(inputToParse, nameof(DestinationsCDF), prefix, newInput.Destinations, newInput.DestinationsCDF,
                            newInput.DestinationChoice == DestinationChoices.EvacGroupCDF && trafficEnabled);
                    }
                }

                //critical for the pedestrian module: every household draws its departure from one of these
                using (PREACTInput.SoftRequirements(!pedestrianEnabled))
                {
                    nameOfInput = nameof(ResponseCurves);
                    if (inputToParse.TryGetValue(nameOfInput, out userInput))
                    {
                        newInput.ResponseCurves.AddRange(InputParse.List(userInput));
                        foreach (string curve in newInput.ResponseCurves)
                        {
                            if (!responseCurves.ContainsKey(curve))
                            {
                                groupOk &= !pedestrianEnabled;
                                PREACTInput.MissingReferenceToOtherInput(prefix + nameOfInput, curve);
                            }
                        }
                    }
                    if (newInput.ResponseCurves.Count == 0)
                    {
                        groupOk &= !pedestrianEnabled;
                        PREACTInput.InputNotFoundMessage(prefix + nameOfInput, true);
                    }

                    if (newInput.ResponseCurves.Count == 1)
                    {
                        newInput.ResponseCurvesCDF.Add(1.0);
                    }
                    else if (newInput.ResponseCurves.Count > 1)
                    {
                        groupOk &= ReadCdf(inputToParse, nameof(ResponseCurvesCDF), prefix, newInput.ResponseCurves, newInput.ResponseCurvesCDF, pedestrianEnabled);
                    }

                    //A group's area comes from either a painted mask or a shapefile. The mask is checked
                    //first and wins when both are present, so a group that has been painted is not
                    //silently overridden by whatever shapefile it was originally defined from. A file that is
                    //named but missing is critical (the user meant an area); no area at all is critical only
                    //when there are several groups to tell apart.
                    bool haveMask = inputToParse.TryGetValue(nameof(MaskFile), out string mask) && mask.Length > 0;
                    bool haveShape = inputToParse.TryGetValue(nameof(ShapeFile), out string shape) && shape.Length > 0;
                    if (haveMask)
                    {
                        newInput.MaskFile = mask;
                        PREACTInput.CheckIfFileExist(prefix + nameof(MaskFile), ref newInput.MaskFile, rootFolder, out bool found);
                        groupOk &= found || !pedestrianEnabled;
                    }
                    if (haveShape)
                    {
                        newInput.ShapeFile = shape;
                        PREACTInput.CheckIfFileExist(prefix + nameof(ShapeFile), ref newInput.ShapeFile, rootFolder, out bool found, !haveMask);
                        groupOk &= found || haveMask || !pedestrianEnabled;
                    }
                    if (!haveMask && !haveShape)
                    {
                        if (evacGroupLineIndices.Count > 1)
                        {
                            groupOk &= !pedestrianEnabled;
                            PREACTInput.InputProblem(prefix + nameof(ShapeFile), "no MaskFile or ShapeFile, so no household can be placed in this group rather than another.");
                        }
                        else
                        {
                            PREACTInput.InputWarning(prefix + nameof(ShapeFile), "no MaskFile or ShapeFile: every household is outside the group"
                                + (population.CullOutsideGroups ? " and, with CullOutsideGroups=true, is removed from the run." : " and is assigned to it as the default group."));
                        }
                    }
                }

                //not critical
                if (newInputs.Count == 0)
                {
                    newInput.Default = true;
                }
                if (inputToParse.TryGetValue(nameof(Default), out userInput) && InputParse.Bool(userInput, out bool isDefault))
                {
                    if (isDefault)
                    {
                        foreach (EvacuationGroupInput prevInput in newInputs.Values)
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

                //not critical: display only
                nameOfInput = nameof(Color);
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Color(userInput, out newInput.Color))
                    {
                        newInput.Color = PREACTColor.Random();
                        PREACTInput.CouldNotInterpretInputMessage(prefix + nameOfInput, userInput, false, "a random colour");
                    }
                }
                else
                {
                    newInput.Color = PREACTColor.Random();
                }

                success &= groupOk;
                newInputs.Add(newInput.Name, newInput);
            }
        }

        /// <summary>
        /// Reads a cumulative distribution over <paramref name="items"/> and checks it is one: as many values as
        /// items, each in [0, 1], never decreasing, ending at 1.
        /// </summary>
        /// <remarks>
        /// An item whose CDF step is zero can never be drawn - Mati's <c>DestinationsCDF=1,1</c> sends every car
        /// to the first destination - which is valid but almost always a mistake, so it is said. A CDF that ends
        /// below 1 sends the remaining draws to the last item.
        /// </remarks>
        private static bool ReadCdf(Dictionary<string, string> inputToParse, string key, string prefix, List<string> items, List<double> cdf, bool critical)
        {
            if (!inputToParse.TryGetValue(key, out string userInput))
            {
                PREACTInput.InputNotFoundMessage(prefix + key, critical);
                //Evenly spread, so a run with the module off - or a save - has something consistent.
                for (int k = 0; k < items.Count; ++k)
                {
                    cdf.Add((k + 1) / (double)items.Count);
                }
                return !critical;
            }

            if (!InputParse.DoubleList(userInput, cdf))
            {
                PREACTInput.CouldNotInterpretInputMessage(prefix + key, userInput, critical);
                for (int k = 0; k < items.Count; ++k)
                {
                    cdf.Add((k + 1) / (double)items.Count);
                }
                return !critical;
            }

            if (cdf.Count != items.Count)
            {
                if (critical)
                {
                    PREACTInput.IncorrectInputCount(prefix + key);
                }
                else
                {
                    PREACTInput.InputWarning(prefix + key, $"has {cdf.Count} values for {items.Count} entries.");
                }
                return !critical;
            }

            for (int k = 0; k < cdf.Count; ++k)
            {
                double previous = k == 0 ? 0.0 : cdf[k - 1];
                if (cdf[k] < 0.0 || cdf[k] > 1.0 + 1e-9 || cdf[k] < previous)
                {
                    PREACTInput.CouldNotInterpretInputMessage(prefix + key, userInput + " (a cumulative distribution must rise from 0 to 1 and never fall)", critical);
                    return !critical;
                }
                if (cdf[k] - previous < 1e-12)
                {
                    PREACTInput.InputWarning(prefix + key, $"gives {items[k]} a probability of 0 ({userInput}), so it is never chosen. Cumulative values: 0.5,1 would split evenly between two.");
                }
            }
            if (cdf[cdf.Count - 1] < 1.0 - 1e-9)
            {
                PREACTInput.InputWarning(prefix + key, $"ends at {cdf[cdf.Count - 1]} rather than 1; draws above it go to {items[items.Count - 1]}.");
            }

            return true;
        }
    }
}
