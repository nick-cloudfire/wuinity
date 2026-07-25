using System;
using System.Collections.Generic;

namespace PREACT.Utility
{
    /// <summary>
    /// Generic Fortran-namelist (<c>&amp;GROUP ... /</c>) template patcher: finds or inserts a
    /// <c>KEY = value</c> line inside a named group. Mirrors the "clone a base template, patch
    /// known keys, write it out" convention already used for <c>.wui</c> files by
    /// <see cref="ProbabilisticTrigger"/>'s <c>SetKeyInSection</c> (bracket-delimited sections),
    /// adapted to ELMFIRE's <c>&amp;GROUP</c>/<c>/</c> delimiters.
    /// </summary>
    public static class ElmfireNamelist
    {
        public static string[] SetKeyInGroup(string[] lines, string group, string key, string value, bool quoted = false)
        {
            string formattedValue = quoted ? "'" + value + "'" : value;
            string newLine = " " + key + " = " + formattedValue;

            var result = new List<string>(lines);
            string groupHeader = "&" + group;
            bool inGroup = false;
            int groupStart = -1;
            int groupEnd = -1;

            for (int i = 0; i < result.Count; ++i)
            {
                string trimmed = result[i].Trim();
                if (trimmed.StartsWith("&", StringComparison.Ordinal))
                {
                    inGroup = string.Equals(trimmed, groupHeader, StringComparison.OrdinalIgnoreCase);
                    if (inGroup) groupStart = i;
                    continue;
                }
                if (inGroup && trimmed.StartsWith("/", StringComparison.Ordinal))
                {
                    groupEnd = i;
                    break;
                }
                if (inGroup)
                {
                    string noSpace = trimmed.Replace(" ", "");
                    if (noSpace.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        result[i] = newLine;
                        return result.ToArray();
                    }
                }
            }

            if (groupStart < 0)
            {
                //group not present in the template at all: append a new one
                result.Add("");
                result.Add(groupHeader);
                result.Add(newLine);
                result.Add("/");
            }
            else if (groupEnd < 0)
            {
                //group opened but never closed (malformed template): append the key and close it
                result.Add(newLine);
                result.Add("/");
            }
            else
            {
                result.Insert(groupEnd, newLine);
            }
            return result.ToArray();
        }
    }
}
