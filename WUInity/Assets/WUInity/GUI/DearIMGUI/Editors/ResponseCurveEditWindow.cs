using ImGuiNET;
using PREACT.Evacuation;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI.Editors
{
    /// <summary>
    /// Editor for a response curve: the cumulative share of households that have set off by a given
    /// time after the evacuation order.
    ///
    /// ResponseCurve is a struct, so it cannot be edited through the dictionary that holds it -
    /// mutating what the indexer returns would change a copy and lose the edit. Everything is
    /// therefore edited in local state here and written back as a whole value on OK.
    /// </summary>
    public static class ResponseCurveEditWindow
    {
        private static bool _isOpen;
        private static Dictionary<string, ResponseCurve> _inputs;
        private static string _oldKey = string.Empty;

        private static string _name = string.Empty;
        private static TimeInputs _timeInput = TimeInputs.Relative;
        private static readonly List<ResponseDataPoint> _points = new List<ResponseDataPoint>();

        private static string[] TimeInputStrings = Enum.GetNames(typeof(TimeInputs));
        private static int _timeInputIndex;

        //Rebuilt whenever a value changes rather than every frame, since PlotLines needs a plain
        //float array and the curve can hold a few hundred points.
        private static float[] _plotProbabilities = new float[0];
        private static bool _plotDirty = true;

        public static void Open(Dictionary<string, ResponseCurve> inputs, ResponseCurve? curve)
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;

            _inputs = inputs;
            _points.Clear();

            if (curve == null)
            {
                _name = string.Empty;
                _oldKey = string.Empty;
                _timeInput = TimeInputs.Relative;
                //A usable starting curve rather than an empty one: nobody away at the order, everyone
                //away an hour later. An empty curve would divide by zero downstream.
                _points.Add(new ResponseDataPoint(0f, 0f));
                _points.Add(new ResponseDataPoint(3600f, 1f));
            }
            else
            {
                ResponseCurve c = curve.Value;
                _name = c.Name;
                _oldKey = c.Name;
                _timeInput = c.TimeInput;
                if (c.DataPoints != null)
                {
                    _points.AddRange(c.DataPoints);
                }
            }

            _timeInputIndex = (int)_timeInput;
            _plotDirty = true;
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.Begin("Response curve editor", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            ImGui.InputText("Name", ref _name, 64);

            ImGui.Combo("TimeInput", ref _timeInputIndex, TimeInputStrings, TimeInputStrings.Length);
            _timeInput = (TimeInputs)_timeInputIndex;
            ImGui.TextDisabled(_timeInput == TimeInputs.Relative
                ? "Times are seconds after the evacuation order."
                : "Times are absolute seconds from the simulation start.");

            ImGui.SeparatorText("Curve");
            DrawPlot();

            ImGui.SeparatorText("Points");
            DrawPoints();

            ImGui.Separator();
            DrawProblems();

            //A curve is keyed by name, so an empty or already-taken one cannot be committed - the
            //same rule the destination and demographics editors use.
            bool nameIsFree = !string.IsNullOrWhiteSpace(_name)
                              && (_name == _oldKey || _inputs == null || !_inputs.ContainsKey(_name));
            bool canCommit = nameIsFree && _points.Count >= 2;

            ImGui.BeginDisabled(!canCommit);
            if (ImGui.Button("OK"))
            {
                Commit();
                _isOpen = false;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _isOpen = false;
            }

            if (!nameIsFree)
            {
                ImGui.TextDisabled(string.IsNullOrWhiteSpace(_name) ? "Needs a name." : "That name is already used.");
            }
            else if (_points.Count < 2)
            {
                ImGui.TextDisabled("A curve needs at least two points.");
            }

            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void DrawPlot()
        {
            if (_plotDirty)
            {
                RebuildPlot();
            }

            if (_plotProbabilities.Length < 2)
            {
                ImGui.TextDisabled("Add at least two points to plot the curve.");
                return;
            }

            //Fixed 0-1 vertical range: this is a cumulative probability, so letting the plot
            //autoscale would make a curve that only reaches 0.4 look complete.
            ImGui.PlotLines("###responsecurve", ref _plotProbabilities[0], _plotProbabilities.Length, 0,
                $"0 to {LastTime()} s, 0 to 1", 0f, 1f, new Vector2(-1, 120));
        }

        private static void DrawPoints()
        {
            int removeAt = -1;

            //Fixed height so a long curve scrolls inside the window instead of pushing OK off it.
            ImGui.BeginChild("points", new Vector2(0, 180), (ImGuiChildFlags)1);
            for (int i = 0; i < _points.Count; ++i)
            {
                ImGui.PushID(i);

                ResponseDataPoint p = _points[i];
                float time = p.Time;
                float probability = p.Probability;

                ImGui.SetNextItemWidth(110);
                if (ImGui.InputFloat("Time (s)", ref time))
                {
                    _points[i] = new ResponseDataPoint(time, probability);
                    _plotDirty = true;
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(110);
                //Clamped: a cumulative probability outside 0-1 is never meaningful, and the sliders
                //that consume it downstream assume the range.
                if (ImGui.SliderFloat("Probability", ref probability, 0f, 1f))
                {
                    _points[i] = new ResponseDataPoint(time, probability);
                    _plotDirty = true;
                }
                ImGui.SameLine();
                if (ImGui.Button("X"))
                {
                    removeAt = i;
                }

                ImGui.PopID();
            }
            ImGui.EndChild();

            if (removeAt >= 0)
            {
                _points.RemoveAt(removeAt);
                _plotDirty = true;
            }

            if (ImGui.Button("Add point"))
            {
                //Continues past the end of the curve rather than landing on top of an existing point.
                float time = _points.Count > 0 ? LastTime() + 600f : 0f;
                float probability = _points.Count > 0 ? _points[_points.Count - 1].Probability : 0f;
                _points.Add(new ResponseDataPoint(time, probability));
                _plotDirty = true;
            }
            ImGui.SameLine();
            if (ImGui.Button("Sort by time"))
            {
                _points.Sort((a, b) => a.Time.CompareTo(b.Time));
                _plotDirty = true;
            }
        }

        /// <summary>
        /// Reports what is wrong with the curve without refusing to save it, since a curve can be
        /// legitimately mid-edit. Both checks matter downstream: the curve is sampled as a cumulative
        /// distribution, so points out of time order or probabilities that fall are read as a
        /// negative share of households.
        /// </summary>
        private static void DrawProblems()
        {
            bool timeOutOfOrder = false;
            bool probabilityFalls = false;
            for (int i = 1; i < _points.Count; ++i)
            {
                if (_points[i].Time <= _points[i - 1].Time) timeOutOfOrder = true;
                if (_points[i].Probability < _points[i - 1].Probability) probabilityFalls = true;
            }

            if (timeOutOfOrder)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Times are not strictly increasing - use \"Sort by time\".");
            }
            if (probabilityFalls)
            {
                ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f), "Probability decreases along the curve; it is cumulative, so it should only rise.");
            }
            if (_points.Count > 0 && _points[_points.Count - 1].Probability < 0.999f)
            {
                ImGui.TextDisabled($"Ends at {_points[_points.Count - 1].Probability:F2}, so some households never leave.");
            }
        }

        private static float LastTime()
        {
            return _points.Count > 0 ? _points[_points.Count - 1].Time : 0f;
        }

        /// <summary>
        /// Samples the curve at even time steps. The points are unevenly spaced, but PlotLines draws
        /// one value per array slot at a fixed pitch, so plotting the raw probabilities would show
        /// the right shape against the wrong time axis.
        /// </summary>
        private static void RebuildPlot()
        {
            _plotDirty = false;

            if (_points.Count < 2)
            {
                _plotProbabilities = new float[0];
                return;
            }

            var sorted = new List<ResponseDataPoint>(_points);
            sorted.Sort((a, b) => a.Time.CompareTo(b.Time));

            float start = sorted[0].Time;
            float end = sorted[sorted.Count - 1].Time;
            if (end <= start)
            {
                _plotProbabilities = new float[0];
                return;
            }

            const int samples = 128;
            _plotProbabilities = new float[samples];
            for (int i = 0; i < samples; ++i)
            {
                float t = start + (end - start) * i / (samples - 1);
                _plotProbabilities[i] = Sample(sorted, t);
            }
        }

        private static float Sample(List<ResponseDataPoint> sorted, float time)
        {
            if (time <= sorted[0].Time) return sorted[0].Probability;
            if (time >= sorted[sorted.Count - 1].Time) return sorted[sorted.Count - 1].Probability;

            for (int i = 1; i < sorted.Count; ++i)
            {
                if (time <= sorted[i].Time)
                {
                    float span = sorted[i].Time - sorted[i - 1].Time;
                    float f = span <= 0f ? 0f : (time - sorted[i - 1].Time) / span;
                    return sorted[i - 1].Probability + (sorted[i].Probability - sorted[i - 1].Probability) * f;
                }
            }
            return sorted[sorted.Count - 1].Probability;
        }

        private static void Commit()
        {
            if (_inputs == null)
            {
                return;
            }

            //Stored sorted, so whatever consumes it does not have to cope with arbitrary ordering.
            var sorted = new List<ResponseDataPoint>(_points);
            sorted.Sort((a, b) => a.Time.CompareTo(b.Time));

            ResponseCurve curve = new ResponseCurve(sorted, _name);
            curve.TimeInput = _timeInput;

            if (_oldKey != _name)
            {
                _inputs.Remove(_oldKey);
            }
            _inputs[_name] = curve;
        }
    }
}
