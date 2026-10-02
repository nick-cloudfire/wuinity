using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PREACT.Math;

namespace PREACT.Tools
{
    /// <summary>
    /// The LANDFIRE releases the LANDFIRE Product Service (LFPS) serves fuel and canopy for, and which one a scenario
    /// asks for.
    /// </summary>
    /// <remarks>
    /// From LFPS's own product list (lfps.usgs.gov/api/products, October 2026): fuel models and canopy as
    /// <c>LF2016_*</c> (the 2016 remap), <c>LF2022_*</c>, <c>LF2023_*</c>, <c>LF2024_*</c> and <c>LF2025_*</c>, topography as
    /// <c>LF2020_Elev/SlpD/Asp</c> only. There is no <c>LF2020_FBFM40</c>, which the downloader used to ask for for any
    /// scenario year from 2018 to 2021. A release is named for the year whose disturbances it includes, so a historic
    /// fire is best run on the release before it: a later one already has the burn scar in it.
    /// </remarks>
    public static class LandfireVersions
    {
        /// <summary>The setting that picks the release nearest the scenario's year (the default).</summary>
        public const string Closest = "closest";

        public enum Region { Conus, Alaska, Hawaii, None }

        public sealed class Release
        {
            public int Year;
            public string Description;
            public bool Conus, Alaska, Hawaii;

            /// <summary>The LFPS layer prefix: <c>LF2024</c>.</summary>
            public string Name => "LF" + Year.ToString(CultureInfo.InvariantCulture);

            public bool Covers(Region region)
            {
                switch (region)
                {
                    case Region.Conus: return Conus;
                    case Region.Alaska: return Alaska;
                    case Region.Hawaii: return Hawaii;
                    default: return false;
                }
            }

            public override string ToString() => Name;
        }

        /// <summary>Oldest first.</summary>
        public static readonly Release[] All =
        {
            new Release { Year = 2016, Description = "LF 2016 Remap (LANDFIRE 2.0.0)", Conus = true, Alaska = true, Hawaii = true },
            new Release { Year = 2022, Description = "LF 2022 update", Conus = true, Alaska = true, Hawaii = true },
            new Release { Year = 2023, Description = "LF 2023 update", Conus = true, Alaska = true, Hawaii = true },
            new Release { Year = 2024, Description = "LF 2024 update", Conus = true, Alaska = true, Hawaii = true },
            new Release { Year = 2025, Description = "LF 2025 update (CONUS only)", Conus = true },
        };

        /// <summary>The topography LFPS serves, whatever the fuel release.</summary>
        public const string TopographyRelease = "LF2020";

        /// <summary>
        /// Which LANDFIRE map zone set covers a domain: the conterminous United States, Alaska or Hawaii, by its centre.
        /// A bounding-box answer; LFPS itself confirms it.
        /// </summary>
        public static Region RegionOf(Vector2d lowerLeftLatLon, Vector2d upperRightLatLon)
        {
            double lat = 0.5 * (lowerLeftLatLon.x + upperRightLatLon.x);
            double lon = 0.5 * (lowerLeftLatLon.y + upperRightLatLon.y);

            if (lat >= 24.0 && lat <= 49.5 && lon >= -125.0 && lon <= -66.5) return Region.Conus;
            if (lat >= 51.0 && lat <= 71.5 && lon >= -180.0 && lon <= -129.0) return Region.Alaska;
            if (lat >= 18.5 && lat <= 22.5 && lon >= -160.5 && lon <= -154.5) return Region.Hawaii;
            return Region.None;
        }

        /// <summary>
        /// The setting in its written form: <see cref="Closest"/>, or a release name (<c>LF2022</c>); null when it
        /// names neither. Accepts <c>2022</c>, <c>lf2022</c>, <c>LF 2022</c> and an empty value (closest).
        /// </summary>
        public static string Normalise(string setting)
        {
            if (string.IsNullOrWhiteSpace(setting)) return Closest;
            string s = setting.Trim();
            if (string.Equals(s, Closest, StringComparison.OrdinalIgnoreCase)) return Closest;

            s = s.Replace(" ", string.Empty).Replace("_", string.Empty);
            if (s.StartsWith("LF", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            if (s.EndsWith("Remap", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 5);
            if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int year))
            {
                foreach (Release r in All)
                {
                    if (r.Year == year) return r.Name;
                }
            }
            return null;
        }

        /// <summary>
        /// The release to ask for: the one the setting names, or (for <see cref="Closest"/>) the one nearest
        /// <paramref name="scenarioYear"/> among those covering <paramref name="region"/> - the earlier of two equally
        /// near, the fuels before an event rather than after it. Throws for a setting naming no release, or a release
        /// that does not cover the region.
        /// </summary>
        public static Release Resolve(string setting, int scenarioYear, Region region, out string explanation)
        {
            string normalised = Normalise(setting);
            if (normalised == null)
            {
                throw new ArgumentException($"LandfireVersion={setting} is not a LANDFIRE release LFPS serves; use "
                    + Closest + " or one of " + string.Join(", ", Names()) + ".");
            }

            if (normalised != Closest)
            {
                Release named = Array.Find(All, r => r.Name == normalised);
                if (!named.Covers(region))
                {
                    throw new ArgumentException($"{named.Name} does not cover {Describe(region)}; choose another LANDFIRE release.");
                }
                explanation = $"{named.Name} ({named.Description}), as the scenario's LandfireVersion asks.";
                return named;
            }

            Release best = null;
            foreach (Release r in All)
            {
                if (!r.Covers(region)) continue;
                if (best == null || System.Math.Abs(r.Year - scenarioYear) < System.Math.Abs(best.Year - scenarioYear))
                {
                    best = r;
                }
            }
            if (best == null)
            {
                throw new ArgumentException("LANDFIRE has no fuel release for " + Describe(region) + ".");
            }

            explanation = $"{best.Name} ({best.Description}), the release closest to the scenario's year {scenarioYear}"
                          + (best.Year > scenarioYear
                              ? $"; it includes disturbances up to {best.Year}, after the scenario - choose an earlier release for a historic fire"
                              : string.Empty)
                          + ".";
            return best;
        }

        public static IEnumerable<string> Names()
        {
            foreach (Release r in All) yield return r.Name;
        }

        private static string Describe(Region region)
        {
            switch (region)
            {
                case Region.Conus: return "the conterminous United States";
                case Region.Alaska: return "Alaska";
                case Region.Hawaii: return "Hawaii";
                default: return "this area (LANDFIRE covers the United States only)";
            }
        }

        /// <summary>
        /// The LFPS layer list: elevation, slope, aspect, the fuel model, then CC, CH, CBH and CBD - LANDFIRE's landscape
        /// order, which [Landscape] LandscapeFile also reads a multiband file in. FCCS is no longer asked for: nothing
        /// reads it, and LFPS does not serve it for every release.
        /// </summary>
        public static string LayerList(Release release, bool anderson13)
        {
            string v = release.Name;
            return string.Join(";",
                TopographyRelease + "_Elev", TopographyRelease + "_SlpD", TopographyRelease + "_Asp",
                v + (anderson13 ? "_FBFM13" : "_FBFM40"), v + "_CC", v + "_CH", v + "_CBH", v + "_CBD");
        }
    }

    /// <summary>
    /// The LANDFIRE Product Service client: submits a job for an area and a layer list, waits for it, downloads the
    /// result zip and unpacks it.
    /// </summary>
    /// <remarks>
    /// The download returns where the result raster is. The GUI used to look for "a .tif written since the step
    /// started" in the folder instead; the zip's entries carry LFPS's own timestamps (US Central time, read as local
    /// time), so on a machine in Athens the raster that had just been unpacked was eight hours old and the step said
    /// "LANDFIRE returned nothing usable" with the right file on disk (Auburn2, v1).
    /// </remarks>
    public static class LandfireLandscapeDownloader
    {
        public const string DefaultServiceUrl = "https://lfps.usgs.gov/api";

        public sealed class Request
        {
            /// <summary>The area to cut, (lat, lon) as x, y. Already grown by whatever margin the caller needs.</summary>
            public Vector2d LowerLeft, UpperRight;

            /// <summary>The <c>Layer_List</c>, from <see cref="LandfireVersions.LayerList"/>.</summary>
            public string LayerList;

            /// <summary>EPSG code of the output projection: the scenario's UTM zone.</summary>
            public int OutputEpsg;

            /// <summary>LFPS asks every request for a contact e-mail.</summary>
            public string Email;

            /// <summary>Where the zip and the folder it is unpacked into go.</summary>
            public string DownloadFolder;

            public string ServiceUrl = DefaultServiceUrl;
            public TimeSpan PollInterval = TimeSpan.FromSeconds(20);
            public TimeSpan MaxWait = TimeSpan.FromMinutes(30);
            public int Retries = 5;
            public TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);
            public TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);
            public Action<string> Log;
            public CancellationToken Cancellation;
        }

        public sealed class Result
        {
            public string JobId;
            public string ZipPath;

            /// <summary>The multi-band GeoTIFF the zip held, unpacked beside its .tfw and .aux.xml.</summary>
            public string RasterPath;
        }

        private static readonly HttpClient Client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        /// <summary>Submits, waits, downloads and unpacks. Throws, saying what LFPS said, when any of it fails.</summary>
        public static async Task<Result> DownloadAsync(Request request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Email))
            {
                throw new ArgumentException("LANDFIRE asks every request for a contact e-mail, and none is set.");
            }
            Action<string> log = request.Log ?? (m => Engine.Message(null, Engine.LogType.Log, m));
            Directory.CreateDirectory(request.DownloadFolder);

            string submitUrl = SubmitUrl(request);
            log($"Submitting the LANDFIRE (LFPS) job: {request.LayerList.Replace(";", ", ")} for "
                + $"{request.LowerLeft.x:F4},{request.LowerLeft.y:F4} to {request.UpperRight.x:F4},{request.UpperRight.y:F4}, "
                + $"in EPSG:{request.OutputEpsg}.");

            string submitted = await GetWithRetries(submitUrl, "submit the LANDFIRE job", request, log).ConfigureAwait(false);
            string jobId = ReadString(submitted, "jobId");
            if (string.IsNullOrEmpty(jobId))
            {
                throw new Exception("LFPS accepted the request but returned no job ID: " + Shorten(submitted));
            }
            log($"LFPS job {jobId} submitted.");

            string outputUrl = await WaitForJob(jobId, request, log).ConfigureAwait(false);
            string zip = Path.Combine(request.DownloadFolder, jobId + ".zip");
            await DownloadFile(outputUrl, zip, request, log).ConfigureAwait(false);

            string raster = Unpack(zip, Path.Combine(request.DownloadFolder, jobId));
            log($"LANDFIRE result unpacked: {raster}");
            return new Result { JobId = jobId, ZipPath = zip, RasterPath = raster };
        }

        /// <summary>The submit URL: projection, layers, area (west south east north) and e-mail, escaped.</summary>
        public static string SubmitUrl(Request r)
        {
            string area = string.Join(" ",
                r.LowerLeft.y.ToString("R", CultureInfo.InvariantCulture), r.LowerLeft.x.ToString("R", CultureInfo.InvariantCulture),
                r.UpperRight.y.ToString("R", CultureInfo.InvariantCulture), r.UpperRight.x.ToString("R", CultureInfo.InvariantCulture));
            return r.ServiceUrl.TrimEnd('/') + "/job/submit?"
                   + "Output_Projection=" + r.OutputEpsg.ToString(CultureInfo.InvariantCulture)
                   + "&Layer_List=" + Uri.EscapeDataString(r.LayerList)
                   + "&Area_of_Interest=" + Uri.EscapeDataString(area)
                   + "&Email=" + Uri.EscapeDataString(r.Email.Trim());
        }

        /// <summary>
        /// Polls the job until it succeeds (returning its output file's URL), fails or is cancelled (throwing with
        /// LFPS's messages), or takes longer than <see cref="Request.MaxWait"/>. Only a change of state is logged.
        /// </summary>
        private static async Task<string> WaitForJob(string jobId, Request request, Action<string> log)
        {
            string statusUrl = request.ServiceUrl.TrimEnd('/') + "/job/status?JobId=" + Uri.EscapeDataString(jobId);
            DateTime start = DateTime.UtcNow;
            string lastReported = null;

            while (true)
            {
                if (DateTime.UtcNow - start > request.MaxWait)
                {
                    throw new TimeoutException($"The LANDFIRE (LFPS) job {jobId} did not finish within {request.MaxWait.TotalMinutes:F0} "
                        + "minutes. LFPS is sometimes simply busy; trying again later works.");
                }

                string json = await GetWithRetries(statusUrl, "get the status of LANDFIRE job " + jobId, request, log).ConfigureAwait(false);
                string status = ReadString(json, "status") ?? ReadString(json, "jobStatus");
                if (string.IsNullOrEmpty(status))
                {
                    throw new Exception("The LANDFIRE (LFPS) status names no status: " + Shorten(json));
                }

                string queue = ReadRaw(json, "queuePosition");
                string report = status + (queue != null && queue != "-1" ? $", queue position {queue}" : string.Empty);
                if (report != lastReported)
                {
                    log("LFPS job: " + report + ".");
                    lastReported = report;
                }

                if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
                {
                    string output = ReadString(json, "outputFile");
                    if (string.IsNullOrEmpty(output))
                    {
                        throw new Exception($"The LANDFIRE (LFPS) job {jobId} succeeded but names no output file: " + Shorten(json));
                    }
                    return Uri.TryCreate(output, UriKind.Absolute, out Uri absolute)
                        ? absolute.ToString()
                        : request.ServiceUrl.TrimEnd('/') + "/" + output.TrimStart('/');
                }

                if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Canceled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase))
                {
                    string why = Messages(json);
                    throw new Exception($"The LANDFIRE (LFPS) job {jobId} {status.ToLowerInvariant()}"
                        + (why.Length > 0 ? ": " + why : "."));
                }

                await Task.Delay(request.PollInterval, request.Cancellation).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// A GET that is tried again, after a growing pause, when the network or the service has a moment (no answer,
        /// 408, 429, 5xx); a 4xx is LFPS refusing the request, said with its body and not repeated.
        /// </summary>
        private static async Task<string> GetWithRetries(string url, string what, Request request, Action<string> log)
        {
            string lastProblem = null;
            int tries = System.Math.Max(1, request.Retries);
            for (int attempt = 1; attempt <= tries; ++attempt)
            {
                request.Cancellation.ThrowIfCancellationRequested();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation))
                {
                    timeout.CancelAfter(request.RequestTimeout);
                    try
                    {
                        using (HttpResponseMessage response = await Client.GetAsync(url, timeout.Token).ConfigureAwait(false))
                        {
                            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                return body;
                            }

                            int code = (int)response.StatusCode;
                            lastProblem = $"HTTP {code} {response.StatusCode}" + (string.IsNullOrWhiteSpace(body) ? "" : " - " + Shorten(body));
                            if (code != 408 && code != 429 && code < 500)
                            {
                                throw new Exception($"LFPS refused to {what}: {lastProblem}");
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!request.Cancellation.IsCancellationRequested)
                    {
                        lastProblem = $"no answer within {request.RequestTimeout.TotalSeconds:F0} s";
                    }
                    catch (HttpRequestException e)
                    {
                        lastProblem = "could not reach it (" + Innermost(e) + ")";
                    }
                }

                if (attempt < tries)
                {
                    TimeSpan wait = TimeSpan.FromTicks(request.FirstBackoff.Ticks * attempt);
                    log($"Could not {what} ({lastProblem}); trying again in {wait.TotalSeconds:F0} s (attempt {attempt + 1} of {tries}).");
                    await Task.Delay(wait, request.Cancellation).ConfigureAwait(false);
                }
            }

            throw new Exception($"Could not {what} after {tries} attempts: {lastProblem}");
        }

        /// <summary>Downloads to a working copy, checks it is a zip, and only then puts it in place.</summary>
        private static async Task DownloadFile(string url, string destination, Request request, Action<string> log)
        {
            string part = destination + ".part";
            string lastProblem = null;
            int tries = System.Math.Max(1, request.Retries);
            for (int attempt = 1; attempt <= tries; ++attempt)
            {
                request.Cancellation.ThrowIfCancellationRequested();
                log($"Downloading the LANDFIRE result (attempt {attempt} of {tries}).");
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.Cancellation))
                {
                    timeout.CancelAfter(request.RequestTimeout);
                    try
                    {
                        using (HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                lastProblem = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                            }
                            else
                            {
                                using (Stream body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    await body.CopyToAsync(file, 81920, timeout.Token).ConfigureAwait(false);
                                }

                                string problem = CheckZip(part);
                                if (problem == null)
                                {
                                    if (File.Exists(destination)) File.Delete(destination);
                                    File.Move(part, destination);
                                    log($"LANDFIRE result downloaded: {destination} ({new FileInfo(destination).Length / (1024.0 * 1024.0):F1} MB).");
                                    return;
                                }
                                lastProblem = problem;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (!request.Cancellation.IsCancellationRequested)
                    {
                        lastProblem = $"no complete answer within {request.RequestTimeout.TotalMinutes:F0} min";
                    }
                    catch (HttpRequestException e)
                    {
                        lastProblem = "could not reach it (" + Innermost(e) + ")";
                    }
                    catch (IOException e)
                    {
                        lastProblem = "the transfer broke off (" + e.Message + ")";
                    }
                    finally
                    {
                        TryDelete(part);
                    }
                }

                if (attempt < tries)
                {
                    TimeSpan wait = TimeSpan.FromTicks(request.FirstBackoff.Ticks * attempt);
                    log($"  {lastProblem}; trying again in {wait.TotalSeconds:F0} s.");
                    await Task.Delay(wait, request.Cancellation).ConfigureAwait(false);
                }
            }

            throw new Exception($"Could not download the LANDFIRE result after {tries} attempts: {lastProblem}");
        }

        private static string CheckZip(string path)
        {
            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(path))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) return null;
                    }
                    return "the zip holds no GeoTIFF";
                }
            }
            catch (InvalidDataException)
            {
                return "what arrived is not a complete zip";
            }
        }

        /// <summary>
        /// Unpacks an LFPS result zip into <paramref name="folder"/> (replacing what an earlier unpack left there) and
        /// returns its raster: the zip's one GeoTIFF, or of several the one with the most bands. The .tfw and the
        /// .aux.xml come with it - the aux.xml is where LFPS states each band's units.
        /// </summary>
        public static string Unpack(string zipPath, string folder)
        {
            Directory.CreateDirectory(folder);
            string root = Path.GetFullPath(folder);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString())) root += Path.DirectorySeparatorChar;

            var rasters = new List<string>();
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; //a folder

                    //Flattened, and never outside the folder however the entry is named.
                    string destination = Path.GetFullPath(Path.Combine(folder, entry.Name));
                    if (!destination.StartsWith(root, StringComparison.Ordinal)) continue;

                    entry.ExtractToFile(destination, overwrite: true);
                    //The entry's own time is LFPS's local time read as this machine's; the unpack is what happened now.
                    File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);
                    if (destination.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)) rasters.Add(destination);
                }
            }

            if (rasters.Count == 0)
            {
                throw new Exception(Path.GetFileName(zipPath) + " holds no GeoTIFF.");
            }
            if (rasters.Count == 1)
            {
                return rasters[0];
            }

            string best = rasters[0];
            int bestBands = -1;
            foreach (string raster in rasters)
            {
                int bands = PREACT.Utility.AscRaster.GetBandCount(raster);
                if (bands > bestBands)
                {
                    best = raster;
                    bestBands = bands;
                }
            }
            return best;
        }

        // ------------------------------------------------------------------ JSON

        internal static string ReadString(string json, string name)
        {
            string raw = ReadRaw(json, name, true);
            return raw;
        }

        /// <summary>A top-level property, matched without regard to case, as text; null when absent or not JSON.</summary>
        internal static string ReadRaw(string json, string name, bool asString = false)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
                    foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                    {
                        if (!string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (p.Value.ValueKind == JsonValueKind.Null) return null;
                        return asString && p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString();
                    }
                }
            }
            catch (JsonException)
            {
            }
            return null;
        }

        /// <summary>LFPS's messages for a job, as one line: each one's description (or the message itself).</summary>
        internal static string Messages(string json)
        {
            var parts = new List<string>();
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(json))
                {
                    foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                    {
                        if (!string.Equals(p.Name, "messages", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(p.Name, "message", StringComparison.OrdinalIgnoreCase)) continue;

                        if (p.Value.ValueKind == JsonValueKind.String)
                        {
                            parts.Add(p.Value.GetString());
                        }
                        else if (p.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement m in p.Value.EnumerateArray())
                            {
                                if (m.ValueKind == JsonValueKind.String) parts.Add(m.GetString());
                                else if (m.ValueKind == JsonValueKind.Object)
                                {
                                    string text = null;
                                    foreach (JsonProperty q in m.EnumerateObject())
                                    {
                                        if (string.Equals(q.Name, "description", StringComparison.OrdinalIgnoreCase)
                                            || string.Equals(q.Name, "message", StringComparison.OrdinalIgnoreCase))
                                        {
                                            text = q.Value.ToString();
                                        }
                                    }
                                    parts.Add(text ?? m.ToString());
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                return Shorten(json);
            }
            parts.RemoveAll(string.IsNullOrWhiteSpace);
            return string.Join(" ", parts);
        }

        private static string Shorten(string text)
        {
            if (text == null) return string.Empty;
            text = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return text.Length <= 300 ? text : text.Substring(0, 300) + "...";
        }

        private static string Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e.Message;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
