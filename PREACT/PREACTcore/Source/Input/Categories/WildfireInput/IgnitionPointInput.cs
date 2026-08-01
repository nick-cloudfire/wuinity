//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PREACT.Math;
using System;

namespace PREACT.Wildfire
{
    [System.Serializable]                           
    public struct IgnitionPointInput
    {
        public Vector2d LatLon;
        public bool AbsoluteTime;
        public float IgnitionTime;
        public DateTime IgnitionDateTime;

        public IgnitionPointInput(Vector2d latLong, bool absoluteTime, float ignitionTime, DateTime ignitionDateTime)    
        {
            LatLon = latLong;
            IgnitionTime = ignitionTime;
            AbsoluteTime = absoluteTime;
            IgnitionDateTime = ignitionDateTime;
        }

        public IgnitionPointInput(double lat, double lon, bool absoluteTime, float ignitionTime, DateTime ignitionDateTime)
        {
            LatLon = new Vector2d(lat, lon);
            IgnitionTime = ignitionTime;
            AbsoluteTime = absoluteTime;
            IgnitionDateTime = ignitionDateTime;
        }

        /// <summary>
        /// Reads one point from an <c>[IgnitionPoint]</c> section of a <c>.wui</c>.
        ///
        /// <c>IgnitionTime</c> is derived rather than read when the point is on absolute time, so a
        /// scenario whose start moves takes its ignitions with it - the seconds in the file were measured
        /// from the old start and would otherwise silently mean a different moment.
        /// </summary>
        public static IgnitionPointInput Parse(string[] inputLines, int startIndex,
            Input.SimulationInput simulationInput, out bool success)
        {
            success = false;
            var point = new IgnitionPointInput(0.0, 0.0, false, 0f, simulationInput.StartDateTime);

            System.Collections.Generic.Dictionary<string, string> input =
                Input.PREACTInput.GetHeaderInput(inputLines, startIndex);

            if (!input.TryGetValue(nameof(LatLon), out string latLon))
            {
                Input.PREACTInput.InputNotFoundMessage(nameof(LatLon), true);
                return point;
            }

            string[] pair = latLon.Split(',');
            if (pair.Length < 2
                || !double.TryParse(pair[0], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                || !double.TryParse(pair[1], System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out double lon))
            {
                Input.PREACTInput.CouldNotInterpretInputMessage(nameof(LatLon), latLon);
                return point;
            }

            point.LatLon = new Vector2d(lat, lon);

            if (input.TryGetValue(nameof(AbsoluteTime), out string absolute))
            {
                bool.TryParse(absolute, out point.AbsoluteTime);
            }

            if (point.AbsoluteTime)
            {
                if (input.TryGetValue(nameof(IgnitionDateTime), out string when)
                    && DateTime.TryParse(when, CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out DateTime dateTime))
                {
                    point.IgnitionDateTime = dateTime;
                    point.IgnitionTime = (float)(dateTime - simulationInput.StartDateTime).TotalSeconds;
                }
                else
                {
                    Input.PREACTInput.CouldNotInterpretInputMessage(nameof(IgnitionDateTime), when);
                    return point;
                }
            }
            else if (input.TryGetValue(nameof(IgnitionTime), out string seconds))
            {
                float.TryParse(seconds, System.Globalization.NumberStyles.Float, CultureInfo.InvariantCulture, out point.IgnitionTime);
                point.IgnitionDateTime = simulationInput.StartDateTime.AddSeconds(point.IgnitionTime);
            }

            success = true;
            return point;
        }

        /// <summary>
        /// Tries to load ignition points froma file defined in the general input file.
        /// Returns an array with anu loaded ignition points, othwerwise returns null.
        /// Sends message to the WUI_LOG to inform the user.
        /// </summary>
        /// <returns></returns>
        public static void LoadIgnitionPointsFile(List<IgnitionPointInput> ignitionPoints, string path, Input.SimulationInput simulationInput, out bool success)
        {
            success = false;
            ignitionPoints.Clear();
            
            bool fileExists = File.Exists(path);
            if (fileExists)
            {
                string[] dataLines = File.ReadAllLines(path);
                //skip first line (header)
                for (int j = 1; j < dataLines.Length; j++)
                {
                    string[] data = dataLines[j].Split(',');
                    if (data.Length >= 3)
                    {
                        double lat, lon;
                        float ignitionTime = 0;
                        DateTime dateTime = DateTime.Now;
                        bool absoluteTime = false;

                        bool b1 = double.TryParse(data[0], out lat);
                        bool b2 = double.TryParse(data[1], out lon);
                        bool b3 = bool.TryParse(data[2], out absoluteTime);

                        bool b4;
                        if(absoluteTime)
                        {
                            b4 = DateTime.TryParse(data[3], out dateTime);
                            if(b4)
                            {
                                ignitionTime = (float)(dateTime - simulationInput.StartDateTime).TotalSeconds;
                            }                           
                        }
                        else
                        {
                            b4 = float.TryParse(data[3], out ignitionTime);
                        }                           

                        if (b1 && b2 && b3 && b4)
                        {
                            IgnitionPointInput iP = new IgnitionPointInput(lat, lon, absoluteTime, ignitionTime, dateTime);
                            ignitionPoints.Add(iP);
                        }
                    }
                }
            }
            else
            {
                Engine.Message(null, Engine.LogType.Warning, "Ignition points data file " + path + " not found and could not be loaded, fire and smoke spread will have to rely on other ignition methods (painted map).");
            }

            if (ignitionPoints.Count > 0)
            {
                Engine.Message(null, Engine.LogType.Log, " Ignition points data file " + path + " was found, " + ignitionPoints.Count + " valid data points were succesfully loaded.");
                success = true;
            }
            else if (fileExists)
            {
                Engine.Message(null, Engine.LogType.Warning, "Ignition points data file " + path + " was found but did not contain any valid data, fire and smoke spread will have to rely on other ignition methods (painted map).");
            }
        }
    }
}
