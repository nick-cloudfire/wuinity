using System.Globalization;
using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>
    /// Round 2's case build and fire areas (BL2): the fuel check that comes before any download, rebuilding only the
    /// weather, the WUI area as the evacuation groups' union, and painted initial ignition turned into an ignition point.
    /// On the synthetic case of <see cref="PipelineTests.SyntheticCase"/>: no network, no WindNinja, no ELMFIRE.
    /// </summary>
    /// <summary>
    /// A painting as builds before round 2 wrote it, with a painted WUI area and initial ignition: two int32 for the grid,
    /// four masks of one byte per cell (WUI area, ignition area, initial ignition, trigger buffer), and the grid record
    /// when given. The engine no longer writes the first and third, so tests of how old files are read write them here.
    /// </summary>
    internal static class LegacyPainting
    {
        public static void Write(string path, int ncols, int nrows, bool[] wui = null, bool[] area = null, bool[] initial = null,
            GraphicalFireInput.PaintedGrid grid = null)
        {
            int cells = ncols * nrows;
            using (var bw = new BinaryWriter(File.Create(path)))
            {
                bw.Write(ncols);
                bw.Write(nrows);
                foreach (bool[] mask in new[] { wui, area, initial, null })
                {
                    var bytes = new byte[cells];
                    if (mask != null) for (int i = 0; i < cells && i < mask.Length; ++i) bytes[i] = mask[i] ? (byte)1 : (byte)0;
                    bw.Write(bytes);
                }
                if (grid != null)
                {
                    bw.Write(System.Text.Encoding.ASCII.GetBytes(GraphicalFireInput.GridTrailerTag));
                    bw.Write(grid.XllCorner);
                    bw.Write(grid.YllCorner);
                    bw.Write(grid.CellSize);
                    bw.Write(grid.EpsgCode);
                }
            }
        }
    }

    internal static class CaseBuildTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("builder: a case without a fuel model is refused before its DEM, weather or WindNinja", NoFuelRefusedFirst);
            runner.Add("weather: rebuilding only the weather changes the five rasters and the namelist's time and band keys, nothing else", RebuildWeatherOnly);
            runner.Add("cli: build-case --weather-only rebuilds the weather and records only the [Weather] keys", CliWeatherOnly);
            runner.Add("wui: the case's wui_area.tif is the union of the evacuation groups, by mask and by shapefile; a painted one is left out", WuiAreaIsTheGroups);
            runner.Add("wui: the groups are placed on a case grid in another UTM zone than the simulation's", WuiAreaAcrossZones);
            runner.Add("wui: [kPERIL] protects the groups unless it names a mask of its own", KperilSourceDefaults);
            runner.Add("campaign: refused when the case's wui_area.tif is not the groups' union, accepted when it is", CampaignChecksWuiArea);
            runner.Add("painting: an old .gfi's initial ignition becomes one [IgnitionPoint] at its centroid; nothing writes or places it any more", InitialIgnitionBecomesAPoint);
        }

        /// <summary>
        /// Nick: "Why can I specify an initial ignition painting and also ignition points?" (decision: drop painted initial
        /// ignition). Auburn2's painting holds 27 initial-ignition cells: an old painting's become one ignition point at
        /// their centroid when the scenario is read - the point the case build used to ignite - said once; a scenario with
        /// points of its own keeps them; and nothing writes, moves or places a painted initial ignition any more.
        /// </summary>
        private static void InitialIgnitionBecomesAPoint()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                ElmfireCaseBuilder.Build(c.Options(Path.Combine(c.Folder, "case"), 150.0, new List<string>())).GetAwaiter().GetResult();
                string caseDem = ElmfireStems.Tif(Path.Combine(c.Folder, "case", "inputs"), ElmfireStems.Dem);
                MasterGrid g = MasterGrid.FromRasterFile(caseDem);
                int n = g.Header.Ncols * g.Header.Nrows;

                //Four cells, (20..21, 30..31): the centroid is the corner they share.
                var initial = new bool[n];
                var area = new bool[n];
                foreach ((int x, int y) in new[] { (20, 30), (21, 30), (20, 31), (21, 31) }) initial[x + y * g.Header.Ncols] = true;
                for (int i = 0; i < 300; ++i) area[i] = true;
                var record = PaintedMaskResampler.Grid.FromRaster(caseDem).ToPaintedGrid();
                string gfi = Path.Combine(c.Folder, "painted_fire_areas.gfi");
                LegacyPainting.Write(gfi, g.Header.Ncols, g.Header.Nrows, area: area, initial: initial, grid: record);
                Assert.True(CrsTransform.TryToWgs84(g.Epsg, g.XMin + 21 * 30.0, g.YMin + 31 * 30.0, out double lat, out double lon),
                    "the centroid in WGS84");

                string wui = c.WriteScenario("case", 150.0);
                File.WriteAllLines(wui, File.ReadAllLines(wui)
                    .Select(l => l == "Module=ELMFIRE" ? "Module=ELMFIRE\nGraphicalFireInputFile=painted_fire_areas.gfi" : l)
                    .SelectMany(l => l.Split('\n')));
                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                List<Wildfire.IgnitionPointInput> points = input.WildfireModule.Data.IgnitionPoints;
                Assert.Equal(1, points.Count, "one ignition point is made of the painted cells");
                Assert.Near(lat, points[0].LatLon.x, 1e-7, "at their centroid (latitude)");
                Assert.Near(lon, points[0].LatLon.y, 1e-7, "and longitude");
                Assert.True(!points[0].AbsoluteTime && points[0].IgnitionTime == 0f, "at the start, as the painted one was");
                Assert.Equal(300, input.WildfireModule.Data.RandomIgnition.Count(b => b), "the ignition area is kept");

                //Saved, it is a point of the scenario's own, and reading it again makes no second one.
                File.WriteAllLines(wui, Input.PREACTInputWriter.Write(input));
                Assert.True(File.ReadAllText(wui).Contains("[IgnitionPoint]"), "the save writes it as an [IgnitionPoint]");
                Input.PREACTInput again = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                Assert.Equal(1, again.WildfireModule.Data.IgnitionPoints.Count, "and it is not made twice");

                //A scenario with a point of its own elsewhere keeps it; the painted one is only noted.
                var own = new Wildfire.IgnitionPointInput(new Math.Vector2d(PipelineTests.SyntheticCase.Lat + 0.004,
                    PipelineTests.SyntheticCase.Lon + 0.004), false, 0f, again.Simulation.StartDateTime);
                again.WildfireModule.Data.IgnitionPoints.Clear();
                again.WildfireModule.Data.IgnitionPoints.Add(own);
                File.WriteAllLines(wui, Input.PREACTInputWriter.Write(again));
                Input.PREACTInput withOwn = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                Assert.True(withOwn.WildfireModule.Data.IgnitionPoints.Count == 1
                            && Math.Vector2d.Distance(withOwn.WildfireModule.Data.IgnitionPoints[0].LatLon, own.LatLon) < 1e-9,
                    "a scenario's own ignition point wins, as it did over the painted one");

                //The builder no longer places a painted initial ignition: only the points become X_IGN/Y_IGN.
                var log = new List<string>();
                ElmfireCaseBuilder.Options o = c.Options(Path.Combine(c.Folder, "case"), 150.0, log);
                o.PaintedMasksPath = gfi;
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();
                Assert.True(r.Ignitions.Count == 0 && log.Any(l => l.Contains("initial ignition (4 cells) is not used")),
                    "the build leaves it out and says so: " + string.Join(" | ", log.Where(l => l.Contains("painted"))));

                //Moving the painting, or saving it again, writes no initial ignition (nor WUI area).
                string moved = Path.Combine(c.Folder, "moved.gfi");
                PaintedMaskResampler.Result m = PaintedMaskResampler.ResampleFile(gfi, caseDem, caseDem, moved);
                GraphicalFireInput.LoadGraphicalFireInput(moved, out int _, out int _, out bool[] wuiAfter, out bool[] areaAfter,
                    out bool[] initialAfter, out bool[] _, out bool ok);
                Assert.True(ok && initialAfter.Count(b => b) == 0 && wuiAfter.Count(b => b) == 0 && areaAfter.Count(b => b) == 300,
                    "a moved painting keeps the ignition area and leaves the initial ignition out");
                Assert.True(m.LeftOutInitialIgnitionCells == 4 && m.Describe().Any(l => l.Contains("initial ignition: 4 cells, left out")),
                    "and says so");

                string saved = Path.Combine(c.Folder, "saved.gfi");
                GraphicalFireInput.SaveGraphicalFireInput(saved, input.WildfireModule.Data, g.Header.Ncols, g.Header.Nrows, record);
                GraphicalFireInput.LoadGraphicalFireInput(saved, out int _, out int _, out bool[] _, out bool[] savedArea,
                    out bool[] savedInitial, out bool[] _, out bool savedOk);
                Assert.True(savedOk && savedInitial.Count(b => b) == 0 && savedArea.Count(b => b) == 300,
                    "a painting saved now has the ignition area in its place and nothing in the initial ignition's");
            }
        }

        // ------------------------------------------------------------------ the WUI area

        /// <summary>A mask group in simulation coordinates: 5 x 5 cells of 30 m, 60 to 210 m east and north of the origin.</summary>
        private static void WriteWestMask(string path, int from = 2, int to = 6)
        {
            var header = new AscRaster.Header { Ncols = 10, Nrows = 10, XllCorner = 0.0, YllCorner = 0.0, CellSize = 30.0, NoDataValue = -9999 };
            var data = new float[10, 10];
            for (int x = from; x <= to; ++x) for (int y = from; y <= to; ++y) data[x, y] = 1f;
            AscRaster.Write(data, header, path);
        }

        private static bool InWest(double sx, double sy, int from = 2, int to = 6)
        {
            int x = (int)System.Math.Floor(sx / 30.0), y = (int)System.Math.Floor(sy / 30.0);
            return x >= from && x <= to && y >= from && y <= to;
        }

        /// <summary>A shapefile group: the square from <paramref name="lo"/> to <paramref name="hi"/> m east and north of the origin, in WGS84.</summary>
        private static void WriteSquareShapefile(string path, Input.SimulationData simulation, double lo, double hi)
        {
            OSGeo.OGR.Ogr.RegisterAll();
            using (OSGeo.OGR.Driver driver = OSGeo.OGR.Ogr.GetDriverByName("ESRI Shapefile"))
            using (OSGeo.OGR.DataSource ds = driver.CreateDataSource(path, new string[0]))
            using (var wgs84 = new OSGeo.OSR.SpatialReference(string.Empty))
            {
                wgs84.ImportFromEPSG(4326);
                OSGeo.OGR.Layer layer = ds.CreateLayer("east", wgs84, OSGeo.OGR.wkbGeometryType.wkbPolygon, new string[0]);
                var ring = new OSGeo.OGR.Geometry(OSGeo.OGR.wkbGeometryType.wkbLinearRing);
                foreach ((double x, double y) in new[] { (lo, lo), (hi, lo), (hi, hi), (lo, hi), (lo, lo) })
                {
                    Math.Vector2d latLon = simulation.GetWGS84FromSimulationPosition(new Math.Vector2d(x, y));
                    ring.AddPoint_2D(latLon.y, latLon.x);
                }
                var polygon = new OSGeo.OGR.Geometry(OSGeo.OGR.wkbGeometryType.wkbPolygon);
                polygon.AddGeometry(ring);
                using (var feature = new OSGeo.OGR.Feature(layer.GetLayerDefn()))
                {
                    feature.SetGeometry(polygon);
                    layer.CreateFeature(feature);
                }
                ds.FlushCache();
            }
        }

        /// <summary>Two groups: west by a painted mask, east by a shapefile.</summary>
        private static string ScenarioWithGroups(PipelineTests.SyntheticCase c, params string[] extra)
        {
            WriteWestMask(Path.Combine(c.Folder, "evac_group_west.asc"));
            var simulation = new Input.SimulationData(new Math.Vector2d(PipelineTests.SyntheticCase.Lat, PipelineTests.SyntheticCase.Lon));
            WriteSquareShapefile(Path.Combine(c.Folder, "east.shp"), simulation, 900.0, 1200.0);
            return c.WriteScenario("case", 150.0, new[]
            {
                "", "[EvacuationGroup]", "Name=west", "MaskFile=evac_group_west.asc", "Destinations=out", "ResponseCurves=standard",
                "", "[EvacuationGroup]", "Name=east", "ShapeFile=east.shp", "Destinations=out", "ResponseCurves=standard",
            }.Concat(extra).ToArray());
        }

        /// <summary>The scenario's build, as the GUI and build-case make it, offline.</summary>
        private static ElmfireCaseBuilder.Result BuildFrom(PipelineTests.SyntheticCase c, Input.PREACTInput input, List<string> log,
            string painted = null)
        {
            string caseDir = Path.Combine(c.Folder, "case");
            ElmfireCaseBuilder.Options o = ElmfireCoupling.CreateBuildOptions(input, input.WildfireModule.ElmfireInput, caseDir,
                m => { lock (log) log.Add(m); });
            o.LocalDemPath = c.DemPath;
            o.Weather.UseClimatology = false;
            o.Weather.WindNinjaExe = Path.Combine(c.Folder, "no-windninja-here");
            if (painted != null) o.PaintedMasksPath = painted;
            return ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Nick: "Why can I paint the trigger boundary WUI area in Fire areas ... when the trigger boundary menu depends on
        /// the evacuation groups (as it should)?" The case's WUI area is now the groups' union, cell for cell where a cell's
        /// centre is in a group: a painted mask in simulation coordinates and a WGS84 shapefile alike. A painted WUI area is
        /// left out with a note, and a scenario without groups has no WUI area - an earlier one is removed.
        /// </summary>
        private static void WuiAreaIsTheGroups()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string wui = ScenarioWithGroups(c);
                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                Assert.Equal(2, input.Evacuation.EvacuationGroupInputs.Count, "both groups are read");

                var log = new List<string>();
                ElmfireCaseBuilder.Result r = BuildFrom(c, input, log);
                string inputs = Path.Combine(c.Folder, "case", "inputs");
                string wuiArea = ElmfireStems.Tif(inputs, ElmfireStems.WuiArea);
                Assert.True(r.WuiAreaFile == wuiArea && File.Exists(wuiArea), "the build writes wui_area.tif: " + string.Join(" | ", log));

                MasterGrid g = MasterGrid.FromRasterFile(wuiArea);
                float[,] mask = AscRaster.ReadGeoTiff(wuiArea, out AscRaster.Header _, out bool ok);
                Assert.True(ok, "it reads back");
                Math.Vector2d origin = input.Simulation.Data.UTMOrigin;
                int west = 0, east = 0, wrong = 0, marked = 0;
                for (int x = 0; x < g.Header.Ncols; ++x)
                {
                    for (int y = 0; y < g.Header.Nrows; ++y)
                    {
                        double sx = g.XMin + (x + 0.5) * 30.0 - origin.x, sy = g.YMin + (y + 0.5) * 30.0 - origin.y;
                        bool w = InWest(sx, sy), e = sx > 900.0 && sx < 1200.0 && sy > 900.0 && sy < 1200.0;
                        if (w) ++west;
                        if (e) ++east;
                        bool set = mask[x, y] > 0.5f;
                        if (set) ++marked;
                        if (set != (w || e)) ++wrong;
                    }
                }
                Assert.True(west > 0 && east > 0, $"both groups cover cells of the case grid (west {west}, east {east})");
                Assert.Equal(0, wrong, $"every cell is the union of the groups ({marked} marked, {west + east} expected)");
                Assert.Equal(west + east, r.WuiAreaCells, "and the build counts them");
                Assert.True(log.Any(l => l.Contains("WUI area: the evacuation group(s) west,east -> wui_area.tif")), "it says which groups");
                Assert.True(File.ReadAllLines(Path.Combine(c.Folder, "case", ElmfireCaseBuilder.SourceManifestName))
                    .Contains(ElmfireCaseBuilder.WuiAreaGroupsKey + "=west,east"), "and records them");
                Assert.True(ElmfireCoupling.CaseKeysForScenario(input.RootFolder, Path.Combine(c.Folder, "case"), "synthetic", r)
                    .All(k => k.Section != "kPERIL"), "no [kPERIL] key is recorded: k-PERIL reads the groups themselves");

                //An older painting with a WUI area (every cell) changes nothing about it, and says so once.
                int all = g.Header.Ncols * g.Header.Nrows;
                var everywhere = new bool[all];
                for (int i = 0; i < all; ++i) everywhere[i] = true;
                string gfi = Path.Combine(c.Folder, "painted_fire_areas.gfi");
                LegacyPainting.Write(gfi, g.Header.Ncols, g.Header.Nrows, wui: everywhere);
                string before = ElmfireFingerprint.HashFile(wuiArea);
                log.Clear();
                BuildFrom(c, input, log, gfi);
                Assert.Equal(before, ElmfireFingerprint.HashFile(wuiArea), "the painted WUI area is not the case's");
                Assert.Equal(1, log.Count(l => l.Contains($"the painting's WUI area ({all} cells) is not used")),
                    "it is noted once: " + string.Join(" | ", log));

                //One group fewer, then none.
                input.Evacuation.EvacuationGroupInputs.Remove("east");
                ElmfireCaseBuilder.Result westOnly = BuildFrom(c, input, new List<string>());
                Assert.Equal(west, westOnly.WuiAreaCells, "without east it is west's cells");
                input.Evacuation.EvacuationGroupInputs.Clear();
                log.Clear();
                ElmfireCaseBuilder.Result none = BuildFrom(c, input, log);
                Assert.True(none.WuiAreaFile == null && !File.Exists(wuiArea), "with no group the case has no WUI area");
                Assert.True(log.Any(l => l.Contains("WUI area: none - no evacuation group has an area")
                                         && l.Contains("the wui_area.tif an earlier build wrote was removed")), "and says so");
            }
        }

        /// <summary>
        /// Mati sits on 24 E: a case is cut in its padded centre's zone, which can be the next one over from the corner's.
        /// The groups (simulation coordinates, in the corner's zone) land on the same ground either way.
        /// </summary>
        private static void WuiAreaAcrossZones()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string mask = Path.Combine(c.Folder, "evac_group_west.asc");
                WriteWestMask(mask, 0, 9);
                var simulation = new Input.SimulationData(new Math.Vector2d(PipelineTests.SyntheticCase.Lat, PipelineTests.SyntheticCase.Lon));
                var group = new Evacuation.EvacuationGroupInput { Name = "west", MaskFile = mask };
                Evacuation.EvacuationGroupArea area = Evacuation.EvacuationGroupArea.Load(group, c.Folder, simulation, out string problem, out bool _);
                Assert.True(problem == null && area.HasArea && !area.IsEmpty, "the mask reads: " + problem);

                //The same 600 m around the area in both zones, at 10 m.
                MasterGrid Around(string epsg)
                {
                    Assert.True(CrsTransform.TryWgs84To(epsg, PipelineTests.SyntheticCase.Lat, PipelineTests.SyntheticCase.Lon,
                        out double x0, out double y0), "corner in " + epsg);
                    return new MasterGrid
                    {
                        Header = new AscRaster.Header { Ncols = 60, Nrows = 60, CellSize = 10, CellSizeY = 10, XllCorner = x0 - 150.0, YllCorner = y0 - 150.0 },
                        Epsg = epsg,
                    };
                }

                bool[] own = Evacuation.EvacuationGroupArea.Rasterize(new[] { area }, Around("EPSG:32634"), simulation, out int ownCells);
                bool[] next = Evacuation.EvacuationGroupArea.Rasterize(new[] { area }, Around("EPSG:32635"), simulation, out int nextCells);
                Assert.Equal(900, ownCells, "300 x 300 m at 10 m in the simulation's own zone");
                Assert.True(System.Math.Abs(nextCells - ownCells) <= 0.1 * ownCells,
                    $"about the same area through a zone change ({nextCells} vs {ownCells} cells)");
                Assert.True(own.Length == next.Length, "(two grids of one size)");
            }
        }

        /// <summary>No source and no file: the groups combined. A WuiAreaFile with no source: that mask, as it always was.</summary>
        private static void KperilSourceDefaults()
        {
            Input.kPERILInput Parse(params string[] keys)
            {
                string[] lines = new[] { "[kPERIL]" }.Concat(keys).ToArray();
                return Input.kPERILInput.Parse(lines, 0, Path.GetTempPath(), out bool _);
            }

            Assert.Equal(Input.kPERILInput.WuiAreaSources.EvacuationGroupsCombined, Parse("OutputName=b").WuiAreaSource,
                "a scenario that names neither protects the groups");
            Assert.Equal(Input.kPERILInput.WuiAreaSources.Raster, Parse("WuiAreaFile=own_wui.tif").WuiAreaSource,
                "one that names a mask reads it");
            Assert.Equal(Input.kPERILInput.WuiAreaSources.EvacuationGroupsSeparate, Parse("WuiAreaSource=EvacuationGroupsSeparate").WuiAreaSource,
                "and a source given is the source");
            Assert.Equal(Input.kPERILInput.WuiAreaSources.EvacuationGroupsCombined, new Input.kPERILInput().WuiAreaSource,
                "a new trigger boundary protects the groups");
        }

        /// <summary>
        /// Every realization protects the case's wui_area.tif and is checked against it; a campaign on one that is not the
        /// groups' union (repainted since, or an older build's painted WUI area) would answer for other ground.
        /// </summary>
        private static void CampaignChecksWuiArea()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string wui = ScenarioWithGroups(c, "", "[TriggerBufferModule]", "Enabled=true", "Module=kPERIL", "", "[kPERIL]",
                    "WuiAreaSource=EvacuationGroupsCombined");
                Input.PREACTInput input = Input.PREACTInput.LoadFromDisk(wui, out bool _);
                BuildFrom(c, input, new List<string>());
                //The table the build copies from ELMFIRE's source tree, which a checkout without the submodule lacks.
                string table = Path.Combine(c.Folder, "case", "inputs", ElmfireStems.FuelModelTable);
                if (!File.Exists(table)) File.WriteAllText(table, "# a stand-in fuel model table\n");

                (PREACTcli.Campaigns.Campaign Campaign, string Said) Inspect()
                {
                    TextWriter error = Console.Error, output = Console.Out;
                    var captured = new StringWriter();
                    try
                    {
                        Console.SetError(captured);
                        Console.SetOut(TextWriter.Null);
                        return (PREACTcli.Campaigns.CampaignSetup.Resolve(PREACTcli.Campaigns.CampaignOptions.Parse(
                            new[] { "--wui", wui, "--inspect" })), captured.ToString());
                    }
                    finally
                    {
                        Console.SetError(error);
                        Console.SetOut(output);
                    }
                }

                (PREACTcli.Campaigns.Campaign ready, string said) = Inspect();
                string caseWui = ElmfireStems.Tif(Path.Combine(c.Folder, "case", "inputs"), ElmfireStems.WuiArea);
                Assert.True(ready != null && ready.WuiAreaFile == caseWui && ready.WuiCells > 0,
                    "a case built from the groups is accepted, protecting its wui_area.tif: " + said);

                //The west group painted again, bigger, and the case not built again.
                WriteWestMask(Path.Combine(c.Folder, "evac_group_west.asc"), 1, 8);
                (PREACTcli.Campaigns.Campaign stale, string why) = Inspect();
                Assert.True(stale == null && why.Contains("is not the WUI area of the scenario's evacuation groups")
                            && why.Contains("Build the case again"), "a stale one is refused, saying what to do: " + why);
            }
        }

        /// <summary>Every file of a case by content, without the five weather rasters (and GDAL's .aux.xml beside any raster).</summary>
        private static Dictionary<string, string> HashesWithoutWeather(string caseDir)
        {
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string f in Directory.GetFiles(caseDir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(f);
                if (name.EndsWith(".aux.xml", StringComparison.Ordinal)) continue;
                if (ElmfireStems.Weather.Any(w => name == w + ".tif")) continue;
                hashes[Path.GetRelativePath(caseDir, f)] = ElmfireFingerprint.HashFile(f);
            }
            return hashes;
        }

        /// <summary>
        /// Nick: "Rebuild the weather now should not need to rebuild the entire case, just weather." A 1 h case from 12:00
        /// has its weather made again for 3 h from 15:30: ws/wd/m1/m10/m100 get 3 bands, the namelist's start and band keys
        /// follow, and every other file in the case is byte for byte what it was. The record of the namelist's hash follows
        /// it, so the next full build keeps it and keeps the weather.
        /// </summary>
        private static void RebuildWeatherOnly()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                string inputs = Path.Combine(caseDir, "inputs");
                ElmfireCaseBuilder.Options first = c.Options(caseDir, 150.0, new List<string>());
                first.StartDateTime = new DateTime(2026, 6, 28, 12, 0, 0);
                ElmfireCaseBuilder.Build(first).GetAwaiter().GetResult();
                Assert.Equal(1, AscRaster.GetBandCount(ElmfireStems.Tif(inputs, ElmfireStems.WindSpeed)), "a 1 h fire's weather bands");
                Dictionary<string, string> before = HashesWithoutWeather(caseDir);
                string namelistBefore = before["elmfire.data"];
                string[] linesBefore = File.ReadAllLines(Path.Combine(caseDir, "elmfire.data"));

                var log = new List<string>();
                ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, log);
                o.StartDateTime = new DateTime(2026, 6, 28, 15, 30, 0);
                o.SimulationTstopSeconds = 3 * 3600.0;
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.RebuildWeather(o).GetAwaiter().GetResult();

                foreach (string stem in ElmfireStems.Weather)
                {
                    Assert.Equal(3, AscRaster.GetBandCount(ElmfireStems.Tif(inputs, stem)), stem + ".tif bands for 3 h of fire");
                }
                Assert.True(r.Validation != null && r.Validation.Ok, "the case is consistent: " + ElmfireCaseValidator.Summarize(r.Validation));
                Assert.True(!log.Any(l => l.Contains("warping onto the master grid") || l.Contains("Warping DEM")),
                    "no layer was warped: " + string.Join(" | ", log));

                Dictionary<string, string> after = HashesWithoutWeather(caseDir);
                foreach (KeyValuePair<string, string> kv in before)
                {
                    if (kv.Key == "elmfire.data" || kv.Key == ElmfireCaseBuilder.SourceManifestName) continue;
                    Assert.True(after.TryGetValue(kv.Key, out string now) && now == kv.Value, kv.Key + " is unchanged");
                }
                Assert.True(after.Keys.All(k => before.ContainsKey(k)), "and no file was added: "
                    + string.Join(", ", after.Keys.Where(k => !before.ContainsKey(k))));

                string[] linesAfter = File.ReadAllLines(Path.Combine(caseDir, "elmfire.data"));
                List<string> changed = ElmfireNamelist.DescribeDifferences(linesBefore, linesAfter);
                var allowed = new[] { "BAND_ONE_HOUR_OF_YEAR", "HOUR_OF_YEAR", "FORECAST_START_HOUR", "SIMULATION_TSTOP", "NUM_METEOROLOGY_TIMES" };
                Assert.True(changed.Count > 0 && changed.All(d => allowed.Any(k => d.Contains(" " + k + ":"))),
                    "only the time and band keys changed: " + string.Join(" | ", changed));
                Assert.Equal("15.5", ElmfireNamelist.GetKeyInGroup(linesAfter, ElmfireNamelistKeys.TimeControlGroup, "FORECAST_START_HOUR"),
                    "the fire starts at 15:30");
                Assert.Equal("3", ElmfireNamelist.GetKeyInGroup(linesAfter, ElmfireNamelistKeys.MonteCarloGroup, ElmfireNamelistKeys.NumMeteorologyTimes),
                    "and reads the three bands");

                ElmfireCaseBuilder.WeatherRecord record = ElmfireCaseBuilder.ReadWeatherRecord(caseDir);
                Assert.True(record != null && record.Start == o.StartDateTime && System.Math.Abs(record.Hours - 3.0) < 1e-9,
                    "the case records what its weather was made for");

                //The next full build of the same fire keeps both, and does not take the fitted namelist for a hand edit.
                var next = new List<string>();
                ElmfireCaseBuilder.Options same = c.Options(caseDir, 150.0, next);
                same.StartDateTime = o.StartDateTime;
                same.SimulationTstopSeconds = o.SimulationTstopSeconds;
                ElmfireCaseBuilder.Result rebuilt = ElmfireCaseBuilder.Build(same).GetAwaiter().GetResult();
                Assert.True(rebuilt.KeptNamelistPath == null, "the fitted namelist is the builder's own, not a hand edit");
                Assert.True(rebuilt.Reused.Contains(ElmfireStems.WindSpeed), "and the weather is kept: " + string.Join(" | ", next));
                Assert.True(namelistBefore != ElmfireFingerprint.HashFile(Path.Combine(caseDir, "elmfire.data")), "(the namelist did change)");

                //A full build for another start hour makes the weather again, saying why.
                var moved = new List<string>();
                ElmfireCaseBuilder.Options later = c.Options(caseDir, 150.0, moved);
                later.StartDateTime = new DateTime(2026, 6, 28, 9, 0, 0);
                later.SimulationTstopSeconds = o.SimulationTstopSeconds;
                ElmfireCaseBuilder.Result redone = ElmfireCaseBuilder.Build(later).GetAwaiter().GetResult();
                Assert.True(!redone.Reused.Contains(ElmfireStems.WindSpeed)
                            && moved.Any(l => l.Contains("made for a fire starting at 15:30 and this one starts at 09:00")),
                    "weather made for another start hour is made again: " + string.Join(" | ", moved));

                //No case, no weather: refused, not built.
                Exception none = null;
                try { ElmfireCaseBuilder.RebuildWeather(c.Options(Path.Combine(c.Folder, "nothing"), 150.0, new List<string>())).GetAwaiter().GetResult(); }
                catch (Exception e) { none = e; }
                Assert.True(none is FileNotFoundException && !Directory.Exists(Path.Combine(c.Folder, "nothing", "inputs")),
                    "a folder without a case is refused and left alone: " + none?.Message);
            }
        }

        /// <summary>The same through PREACTcli: <c>--weather-only</c> on a built case, which records only the weather keys.</summary>
        private static void CliWeatherOnly()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string wui = c.WriteScenario("case", 300.0);
                string windNinja = Path.Combine(c.Folder, "no-windninja-here");
                (int exit, string output) = PipelineTests.RunCli(c.Folder, "build-case", "--wui", wui, "--dem", c.DemPath,
                    "--no-climatology", "--windninja", windNinja);
                Assert.Equal(0, exit, "the case builds (" + PipelineTests.Tail(output) + ")");
                string caseDir = Path.Combine(c.Folder, "case");
                Dictionary<string, string> before = HashesWithoutWeather(caseDir);

                (exit, output) = PipelineTests.RunCli(c.Folder, "build-case", "--wui", wui, "--weather-only", "--hours", "2",
                    "--no-climatology", "--windninja", windNinja);
                Assert.Equal(0, exit, "--weather-only exit code (" + PipelineTests.Tail(output) + ")");
                Assert.True(output.Contains("Weather rebuilt in") && output.Contains("Rebuilding the weather only"),
                    "it says what it did: " + PipelineTests.Tail(output, 12));
                Assert.Equal(2, AscRaster.GetBandCount(ElmfireStems.Tif(Path.Combine(caseDir, "inputs"), ElmfireStems.WindSpeed)),
                    "ws.tif covers 2 h");
                Dictionary<string, string> after = HashesWithoutWeather(caseDir);
                Assert.True(before.Where(kv => kv.Key != "elmfire.data" && kv.Key != ElmfireCaseBuilder.SourceManifestName)
                        .All(kv => after.TryGetValue(kv.Key, out string h) && h == kv.Value),
                    "no other file of the case changed");
                Assert.True(!output.Contains("[Landscape]") && !output.Contains("[kPERIL]"),
                    "only [Weather] keys would be recorded: " + PipelineTests.Tail(output, 12));

                (exit, output) = PipelineTests.RunCli(c.Folder, "build-case", "--wui", wui, "--weather-only", "--rebuild");
                Assert.True(exit == 2 && output.Contains("Pass one of them"), "--weather-only with --rebuild is refused: " + PipelineTests.Tail(output));
            }
        }

        /// <summary>
        /// Auburn2: the build downloaded ERA5 and ran 24 WindNinja bands before its validation found no fbfm40/fbfm13. Asked
        /// first now: nothing is written, and the message names the missing file when the scenario names one.
        /// </summary>
        private static void NoFuelRefusedFirst()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                var log = new List<string>();
                ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, log);
                o.UserRasters.Remove("fbfm40");
                o.UnresolvedSourceRasters["fbfm40"] = "downloads/landfire/fbfm40.tif";

                Exception failed = null;
                try { ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult(); }
                catch (Exception e) { failed = e; }

                Assert.True(failed is InvalidDataException && failed.Message.Contains("no fuel model")
                            && failed.Message.Contains("downloads/landfire/fbfm40.tif"),
                    "refused for want of fuel, naming the file the scenario names: " + failed?.Message);
                string inputs = Path.Combine(caseDir, "inputs");
                Assert.True(!File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.Dem)), "no DEM was made");
                Assert.True(!File.Exists(ElmfireStems.Tif(inputs, ElmfireStems.WindSpeed)), "no weather was made");
                Assert.True(!log.Any(l => l.Contains("Warping DEM") || l.Contains("baseline weather")),
                    "nothing was started: " + string.Join(" | ", log));

                //The case's own fuel is enough: a second build of a built case needs no source for it.
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, new List<string>())).GetAwaiter().GetResult();
                ElmfireCaseBuilder.Options again = c.Options(caseDir, 150.0, new List<string>());
                again.UserRasters.Remove("fbfm40");
                ElmfireCaseBuilder.Result r = ElmfireCaseBuilder.Build(again).GetAwaiter().GetResult();
                Assert.True(r.FuelStem == "fbfm40" && r.Validation.Ok, "a case that has its fuel builds without a source for it");
            }
        }
    }
}
