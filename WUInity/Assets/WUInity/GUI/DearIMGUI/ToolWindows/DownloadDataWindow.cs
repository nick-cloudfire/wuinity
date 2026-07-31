using ImGuiNET;
using PREACT.Math;
using System;
using System.Threading.Tasks;
using System.IO;

namespace Assets.WUInity.GUI.DearIMGUI
{
    public static class DownloadDataWindow
    {
        private static Vector2d _lowerLeftLatLon, _upperRightLatLon, _domainSize;
        private static DateTime _startDateTime, _endDateTime;
        private static bool _isOpen;
        private static bool _folderSet;
        private static string _downloadFolder = string.Empty;
        private static bool _useAnderson13 = true;
        private static string _osmFileName = string.Empty, _weatherFileName = string.Empty, _worldPopFileName = string.Empty;
        private static string _demFileName = "dem";
        private static int _demTypeIndex;
        //private static string[] _landfireYears = new string[] { "2016", "2020", "2023", "2024" };
 
        public static void Open()
        {
            if (!_isOpen)
            {
                PreactGUI.DrawWindow(Draw);
            }
            _isOpen = true;

            //The DEM type is the same user-level choice the scenario steps make, so the combo follows it
            //rather than starting at the first entry regardless of what was already chosen.
            _demTypeIndex = Math.Max(0, Array.IndexOf(ScenarioDataSteps.DemTypes, ScenarioDataSteps.DemType));

            if(ScenarioEditorWindow.HasInput)
            {
                _lowerLeftLatLon = ScenarioEditorWindow.Input.Simulation.LowerLeftLatLon;
                //A scenario stores the domain as a south-west corner plus a size in metres, so the
                //north-east corner is the corner plus that size in degrees. Seeding it with the south-west
                //corner instead left the AIO a point, and every download here a zero-sized request.
                Vector2d sizeDegrees = PREACT.Population.LocalGPWData.SizeToDegrees(
                    _lowerLeftLatLon, ScenarioEditorWindow.Input.Simulation.DomainSize);
                //SizeToDegrees returns (lonDegrees, latDegrees) for a size given as (east, north)
                _upperRightLatLon = new Vector2d(_lowerLeftLatLon.x + sizeDegrees.y, _lowerLeftLatLon.y + sizeDegrees.x);
                _startDateTime = ScenarioEditorWindow.Input.Simulation.StartDateTime;
                _endDateTime = ScenarioEditorWindow.Input.Simulation.EndDateTime;
            }
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
            ImGui.Begin("Download tool", ref _isOpen, PreactGUI.NoDockingNoCollapse);

            if (ImGui.Button("Set download folder")) { OpenSetDownloadFolder(); }
            if (!_folderSet)
            {                
                return;
            }
            ImGui.Text("Download folder set to:" + _downloadFolder);

            ImGui.SeparatorText("Area of interest (AIO)");
            if (ImGui.Button("Set AIO on map")) 
            {
                Close();
                PreactGUI.WUInity.PickBoundingBoxOnMap(SetAIO); 
            }
            CustomTypes.InputDouble2(nameof(PREACT.Input.SimulationInput.LowerLeftLatLon), ref _lowerLeftLatLon);
            CustomTypes.InputDouble2("UpperRightLatLon", ref _upperRightLatLon);
            //CustomTypes.InputDouble2(nameof(PREACT.Input.SimulationInput.DomainSize), ref _domainSize);

            ImGui.SeparatorText("Time period of interest");
            CustomTypes.InputDateTimePopup(nameof(PREACT.Input.SimulationInput.StartDateTime), ref _startDateTime);
            CustomTypes.InputDateTimePopup(nameof(PREACT.Input.SimulationInput.EndDateTime), ref _endDateTime);

            //if (ImGui.Button("Download all")) { DownloadAll(); }

            ImGui.SeparatorText("Landfire data");
            ImGui.Text("Downloads data from Landfire for the specified AIO.");
            ImGui.Checkbox("Get 13 Anderson FBFM? (else 40 Scott and Burgan)", ref _useAnderson13);
            if (ImGui.Button("Download landscape")) { Task.Run(() => DownloadLandscape()); }

            ImGui.SeparatorText("Elevation data");
            ImGui.Text("Downloads a DEM from OpenTopography for the specified AIO, warps it into the local UTM");
            ImGui.Text("zone, and derives slope and aspect beside it.");
            ScenarioDataWindow.DrawOpenTopographyKey();
            ImGui.InputText("DEM filename", ref _demFileName, 64);
            if (ImGui.Combo("DEM source", ref _demTypeIndex, ScenarioDataSteps.DemTypes, ScenarioDataSteps.DemTypes.Length))
            {
                ScenarioDataSteps.DemType = ScenarioDataSteps.DemTypes[_demTypeIndex];
            }
            if (ImGui.Button("Download DEM")) { Task.Run(() => DownloadDem()); }

            ImGui.SeparatorText("Weather data");
            ImGui.Text("Downloads data from Open-Meteo at the center of AIO and for the entire year of interest.");
            ImGui.InputText("Weather filename", ref _weatherFileName, 64);
            if (ImGui.Button("Download weather")) 
            {
                Task.Run(() => PREACT.Tools.OpenMeteoDownloader.Download(0.5 * (_lowerLeftLatLon + _upperRightLatLon), _startDateTime, _endDateTime, Path.Combine(_downloadFolder, _weatherFileName + ".csv")));
            }

            ImGui.SeparatorText("OpenStreetMap data");
            ImGui.Text("Downloads OSM data via Overpass for the specified AIO.");
            ImGui.InputText("OSM filename", ref _osmFileName, 64);
            if (ImGui.Button("Download OSM")) 
            {
                Task.Run(() => PREACT.Tools.OSMDownloader.Download(_lowerLeftLatLon, _upperRightLatLon, Path.Combine(_downloadFolder, _osmFileName + ".osm.xml"))); 
            }

            ImGui.SeparatorText("WorldPop data");
            ImGui.Text("Downloads WorldPopData for the entire country of interest.");
            ImGui.InputText("WorldPop filename", ref _worldPopFileName, 64);
            if (ImGui.Button("Download WorldPop"))
            {
                Task.Run(() => PREACT.Tools.WorldPopDownloader.DownloadRegionUTM(_startDateTime.Year, _lowerLeftLatLon, _upperRightLatLon, _downloadFolder, _worldPopFileName));
            }

            ImGui.End();
            if (!_isOpen)
            {
                PreactGUI.CloseWindow(Draw);
            }
        }

        private static void SetAIO(Vector2d[] latLons)
        {
            _lowerLeftLatLon = new Vector2d(Mathd.Min(latLons[0].x, latLons[1].x), Mathd.Min(latLons[0].y, latLons[1].y));
            _upperRightLatLon = new Vector2d(Mathd.Max(latLons[0].x, latLons[1].x), Mathd.Max(latLons[0].y, latLons[1].y));
            Open();
        }

        private static async Task DownloadLandscape()
        {
            //verify that we are in US
            Vector2d center = 0.5 * (_lowerLeftLatLon + _upperRightLatLon);
            string iso3 = await PREACT.Tools.WorldPopDownloader.LatLonToISO3(center.x, center.y);
            if(iso3 != "USA")
            {
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "Specified region is outside of the USA, cannot download Landfire data.");
            }
            else
            {
                await PREACT.Tools.LandfireLandscapeDownloader.Download(_startDateTime.Year, _useAnderson13, _lowerLeftLatLon, _upperRightLatLon, _downloadFolder);
            }           
        }

        /// <summary>
        /// Downloads a DEM for the area of interest and leaves it usable: warped into the local UTM zone,
        /// with slope and aspect derived beside it.
        ///
        /// The warp is not optional. OpenTopography serves degrees, and everything downstream - the
        /// landscape reader, the painter, k-PERIL - takes the cell size as metres, so a raster left in
        /// geographic coordinates arrives with its cells 0.0003 m apart. Unlike the scenario step, there is
        /// no simulation here to name a zone, so the zone is the one the centre of the AIO falls in.
        /// </summary>
        private static async Task DownloadDem()
        {
            try
            {
                string apiKey = ScenarioDataSteps.EffectiveOpenTopographyApiKey;
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning, "OpenTopography needs an API key. "
                        + "Copy Assets/Resources/OpenTopography/OpenTopographyConfigurationTemplate.txt to "
                        + "OpenTopographyConfiguration.txt beside it and paste a key into it. One is free from "
                        + "portal.opentopography.org.");
                    return;
                }

                string name = string.IsNullOrWhiteSpace(_demFileName) ? "dem" : _demFileName.Trim();
                string downloaded = Path.Combine(_downloadFolder, name + "_wgs84.tif");
                string warped = Path.Combine(_downloadFolder, name + ".tif");

                //Asked for with a margin, and clipped back to the AIO afterwards. A lat/lon box is not a
                //rectangle in UTM, so the box the warp clips to reaches past the corners of what was
                //downloaded, and GDAL fills what the source does not cover - with zero, since Copernicus
                //declares no nodata. Beside real hillside that fill reads as a cliff.
                const double marginDegrees = 0.005;
                Vector2d paddedLowerLeft = new Vector2d(_lowerLeftLatLon.x - marginDegrees, _lowerLeftLatLon.y - marginDegrees);
                Vector2d paddedUpperRight = new Vector2d(_upperRightLatLon.x + marginDegrees, _upperRightLatLon.y + marginDegrees);

                PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "Downloading " + ScenarioDataSteps.DemType
                    + " from OpenTopography, using the key from " + ScenarioDataSteps.OpenTopographyApiKeySource + ".");

                await PREACT.Tools.OpenTopographyDownloader.Download(
                    paddedLowerLeft, paddedUpperRight, apiKey, downloaded, ScenarioDataSteps.DemType);

                //Into the zone the open scenario measures in when there is one, and only otherwise the zone
                //recomputed from the centre of the AIO. The painter places this raster by subtracting the
                //simulation's UTM origin from its corner, so a DEM warped into a neighbouring zone would be
                //placed half a million metres away - which is what happens on a domain near a zone boundary.
                int targetEpsg = ScenarioEditorWindow.HasInput
                    ? ScenarioEditorWindow.Input.Simulation.Data.UtmEpsgCode
                    : 0;

                PREACT.Utility.MasterGrid grid = PREACT.Utility.RasterHarmonizer.BuildUtmMasterGrid(
                    downloaded, warped,
                    _lowerLeftLatLon.x, _lowerLeftLatLon.y, _upperRightLatLon.x, _upperRightLatLon.y,
                    null, targetEpsg);

                PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, $"Wrote {warped}: {grid.Header.Ncols} x "
                    + $"{grid.Header.Nrows} cells of {grid.Header.CellSize:F1} m, in {grid.Epsg}.");

                string slopePath = Path.Combine(_downloadFolder, name + "_slope.tif");
                string aspectPath = Path.Combine(_downloadFolder, name + "_aspect.tif");
                WriteSlopeAndAspect(grid, warped, slopePath, aspectPath);

                PointScenarioAtDem(warped, slopePath, aspectPath);
            }
            catch (Exception exception)
            {
                //Caught rather than left to fault the fire-and-forget Task, where it would vanish without
                //the window ever saying the download failed.
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Exception, "DEM download failed: " + exception.Message);
            }
        }

        /// <summary>
        /// Points the loaded scenario's landscape at what was just downloaded.
        ///
        /// Without this the download is only files on disk: everything that wants terrain - the painter
        /// above all, which needs a georeferenced raster to define its cells and otherwise says there is
        /// nothing to paint on - reads the scenario's Landscape paths, not the download folder. The
        /// scenario-scoped step does the same thing; this window downloading without it is why a DEM could
        /// arrive and change nothing.
        ///
        /// Written relative to the scenario root when the download landed inside it, so the scenario stays
        /// portable, and absolute otherwise, which the readers accept because they resolve with
        /// Path.Combine.
        /// </summary>
        private static void PointScenarioAtDem(string demPath, string slopePath, string aspectPath)
        {
            if (!ScenarioEditorWindow.HasInput)
            {
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "No scenario is open, so the DEM was left "
                    + "on disk. Set it as the elevation file under Landscape, or use the scenario's own "
                    + "Download DEM step, to paint or run on it.");
                return;
            }

            PREACT.Input.PREACTInput input = ScenarioEditorWindow.Input;
            input.Landscape.ElevationFile = RelativeToRootIfInside(demPath, input.RootFolder);
            if (File.Exists(slopePath)) { input.Landscape.SlopeFile = RelativeToRootIfInside(slopePath, input.RootFolder); }
            if (File.Exists(aspectPath)) { input.Landscape.AspectFile = RelativeToRootIfInside(aspectPath, input.RootFolder); }

            PREACT.Engine.Message(null, PREACT.Engine.LogType.Log, "Scenario now points at "
                + input.Landscape.ElevationFile + " for elevation. Save the scenario to keep it.");
        }

        private static string RelativeToRootIfInside(string path, string rootFolder)
        {
            if (string.IsNullOrEmpty(rootFolder))
            {
                return path;
            }

            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(rootFolder);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                root += Path.DirectorySeparatorChar;
            }

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length)
                : full;
        }

        /// <summary>
        /// Derives slope and aspect from the warped DEM and writes both as GeoTIFFs on its grid. ELMFIRE
        /// takes elevation, slope and aspect as three separate inputs, so a DEM downloaded on its own is
        /// only two thirds of what it wants.
        /// </summary>
        private static void WriteSlopeAndAspect(PREACT.Utility.MasterGrid grid, string demPath,
            string slopePath, string aspectPath)
        {
            float[,] elevation = PREACT.Utility.AscRaster.ReadGeoTiff(demPath,
                out PREACT.Utility.AscRaster.Header header, out bool ok);
            if (!ok || elevation == null)
            {
                PREACT.Engine.Message(null, PREACT.Engine.LogType.Warning,
                    "Could not read the DEM back, so no slope or aspect was written.");
                return;
            }

            PREACT.Utility.SlopeAspect.Compute(elevation, header.CellSize,
                out float[,] slope, out float[,] aspect);

            PREACT.Utility.GeoTiffRasterWriter.WriteBand(grid, slope, slopePath);
            PREACT.Utility.GeoTiffRasterWriter.WriteBand(grid, aspect, aspectPath);

            PREACT.Engine.Message(null, PREACT.Engine.LogType.Log,
                $"Wrote {slopePath} and {aspectPath}.");
        }

        /*private static void DownloadAll()
        {
            Task.Run(() => PREACT.Tools.LandfireLandscapeDownloader.Download(_startDateTime.Year, _useAnderson13, _lowerLeftLatLon, _upperRightLatLon, _downloadFolder));
            Task.Run(() => PREACT.Tools.OpenMeteoDownloader.Download(0.5 * (_lowerLeftLatLon + _upperRightLatLon), _startDateTime, _endDateTime, Path.Combine(_downloadFolder, _weatherFileName + ".csv")));
            Task.Run(() => PREACT.Tools.OSMTools.DownloadOMSData(_lowerLeftLatLon, _upperRightLatLon, Path.Combine(_downloadFolder, _osmFileName + ".osm.xml")));
            Task.Run(() => PREACT.Tools.WorldPopDownloader.DownloadRegionUTM(_startDateTime.Year, _lowerLeftLatLon, _upperRightLatLon, Path.Combine(_downloadFolder, _worldPopFileName + ".tif")));
        }*/

        private static void OpenSetDownloadFolder()
        {
            string initialFolder = PreactGUI.Engine.WorkingFolder;
            SimpleFileBrowser.FileBrowser.ShowLoadDialog(SetRootFolder, FileBrowser.Cancel, SimpleFileBrowser.FileBrowser.PickMode.Folders, false, initialFolder, null, "Set download folder", "Set");
        }
        private static void SetRootFolder(string[] paths)
        {
            _folderSet = true;
            _downloadFolder = paths[0];
        }
    }
}
