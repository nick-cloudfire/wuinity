using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PREACT.Tests
{
    /// <summary>
    /// A local HTTP server that answers with scripted responses, for the downloaders: Overpass, LFPS and most data
    /// hosts cannot be reached from a test machine, and what matters is how a downloader treats a 504 page, a 429 or a
    /// job that fails - which the real services produce only when they feel like it.
    /// </summary>
    internal sealed class StubHttpServer : IDisposable
    {
        internal sealed class Reply
        {
            public int Status = 200;
            public string ContentType = "text/plain";
            public byte[] Body = Array.Empty<byte>();
            public Dictionary<string, string> Headers = new Dictionary<string, string>();
            /// <summary>Close the connection after this many body bytes, to imitate a transfer cut short.</summary>
            public int CutAfter = -1;

            public static Reply Text(int status, string body, string type = "text/plain")
                => new Reply { Status = status, Body = Encoding.UTF8.GetBytes(body), ContentType = type };

            public static Reply Bytes(byte[] body, string type) => new Reply { Body = body, ContentType = type };
        }

        private readonly HttpListener _listener = new HttpListener();
        private readonly Func<HttpListenerRequest, string, Reply> _handler;
        private readonly Thread _thread;
        private readonly List<string> _requests = new List<string>();

        public string BaseUrl { get; }

        /// <summary>Every request so far, as "METHOD /path?query".</summary>
        public List<string> Requests { get { lock (_requests) return new List<string>(_requests); } }

        /// <param name="handler">The request and its body (form posts) to the reply.</param>
        public StubHttpServer(Func<HttpListenerRequest, string, Reply> handler)
        {
            _handler = handler;
            int port = FreePort();
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _thread = new Thread(Serve) { IsBackground = true, Name = "stub-http" };
            _thread.Start();
        }

        private static int FreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private void Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch { return; }

                try
                {
                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }
                    lock (_requests) _requests.Add(context.Request.HttpMethod + " " + context.Request.Url.PathAndQuery);

                    Reply reply = _handler(context.Request, body) ?? Reply.Text(404, "no such stub");
                    context.Response.StatusCode = reply.Status;
                    context.Response.ContentType = reply.ContentType;
                    foreach (KeyValuePair<string, string> h in reply.Headers) context.Response.AddHeader(h.Key, h.Value);

                    if (reply.CutAfter >= 0 && reply.CutAfter < reply.Body.Length)
                    {
                        context.Response.ContentLength64 = reply.Body.Length;
                        context.Response.OutputStream.Write(reply.Body, 0, reply.CutAfter);
                        context.Response.OutputStream.Flush();
                        context.Response.Abort();
                        continue;
                    }

                    context.Response.ContentLength64 = reply.Body.Length;
                    context.Response.OutputStream.Write(reply.Body, 0, reply.Body.Length);
                    context.Response.OutputStream.Close();
                }
                catch
                {
                    try { context.Response.Abort(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }
}
