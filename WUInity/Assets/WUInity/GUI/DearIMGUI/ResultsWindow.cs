using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ImGuiNET;
using PREACT.Input;
using UnityEngine;
using WUInity.Visualization;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// Results: what the last run and the last campaign wrote, and a way to put each raster on the map.
    /// Workflow step 12.
    /// </summary>
    /// <remarks>
    /// There was no results view at all. The trigger boundary, the campaign's probability raster and the
    /// ensemble statistics were only files in _output - the campaign's own status line said so - and
    /// <c>FireDomainVisualizerUnity.DisplayTriggerBuffer</c>, written for exactly this, had no caller.
    /// </remarks>
    public static class ResultsWindow
    {
        private static bool _isOpen;

        /// <summary>What a result file is, which decides how it is drawn and where it is listed.</summary>
        public enum Kind { RunLog, Arrivals, TriggerBoundary, TriggerProbability, BurnProbability, ArrivalStatistic, Convergence, FireArrival, WuiArea, Other }

        private sealed class Entry
        {
            public string Path;
            public string Name;
            public Kind Kind;
            public long Bytes;
            public DateTime Written;
            public bool IsRaster;

            /// <summary>
            /// A trigger boundary with no .prj beside it: every boundary since the k-PERIL fix has one, so this one was
            /// written before it, by a wrapper that read the fire's grids the wrong way round.
            /// </summary>
            public bool Earlier;
        }

        private static readonly List<Entry> _entries = new List<Entry>();
        private static string _scannedRoot;
        private static float _nextScan;

        private static string _shown;
        private static string _legend = string.Empty;
        private static volatile bool _loading;
        private static string _status = string.Empty;

        //Why the listed campaign's results are not to be trusted, when it was made by an earlier version; else null.
        private static string _campaignNote;

        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;
            Rescan();
        }

        /// <summary>Lists the result files again (after a run, or when asked).</summary>
        public static void Rescan()
        {
            _entries.Clear();
            _campaignNote = null;
            _scannedRoot = ScenarioSession.RootFolder;
            _nextScan = UnityEngine.Time.realtimeSinceStartup + 5f;

            PREACTInput input = ScenarioSession.Input;
            if (input == null) return;

            string output = Path.Combine(input.RootFolder, "_output");
            string boundaryName = input.TriggerBufferModule?.kPERILInput?.OutputName;
            string name = input.Simulation.Name.ToLowerInvariant();

            //A campaign keeps its results in its own folder; only a campaign from before that wrote them into
            //_output itself, and those are listed only when there is no newer campaign folder.
            string campaign = ScenarioWorkflow.CampaignFolderOf(input, ScenarioSession.FilePath);
            bool legacyCampaign = campaign == null || SameFolder(campaign, output);

            try
            {
                if (Directory.Exists(output))
                {
                    foreach (string f in Directory.GetFiles(output))
                    {
                        string lower = Path.GetFileName(f).ToLowerInvariant();
                        if (!legacyCampaign && ScenarioWorkflow.IsCampaignResult(lower)) continue;
                        Kind? kind = Classify(lower, name, boundaryName);
                        if (kind.HasValue) Add(f, kind.Value);
                    }
                }

                if (!legacyCampaign && Directory.Exists(campaign))
                {
                    foreach (string f in Directory.GetFiles(campaign))
                    {
                        string lower = Path.GetFileName(f).ToLowerInvariant();
                        if (!ScenarioWorkflow.IsCampaignResult(lower)) continue;
                        Kind? kind = Classify(lower, name, boundaryName);
                        if (kind.HasValue) Add(f, kind.Value);
                    }
                }

                //The fire of the last run, from the case's own outputs: the newest arrival-time raster.
                string caseDir = PREACT.Utility.ElmfireCoupling.CaseDirectoryPath(input.RootFolder, input.WildfireModule?.ElmfireInput);
                string caseOutputs = caseDir == null ? null : Path.Combine(caseDir, "outputs");
                if (caseOutputs != null && Directory.Exists(caseOutputs))
                {
                    string newest = null;
                    DateTime newestAt = DateTime.MinValue;
                    foreach (string f in Directory.GetFiles(caseOutputs, "time_of_arrival*.tif"))
                    {
                        DateTime at = File.GetLastWriteTimeUtc(f);
                        if (at > newestAt) { newestAt = at; newest = f; }
                    }
                    if (newest != null) Add(newest, Kind.FireArrival);
                }

                string wui = GuiFiles.Resolve(input.RootFolder, ScenarioFiles.CaseInput(input, "wui_area.tif"));
                if (File.Exists(wui)) Add(wui, Kind.WuiArea);

                string listed = legacyCampaign ? output : campaign;
                if (PREACT.Utility.CampaignLayout.PredatesEvacuationSeeds(listed) && !ProbabilisticTriggerWindow.IsRunning)
                {
                    _campaignNote = PREACT.Utility.CampaignLayout.DescribeEarlierCampaign(listed);
                }
            }
            catch (Exception e)
            {
                _status = "Could not list the results: " + e.Message;
            }

            _entries.Sort((a, b) => b.Written.CompareTo(a.Written));
        }

        private static bool SameFolder(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <remarks>
        /// The raster kinds take a raster only - .asc or .tif by the engine's rule, a boundary by the engine's own naming -
        /// never the .prj the engine writes beside each one, nor GDAL's or QGIS's .aux.xml and .ovr: those were listed as
        /// rasters of their own (ensemble_burn_probability.prj) and failed to read when shown.
        /// </remarks>
        private static Kind? Classify(string lower, string name, string outputName)
        {
            if (PREACT.Utility.CampaignLayout.IsRasterSidecar(lower)) return null;
            if (lower == name + ".log") return Kind.RunLog;
            if (lower.StartsWith(name + "_") && lower.EndsWith("_arrivaldata.csv") && !lower.Contains("_prob_")) return Kind.Arrivals;
            if (PREACT.Evacuation.EvacuationManager.IsBoundaryFile(lower, outputName)) return Kind.TriggerBoundary;
            if (lower == PREACT.Utility.CampaignLayout.ConvergenceCsv) return Kind.Convergence;
            if (!PREACT.Utility.CampaignLayout.IsRasterFile(lower)) return null;
            if (lower.StartsWith("trigger_probability")) return Kind.TriggerProbability;
            if (lower.StartsWith(PREACT.Utility.CampaignLayout.EnsemblePrefix + "_") && lower.Contains("burn_probability")) return Kind.BurnProbability;
            if (lower.StartsWith(PREACT.Utility.CampaignLayout.EnsemblePrefix + "_")) return Kind.ArrivalStatistic;
            return null;
        }

        private static void Add(string path, Kind kind)
        {
            var info = new FileInfo(path);
            _entries.Add(new Entry
            {
                Path = path,
                Name = info.Name,
                Kind = kind,
                Bytes = info.Length,
                Written = info.LastWriteTime,
                IsRaster = kind == Kind.TriggerBoundary || kind == Kind.TriggerProbability || kind == Kind.BurnProbability
                           || kind == Kind.ArrivalStatistic || kind == Kind.FireArrival || kind == Kind.WuiArea,
                Earlier = kind == Kind.TriggerBoundary && !File.Exists(System.IO.Path.ChangeExtension(path, ".prj")),
            });
        }

        private static FireDomainVisualizerUnity.RasterRamp RampFor(Kind kind)
        {
            switch (kind)
            {
                case Kind.TriggerProbability:
                case Kind.BurnProbability:
                    return FireDomainVisualizerUnity.RasterRamp.Probability;
                case Kind.ArrivalStatistic:
                case Kind.FireArrival:
                    return FireDomainVisualizerUnity.RasterRamp.Arrival;
                case Kind.TriggerBoundary:
                    return FireDomainVisualizerUnity.RasterRamp.Boundary;
                default:
                    return FireDomainVisualizerUnity.RasterRamp.Mask;
            }
        }

        /// <summary>Puts the newest result of a kind on the map (Results &gt; Show on map).</summary>
        public static bool ShowNewest(Kind kind, string nameContains = null)
        {
            RescanIfStale();
            foreach (Entry e in _entries)
            {
                if (e.Kind == kind && (nameContains == null || e.Name.ToLowerInvariant().Contains(nameContains)))
                {
                    Show(e);
                    return true;
                }
            }
            PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "There is no " + KindLabel(kind).ToLowerInvariant()
                + (nameContains != null ? " (" + nameContains + ")" : "") + " to show yet.");
            return false;
        }

        public static bool HasAny(Kind kind, string nameContains = null)
        {
            RescanIfStale();
            return _entries.Exists(e => e.Kind == kind && (nameContains == null || e.Name.ToLowerInvariant().Contains(nameContains)));
        }

        /// <summary>True when a result raster is on the map (View &gt; Map layers).</summary>
        public static bool IsShowing { get => PreactGUI.WUInity?.FireDomainVisualizer?.IsRasterVisible ?? false; }

        //A campaign writes while it runs and cannot say when; one directory listing every few seconds at most.
        private static void RescanIfStale()
        {
            if (_scannedRoot != ScenarioSession.RootFolder || UnityEngine.Time.realtimeSinceStartup > _nextScan)
            {
                Rescan();
            }
        }

        public static void Hide()
        {
            PreactGUI.WUInity?.FireDomainVisualizer?.HideRaster();
            _shown = null;
            _legend = string.Empty;
        }

        /// <summary>Reads the raster on a worker and draws it on the map when it arrives.</summary>
        private static void Show(Entry e)
        {
            PREACTInput input = ScenarioSession.Input;
            if (input == null || _loading) return;

            PREACT.Math.Vector2d origin = input.Simulation.Data.UTMOrigin;
            int zone = input.Simulation.Data.UtmEpsgCode;
            string path = e.Path;
            Kind kind = e.Kind;
            _loading = true;
            _status = "Reading " + e.Name + "...";

            Task.Run(() =>
            {
                float[,] data = null;
                PREACT.Utility.AscRaster.Header header = default;
                string failure = null;
                try
                {
                    data = PREACT.Utility.AscRaster.Read(path, out header, out bool ok);
                    if (!ok || data == null) failure = "could not read it";
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                }

                PreactGUI.Post(() =>
                {
                    _loading = false;
                    if (failure != null)
                    {
                        _status = "Could not show " + Path.GetFileName(path) + ": " + failure;
                        return;
                    }

                    //The corner is in the case's UTM zone, as the fire, the case and the campaign all are. An
                    //.asc cannot say which zone it is in; a GeoTIFF can, and is checked.
                    if (header.EpsgCode != 0 && zone != 0 && header.EpsgCode != zone)
                    {
                        _status = $"{Path.GetFileName(path)} is in EPSG:{header.EpsgCode}, not the scenario's EPSG:{zone}; shown anyway, likely misplaced.";
                    }
                    else
                    {
                        _status = string.Empty;
                    }

                    PREACT.Math.Vector2d size = new PREACT.Math.Vector2d(header.Ncols * header.CellSize, header.Nrows * header.CellSize);
                    PREACT.Math.Vector2d corner = new PREACT.Math.Vector2d(header.XllCorner, header.YllCorner) - origin;

                    PreactGUI.WUInity.ShowUTMMap();
                    if (PreactGUI.WUInity.FireDomainVisualizer.DisplayRaster(data, header.NoDataValue, size, corner, RampFor(kind),
                            out float min, out float max))
                    {
                        _shown = path;
                        _legend = Legend(kind, min, max);
                    }
                });
            });
        }

        private static string Legend(Kind kind, float min, float max)
        {
            switch (kind)
            {
                case Kind.TriggerProbability:
                    return $"Probability a cell is inside the trigger boundary: yellow low, dark red high (max {max:0.00}).";
                case Kind.BurnProbability:
                    return $"Probability a cell burns across the realizations: yellow low, dark red high (max {max:0.00}).";
                case Kind.ArrivalStatistic:
                case Kind.FireArrival:
                    return $"Fire arrival: red earliest ({min / 3600f:0.0} h) to blue latest ({max / 3600f:0.0} h).";
                case Kind.TriggerBoundary:
                    return $"Trigger boundary: values {min:0.##} to {max:0.##}, darker is lower.";
                default:
                    return "Cells in the mask.";
            }
        }

        public static string KindLabel(Kind kind)
        {
            switch (kind)
            {
                case Kind.RunLog: return "Run log";
                case Kind.Arrivals: return "Arrival times (CSV)";
                case Kind.TriggerBoundary: return "Trigger boundary";
                case Kind.TriggerProbability: return "Trigger probability";
                case Kind.BurnProbability: return "Burn probability";
                case Kind.ArrivalStatistic: return "Arrival statistic";
                case Kind.Convergence: return "Campaign convergence (CSV)";
                case Kind.FireArrival: return "Fire arrival (last run)";
                case Kind.WuiArea: return "WUI area (case)";
                default: return "Other";
            }
        }

        private static void Draw()
        {
            if (!_isOpen)
            {
                return;
            }

            //The campaign writes while it runs, and cannot say when it has; a listing is one directory read.
            if (UnityEngine.Time.realtimeSinceStartup > _nextScan || _scannedRoot != ScenarioSession.RootFolder)
            {
                Rescan();
            }

            PreactGUI.PlaceNextWindow(new Vector2(620f, 460f));
            if (ImGui.Begin("Results###Results", ref _isOpen, PreactGUI.ToolWindowFlags))
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

            if (ImGui.Button("Refresh")) Rescan();
            ImGui.SameLine();
            if (ImGui.Button("Open output folder"))
            {
                MainMenuBar.OpenInFileManager(Path.Combine(ScenarioSession.RootFolder, "_output"));
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(_shown == null && !(PreactGUI.WUInity?.FireDomainVisualizer?.IsRasterVisible ?? false));
            if (ImGui.Button("Hide from map")) Hide();
            ImGui.EndDisabled();

            if (_loading) ImGui.TextDisabled("Reading...");
            if (!string.IsNullOrEmpty(_status)) Fields.Warn(_status);
            if (!string.IsNullOrEmpty(_campaignNote)) Fields.Warn(_campaignNote);
            if (!string.IsNullOrEmpty(_legend))
            {
                ImGui.TextWrapped("On the map: " + Path.GetFileName(_shown ?? string.Empty) + ". " + _legend);
            }

            if (_entries.Count == 0)
            {
                ImGui.TextDisabled("Nothing yet: run the simulation (Run > Run simulation) or a campaign.");
                return;
            }

            ImGui.BeginChild("results", new Vector2(0, 0), (ImGuiChildFlags)1);
            DrawGroup("Last run", Kind.RunLog, Kind.Arrivals, Kind.TriggerBoundary, Kind.FireArrival);
            DrawGroup("Campaign", Kind.TriggerProbability, Kind.BurnProbability, Kind.ArrivalStatistic, Kind.Convergence);
            DrawGroup("Case", Kind.WuiArea);
            ImGui.EndChild();
        }

        private static void DrawGroup(string title, params Kind[] kinds)
        {
            bool any = false;
            foreach (Entry e in _entries)
            {
                if (Array.IndexOf(kinds, e.Kind) >= 0) { any = true; break; }
            }
            if (!any) return;

            ImGui.SeparatorText(title);
            int index = 0;
            foreach (Entry e in _entries)
            {
                if (Array.IndexOf(kinds, e.Kind) < 0) continue;
                ImGui.PushID(title + index++);

                ImGui.TextUnformatted(e.Name);
                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip($"{KindLabel(e.Kind)}\n{e.Path}\n{e.Bytes / 1024.0:0} KB, written {e.Written:yyyy-MM-dd HH:mm}");
                }
                ImGui.SameLine();
                ImGui.TextDisabled($"{e.Written:MM-dd HH:mm}");
                if (e.Earlier)
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled("(earlier version)");
                    if (ImGui.IsItemHovered())
                    {
                        ImGui.SetTooltip("No .prj beside it: written before the k-PERIL fix, when boundaries came out rotated by "
                            + "90 degrees. Run the simulation again for one to use.");
                    }
                }
                if (e.IsRaster)
                {
                    ImGui.SameLine();
                    ImGui.BeginDisabled(_loading);
                    if (ImGui.SmallButton(e.Path == _shown ? "Shown" : "Show on map")) Show(e);
                    ImGui.EndDisabled();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Open")) MainMenuBar.OpenInFileManager(e.Path);

                ImGui.PopID();
            }
        }
    }
}
