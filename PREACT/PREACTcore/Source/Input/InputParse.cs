//This file is part of PREACT Copyright (C) 2025 Jonathan Wahlqvist
//WUIPlatform is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or (at your option) any later version.
//This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License for more details.
//You should have received a copy of the GNU General Public License along with this program.  If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Globalization;
using PREACT.Math;

namespace PREACT.Input
{
    /// <summary>
    /// Culture-independent parsing of the values a scenario and its data files hold.
    /// </summary>
    /// <remarks>
    /// Every number and date in a <c>.wui</c>, a population CSV or a weather CSV is written with a point as the
    /// decimal separator. The parsers used to call <c>double.TryParse(text, out x)</c>, which reads with the
    /// current thread's culture: on an el-GR or de-DE thread <c>38.0123</c> is 380123 and <c>3.5</c> is 35. Only
    /// the thread that constructed the engine was ever set to invariant, so a GUI background load or a worker
    /// thread could read a different scenario from the same file. Everything goes through here instead.
    ///
    /// Thousands separators are not accepted: in the invariant culture <c>1,5</c> would silently become 15.
    /// </remarks>
    public static class InputParse
    {
        public static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

        public static bool Double(string text, out double value)
        {
            return double.TryParse(text?.Trim(), NumberStyles.Float, Culture, out value);
        }

        public static bool Float(string text, out float value)
        {
            return float.TryParse(text?.Trim(), NumberStyles.Float, Culture, out value);
        }

        public static bool Int(string text, out int value)
        {
            return int.TryParse(text?.Trim(), NumberStyles.Integer, Culture, out value);
        }

        public static bool Bool(string text, out bool value)
        {
            return bool.TryParse(text?.Trim(), out value);
        }

        /// <summary>ISO 8601 (<c>2026-06-28T12:35:53</c>) and anything else the invariant culture reads.</summary>
        public static bool DateTime(string text, out DateTime value)
        {
            return System.DateTime.TryParse(text?.Trim(), Culture, DateTimeStyles.None, out value);
        }

        /// <summary>The trimmed, non-empty elements of a comma-separated value.</summary>
        public static string[] List(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Array.Empty<string>();
            }

            string[] parts = text.Split(',');
            var result = new List<string>(parts.Length);
            foreach (string part in parts)
            {
                string trimmed = part.Trim();
                if (trimmed.Length > 0)
                {
                    result.Add(trimmed);
                }
            }
            return result.ToArray();
        }

        /// <summary>Exactly two comma-separated numbers, e.g. a lat/lon.</summary>
        public static bool Vector2d(string text, out Vector2d value)
        {
            value = Math.Vector2d.zero;
            string[] parts = (text ?? string.Empty).Split(',');
            if (parts.Length != 2 || !Double(parts[0], out double x) || !Double(parts[1], out double y))
            {
                return false;
            }
            value = new Vector2d(x, y);
            return true;
        }

        public static bool Vector2(string text, out System.Numerics.Vector2 value)
        {
            value = System.Numerics.Vector2.Zero;
            string[] parts = (text ?? string.Empty).Split(',');
            if (parts.Length != 2 || !Float(parts[0], out float x) || !Float(parts[1], out float y))
            {
                return false;
            }
            value = new System.Numerics.Vector2(x, y);
            return true;
        }

        /// <summary>Three components r,g,b in 0-1; alpha is not part of the format.</summary>
        public static bool Color(string text, out PREACTColor value)
        {
            value = PREACTColor.white;
            string[] parts = (text ?? string.Empty).Split(',');
            if (parts.Length != 3 || !Float(parts[0], out float r) || !Float(parts[1], out float g) || !Float(parts[2], out float b))
            {
                return false;
            }
            value = new PREACTColor(r, g, b);
            return true;
        }

        /// <summary>A comma-separated list of numbers; false if any element is not one.</summary>
        public static bool DoubleList(string text, List<double> into)
        {
            into.Clear();
            foreach (string element in List(text))
            {
                if (!Double(element, out double value))
                {
                    into.Clear();
                    return false;
                }
                into.Add(value);
            }
            return true;
        }

        /// <summary>A named value of <typeparamref name="T"/>, case-sensitive, never a bare number.</summary>
        public static bool Enum<T>(string text, out T value) where T : struct
        {
            value = default;
            string trimmed = text?.Trim();
            if (string.IsNullOrEmpty(trimmed) || char.IsDigit(trimmed[0]) || trimmed[0] == '-')
            {
                return false;
            }
            return System.Enum.TryParse(trimmed, false, out value) && System.Enum.IsDefined(typeof(T), value);
        }

        /// <summary>Formats a number the way the writer does, for messages that name a default.</summary>
        public static string Format(double value)
        {
            return value.ToString("R", Culture);
        }
    }
}
