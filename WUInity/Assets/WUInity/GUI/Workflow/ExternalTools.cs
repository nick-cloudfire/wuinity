using System;
using System.IO;
using PREACT.Utility;

namespace WUInity.Workflow
{
    /// <summary>One external tool: what is in use, where that came from, and what the user's setting says.</summary>
    public sealed class ToolStatus
    {
        /// <summary>The path in use: an executable, or a folder for the GDAL tools, SUMO's bin and PROJ. Empty when none.</summary>
        public string InUse = string.Empty;

        /// <summary>Where <see cref="InUse"/> came from.</summary>
        public ToolPaths.Source Source = ToolPaths.Source.None;

        /// <summary>What the open scenario names for it ([ELMFIRE] ElmfireExe, PathToGdal, WindNinjaExe), or empty.</summary>
        public string Explicit = string.Empty;

        /// <summary>Why what the scenario names cannot be used, or empty. The engine does not fall back from it.</summary>
        public string ExplicitProblem = string.Empty;

        /// <summary>The saved setting, as written in the settings file; empty when there is none.</summary>
        public string Setting = string.Empty;

        /// <summary>Why the saved setting is not used, or empty.</summary>
        public string SettingProblem = string.Empty;

        /// <summary>
        /// SUMO and PROJ: true when what the settings now give is not what this process uses, which only a restart
        /// changes; <see cref="AfterRestart"/> is then what it would use (empty: nothing found).
        /// </summary>
        public bool NeedsRestart;
        public string AfterRestart = string.Empty;

        /// <summary>For PROJ, which variables gave the paths ("PROJ_DATA", ...); empty otherwise.</summary>
        public string Detail = string.Empty;

        public bool Found => !string.IsNullOrEmpty(InUse);
    }

    /// <summary>What the engine itself decided when it started, which the probe reports rather than redoes.</summary>
    public sealed class EngineToolState
    {
        public string SumoBin;
        public ToolPaths.Source SumoSource;
        public string[] ProjPaths = new string[0];
        public ToolPaths.Source ProjSource;
        public string ProjDetail = string.Empty;
        /// <summary>Why a PROJ change saved while running could not be applied, or null.</summary>
        public string ProjPending;
        public string ProjLib;
        public string ProjData;
        /// <summary>Why GDAL's library did not load, or null.</summary>
        public string GdalLibraryProblem;
    }

    /// <summary>What was found of the external tools and keys the platform shells out to or needs.</summary>
    public sealed class ExternalToolsSnapshot
    {
        /// <summary>False until the first probe has finished; everything below is empty until then.</summary>
        public bool Probed;
        public DateTime ProbedAt;

        public ToolStatus Elmfire = new ToolStatus();
        public ToolStatus Gdal = new ToolStatus();
        public ToolStatus WindNinja = new ToolStatus();
        public ToolStatus Sumo = new ToolStatus();
        public ToolStatus Proj = new ToolStatus();

        /// <summary>FireDX: the Python it runs with, and its source folder (the submodule, or the user's).</summary>
        public ToolStatus FireDxPython = new ToolStatus();
        public ToolStatus FireDx = new ToolStatus();

        /// <summary>The settings file this process reads, and why it could not be read (empty when it could).</summary>
        public string SettingsFile = string.Empty;
        public string SettingsReadError = string.Empty;

        /// <summary>Why GDAL's library did not load when the engine started, or empty.</summary>
        public string GdalLibraryProblem = string.Empty;

        //The flat names the workflow, the editor and the campaign window read.
        public string ElmfireExe = string.Empty;
        /// <summary>The scenario's own [ELMFIRE] ElmfireExe, when it names one.</summary>
        public string ElmfireOverride = string.Empty;

        /// <summary>The GDAL tools folder in use: the scenario's PathToGdal when it names one that exists, else found.</summary>
        public string GdalBin = string.Empty;
        public string GdalOverride = string.Empty;

        public string WindNinjaExe = string.Empty;
        public string WindNinjaOverride = string.Empty;

        /// <summary>SUMO's bin folder as the engine found it when it started (the settings, SUMO_HOME/bin, then PATH).</summary>
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
        public bool HaveFireDxPython => FireDxPython.Found;
        public bool HaveFireDx => FireDx.Found;
    }

    /// <summary>
    /// Finds ELMFIRE, GDAL and WindNinja the same way the engine does, once, rather than every frame, and says for
    /// each where the one in use came from: the scenario, the user's tool settings (<see cref="ToolPaths"/>), or
    /// the automatic search.
    /// </summary>
    /// <remarks>
    /// <c>WindNinjaRunner.FindExecutable</c> searches its install roots recursively and was called from the
    /// scenario editor's draw, as was <c>ElmfireCoupling.ResolveExecutable</c> - so merely having the Fire tab
    /// open cost a directory walk per frame. The answers change when something is installed or a setting is saved,
    /// which is what Look again and Save in Help &gt; External tools are for.
    ///
    /// Synchronous and free of Unity: the GUI runs it on a worker thread and hands the snapshot back.
    /// </remarks>
    public static class ExternalTools
    {
        public static ExternalToolsSnapshot Probe(string scenarioRoot, string elmfireOverride, string gdalOverride,
            string windNinjaOverride, EngineToolState engine, string openTopographyKeySource, bool? mapboxTokenValid)
        {
            engine = engine ?? new EngineToolState();
            var s = new ExternalToolsSnapshot
            {
                ElmfireOverride = elmfireOverride ?? string.Empty,
                GdalOverride = gdalOverride ?? string.Empty,
                WindNinjaOverride = windNinjaOverride ?? string.Empty,
                ProjLib = engine.ProjLib ?? string.Empty,
                ProjData = engine.ProjData ?? string.Empty,
                OpenTopographyKeySource = openTopographyKeySource ?? string.Empty,
                MapboxTokenValid = mapboxTokenValid,
                GdalLibraryProblem = engine.GdalLibraryProblem ?? string.Empty,
            };

            Try(() =>
            {
                s.SettingsFile = ToolPaths.SettingsFile;
                s.SettingsReadError = ToolPaths.ReadError ?? string.Empty;
            }, s);

            //"Look again" means again: the GDAL search caches its answer for the session otherwise.
            Try(GdalTools.SearchAgain, s);

            Try(() => ProbeElmfire(s.Elmfire, scenarioRoot, elmfireOverride), s);
            Try(() => ProbeGdal(s.Gdal, scenarioRoot, gdalOverride), s);
            Try(() => ProbeWindNinja(s.WindNinja, scenarioRoot, windNinjaOverride), s);
            Try(() => ProbeSumo(s.Sumo, engine), s);
            Try(() => ProbeProj(s.Proj, engine), s);
            Try(() => ProbeFireDx(s.FireDxPython, s.FireDx), s);

            s.ElmfireExe = s.Elmfire.InUse;
            s.GdalBin = s.Gdal.InUse;
            s.WindNinjaExe = s.WindNinja.InUse;
            s.SumoBin = s.Sumo.InUse;

            s.Probed = true;
            s.ProbedAt = DateTime.Now;
            return s;
        }

        private static void Setting(ToolStatus t, ToolPaths.Tool tool)
        {
            t.Setting = ToolPaths.Load().Get(tool);
            t.SettingProblem = ToolPaths.UserSettingProblem(tool) ?? string.Empty;
        }

        private static void ProbeElmfire(ToolStatus t, string root, string named)
        {
            Setting(t, ToolPaths.Tool.Elmfire);
            t.Explicit = named ?? string.Empty;
            t.InUse = ElmfireCoupling.ResolveExecutable(root, named) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(named) && !t.Found)
            {
                t.ExplicitProblem = ElmfireCoupling.DescribeMissingExecutable(root, named);
            }
            t.Source = ToolPaths.Classify(ToolPaths.Tool.Elmfire, t.InUse, named);
        }

        private static void ProbeGdal(ToolStatus t, string root, string named)
        {
            Setting(t, ToolPaths.Tool.Gdal);
            t.Explicit = named ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(named))
            {
                //What the engine hands ELMFIRE as PATH_TO_GDAL, with no fallback: so a missing one is a problem to show.
                string path = Resolve(root, named);
                t.InUse = path != null && Directory.Exists(path) ? path : string.Empty;
                if (!t.Found)
                {
                    t.ExplicitProblem = "[ELMFIRE] PathToGdal names " + (path ?? named) + ", which is not there. ELMFIRE is given it "
                                        + "anyway; correct the key or remove it to use your setting or the search.";
                }
            }
            else
            {
                t.InUse = GdalTools.FindBinDirectory() ?? string.Empty;
            }
            t.Source = ToolPaths.Classify(ToolPaths.Tool.Gdal, t.InUse, named);
        }

        private static void ProbeWindNinja(ToolStatus t, string root, string named)
        {
            Setting(t, ToolPaths.Tool.WindNinja);
            t.Explicit = named ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(named))
            {
                string path = Resolve(root, named);
                t.InUse = path != null && File.Exists(path) ? path : string.Empty;
                if (!t.Found)
                {
                    t.ExplicitProblem = "[ELMFIRE] WindNinjaExe names " + (path ?? named) + ", which is not there, so the case gets "
                                        + "uniform wind. Correct the key or remove it to use your setting or the search.";
                }
            }
            else
            {
                t.InUse = WindNinjaRunner.FindExecutable() ?? string.Empty;
            }
            t.Source = ToolPaths.Classify(ToolPaths.Tool.WindNinja, t.InUse, named);
        }

        /// <summary>SUMO as the engine decided at start-up, and what a restart would decide now.</summary>
        private static void ProbeSumo(ToolStatus t, EngineToolState engine)
        {
            Setting(t, ToolPaths.Tool.Sumo);
            t.InUse = engine.SumoBin ?? string.Empty;
            t.Source = t.Found ? engine.SumoSource : ToolPaths.Source.None;

            string next = PREACT.Engine.FindSumoBinFolder(out _);
            t.NeedsRestart = string.IsNullOrEmpty(next) ? t.Found : !ToolPaths.SamePath(next, t.InUse);
            t.AfterRestart = next ?? string.Empty;
        }

        /// <summary>PROJ as this process's GDAL has it, and whether the saved setting is still waiting for a restart.</summary>
        private static void ProbeProj(ToolStatus t, EngineToolState engine)
        {
            Setting(t, ToolPaths.Tool.Proj);
            t.InUse = string.Join(Path.PathSeparator.ToString(), engine.ProjPaths ?? new string[0]);
            t.Source = t.Found ? engine.ProjSource : ToolPaths.Source.None;
            t.Detail = engine.ProjDetail ?? string.Empty;
            t.NeedsRestart = !string.IsNullOrEmpty(engine.ProjPending);
            if (t.NeedsRestart)
            {
                string[] next = ToolPaths.ProjSearchPaths(out _, out _);
                t.AfterRestart = string.Join(Path.PathSeparator.ToString(), next);
            }
        }

        /// <summary>FireDX's Python and source, as FireDxRunner finds them: the user's setting, else the automatic search.</summary>
        private static void ProbeFireDx(ToolStatus python, ToolStatus package)
        {
            Setting(python, ToolPaths.Tool.FireDxPython);
            python.InUse = FireDxRunner.FindPython(out ToolPaths.Source pythonSource) ?? string.Empty;
            python.Source = pythonSource;
            Setting(package, ToolPaths.Tool.FireDx);
            package.InUse = FireDxRunner.FindPackage(out ToolPaths.Source packageSource) ?? string.Empty;
            package.Source = packageSource;
        }

        /// <summary>A scenario-relative path made absolute, as the campaign window and the CLI do.</summary>
        private static string Resolve(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (Path.IsPathRooted(path) || string.IsNullOrEmpty(root)) return Path.GetFullPath(path);
                return Path.GetFullPath(Path.Combine(root, path));
            }
            catch
            {
                return null;
            }
        }

        private static void Try(Action probe, ExternalToolsSnapshot into)
        {
            try
            {
                probe();
            }
            catch (Exception e)
            {
                into.ProbeError = string.IsNullOrEmpty(into.ProbeError) ? e.Message : into.ProbeError + "; " + e.Message;
            }
        }
    }
}
