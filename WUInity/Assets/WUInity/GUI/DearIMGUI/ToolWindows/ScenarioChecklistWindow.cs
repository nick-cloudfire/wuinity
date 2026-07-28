using ImGuiNET;
using PREACT.Input;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// What a scenario still needs, shown when one is opened.
    ///
    /// A scenario is assembled over several sittings and its parts arrive in whatever order they are
    /// produced, so half-finished is the normal state rather than an error. Reading the outstanding
    /// items off a checklist beats reconstructing them from log lines that scroll past among the
    /// hundred or so ordinary messages a load produces.
    /// </summary>
    public static class ScenarioChecklistWindow
    {
        private static bool _isOpen;
        private static bool _onlyOutstanding = true;

        //Snapshotted on open: PREACTInput.Requirements is rebuilt by the next load, and the window
        //should keep describing the scenario it was opened for.
        private static readonly List<PREACTInput.InputRequirement> _items = new List<PREACTInput.InputRequirement>();
        private static string _scenario = string.Empty;

        /// <summary>
        /// Called after a load. Opens itself only when something is outstanding, so a complete
        /// scenario does not have to be dismissed every time it is opened.
        /// </summary>
        public static void ShowFor(string scenarioName)
        {
            _items.Clear();
            _items.AddRange(PREACTInput.Requirements);
            _scenario = scenarioName;

            bool anyCritical = false;
            for (int i = 0; i < _items.Count; ++i)
            {
                if (_items[i].Critical) { anyCritical = true; break; }
            }

            if (anyCritical)
            {
                Open();
            }
        }

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
        }

        public static bool HasItems { get => _items.Count > 0; }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.Begin("Scenario checklist", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            int required = 0;
            int optional = 0;
            for (int i = 0; i < _items.Count; ++i)
            {
                if (_items[i].Critical) ++required; else ++optional;
            }

            ImGui.Text(_scenario);

            if (required == 0)
            {
                ImGui.TextColored(new Vector4(0.35f, 0.8f, 0.35f, 1f), "Everything required is set; this scenario can run.");
            }
            else
            {
                ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.3f, 1f), $"{required} item(s) still required before this scenario can run.");
                //The point of loading anyway: the scenario is editable and saveable meanwhile.
                ImGui.TextWrapped("It is still fully editable, and saving keeps whatever you have done. The items below are what a run would need.");
            }

            if (optional > 0)
            {
                ImGui.TextDisabled($"{optional} optional item(s) fell back to defaults.");
            }

            ImGui.Separator();
            ImGui.Checkbox("Only show what is required", ref _onlyOutstanding);

            ImGui.BeginChild("checklist", new Vector2(0, 260), (ImGuiChildFlags)1);

            string section = null;
            for (int i = 0; i < _items.Count; ++i)
            {
                PREACTInput.InputRequirement item = _items[i];
                if (_onlyOutstanding && !item.Critical)
                {
                    continue;
                }

                //Grouped under the section they came from, which is also where they are edited.
                if (item.Section != section)
                {
                    section = item.Section;
                    ImGui.SeparatorText(string.IsNullOrEmpty(section) ? "Scenario" : section);
                }

                if (item.Critical)
                {
                    ImGui.TextColored(new Vector4(0.9f, 0.45f, 0.3f, 1f), "[required]");
                }
                else
                {
                    ImGui.TextDisabled("[default]");
                }
                ImGui.SameLine();
                ImGui.TextWrapped($"{item.Key} - {item.Message}");
            }

            if (required == 0 && _onlyOutstanding)
            {
                ImGui.TextDisabled("Nothing outstanding.");
            }

            ImGui.EndChild();

            ImGui.Separator();
            if (ImGui.Button("Open scenario editor"))
            {
                ScenarioEditorWindow.Open();
            }
            ImGui.SameLine();
            if (ImGui.Button("Close"))
            {
                _isOpen = false;
            }

            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }
    }
}
