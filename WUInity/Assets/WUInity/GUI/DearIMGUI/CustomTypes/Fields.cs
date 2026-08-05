using System;
using ImGuiNET;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The shared vocabulary the scenario editor and the tool windows are built from: one path picker, one
    /// set of severity colours, one way to write a hint under a field.
    /// </summary>
    /// <remarks>
    /// These existed three or four times over, once per window that needed them, which is why the same
    /// control looked and behaved differently depending on which tab it was on — the landscape tab's path
    /// picker normalised backslashes and the hazards tab's did not, so whether a scenario stayed portable
    /// depended on where the path was typed. The severity colour was written out as a literal
    /// <c>Vector4</c> twenty-six times, so "this is a warning" was a number to be matched by eye rather
    /// than something the code said.
    /// </remarks>
    public static class Fields
    {
        /// <summary>Something is set in a way that will not work, or is about to be lost.</summary>
        public static readonly Vector4 Warning = new Vector4(0.9f, 0.7f, 0.2f, 1f);

        /// <summary>Something destructive, or a state that looks like progress and is not.</summary>
        public static readonly Vector4 Alert = new Vector4(0.9f, 0.45f, 0.3f, 1f);

        /// <summary>A step that is done, a check that passed.</summary>
        public static readonly Vector4 Good = new Vector4(0.35f, 0.8f, 0.35f, 1f);

        /// <summary>
        /// A file or folder path: a picker, a clear button, and the path as text so it can be typed or pasted.
        /// </summary>
        /// <remarks>
        /// The picker's result is normalised to forward slashes. That is not cosmetic — the path is written
        /// into the <c>.wui</c>, and a backslash is a separator on Windows only, so a scenario with
        /// backslashes in it stops resolving its own files anywhere else.
        /// </remarks>
        public static void Path(string label, Func<string> get, Action<string> set,
            bool required = false, string[] filter = null, string tooltip = null)
        {
            //Labels are suffixed onto the button IDs because ImGui identifies widgets by label: two "Set"
            //buttons in one window are the same button, and clicking either drives the first.
            if (ImGui.Button("Set###set" + label))
            {
                FileBrowser.OpenSetFilePath(path => set(Normalise(path)), "Select " + label, true, filter);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear###clear" + label))
            {
                set(string.Empty);
            }
            ImGui.SameLine();

            string value = get() ?? string.Empty;
            if (ImGui.InputText(label, ref value, 256))
            {
                set(Normalise(value));
            }

            if (tooltip != null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }

            if (required && string.IsNullOrEmpty(value))
            {
                Warn("Required: " + label + " is not set.");
            }
        }

        /// <summary>A folder path. Same control; the picker is told to expect a directory.</summary>
        public static void Folder(string label, Func<string> get, Action<string> set, string tooltip = null)
        {
            if (ImGui.Button("Set###setf" + label))
            {
                FileBrowser.OpenSetFolderPath(path => set(Normalise(path)), "Select " + label);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear###clearf" + label))
            {
                set(string.Empty);
            }
            ImGui.SameLine();

            string value = get() ?? string.Empty;
            if (ImGui.InputText(label, ref value, 256))
            {
                set(Normalise(value));
            }

            if (tooltip != null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }
        }

        /// <summary>
        /// Explanation for the field just drawn, revealed on hover rather than printed under it.
        /// </summary>
        /// <remarks>
        /// This used to render the lines inline as disabled text, which put three or four lines of prose under
        /// almost every control — accurate, and so much of it that the controls were hard to find between the
        /// explanations. A hover marker keeps the reasoning one gesture away without spending the panel on it.
        ///
        /// Marked with <c>(?)</c> beside the preceding widget. Deliberately still visible as a marker: a
        /// tooltip nobody knows is there is the same as no tooltip, so the affordance stays on screen even
        /// though its content does not.
        ///
        /// Lines are passed separately rather than as one string because the caller chose where the breaks go,
        /// and a tooltip wrap position guesses worse than the author did. Wrapping is set generously so a long
        /// line still folds rather than running off the screen.
        /// </remarks>
        public static void Hint(params string[] lines)
        {
            if (lines == null || lines.Length == 0)
            {
                return;
            }

            ImGui.SameLine();
            ImGui.TextDisabled("(?)");

            if (!ImGui.IsItemHovered())
            {
                return;
            }

            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
            foreach (string line in lines)
            {
                ImGui.TextUnformatted(line);
            }
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        public static void Warn(params string[] lines)
        {
            foreach (string line in lines) ImGui.TextColored(Warning, line);
        }

        public static void Caution(params string[] lines)
        {
            foreach (string line in lines) ImGui.TextColored(Alert, line);
        }

        public static void Ok(params string[] lines)
        {
            foreach (string line in lines) ImGui.TextColored(Good, line);
        }

        /// <summary>A checkbox with its explanation attached, so the two cannot drift apart.</summary>
        public static bool Check(string label, ref bool value, string tooltip = null)
        {
            bool changed = ImGui.Checkbox(label, ref value);
            if (tooltip != null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }
            return changed;
        }

        /// <summary>An enum as a combo, taking and returning the enum rather than an index.</summary>
        /// <remarks>
        /// Matched by <b>name</b> rather than by casting the value to an index. Casting only works for an enum
        /// whose members run 0, 1, 2… in declaration order, which is true of most of them here and is not a
        /// property the compiler enforces — an enum given explicit values, or reordered, would silently start
        /// selecting the wrong entry.
        /// </remarks>
        public static bool Choice<T>(string label, ref T value, string[] names, string tooltip = null)
            where T : struct, Enum
        {
            int index = Array.IndexOf(names, value.ToString());
            if (index < 0) index = 0;

            bool changed = ImGui.Combo(label, ref index, names, names.Length);
            if (changed)
            {
                value = (T)Enum.Parse(typeof(T), names[index]);
            }

            if (tooltip != null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }
            return changed;
        }

        /// <summary>Free text, for a setting whose value is neither a path to pick nor one of a fixed set.</summary>
        public static bool Text(string label, ref string value, string tooltip = null)
        {
            value = value ?? string.Empty;
            bool changed = ImGui.InputText(label, ref value, 256);

            if (tooltip != null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }
            return changed;
        }

        /// <summary>One of a fixed set of strings as a combo, for a setting that is not modelled as an enum.</summary>
        /// <remarks>
        /// A value that is not in the list selects the first entry rather than nothing, so the control always
        /// shows something. It is not written back unless the user picks, which keeps an unexpected value from
        /// being silently replaced just because a panel was opened.
        /// </remarks>
        public static bool Choice(string label, ref string value, string[] names, string tooltip = null)
        {
            int index = Array.IndexOf(names, value);
            if (index < 0) index = 0;

            bool changed = ImGui.Combo(label, ref index, names, names.Length);
            if (changed)
            {
                value = names[index];
            }

            if (tooltip != null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }
            return changed;
        }

        /// <summary>A double.</summary>
        /// <remarks>
        /// <c>InputDouble</c>, not the <c>InputFloat</c> this first used: several of the values edited through
        /// here are like <c>1E-3</c> or <c>999999</c>, where a float round-trip visibly changes what was typed.
        /// Editing a double through a float control also means merely opening a panel and touching nothing can
        /// rewrite the scenario.
        /// </remarks>
        public static bool Real(string label, ref double value, string tooltip = null)
        {
            bool changed = ImGui.InputDouble(label, ref value);
            if (tooltip != null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }
            return changed;
        }

        /// <summary>An int.</summary>
        public static bool Whole(string label, ref int value, string tooltip = null)
        {
            bool changed = ImGui.InputInt(label, ref value);
            if (tooltip != null && ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(tooltip);
            }
            return changed;
        }

        private static string Normalise(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('\\', '/');
        }
    }
}
