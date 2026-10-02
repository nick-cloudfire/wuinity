namespace PREACT.Tests
{
    /// <summary><c>PREACTcli landfire</c>: the GUI's fuels-step download from the command line, against a stub LFPS.</summary>
    internal static class LandfireCliTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("cli: landfire downloads the release asked for, and --update-wui writes the source layers and the scaling flags", LandfireCommand);
        }

        private static void LandfireCommand()
        {
            string dir = Directory.CreateTempSubdirectory("preact-lfcli-").FullName;
            try
            {
                string wui = Path.Combine(dir, "Auburn2.wui");
                File.WriteAllLines(wui, new[]
                {
                    "[Simulation]", "Name=Auburn2", "DomainSize=3500,3500", "LowerLeftLatLon=38.885,-121.090",
                    "StartDateTime=2026-09-29T12:00:00", "EndDateTime=2026-09-30T12:00:00",
                    "", "[WildfireModule]", "Enabled=true", "Module=ELMFIRE",
                    "", "[ELMFIRE]", "PaddingMetres=1000", "LandfireVersion=LF2022",
                    "", "[ElmfireNamelist]", "CH_TIMES_10=false",
                });
                string zip = DownloaderTests.SyntheticLfpsZip(dir, null);
                using (var lfps = new DownloaderTests.StubLfps(File.ReadAllBytes(zip)))
                {
                    (int exit, string output) = PipelineTests.RunCli(dir, "landfire", "--wui", wui, "--version", "LF2024",
                        "--email", "someone@example.org", "--service-url", lfps.Server.BaseUrl + "/api", "--poll-seconds", "1", "--update-wui");
                    Assert.Equal(0, exit, "landfire: " + output);
                    Assert.True(Uri.UnescapeDataString(lfps.SubmitQuery).Contains("LF2024_FBFM40"), "--version wins over the scenario's LF2022");

                    string[] lines = File.ReadAllLines(wui);
                    foreach (string expected in new[]
                             {
                                 "FuelModelFile=downloads/landfire/Auburn2_LF2024_fbfm40.tif", "CanopyBulkDensityFile=downloads/landfire/Auburn2_LF2024_cbd.tif",
                                 "LandfireVersion=LF2024", "CH_TIMES_10=true", "CBD_TIMES_100=true",
                             })
                    {
                        Assert.True(lines.Contains(expected), expected + " is written: " + string.Join(" | ", lines));
                    }
                    Assert.True(!lines.Contains("CH_TIMES_10=false") && !lines.Contains("LandfireVersion=LF2022"), "replaced, not added beside");
                    Assert.True(File.Exists(Path.Combine(dir, "downloads", "landfire", "Auburn2_LF2024_fbfm40.tif")), "the layer is there");
                }

                (int bad, string said) = PipelineTests.RunCli(dir, "landfire", "--wui", wui, "--version", "LF2020");
                Assert.True(bad == 2 && said.Contains("LF2016, LF2022"), "an unserved release is refused: " + said);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
