using System;

namespace WUInity.Workflow
{
    /// <summary>What was found of the external tools and keys the platform shells out to or needs.</summary>
    public sealed class ExternalToolsSnapshot
    {
        /// <summary>False until the first probe has finished; everything below is empty until then.</summary>
        public bool Probed;
        public DateTime ProbedAt;

        public string ElmfireExe = string.Empty;
        /// <summary>The scenario's own [ELMFIRE] ElmfireExe, when it names one.</summary>
        public string ElmfireOverride = string.Empty;

        public string GdalBin = string.Empty;
        public string GdalOverride = string.Empty;

        public string WindNinjaExe = string.Empty;
        public string WindNinjaOverride = string.Empty;

        /// <summary>SUMO's bin folder as the engine found it (SUMO_HOME/bin, then PATH).</summary>
        public string SumoBin = string.Empty;
        public string ProjLib = string.Empty;
        public string ProjData = string.Empty;

        /// <summary>Where the OpenTopography key comes from, or empty when there is none.</summary>
        public string OpenTopographyKeySource = string.Empty;

        /// <summary>Null when not checked (no map yet).</summary>
        public bool? MapboxTokenValid;

        /// <summary>Anything a probe threw, so a broken probe says so instead of reporting "not found".</summary>
        public string ProbeError = string.Empty;

        public bool HaveElmfire => !string.IsNullOrEmpty(ElmfireExe);
        public bool HaveGdal => !string.IsNullOrEmpty(GdalBin);
        public bool HaveWindNinja => !string.IsNullOrEmpty(WindNinjaExe);
        public bool HaveSumo => !string.IsNullOrEmpty(SumoBin);
        public bool HaveOpenTopographyKey => !string.IsNullOrEmpty(OpenTopographyKeySource);
    }

    /// <summary>
    /// Finds ELMFIRE, GDAL and WindNinja the same way the engine does, once, rather than every frame.
    /// </summary>
    /// <remarks>
    /// <c>WindNinjaRunner.FindExecutable</c> searches its install roots recursively and was called from the
    /// scenario editor's draw, as was <c>ElmfireCoupling.ResolveExecutable</c> - so merely having the Fire tab
    /// open cost a directory walk per frame. The answers change when something is installed, which is what
    /// the Refresh button in Help &gt; External tools is for.
    ///
    /// Synchronous and free of Unity: the GUI runs it on a worker thread and hands the snapshot back.
    /// </remarks>
    public static class ExternalTools
    {
        public static ExternalToolsSnapshot Probe(string scenarioRoot, string elmfireOverride, string gdalOverride,
            string windNinjaOverride, string sumoBin, string projLib, string projData,
            string openTopographyKeySource, bool? mapboxTokenValid)
        {
            var s = new ExternalToolsSnapshot
            {
                ElmfireOverride = elmfireOverride ?? string.Empty,
                GdalOverride = gdalOverride ?? string.Empty,
                WindNinjaOverride = windNinjaOverride ?? string.Empty,
                SumoBin = sumoBin ?? string.Empty,
                ProjLib = projLib ?? string.Empty,
                ProjData = projData ?? string.Empty,
                OpenTopographyKeySource = openTopographyKeySource ?? string.Empty,
                MapboxTokenValid = mapboxTokenValid,
            };

            s.ElmfireExe = Try(() => PREACT.Utility.ElmfireCoupling.ResolveExecutable(scenarioRoot, elmfireOverride), s);
            s.GdalBin = Try(() => PREACT.Utility.GdalTools.FindBinDirectory(), s);
            s.WindNinjaExe = Try(() => PREACT.Utility.WindNinjaRunner.FindExecutable(), s);

            s.Probed = true;
            s.ProbedAt = DateTime.Now;
            return s;
        }

        private static string Try(Func<string> probe, ExternalToolsSnapshot into)
        {
            try
            {
                return probe() ?? string.Empty;
            }
            catch (Exception e)
            {
                into.ProbeError = string.IsNullOrEmpty(into.ProbeError) ? e.Message : into.ProbeError + "; " + e.Message;
                return string.Empty;
            }
        }
    }
}
