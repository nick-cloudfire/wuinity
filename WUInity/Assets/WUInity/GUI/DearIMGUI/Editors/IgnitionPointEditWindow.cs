using ImGuiNET;
using PREACT;
using PREACT.Math;
using PREACT.Wildfire;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI.Editors
{
    /// <summary>
    /// Places one ignition point on the map and keeps it in the scenario.
    ///
    /// There was no supported way to do this: the points existed as a type and were drawn as markers, but
    /// they could only arrive from a CSV named by the legacy <c>[ELMFIRE] IgnitionPointsFile</c>, and only
    /// for that one module. So an ELMFIRE case had to be given its ignition by editing the namelist, which
    /// is where the Mati case's ignition acquired zone-35 coordinates in a zone-34 case: a point half a
    /// zone outside its own domain, which ELMFIRE ran without complaint.
    ///
    /// A point is held here in latitude and longitude for that reason. It is measured in the case's own
    /// CRS at case-build time, from the grid that case actually has, and nowhere else.
    /// </summary>
    public static class IgnitionPointEditWindow
    {
        private static bool _isOpen;
        private static List<IgnitionPointInput> _points;
        private static IgnitionPointInput _point;
        private static int _index = -1;

        private static bool _snapToCell = true;
        private static string _snapDescription = string.Empty;
        private static string _absoluteText = string.Empty;

        /// <summary>Opens the editor for an existing point, or for a new one when <paramref name="index"/> is negative.</summary>
        public static void Open(List<IgnitionPointInput> points, int index)
        {
            Reopen();

            _points = points;
            _index = index;

            if (index >= 0 && index < points.Count)
            {
                _point = points[index];
            }
            else
            {
                //In the middle of the domain, which is somewhere to start from that is at least on the
                //map - a point defaulting to 0,0 opens the editor in the Gulf of Guinea.
                _point = new IgnitionPointInput(DomainCentre(), false, 0f, StartDateTime());
                _index = -1;
            }

            _absoluteText = _point.IgnitionDateTime.ToString("yyyy-MM-ddTHH:mm:ss");
            _snapDescription = string.Empty;
        }

        /// <summary>
        /// Puts the window back on screen without touching the point being edited.
        ///
        /// Distinct from <see cref="Open"/>, which loads the point from the list: the window closes itself
        /// while the map is being clicked, so reopening it through Open would read the point back from the
        /// list and discard the position that was just picked.
        /// </summary>
        private static void Reopen()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
        }

        public static void Close()
        {
            if (_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
            _isOpen = false;
        }

        public static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            ImGui.Begin("Ignition point editor", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            if (ImGui.Button("Set on map"))
            {
                Close();
                PreactGUI.WUInity.PickPosOnMap(SetIgnitionPos);
            }
            ImGui.SameLine();
            //Only written back when the field was actually edited: a float2 holds about a metre of latitude
            //at these magnitudes, which is enough to walk a snapped point out of its cell.
            Vector2 latLon = new Vector2((float)_point.LatLon.x, (float)_point.LatLon.y);
            if (ImGui.InputFloat2("LatLon", ref latLon))
            {
                _point.LatLon = new Vector2d(latLon.x, latLon.y);
                _snapDescription = string.Empty;
            }

            ImGui.Checkbox("Snap to the fire grid cell centre when placed", ref _snapToCell);
            ImGui.SameLine();
            if (ImGui.Button("Snap now"))
            {
                SnapToCellCentre(true);
            }

            if (!string.IsNullOrEmpty(_snapDescription))
            {
                ImGui.TextDisabled(_snapDescription);
            }

            ImGui.SeparatorText("When it starts");

            bool absolute = _point.AbsoluteTime;
            if (ImGui.Checkbox("Give a date and time rather than seconds from the start", ref absolute))
            {
                _point.AbsoluteTime = absolute;
                //Kept as the same moment across the switch, so ticking the box does not move the ignition.
                if (absolute)
                {
                    _point.IgnitionDateTime = StartDateTime().AddSeconds(_point.IgnitionTime);
                    _absoluteText = _point.IgnitionDateTime.ToString("yyyy-MM-ddTHH:mm:ss");
                }
                else
                {
                    _point.IgnitionTime = (float)(_point.IgnitionDateTime - StartDateTime()).TotalSeconds;
                }
            }

            if (_point.AbsoluteTime)
            {
                if (ImGui.InputText("IgnitionDateTime (yyyy-MM-ddTHH:mm:ss)", ref _absoluteText, 32))
                {
                    if (DateTime.TryParse(_absoluteText, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out DateTime parsed))
                    {
                        _point.IgnitionDateTime = parsed;
                        _point.IgnitionTime = (float)(parsed - StartDateTime()).TotalSeconds;
                    }
                }
                ImGui.TextDisabled($"{_point.IgnitionTime:F0} s from the scenario start ({StartDateTime():yyyy-MM-dd HH:mm}).");
                if (_point.IgnitionTime < 0f)
                {
                    ImGui.TextColored(new Vector4(0.9f, 0.7f, 0.2f, 1f),
                        "That is before the scenario starts, so the fire would already be burning.");
                }
            }
            else
            {
                float seconds = _point.IgnitionTime;
                if (ImGui.InputFloat("IgnitionTime (s from the scenario start)", ref seconds))
                {
                    _point.IgnitionTime = seconds;
                    _point.IgnitionDateTime = StartDateTime().AddSeconds(seconds);
                }
            }

            ImGui.Separator();
            ImGui.TextWrapped("The point is measured in the ELMFIRE case's own coordinate system when the case "
                + "is built, from that case's grid - so a domain on a UTM zone boundary cannot end up with its "
                + "ignition in the neighbouring zone.");

            if (ImGui.Button("OK"))
            {
                Commit();
            }

            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void Commit()
        {
            if (_index >= 0 && _index < _points.Count)
            {
                //IgnitionPointInput is a struct, so the edited copy has to be put back rather than assumed
                //to have mutated in place.
                _points[_index] = _point;
            }
            else
            {
                _points.Add(_point);
            }

            _isOpen = false;
            //Or the marker stays where the point used to be, which reads as an edit that did not take.
            PreactGUI.WUInity.RefreshWildfireIgnitionMarkers();
            Engine.Message(null, Engine.LogType.Log, "Ignition point set. Save the scenario to keep it.");
        }

        private static void SetIgnitionPos(Vector2d simulationPos)
        {
            _point.LatLon = ScenarioEditorWindow.Input.Simulation.Data.GetWGS84FromSimulationPosition(simulationPos);
            _snapDescription = string.Empty;

            if (_snapToCell)
            {
                //Quietly when it works - a click moved to the middle of its own cell does not need
                //announcing - but reported when the click was off the grid, since then it stands as clicked.
                SnapToCellCentre(false);
            }

            Reopen();
        }

        /// <summary>
        /// Moves the point to the middle of the fire grid cell it falls in, and says which cell.
        /// </summary>
        private static void SnapToCellCentre(bool reportWhenAlreadyCentred)
        {
            if (!ScenarioEditorWindow.HasInput)
            {
                _snapDescription = "No scenario is loaded, so there is no grid to snap to.";
                return;
            }

            PREACT.Input.SimulationData data = ScenarioEditorWindow.Input.Simulation.Data;
            Vector2d simulationPos = data.GetSimulationPosition(_point.LatLon);

            if (!PreactGUI.WUInity.TrySnapToFireGridCell(simulationPos, out Vector2d snapped,
                    out Vector2int cell, out double cellSize))
            {
                //The reason is logged by the painter, which knows whether the grid is missing or the point
                //is off it. Said here too, because this is where it matters.
                _snapDescription = "Not on the fire grid - see the console. The point stands as placed.";
                return;
            }

            double moved = Mathd.Sqrt((snapped.x - simulationPos.x) * (snapped.x - simulationPos.x)
                                      + (snapped.y - simulationPos.y) * (snapped.y - simulationPos.y));

            _point.LatLon = data.GetWGS84FromSimulationPosition(snapped);
            _snapDescription = $"Fire grid cell {cell.x}, {cell.y} ({cellSize:F0} m)"
                               + (reportWhenAlreadyCentred || moved >= 1.0 ? $", moved {moved:F1} m." : ".");
        }

        private static DateTime StartDateTime()
        {
            return ScenarioEditorWindow.HasInput
                ? ScenarioEditorWindow.Input.Simulation.StartDateTime
                : DateTime.Now;
        }

        private static Vector2d DomainCentre()
        {
            if (!ScenarioEditorWindow.HasInput)
            {
                return new Vector2d(0.0, 0.0);
            }

            PREACT.Input.SimulationInput simulation = ScenarioEditorWindow.Input.Simulation;
            return simulation.Data.GetWGS84FromSimulationPosition(
                new Vector2d(0.5 * simulation.DomainSize.x, 0.5 * simulation.DomainSize.y));
        }
    }
}
