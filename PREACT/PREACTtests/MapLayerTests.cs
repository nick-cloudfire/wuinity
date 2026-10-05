using PREACT.Utility;
using PREACT.Visualization.MapLayers;

namespace PREACT.Tests
{
    /// <summary>
    /// View &gt; Map layers &gt; Fire case inputs and the point info: the fuel model colours, which rasters a case lists and
    /// how their numbers read, the cell a point falls in, the colouring, and the GDAL reads behind them.
    /// </summary>
    internal static class MapLayerTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("map layers: FBFM40 and FBFM13 tables are complete, unique and in LANDFIRE's colours", PaletteTables);
            runner.Add("map layers: a case lists its namelist's rasters, the standard stems and the LANDFIRE sources", CatalogLists);
            runner.Add("map layers: canopy, wind and moisture read in real units under the namelist's scaling flags", ScalingFlags);
            runner.Add("map layers: values in words (fuel, aspect, wind from, masks, arrival times, bands)", ValueWords);
            runner.Add("map layers: a point's cell, from the west and from the north, and nothing off the grid", CellLookup);
            runner.Add("map layers: colouring (fuel colours, transparent nodata and code 0, masks, ramp ends, opacity)", Colouring);
            runner.Add("map layers: GDAL display read decimates, is cached by write time, and samples the right cell", GdalReads);
        }

        private static void PaletteTables()
        {
            Assert.Equal(45, FuelModelPalette.Fbfm40.Length, "40 burnable models and 5 non-burnable codes");
            Assert.Equal(18, FuelModelPalette.Fbfm13.Length, "13 models and 5 non-burnable codes");
            foreach (FuelModelPalette.FuelSystem system in new[] { FuelModelPalette.FuelSystem.Fbfm40, FuelModelPalette.FuelSystem.Fbfm13 })
            {
                var table = FuelModelPalette.Table(system);
                Assert.Equal(table.Length, table.Select(e => e.Code).Distinct().Count(), system + " codes unique");
                Assert.Equal(table.Length, table.Select(e => (e.R, e.G, e.B)).Distinct().Count(), system + " colours unique");
                Assert.True(table.All(e => !string.IsNullOrWhiteSpace(e.Name) && !string.IsNullOrWhiteSpace(e.Label)), system + " named");
            }
            int[] families = { 91, 92, 93, 98, 99, 101, 109, 121, 124, 141, 149, 161, 165, 181, 189, 201, 204 };
            foreach (int code in families) Assert.True(FuelModelPalette.TryGet(FuelModelPalette.FuelSystem.Fbfm40, code, out _), "FBFM40 has " + code);
            Assert.True(!FuelModelPalette.TryGet(FuelModelPalette.FuelSystem.Fbfm40, 110, out _), "no GR10");
            Assert.True(FuelModelPalette.Fbfm40.Count(e => !e.Official) == 1 && FuelModelPalette.Fbfm40.First(e => !e.Official).Label == "GR9",
                "only GR9's colour is not LANDFIRE's");
            Assert.True(FuelModelPalette.Fbfm40.Count(e => !e.Burnable) == 5, "five non-burnable codes");

            //Spot checks against LANDFIRE's legend swatches (LF2024 FBFM40/FBFM13 CONUS).
            AssertColor(FuelModelPalette.FuelSystem.Fbfm40, 102, 255, 211, 115, "GR2");
            AssertColor(FuelModelPalette.FuelSystem.Fbfm40, 165, 38, 115, 0, "TU5");
            AssertColor(FuelModelPalette.FuelSystem.Fbfm40, 188, 0, 92, 230, "TL8");
            AssertColor(FuelModelPalette.FuelSystem.Fbfm40, 98, 0, 14, 214, "NB8");
            AssertColor(FuelModelPalette.FuelSystem.Fbfm13, 1, 255, 255, 190, "Anderson 1");
            AssertColor(FuelModelPalette.FuelSystem.Fbfm13, 10, 38, 115, 0, "Anderson 10");
            AssertColor(FuelModelPalette.FuelSystem.Fbfm13, 98, 0, 0, 255, "FBFM13 water");

            Assert.Equal("102 GR2 - Low load, dry climate grass", FuelModelPalette.Describe(FuelModelPalette.FuelSystem.Fbfm40, 102), "FBFM40 words");
            Assert.Equal("7 - Southern rough", FuelModelPalette.Describe(FuelModelPalette.FuelSystem.Fbfm13, 7), "FBFM13 words");
            Assert.True(FuelModelPalette.Describe(FuelModelPalette.FuelSystem.Fbfm40, 256).Contains("not a standard FBFM40"), "unknown code says so");
            FuelModelPalette.Color(FuelModelPalette.FuelSystem.Fbfm40, 256, out byte r, out byte g, out byte b);
            Assert.True(r == 150 && g == 150 && b == 160, "unknown code grey");
        }

        private static void AssertColor(FuelModelPalette.FuelSystem system, int code, byte r, byte g, byte b, string what)
        {
            Assert.True(FuelModelPalette.TryGet(system, code, out FuelModelPalette.Entry e), what + " present");
            Assert.Equal((r, g, b), (e.R, e.G, e.B), what + " colour");
        }

        private static string[] Namelist(params string[] extra)
        {
            var lines = new List<string>
            {
                "&INPUTS",
                "FUELS_AND_TOPOGRAPHY_DIRECTORY = './inputs'",
                "DEM_FILENAME = 'dem'",
                "ASP_FILENAME = 'asp'",
                "FBFM_FILENAME = 'fbfm40_roads101'",
                "CC_FILENAME = 'cc'",
                "CH_FILENAME = 'ch'",
                "WEATHER_DIRECTORY = './inputs'",
                "WS_FILENAME = 'ws'",
                "WD_FILENAME = 'wd'",
                "M1_FILENAME = 'm1'",
                "IGNITION_MASK_FILENAME = 'ignition_mask'",
                "BLDG_FUEL_MODEL_FILENAME = 'bfm_h'",
            };
            lines.AddRange(extra);
            lines.Add("/");
            return lines.ToArray();
        }

        private static void CatalogLists()
        {
            string root = Directory.CreateTempSubdirectory("preact-layers-").FullName;
            try
            {
                string caseDir = Path.Combine(root, "elmfire"), inputs = Path.Combine(caseDir, "inputs");
                string landfire = Path.Combine(root, "downloads", "landfire");
                Directory.CreateDirectory(inputs);
                Directory.CreateDirectory(landfire);
                foreach (string stem in new[] { "dem", "asp", "fbfm40_roads101", "fbfm40", "cc", "ws", "wd", "ignition_mask", "wui_area", "bfm_h", "mati_dem" })
                {
                    File.WriteAllText(Path.Combine(inputs, stem + ".tif"), string.Empty);
                }
                //ch is named by the namelist but missing: not listed.
                foreach (string f in new[] { "Auburn2_LF2024_fbfm40.tif", "Auburn2_LF2024_ch.tif", "jb60e4bb5f2664172903f9c734946230f.tif", "Auburn2_LF2024_landfire.txt" })
                {
                    File.WriteAllText(Path.Combine(landfire, f), string.Empty);
                }
                File.WriteAllText(Path.Combine(landfire, "Auburn2_LF2024_landfire.txt"), "Release=LF2024\nCH_TIMES_10=false\n");

                List<InputLayer> layers = InputLayerCatalog.List(caseDir, Namelist(), landfire);
                string[] names = layers.Select(l => l.FileName).ToArray();
                Assert.True(names.Contains("fbfm40_roads101.tif") && names.Contains("fbfm40.tif"), "the fuel the namelist runs, and the plain one beside it");
                Assert.True(names.Contains("wui_area.tif") && names.Contains("bfm_h.tif"), "wui_area (no key) and the building fuel model");
                Assert.True(!names.Contains("ch.tif"), "a raster the namelist names but the case lacks is not listed");
                Assert.True(!names.Contains("mati_dem.tif"), "an intermediate that is no input is not listed");
                Assert.True(!names.Contains("jb60e4bb5f2664172903f9c734946230f.tif"), "the LFPS download itself is not a source layer");
                Assert.Equal(12, layers.Count, "ten case rasters and two sources");

                InputLayer run = layers.First(l => l.FileName == "fbfm40_roads101.tif");
                Assert.Equal(LayerStyle.FuelModel40, run.Style, "fuel model style");
                Assert.Equal(0, layers.IndexOf(run), "the fuel ELMFIRE runs first");
                Assert.Equal(LayerStyle.Mask, layers.First(l => l.FileName == "wui_area.tif").Style, "wui_area a mask");
                Assert.Equal(LayerStyle.Classes, layers.First(l => l.FileName == "bfm_h.tif").Style, "building fuel model classes");
                Assert.Equal(LayerStyle.Cyclic, layers.First(l => l.FileName == "wd.tif").Style, "wind direction cyclic");
                Assert.True(layers.First(l => l.FileName == "ws.tif").Weather, "ws is weather");

                InputLayer sourceCh = layers.First(l => l.FileName == "Auburn2_LF2024_ch.tif");
                Assert.Equal("LANDFIRE sources (LF2024)", sourceCh.Group, "source group");
                Assert.Equal(1.0, sourceCh.Scale, "the download's provenance says CH is not x 10");
                Assert.True(layers.TakeWhile(l => l.Group == InputLayerCatalog.CaseGroup).Count() == layers.Count - 2, "both sources last");

                //No namelist yet (case not built): the standard stems only.
                List<InputLayer> bare = InputLayerCatalog.List(caseDir, null, null);
                Assert.True(bare.Any(l => l.FileName == "fbfm40.tif") && !bare.Any(l => l.FileName == "fbfm40_roads101.tif"), "stems without a namelist");

                Assert.True(InputLayerCatalog.TryParseSourceName("Mati_Test_LF2022_cbd.tif", out string prefix, out string release, out string s)
                            && prefix == "Mati_Test" && release == "LF2022" && s == "cbd", "a name with underscores");
                Assert.True(!InputLayerCatalog.TryParseSourceName("Auburn2_LF2024_fccs.tif", out _, out _, out _), "not a source stem");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void ScalingFlags()
        {
            LayerUnits defaults = LayerUnits.FromNamelist(Namelist());
            InputLayer ch = InputLayerCatalog.Describe("CH_FILENAME", "ch", "ch.tif", defaults, InputLayerCatalog.CaseGroup);
            Assert.Equal(0.1, ch.Scale, "ELMFIRE's default CH_TIMES_10 is on");
            Assert.Equal("25 m", LayerValues.Describe(ch, 250, -9999), "250 stored is 25 m");

            LayerUnits real = LayerUnits.FromNamelist(Namelist("CC_IN_PERCENT = .FALSE.", "CH_TIMES_10 = .FALSE.", "CBD_TIMES_100 = .FALSE.",
                "WS_IN_KPH = .TRUE.", "WS_AT_10M = .TRUE.", "DEAD_MC_IN_PERCENT = .FALSE.", "DT_METEOROLOGY = 1800.0"));
            Assert.Equal("25 m", LayerValues.Describe(InputLayerCatalog.Describe("CH_FILENAME", "ch", "ch.tif", real, ""), 25, -9999), "real units");
            Assert.Equal("45%", LayerValues.Describe(InputLayerCatalog.Describe("CC_FILENAME", "cc", "cc.tif", real, ""), 0.45, -9999), "fraction cover");
            Assert.Equal("0.12 kg/m³", LayerValues.Describe(InputLayerCatalog.Describe("CBD_FILENAME", "cbd", "cbd.tif", real, ""), 0.12, -9999), "real CBD");
            Assert.Equal("0.12 kg/m³", LayerValues.Describe(InputLayerCatalog.Describe("CBD_FILENAME", "cbd", "cbd.tif", defaults, ""), 12, -9999), "CBD x 100");
            InputLayer ws = InputLayerCatalog.Describe("WS_FILENAME", "ws", "ws.tif", real, "");
            Assert.True(ws.Unit == "km/h" && ws.Note.Contains("10 m"), "km/h at 10 m");
            Assert.Equal("mi/h", InputLayerCatalog.Describe("WS_FILENAME", "ws", "ws.tif", defaults, "").Unit, "mi/h by default");
            Assert.Equal("8%", LayerValues.Describe(InputLayerCatalog.Describe("M1_FILENAME", "m1", "m1.tif", real, ""), 0.08, -9999), "fraction moisture");
            Assert.Equal("1-h dead fuel moisture", InputLayerCatalog.Describe("M1_FILENAME", "m1", "m1.tif", real, "").Label, "label");
            Assert.Equal(1800.0, real.DtMeteorology, "DT_METEOROLOGY");
            Assert.Equal("6 - 845 m", LayerValues.Range(InputLayerCatalog.Describe("DEM_FILENAME", "dem", "dem.tif", defaults, ""), 6, 845.3), "range");
            Assert.Equal("0 - 30.5 m", LayerValues.Range(ch, 0, 305), "range scaled");
        }

        private static void ValueWords()
        {
            var u = new LayerUnits();
            InputLayer asp = InputLayerCatalog.Describe("ASP_FILENAME", "asp", "asp.tif", u, "");
            InputLayer wd = InputLayerCatalog.Describe("WD_FILENAME", "wd", "wd.tif", u, "");
            InputLayer fuel13 = InputLayerCatalog.Describe("FBFM_FILENAME", "fbfm13", "fbfm13.tif", u, "");
            InputLayer mask = InputLayerCatalog.Describe("IGNITION_MASK_FILENAME", "ignition_mask", "m.tif", u, "");
            Assert.Equal("225° (SW)", LayerValues.Describe(asp, 225, -9999), "aspect");
            Assert.Equal("359° (N)", LayerValues.Describe(asp, 359, -9999), "aspect near north");
            Assert.Equal("270° (from W)", LayerValues.Describe(wd, 270, -9999), "wind from");
            Assert.Equal(LayerStyle.FuelModel13, fuel13.Style, "fbfm13 stem");
            Assert.Equal("8 - Closed timber litter", LayerValues.Describe(fuel13, 8, -9999), "anderson words");
            Assert.Equal("in (1)", LayerValues.Describe(mask, 1, -9999), "mask in");
            Assert.Equal("out (0)", LayerValues.Describe(mask, 0, -9999), "mask out");
            Assert.Equal("no data", LayerValues.Describe(asp, -9999, -9999), "nodata");
            Assert.Equal("no data", LayerValues.Describe(asp, 32767, 32767), "file nodata");
            Assert.Equal("2.35 h after ignition (8460 s)", LayerValues.ArrivalTime(8460, -9999), "arrival");
            Assert.Equal("not reached", LayerValues.ArrivalTime(-1, -9999), "not reached");
            Assert.Equal("outside", LayerValues.TriggerBoundary(0, -9999), "outside");
            Assert.Equal("band 3 of 72, +2 h", LayerValues.Band(3, 72, 3600), "hourly band");
            Assert.Equal("band 2 of 4, +30 min", LayerValues.Band(2, 4, 1800), "half-hourly band");
            Assert.Equal("NE", LayerValues.Compass(45), "compass");
        }

        private static void CellLookup()
        {
            var h = new AscRaster.Header { Ncols = 10, Nrows = 5, XllCorner = 1000, YllCorner = 2000, CellSize = 30, CellSizeY = 20 };
            Assert.True(LayerValues.TryCell(h, 1000.1, 2000.1, out int c, out int r) && c == 0 && r == 4, "south-west cell is the last line");
            Assert.True(LayerValues.TryCell(h, 1299.9, 2099.9, out c, out r) && c == 9 && r == 0, "north-east cell");
            Assert.True(LayerValues.TryCell(h, 1045, 2065, out c, out r) && c == 1 && r == 1, "rectangular cells use their own height");
            Assert.True(!LayerValues.TryCell(h, 1300, 2050, out _, out _), "the east edge is outside");
            Assert.True(!LayerValues.TryCell(h, 999, 2050, out _, out _), "west of the grid");

            LayerValues.DisplaySize(4000, 2000, 1024, out int w, out int hh);
            Assert.True(w == 1024 && hh == 512, "downsampled evenly");
            LayerValues.DisplaySize(700, 600, 1024, out w, out hh);
            Assert.True(w == 700 && hh == 600, "small raster kept");
        }

        private static void Colouring()
        {
            var data = new float[3, 2];
            data[0, 0] = 102; data[1, 0] = 0; data[2, 0] = -9999;
            data[0, 1] = 165; data[1, 1] = 250; data[2, 1] = 102;
            LayerImage img = LayerColoring.Colorize(data, -9999, LayerStyle.FuelModel40, 0.5f);
            Assert.Equal(3 * 2 * 4, img.Rgba.Length, "RGBA size");
            Assert.True(img.Rgba[0] == 255 && img.Rgba[1] == 211 && img.Rgba[2] == 115 && img.Rgba[3] == 128, "GR2 at half opacity, row 0 south");
            Assert.Equal(0, (int)img.Rgba[4 + 3], "code 0 transparent");
            Assert.Equal(0, (int)img.Rgba[8 + 3], "nodata transparent");
            Assert.True(img.Rgba[12] == 38 && img.Rgba[13] == 115 && img.Rgba[14] == 0, "TU5 on row 1");
            Assert.Equal(2, img.Classes[102], "codes counted");
            Assert.True(img.Classes.ContainsKey(250) && img.Classes.ContainsKey(0), "every code present is counted");

            var ramp = new float[2, 1] { { 10 }, { 20 } };
            LayerImage seq = LayerColoring.Colorize(ramp, -9999, LayerStyle.Sequential, 1f);
            Assert.True(seq.Rgba[0] == 68 && seq.Rgba[1] == 1 && seq.Rgba[2] == 84, "minimum at the dark end");
            Assert.True(seq.Rgba[4] == 253 && seq.Rgba[5] == 231 && seq.Rgba[6] == 37, "maximum at the light end");
            Assert.True(seq.Min == 10 && seq.Max == 20 && seq.ValidCells == 2, "range");

            var m = new float[2, 1] { { 0 }, { 1 } };
            LayerImage mask = LayerColoring.Colorize(m, -9999, LayerStyle.Mask, 1f);
            Assert.True(mask.Rgba[3] == 0 && mask.Rgba[7] == 255, "only cells in the mask show");

            LayerColoring.Cyclic(0, out byte r0, out byte g0, out byte b0);
            LayerColoring.Cyclic(360, out byte r1, out byte g1, out byte b1);
            Assert.True(r0 == r1 && g0 == g1 && b0 == b1, "cyclic ramp closes");
        }

        private static void GdalReads()
        {
            string root = Directory.CreateTempSubdirectory("preact-layers-gdal-").FullName;
            try
            {
                var grid = new MasterGrid
                {
                    Header = new AscRaster.Header { Ncols = 40, Nrows = 20, CellSize = 30, CellSizeY = 30, XllCorner = 700000, YllCorner = 4200000, NoDataValue = -9999 },
                    Epsg = "EPSG:32634",
                };
                var data = new float[40, 20];
                for (int x = 0; x < 40; ++x) for (int y = 0; y < 20; ++y) data[x, y] = x + 100 * y;
                string path = Path.Combine(root, "dem.tif");
                GeoTiffRasterWriter.WriteBand(grid, data, path);

                LayerRasterReader.Clear();
                LayerRasterReader.Display d = LayerRasterReader.ReadForDisplay(path, 1, 10);
                Assert.True(d.Data.GetLength(0) == 10 && d.Data.GetLength(1) == 5, "decimated to 10 x 5");
                Assert.True(d.Header.Ncols == 40 && d.Header.Nrows == 20, "full-resolution grid kept");
                Assert.Near(700000, d.Header.XllCorner, 1e-6, "corner x");
                Assert.Near(4200000, d.Header.YllCorner, 1e-6, "corner y");
                Assert.Equal(32634, d.Header.EpsgCode, "CRS");
                Assert.True(d.Data[0, 0] < 400 && d.Data[0, 4] >= 1500, "south row at y = 0");
                Assert.True(ReferenceEquals(d, LayerRasterReader.ReadForDisplay(path, 1, 10)), "cached");

                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
                Assert.True(!ReferenceEquals(d, LayerRasterReader.ReadForDisplay(path, 1, 10)), "read again after a rewrite");

                //Cell (col 5, from the north line 2) holds x = 5, y = 17 from the south: 1705.
                double px = 700000 + 5 * 30 + 15, py = 4200000 + 17 * 30 + 15;
                LayerRasterReader.Sample s = LayerRasterReader.SampleAt(path, 1, px, py);
                Assert.True(s.Inside && s.Col == 5 && s.Row == 2, $"cell (5, 2), got ({s.Col}, {s.Row})");
                Assert.Equal(1705.0, s.Value, "value at the cell");
                Assert.True(LayerValues.TryCell(d.Header, px, py, out int c, out int r) && c == s.Col && r == s.Row, "TryCell agrees with GDAL");
                Assert.True(!LayerRasterReader.SampleAt(path, 1, 690000, py).Inside, "off the raster");

                var bands = new List<float[,]> { new float[40, 20], new float[40, 20] };
                bands[1][5, 17] = 7f;
                string ws = Path.Combine(root, "ws.tif");
                GeoTiffRasterWriter.WriteBands(grid, bands, ws);
                LayerRasterReader.Sample s2 = LayerRasterReader.SampleAt(ws, 2, px, py);
                Assert.True(s2.Bands == 2 && s2.Value == 7.0, "the band asked for");
                Assert.Equal(2, LayerRasterReader.ReadForDisplay(ws, 2, 1024).Bands, "band count");
            }
            finally
            {
                LayerRasterReader.Clear();
                Directory.Delete(root, true);
            }
        }
    }
}
