using PREACT.Math;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using OsmSharp.Streams;

namespace PREACT.Tools
{
    /// <summary>
    /// Downloads the roads of an area from Overpass as an OSM XML file, for the RouterDb and the SUMO network.
    /// </summary>
    /// <remarks>
    /// Throws when no usable data arrived, and writes the file only then: the GUI chains the RouterDb and the SUMO
    /// network after this step and skips a step whose output exists, so a download that "finished" without data, or
    /// left half a file behind, poisoned every step after it. It used to log the Overpass error and return normally -
    /// "Overpass returned 504 GatewayTimeout", then "Downloading OSM roads: done", then "Building the RouterDb FAILED:
    /// Download the OSM data first" (Auburn2, v1) - and the retry it meant to make never ran (the counter started at
    /// 20 and retried only below 5).
    ///
    /// The public Overpass servers are busy rather than broken: a 504 (dispatcher queue full), a 429 (too many
    /// requests from this address) or a 200 whose body says the query timed out are all answered by asking again
    /// later or elsewhere. So every server in <see cref="Options.Endpoints"/> is tried in turn, and the whole list
    /// again after a growing pause, before the step gives up and says what each attempt was told.
    /// </remarks>
    public static class OSMDownloader
    {
        /// <summary>
        /// The main public instance, then an independent mirror with the same API and the same planet data.
        /// </summary>
        public static readonly string[] DefaultEndpoints =
        {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.kumi.systems/api/interpreter",
        };

        /// <summary>
        /// Overpass servers to use instead of <see cref="DefaultEndpoints"/>, separated by <c>;</c> - for a private
        /// instance, or a network that reaches only some of the public ones.
        /// </summary>
        public const string EndpointsVariable = "PREACT_OVERPASS_URLS";

        public sealed class Options
        {
            /// <summary>Interpreter URLs in the order they are tried. Null: <see cref="EndpointsVariable"/>, else the defaults.</summary>
            public IList<string> Endpoints;

            /// <summary>How many times the whole list is tried.</summary>
            public int Rounds = 3;

            /// <summary>The pause after the first round in which every server failed; doubled after each further one.</summary>
            public TimeSpan FirstBackoff = TimeSpan.FromSeconds(15);

            public TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

            /// <summary>How long one request may take, transfer included.</summary>
            public TimeSpan RequestTimeout = TimeSpan.FromMinutes(6);

            /// <summary>The query's own [timeout:], after which Overpass gives up on it and says so in the body.</summary>
            public int QueryTimeoutSeconds = 300;

            /// <summary>Where progress and each failed attempt are reported. Null: the engine log.</summary>
            public Action<string> Log;

            /// <summary>Ends the waiting between attempts, and an attempt under way, with an OperationCanceledException.</summary>
            public CancellationToken Cancellation;
        }

        /// <summary>What was written, and where from.</summary>
        public sealed class Result
        {
            public string Path;
            public string Endpoint;
            public int Attempts;
            public int Nodes;
            public int Ways;
            public long Bytes;
        }

        //One client: HttpClient is meant to be shared, and creating one per attempt exhausts sockets under retries.
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            //Overpass rejects clients that do not identify themselves; without this the public instance can answer
            //406 or 429 rather than serving the query.
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "PREACT/WUInity (OSM roads for evacuation modelling)");
            return client;
        }

        /// <summary>The GUI's entry point: the defaults, reporting to the engine log. Throws on failure.</summary>
        public static Task Download(Vector2d lowerLeftLatLon, Vector2d upperRightLatLon, string saveFilePath)
        {
            return DownloadAsync(lowerLeftLatLon, upperRightLatLon, saveFilePath, new Options());
        }

        /// <summary>
        /// Downloads the roads in [<paramref name="lowerLeftLatLon"/>, <paramref name="upperRightLatLon"/>] ((lat, lon)
        /// as x, y) to <paramref name="saveFilePath"/>. Throws, with what every attempt was told, when no server
        /// delivered a complete answer with roads in it; nothing is written then, and an earlier file is left as it was.
        /// </summary>
        public static async Task<Result> DownloadAsync(Vector2d lowerLeftLatLon, Vector2d upperRightLatLon,
            string saveFilePath, Options options)
        {
            options = options ?? new Options();
            Action<string> log = options.Log ?? (m => Engine.Message(null, Engine.LogType.Log, m));
            IList<string> endpoints = ResolveEndpoints(options.Endpoints);
            if (endpoints.Count == 0)
            {
                throw new ArgumentException("No Overpass server to ask.", nameof(options));
            }

            string query = BuildQuery(lowerLeftLatLon, upperRightLatLon, options.QueryTimeoutSeconds);
            string folder = Path.GetDirectoryName(Path.GetFullPath(saveFilePath));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            string part = saveFilePath + ".part";

            var failures = new List<string>();
            int attempt = 0;
            int rounds = System.Math.Max(1, options.Rounds);
            for (int round = 1; round <= rounds; ++round)
            {
                TimeSpan? retryAfter = null;
                foreach (string endpoint in endpoints)
                {
                    options.Cancellation.ThrowIfCancellationRequested();
                    ++attempt;
                    string host = HostOf(endpoint);
                    log($"Downloading OSM roads from {host} (attempt {attempt} of {rounds * endpoints.Count})...");

                    Attempt outcome;
                    try
                    {
                        outcome = await TryOnce(endpoint, query, part, options).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        //Stopped during the transfer: nothing half-downloaded is left behind (review R2 NI-2).
                        TryDelete(part);
                        throw;
                    }
                    if (outcome.Ok)
                    {
                        Result result = Finish(part, saveFilePath, endpoint, attempt, outcome);
                        log($"OSM data saved to {saveFilePath} ({result.Ways} roads, {result.Nodes} nodes, "
                            + $"{result.Bytes / (1024.0 * 1024.0):F1} MB, from {host}).");
                        return result;
                    }

                    TryDelete(part);
                    failures.Add($"{host}: {outcome.Problem}");
                    log($"  {host}: {outcome.Problem}");
                    if (outcome.RetryAfter.HasValue && (!retryAfter.HasValue || outcome.RetryAfter > retryAfter))
                    {
                        retryAfter = outcome.RetryAfter;
                    }

                    if (!outcome.Retryable)
                    {
                        //Asking again gets the same answer: the query or the area is what is wrong.
                        throw new Exception("Downloading the OSM roads failed: " + outcome.Problem);
                    }
                }

                if (round < rounds)
                {
                    TimeSpan wait = Backoff(options, round, retryAfter);
                    log($"No Overpass server could answer; trying again in {wait.TotalSeconds:F0} s.");
                    await Task.Delay(wait, options.Cancellation).ConfigureAwait(false);
                }
            }

            throw new Exception($"Downloading the OSM roads failed: no Overpass server delivered the data after {attempt} "
                + "attempts. The public servers are often busy; trying again later usually works. What each said: "
                + string.Join("; ", failures));
        }

        /// <summary>
        /// The pause after round <paramref name="round"/> (1-based) in which every server failed: the first backoff,
        /// doubled each round, capped, and never shorter than a server's own Retry-After (also capped).
        /// </summary>
        internal static TimeSpan Backoff(Options options, int round, TimeSpan? retryAfter)
        {
            double seconds = options.FirstBackoff.TotalSeconds * System.Math.Pow(2.0, round - 1);
            if (retryAfter.HasValue) seconds = System.Math.Max(seconds, retryAfter.Value.TotalSeconds);
            seconds = System.Math.Min(seconds, options.MaxBackoff.TotalSeconds);
            return TimeSpan.FromSeconds(System.Math.Max(0.0, seconds));
        }

        internal static IList<string> ResolveEndpoints(IList<string> given)
        {
            if (given != null && given.Count > 0) return given;

            string fromEnvironment = Environment.GetEnvironmentVariable(EndpointsVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                var list = new List<string>();
                foreach (string url in fromEnvironment.Split(';'))
                {
                    if (!string.IsNullOrWhiteSpace(url)) list.Add(url.Trim());
                }
                if (list.Count > 0) return list;
            }

            return DefaultEndpoints;
        }

        /// <summary>
        /// Roads only, plus the nodes that give them geometry: every value of "highway" (pedestrians use footways;
        /// netconvert drops what cars cannot use), recursed down only - up would drag in every relation a way is in.
        /// </summary>
        public static string BuildQuery(Vector2d lowerLeftLatLon, Vector2d upperRightLatLon, int timeoutSeconds)
        {
            //Invariant culture: on a comma-decimal locale "38.01" becomes "38,01" and the four-number box eight numbers.
            string bbox = string.Join(",", new[]
            {
                lowerLeftLatLon.x.ToString("R", CultureInfo.InvariantCulture),
                lowerLeftLatLon.y.ToString("R", CultureInfo.InvariantCulture),
                upperRightLatLon.x.ToString("R", CultureInfo.InvariantCulture),
                upperRightLatLon.y.ToString("R", CultureInfo.InvariantCulture),
            });

            return $"[out:xml][timeout:{timeoutSeconds.ToString(CultureInfo.InvariantCulture)}];"
                   + "way[\"highway\"](" + bbox + ");"
                   + "(._;>;);"
                   + "out body;";
        }

        internal struct Attempt
        {
            public bool Ok;
            public bool Retryable;
            public string Problem;
            public TimeSpan? RetryAfter;
            public int Nodes;
            public int Ways;
        }

        private static async Task<Attempt> TryOnce(string endpoint, string query, string part, Options options)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(options.Cancellation))
            {
                timeout.CancelAfter(options.RequestTimeout);
                try
                {
                    //The query in a "data" form field, which is what Overpass reads.
                    using (var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("data", query) }))
                    using (HttpResponseMessage response = await Client.PostAsync(endpoint, content, timeout.Token).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            string body = await ReadSome(response).ConfigureAwait(false);
                            int code = (int)response.StatusCode;
                            return new Attempt
                            {
                                Retryable = IsRetryable(response.StatusCode),
                                Problem = $"HTTP {code} {response.StatusCode}" + Explain(body),
                                RetryAfter = RetryAfterOf(response),
                            };
                        }

                        using (Stream body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
                        {
                            await body.CopyToAsync(file, 81920, timeout.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (!options.Cancellation.IsCancellationRequested)
                {
                    return new Attempt { Retryable = true, Problem = $"no complete answer within {options.RequestTimeout.TotalMinutes:F0} min" };
                }
                catch (HttpRequestException e)
                {
                    return new Attempt { Retryable = true, Problem = "could not be reached (" + Innermost(e) + ")" };
                }
                catch (IOException e)
                {
                    return new Attempt { Retryable = true, Problem = "the transfer broke off (" + e.Message + ")" };
                }
            }

            return Inspect(part);
        }

        /// <summary>
        /// Whether a 200 response really holds the roads: OSM XML to its end, no runtime error in a remark (Overpass
        /// answers a query that ran out of time or memory with 200, the data so far and a remark saying so), and at
        /// least one road.
        /// </summary>
        internal static Attempt Inspect(string path)
        {
            int nodes = 0, ways = 0;
            string remark = null;
            bool osmRoot = false;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, IgnoreWhitespace = true };
                using (XmlReader reader = XmlReader.Create(path, settings))
                {
                    bool root = true;
                    bool more = reader.Read();
                    while (more)
                    {
                        if (reader.NodeType == XmlNodeType.Element)
                        {
                            if (root)
                            {
                                root = false;
                                osmRoot = reader.Name == "osm";
                                if (!osmRoot) break;
                            }
                            else if (reader.Name == "remark")
                            {
                                //Reading the content moves past the end tag, onto what follows, which is not skipped.
                                remark = reader.ReadElementContentAsString().Trim();
                                more = !reader.EOF;
                                continue;
                            }
                            else if (reader.Name == "node") ++nodes;
                            else if (reader.Name == "way") ++ways;
                        }
                        more = reader.Read();
                    }
                }
            }
            catch (XmlException e)
            {
                return new Attempt { Retryable = true, Problem = "the answer was cut short or is not OSM XML (" + e.Message + ")" };
            }

            if (!osmRoot)
            {
                return new Attempt { Retryable = true, Problem = "the answer is not OSM data" + Explain(ReadHead(path)) };
            }

            if (!string.IsNullOrEmpty(remark) && remark.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                //Partial data: never kept, however much of it there is.
                return new Attempt { Retryable = true, Problem = "Overpass stopped the query: " + Shorten(remark, 200) };
            }

            if (ways == 0)
            {
                return new Attempt
                {
                    Retryable = false,
                    Problem = "Overpass found no roads in the area. Check the area of interest (step 1, Place and time).",
                };
            }

            return new Attempt { Ok = true, Nodes = nodes, Ways = ways };
        }

        /// <summary>
        /// Writes the checked download into place through OsmSharp, as the file always was written, then removes the
        /// raw copy. An earlier file is replaced only now.
        /// </summary>
        private static Result Finish(string part, string saveFilePath, string endpoint, int attempt, Attempt outcome)
        {
            string written = saveFilePath + ".tmp";
            try
            {
                using (var input = File.OpenRead(part))
                using (var source = new XmlOsmStreamSource(input))
                using (var output = File.Create(written))
                {
                    var target = new XmlOsmStreamTarget(output);
                    target.RegisterSource(source);
                    target.Pull();
                }

                if (File.Exists(saveFilePath)) File.Delete(saveFilePath);
                File.Move(written, saveFilePath);
            }
            finally
            {
                TryDelete(part);
                TryDelete(written);
            }

            return new Result
            {
                Path = saveFilePath,
                Endpoint = endpoint,
                Attempts = attempt,
                Nodes = outcome.Nodes,
                Ways = outcome.Ways,
                Bytes = new FileInfo(saveFilePath).Length,
            };
        }

        internal static bool IsRetryable(HttpStatusCode status)
        {
            int code = (int)status;
            //Busy, rate-limited, timed out or briefly down. A 400 is a query Overpass cannot parse, which a retry repeats.
            return code == 408 || code == 429 || code >= 500;
        }

        private static TimeSpan? RetryAfterOf(HttpResponseMessage response)
        {
            var header = response.Headers.RetryAfter;
            if (header == null) return null;
            if (header.Delta.HasValue) return header.Delta;
            if (header.Date.HasValue)
            {
                TimeSpan wait = header.Date.Value - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
            return null;
        }

        private static async Task<string> ReadSome(HttpResponseMessage response)
        {
            try
            {
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return body != null && body.Length > 8192 ? body.Substring(0, 8192) : body;
            }
            catch
            {
                return null;
            }
        }

        private static string ReadHead(string path)
        {
            try
            {
                using (var reader = new StreamReader(path, Encoding.UTF8))
                {
                    var buffer = new char[4096];
                    int n = reader.ReadBlock(buffer, 0, buffer.Length);
                    return new string(buffer, 0, n);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// What an Overpass error page says, as one readable sentence: its "Error: ..." paragraphs when it has them
        /// (the 504 page's "runtime error: ... The server is probably too busy to handle your request."), else its
        /// text without markup. Empty for an empty body; prefixed with " - " otherwise.
        /// </summary>
        internal static string Explain(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return string.Empty;

            var said = new List<string>();
            int at = 0;
            while ((at = body.IndexOf("Error</strong>:", at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                at += "Error</strong>:".Length;
                int end = body.IndexOf("</p>", at, StringComparison.OrdinalIgnoreCase);
                if (end < 0) end = body.Length;
                string text = Squash(StripTags(body.Substring(at, end - at)));
                if (text.Length > 0 && !said.Contains(text)) said.Add(text);
                at = end;
            }

            string message = said.Count > 0 ? string.Join(" ", said) : Squash(StripTags(body));
            return message.Length == 0 ? string.Empty : " - " + Shorten(message, 300);
        }

        private static string StripTags(string html)
        {
            var sb = new StringBuilder(html.Length);
            bool inTag = false;
            foreach (char c in html)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; sb.Append(' '); continue; }
                if (!inTag) sb.Append(c);
            }
            return WebUtility.HtmlDecode(sb.ToString());
        }

        private static string Squash(string text)
        {
            var sb = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = sb.Length > 0;
                    continue;
                }
                if (space) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Shorten(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "...";

        private static string Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e.Message;
        }

        private static string HostOf(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri.Authority : url;
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
