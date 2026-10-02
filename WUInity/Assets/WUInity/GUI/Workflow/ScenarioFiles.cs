using System;
using System.IO;
using PREACT.Math;

namespace WUInity.Workflow
{
    /// <summary>
    /// Where each prepared file of a scenario lives, relative to its folder, and a few facts about them that
    /// the data steps and the workflow both need. One table, so the step that writes a file and the check that
    /// looks for it cannot disagree about its name.
    /// </summary>
    /// <remarks>
    /// Forward slashes throughout: these strings are written into the .wui, and a backslash is a separator on
    /// Windows only. No Unity here, so the workflow model can be tested from a console program.
    /// </remarks>
    public static class ScenarioFiles
    {
        /// <summary>Raw downloads, as they arrive: before clipping, warping, or conversion.</summary>
        public const string DownloadsFolder = "downloads";

        /// <summary>Terrain rasters for a scenario without a fire case (and, historically, beside one).</summary>
        public const string LandscapeFolder = "elmfire/inputs";

        /// <summary>LANDFIRE's download and the layers split out of it.</summary>
        public const string LandfireFolder = "downloads/landfire";

        public const string SumoFolder = PREACT.Utility.SumoNetworkBuilder.SumoFolderName;
        public static string SumoConfig => SumoFolder + "/" + PREACT.Utility.SumoNetworkBuilder.ConfigurationFileName;
        public static string SumoNetwork => SumoFolder + "/" + PREACT.Utility.SumoNetworkBuilder.NetworkFileName;

        public static string WorldPopBaseName(string name) => name + "_worldpop";
        public static string WorldPop(string name) => DownloadsFolder + "/" + WorldPopBaseName(name) + ".tif";

        /// <summary>
        /// The reprojected WorldPop raster the download also writes, and the one the population step reads:
        /// PopulationMap.CreatePopulation treats the geotransform as UTM metres.
        /// </summary>
        public static string WorldPopUtm(string name) => DownloadsFolder + "/" + WorldPopBaseName(name) + "_UTM.tif";

        public static string Dem(string name) => LandscapeFolder + "/" + name + "_dem.tif";
        public static string Slope(string name) => LandscapeFolder + "/" + name + "_slope.tif";
        public static string Aspect(string name) => LandscapeFolder + "/" + name + "_aspect.tif";
        public static string DemDownload(string name) => DownloadsFolder + "/" + name + "_dem_wgs84.tif";
        public static string Osm(string name) => DownloadsFolder + "/" + name + ".osm.xml";

        //These three stay in the root: they are the scenario's own description of itself rather than
        //data fetched or derived for it, and they are what a person opening the folder looks for.
        public static string RouterDb(string name) => name + ".routerdb";
        public static string Population(string name) => name + "_population.csv";
        public static string Weather(string name) => name + "_weather.csv";


        /// <summary>The case folder, relative, normalised; "elmfire" when the scenario names none.</summary>
        public static string CaseDirectory(PREACT.Input.PREACTInput input)
        {
            string dir = input?.WildfireModule?.ElmfireInput?.CaseDirectory;
            if (string.IsNullOrWhiteSpace(dir)) dir = "elmfire";
            return dir.Replace('\\', '/').TrimEnd('/');
        }

        public static string CaseInput(PREACT.Input.PREACTInput input, string file) => CaseDirectory(input) + "/inputs/" + file;

        /// <summary>The ELMFIRE case's namelist, which is written last and so marks the case as built.</summary>
        public static string ElmfireNamelist(PREACT.Input.PREACTInput input) => CaseDirectory(input) + "/elmfire.data";

        /// <summary>Data rows in a population CSV, excluding its header.</summary>
        public static int CountPopulationRows(string path)
        {
            int rows = 0;
            using (var reader = new StreamReader(path))
            {
                reader.ReadLine();
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line)) ++rows;
                }
            }
            return rows;
        }

        /// <summary>
        /// Whether a domain lies where LANDFIRE has data: the conterminous United States, Alaska or Hawaii.
        /// A bounding-box answer, cheap enough to decide what to offer; the download itself confirms it.
        /// </summary>
        public static bool IsInLandfireCoverage(Vector2d lowerLeftLatLon, Vector2d upperRightLatLon)
        {
            double lat = 0.5 * (lowerLeftLatLon.x + upperRightLatLon.x);
            double lon = 0.5 * (lowerLeftLatLon.y + upperRightLatLon.y);

            bool conus = lat >= 24.0 && lat <= 49.5 && lon >= -125.0 && lon <= -66.5;
            bool alaska = lat >= 51.0 && lat <= 71.5 && (lon >= -180.0 && lon <= -129.0);
            bool hawaii = lat >= 18.5 && lat <= 22.5 && lon >= -160.5 && lon <= -154.5;
            return conus || alaska || hawaii;
        }

        /// <summary>A path the scenario recorded, made absolute against its folder, separators normalised.</summary>
        public static string Resolve(string root, string recorded)
        {
            if (string.IsNullOrWhiteSpace(recorded)) return null;
            string normalised = recorded.Replace('\\', '/');
            try
            {
                return Path.IsPathRooted(normalised) || string.IsNullOrEmpty(root) ? normalised : Path.Combine(root, normalised);
            }
            catch
            {
                return null;
            }
        }
    }
}
