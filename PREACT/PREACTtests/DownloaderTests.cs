using System.Text;
using PREACT.Math;
using PREACT.Tools;

namespace PREACT.Tests
{
    /// <summary>
    /// The data downloads, against a local stub server replaying what the real services answer: Overpass's 504 page,
    /// a 429, a query that ran out of time, a good answer; LFPS's job submit, status and result zip.
    /// </summary>
    internal static partial class DownloaderTests
    {
        public static void Register(Runner runner)
        {
            runner.Add("osm: a 504 fails over to the mirror, a 429 there is retried after a pause, the good answer is written and read by Itinero", OsmFailsOverAndRetries);
            runner.Add("osm: every server failing throws with what each said, and writes nothing (an earlier file is kept)", OsmFailureWritesNothing);
            runner.Add("osm: a 200 that Overpass cut short (runtime error remark) is not data; one with no roads stops at once", OsmPartialAnswers);
            runner.Add("osm: Overpass's error page is reported as its sentence, and the query asks for roads in the box", OsmMessagesAndQuery);
        }

        // ------------------------------------------------------------------ OSM

        /// <summary>The 504 page overpass-api.de serves when its dispatcher queue is full (Nick's Auburn2 log, v1).</summary>
        internal const string Overpass504Page =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.0 Strict//EN\"\n    \"http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd\">\n"
            + "<html xmlns=\"http://www.w3.org/1999/xhtml\" xml:lang=\"en\" lang=\"en\">\n<head>\n"
            + "  <meta http-equiv=\"content-type\" content=\"text/html; charset=utf-8\" lang=\"en\"/>\n"
            + "  <title>OSM3S Response</title>\n</head>\n<body>\n\n"
            + "<p>The data included in this document is from www.openstreetmap.org. The data is made available under ODbL.</p>\n"
            + "<p><strong style=\"color:#FF0000\">Error</strong>: runtime error: open64: 0 Success /osm3s_osm_base "
            + "Dispatcher_Client::request_read_and_idx::timeout. The server is probably too busy to handle your request. </p>\n\n"
            + "</body>\n</html>\n";

        /// <summary>A small Overpass answer: a road grid near the Auburn2 ignition, nodes first, as Overpass sorts them.</summary>
        internal static string OverpassRoads(string remark = null, bool noWays = false)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<osm version=\"0.6\" generator=\"Overpass API 0.7.62.1 084b4234\">\n");
            sb.Append("<note>The data included in this document is from www.openstreetmap.org. The data is made available under ODbL.</note>\n");
            sb.Append("<meta osm_base=\"2026-09-29T10:42:01Z\"/>\n\n");
            //A 4 x 4 lattice of nodes 0.002 deg apart, and a road along every row and every column.
            int id = 1;
            for (int r = 0; r < 4; ++r)
            {
                for (int c = 0; c < 4; ++c)
                {
                    sb.Append($"  <node id=\"{id++}\" lat=\"{(38.900 + 0.002 * r).ToString(System.Globalization.CultureInfo.InvariantCulture)}\" "
                        + $"lon=\"{(-121.070 + 0.002 * c).ToString(System.Globalization.CultureInfo.InvariantCulture)}\"/>\n");
                }
            }
            if (!noWays)
            {
                int way = 100;
                for (int r = 0; r < 4; ++r)
                {
                    sb.Append($"  <way id=\"{way++}\">\n");
                    for (int c = 0; c < 4; ++c) sb.Append($"    <nd ref=\"{1 + r * 4 + c}\"/>\n");
                    sb.Append("    <tag k=\"highway\" v=\"residential\"/>\n    <tag k=\"name\" v=\"Row " + r + "\"/>\n  </way>\n");
                }
                for (int c = 0; c < 4; ++c)
                {
                    sb.Append($"  <way id=\"{way++}\">\n");
                    for (int r = 0; r < 4; ++r) sb.Append($"    <nd ref=\"{1 + r * 4 + c}\"/>\n");
                    sb.Append("    <tag k=\"highway\" v=\"tertiary\"/>\n  </way>\n");
                }
            }
            if (remark != null) sb.Append("<remark> " + remark + " </remark>\n");
            sb.Append("\n</osm>\n");
            return sb.ToString();
        }

        private static readonly Vector2d AuburnLowerLeft = new Vector2d(38.89, -121.08);
        private static readonly Vector2d AuburnUpperRight = new Vector2d(38.91, -121.06);

        private static OSMDownloader.Options FastOptions(List<string> log, params string[] endpoints)
        {
            return new OSMDownloader.Options
            {
                Endpoints = endpoints,
                Rounds = 3,
                FirstBackoff = TimeSpan.FromMilliseconds(50),
                MaxBackoff = TimeSpan.FromMilliseconds(200),
                RequestTimeout = TimeSpan.FromSeconds(20),
                Log = m => { lock (log) log.Add(m); },
            };
        }

        private static string TempFolder(string prefix) => Directory.CreateTempSubdirectory(prefix).FullName;

        private static void OsmFailsOverAndRetries()
        {
            int mirrorCalls = 0;
            string query = null;
            using (var main = new StubHttpServer((r, body) => StubHttpServer.Reply.Text(504, Overpass504Page, "text/html")))
            using (var mirror = new StubHttpServer((r, body) =>
                   {
                       query = body;
                       if (Interlocked.Increment(ref mirrorCalls) == 1)
                       {
                           var busy = StubHttpServer.Reply.Text(429, "Too Many Requests");
                           busy.Headers["Retry-After"] = "0";
                           return busy;
                       }
                       return StubHttpServer.Reply.Text(200, OverpassRoads(), "application/osm3s+xml");
                   }))
            {
                string folder = TempFolder("preact-osm-");
                try
                {
                    string path = Path.Combine(folder, "downloads", "Auburn2.osm.xml");
                    var log = new List<string>();
                    OSMDownloader.Result result = OSMDownloader.DownloadAsync(AuburnLowerLeft, AuburnUpperRight, path,
                        FastOptions(log, main.BaseUrl + "/api/interpreter", mirror.BaseUrl + "/api/interpreter")).GetAwaiter().GetResult();

                    Assert.Equal(4, result.Attempts, "main 504, mirror 429, main 504 again, then the mirror's data");
                    Assert.True(result.Endpoint.StartsWith(mirror.BaseUrl), "the data came from the mirror");
                    Assert.Equal(8, result.Ways, "the eight roads");
                    Assert.Equal(16, result.Nodes, "their sixteen nodes");
                    Assert.True(File.Exists(path), "the file is written");
                    Assert.True(!File.Exists(path + ".part") && !File.Exists(path + ".tmp"), "no working copies are left");
                    Assert.True(log.Any(l => l.Contains("HTTP 504") && l.Contains("too busy to handle your request")),
                        "the 504 is reported as what Overpass said: " + string.Join(" | ", log));
                    Assert.True(log.Any(l => l.Contains("HTTP 429")), "the 429 is reported");
                    Assert.True(log.Any(l => l.Contains("trying again in")), "the pause between rounds is said");
                    Assert.True(query != null && query.StartsWith("data="), "the query is posted as Overpass's data field: " + query);

                    //What the RouterDb step reads it with: the written file has to be one Itinero can build from.
                    string routerDb = Path.Combine(folder, "Auburn2.routerdb");
                    PopulationTools.CreateAndSaveRouterDb(path, routerDb, out bool ok);
                    Assert.True(ok && File.Exists(routerDb) && new FileInfo(routerDb).Length > 0, "Itinero builds a RouterDb from it");
                }
                finally
                {
                    try { Directory.Delete(folder, true); } catch { }
                }
            }
        }

        private static void OsmFailureWritesNothing()
        {
            using (var main = new StubHttpServer((r, b) => StubHttpServer.Reply.Text(504, Overpass504Page, "text/html")))
            using (var mirror = new StubHttpServer((r, b) => StubHttpServer.Reply.Text(503, "Service Unavailable")))
            {
                string folder = TempFolder("preact-osm-");
                try
                {
                    //No earlier file: none appears.
                    string path = Path.Combine(folder, "Auburn2.osm.xml");
                    var log = new List<string>();
                    Exception failed = null;
                    try
                    {
                        OSMDownloader.DownloadAsync(AuburnLowerLeft, AuburnUpperRight, path,
                            FastOptions(log, main.BaseUrl + "/api/interpreter", mirror.BaseUrl + "/api/interpreter")).GetAwaiter().GetResult();
                    }
                    catch (Exception e) { failed = e; }

                    Assert.True(failed != null, "a download that got nothing fails");
                    Assert.True(failed.Message.Contains("after 6 attempts") && failed.Message.Contains("HTTP 504")
                                && failed.Message.Contains("HTTP 503"), "it says what each server said: " + failed.Message);
                    Assert.True(Directory.GetFiles(folder).Length == 0, "nothing is written: " + string.Join(", ", Directory.GetFiles(folder)));
                    Assert.Equal(6, main.Requests.Count + mirror.Requests.Count, "three rounds over both servers");

                    //An earlier, good file is left exactly as it was.
                    File.WriteAllText(path, "earlier download");
                    failed = null;
                    try
                    {
                        OSMDownloader.DownloadAsync(AuburnLowerLeft, AuburnUpperRight, path,
                            FastOptions(log, main.BaseUrl + "/api/interpreter")).GetAwaiter().GetResult();
                    }
                    catch (Exception e) { failed = e; }
                    Assert.True(failed != null && File.ReadAllText(path) == "earlier download", "an earlier file is untouched");

                    //A transfer cut short is not data either.
                    using (var cut = new StubHttpServer((r, b) =>
                           {
                               var reply = StubHttpServer.Reply.Text(200, OverpassRoads(), "application/osm3s+xml");
                               reply.CutAfter = reply.Body.Length / 2;
                               return reply;
                           }))
                    {
                        File.Delete(path);
                        failed = null;
                        try
                        {
                            var options = FastOptions(log, cut.BaseUrl + "/api/interpreter");
                            options.Rounds = 2;
                            OSMDownloader.DownloadAsync(AuburnLowerLeft, AuburnUpperRight, path, options).GetAwaiter().GetResult();
                        }
                        catch (Exception e) { failed = e; }
                        Assert.True(failed != null && !File.Exists(path), "half an answer is not kept: " + failed?.Message);
                    }

                    //A stop ends the waiting between rounds at once.
                    using (var stop = new CancellationTokenSource())
                    {
                        var options = FastOptions(log, main.BaseUrl + "/api/interpreter");
                        options.FirstBackoff = TimeSpan.FromMinutes(5);
                        options.MaxBackoff = TimeSpan.FromMinutes(5);
                        options.Cancellation = stop.Token;
                        stop.CancelAfter(TimeSpan.FromSeconds(1));
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        failed = null;
                        try { OSMDownloader.DownloadAsync(AuburnLowerLeft, AuburnUpperRight, path, options).GetAwaiter().GetResult(); }
                        catch (Exception e) { failed = e; }
                        Assert.True(failed is OperationCanceledException && watch.Elapsed < TimeSpan.FromSeconds(30),
                            $"stopped, not left to wait 5 minutes ({failed?.GetType().Name}, {watch.Elapsed.TotalSeconds:F1} s)");
                    }
                }
                finally
                {
                    try { Directory.Delete(folder, true); } catch { }
                }
            }
        }

        private static void OsmPartialAnswers()
        {
            const string timedOut = "runtime error: Query timed out in \"query\" at line 1 after 301 seconds.";
            using (var main = new StubHttpServer((r, b) => StubHttpServer.Reply.Text(200, OverpassRoads(timedOut), "application/osm3s+xml")))
            using (var mirror = new StubHttpServer((r, b) => StubHttpServer.Reply.Text(200, OverpassRoads(), "application/osm3s+xml")))
            using (var empty = new StubHttpServer((r, b) => StubHttpServer.Reply.Text(200, OverpassRoads(noWays: true), "application/osm3s+xml")))
            {
                string folder = TempFolder("preact-osm-");
                try
                {
                    string path = Path.Combine(folder, "a.osm.xml");
                    var log = new List<string>();
                    OSMDownloader.Result result = OSMDownloader.DownloadAsync(AuburnLowerLeft, AuburnUpperRight, path,
                        FastOptions(log, main.BaseUrl + "/api/interpreter", mirror.BaseUrl + "/api/interpreter")).GetAwaiter().GetResult();
                    Assert.True(result.Endpoint.StartsWith(mirror.BaseUrl) && result.Attempts == 2, "the timed-out answer was not taken");
                    Assert.True(log.Any(l => l.Contains("Overpass stopped the query") && l.Contains("timed out")), "and it says why");

                    string none = Path.Combine(folder, "b.osm.xml");
                    int mirrorBefore = mirror.Requests.Count;
                    Exception failed = null;
                    try
                    {
                        OSMDownloader.DownloadAsync(AuburnLowerLeft, AuburnUpperRight, none,
                            FastOptions(log, empty.BaseUrl + "/api/interpreter", mirror.BaseUrl + "/api/interpreter")).GetAwaiter().GetResult();
                    }
                    catch (Exception e) { failed = e; }
                    Assert.True(failed != null && failed.Message.Contains("no roads in the area") && !File.Exists(none),
                        "no roads is a failure: " + failed?.Message);
                    Assert.True(empty.Requests.Count == 1 && mirror.Requests.Count == mirrorBefore,
                        "and no other server is asked: the same box has no roads anywhere");
                }
                finally
                {
                    try { Directory.Delete(folder, true); } catch { }
                }
            }
        }

        private static void OsmMessagesAndQuery()
        {
            Assert.Equal(" - runtime error: open64: 0 Success /osm3s_osm_base Dispatcher_Client::request_read_and_idx::timeout. "
                         + "The server is probably too busy to handle your request.", OSMDownloader.Explain(Overpass504Page), "the 504 page");
            Assert.Equal(" - Too Many Requests", OSMDownloader.Explain("<html><body><h1>Too Many Requests</h1></body></html>"), "a plain page");
            Assert.Equal(string.Empty, OSMDownloader.Explain(""), "an empty body");

            Assert.True(OSMDownloader.IsRetryable(System.Net.HttpStatusCode.GatewayTimeout)
                        && OSMDownloader.IsRetryable((System.Net.HttpStatusCode)429)
                        && !OSMDownloader.IsRetryable(System.Net.HttpStatusCode.BadRequest), "504 and 429 are retried, 400 is not");

            //Comma-decimal locales must not split the numbers.
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("el-GR");
                string q = OSMDownloader.BuildQuery(new Vector2d(38.81892, -121.19283), new Vector2d(38.98193, -120.94696), 300);
                Assert.Equal("[out:xml][timeout:300];way[\"highway\"](38.81892,-121.19283,38.98193,-120.94696);(._;>;);out body;", q, "the query");
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = culture;
            }

            var options = new OSMDownloader.Options { FirstBackoff = TimeSpan.FromSeconds(15), MaxBackoff = TimeSpan.FromMinutes(2) };
            Assert.Equal(15.0, OSMDownloader.Backoff(options, 1, null).TotalSeconds, "first pause");
            Assert.Equal(30.0, OSMDownloader.Backoff(options, 2, null).TotalSeconds, "doubled");
            Assert.Equal(90.0, OSMDownloader.Backoff(options, 1, TimeSpan.FromSeconds(90)).TotalSeconds, "a longer Retry-After wins");
            Assert.Equal(120.0, OSMDownloader.Backoff(options, 5, null).TotalSeconds, "capped");

            string saved = Environment.GetEnvironmentVariable(OSMDownloader.EndpointsVariable);
            try
            {
                Environment.SetEnvironmentVariable(OSMDownloader.EndpointsVariable, " https://a.example/api/interpreter ;https://b.example/api/interpreter;");
                IList<string> urls = OSMDownloader.ResolveEndpoints(null);
                Assert.True(urls.Count == 2 && urls[0] == "https://a.example/api/interpreter", "the environment names the servers");
                Environment.SetEnvironmentVariable(OSMDownloader.EndpointsVariable, null);
                Assert.True(OSMDownloader.ResolveEndpoints(null).Count >= 2, "two public servers by default");
            }
            finally
            {
                Environment.SetEnvironmentVariable(OSMDownloader.EndpointsVariable, saved);
            }
        }
    }
}
