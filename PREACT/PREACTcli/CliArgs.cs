using System;
using System.Collections.Generic;
using System.Globalization;

namespace PREACTcli
{
    /// <summary>
    /// A strict command-line parser: every flag must be known, every value must parse, and every flag that takes
    /// a value must have one.
    /// </summary>
    /// <remarks>
    /// The campaign's parser ignored unknown flags and fell back to defaults on unparsable values, so
    /// <c>--tsop 36000</c> quietly ran 72 hours and <c>--tstop 36,000</c> quietly left the template's stop time
    /// in force. Both are campaigns of hours that answer a question nobody asked.
    /// </remarks>
    internal sealed class CliArgs
    {
        private readonly Dictionary<string, Action<string>> _valued = new Dictionary<string, Action<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action> _switches = new Dictionary<string, Action>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _retired = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>A flag followed by a value.</summary>
        public CliArgs Value(string flag, Action<string> set)
        {
            _valued[flag] = set;
            return this;
        }

        public CliArgs Int(string flag, Action<int> set, int min = int.MinValue, int max = int.MaxValue)
        {
            return Value(flag, v =>
            {
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                    throw new ArgumentException($"{flag} takes a whole number, not '{v}'.");
                if (n < min || n > max)
                    throw new ArgumentException($"{flag} must be between {min} and {max}, not {n}.");
                set(n);
            });
        }

        public CliArgs Double(string flag, Action<double> set, double min = double.MinValue, double max = double.MaxValue)
        {
            return Value(flag, v =>
            {
                if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                    || double.IsNaN(d) || double.IsInfinity(d))
                    throw new ArgumentException($"{flag} takes a number (with a '.' for decimals), not '{v}'.");
                if (d < min || d > max)
                    throw new ArgumentException($"{flag} must be between {min.ToString(CultureInfo.InvariantCulture)} "
                                                + $"and {max.ToString(CultureInfo.InvariantCulture)}, not {v}.");
                set(d);
            });
        }

        /// <summary>A flag with no value.</summary>
        public CliArgs Switch(string flag, Action set)
        {
            _switches[flag] = set;
            return this;
        }

        /// <summary>A flag that used to exist, refused with what to do instead.</summary>
        public CliArgs Retired(string flag, string instead)
        {
            _retired[flag] = instead;
            return this;
        }

        /// <summary>Applies <paramref name="args"/>. Throws <see cref="ArgumentException"/> with a user-facing message.</summary>
        public void Parse(IReadOnlyList<string> args)
        {
            for (int i = 0; i < args.Count; ++i)
            {
                string a = args[i];

                if (_retired.TryGetValue(a, out string instead))
                {
                    throw new ArgumentException($"{a} is no longer an option: {instead}");
                }

                if (_switches.TryGetValue(a, out Action toggle))
                {
                    toggle();
                    continue;
                }

                if (_valued.TryGetValue(a, out Action<string> set))
                {
                    if (i + 1 >= args.Count || (args[i + 1].StartsWith("--", StringComparison.Ordinal) && args[i + 1].Length > 2
                                                && !char.IsDigit(args[i + 1][2])))
                    {
                        throw new ArgumentException($"{a} needs a value.");
                    }
                    set(args[++i]);
                    continue;
                }

                throw new ArgumentException(a.StartsWith("-", StringComparison.Ordinal)
                    ? $"Unknown option: {a}"
                    : $"Unexpected argument: {a}");
            }
        }
    }
}
