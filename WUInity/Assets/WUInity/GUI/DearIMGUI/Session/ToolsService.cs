using System;
using System.Threading.Tasks;
using WUInity.Workflow;

namespace Assets.WUInity.GUI.DearIMGUI
{
    /// <summary>
    /// The GUI's copy of what was found of ELMFIRE, GDAL, WindNinja, SUMO, PROJ and the two API keys.
    /// Probed off the main thread, when the application starts, when a scenario is opened (it may name its
    /// own tool paths) and when asked to; read everywhere else from <see cref="Current"/>.
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
            string sumo = PreactGUI.Engine.SumoPath;
            string projLib = PreactGUI.Engine.ProjLibPath;
            string projData = PreactGUI.Engine.ProjDataPath;
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
                    snapshot = ExternalTools.Probe(root, elmfireOverride, gdalOverride, windNinjaOverride,
                        sumo, projLib, projData, keySource, mapbox);
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
