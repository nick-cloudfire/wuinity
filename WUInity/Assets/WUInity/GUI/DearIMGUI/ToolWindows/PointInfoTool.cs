using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ImGuiNET;
using PREACT.Input;
using PREACT.Visualization.MapLayers;
using UnityEngine;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// View &gt; Point info: click the map and a small panel says where that is - latitude and longitude, UTM, the fire
    /// case's grid column and row - and what every input raster of the case holds there (the fuel model by code and name,
    /// canopy in real units, terrain, the weather at the band the map layers show, the masks), plus the last run's time of
    /// arrival and trigger boundary when there are any.
    /// </summary>
    /// <remarks>
    /// The pointer's position is followed every frame (arithmetic only); the rasters are read once per click, one cell
    /// each, on a worker. A click is taken only when ImGui does not want the mouse, nothing is being picked or painted, and
    /// the press did not move (a moved press pans the map). Escape, or closing the panel, ends the tool.
    /// </remarks>
    public static class PointInfoTool
    {
        private static bool _active;
        private static Vector3 _mouseDown;
        private const float ClickSlop = 6f;
        private static readonly Plane Ground = new Plane(Vector3.up, 0f);

        private static bool _haveHover;
        private static PREACT.Math.Vector2d _hover;

        private static bool _havePoint;
        private static PREACT.Math.Vector2d _point;
        private static volatile bool _reading;
        private static int _generation;
        private static Result _result;

        private sealed class Row
        {
            public string Group;
            public string Label;
            public string Value;
            public string Tooltip;
        }

        private sealed class Result
        {
            public PREACT.Math.Vector2d LatLon;
            public double UtmX, UtmY;
            public int Epsg;
            public string Grid;
            public int Band;
            public readonly List<Row> Rows = new List<Row>();
        }

        public static bool IsActive { get => _active; }

        public static void SetActive(bool active)
        {
            if (active == _active) return;
            _active = active;
            if (active)
            {
                if (!ScenarioSession.HasInput)
                {
                    _active = false;
                    PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "Point info needs an open scenario.");
                    return;
                }
                PreactGUI.WUInity?.ShowUTMMap();
                if (MapLayersWindow.Layers.Count == 0) MapLayersWindow.Relist();
                PreactGUI.DrawWindow(Draw);
            }
            else
            {
                PreactGUI.CloseWindow(Draw);
                _havePoint = _haveHover = false;
                _result = null;
                ++_generation;
            }
        }

        private static void Draw()
        {
            if (!_active) return;
            if (!ScenarioSession.HasInput)
            {
                SetActive(false);
                return;
            }
            MapLayersWindow.Follow();
            TrackPointer();
            if (!_active) return;

            DrawMarker();

            bool open = true;
            ImGuiViewportPtr viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.WorkPos + new Vector2(viewport.WorkSize.x - 20f, 40f), ImGuiCond.FirstUseEver, new Vector2(1f, 0f));
            ImGui.SetNextWindowSize(new Vector2(420f, 440f), ImGuiCond.FirstUseEver);
            if (ImGui.Begin("Point info###PointInfo", ref open, PreactGUI.ToolWindowFlags))
            {
                DrawContents();
            }
            ImGui.End();
            if (!open) SetActive(false);
        }

        /// <summary>The ground point under the pointer, in simulation coordinates; false off the UTM map or over the GUI.</summary>
        private static bool PointerOnMap(out PREACT.Math.Vector2d sim)
        {
            sim = default;
            Camera camera = Camera.main;
            if (camera == null || PreactGUI.WUInity == null || !PreactGUI.WUInity.IsUTMMapShown) return false;
            Ray ray = camera.ScreenPointToRay(UnityEngine.Input.mousePosition);
            if (!Ground.Raycast(ray, out float enter)) return false;
            Vector3 p = ray.GetPoint(enter);
            sim = new PREACT.Math.Vector2d(p.x, p.z);
            return true;
        }

        private static void TrackPointer()
        {
            ImGuiIOPtr io = ImGui.GetIO();
            bool otherTool = PreactGUI.WUInity == null || PreactGUI.WUInity.IsPicking || PreactGUI.WUInity.IsPainterActive();

            //Escape ends the tool, unless a text field has it, or a pick (whose own Escape it is) is going on.
            if (!io.WantTextInput && !otherTool && UnityEngine.Input.GetKeyDown(KeyCode.Escape))
            {
                SetActive(false);
                return;
            }

            _haveHover = !io.WantCaptureMouse && PointerOnMap(out _hover);

            if (UnityEngine.Input.GetMouseButtonDown(0)) _mouseDown = UnityEngine.Input.mousePosition;
            bool click = UnityEngine.Input.GetMouseButtonUp(0) && !io.WantCaptureMouse && !otherTool
                         && (UnityEngine.Input.mousePosition - _mouseDown).sqrMagnitude < ClickSlop * ClickSlop;
            if (click && _haveHover)
            {
                Probe(_hover);
            }
        }

        /// <summary>Reads every input layer (and the last run's results) at <paramref name="sim"/>, on a worker.</summary>
        private static void Probe(PREACT.Math.Vector2d sim)
        {
            PREACTInput input = ScenarioSession.Input;
            if (input == null) return;

            _havePoint = true;
            _point = sim;
            int generation = ++_generation;
            _reading = true;

            PREACT.Math.Vector2d utm = sim + input.Simulation.Data.UTMOrigin;
            PREACT.Math.Vector2d latLon = input.Simulation.Data.GetWGS84FromSimulationPosition(sim);
            int zone = input.Simulation.Data.UtmEpsgCode;
            int band = MapLayersWindow.Band;
            double dt = MapLayersWindow.Units.DtMeteorology;
            var layers = new List<InputLayer>(MapLayersWindow.Layers);
            string caseDir = MapLayersWindow.CaseDirectory();
            InputLayer demLayer = layers.Find(l => l.Group == InputLayerCatalog.CaseGroup && l.Key == "DEM_FILENAME");
            string dem = demLayer != null ? demLayer.Path : caseDir == null ? null : Path.Combine(caseDir, "inputs", "dem.tif");
            string arrival = ResultsWindow.NewestRasterPath(ResultsWindow.Kind.FireArrival);
            string boundary = ResultsWindow.NewestRasterPath(ResultsWindow.Kind.TriggerBoundary);

            Task.Run(() =>
            {
                var r = new Result { LatLon = latLon, UtmX = utm.x, UtmY = utm.y, Epsg = zone, Band = band };
                try
                {
                    r.Grid = "no case grid (dem.tif) yet";
                    if (dem != null && File.Exists(dem))
                    {
                        LayerRasterReader.Sample g = LayerRasterReader.SampleAt(dem, 1, utm.x, utm.y);
                        r.Grid = g.Inside ? $"col {g.Col}, row {g.Row} (from the north-west, 0-based)" : "outside the case grid";
                    }

                    foreach (InputLayer layer in layers)
                    {
                        var row = new Row { Group = layer.Group, Label = layer.Label, Tooltip = layer.Path };
                        try
                        {
                            int b = layer.Bands > 1 ? Math.Min(band, layer.Bands) : 1;
                            LayerRasterReader.Sample s = LayerRasterReader.SampleAt(layer.Path, b, utm.x, utm.y);
                            if (s.EpsgCode != 0 && zone != 0 && s.EpsgCode != zone) row.Value = $"not read: in EPSG:{s.EpsgCode}";
                            else if (!s.Inside) row.Value = "outside this raster";
                            else row.Value = LayerValues.Describe(layer, s.Value, s.NoData);
                            if (layer.Bands > 1) row.Label += " [" + LayerValues.Band(b, layer.Bands, dt) + "]";
                            if (s.Inside) row.Tooltip += $"\ncell col {s.Col}, row {s.Row}; stored value {s.Value:G6}"
                                                         + (layer.Scale != 1.0 ? $" x {layer.Scale:G3}" : string.Empty)
                                                         + (layer.Note.Length > 0 ? "\n" + layer.Note : string.Empty);
                        }
                        catch (Exception e)
                        {
                            row.Value = "could not read: " + e.Message;
                        }
                        r.Rows.Add(row);
                    }

                    AddResult(r, "Time of arrival", arrival, utm, (v, nd) => LayerValues.ArrivalTime(v, nd));
                    AddResult(r, "Trigger boundary", boundary, utm, (v, nd) => LayerValues.TriggerBoundary(v, nd));
                }
                catch (Exception e)
                {
                    r.Rows.Add(new Row { Group = "Error", Label = "Reading", Value = e.Message, Tooltip = string.Empty });
                }

                PreactGUI.Post(() =>
                {
                    if (generation != _generation) return;
                    _reading = false;
                    _result = r;
                });
            });
        }

        private static void AddResult(Result r, string label, string path, PREACT.Math.Vector2d utm, Func<double, double, string> describe)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            var row = new Row { Group = "Last run", Label = label, Tooltip = path };
            try
            {
                LayerRasterReader.Sample s = LayerRasterReader.SampleAt(path, 1, utm.x, utm.y);
                row.Value = s.Inside ? describe(s.Value, s.NoData) : "outside this raster";
            }
            catch (Exception e)
            {
                row.Value = "could not read: " + e.Message;
            }
            r.Rows.Add(row);
        }

        private static void DrawContents()
        {
            PREACTInput input = ScenarioSession.Input;
            if (_haveHover && input != null)
            {
                PREACT.Math.Vector2d ll = input.Simulation.Data.GetWGS84FromSimulationPosition(_hover);
                PREACT.Math.Vector2d utm = _hover + input.Simulation.Data.UTMOrigin;
                ImGui.TextDisabled($"Pointer: {ll.x:0.00000}, {ll.y:0.00000}  |  UTM {utm.x:0} E, {utm.y:0} N");
            }
            else
            {
                ImGui.TextDisabled(PreactGUI.WUInity != null && PreactGUI.WUInity.IsUTMMapShown
                    ? "Pointer: over the GUI." : "Pointer: the world map is shown; points are read on the scenario's map.");
            }

            if (!_havePoint)
            {
                ImGui.TextWrapped("Click the map to read the case's rasters at that point. Escape or closing this panel stops.");
                if (MapLayersWindow.Layers.Count == 0) ImGui.TextDisabled("(The case has no input rasters listed yet.)");
                return;
            }
            if (_reading) ImGui.TextDisabled("Reading...");
            Result r = _result;
            if (r == null) return;

            ImGui.SeparatorText("Point");
            ImGui.TextUnformatted($"Lat / lon   {r.LatLon.x:0.000000}, {r.LatLon.y:0.000000}");
            ImGui.TextUnformatted($"UTM         {r.UtmX:0.0} E, {r.UtmY:0.0} N" + (r.Epsg != 0 ? $" (EPSG:{r.Epsg})" : string.Empty));
            ImGui.TextUnformatted("Case grid   " + r.Grid);
            if (ImGui.SmallButton("Copy"))
            {
                GUIUtility.systemCopyBuffer = Copy(r);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Copies the point and every value as text.");

            string group = null;
            if (ImGui.BeginTable("pointinfo", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                foreach (Row row in r.Rows)
                {
                    if (row.Group != group)
                    {
                        group = row.Group;
                        ImGui.TableNextRow();
                        ImGui.TableSetColumnIndex(0);
                        ImGui.TextDisabled(group);
                    }
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    ImGui.TextUnformatted(row.Label);
                    if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(row.Tooltip)) ImGui.SetTooltip(row.Tooltip);
                    ImGui.TableSetColumnIndex(1);
                    ImGui.TextWrapped(row.Value);
                }
                ImGui.EndTable();
            }
        }

        private static string Copy(Result r)
        {
            var b = new System.Text.StringBuilder();
            b.AppendLine(FormattableString.Invariant($"lat/lon\t{r.LatLon.x:0.000000}, {r.LatLon.y:0.000000}"));
            b.AppendLine(FormattableString.Invariant($"UTM\t{r.UtmX:0.0} E, {r.UtmY:0.0} N (EPSG:{r.Epsg})"));
            b.AppendLine("case grid\t" + r.Grid);
            foreach (Row row in r.Rows) b.AppendLine(row.Label + "\t" + row.Value);
            return b.ToString();
        }

        /// <summary>A ring where the point was read, drawn over the map (not over the windows).</summary>
        private static void DrawMarker()
        {
            Camera camera = Camera.main;
            if (!_havePoint || camera == null || PreactGUI.WUInity == null || !PreactGUI.WUInity.IsUTMMapShown) return;
            Vector3 screen = camera.WorldToScreenPoint(new Vector3((float)_point.x, 3f, (float)_point.y));
            if (screen.z <= 0f) return;
            ImGuiIOPtr io = ImGui.GetIO();
            float sx = Screen.width > 0 ? io.DisplaySize.x / Screen.width : 1f;
            float sy = Screen.height > 0 ? io.DisplaySize.y / Screen.height : 1f;
            Vector2 at = new Vector2(screen.x * sx, (Screen.height - screen.y) * sy);
            ImDrawListPtr draw = ImGui.GetBackgroundDrawList();
            draw.AddCircle(at, 7f, ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.9f)), 16, 4f);
            draw.AddCircle(at, 7f, ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f)), 16, 2f);
        }
    }
}
