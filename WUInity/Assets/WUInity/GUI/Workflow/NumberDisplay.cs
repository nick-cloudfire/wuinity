using System;
using System.Globalization;

namespace WUInity.Workflow
{
    /// <summary>
    /// How a number is shown in a field that shows fewer digits than it holds (the ELMFIRE settings), and what an edit of
    /// it stores. Unity-free, so the tests can hold it to that.
    /// </summary>
    /// <remarks>
    /// Two decimals for a value of 1 or more; three significant figures below 1, so a small factor does not read as
    /// another number - WSMFEFF_LOW_MULT, 0.011364, showed as "0.01", 12 % low, and correcting it to "0.02" raised it by
    /// 76 % rather than doubling it (review R2 MI-2). A field writes a value back only when its text is edited; an edit
    /// that leaves exactly the text that was shown keeps the value as it was, so the hidden digits are never lost to
    /// the display's rounding.
    /// </remarks>
    public static class NumberDisplay
    {
        /// <summary>The printf format the field shows <paramref name="value"/> with (ImGui's).</summary>
        public static string Format(double value, int decimals)
        {
            return value != 0.0 && Math.Abs(value) < 1.0 ? "%.3g" : "%." + decimals.ToString(CultureInfo.InvariantCulture) + "f";
        }

        /// <summary>The text <see cref="Format"/> gives, from C# (printf's lower-case exponent).</summary>
        public static string Shown(double value, int decimals)
        {
            return value != 0.0 && Math.Abs(value) < 1.0
                ? value.ToString("G3", CultureInfo.InvariantCulture).Replace('E', 'e')
                : value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        }

        /// <summary>Whether the field shows <paramref name="value"/> rounded, so the exact value belongs in its tooltip.</summary>
        public static bool IsRounded(double value, int decimals)
        {
            return !double.TryParse(Shown(value, decimals), NumberStyles.Float, CultureInfo.InvariantCulture, out double shown)
                   || shown != value;
        }

        /// <summary>
        /// The value to keep after the field's text was edited from <paramref name="before"/> to what parses as
        /// <paramref name="after"/>: <paramref name="before"/> when <paramref name="after"/> is only the number the field
        /// showed for it (the text was typed back as it was), otherwise <paramref name="after"/>.
        /// </summary>
        public static double Committed(double before, double after, int decimals)
        {
            if (after == before) return before;
            return double.TryParse(Shown(before, decimals), NumberStyles.Float, CultureInfo.InvariantCulture, out double shown)
                   && shown == after
                ? before
                : after;
        }
    }
}
