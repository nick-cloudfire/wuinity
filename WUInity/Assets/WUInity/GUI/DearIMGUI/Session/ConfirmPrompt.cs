using System;
using ImGuiNET;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The one modal question the GUI asks: "there are unsaved changes (or work running) - what now?"
    /// </summary>
    /// <remarks>
    /// Loading, creating, closing and quitting all discarded unsaved work silently; nine different actions
    /// ended with "save the scenario to keep it" and nothing asked before that work was thrown away. Every
    /// one of those paths now goes through <see cref="AskToSave"/> (or <see cref="AskToConfirm"/> for
    /// something running), which keeps what was about to happen and does it once the question is answered.
    ///
    /// Drawn once per frame from the GUI's layout, after every window, so it sits on top of them.
    /// </remarks>
    public static class ConfirmPrompt
    {
        private const string PopupId = "Please confirm###ConfirmPrompt";

        private static bool _pending;
        private static bool _openRequested;
        private static string _message = string.Empty;
        private static string _primaryLabel = string.Empty;
        private static string _secondaryLabel = string.Empty;
        private static Action _onPrimary, _onSecondary, _onCancel;

        public static bool IsOpen { get => _pending; }

        /// <summary>
        /// Asks whether to save before <paramref name="what"/>, then does <paramref name="proceed"/> - after
        /// saving, or without. Proceeds at once when nothing is unsaved.
        /// </summary>
        public static void AskToSave(string what, Action proceed, Action cancelled = null)
        {
            if (!ScenarioSession.IsDirty)
            {
                proceed?.Invoke();
                return;
            }

            Ask($"\"{ScenarioSession.DisplayName}\" has unsaved changes ({ScenarioSession.DirtySummary}).\n\n"
                + $"Save them before {what}?",
                "Save", () =>
                {
                    if (ScenarioSession.Save())
                    {
                        proceed?.Invoke();
                    }
                },
                "Don't save", proceed, cancelled);
        }

        /// <summary>A yes/cancel question, for things that cannot be undone.</summary>
        public static void AskToConfirm(string message, string confirmLabel, Action confirmed, Action cancelled = null)
        {
            Ask(message, confirmLabel, confirmed, null, null, cancelled);
        }

        private static void Ask(string message, string primary, Action onPrimary, string secondary,
            Action onSecondary, Action onCancel)
        {
            //A second question while one is showing replaces it; the first one's caller is told it was
            //cancelled, so nothing is left waiting for an answer it will never get.
            if (_pending)
            {
                Action previousCancel = _onCancel;
                _pending = false;
                previousCancel?.Invoke();
            }

            _message = message;
            _primaryLabel = primary;
            _secondaryLabel = secondary;
            _onPrimary = onPrimary;
            _onSecondary = onSecondary;
            _onCancel = onCancel;
            _pending = true;
            _openRequested = true;
        }

        public static void Draw()
        {
            if (!_pending)
            {
                return;
            }

            if (_openRequested)
            {
                ImGui.OpenPopup(PopupId);
                _openRequested = false;
            }

            ImGuiViewportPtr viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.WorkPos + 0.5f * viewport.WorkSize, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
            ImGui.SetNextWindowSize(new Vector2(460f, 0f), ImGuiCond.Appearing);

            //The title bar's close button counts as Cancel.
            bool open = true;
            if (!ImGui.BeginPopupModal(PopupId, ref open, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoSavedSettings))
            {
                //Closed some other way: the same as Cancel.
                Finish(_onCancel);
                return;
            }

            if (!open)
            {
                ImGui.CloseCurrentPopup();
                ImGui.EndPopup();
                Finish(_onCancel);
                return;
            }

            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
            ImGui.TextUnformatted(_message);
            ImGui.PopTextWrapPos();
            ImGui.Separator();

            Action chosen = null;
            bool answered = false;

            if (ImGui.Button(_primaryLabel + "###ConfirmPrimary"))
            {
                chosen = _onPrimary;
                answered = true;
            }

            if (!string.IsNullOrEmpty(_secondaryLabel))
            {
                ImGui.SameLine();
                if (ImGui.Button(_secondaryLabel + "###ConfirmSecondary"))
                {
                    chosen = _onSecondary;
                    answered = true;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel###ConfirmCancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                chosen = _onCancel;
                answered = true;
            }

            if (answered)
            {
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndPopup();

            if (answered)
            {
                Finish(chosen);
            }
        }

        private static void Finish(Action then)
        {
            _pending = false;
            _onPrimary = _onSecondary = _onCancel = null;
            then?.Invoke();
        }
    }
}
