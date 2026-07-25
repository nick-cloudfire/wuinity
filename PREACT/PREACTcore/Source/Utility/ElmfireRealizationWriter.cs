using System.Globalization;
using System.IO;

namespace PREACT.Utility
{
    /// <summary>
    /// Namelist keys ELMFIRE's <c>elmfire.data</c> is patched with per realization.
    ///
    /// UNVERIFIED — the doc's ELMFIRE runner contract (docs/probabilistic-trigger-convergence.md,
    /// "ELMFIRE runner contract") says the real template comes from the sibling
    /// <c>nick-cloudfire/WildfireAV</c> repo, which was not accessible when this was written.
    /// <see cref="LhMoistureContent"/>/<see cref="LwMoistureContent"/> are already named in that
    /// doc; every other key here is inferred from public ELMFIRE documentation/example namelists
    /// and MUST be checked against the real template before this is pointed at an actual ELMFIRE
    /// run. A wrong key name here is a safe failure mode — ELMFIRE (like other Fortran namelist
    /// readers) rejects unrecognized variables at read time rather than silently misconfiguring —
    /// but that also means none of these have been exercised against the real engine.
    /// </summary>
    public static class ElmfireNamelistKeys
    {
        public const string ComputationalDomainGroup = "COMPUTATIONAL_DOMAIN";
        public const string InputsGroup = "INPUTS";
        public const string TimeControlGroup = "TIME_CONTROL";

        public const string Srs = "A_SRS";
        public const string CellSize = "COMPUTATIONAL_DOMAIN_CELLSIZE";
        public const string XllCorner = "COMPUTATIONAL_DOMAIN_XLLCORNER";
        public const string YllCorner = "COMPUTATIONAL_DOMAIN_YLLCORNER";
        public const string Cols = "COMPUTATIONAL_DOMAIN_COLS";
        public const string Rows = "COMPUTATIONAL_DOMAIN_ROWS";
        public const string XIgnition = "X_IGNITION";
        public const string YIgnition = "Y_IGNITION";

        public const string WsFilename = "WS_FILENAME";
        public const string WdFilename = "WD_FILENAME";
        public const string M1Filename = "M1_FILENAME";
        public const string M10Filename = "M10_FILENAME";
        public const string M100Filename = "M100_FILENAME";
        public const string LhMoistureContent = "LH_MOISTURE_CONTENT";
        public const string LwMoistureContent = "LW_MOISTURE_CONTENT";

        public const string SimulationTstop = "SIMULATION_TSTOP";
    }

    /// <summary>One realization's sampled inputs, ready to be patched into a base elmfire.data template.</summary>
    public class ElmfireRealization
    {
        public string RunId;
        public MasterGrid Grid;

        /// <summary>Ignition location in the master grid's own coordinate units (e.g. UTM meters).</summary>
        public double IgnitionX, IgnitionY;

        public double WindSpeedMps;
        public double WindDirDeg;
        public double M1Percent, M10Percent, M100Percent;

        /// <summary>Live (forest/shrub) fuel moisture, % of dry mass; null to leave the template's own value.</summary>
        public double? LiveMoisturePercent;

        /// <summary>Simulation stop time in seconds; 0 leaves the template's own value untouched.</summary>
        public double SimulationStopSeconds;
    }

    /// <summary>
    /// Patches a base <c>elmfire.data</c> template with one realization's sampled inputs,
    /// following the same "clone template, patch known keys, write out" convention
    /// <see cref="ProbabilisticTrigger"/> already uses for <c>.wui</c> files. Wind/moisture are
    /// written as uniform "constant transient rasters" (docs/probabilistic-trigger-convergence.md)
    /// via <see cref="ConstantRasterWriter"/> rather than scalars, since ELMFIRE takes gridded
    /// inputs, until the WindNinja/Nelson steps produce spatially-varying ones.
    /// </summary>
    public static class ElmfireRealizationWriter
    {
        public static string[] Write(string[] baseTemplateLines, ElmfireRealization r, string rasterOutputDir)
        {
            Directory.CreateDirectory(rasterOutputDir);

            string wsPath = Path.Combine(rasterOutputDir, $"ws_{r.RunId}.asc");
            string wdPath = Path.Combine(rasterOutputDir, $"wd_{r.RunId}.asc");
            string m1Path = Path.Combine(rasterOutputDir, $"m1_{r.RunId}.asc");
            string m10Path = Path.Combine(rasterOutputDir, $"m10_{r.RunId}.asc");
            string m100Path = Path.Combine(rasterOutputDir, $"m100_{r.RunId}.asc");

            ConstantRasterWriter.WriteConstant(r.Grid, (float)r.WindSpeedMps, wsPath);
            ConstantRasterWriter.WriteConstant(r.Grid, (float)r.WindDirDeg, wdPath);
            ConstantRasterWriter.WriteConstant(r.Grid, (float)r.M1Percent, m1Path);
            ConstantRasterWriter.WriteConstant(r.Grid, (float)r.M10Percent, m10Path);
            ConstantRasterWriter.WriteConstant(r.Grid, (float)r.M100Percent, m100Path);

            string[] lines = (string[])baseTemplateLines.Clone();

            lines = Set(lines, ElmfireNamelistKeys.ComputationalDomainGroup, ElmfireNamelistKeys.Srs, r.Grid.Epsg, quoted: true);
            lines = Set(lines, ElmfireNamelistKeys.ComputationalDomainGroup, ElmfireNamelistKeys.CellSize, D(r.Grid.Header.CellSize));
            lines = Set(lines, ElmfireNamelistKeys.ComputationalDomainGroup, ElmfireNamelistKeys.XllCorner, D(r.Grid.XMin));
            lines = Set(lines, ElmfireNamelistKeys.ComputationalDomainGroup, ElmfireNamelistKeys.YllCorner, D(r.Grid.YMin));
            lines = Set(lines, ElmfireNamelistKeys.ComputationalDomainGroup, ElmfireNamelistKeys.Cols, r.Grid.Header.Ncols.ToString(CultureInfo.InvariantCulture));
            lines = Set(lines, ElmfireNamelistKeys.ComputationalDomainGroup, ElmfireNamelistKeys.Rows, r.Grid.Header.Nrows.ToString(CultureInfo.InvariantCulture));
            lines = Set(lines, ElmfireNamelistKeys.ComputationalDomainGroup, ElmfireNamelistKeys.XIgnition, D(r.IgnitionX));
            lines = Set(lines, ElmfireNamelistKeys.ComputationalDomainGroup, ElmfireNamelistKeys.YIgnition, D(r.IgnitionY));

            lines = Set(lines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WsFilename, wsPath, quoted: true);
            lines = Set(lines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.WdFilename, wdPath, quoted: true);
            lines = Set(lines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.M1Filename, m1Path, quoted: true);
            lines = Set(lines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.M10Filename, m10Path, quoted: true);
            lines = Set(lines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.M100Filename, m100Path, quoted: true);
            if (r.LiveMoisturePercent.HasValue)
            {
                string lm = D(r.LiveMoisturePercent.Value);
                lines = Set(lines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.LhMoistureContent, lm);
                lines = Set(lines, ElmfireNamelistKeys.InputsGroup, ElmfireNamelistKeys.LwMoistureContent, lm);
            }

            if (r.SimulationStopSeconds > 0)
            {
                lines = Set(lines, ElmfireNamelistKeys.TimeControlGroup, ElmfireNamelistKeys.SimulationTstop, D(r.SimulationStopSeconds));
            }

            return lines;
        }

        private static string[] Set(string[] lines, string group, string key, string value, bool quoted = false)
        {
            return ElmfireNamelist.SetKeyInGroup(lines, group, key, value, quoted);
        }

        private static string D(double v) => v.ToString(CultureInfo.InvariantCulture);
    }
}
