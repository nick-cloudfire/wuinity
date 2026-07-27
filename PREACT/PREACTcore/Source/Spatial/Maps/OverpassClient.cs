using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using OsmSharp;
using OsmSharp.Streams;

namespace PREACT.Spatial
{
    /// <summary>
    /// Overpass is used to download OSM data as the default OSM server does not allow big enough areas.
    /// </summary>
    public class OverpassClient
    {
        private readonly HttpClient _http;

        public OverpassClient()
        {
            _http = new HttpClient();
            //Overpass rejects requests from clients that do not identify themselves; without this
            //the public instance can answer 406 or 429 rather than serving the query.
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PREACT/WUInity (OSM data for evacuation modelling)");
            _http.Timeout = TimeSpan.FromMinutes(5);
        }

        public async Task<List<OsmGeo>> DownloadBoundingBoxAsync(double south, double west, double north, double east)
        {
            //Invariant culture is essential, not tidiness: the default formatting follows the
            //machine's locale, and on any comma-decimal locale "38.01" becomes "38,01", which
            //silently turns a four-number bounding box into an eight-number one and makes the
            //query invalid.
            string bbox = string.Join(",", new[]
            {
                south.ToString("R", CultureInfo.InvariantCulture),
                west.ToString("R", CultureInfo.InvariantCulture),
                north.ToString("R", CultureInfo.InvariantCulture),
                east.ToString("R", CultureInfo.InvariantCulture),
            });

            // Build Overpass query
            string query =
                "[out:xml][timeout:180];(" +
                "node(" + bbox + ");" +
                "way(" + bbox + ");" +
                "relation(" + bbox + ");" +
                ");" +
                "(._;>;>>;);" +
                "out body;";

            //Sent as a proper form field. The query used to be posted as the raw body while
            //declaring application/x-www-form-urlencoded (and with a charset parameter Overpass
            //does not accept on that type), which is what the 406 responses were: the server
            //could not make sense of the content it was told to expect. Overpass wants the query
            //in a "data" field.
            var content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("data", query),
            });

            var response = await _http.PostAsync(
                "https://overpass-api.de/api/interpreter", content);

            if (!response.IsSuccessStatusCode)
            {
                //Overpass explains itself in the body; the status code alone rarely says why.
                string detail = await response.Content.ReadAsStringAsync();
                if (detail != null && detail.Length > 400) detail = detail.Substring(0, 400) + "...";
                throw new HttpRequestException(
                    $"Overpass returned {(int)response.StatusCode} {response.StatusCode}. {detail}");
            }

            string xml = await response.Content.ReadAsStringAsync();

            // Parse XML into OsmGeo objects
            var result = new List<OsmGeo>();

            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(xml)))
            {
                using (var source = new XmlOsmStreamSource(ms))
                {
                    foreach (var osmGeo in source)
                    {
                        result.Add(osmGeo);
                    }
                }
            }

            return result;            
        }
    }
}

