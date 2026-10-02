using System;
using System.Threading.Tasks;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The GUI's copy of what was found of ELMFIRE, GDAL, WindNinja, SUMO, PROJ and the two API keys.
    /// Probed off the main thread, when the application starts, when a scenario is opened (it may name its
    /// own tool paths), when the tool settings are saved and when asked to; read everywhere else from
    /// <see cref="Current"/>.
    /// </summary>
    public static class ToolsService
    {
        private static ExternalToolsSnapshot _current = new ExternalToolsSnapshot();
        private static volatile bool _probing;
        private static bool _again;

        /// <summary>The last finished probe. Never null; <c>Probed</c> is false until the first one lands.</summary>
        public static ExternalToolsSnapshot Current { get => _current; }

        public static bool Probing { get => _probing; }

        /// <summary>Raised on the main thread when a probe has finished.</summary>
        public static event Action Changed;

        /// <summary>Starts a probe unless one is already running (then one more runs after it).</summary>
        public static void Refresh()
        {
            if (PreactGUI.Engine == null)
            {
                return;
            }

            if (_probing)
            {
                _again = true;
                return;
            }

            //Everything that has to be read on the main thread - the scenario, the Unity resource holding the
            //OpenTopography key, the Mapbox map - is read here, before the worker starts.
            PREACT.Input.PREACTInput input = ScenarioEditorWindow.Input;
            PREACT.Input.ElmfireInput elmfire = input?.WildfireModule?.ElmfireInput;
            string root = input?.RootFolder;
            string elmfireOverride = elmfire?.ElmfireExe;
            string gdalOverride = elmfire?.PathToGdal;
            string windNinjaOverride = elmfire?.WindNinjaExe;

            //A PROJ folder saved since the last look - from the window, or by hand in the file - is handed to GDAL
            //now, so what is reported below is what is in force.
            PREACT.Engine engine = PreactGUI.Engine;
            string projPending = engine.ApplyToolSettings();
            var state = new EngineToolState
            {
                SumoBin = engine.SumoPath,
                SumoSource = engine.SumoSource,
                ProjPaths = engine.ProjSearchPaths,
                ProjSource = engine.ProjSource,
                ProjDetail = engine.ProjSourceDetail,
                ProjPending = projPending,
                ProjLib = engine.ProjLibPath,
                ProjData = engine.ProjDataPath,
                GdalLibraryProblem = engine.GdalLibraryProblem,
            };

            //The engine reads the OpenTopography key from the environment or from the resource file on disk, which a
            //standalone player does not have: its copy of the key is built into its data. Handed over here, at start-up
            //and on every look, so a fire case built in the player has the key the GUI shows.
            try
            {
                PREACT.Utility.OpenTopographyKey.ApplicationKey = global::WUInity.OpenTopographyAccess.ApiKey;
            }
            catch
            {
                //no resource: the environment and the session key remain
            }

            string keySource = ScenarioDataSteps.OpenTopographyApiKeySource;
            bool? mapbox = null;
            try
            {
                if (PreactGUI.WUInity != null && PreactGUI.WUInity.UTMMap != null)
                {
                    mapbox = PreactGUI.WUInity.UTMMap.IsAccessTokenValid;
                }
            }
            catch
            {
                mapbox = null;
            }

            _probing = true;
            Task.Run(() =>
            {
                ExternalToolsSnapshot snapshot;
                try
                {
                    snapshot = ExternalTools.Probe(root, elmfireOverride, gdalOverride, windNinjaOverride, state, keySource, mapbox);
                }
                catch (Exception e)
                {
                    snapshot = new ExternalToolsSnapshot { Probed = true, ProbedAt = DateTime.Now, ProbeError = e.Message };
                }

                PreactGUI.Post(() =>
                {
                    _current = snapshot;
                    _probing = false;
                    Changed?.Invoke();
                    if (_again)
                    {
                        _again = false;
                        Refresh();
                    }
                });
            });
        }

        /// <summary>
        /// Called when the OpenTopography key was typed in, which needs no probe. The key goes to where the case
        /// build looks for one, so what unblocks step 5 is also what the build uses.
        /// </summary>
        public static void KeyChanged()
        {
            ScenarioDataSteps.ApplySessionOpenTopographyKey();
            _current.OpenTopographyKeySource = ScenarioDataSteps.OpenTopographyApiKeySource;
            Changed?.Invoke();
        }
    }
}
