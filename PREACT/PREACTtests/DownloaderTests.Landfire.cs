using System.IO.Compression;
using System.Text;
using OSGeo.GDAL;
using PREACT.Input;
using PREACT.Math;
using PREACT.Tools;
using PREACT.Utility;

namespace PREACT.Tests
{
    internal static partial class DownloaderTests
    {
        private static void RegisterLandfire(Runner runner)
        {
            runner.Add("landfire: releases LFPS serves, closest to the scenario's year (earlier on a tie), by region; the layer list", LandfireReleases);
            runner.Add("landfire: the real Auburn2 LFPS zip, from a stub LFPS, becomes Int16 fbfm40 and cc/ch/cbh/cbd with the flags its units call for", LandfireRealZip);
            runner.Add("landfire: a submit that fails once is retried, a failed job says LFPS's message, a cut-off zip is fetched again", LandfireFailures);
            runner.Add("landfire: band descriptions in every LFPS form, units from the aux.xml, unlabelled bands by request order", LandfireBands);
            runner.Add("landfire: the contact e-mail is the user's (explicit, LANDFIRE_EMAIL, per-user file), never the old hard-coded one", LandfireContactEmail);
            runner.Add("landfire: [ELMFIRE] LandfireVersion is read, normalised, kept and written; a bad value keeps closest", LandfireVersionKey);
        }

        /// <summary>The real LFPS result for Auburn2 (Sep 2026): one 9-band GeoTIFF, LF2024 fuel and canopy.</summary>
        private static string RealLfpsZip()
        {
            string path = Environment.GetEnvironmentVariable("PREACT_TEST_LFPS_ZIP")
                          ?? "/home/claude/cases/auburn2/downloads/landfire/48622145-1b8e-474c-ac4f-ddbcd2ee6526.zip";
            return File.Exists(path) ? path : null;
        }

        private static void LandfireReleases()
        {
            var conus = LandfireVersions.Region.Conus;
            Assert.Equal("LF2025", LandfireVersions.Resolve("closest", 2026, conus, out string why).Name, "2026 in CONUS");
            Assert.True(why.Contains("closest to the scenario's year 2026"), "it says why: " + why);
            Assert.Equal("LF2024", LandfireVersions.Resolve("closest", 2026, LandfireVersions.Region.Hawaii, out _).Name, "LF2025 has no Hawaii");
            Assert.Equal("LF2016", LandfireVersions.Resolve("closest", 2019, conus, out _).Name, "a tie goes to the release before");
            Assert.Equal("LF2022", LandfireVersions.Resolve("closest", 2020, conus, out _).Name, "2020: there is no LF2020 fuel");
            Assert.Equal("LF2023", LandfireVersions.Resolve("closest", 2023, conus, out _).Name, "its own year");
            Assert.Equal("LF2016", LandfireVersions.Resolve("closest", 2007, conus, out _).Name, "before every release");
            Assert.True(LandfireVersions.Resolve(" ", 2022, conus, out _).Name == "LF2022", "an empty setting is closest");
            Assert.True(LandfireVersions.Resolve("closest", 2021, conus, out why).Name == "LF2022" && why.Contains("historic fire"),
                "a release after the scenario says so: " + why);

            Assert.Equal("LF2022", LandfireVersions.Resolve("lf 2022", 2026, conus, out why).Name, "a named release is taken as named");
            Assert.True(why.Contains("LandfireVersion asks"), "and says it was asked for");
            Assert.Equal("LF2016", LandfireVersions.Normalise("LF2016 Remap"), "the remap by its LANDFIRE name");
            Assert.Equal("LF2023", LandfireVersions.Normalise("2023"), "a bare year");
            Assert.True(LandfireVersions.Normalise("LF2020") == null && LandfireVersions.Normalise("newest") == null, "no LF2020 fuel, no 'newest'");

            bool refused = false;
            try { LandfireVersions.Resolve("LF2025", 2026, LandfireVersions.Region.Hawaii, out _); }
            catch (ArgumentException e) { refused = e.Message.Contains("Hawaii"); }
            Assert.True(refused, "a release that does not cover the region is refused");
            refused = false;
            try { LandfireVersions.Resolve("LF2019", 2019, conus, out _); }
            catch (ArgumentException e) { refused = e.Message.Contains("LF2016, LF2022"); }
            Assert.True(refused, "an unknown release is refused, listing the real ones");

            Assert.Equal("LF2020_Elev;LF2020_SlpD;LF2020_Asp;LF2022_FBFM40;LF2022_CC;LF2022_CH;LF2022_CBH;LF2022_CBD",
                LandfireVersions.LayerList(LandfireVersions.All[1], false), "the layer list, in LANDFIRE's landscape order");
            Assert.True(LandfireVersions.LayerList(LandfireVersions.All[0], true).Contains("LF2016_FBFM13"), "Anderson 13");

            Assert.Equal(LandfireVersions.Region.Conus, LandfireVersions.RegionOf(new Vector2d(38.82, -121.19), new Vector2d(38.99, -120.95)), "Auburn");
            Assert.Equal(LandfireVersions.Region.None, LandfireVersions.RegionOf(new Vector2d(38.0, 23.9), new Vector2d(38.1, 24.0)), "Mati");
        }

        /// <summary>A stub of LFPS: submit, a status that queues, runs and succeeds, and the result zip.</summary>
        internal sealed class StubLfps : IDisposable
        {
            public readonly StubHttpServer Server;
            public string SubmitQuery;
            public int StatusCalls, SubmitCalls, ZipCalls;
            public int SubmitFailures;       //answered 500 this many times first
            public bool FailJob;             //the job fails instead of succeeding
            public int CutZip;               //the zip is cut short this many times first
            private readonly byte[] _zip;
            public const string JobId = "48622145-1b8e-474c-ac4f-ddbcd2ee6526";

            public StubLfps(byte[] zip)
            {
                _zip = zip;
                Server = new StubHttpServer((r, body) =>
                {
                    string path = r.Url.AbsolutePath;
                    if (path == "/api/job/submit")
                    {
                        if (Interlocked.Increment(ref SubmitCalls) <= SubmitFailures)
                        {
                            return StubHttpServer.Reply.Text(500, "{\"message\":\"Internal Server Error\"}", "application/json");
                        }
                        SubmitQuery = r.Url.Query;
                        return StubHttpServer.Reply.Text(200, "{\"jobId\":\"" + JobId + "\",\"status\":\"Pending\"}", "application/json");
                    }
                    if (path == "/api/job/status")
                    {
                        int n = Interlocked.Increment(ref StatusCalls);
                        if (n == 1) return StubHttpServer.Reply.Text(200, "{\"jobId\":\"" + JobId + "\",\"status\":\"Pending\",\"queuePosition\":2,\"messages\":[]}", "application/json");
                        if (n == 2) return StubHttpServer.Reply.Text(200, "{\"jobId\":\"" + JobId + "\",\"status\":\"Executing\",\"queuePosition\":-1,\"messages\":[]}", "application/json");
                        if (FailJob)
                        {
                            return StubHttpServer.Reply.Text(200, "{\"jobId\":\"" + JobId + "\",\"status\":\"Failed\",\"queuePosition\":-1,"
                                + "\"messages\":[{\"type\":\"Error\",\"description\":\"Layer LF2019_FBFM40 is not a valid product.\"}]}", "application/json");
                        }
                        return StubHttpServer.Reply.Text(200, "{\"jobId\":\"" + JobId + "\",\"status\":\"Succeeded\",\"queuePosition\":-1,"
                            + "\"outputFile\":\"" + Server.BaseUrl + "/downloads/" + JobId + ".zip\",\"messages\":[]}", "application/json");
                    }
                    if (path == "/downloads/" + JobId + ".zip")
                    {
                        var reply = StubHttpServer.Reply.Bytes(_zip, "application/zip");
                        if (Interlocked.Increment(ref ZipCalls) <= CutZip) reply.CutAfter = _zip.Length / 3;
                        return reply;
                    }
                    return null;
                });
            }

            public void Dispose() => Server.Dispose();
        }

        private static LandfireFuels.Options AuburnOptions(string root, string serviceUrl, List<string> log, string version = "LF2024")
        {
            //The scenario's own numbers: its area of interest, 2 km padding, a 2026 start.
            return new LandfireFuels.Options
            {
                Root = root,
                Name = "Auburn2",
                LowerLeft = new Vector2d(38.818923064967734, -121.19282894713631),
                DomainSize = new Vector2d(20939.844352100859, 18542.447478438728),
                ScenarioYear = 2026,
                PaddingMetres = 2000,
                CaseDirectory = Path.Combine(root, "elmfire"),
                Version = version,
                Email = "nick@example.com",
                ServiceUrl = serviceUrl + "/api",
                PollInterval = TimeSpan.FromMilliseconds(20),
                MaxWait = TimeSpan.FromMinutes(2),
                FirstBackoff = TimeSpan.FromMilliseconds(20),
                Log = m => { lock (log) log.Add(m); },
            };
        }

        private static List<string> LandfireRealZip()
        {
            string real = RealLfpsZip();
            List<string> warnings = new List<string>();
            string scratch = TempFolder("preact-lfzip-");
            try
            {
                string zip = real ?? SyntheticLfpsZip(scratch, null);
                if (real == null) warnings.Add("the real Auburn2 LFPS zip is not here; a synthetic one of the same layout was used");

                string root = Path.Combine(scratch, "Auburn2");
                //A case that already has fuel and canopy from somewhere else: they must not survive the download.
                string inputs = Path.Combine(root, "elmfire", "inputs");
                Directory.CreateDirectory(inputs);
                foreach (string stem in new[] { "fbfm40", "cc", "ch", "cbh", "cbd", "dem" }) File.WriteAllText(Path.Combine(inputs, stem + ".tif"), "old");
                File.WriteAllText(Path.Combine(inputs, "fbfm40.bsq"), "old");

                using (var lfps = new StubLfps(File.ReadAllBytes(zip)))
                {
                    var log = new List<string>();
                    LandfireFuels.Result result = LandfireFuels.DownloadAsync(AuburnOptions(root, lfps.Server.BaseUrl, log)).GetAwaiter().GetResult();

                    //What was asked for.
                    string query = Uri.UnescapeDataString(lfps.SubmitQuery ?? "");
                    Assert.True(query.Contains("Email=nick@example.com") && !query.Contains("brand.lth.se"), "the user's e-mail: " + query);
                    Assert.True(query.Contains("Layer_List=LF2020_Elev;LF2020_SlpD;LF2020_Asp;LF2024_FBFM40;LF2024_CC;LF2024_CH;LF2024_CBH;LF2024_CBD")
                                && !query.Contains("FCCS"), "the release asked for: " + query);
                    Assert.True(query.Contains("Output_Projection=32610"), "in the case's UTM zone");

                    //The area: the padded domain, grown to the UTM grid cut from it.
                    string aoi = query.Substring(query.IndexOf("Area_of_Interest=", StringComparison.Ordinal) + 17);
                    aoi = aoi.Substring(0, aoi.IndexOf('&'));
                    double[] box = aoi.Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    (Vector2d sw, Vector2d ne) = ElmfireCaseBuilder.PaddedBounds(new ElmfireCaseBuilder.Options
                    {
                        LowerLeftLatLon = new Vector2d(38.818923064967734, -121.19282894713631),
                        DomainSizeMetres = new Vector2d(20939.844352100859, 18542.447478438728),
                        PaddingMetres = 2000,
                    });
                    Assert.True(box[0] < sw.y && box[1] < sw.x && box[2] > ne.y && box[3] > ne.x,
                        $"the padded domain {sw.x:F5},{sw.y:F5}-{ne.x:F5},{ne.y:F5} is inside what was asked: {aoi}");
                    AssertCoversUtmGrid(new Vector2d(box[1], box[0]), new Vector2d(box[3], box[2]), sw, ne, 32610, "the LANDFIRE request");

                    //Only state changes are logged.
                    Assert.True(log.Count(l => l.StartsWith("LFPS job: ")) == 3, "Pending, Executing, Succeeded: " + string.Join(" | ", log));

                    //What came back.
                    Assert.Equal("LF2024", result.Release, "the release");
                    Assert.Equal("fbfm40", result.FuelStem, "the fuel stem");
                    string fuel = Path.Combine(root, result.Layers["fbfm40"]);
                    Assert.Equal("downloads/landfire/Auburn2_LF2024_fbfm40.tif", result.Layers["fbfm40"], "named after the scenario and the release");
                    foreach (string stem in new[] { "cc", "ch", "cbh", "cbd" })
                    {
                        Assert.Equal($"downloads/landfire/Auburn2_LF2024_{stem}.tif", result.Layers[stem], stem);
                    }

                    Gdal.AllRegister();
                    string raster = Directory.GetFiles(Path.Combine(root, "downloads", "landfire", StubLfps.JobId), "*.tif").Single();
                    using (Dataset src = Gdal.Open(raster, Access.GA_ReadOnly))
                    {
                        CompareBand(src, 4, fuel, DataType.GDT_Int16, "fbfm40");
                        CompareBand(src, 5, Path.Combine(root, result.Layers["cc"]), DataType.GDT_Float32, "cc");
                        CompareBand(src, 6, Path.Combine(root, result.Layers["ch"]), DataType.GDT_Float32, "ch");
                        CompareBand(src, 7, Path.Combine(root, result.Layers["cbh"]), DataType.GDT_Float32, "cbh");
                        CompareBand(src, 8, Path.Combine(root, result.Layers["cbd"]), DataType.GDT_Float32, "cbd");
                    }
                    Assert.True(File.Exists(Path.Combine(root, "downloads", "landfire", StubLfps.JobId + ".zip")), "the zip is kept");
                    Assert.True(File.Exists(raster + ".aux.xml"), "the aux.xml is unpacked beside the raster");

                    //The flags, from the units the aux.xml states.
                    Assert.True(result.Scaling.CcInPercent && result.Scaling.ChTimes10 && result.Scaling.CbhTimes10 && result.Scaling.CbdTimes100,
                        "LANDFIRE's scaled integers: all four flags on");
                    Assert.True(log.Any(l => l.Contains("CH is in Meters * 10: CH_TIMES_10 on"))
                                && log.Any(l => l.Contains("CBD is in Kilograms per cubic meter * 100: CBD_TIMES_100 on"))
                                && log.Any(l => l.Contains("CC is in Percent: CC_IN_PERCENT on")),
                        "decided from the stated units: " + string.Join(" | ", log.Where(l => l.Contains("_"))));
                    Assert.True(result.Warnings.Count == 0, "no warnings for a clean download: " + string.Join("; ", result.Warnings));

                    //The case's old layers are gone (its terrain is not), so the next build warps these.
                    Assert.True(!File.Exists(Path.Combine(inputs, "fbfm40.tif")) && !File.Exists(Path.Combine(inputs, "cc.tif"))
                                && !File.Exists(Path.Combine(inputs, "fbfm40.bsq")) && File.Exists(Path.Combine(inputs, "dem.tif")),
                        "the superseded fuel and canopy are removed, the DEM kept");
                    Assert.Equal(5, result.RemovedCaseLayers.Count, "five removed");

                    //The record.
                    string provenance = File.ReadAllText(Path.Combine(root, result.ProvenanceFile));
                    Assert.True(provenance.Contains("Release=LF2024") && provenance.Contains("JobId=" + StubLfps.JobId)
                                && provenance.Contains("CH_TIMES_10=.TRUE.") && !provenance.Contains("nick@example.com"),
                        "the provenance names the release and the job, and not the e-mail: " + provenance);

                    //Named on the scenario.
                    var e = new ElmfireInput { FuelModelStandard = ElmfireInput.FuelModelStandards.FBFM13 };
                    LandfireFuels.Apply(result, e);
                    Assert.True(e.FuelModelFile == result.Layers["fbfm40"] && e.FuelModelStandard == ElmfireInput.FuelModelStandards.FBFM40
                                && e.CanopyHeightFile == result.Layers["ch"] && e.CanopyBulkDensityFile == result.Layers["cbd"],
                        "the source layers name them");
                    Assert.True(e.Namelist.CH_TIMES_10 && e.Namelist.CBH_TIMES_10 && e.Namelist.CBD_TIMES_100 && e.Namelist.CC_IN_PERCENT,
                        "and the namelist flags match");

                    //A second download of the same job unpacks over the first instead of failing on the existing file.
                    LandfireFuels.Result again = LandfireFuels.DownloadAsync(AuburnOptions(root, lfps.Server.BaseUrl, log)).GetAwaiter().GetResult();
                    Assert.Equal(result.Layers["fbfm40"], again.Layers["fbfm40"], "the same layers again");
                }
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch { }
            }
            return warnings;
        }

        private static void CompareBand(Dataset source, int band, string written, DataType type, string what)
        {
            using (Dataset ds = Gdal.Open(written, Access.GA_ReadOnly))
            using (Band a = source.GetRasterBand(band))
            using (Band b = ds.GetRasterBand(1))
            {
                Assert.Equal(type, b.DataType, what + " type");
                Assert.True(ds.RasterXSize == source.RasterXSize && ds.RasterYSize == source.RasterYSize, what + " size");
                double[] gtA = new double[6], gtB = new double[6];
                source.GetGeoTransform(gtA);
                ds.GetGeoTransform(gtB);
                Assert.True(gtA.Zip(gtB, (x, y) => System.Math.Abs(x - y)).Max() < 1e-6, what + " georeferencing");
                Assert.True(ds.GetProjection().Contains("32610") || ds.GetProjection().Contains("UTM zone 10N"), what + " CRS");

                int n = source.RasterXSize * source.RasterYSize;
                var va = new double[n];
                var vb = new double[n];
                a.ReadRaster(0, 0, source.RasterXSize, source.RasterYSize, va, source.RasterXSize, source.RasterYSize, 0, 0);
                b.ReadRaster(0, 0, source.RasterXSize, source.RasterYSize, vb, source.RasterXSize, source.RasterYSize, 0, 0);
                for (int i = 0; i < n; ++i)
                {
                    if (va[i] != vb[i]) throw new TestFailure($"{what}: cell {i} is {vb[i]}, the band has {va[i]}");
                }
                b.GetNoDataValue(out double nodata, out int has);
                Assert.True(has != 0 && nodata == -9999, what + " nodata -9999");
                Assert.True(!string.IsNullOrEmpty(ds.GetMetadataItem("LANDFIRE_LAYER", "")), what + " records its LFPS layer");
            }
        }

        private static void LandfireFailures()
        {
            string scratch = TempFolder("preact-lffail-");
            try
            {
                string zip = SyntheticLfpsZip(scratch, null);
                string root = Path.Combine(scratch, "s");
                var log = new List<string>();

                using (var lfps = new StubLfps(File.ReadAllBytes(zip)) { SubmitFailures = 1, CutZip = 1 })
                {
                    LandfireFuels.Result result = LandfireFuels.DownloadAsync(AuburnOptions(root, lfps.Server.BaseUrl, log)).GetAwaiter().GetResult();
                    Assert.True(lfps.SubmitCalls == 2 && log.Any(l => l.Contains("HTTP 500") && l.Contains("trying again")), "a 500 on submit is retried");
                    Assert.True(lfps.ZipCalls == 2 && File.Exists(Path.Combine(root, result.Layers["fbfm40"])), "a cut-off zip is fetched again");
                    Assert.True(!Directory.GetFiles(Path.Combine(root, "downloads", "landfire"), "*.part").Any(), "no working copy is left");
                }

                using (var lfps = new StubLfps(File.ReadAllBytes(zip)) { FailJob = true })
                {
                    Exception failed = null;
                    try { LandfireFuels.DownloadAsync(AuburnOptions(Path.Combine(scratch, "t"), lfps.Server.BaseUrl, log)).GetAwaiter().GetResult(); }
                    catch (Exception e) { failed = e; }
                    Assert.True(failed != null && failed.Message.Contains("failed: Layer LF2019_FBFM40 is not a valid product."),
                        "a failed job says what LFPS said: " + failed?.Message);
                    Assert.True(!Directory.Exists(Path.Combine(scratch, "t", "downloads", "landfire"))
                                || !Directory.GetFiles(Path.Combine(scratch, "t", "downloads", "landfire"), "*.tif", SearchOption.AllDirectories).Any(),
                        "and leaves no layers");
                }

                //No e-mail anywhere: refused before anything is asked.
                string saved = Environment.GetEnvironmentVariable(LandfireContact.Variable);
                LandfireContact.SettingsPathOverride = Path.Combine(scratch, "no-settings.txt");
                try
                {
                    Environment.SetEnvironmentVariable(LandfireContact.Variable, null);
                    using (var lfps = new StubLfps(File.ReadAllBytes(zip)))
                    {
                        var options = AuburnOptions(Path.Combine(scratch, "u"), lfps.Server.BaseUrl, log);
                        options.Email = null;
                        Exception failed = null;
                        try { LandfireFuels.DownloadAsync(options).GetAwaiter().GetResult(); }
                        catch (Exception e) { failed = e; }
                        Assert.True(failed != null && failed.Message.Contains("contact e-mail") && lfps.SubmitCalls == 0,
                            "no e-mail, no request: " + failed?.Message);
                    }
                }
                finally
                {
                    LandfireContact.SettingsPathOverride = null;
                    Environment.SetEnvironmentVariable(LandfireContact.Variable, saved);
                }

                //Outside the United States: refused before anything is asked.
                using (var lfps = new StubLfps(File.ReadAllBytes(zip)))
                {
                    var options = AuburnOptions(Path.Combine(scratch, "v"), lfps.Server.BaseUrl, log);
                    options.LowerLeft = new Vector2d(38.0123, 23.9);
                    Exception failed = null;
                    try { LandfireFuels.DownloadAsync(options).GetAwaiter().GetResult(); }
                    catch (Exception e) { failed = e; }
                    Assert.True(failed != null && failed.Message.Contains("United States only") && lfps.SubmitCalls == 0, "Mati is refused");
                }
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch { }
            }
        }

        /// <summary>
        /// An LFPS-shaped result zip: eight Int32 bands (elevation, slope, aspect, fuel, CC, CH, CBH, CBD) on a small UTM 10N
        /// grid, labelled as LFPS labels them unless <paramref name="descriptions"/> says otherwise (null entries: unlabelled),
        /// and an aux.xml stating LFPS's units.
        /// </summary>
        internal static string SyntheticLfpsZip(string folder, string[] descriptions, string[] units = null)
        {
            descriptions = descriptions ?? new[]
            {
                "LF2020_Elev_CONUS", "LF2020_SlpD_CONUS", "LF2020_Asp_CONUS", "LF2024_FBFM40_CONUS",
                "LF2024_CC_CONUS", "LF2024_CH_CONUS", "LF2024_CBH_CONUS", "LF2024_CBD_CONUS",
            };
            units = units ?? new[] { "Meters", "Degrees", "Degrees", "Class", "Percent", "Meters * 10", "Meters * 10", "Kilograms per cubic meter * 100" };

            string work = Path.Combine(folder, "lfps-build");
            Directory.CreateDirectory(work);
            string name = "jsynthetic0000000000000000000000";
            string tif = Path.Combine(work, name + ".tif");
            const int nx = 40, ny = 30;
            Gdal.AllRegister();
            using (Driver driver = Gdal.GetDriverByName("GTiff"))
            using (Dataset ds = driver.Create(tif, nx, ny, descriptions.Length, DataType.GDT_Int32, null))
            {
                ds.SetGeoTransform(new[] { 666000.0, 30.0, 0.0, 4310000.0, 0.0, -30.0 });
                using (var srs = new OSGeo.OSR.SpatialReference(""))
                {
                    srs.ImportFromEPSG(32610);
                    srs.ExportToWkt(out string wkt, null);
                    ds.SetProjection(wkt);
                }
                for (int b = 1; b <= descriptions.Length; ++b)
                {
                    var data = new int[nx * ny];
                    for (int i = 0; i < data.Length; ++i)
                    {
                        int x = i % nx, y = i / nx;
                        switch (b)
                        {
                            case 1: data[i] = 300 + x; break;
                            case 4: data[i] = x < 10 ? 91 : y < 15 ? 102 : 165; break;
                            case 5: data[i] = y < 15 ? 0 : 55; break;
                            case 6: data[i] = y < 15 ? 0 : 230; break;
                            case 7: data[i] = y < 15 ? 0 : 12; break;
                            case 8: data[i] = y < 15 ? 0 : 14; break;
                            default: data[i] = (x + y) % 30; break;
                        }
                        if (x == 0 && y == 0) data[i] = -9999;
                    }
                    using (Band band = ds.GetRasterBand(b))
                    {
                        band.SetNoDataValue(-9999);
                        if (descriptions[b - 1] != null) band.SetDescription(descriptions[b - 1]);
                        band.WriteRaster(0, 0, nx, ny, data, nx, ny, 0, 0);
                    }
                }
            }
            //Our own aux.xml in LFPS's form (GDAL would write UnitType, not UnitOfMeasure).
            string aux = tif + ".aux.xml";
            if (File.Exists(aux)) File.Delete(aux);
            var sb = new StringBuilder("<PAMDataset>\n");
            for (int b = 1; b <= units.Length; ++b)
            {
                sb.Append($"  <PAMRasterBand band=\"{b}\">\n");
                if (descriptions.Length >= b && descriptions[b - 1] != null) sb.Append($"    <Description>{descriptions[b - 1]}</Description>\n");
                if (units[b - 1] != null) sb.Append($"    <UnitOfMeasure>{units[b - 1]}</UnitOfMeasure>\n");
                sb.Append("  </PAMRasterBand>\n");
            }
            sb.Append("</PAMDataset>\n");
            File.WriteAllText(aux, sb.ToString());
            File.WriteAllText(Path.Combine(work, name + ".tfw"), "30\n0\n0\n-30\n666015\n4309985\n");

            string zip = Path.Combine(folder, "synthetic-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".zip");
            ZipFile.CreateFromDirectory(work, zip);
            Directory.Delete(work, true);
            return zip;
        }

        private static void LandfireBands()
        {
            LandfireLayers.Band b = LandfireLayers.ParseDescription("LF2024_FBFM40_CONUS");
            Assert.True(b.Product == "FBFM40" && b.Year == 2024 && b.Region == "CONUS" && b.Release == "LF2024", "the current form");
            b = LandfireLayers.ParseDescription("LF2020_SlpD_CONUS");
            Assert.True(b.Product == "SLPD" && b.Year == 2020, "slope");
            b = LandfireLayers.ParseDescription("LF2023_CC");
            Assert.True(b.Product == "CC" && b.Year == 2023 && b.Region == "", "no region");
            b = LandfireLayers.ParseDescription("US_230FBFM40");
            Assert.True(b.Product == "FBFM40" && b.Year == 2022 && b.Region == "CONUS", "the old form: " + b.Product + " " + b.Year);
            b = LandfireLayers.ParseDescription("US_220CBH");
            Assert.True(b.Product == "CBH" && b.Year == 2020, "CBH is not CH");
            b = LandfireLayers.ParseDescription("US_220CH");
            Assert.True(b.Product == "CH", "and CH is CH");
            Assert.True(LandfireLayers.ParseDescription("").Product == "", "unlabelled");

            Assert.Equal(10, LandfireLayers.UnitMultiplier("Meters * 10"), "m x 10");
            Assert.Equal(100, LandfireLayers.UnitMultiplier("Kilograms per cubic meter * 100"), "kg/m3 x 100");
            Assert.Equal(10, LandfireLayers.UnitMultiplier("meters x 10"), "x as times");
            Assert.Equal(1, LandfireLayers.UnitMultiplier("Meters"), "real units");
            Assert.Equal(1, LandfireLayers.UnitMultiplier("Index"), "an x at the end is not a multiplier");
            Assert.Equal(0, LandfireLayers.UnitMultiplier(""), "none stated");

            string scratch = TempFolder("preact-lfbands-");
            try
            {
                //Unlabelled bands are taken by request order, and said to be.
                string zip = SyntheticLfpsZip(scratch, new string[8]);
                string tif = LandfireLandscapeDownloader.Unpack(zip, Path.Combine(scratch, "u"));
                var log = new List<string>();
                LandfireLayers.Split split = LandfireLayers.SplitLayers(tif, Path.Combine(scratch, "out"), "S", false, log.Add);
                Assert.True(split.Layers.Count == 5 && split.Sources["cbd"].Index == 8 && split.Sources["fbfm40"].Index == 4, "by request order");
                Assert.True(split.Warnings.Any(w => w.Contains("band 6 has no description")), "and said: " + string.Join("; ", split.Warnings));
                Assert.True(split.Layers["fbfm40"].EndsWith("S_lf_fbfm40.tif"), "no release known: _lf_ " + split.Layers["fbfm40"]);

                //Canopy in real units turns the flags off; a value no canopy has is reported.
                zip = SyntheticLfpsZip(scratch, null, new[] { "Meters", "Degrees", "Degrees", "Class", "Percent", "Meters", "Meters", "Kilograms per cubic meter" });
                tif = LandfireLandscapeDownloader.Unpack(zip, Path.Combine(scratch, "r"));
                split = LandfireLayers.SplitLayers(tif, Path.Combine(scratch, "out2"), "S", false, log.Add);
                Assert.True(!split.Scaling.ChTimes10 && !split.Scaling.CbhTimes10 && !split.Scaling.CbdTimes100 && split.Scaling.CcInPercent,
                    "real units: CH/CBH/CBD flags off");
                Assert.True(split.Warnings.Any(w => w.StartsWith("CH reaches 230")) && split.Warnings.Any(w => w.StartsWith("CBD reaches 14")),
                    "230 m trees and 14 kg/m3 are reported: " + string.Join("; ", split.Warnings));

                //Units not stated: LANDFIRE's convention, said.
                zip = SyntheticLfpsZip(scratch, null, new string[8]);
                tif = LandfireLandscapeDownloader.Unpack(zip, Path.Combine(scratch, "n"));
                log.Clear();
                split = LandfireLayers.SplitLayers(tif, Path.Combine(scratch, "out3"), "S", false, log.Add);
                Assert.True(split.Scaling.ChTimes10 && split.Scaling.CbdTimes100 && log.Any(l => l.Contains("CH states no unit; LANDFIRE's x10 assumed")),
                    "no unit: assumed and said");

                //An Anderson 13 scenario given a Scott & Burgan download is told so.
                Exception failed = null;
                try { LandfireLayers.SplitLayers(tif, Path.Combine(scratch, "out4"), "S", true, log.Add); }
                catch (Exception e) { failed = e; }
                Assert.True(failed != null && failed.Message.Contains("LF2024_FBFM40_CONUS, not the FBFM13"), "the wrong fuel set: " + failed?.Message);
            }
            finally
            {
                try { Directory.Delete(scratch, true); } catch { }
            }
        }

        private static void LandfireContactEmail()
        {
            string scratch = TempFolder("preact-lfmail-");
            string saved = Environment.GetEnvironmentVariable(LandfireContact.Variable);
            LandfireContact.SettingsPathOverride = Path.Combine(scratch, "WUInity", "user-settings.txt");
            try
            {
                Environment.SetEnvironmentVariable(LandfireContact.Variable, null);
                Assert.True(LandfireContact.Resolve(null, out string source) == null && source == null, "none anywhere");
                Assert.True(LandfireContact.MissingMessage.Contains(LandfireContact.Variable), "the message says where one goes");

                File.WriteAllText(Path.Combine(scratch, "other.txt"), "");
                Directory.CreateDirectory(Path.GetDirectoryName(LandfireContact.SettingsPathOverride));
                File.WriteAllLines(LandfireContact.SettingsPathOverride, new[] { "# kept", "ElmfireExe=C:/elmfire.exe" });
                Assert.True(LandfireContact.Save("  nick@cloudfire.example  ", out _), "saved");
                string[] lines = File.ReadAllLines(LandfireContact.SettingsPathOverride);
                Assert.True(lines.Contains("ElmfireExe=C:/elmfire.exe") && lines.Contains("LandfireEmail=nick@cloudfire.example"),
                    "the other settings are kept: " + string.Join(" | ", lines));
                Assert.Equal("nick@cloudfire.example", LandfireContact.Resolve(null, out source), "from the file");
                Assert.Equal(LandfireContact.SettingsPathOverride, source, "and says so");

                Environment.SetEnvironmentVariable(LandfireContact.Variable, "env@example.org");
                Assert.Equal("env@example.org", LandfireContact.Resolve(null, out source), "the environment before the file");
                Assert.Equal("cli@example.org", LandfireContact.Resolve("cli@example.org", out source), "an explicit one first");
                Assert.Equal("env@example.org", LandfireContact.Resolve("not an address", out source), "a bad explicit one is passed over");

                Assert.True(LandfireContact.Save("", out _) && !File.ReadAllLines(LandfireContact.SettingsPathOverride).Any(l => l.StartsWith("LandfireEmail")),
                    "an empty one removes it");
                Assert.True(!LandfireContact.IsPlausible("a@b") && !LandfireContact.IsPlausible("a b@c.d") && LandfireContact.IsPlausible("a.b@c.de"),
                    "plausible addresses");

                //The address the downloader used to send for everyone is nowhere in the engine.
                string repo = Program.FindRepositoryRoot();
                if (repo != null)
                {
                    foreach (string file in Directory.GetFiles(Path.Combine(repo, "PREACT", "PREACTcore", "Source"), "*.cs", SearchOption.AllDirectories))
                    {
                        Assert.True(!File.ReadAllText(file).Contains("jonathan.wahlqvist@"), "no hard-coded e-mail in " + file);
                    }
                }
            }
            finally
            {
                LandfireContact.SettingsPathOverride = null;
                Environment.SetEnvironmentVariable(LandfireContact.Variable, saved);
                try { Directory.Delete(scratch, true); } catch { }
            }
        }

        private static void LandfireVersionKey()
        {
            string folder = TempFolder("preact-lfkey-");
            try
            {
                string[] lines =
                {
                    "[Simulation]", "Name=Auburn2", "DomainSize=2000,2000", "LowerLeftLatLon=38.89,-121.08",
                    "StartDateTime=2019-08-01T12:00:00", "EndDateTime=2019-08-02T12:00:00",
                    "[WildfireModule]", "Enabled=true", "Module=ELMFIRE",
                    "[ELMFIRE]", "LandfireVersion=lf 2016 remap",
                };
                PREACTInput input = PREACTInput.LoadFromLines(lines, folder, out bool _);
                Assert.Equal("LF2016", input.WildfireModule.ElmfireInput.LandfireVersion, "read and normalised");
                string[] written = PREACTInputWriter.Write(input);
                Assert.True(written.Contains("LandfireVersion=LF2016"), "written: " + string.Join(" ", written.Where(l => l.StartsWith("Landfire"))));
                PREACTInput again = PREACTInput.LoadFromLines(written, folder, out bool _);
                Assert.Equal("LF2016", again.WildfireModule.ElmfireInput.LandfireVersion, "kept");

                LandfireFuels.Options options = LandfireFuels.Options.FromScenario(again);
                Assert.True(options.Version == "LF2016" && options.ScenarioYear == 2019 && options.PaddingMetres == 2000
                            && options.CaseDirectory.EndsWith("elmfire"), "the download reads the scenario's settings");

                lines[lines.Length - 1] = "LandfireVersion=LF2019";
                PREACTInput bad = PREACTInput.LoadFromLines(lines, folder, out bool _);
                Assert.Equal(LandfireVersions.Closest, bad.WildfireModule.ElmfireInput.LandfireVersion, "an unknown release keeps closest");
                Assert.True(!PREACTInputWriter.Write(new PREACTInput(folder)).Any(l => l.StartsWith("LandfireVersion")), "the default is not written");
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch { }
            }
        }
    }
}
