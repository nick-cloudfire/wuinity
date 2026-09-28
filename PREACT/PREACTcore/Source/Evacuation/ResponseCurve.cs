//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using PREACT.Input;
using System;

namespace PREACT.Evacuation
{
    public enum TimeInputs { Absolute, Relative }

    public struct ResponseDataPoint
    {
        public float Probability;
        public float Time;

        public ResponseDataPoint(float time, float probability)
        {
            Time = time;
            Probability = probability;
        }
    }

    /// <summary>
    /// Response curve used for when people will start evacuating.
    /// Time relative to evacuation order being announced.
    /// </summary>
    public struct ResponseCurve
    {
        public string Name;
        public TimeInputs TimeInput;
        public ResponseDataPoint[] DataPoints;


        public ResponseCurve(ResponseDataPoint[] dataPoints, string name)
        {
            Name = name;
            DataPoints = dataPoints;
            TimeInput = TimeInputs.Relative;
        }

        public ResponseCurve(List<ResponseDataPoint> dataPoints, string name)
        {
            Name = name;
            DataPoints = dataPoints.ToArray();
            TimeInput = TimeInputs.Relative;
        }

        public void SetDataPoints(List<ResponseDataPoint> dataPoints)
        {
            DataPoints = dataPoints.ToArray();
        }

        /// <summary>
        /// Reads every <c>[ResponseCurve]</c>: <c>Name</c>, <c>TimeInput</c> and then bare <c>time,probability</c>
        /// rows, in any order, with comments and blank lines allowed between them.
        /// </summary>
        /// <remarks>
        /// <b>Relative</b> times are seconds after the group's evacuation order. <b>Absolute</b> times are dates;
        /// they are held as seconds after the simulation start (the group's order time is then <i>not</i> added,
        /// see <see cref="EvacuationGroup.GetWeightedRandomResponseTime"/>) and written back as dates, so a save
        /// keeps them. A save used to write those seconds under <c>TimeInput=Absolute</c>, which the next load
        /// could not read - the curve was lost.
        ///
        /// Rows used to be located by position (header + 3) and counted from the number of keys, so a comment,
        /// a blank line or a reordered key broke the curve, and the error printed the TimeInput value instead of
        /// the offending row. One curve without a name also stopped every curve after it from being read.
        /// </remarks>
        public static void Parse(Dictionary<string, ResponseCurve> newInputs, string[] inputLines, List<int> responseCurveLineIndices, SimulationInput simulationInput, out bool success)
        {
            success = true;
            newInputs.Clear();

            for (int i = 0; i < responseCurveLineIndices.Count; ++i)
            {
                int headerIndex = responseCurveLineIndices[i];
                ResponseCurve newInput = new ResponseCurve();
                Dictionary<string, string> inputToParse = PREACTInput.GetHeaderInput(inputLines, headerIndex, true);
                string nameOfInput, userInput;

                nameOfInput = nameof(Name);
                if (!inputToParse.TryGetValue(nameOfInput, out userInput) || userInput.Length == 0)
                {
                    PREACTInput.InputProblem("ResponseCurve", $"the section on line {headerIndex + 1} has no Name, so nothing can refer to it; it is ignored.");
                    success = false;
                    continue;
                }
                newInput.Name = userInput;
                if (newInputs.ContainsKey(newInput.Name))
                {
                    PREACTInput.InputWarning("ResponseCurve", $"{newInput.Name} is defined more than once; the first definition is used.");
                    continue;
                }

                nameOfInput = nameof(TimeInput);
                newInput.TimeInput = TimeInputs.Relative;
                if (inputToParse.TryGetValue(nameOfInput, out userInput))
                {
                    if (!InputParse.Enum(userInput, out newInput.TimeInput))
                    {
                        success = false;
                        PREACTInput.CouldNotInterpretInputMessage(newInput.Name + " " + nameOfInput, userInput);
                        continue;
                    }
                }
                else
                {
                    PREACTInput.InputNotFoundMessage(newInput.Name + " " + nameOfInput, false, nameof(TimeInputs.Relative));
                }

                //The data rows are the lines of the section that have no '=' - whatever order they come in.
                List<ResponseDataPoint> points = new List<ResponseDataPoint>();
                bool rowsOk = true;
                for (int j = headerIndex + 1; j < inputLines.Length; ++j)
                {
                    string row = inputLines[j];
                    if (row.StartsWith("["))
                    {
                        break;
                    }
                    if (row.Length == 0 || row.IndexOf('=') >= 0)
                    {
                        continue;
                    }

                    string[] data = row.Split(',');
                    ResponseDataPoint dataPoint = new ResponseDataPoint();
                    bool ok = data.Length == 2;
                    if (ok && newInput.TimeInput == TimeInputs.Relative)
                    {
                        ok = InputParse.Float(data[0], out dataPoint.Time);
                    }
                    else if (ok)
                    {
                        ok = InputParse.DateTime(data[0], out DateTime dateTime);
                        dataPoint.Time = (float)(dateTime - simulationInput.StartDateTime).TotalSeconds;
                    }
                    ok = ok && InputParse.Float(data[1], out dataPoint.Probability);

                    if (!ok)
                    {
                        rowsOk = false;
                        PREACTInput.CouldNotInterpretInputMessage($"Response curve {newInput.Name} data row on line {j + 1}", row);
                        continue;
                    }
                    points.Add(dataPoint);
                }

                if (points.Count < 2)
                {
                    success = false;
                    PREACTInput.InputProblem(newInput.Name, $"a response curve needs at least two time,probability rows; it has {points.Count}.");
                    continue;
                }

                //Monotone in both, or the inverse-CDF draw in EvacuationGroup picks the wrong interval.
                for (int k = 1; k < points.Count; ++k)
                {
                    if (points[k].Time < points[k - 1].Time || points[k].Probability < points[k - 1].Probability)
                    {
                        rowsOk = false;
                        PREACTInput.InputProblem(newInput.Name, $"rows must increase in both time and probability; row {k + 1} ({points[k].Time}, {points[k].Probability}) goes back.");
                        break;
                    }
                }
                if (points[points.Count - 1].Probability > 1.0001f || points[0].Probability < 0f)
                {
                    rowsOk = false;
                    PREACTInput.InputProblem(newInput.Name, "probabilities must lie between 0 and 1.");
                }
                else if (points[points.Count - 1].Probability < 0.9999f)
                {
                    //Legitimate: the rest of the population never leaves. Worth saying, because it is also what a typo looks like.
                    PREACTInput.InputWarning(newInput.Name, $"ends at probability {points[points.Count - 1].Probability}, so {(1f - points[points.Count - 1].Probability) * 100f:F0}% of the households using it never evacuate.");
                }

                success &= rowsOk;
                newInput.SetDataPoints(points);
                newInputs.Add(newInput.Name, newInput);
            }
        }
    }
}

