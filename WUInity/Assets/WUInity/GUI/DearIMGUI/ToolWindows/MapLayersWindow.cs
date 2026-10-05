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
    /// View &gt; Map layers &gt; Fire case inputs: the fire case's input rasters - fuel model, canopy, terrain, weather (a
    /// band at a time), masks, buildings - and the LANDFIRE source layers a download left, one at a time on the map, with
    /// an opacity and a legend in real units.
    /// </summary>
    /// <remarks>
    /// <para>What a case holds was otherwise only visible in QGIS: whether the fuel came out where the town is, what CH_TIMES_10
    /// did to the canopy, which way the wind of hour 30 blows.</para>
    /// <para>Kept off the main thread and out of the frame: the list is made on a worker when the window opens, on Refresh,
    /// when another scenario is opened and when a data step or run finishes (any of which may have rewritten the case); a
    /// layer is read and coloured on a worker (<see cref="LayerRasterReader"/> caches by file and write time and
    /// decimates anything over <see cref="MaxTextureSide"/> cells a side), and the frame only uploads the finished pixels.
    /// Opacity and band apply when the slider is let go.</para>
    /// <para>Placed by each raster's own georeference, in the scenario's UTM zone, the way the result overlay and the fire
    /// grid outline are; a raster in another CRS is refused rather than drawn in the wrong place.</para>
    /// </remarks>
    public static class MapLayersWindow
    {
        /// <summary>The longest side a layer's texture is given; a larger raster is decimated to it.</summary>
        public const int MaxTextureSide = 1024;

        private static bool _isOpen;

        private static List<InputLayer> _layers = new List<InputLayer>();
        private static LayerUnits _units = new LayerUnits();
        private static volatile bool _listing;
        private static string _listedFor;
        private static bool _wasBusy;
        private static string _listNote = string.Empty;

        private static InputLayer _active;
        private static int _band = 1;
        private static float _opacity = 0.75f;
        private static volatile bool _loading;
        private static int _generation;
        private static string _status = string.Empty;

        //What is on the map now, for the legend.
        private static InputLayer _shownLayer;
        private static int _shownBand;
        private static LayerImage _shownImage;
        private static PREACT.Utility.AscRaster.Header _shownHeader;

        /// <summary>The layers of the open scenario's case as last listed (the point info reads them all).</summary>
        public static IReadOnlyList<InputLayer> Layers { get => _layers; }

        /// <summary>The namelist's units for the listed case.</summary>
        public static LayerUnits Units { get => _units; }

        /// <summary>The input layer on the map, or null.</summary>
        public static InputLayer Shown { get => IsShowing ? _shownLayer : null; }

        /// <summary>The weather band the map shows (and the point info reads) for multi-band layers.</summary>
        public static int Band { get => _band; }

        public static bool IsShowing { get => PreactGUI.WUInity?.FireDomainVisualizer?.IsInputLayerVisible ?? false; }

        public static bool IsOpen { get => _isOpen; }

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
            Relist();
        }

        public static void Hide()
        {
            ++_generation;
            _loading = false;
            PreactGUI.WUInity?.FireDomainVisualizer?.HideInputLayer();
            _shownLayer = null;
            _shownImage = null;
        }

        /// <summary>The case folder of the open scenario, absolute, or null.</summary>
        public static string CaseDirectory()
        {
            PREACTInput input = ScenarioSession.Input;
            if (input == null) return null;
            return PREACT.Utility.ElmfireCoupling.CaseDirectoryPath(input.RootFolder, input.WildfireModule?.ElmfireInput);
        }

        /// <summary>Lists the layers again on a worker. Also what the point info calls when it has none.</summary>
        public static void Relist()
        {
            PREACTInput input = ScenarioSession.Input;
            if (input == null || _listing) return;
            string root = input.RootFolder;
            string caseDir = CaseDirectory();
            string landfire = Path.Combine(root, PREACT.Tools.LandfireFuels.Folder);
            string key = ScenarioKey();
            _listing = true;
            _listNote = "Listing the case's rasters...";

            Task.Run(() =>
            {
                List<InputLayer> layers = null;
                LayerUnits units = null;
                string failure = null;
                try
                {
                    string namelist = caseDir == null ? null : Path.Combine(caseDir, "elmfire.data");
                    string[] lines = namelist != null && File.Exists(namelist) ? File.ReadAllLines(namelist) : null;
                    units = LayerUnits.FromNamelist(lines);
                    layers = InputLayerCatalog.List(caseDir, lines, landfire);
                    InputLayerCatalog.ReadBandCounts(layers);
                }
                catch (Exception e)
                {
                    failure = e.Message;
                }

                PreactGUI.Post(() =>
                {
                    _listing = false;
                    if (key != ScenarioKey()) return; //another scenario was opened meanwhile
                    _listedFor = key;
                    if (failure != null)
                    {
                        _listNote = "Could not list the case's rasters: " + failure;
                        return;
                    }
                    _layers = layers;
                    _units = units;
                    _listNote = layers.Count == 0
                        ? "The fire case has no input rasters yet: build it (workflow step 6), or download the fuels (step 4)."
                        : string.Empty;
                    //A layer that went away (a rebuild moved it) comes off the map; one still there stays.
                    if (_active != null) _active = layers.Find(l => l.Path == _active.Path);
                    if (_shownLayer != null && !layers.Exists(l => l.Path == _shownLayer.Path)) Hide();
                });
            });
        }

        private static string ScenarioKey()
        {
            return ScenarioSession.Input == null ? null : ScenarioSession.FilePath + "|" + ScenarioSession.RootFolder;
        }

        /// <summary>
        /// Keeps the list and the map in step with the scenario: another scenario clears both, and the end of a data step
        /// or a run lists again (it may have rewritten the case). Called every frame by the point info and the window;
        /// costs a comparison unless something changed.
        /// </summary>
        public static void Follow()
        {
            string key = ScenarioKey();
            if (key != _listedFor && !_listing)
            {
                if (_listedFor != null || key == null)
                {
                    Hide();
                    _layers = new List<InputLayer>();
                    _active = null;
                    PREACT.Visualization.MapLayers.LayerRasterReader.Clear();
                }
                _listedFor = key;
                if (key != null && (_isOpen || PointInfoTool.IsActive)) Relist();
            }

            bool busy = ScenarioSession.IsBusy;
            if (_wasBusy && !busy && key != null && (_isOpen || PointInfoTool.IsActive)) Relist();
            _wasBusy = busy;
        }

        /// <summary>Puts <paramref name="layer"/> (band <paramref name="band"/>) on the map, read and coloured on a worker.</summary>
        public static void Show(InputLayer layer, int band)
        {
            PREACTInput input = ScenarioSession.Input;
            if (input == null || layer == null) return;

            int generation = ++_generation;
            band = Mathf.Clamp(band, 1, Math.Max(1, layer.Bands));
            float opacity = _opacity;
            PREACT.Math.Vector2d origin = input.Simulation.Data.UTMOrigin;
            int zone = input.Simulation.Data.UtmEpsgCode;
            _loading = true;
            _status = "Reading " + layer.FileName + (layer.Bands > 1 ? ", band " + band : string.Empty) + "...";

            Task.Run(() =>
            {
                LayerRasterReader.Display display = null;
                LayerImage image = null;
                string failure = null;
                try
                {
                    display = LayerRasterReader.ReadForDisplay(layer.Path, band, MaxTextureSide);
                    image = LayerColoring.Colorize(display.Data, display.NoData, layer.Style, opacity);
                }
                catch (Exception e)
                {
                    failure = e.Message;
                }

                PreactGUI.Post(() =>
                {
                    if (generation != _generation) return; //superseded, or hidden meanwhile
                    _loading = false;
                    if (failure != null)
                    {
                        _status = "Could not show " + layer.FileName + ": " + failure;
                        return;
                    }

                    PREACT.Utility.AscRaster.Header h = display.Header;
                    if (h.EpsgCode != 0 && zone != 0 && h.EpsgCode != zone)
                    {
                        _status = $"{layer.FileName} is in EPSG:{h.EpsgCode}, not the scenario's EPSG:{zone}; not drawn, since it would "
                                  + "land in the wrong place. Warp it to the scenario's zone (the case build does) to see it here.";
                        return;
                    }
                    _status = h.EpsgCode == 0 ? layer.FileName + " states no CRS; drawn as if in the scenario's UTM zone." : string.Empty;

                    double cy = h.CellSizeY > 0.0 ? h.CellSizeY : h.CellSize;
                    var size = new PREACT.Math.Vector2d(h.Ncols * h.CellSize, h.Nrows * cy);
                    PREACT.Math.Vector2d corner = new PREACT.Math.Vector2d(h.XllCorner, h.YllCorner) - origin;
                    global::WUInity.Visualization.FireDomainVisualizerUnity visualizer = PreactGUI.WUInity?.FireDomainVisualizer;
                    if (visualizer == null)
                    {
                        _status = "There is no map to draw on yet.";
                        return;
                    }
                    PreactGUI.WUInity.ShowUTMMap();
                    if (visualizer.DisplayInputLayer(image.Rgba, image.Width, image.Height, size, corner))
                    {
                        _shownLayer = layer;
                        _shownBand = band;
                        _shownImage = image;
                        _shownHeader = h;
                    }
                });
            });
        }

        private static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            Follow();
            PreactGUI.PlaceNextWindow(new Vector2(460f, 560f));
            if (ImGui.Begin("Map layers: fire case inputs###MapLayers", ref _isOpen, PreactGUI.ToolWindowFlags))
            {
                DrawContents();
            }
            ImGui.End();

            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void DrawContents()
        {
            if (!ScenarioSession.HasInput)
            {
                ImGui.TextDisabled("No scenario is open.");
                return;
            }

            ImGui.BeginDisabled(_listing);
            if (ImGui.Button("Refresh")) Relist();
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(!IsShowing);
            if (ImGui.Button("Hide from map")) Hide();
            ImGui.EndDisabled();
            ImGui.SameLine();
            bool probe = PointInfoTool.IsActive;
            if (ImGui.Checkbox("Point info", ref probe)) PointInfoTool.SetActive(probe);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Click the map to read every input raster at that cell (Escape stops).");

            if (!string.IsNullOrEmpty(_listNote)) ImGui.TextDisabled(_listNote);

            ImGui.SetNextItemWidth(160f);
            ImGui.SliderFloat("Opacity", ref _opacity, 0.1f, 1f, "%.2f");
            if (ImGui.IsItemDeactivatedAfterEdit() && _shownLayer != null && IsShowing) Show(_shownLayer, _shownBand);

            if (_active != null && _active.Bands > 1)
            {
                int band = _band;
                ImGui.SetNextItemWidth(160f);
                ImGui.SliderInt("Band###layerband", ref band, 1, _active.Bands);
                _band = Mathf.Clamp(band, 1, _active.Bands);
                if (ImGui.IsItemDeactivatedAfterEdit()) Show(_active, _band);
                ImGui.SameLine();
                ImGui.BeginDisabled(_loading);
                if (ImGui.ArrowButton("##bandprev", ImGuiDir.Left) && _band > 1) Show(_active, --_band);
                ImGui.SameLine();
                if (ImGui.ArrowButton("##bandnext", ImGuiDir.Right) && _band < _active.Bands) Show(_active, ++_band);
                ImGui.EndDisabled();
                ImGui.TextDisabled(PREACT.Visualization.MapLayers.LayerValues.Band(_band, _active.Bands, _units.DtMeteorology)
                                   + " (the point info reads this band too)");
            }

            if (_loading) ImGui.TextDisabled(_status);
            else if (!string.IsNullOrEmpty(_status)) Fields.Warn(_status);

            float legendHeight = _shownImage != null && IsShowing ? Mathf.Min(260f, ImGui.GetContentRegionAvail().y * 0.5f) : 0f;
            ImGui.BeginChild("layerlist", new Vector2(0f, -legendHeight), (ImGuiChildFlags)1);
            string group = null;
            for (int i = 0; i < _layers.Count; ++i)
            {
                InputLayer layer = _layers[i];
                if (layer.Group != group)
                {
                    group = layer.Group;
                    ImGui.SeparatorText(group);
                }
                bool selected = IsShowing && _shownLayer != null && _shownLayer.Path == layer.Path;
                ImGui.BeginDisabled(_loading);
                if (ImGui.RadioButton(layer.Label + "###layer" + i, selected))
                {
                    //The band is kept across the weather layers, so ws and wd of the same hour can be compared.
                    if (layer.Bands > 1) _band = Mathf.Clamp(_band, 1, layer.Bands);
                    _active = layer;
                    Show(layer, layer.Bands > 1 ? _band : 1);
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(layer.Path + (layer.Note.Length > 0 ? "\n" + layer.Note : string.Empty)
                                     + (layer.Bands > 1 ? $"\n{layer.Bands} bands" : string.Empty));
                }
                ImGui.SameLine();
                ImGui.TextDisabled(layer.FileName + (layer.Bands > 1 ? $" ({layer.Bands} bands)" : string.Empty));
            }
            ImGui.EndChild();

            if (legendHeight > 0f)
            {
                ImGui.BeginChild("layerlegend", new Vector2(0f, 0f), (ImGuiChildFlags)1);
                DrawLegend();
                ImGui.EndChild();
            }
        }

        private static void DrawLegend()
        {
            InputLayer l = _shownLayer;
            LayerImage img = _shownImage;
            ImGui.TextUnformatted(l.Label + (l.Bands > 1 ? " - " + LayerValues.Band(_shownBand, l.Bands, _units.DtMeteorology) : string.Empty));
            if (l.Note.Length > 0) ImGui.TextDisabled(l.Note);
            if (img.Width != _shownHeader.Ncols || img.Height != _shownHeader.Nrows)
            {
                ImGui.TextDisabled($"Drawn at {img.Width} x {img.Height} of its {_shownHeader.Ncols} x {_shownHeader.Nrows} cells; the point info reads full cells.");
            }
            if (img.ValidCells == 0)
            {
                Fields.Warn("Every cell is nodata.");
                return;
            }

            switch (l.Style)
            {
                case LayerStyle.FuelModel40:
                case LayerStyle.FuelModel13:
                case LayerStyle.Classes:
                    FuelModelPalette.FuelSystem system = l.Style == LayerStyle.FuelModel13 ? FuelModelPalette.FuelSystem.Fbfm13 : FuelModelPalette.FuelSystem.Fbfm40;
                    foreach (KeyValuePair<int, int> c in img.Classes)
                    {
                        byte r, g, b;
                        string text;
                        if (c.Key <= 0) { r = g = b = 0; text = c.Key + " - none (transparent)"; }
                        else if (l.Style == LayerStyle.Classes) { LayerColoring.Class(c.Key, out r, out g, out b); text = "code " + c.Key; }
                        else { FuelModelPalette.Color(system, c.Key, out r, out g, out b); text = FuelModelPalette.Describe(system, c.Key); }
                        Swatch(r, g, b, c.Key > 0);
                        ImGui.SameLine();
                        ImGui.TextUnformatted($"{text}  ({100.0 * c.Value / img.ValidCells:0.#}%)");
                    }
                    if (l.Style != LayerStyle.Classes)
                    {
                        ImGui.TextDisabled("LANDFIRE's standard fuel model colours (GR9's is not LANDFIRE's: it maps none).");
                    }
                    break;
                case LayerStyle.Mask:
                    Swatch(LayerColoring.MaskColor[0], LayerColoring.MaskColor[1], LayerColoring.MaskColor[2], true);
                    ImGui.SameLine();
                    ImGui.TextUnformatted($"in the mask: {img.PositiveCells} cells ({100.0 * img.PositiveCells / img.ValidCells:0.#}%); the rest transparent");
                    break;
                case LayerStyle.Cyclic:
                    Ramp(t => { LayerColoring.Cyclic(360.0 * t, out byte r, out byte g, out byte b); return (r, g, b); });
                    ImGui.TextUnformatted("N        E        S        W        N");
                    ImGui.TextDisabled(l.IsWindDirection ? "Degrees the wind blows from, 0-360." : "Degrees the slope faces, 0-360; flat cells transparent.");
                    break;
                default:
                    Ramp(t => { LayerColoring.Sequential(t, out byte r, out byte g, out byte b); return (r, g, b); });
                    ImGui.TextUnformatted(LayerValues.Range(l, img.Min, img.Max));
                    ImGui.TextDisabled("Low (dark) to high (light), across this raster's range.");
                    break;
            }
        }

        private static void Swatch(byte r, byte g, byte b, bool filled)
        {
            float s = ImGui.GetTextLineHeight();
            Vector2 min = ImGui.GetCursorScreenPos();
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            if (filled) draw.AddRectFilled(min, min + new Vector2(s, s), ImGui.GetColorU32(new Vector4(r / 255f, g / 255f, b / 255f, 1f)));
            draw.AddRect(min, min + new Vector2(s, s), ImGui.GetColorU32(ImGuiCol.Border));
            ImGui.Dummy(new Vector2(s, s));
        }

        private static void Ramp(Func<double, (byte r, byte g, byte b)> colour)
        {
            const int segments = 32;
            float width = Mathf.Max(120f, ImGui.GetContentRegionAvail().x - 8f);
            float height = ImGui.GetTextLineHeight();
            Vector2 min = ImGui.GetCursorScreenPos();
            ImDrawListPtr draw = ImGui.GetWindowDrawList();
            for (int i = 0; i < segments; ++i)
            {
                (byte r, byte g, byte b) = colour((i + 0.5) / segments);
                Vector2 a = new Vector2(min.x + width * i / segments, min.y);
                Vector2 z = new Vector2(min.x + width * (i + 1) / segments + 1f, min.y + height);
                draw.AddRectFilled(a, z, ImGui.GetColorU32(new Vector4(r / 255f, g / 255f, b / 255f, 1f)));
            }
            ImGui.Dummy(new Vector2(width, height));
        }
    }
}
