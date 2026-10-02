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
            runner.Add("campaign: one made before per-realization evacuation seeds is told apart, one made now is not", EarlierCampaign);
            runner.Add("gui: messages and the generated namelist name the menus the GUI has, not the retired ones", CurrentMenuNames);
            runner.Add("results: a boundary's .prj is written for a UTM grid without PROJ's database, and its absence is said", CompanionPrj);
            runner.Add("gui: a two-decimal field shows three significant figures below 1, and text typed back as shown keeps the exact value", TwoDecimalFields);
        }

        /// <summary>Review R2 MI-2: WSMFEFF_LOW_MULT (0.011364) showed as "0.01", and any edit stored the rounded number.</summary>
        private static void TwoDecimalFields()
        {
            Assert.Equal("0.0114", WUInity.Workflow.NumberDisplay.Shown(60.0 / 5280.0, 2), "WSMFEFF_LOW_MULT shows three significant figures");
            Assert.Equal("%.3g", WUInity.Workflow.NumberDisplay.Format(60.0 / 5280.0, 2), "with ImGui's format to match");
            Assert.Equal("0.006", WUInity.Workflow.NumberDisplay.Shown(0.006, 2), "0.006 is not 0.01");
            Assert.Equal("0.001", WUInity.Workflow.NumberDisplay.Shown(0.001, 2), "EMBER_GR's 0.001 is not 0.00");
            Assert.Equal("0.5", WUInity.Workflow.NumberDisplay.Shown(0.5, 2), "a short value is shown as it is");
            Assert.Equal("12.60", WUInity.Workflow.NumberDisplay.Shown(12.5980556, 2), "two decimals from 1 up");
            Assert.Equal("900000000.00", WUInity.Workflow.NumberDisplay.Shown(900000000.0, 2), "large values in full");
            Assert.Equal("0.00", WUInity.Workflow.NumberDisplay.Shown(0.0, 2), "zero");
            Assert.True(WUInity.Workflow.NumberDisplay.IsRounded(60.0 / 5280.0, 2) && !WUInity.Workflow.NumberDisplay.IsRounded(0.5, 2),
                "the tooltip gives the exact value only when the field rounds it");

            Assert.Near(60.0 / 5280.0, WUInity.Workflow.NumberDisplay.Committed(60.0 / 5280.0, 0.0114, 2), 0.0,
                "text typed back as shown keeps the exact value");
            Assert.Near(12.5980556, WUInity.Workflow.NumberDisplay.Committed(12.5980556, 12.6, 2), 0.0, "also above 1");
            Assert.Near(0.02, WUInity.Workflow.NumberDisplay.Committed(60.0 / 5280.0, 0.02, 2), 0.0, "a real change is stored");
            Assert.Near(12.61, WUInity.Workflow.NumberDisplay.Committed(12.5980556, 12.61, 2), 0.0, "as typed");
        }

        /// <summary>
        /// docs.md 3.7: messages still sent the user to "Prepare data" and the "Hazards tab" (and "Run/edit > Hazards"),
        /// which the v1 GUI does not have; the Hazards one was written into every generated elmfire.data.
        /// </summary>
        private static List<string> CurrentMenuNames()
        {
            var warnings = new List<string>();
            string[] header = ElmfireNamelistBuilder.Build(new Input.ElmfireNamelistInput(), new ElmfireNamelistBuilder.CaseFacts());
            Assert.True(header.Take(8).Any(l => l.Contains("Fire > Fire behaviour")), "the namelist header names the Fire behaviour page");

            string repo = Program.FindRepositoryRoot();
            if (repo == null)
            {
                warnings.Add("the repository was not found; the source scan was skipped");
                return warnings;
            }

            //The GUI has no Hazards menu or tab any more, so the word itself in a message is a retired name (e2e N3: the
            //line-by-line scan missed "(Hazards " + "tab)", split across a line break).
            string[] retired = { "Prepare data", "Hazards", "Run/edit" };
            var found = new List<string>();
            foreach (string root in new[] { Path.Combine(repo, "PREACT"), Path.Combine(repo, "WUInity", "Assets", "WUInity") })
            {
                foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(repo, file).Replace('\\', '/');
                    if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("PREACT/PREACTtests/")) continue;
                    foreach ((int line, string text) in StringLiterals(File.ReadAllText(file)))
                    {
                        foreach (string name in retired)
                        {
                            if (System.Text.RegularExpressions.Regex.IsMatch(text, @"(^|\W)" + System.Text.RegularExpressions.Regex.Escape(name) + @"(\W|$)"))
                            {
                                found.Add($"{rel}:{line} \"{name}\"");
                            }
                        }
                    }
                }
            }
            Assert.True(found.Count == 0, "strings still name retired menus: " + string.Join(", ", found));

            //The scan itself: a literal split with + over lines is read whole, and code in an interpolation hole is not text.
            List<(int, string)> probe = StringLiterals("var m = \"Correct it (Hazards \"\n    + \"tab)\"; // \"Hazards tab\"\nvar n = $\"{sim.Hazards.Count} cells {(a ? \"x\" : \"y\")}\";");
            Assert.True(probe.Count == 2 && probe[0].Item2 == "Correct it (Hazards tab)" && probe[1].Item2 == " cells ",
                "the literal scan: " + string.Join(" | ", probe.Select(p => p.Item1 + ":" + p.Item2)));
            return warnings;
        }

        /// <summary>
        /// The text of every string literal in C# source <paramref name="code"/>, with its line: literals joined by
        /// <c>+</c> (across line breaks) as one, interpolation holes and comments left out.
        /// </summary>
        internal static List<(int Line, string Text)> StringLiterals(string code)
        {
            var result = new List<(int, string)>();
            var text = new System.Text.StringBuilder();
            int line = 1, start = 0;
            bool open = false;       //a literal (or a + chain of them) is being collected
            bool joinNext = false;   //a + followed the last literal
            int i = 0, n = code.Length;

            void Flush()
            {
                if (open) result.Add((start, text.ToString()));
                open = false;
                joinNext = false;
                text.Clear();
            }

            //Reads a "..." (or @"...") body starting after its opening quote, appending its text unless in a hole.
            void ReadString(bool verbatim, bool interpolated, bool keep)
            {
                while (i < n)
                {
                    char c = code[i];
                    if (c == '\n') ++line;
                    if (verbatim && c == '"' && i + 1 < n && code[i + 1] == '"') { if (keep) text.Append('"'); i += 2; continue; }
                    if (!verbatim && c == '\\' && i + 1 < n) { if (keep) text.Append(code[i + 1]); i += 2; continue; }
                    if (c == '"') { ++i; return; }
                    if (interpolated && c == '{')
                    {
                        if (i + 1 < n && code[i + 1] == '{') { if (keep) text.Append('{'); i += 2; continue; }
                        ++i;
                        int depth = 1;
                        while (i < n && depth > 0)
                        {
                            char h = code[i];
                            if (h == '\n') ++line;
                            if (h == '"') { ++i; ReadString(false, false, false); continue; }
                            if (h == '{') ++depth;
                            else if (h == '}') --depth;
                            ++i;
                        }
                        continue;
                    }
                    if (interpolated && c == '}' && i + 1 < n && code[i + 1] == '}') { if (keep) text.Append('}'); i += 2; continue; }
                    if (keep) text.Append(c);
                    ++i;
                }
            }

            while (i < n)
            {
                char c = code[i];
                if (c == '\n') { ++line; ++i; continue; }
                if (char.IsWhiteSpace(c)) { ++i; continue; }
                if (c == '/' && i + 1 < n && code[i + 1] == '/') { while (i < n && code[i] != '\n') ++i; continue; }
                if (c == '/' && i + 1 < n && code[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < n && !(code[i] == '*' && code[i + 1] == '/')) { if (code[i] == '\n') ++line; ++i; }
                    i += 2;
                    continue;
                }
                if (c == '\'')
                {
                    i += code[i + 1] == '\\' ? 3 : 2;
                    while (i < n && code[i] != '\'') ++i;
                    ++i;
                    Flush();
                    continue;
                }

                int prefix = 0;
                bool verbatim = false, interpolated = false;
                while (i + prefix < n && (code[i + prefix] == '@' || code[i + prefix] == '$') && prefix < 2)
                {
                    verbatim |= code[i + prefix] == '@';
                    interpolated |= code[i + prefix] == '$';
                    ++prefix;
                }
                if (i + prefix < n && code[i + prefix] == '"')
                {
                    if (!(open && joinNext)) { Flush(); open = true; start = line; }
                    joinNext = false;
                    i += prefix + 1;
                    ReadString(verbatim, interpolated, true);
                    continue;
                }

                if (c == '+' && open) { joinNext = true; ++i; continue; }
                Flush();
                ++i;
            }
            Flush();
            return result;
        }

        /// <summary>
        /// Review R6: the GUI marks a boundary with no .prj beside it "(earlier version)", and the .prj was left out without
        /// a word whenever GDAL could not describe the code - on Windows when Unity's GDAL does not find proj.db, every new
        /// boundary. A WGS 84 / UTM zone, which every case grid is, is now written from its definition, exactly as GDAL
        /// writes it; for any other code the reason is returned for the caller to say.
        /// </summary>
        private static void CompanionPrj()
        {
            foreach (int epsg in new[] { 32601, 32634, 32660, 32701, 32755, 32760 })
            {
                using (var srs = new OSGeo.OSR.SpatialReference(""))
                {
                    Assert.Equal(0, srs.ImportFromEPSG(epsg), "GDAL knows EPSG:" + epsg);
                    srs.ExportToWkt(out string gdal, null);
                    Assert.Equal(gdal, AscRaster.UtmWkt(epsg), "EPSG:" + epsg + " as GDAL writes it");
                }
            }
            Assert.True(AscRaster.UtmWkt(4326) == null && AscRaster.UtmWkt(2100) == null, "and nothing for a code that is not a UTM zone");

            string dir = Directory.CreateTempSubdirectory("preact-prj-").FullName;
            try
            {
                string asc = Path.Combine(dir, "0_trigger_boundary.asc");
                File.WriteAllLines(asc, new[] { "ncols 1", "nrows 1", "xllcorner 748200", "yllcorner 4203990", "cellsize 30", "NODATA_value -9999", "1" });
                Assert.True(AscRaster.WriteCompanionPrj(asc, 32634) == null, "written");
                File.WriteAllText(Path.ChangeExtension(asc, ".prj"), AscRaster.UtmWkt(32634));
                AscRaster.Read(asc, out AscRaster.Header header, out bool ok);
                Assert.True(ok && header.EpsgCode == 32634, "the definition's .prj reads back as EPSG:32634: " + header.EpsgCode);

                string other = Path.Combine(dir, "other.asc");
                File.Copy(asc, other);
                string why = AscRaster.WriteCompanionPrj(other, 1);
                Assert.True(why != null && why.Contains("without a .prj") && why.Contains("EPSG:1"), "a code nobody can describe is said: " + why);
                Assert.True(!File.Exists(Path.ChangeExtension(other, ".prj")), "and no .prj is made up for it");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static void EarlierCampaign()
        {
            string root = Directory.CreateTempSubdirectory("preact-campaigns-").FullName;
            try
            {
                string output = Path.Combine(root, CampaignLayout.OutputFolder);
                string now = Path.Combine(output, CampaignLayout.CampaignFolderName("mati", "0123abcd"));
                string before = Path.Combine(output, CampaignLayout.CampaignFolderName("mati", "8a356e1e"));
                Directory.CreateDirectory(now);
                Directory.CreateDirectory(before);

                //The manifest as the campaign CLI writes it, with the setting and (an older campaign's) without it.
                var campaign = new PREACTcli.Campaigns.Campaign { Folder = now, SettingsHash = new string('0', 64) };
                campaign.Settings["seed"] = "12345";
                campaign.Settings[CampaignLayout.EvacuationSeedSetting] = "seed + 2000000 + index";
                PREACTcli.Campaigns.CampaignManifest.Write(campaign);
                campaign.Settings.Remove(CampaignLayout.EvacuationSeedSetting);
                campaign.Folder = before;
                PREACTcli.Campaigns.CampaignManifest.Write(campaign);

                Assert.True(!CampaignLayout.PredatesEvacuationSeeds(now), "a campaign made now is not an earlier one");
                Assert.True(CampaignLayout.PredatesEvacuationSeeds(before), "one whose manifest has no evacuation seed rule is");
                Assert.True(CampaignLayout.DescribeEarlierCampaign(before).StartsWith("campaign_mati_8a356e1e was made by an earlier version"),
                    "and it is named: " + CampaignLayout.DescribeEarlierCampaign(before));

                //A campaign from before campaigns had folders: its results in _output, and no manifest at all.
                Assert.True(!CampaignLayout.PredatesEvacuationSeeds(output), "an _output with no campaign results is not a campaign");
                File.WriteAllText(Path.Combine(output, CampaignLayout.ConvergenceCsv), "run,realization_id,nSuccess,streak\n");
                Assert.True(CampaignLayout.PredatesEvacuationSeeds(output), "_output holding an old campaign's results is an earlier one");
                Assert.True(CampaignLayout.DescribeEarlierCampaign(output).StartsWith("The campaign whose results are in _output"),
                    CampaignLayout.DescribeEarlierCampaign(output));

                //A campaign folder that has just been made (no manifest, no results yet) is not called old.
                string starting = Path.Combine(output, CampaignLayout.CampaignFolderName("mati", "fedcba98"));
                Directory.CreateDirectory(starting);
                Assert.True(!CampaignLayout.PredatesEvacuationSeeds(starting), "a campaign that is only starting is not an earlier one");
                Assert.True(!CampaignLayout.PredatesEvacuationSeeds(Path.Combine(output, "missing")), "nor a folder that is not there");

                //The real ones, data permitting: WP1's campaign (before the rule) and FIX-A's (after it).
                string wp1 = "/home/claude/runs/wp1/mati/_output/campaign_mati_8a356e1e";
                string fixA = "/home/claude/runs/fix-a/mati/_output/campaign_mati_f05199c1";
                if (Directory.Exists(wp1)) Assert.True(CampaignLayout.PredatesEvacuationSeeds(wp1), "WP1's Mati campaign is an earlier one");
                if (Directory.Exists(fixA)) Assert.True(!CampaignLayout.PredatesEvacuationSeeds(fixA), "FIX-A's Mati campaign is not");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
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
                var data = new Input.WildfireData { RandomIgnition = new bool[g.Header.Ncols * g.Header.Nrows] };
                for (int i = 500; i < 540; ++i) data.RandomIgnition[i] = true;
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
                var small = new Input.WildfireData { RandomIgnition = new bool[400] };
                small.RandomIgnition[210] = true;
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
                //Older paintings, with the WUI area and initial ignition builds no longer write.
                var wuiCells = new bool[12];
                var initialCells = new bool[12];
                wuiCells[3] = true;
                initialCells[7] = true;
                var record = new GraphicalFireInput.PaintedGrid { XllCorner = 700000.0, YllCorner = 4200000.0, CellSize = 30.0, EpsgCode = 32634 };

                string withRecord = Path.Combine(folder, "recorded.gfi");
                string without = Path.Combine(folder, "legacy.gfi");
                LegacyPainting.Write(withRecord, 4, 3, wui: wuiCells, initial: initialCells, grid: record);
                LegacyPainting.Write(without, 4, 3, wui: wuiCells, initial: initialCells);
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
                Assert.True(input.LegacyWuiCells == 1 && input.LegacyInitialIgnitionCells == 1,
                    "and counts the WUI area and initial ignition it no longer uses");
            }
            finally
            {
                foreach (string f in Directory.GetFiles(folder)) { try { File.SetAttributes(f, FileAttributes.Normal); } catch { } }
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }
}
