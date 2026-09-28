using System;
using System.Collections.Generic;
using Assets.WUInity.GUI.DearIMGUI.Input;
using ImGuiNET;
using PREACT.Input;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>The pages of "All settings" that the workflow and the menus open on their own.</summary>
    public enum SettingsPage
    {
        PlaceAndTime,
        Terrain,
        Weather,
        EvacuationModules,
        Destinations,
        ResponseCurves,
        Demographics,
        EvacuationGroups,
        FireModel,
        FireBehaviour,
        TriggerBoundary,
        Smoke,
    }

    /// <summary>
    /// One settings page as a window of its own: what a workflow row or a menu item opens.
    /// </summary>
    /// <remarks>
    /// The same drawers as the tabbed "All settings" window, so there is one control per setting whichever
    /// way it is reached. Destinations, response curves, demographics and groups used to be reachable only
    /// three tabs down inside the scenario editor; each is now a window that can be docked beside the map.
    /// Several pages can be open at once, each with its own ImGui ID.
    /// </remarks>
    public static class SettingsPageWindow
    {
        private static readonly HashSet<SettingsPage> _open = new HashSet<SettingsPage>();
        private static readonly List<SettingsPage> _toClose = new List<SettingsPage>();
        private static bool _registered;
        private static bool _subscribed;

        public static bool IsOpen(SettingsPage page) => _open.Contains(page);

        public static void Open(SettingsPage page)
        {
            _open.Add(page);
            if (!_registered)
            {
                PreactGUI.DrawWindow(Draw);
                _registered = true;
            }

            if (!_subscribed)
            {
                _subscribed = true;
                //Pages draw whatever scenario is open, so they stay open across a load; only closing the
                //scenario (nothing to show) closes them.
                ScenarioSession.ScenarioChanged += () => { if (!ScenarioSession.HasInput) _open.Clear(); };
            }
        }

        public static string Title(SettingsPage page)
        {
            switch (page)
            {
                case SettingsPage.PlaceAndTime: return "Place and time";
                case SettingsPage.Terrain: return "Terrain";
                case SettingsPage.Weather: return "Weather";
                case SettingsPage.EvacuationModules: return "Evacuation modules";
                case SettingsPage.Destinations: return "Destinations";
                case SettingsPage.ResponseCurves: return "Response curves";
                case SettingsPage.Demographics: return "Demographics";
                case SettingsPage.EvacuationGroups: return "Evacuation groups";
                case SettingsPage.FireModel: return "Fire model settings";
                case SettingsPage.FireBehaviour: return "Fire behaviour (ELMFIRE namelist)";
                case SettingsPage.TriggerBoundary: return "Trigger boundary (k-PERIL)";
                case SettingsPage.Smoke: return "Smoke";
                default: return page.ToString();
            }
        }

        private static Vector2 DefaultSize(SettingsPage page)
        {
            switch (page)
            {
                case SettingsPage.FireBehaviour: return new Vector2(640f, 640f);
                case SettingsPage.FireModel: return new Vector2(620f, 600f);
                case SettingsPage.EvacuationModules: return new Vector2(560f, 460f);
                default: return new Vector2(480f, 380f);
            }
        }

        private static void Draw()
        {
            if (_open.Count == 0)
            {
                PreactGUI.CloseWindow(Draw);
                _registered = false;
                return;
            }

            _toClose.Clear();
            //A copy: a button on one page may open another, which must not change the set being walked.
            var pages = new List<SettingsPage>(_open);
            pages.Sort();
            foreach (SettingsPage page in pages)
            {
                bool open = true;
                PreactGUI.PlaceNextWindow(DefaultSize(page));
                if (ImGui.Begin(Title(page) + "###Page" + page, ref open, PreactGUI.ToolWindowFlags))
                {
                    PREACTInput input = ScenarioSession.Input;
                    if (input == null)
                    {
                        ImGui.TextDisabled("No scenario is open.");
                    }
                    else
                    {
                        //Read-only while a run or a data step is using the scenario: they read it from another
                        //thread, and a change now would be half-seen by them.
                        ImGui.BeginDisabled(ScenarioSession.EditingLocked);
                        DrawPage(page, input);
                        ImGui.EndDisabled();
                        if (ScenarioSession.EditingLocked)
                        {
                            ImGui.TextDisabled("Read-only while " + ScenarioSession.BusyReason + ".");
                        }
                    }
                }
                ImGui.End();

                if (!open)
                {
                    _toClose.Add(page);
                }
            }

            foreach (SettingsPage page in _toClose)
            {
                _open.Remove(page);
            }
        }

        public static void DrawPage(SettingsPage page, PREACTInput input)
        {
            switch (page)
            {
                case SettingsPage.PlaceAndTime:
                    SimulationInputTab.Draw(input);
                    break;
                case SettingsPage.Terrain:
                    LandscapeInputTab.Draw(input);
                    break;
                case SettingsPage.Weather:
                    WeatherInputTab.Draw(input.Weather);
                    break;
                case SettingsPage.EvacuationModules:
                    EvacuationTabs.DrawModules(input);
                    ImGui.SeparatorText("Population");
                    EvacuationTabs.DrawPopulation(input);
                    break;
                case SettingsPage.Destinations:
                    EvacuationTabs.DrawDestinations(input);
                    break;
                case SettingsPage.ResponseCurves:
                    EvacuationTabs.DrawResponseCurves(input);
                    break;
                case SettingsPage.Demographics:
                    EvacuationTabs.DrawDemographics(input);
                    break;
                case SettingsPage.EvacuationGroups:
                    EvacuationTabs.DrawGroups(input);
                    break;
                case SettingsPage.FireModel:
                    FireInputTab.Draw(input);
                    break;
                case SettingsPage.FireBehaviour:
                    FireTabs.DrawBehaviour(input);
                    break;
                case SettingsPage.TriggerBoundary:
                    TriggerBoundaryInputTab.Draw(input);
                    break;
                case SettingsPage.Smoke:
                    SmokeInputTab.Draw(input);
                    break;
            }
        }
    }
}
