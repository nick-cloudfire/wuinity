using System;
using ImGuiNET;
using PREACT.Input;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Place and time: the scenario's name, its area of interest, when it happens, and the map it is shown on.
    /// </summary>
    /// <remarks>
    /// The map provider and zoom used to be a tab of their own with two fields in it. Picking the area on the
    /// map was only possible while creating a scenario, and changing it here by typing moved the simulation
    /// grid without redrawing the map, the markers, the border or the camera, and without saying that every
    /// prepared file was now for another place.
    /// </remarks>
    public static class SimulationInputTab
    {
        private static Vector2 _latLon, _domainSize, _newLatLon, _newDomainSize;
        private static bool _redefinePrefilled;
        private static string _domainNote = string.Empty;

        private static readonly string[] MapProviderStrings = Enum.GetNames(typeof(MapInput.MapServiceProvider));

        public static void Draw(PREACTInput scenario)
        {
            SimulationInput input = scenario.Simulation;

            ImGui.SeparatorText("Name");
            ImGui.InputText(nameof(input.Name), ref input.Name, 128);
            Fields.Hint("Every file the data steps prepare is named after this, so renaming a scenario that has",
                        "prepared data makes the steps look for files that are not there. Rename early.");

            ImGui.SeparatorText("Area of interest");

            //Shown disabled, because these are a readout rather than an input: changing the domain moves the
            //whole simulation grid, so it goes through the picker or "Type it in" below.
            _latLon.x = (float)input.LowerLeftLatLon.x;
            _latLon.y = (float)input.LowerLeftLatLon.y;
            _domainSize.x = (float)input.DomainSize.x;
            _domainSize.y = (float)input.DomainSize.y;

            ImGui.BeginDisabled();
            ImGui.InputFloat2(nameof(input.LowerLeftLatLon), ref _latLon);
            ImGui.InputFloat2(nameof(input.DomainSize) + " (m)", ref _domainSize);
            ImGui.EndDisabled();

            if (ImGui.Button("Pick on map"))
            {
                PickOnMap(scenario);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Shows the world map; click two opposite corners. Escape brings you back without changing anything.");
            }

            if (ImGui.TreeNode("Type it in###RedefineDomain"))
            {
                //Seeded from the current domain the first time this opens. They used to start at zero, so
                //pressing Apply without typing anything moved the domain to lat/lon 0,0.
                if (!_redefinePrefilled)
                {
                    _newLatLon = _latLon;
                    _newDomainSize = _domainSize;
                    _redefinePrefilled = true;
                }

                ImGui.InputFloat2("New " + nameof(input.LowerLeftLatLon), ref _newLatLon);
                ImGui.InputFloat2("New " + nameof(input.DomainSize) + " (m)", ref _newDomainSize);

                //A zero-sized domain is never meaningful and leaves the simulation unusable.
                bool validDomain = _newDomainSize.x > 0f && _newDomainSize.y > 0f;
                ImGui.BeginDisabled(!validDomain);
                if (ImGui.Button("Apply"))
                {
                    SetDomain(scenario, new PREACT.Math.Vector2d(_newLatLon.x, _newLatLon.y),
                        new PREACT.Math.Vector2d(_newDomainSize.x, _newDomainSize.y));
                }
                ImGui.EndDisabled();
                if (!validDomain)
                {
                    ImGui.TextDisabled("Domain size must be greater than zero.");
                }

                ImGui.TreePop();
            }
            else
            {
                //Re-seed next time it opens, so it reflects the domain as it stands then.
                _redefinePrefilled = false;
            }

            if (!string.IsNullOrEmpty(_domainNote))
            {
                Fields.Warn(_domainNote);
            }

            ImGui.SeparatorText("Time");
            CustomTypes.InputDateTimePopup(nameof(input.StartDateTime), ref input.StartDateTime);
            CustomTypes.InputDateTimePopup(nameof(input.EndDateTime), ref input.EndDateTime);
            if (input.EndDateTime <= input.StartDateTime)
            {
                Fields.Warn("The simulation has to end after it starts.");
            }
            ImGui.SetNextItemWidth(120f);
            ImGui.InputFloat(nameof(input.DeltaTime) + " (s)", ref input.DeltaTime);
            ImGui.Checkbox(nameof(input.StopWhenEvacuated), ref input.StopWhenEvacuated);
            Fields.Hint("End the run as soon as everyone has reached a destination, rather than at EndDateTime.");

            ImGui.SeparatorText("Map");
            MapInput map = scenario.Map;
            int provider = Array.IndexOf(MapProviderStrings, map.MapProvider.ToString());
            if (provider < 0) provider = 0;
            if (ImGui.Combo(nameof(map.MapProvider), ref provider, MapProviderStrings, MapProviderStrings.Length))
            {
                map.MapProvider = (MapInput.MapServiceProvider)Enum.Parse(typeof(MapInput.MapServiceProvider), MapProviderStrings[provider]);
            }
            ImGui.SliderInt(nameof(map.ZoomLevel), ref map.ZoomLevel, 0, 20);
            Fields.Hint("Takes effect the next time the scenario is opened, or the area is changed.");
        }

        /// <summary>Picks the area on the world map, then comes back to the scenario - also when the pick is abandoned.</summary>
        private static void PickOnMap(PREACTInput scenario)
        {
            PreactGUI.WUInity.ShowWebMercatorMap();
            PreactGUI.WUInity.PickBoundingBoxOnMap(corners =>
            {
                PREACT.Math.Vector2d ll = new PREACT.Math.Vector2d(Math.Min(corners[0].x, corners[1].x), Math.Min(corners[0].y, corners[1].y));
                PREACT.Math.Vector2d ur = new PREACT.Math.Vector2d(Math.Max(corners[0].x, corners[1].x), Math.Max(corners[0].y, corners[1].y));
                SetDomain(scenario, ll, global::WUInity.Workflow.NewScenarioFactory.MeasureDomain(ll, ur));
            },
            //Abandoned: nothing moved, so back to the scenario's map without resetting the painter.
            () => PreactGUI.WUInity.RestoreScenarioMap());
        }

        private static void SetDomain(PREACTInput scenario, PREACT.Math.Vector2d lowerLeft, PREACT.Math.Vector2d size)
        {
            bool hadData = GuiFiles.Exists(GuiFiles.Resolve(scenario.RootFolder, scenario.Population.PopulationFile))
                           || System.IO.Directory.Exists(System.IO.Path.Combine(scenario.RootFolder, global::WUInity.Workflow.ScenarioFiles.CaseDirectory(scenario)));

            scenario.Simulation.LowerLeftLatLon = lowerLeft;
            scenario.Simulation.DomainSize = size;

            //Everything drawn in simulation coordinates moves with the origin: the map tiles, the markers, the
            //border, the camera, the painter's grid and the road network.
            PreactGUI.WUInity.RefreshScenarioView(sameScenario: true);
            ScenarioSession.NotifyEdited("area of interest");

            _domainNote = hadData
                ? "The area changed. Data prepared for the old area - roads, population, the fire case - is still on disk and "
                  + "no longer matches; rebuild it (the workflow's steps 2 to 5)."
                : string.Empty;
        }
    }
}
