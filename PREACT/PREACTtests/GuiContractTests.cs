using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>
    /// What the Unity GUI relies on the engine for and cannot test itself (it has no test runner): how a painting records
    /// its grid and who checks that record, and which files in a results folder are results.
    /// </summary>
    internal static class GuiContractTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("painting: the grid the painter records is the one the case build and the resampler accept, and only it", PaintingRecordAgrees);
            runner.Add("painting: a read-only .gfi loads, with its grid record; one without the record still loads", ReadOnlyPainting);
            runner.Add("results: a boundary or campaign raster is a result, the .prj/.aux.xml/.ovr beside it is not", ResultNames);
        }

        private static void ResultNames()
        {
            foreach (string sidecar in new[] { "0_trigger_boundary.prj", "ensemble_burn_probability.prj", "trigger_probability.asc.aux.xml",
                         "ensemble_arrival_mean.asc.ovr", "dem.tfw", "TRIGGER_PROBABILITY.PRJ" })
            {
                Assert.True(CampaignLayout.IsRasterSidecar(sidecar), sidecar + " is a sidecar");
                Assert.True(!Evacuation.EvacuationManager.IsBoundaryFile(sidecar, "trigger_boundary"), sidecar + " is no boundary");
            }
            foreach (string raster in new[] { "trigger_probability.asc", "ensemble_burn_probability.tif", "0_trigger_boundary.asc", "x.TIFF" })
            {
                Assert.True(CampaignLayout.IsRasterFile(raster) && !CampaignLayout.IsRasterSidecar(raster), raster + " is a raster");
            }
            Assert.True(!CampaignLayout.IsRasterFile("trigger_convergence.csv"), "the convergence CSV is not a raster");

            //What EvacuationManager.BoundaryFileName writes, for the default name, one with an extension, and per group.
            string single = "0_" + Evacuation.EvacuationManager.BoundaryFileName("trigger_boundary");
            string group = "0_" + Evacuation.EvacuationManager.BoundaryFileName("trigger_boundary", "west");
            string named = "3_" + Evacuation.EvacuationManager.BoundaryFileName("boundary.asc", "east");
            Assert.True(Evacuation.EvacuationManager.IsBoundaryFile(single, "trigger_boundary"), single);
            Assert.True(Evacuation.EvacuationManager.IsBoundaryFile(group, "trigger_boundary"), group + " (a group's)");
            Assert.True(Evacuation.EvacuationManager.IsBoundaryFile(named, "boundary.asc"), named + " (OutputName with its extension)");
            Assert.True(Evacuation.EvacuationManager.IsBoundaryFile("0_trigger_boundary.asc", null), "an empty OutputName is the default");
            Assert.True(Evacuation.EvacuationManager.IsBoundaryFile("0_trigger_boundary", "trigger_boundary"), "a boundary from before they had .asc");
            Assert.True(Evacuation.EvacuationManager.IsBoundaryFile("mati_prob_0000001_0_trigger_boundary", "trigger_boundary"),
                "an old campaign's boundary in _output");
            Assert.True(!Evacuation.EvacuationManager.IsBoundaryFile("0_trigger_boundary.csv", "trigger_boundary"), "not a CSV of it");
            Assert.True(!Evacuation.EvacuationManager.IsBoundaryFile("0_boundary.asc", "trigger_boundary"), "not another name");
        }

        /// <summary>
        /// The painter records its grid from the raster it paints on as <c>PaintedMaskResampler.Grid.FromRaster(..).ToPaintedGrid()</c>.
        /// That record has to be exactly what the case builder compares, and what the resampler checks a source against
        /// and writes for its target.
        /// </summary>
        private static void PaintingRecordAgrees()
        {
            using (var c = new PipelineTests.SyntheticCase())
            {
                string caseDir = Path.Combine(c.Folder, "case");
                ElmfireCaseBuilder.Build(c.Options(caseDir, 150.0, new List<string>())).GetAwaiter().GetResult();
                string caseDem = ElmfireStems.Tif(Path.Combine(caseDir, "inputs"), ElmfireStems.Dem);
                MasterGrid g = MasterGrid.FromRasterFile(caseDem);

                //The painter's record of the case grid.
                GraphicalFireInput.PaintedGrid painted = PaintedMaskResampler.Grid.FromRaster(caseDem).ToPaintedGrid();
                Assert.True(painted.EpsgCode == 32634 && painted.CellSize == 30.0, "the record carries the CRS and cell size: " + painted.Describe());
                Assert.True(ElmfireCaseBuilder.DescribePaintedGridMismatch(painted, g) == null, "the builder takes it for the case grid");

                //The same size ten cells east: another grid, by the record.
                string shifted = Path.Combine(c.Folder, "shifted_dem.tif");
                var moved = new MasterGrid
                {
                    Header = new AscRaster.Header
                    {
                        Ncols = g.Header.Ncols, Nrows = g.Header.Nrows, CellSize = 30, CellSizeY = 30, NoDataValue = -9999,
                        XllCorner = g.XMin + 300.0, YllCorner = g.YMin,
                    },
                    Epsg = g.Epsg,
                };
                GeoTiffRasterWriter.WriteBand(moved, new float[g.Header.Ncols, g.Header.Nrows], shifted);
                string why = PaintedMaskResampler.Grid.FromRaster(shifted).DescribeMismatch(painted);
                Assert.True(why != null && why.Contains("300 m east"), "the resampler's grid says it is elsewhere: " + why);

                //A painting saved with the record, as the painter now saves it.
                var data = new Input.WildfireData { WuiArea = new bool[g.Header.Ncols * g.Header.Nrows] };
                for (int i = 500; i < 540; ++i) data.WuiArea[i] = true;
                string gfi = Path.Combine(c.Folder, "painted.gfi");
                GraphicalFireInput.SaveGraphicalFireInput(gfi, data, g.Header.Ncols, g.Header.Nrows, painted);

                //Moving it from the shifted raster is refused: that raster is its size, but not its grid.
                string refused = null;
                try
                {
                    PaintedMaskResampler.ResampleFile(gfi, shifted, caseDem, Path.Combine(c.Folder, "from_shifted.gfi"));
                }
                catch (InvalidDataException e)
                {
                    refused = e.Message;
                }
                Assert.True(refused != null && refused.Contains("not the grid to move it from"), "moving it from another grid is refused: " + refused);

                //Moving it from its own grid onto the shifted one records the shifted grid, which the builder then checks.
                string onShifted = Path.Combine(c.Folder, "on_shifted.gfi");
                PaintedMaskResampler.ResampleFile(gfi, caseDem, shifted, onShifted);
                GraphicalFireInput.PaintedGrid written = GraphicalFireInput.ReadGrid(onShifted, out int w, out int h);
                Assert.True(written != null && w == g.Header.Ncols && h == g.Header.Nrows, "the moved painting records its grid");
                Assert.True(PaintedMaskResampler.Grid.FromRaster(shifted).DescribeMismatch(written) == null, "the shifted one");
                string onCase = ElmfireCaseBuilder.DescribePaintedGridMismatch(written, g);
                Assert.True(onCase != null && onCase.Contains("300 m west"), "and the builder no longer takes it for the case grid: " + onCase);

                //A landscape with non-square cells is no grid the builder can place a painting from; that is said in the
                //painting's error rather than ending the build with the grid reader's own message. (Mati's 27.592 x 27.616 m
                //DEM is within the grid reader's tolerance, so it is read as square.)
                string rectangular = Path.Combine(c.Folder, "rectangular_dem.tif");
                var rect = new MasterGrid
                {
                    Header = new AscRaster.Header
                    {
                        Ncols = 20, Nrows = 20, CellSize = 25.0, CellSizeY = 30.0, NoDataValue = -9999,
                        XllCorner = g.XMin, YllCorner = g.YMin,
                    },
                    Epsg = g.Epsg,
                };
                WriteRectangular(rect, rectangular);
                var small = new Input.WildfireData { WuiArea = new bool[400] };
                small.WuiArea[210] = true;
                string legacy = Path.Combine(c.Folder, "legacy.gfi");
                GraphicalFireInput.SaveGraphicalFireInput(legacy, small, 20, 20);
                ElmfireCaseBuilder.Options o = c.Options(caseDir, 150.0, new List<string>());
                o.PaintedMasksPath = legacy;
                o.PaintedMasksGridPath = rectangular;
                string error = null;
                try
                {
                    ElmfireCaseBuilder.Build(o).GetAwaiter().GetResult();
                }
                catch (InvalidDataException e)
                {
                    error = e.Message;
                }
                Assert.True(error != null && error.Contains("cannot hold a painting") && error.Contains("Move the painting"),
                    "the painting's error names the unusable landscape: " + error);
            }
        }

        /// <summary>A GeoTIFF with non-square cells, which <see cref="GeoTiffRasterWriter"/> (square cells) cannot write.</summary>
        private static void WriteRectangular(MasterGrid grid, string path)
        {
            OSGeo.GDAL.Gdal.AllRegister();
            using (OSGeo.GDAL.Driver driver = OSGeo.GDAL.Gdal.GetDriverByName("GTiff"))
            using (OSGeo.GDAL.Dataset ds = driver.Create(path, grid.Header.Ncols, grid.Header.Nrows, 1, OSGeo.GDAL.DataType.GDT_Float32, null))
            {
                ds.SetGeoTransform(new[]
                {
                    grid.Header.XllCorner, grid.Header.CellSize, 0.0,
                    grid.Header.YllCorner + grid.Header.Nrows * grid.Header.CellSizeY, 0.0, -grid.Header.CellSizeY,
                });
                using (var srs = new OSGeo.OSR.SpatialReference(""))
                {
                    srs.ImportFromEPSG(GraphicalFireInput.PaintedGrid.EpsgNumber(grid.Epsg));
                    srs.ExportToWkt(out string wkt, null);
                    ds.SetProjection(wkt);
                }
                ds.GetRasterBand(1).Fill(100.0, 0.0);
            }
        }

        private static void ReadOnlyPainting()
        {
            string folder = Directory.CreateTempSubdirectory("preact-gfi-").FullName;
            try
            {
                var data = new Input.WildfireData { WuiArea = new bool[12], InitialIgnition = new bool[12] };
                data.WuiArea[3] = true;
                data.InitialIgnition[7] = true;
                var record = new GraphicalFireInput.PaintedGrid { XllCorner = 700000.0, YllCorner = 4200000.0, CellSize = 30.0, EpsgCode = 32634 };

                string withRecord = Path.Combine(folder, "recorded.gfi");
                string without = Path.Combine(folder, "legacy.gfi");
                GraphicalFireInput.SaveGraphicalFireInput(withRecord, data, 4, 3, record);
                GraphicalFireInput.SaveGraphicalFireInput(without, data, 4, 3);
                foreach (string f in new[] { withRecord, without })
                {
                    if (OperatingSystem.IsWindows()) File.SetAttributes(f, FileAttributes.ReadOnly);
                    else File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }

                //Held open for reading elsewhere as well, as the GUI's model does while it counts the cells.
                using (new FileStream(withRecord, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    GraphicalFireInput.LoadGraphicalFireInput(withRecord, out int ncols, out int nrows, out bool[] wui, out bool[] _,
                        out bool[] initial, out bool[] _, out GraphicalFireInput.PaintedGrid grid, out bool ok);
                    Assert.True(ok && ncols == 4 && nrows == 3 && wui[3] && initial[7], "a read-only painting loads");
                    Assert.True(grid != null && grid.XllCorner == 700000.0 && grid.EpsgCode == 32634, "with its grid record");
                }

                GraphicalFireInput.LoadGraphicalFireInput(without, out int _, out int _, out bool[] _, out bool[] _, out bool[] _, out bool[] _,
                    out GraphicalFireInput.PaintedGrid none, out bool legacyOk);
                Assert.True(legacyOk && none == null, "a painting from before the record loads, and says it has none");
                Assert.True(GraphicalFireInput.ReadGrid(withRecord, out int rw, out int rh)?.YllCorner == 4200000.0 && rw == 4 && rh == 3,
                    "ReadGrid gives the size and the record without the masks");
                Assert.True(GraphicalFireInput.ReadGrid(without, out int _, out int _) == null, "and none for an older file");

                var input = new Input.WildfireData();
                input.LoadGraphicalFireInput(new Input.WildfireModuleInput(), withRecord, false, out bool loaded);
                Assert.True(loaded && input.PaintedGrid != null && input.PaintedGrid.CellSize == 30.0,
                    "a scenario's loaded painting keeps the record, for the run's own check");
            }
            finally
            {
                foreach (string f in Directory.GetFiles(folder)) { try { File.SetAttributes(f, FileAttributes.Normal); } catch { } }
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }
}
