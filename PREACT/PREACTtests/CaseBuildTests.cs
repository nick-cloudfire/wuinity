using System.Globalization;
using PREACT.Utility;

namespace PREACT.Tests
{
    /// <summary>
    /// Round 2's case build and fire areas (BL2): the fuel check that comes before any download, rebuilding only the
    /// weather, the WUI area as the evacuation groups' union, and painted initial ignition turned into an ignition point.
    /// On the synthetic case of <see cref="PipelineTests.SyntheticCase"/>: no network, no WindNinja, no ELMFIRE.
    /// </summary>
    internal static class CaseBuildTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("builder: a case without a fuel model is refused before its DEM, weather or WindNinja", NoFuelRefusedFirst);
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
