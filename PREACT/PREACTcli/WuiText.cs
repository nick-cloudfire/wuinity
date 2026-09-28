using System;
using System.Collections.Generic;

namespace PREACTcli
{
    /// <summary>
    /// Sets <c>Key=Value</c> lines in the <c>[Section]</c>s of a <c>.wui</c>, as text.
    /// </summary>
    /// <remarks>
    /// Text rather than a parse-and-write round trip on purpose: a realization's scenario has to be the base
    /// scenario with a handful of keys changed, and the writer omits sections that only restate their defaults.
    /// Reading a scenario is the engine parser's job (<c>PREACTInput.LoadFromLines</c>); the CLI's hand parsers,
    /// which had started to disagree with it about comments and whitespace, are gone.
    /// </remarks>
    internal static class WuiText
    {
        /// <summary>
        /// Sets <paramref name="key"/> in the first <paramref name="section"/>, inserting the key at the end of the
        /// section, or the section at the end of the file, when absent.
        /// </summary>
        public static string[] Set(IReadOnlyList<string> lines, string section, string key, string value)
        {
            var result = new List<string>(lines);
            int sectionStart = -1;
            int insertAt = -1;
            bool inSection = false;

            for (int i = 0; i < result.Count; ++i)
            {
                string line = result[i].Trim();
                if (IsHeader(line))
                {
                    if (inSection)
                    {
                        insertAt = i;
                        break;
                    }
                    inSection = string.Equals(line.Substring(1, line.Length - 2).Trim(), section, StringComparison.OrdinalIgnoreCase);
                    if (inSection) sectionStart = i;
                    continue;
                }

                if (!inSection || line.StartsWith("#")) continue;

                int eq = line.IndexOf('=');
                if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = key + "=" + value;
                    return result.ToArray();
                }
            }

            if (sectionStart < 0)
            {
                result.Add("");
                result.Add("[" + section + "]");
                result.Add(key + "=" + value);
                return result.ToArray();
            }

            if (insertAt < 0) insertAt = result.Count;

            //Before the blank lines that separate this section from the next, so the file keeps its shape.
            while (insertAt - 1 > sectionStart && result[insertAt - 1].Trim().Length == 0) --insertAt;
            result.Insert(insertAt, key + "=" + value);
            return result.ToArray();
        }

        private static bool IsHeader(string trimmed)
        {
            return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']';
        }
    }
}
