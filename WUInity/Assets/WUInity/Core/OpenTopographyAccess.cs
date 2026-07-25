using UnityEngine;

namespace WUInity
{
    [System.Serializable]
    public class OpenTopographyConfiguration
    {
        public string ApiKey;
    }

    /// <summary>
    /// Reads the OpenTopography API key (docs/probabilistic-trigger-convergence.md, "Topography /
    /// DEM downloader") the same way WUInity already reads its Mapbox access token
    /// (<see cref="Mapbox.Unity.MapboxAccess"/>): a gitignored JSON file under a Resources folder,
    /// loaded via <see cref="Resources.Load{T}"/>. Copy
    /// <c>Assets/Resources/OpenTopography/OpenTopographyConfigurationTemplate.txt</c> to
    /// <c>OpenTopographyConfiguration.txt</c> in the same folder and paste in a real key from
    /// https://opentopography.org/.
    /// </summary>
    public static class OpenTopographyAccess
    {
        private const string ResourcesPath = "OpenTopography/OpenTopographyConfiguration";

        private static OpenTopographyConfiguration _configuration;
        private static bool _loaded;

        public static string ApiKey
        {
            get
            {
                if (!_loaded) Load();
                return _configuration?.ApiKey;
            }
        }

        public static bool IsApiKeyValid => !string.IsNullOrEmpty(ApiKey);

        private static void Load()
        {
            _loaded = true;
            TextAsset configurationTextAsset = Resources.Load<TextAsset>(ResourcesPath);
            if (configurationTextAsset == null)
            {
                Debug.LogError("No OpenTopography configuration file found! Copy Assets/Resources/OpenTopography/OpenTopographyConfigurationTemplate.txt to OpenTopographyConfiguration.txt in the same folder and paste in your API key.");
                return;
            }
            _configuration = JsonUtility.FromJson<OpenTopographyConfiguration>(configurationTextAsset.text);
        }
    }
}
