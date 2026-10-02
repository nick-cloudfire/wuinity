using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using PREACT.Math;

namespace PREACT.Tools
{
    /// <summary>
    /// Global DEM downloader (docs/elmfire-cases.md, "Building a case, step by step") — replaces
    /// LANDFIRE (US-only) outside the US via OpenTopography's Global DEM
    /// API, which serves Copernicus GLO-30 and SRTM (among others) for any location on Earth.
    /// The API key is a caller-supplied parameter, the same as every other downloader in this
    /// folder — wiring it to a stored setting (the way WUInity already does for its Mapbox
    /// token) is a separate, Unity-side concern.
    /// </summary>
    public static class OpenTopographyDownloader
    {
        private static readonly HttpClient client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        private const string BaseUrl = "https://portal.opentopography.org/API/globaldem";
        private const int MaxRetries = 5;

        /// <summary>Copernicus GLO-30 (free, global, 30 m) — the default per this doc's DEM section.</summary>
        public const string DemTypeCopernicus30 = "COP30";
        public const string DemTypeCopernicus90 = "COP90";
        public const string DemTypeSrtm30 = "SRTMGL1";
        public const string DemTypeSrtm90 = "SRTMGL3";

        /// <summary>For the tests: a stub server instead of OpenTopography.</summary>
        internal static string BaseUrlOverride;

        /// <summary>The DEM's pixel in degrees: one arc-second for the 30 m products, three for the 90 m ones.</summary>
        public static double PixelDegrees(string demType)
        {
            switch ((demType ?? string.Empty).ToUpperInvariant())
            {
                case DemTypeCopernicus30:
                case DemTypeSrtm30:
                case "NASADEM":
                case "AW3D30":
                case "SRTMGL1_E":
                    return 1.0 / 3600.0;
                default:
                    return 3.0 / 3600.0;
            }
        }

        /// <summary>
        /// The box actually asked for: [lowerLeftLatLon, upperRightLatLon] grown to cover the UTM grid cut from it
        /// (<see cref="DownloadArea.CoverUtmGrid"/>) and four DEM pixels more on every side.
        /// </summary>
        /// <remarks>
        /// The service snaps the request to its pixel lattice and can come up a fraction of a pixel short - 0.0002 degrees
        /// on Auburn2's east side, which the case build reported as a DEM smaller than its padded domain - and the UTM
        /// grid's corners reach past a lat/lon box by hundreds of metres away from the zone's central meridian.
        /// </remarks>
        public static (Vector2d LowerLeft, Vector2d UpperRight) RequestBounds(Vector2d lowerLeftLatLon, Vector2d upperRightLatLon,
            string demType = DemTypeCopernicus30, int utmEpsg = 0)
        {
            return DownloadArea.CoverUtmGrid(lowerLeftLatLon, upperRightLatLon, utmEpsg, 4.0 * PixelDegrees(demType));
        }

        /// <summary>
        /// Downloads a DEM covering [lowerLeftLatLon, upperRightLatLon], with the margin <see cref="RequestBounds"/> adds,
        /// to <paramref name="outputPath"/> (a GeoTIFF). <paramref name="lowerLeftLatLon"/>/<paramref name="upperRightLatLon"/>
        /// follow this codebase's (lat, lon) = (x, y) `Vector2d` convention; <paramref name="utmEpsg"/> is the zone the DEM
        /// will be warped into (0: the zone of the box's centre).
        /// </summary>
        public static async Task Download(Vector2d lowerLeftLatLon, Vector2d upperRightLatLon, string apiKey, string outputPath,
            string demType = DemTypeCopernicus30, int utmEpsg = 0)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentException("An OpenTopography API key is required.", nameof(apiKey));
            }

            (Vector2d southWest, Vector2d northEast) = RequestBounds(lowerLeftLatLon, upperRightLatLon, demType, utmEpsg);
            string url = BuildUrl(southWest, northEast, apiKey, demType);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));
            Engine.Message(null, Engine.LogType.Log, FormattableString.Invariant(
                $"The DEM is asked for {southWest.x:F4},{southWest.y:F4} to {northEast.x:F4},{northEast.y:F4}: the area and a margin, so the UTM grid cut from it is covered to its corners."));

            Exception lastError = null;
            for (int attempt = 1; attempt <= MaxRetries; ++attempt)
            {
                try
                {
                    Engine.Message(null, Engine.LogType.Log, $"Requesting {demType} DEM from OpenTopography (attempt {attempt}).");
                    using HttpResponseMessage response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode();

                    using FileStream fs = new FileStream(outputPath, FileMode.Create);
                    await response.Content.CopyToAsync(fs);

                    Engine.Message(null, Engine.LogType.Log, $"DEM downloaded: {outputPath}");
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt == MaxRetries) break;

                    int backoff = attempt * 5;
                    Engine.Message(null, Engine.LogType.Log, $"DEM download failed: {ex.Message}. Retrying in {backoff}s.");
                    await Task.Delay(TimeSpan.FromSeconds(backoff));
                }
            }

            throw new Exception($"OpenTopography DEM download failed after {MaxRetries} attempts.", lastError);
        }

        /// <summary>Builds the request URL. Separated from <see cref="Download"/> so it can be
        /// unit-tested without a network call or a real API key.</summary>
        public static string BuildUrl(Vector2d lowerLeftLatLon, Vector2d upperRightLatLon, string apiKey, string demType = DemTypeCopernicus30)
        {
            string south = lowerLeftLatLon.x.ToString(CultureInfo.InvariantCulture);
            string west = lowerLeftLatLon.y.ToString(CultureInfo.InvariantCulture);
            string north = upperRightLatLon.x.ToString(CultureInfo.InvariantCulture);
            string east = upperRightLatLon.y.ToString(CultureInfo.InvariantCulture);

            return $"{BaseUrlOverride ?? BaseUrl}?demtype={demType}&south={south}&north={north}&west={west}&east={east}" +
                   $"&outputFormat=GTiff&API_Key={Uri.EscapeDataString(apiKey)}";
        }
    }
}
