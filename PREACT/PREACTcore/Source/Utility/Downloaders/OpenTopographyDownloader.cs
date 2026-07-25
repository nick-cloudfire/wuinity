using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using PREACT.Math;

namespace PREACT.Tools
{
    /// <summary>
    /// Global DEM downloader (docs/probabilistic-trigger-convergence.md, "Topography / DEM
    /// downloader") — replaces LANDFIRE (US-only) outside the US via OpenTopography's Global DEM
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

        /// <summary>
        /// Downloads a DEM covering [lowerLeftLatLon, upperRightLatLon] to <paramref name="outputPath"/>
        /// (a GeoTIFF). <paramref name="lowerLeftLatLon"/>/<paramref name="upperRightLatLon"/> follow
        /// this codebase's (lat, lon) = (x, y) `Vector2d` convention.
        /// </summary>
        public static async Task Download(Vector2d lowerLeftLatLon, Vector2d upperRightLatLon, string apiKey, string outputPath, string demType = DemTypeCopernicus30)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentException("An OpenTopography API key is required.", nameof(apiKey));
            }

            string url = BuildUrl(lowerLeftLatLon, upperRightLatLon, apiKey, demType);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath)));

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

            return $"{BaseUrl}?demtype={demType}&south={south}&north={north}&west={west}&east={east}" +
                   $"&outputFormat=GTiff&API_Key={Uri.EscapeDataString(apiKey)}";
        }
    }
}
