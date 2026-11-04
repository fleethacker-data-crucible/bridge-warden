// Bridge Warden web server: HTTP and WebSocket plumbing around App.cs (the routes and tick loop, port of main.py).
// C# 5, framework assemblies only; compiled at start by host\Start-BridgeWarden.ps1. Pattern: phoenix-lai WebServer.cs.
//
// Loopback only: HttpListener prefixes http://127.0.0.1:<port>/ and http://localhost:<port>/, never + or *.
// HTTP.sys does not check Host or Origin, so every request is checked here (blocks DNS rebinding and other sites).
// Serves frontend\ (never outside it), /__fleethacker/id and /__fleethacker/shutdown (edge-ops PORTS.md), the /api/
// routes in App.cs, the /ws live state feed, and the streamed explanation as Server-Sent Events.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;

namespace BridgeWarden
{
    public sealed class WebServer : IDisposable
    {
        public const string Tool = "bridge-warden";

        readonly HttpListener _listener = new HttpListener();
        readonly string _webRoot;
        readonly string _version;
        readonly App _app;
        readonly Thread _loop;
        readonly List<Socket> _sockets = new List<Socket>();

        public readonly int Port;
        public volatile bool ShutdownRequested;
        // The host's log file. Written from worker threads, so it is plain C#: a PowerShell scriptblock would have
        // no runspace on these threads.
        public string LogFile;
        readonly object _logLock = new object();

        sealed class Socket
        {
            public WebSocket Ws;
            public readonly object SendLock = new object();
        }

        public WebServer(string webRoot, App app, string version, int port)
        {
            _webRoot = Path.GetFullPath(webRoot);
            _app = app;
            _version = string.IsNullOrEmpty(version) ? "0.0" : version;
            Port = port;
            _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            _listener.Prefixes.Add("http://localhost:" + port + "/");
            _listener.Start();
            _app.Broadcast = Broadcast;
            _loop = new Thread(Loop);
            _loop.IsBackground = true;
            _loop.Name = "bw-http";
            _loop.Start();
        }

        public int OpenSockets { get { lock (_sockets) return _sockets.Count; } }

        public void Log(string text)
        {
            if (LogFile == null) return;
            try { lock (_logLock) File.AppendAllText(LogFile, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [http] " + text + "\r\n"); }
            catch { }
        }

        public void Dispose()
        {
            ShutdownRequested = true;
            lock (_sockets)
                foreach (Socket s in _sockets)
                    try { s.Ws.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "server stopping", CancellationToken.None).Wait(1000); } catch { }
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        void Loop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
        }

        // ------------------------------------------------------------------ request guard
        bool HostOk(string host) { return host == "127.0.0.1:" + Port || host == "localhost:" + Port; }
        bool OriginOk(string origin) { return origin == null || origin == "http://127.0.0.1:" + Port || origin == "http://localhost:" + Port; }

        void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            try
            {
                if (!req.IsLocal || !HostOk(req.Headers["Host"] ?? "") || !OriginOk(req.Headers["Origin"]))
                { Send(res, 403, "text/plain; charset=utf-8", "Forbidden"); return; }
                string path = req.Url.AbsolutePath;
                if (path.StartsWith("/__fleethacker/")) { FleetHacker(req, res, path); return; }
                if (path == "/ws") { SocketSession(ctx); return; }
                if (path.StartsWith("/api/")) { Api(req, res, path); return; }
                if (req.HttpMethod == "GET" || req.HttpMethod == "HEAD") { ServeFile(req, res, path); return; }
                Send(res, 405, "text/plain; charset=utf-8", "Method not allowed");
            }
            catch (Exception ex)
            {
                Log("request " + req.HttpMethod + " " + req.Url.AbsolutePath + " failed: " + ex.Message);
                try { Send(res, 500, "text/plain; charset=utf-8", "Server error"); } catch { }
            }
        }

        // ------------------------------------------------------------------ FleetHacker self-detection
        // GET /__fleethacker/id -> {"tool","version","pid"}; POST /__fleethacker/shutdown with X-FleetHacker-Shutdown: 1.
        // No "path" is reported, so a launcher never force-stops us: shutdown is the only way, and it is graceful.
        void FleetHacker(HttpListenerRequest req, HttpListenerResponse res, string path)
        {
            if (path == "/__fleethacker/id")
            {
                if (req.HttpMethod != "GET") { Send(res, 405, "text/plain; charset=utf-8", "Method not allowed"); return; }
                int pid; using (Process me = Process.GetCurrentProcess()) pid = me.Id;
                SendJson(res, 200, "{\"tool\":" + Json.Quote(Tool) + ",\"version\":" + Json.Quote(_version) + ",\"pid\":" + pid + "}");
                return;
            }
            if (path == "/__fleethacker/shutdown")
            {
                if (req.HttpMethod != "POST" || req.Headers["X-FleetHacker-Shutdown"] != "1")
                { Send(res, 405, "text/plain; charset=utf-8", "Method not allowed"); return; }
                Log("shutdown requested by a FleetHacker launcher");
                ShutdownRequested = true;
                SendJson(res, 202, "{\"stopping\":true}");
                return;
            }
            Send(res, 404, "text/plain; charset=utf-8", "Not found");
        }

        // ------------------------------------------------------------------ API
        void Api(HttpListenerRequest req, HttpListenerResponse res, string path)
        {
            // The streamed explanation is SSE, not one JSON reply.
            string[] seg = path.Trim('/').Split('/');
            if (req.HttpMethod == "GET" && seg.Length == 5 && seg[1] == "intents" && seg[3] == "explain" && seg[4] == "stream")
            { ExplainStream(res, seg[2]); return; }
            if (req.HttpMethod == "POST" && path == "/api/chat/stream")
            { ChatStream(req, res); return; }

            Dictionary<string, string> query = new Dictionary<string, string>();
            foreach (string k in req.QueryString.AllKeys) if (k != null) query[k] = req.QueryString[k];
            Dictionary<string, object> body = new Dictionary<string, object>();
            if (req.HasEntityBody)
            {
                string text;
                using (StreamReader r = new StreamReader(req.InputStream, Encoding.UTF8)) text = r.ReadToEnd();
                if (text.Trim().Length > 0)
                {
                    try { body = Json.ParseObject(text); }
                    catch (FormatException ex) { SendJson(res, 422, Json.Write(Py.D("detail", ex.Message))); return; }
                }
            }
            try { SendJson(res, 200, _app.ApiJson(req.HttpMethod, path, query, body)); }
            catch (HttpError ex) { SendJson(res, ex.Status, Json.Write(Py.D("detail", ex.Message))); }
            catch (KeyNotFoundException ex) { SendJson(res, 404, Json.Write(Py.D("detail", ex.Message))); }
            catch (Exception ex)
            {
                Log("API " + req.HttpMethod + " " + path + " error: " + ex);
                SendJson(res, 500, Json.Write(Py.D("detail", ex.Message)));
            }
        }

        // Operator chat (Chat.cs) as Server-Sent Events: thinking - content (text chunks) - done (final text, proposed actions).
        // The ship snapshot is taken under the gate; the model call runs outside it, so the tick loop never waits on AI.
        void ChatStream(HttpListenerRequest req, HttpListenerResponse res)
        {
            Dictionary<string, object> body;
            try
            {
                string text;
                using (StreamReader r = new StreamReader(req.InputStream, Encoding.UTF8)) text = r.ReadToEnd();
                body = Json.ParseObject(text.Trim().Length > 0 ? text : "{}");
            }
            catch (FormatException ex) { SendJson(res, 422, Json.Write(Py.D("detail", ex.Message))); return; }
            List<object> history = Chat.History(Py.Get(body, "messages"));
            if (history.Count == 0) { SendJson(res, 422, Json.Write(Py.D("detail", "messages: at least one user message"))); return; }
            string context = _app.ChatContext();
            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.Headers["Cache-Control"] = "no-cache";
            res.Headers["X-Accel-Buffering"] = "no";
            res.SendChunked = true;
            Stream o = res.OutputStream;
            Action<string, object> send = delegate(string ev, object data)
            {
                byte[] b = Encoding.UTF8.GetBytes("event: " + ev + "\ndata: " + Json.Write(data) + "\n\n");
                lock (o) { o.Write(b, 0, b.Length); o.Flush(); }
            };
            try
            {
                send("thinking", null);
                LLMResult r = Llm.Chat(Chat.Messages(context, history, _app.ChatAgentIds()), "operator_chat", Chat.TIMEOUT_S, Chat.MAX_TOKENS,
                    delegate(string kind, string chunk) { if (kind == "content") { try { send("content", chunk); } catch { } } });
                Dictionary<string, string> agents = _app.ChatAgentIds();
                string question = Py.Str(((Dictionary<string, object>)history[history.Count - 1])["content"]);
                Chat.Reply reply = Chat.Parse(r.ok ? r.text : "", agents);
                if (r.ok) Chat.ApplyDirect(reply, question, agents);
                _app.ChatLogged(question, reply, r);
                send("done", Py.D("ok", r.ok, "error", r.error, "text", reply.text, "actions", reply.Proposals(),
                    "model", r.model, "latency_ms", r.latency_ms));
            }
            catch (Exception ex) { Log("chat stream: " + ex.Message); }   // the browser closed the panel
            finally { try { o.Close(); } catch { } }
        }

        // The same explanation as /explain, sent as Server-Sent Events while the model writes it.
        // Events: thinking - thought (text) - content (text) - replace (whole text) - done (source, timings).
        // Chunked, flushed after every event, never compressed, so nothing buffers it.
        void ExplainStream(HttpListenerResponse res, string iid)
        {
            Dictionary<string, object> inputs;
            try { inputs = _app.ExplainInputs(iid); }
            catch (HttpError ex) { SendJson(res, ex.Status, Json.Write(Py.D("detail", ex.Message))); return; }
            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.Headers["Cache-Control"] = "no-cache";
            res.Headers["X-Accel-Buffering"] = "no";
            res.SendChunked = true;
            Stream o = res.OutputStream;
            double started = Py.Perf();
            object flushedMs = null;
            try
            {
                foreach (KeyValuePair<string, object> ev in Llm.Stream("explain_intent", inputs))
                {
                    object data;
                    if (ev.Key == "done")
                    {
                        LLMResult r = (LLMResult)ev.Value;
                        data = Py.D("source", r.source, "provider", r.provider, "model", r.model, "latency_ms", r.latency_ms,
                            "prompt_chars", r.prompt_chars, "headers_ms", r.headers_ms, "first_token_ms", r.first_token_ms,
                            "ok", r.ok, "error", r.error, "first_flush_ms", flushedMs);
                        _app.Emit("info", "llm", "Explain " + iid + ": " + r.source + " - prompt " + r.prompt_chars + " chars - headers " +
                            Py.Str(r.headers_ms) + " ms - first token " + Py.Str(r.first_token_ms) + " ms - first sent to browser " +
                            (flushedMs == null ? "None" : Py.Str(flushedMs)) + " ms - total " + Py.Str(r.latency_ms) + " ms");
                    }
                    else
                    {
                        data = ev.Value;
                        if ((ev.Key == "content" || ev.Key == "replace" || ev.Key == "thought") && flushedMs == null)
                            flushedMs = Py.Round((Py.Perf() - started) * 1000, 1);
                    }
                    byte[] b = Encoding.UTF8.GetBytes("event: " + ev.Key + "\ndata: " + Json.Write(data) + "\n\n");
                    o.Write(b, 0, b.Length);
                    o.Flush();
                }
            }
            catch (Exception ex) { Log("explain stream " + iid + ": " + ex.Message); }   // the browser closed the panel
            finally { try { o.Close(); } catch { } }
        }

        // ------------------------------------------------------------------ WebSocket: the live state feed
        // On connect: the state plus the whole log. Then App's tick loop broadcasts {"type":"state",...} every tick.
        void SocketSession(HttpListenerContext ctx)
        {
            if (!ctx.Request.IsWebSocketRequest) { Send(ctx.Response, 400, "text/plain; charset=utf-8", "WebSocket only"); return; }
            Socket s = new Socket();
            s.Ws = ctx.AcceptWebSocketAsync(null).Result.WebSocket;
            try
            {
                SendText(s, _app.HelloMessage());
                lock (_sockets) _sockets.Add(s);
                byte[] buf = new byte[4096];
                // The UI never sends anything that matters; reading only notices the close.
                while (s.Ws.State == WebSocketState.Open && !ShutdownRequested)
                {
                    WebSocketReceiveResult r = s.Ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
            }
            catch { }   // the browser closed the tab or reloaded
            finally
            {
                lock (_sockets) _sockets.Remove(s);
                try { s.Ws.Dispose(); } catch { }
            }
        }

        void Broadcast(string message)
        {
            Socket[] all;
            lock (_sockets) all = _sockets.ToArray();
            foreach (Socket s in all)
            {
                try { SendText(s, message); }
                catch { lock (_sockets) _sockets.Remove(s); }
            }
        }

        static void SendText(Socket s, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            lock (s.SendLock)
                s.Ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(5000);
        }

        // ------------------------------------------------------------------ static files from frontend\
        static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" }, { ".json", "application/json; charset=utf-8" },
            { ".svg", "image/svg+xml" }, { ".png", "image/png" }, { ".ico", "image/x-icon" },
            { ".woff2", "font/woff2" }, { ".woff", "font/woff" }, { ".txt", "text/plain; charset=utf-8" },
        };

        void ServeFile(HttpListenerRequest req, HttpListenerResponse res, string path)
        {
            if (path == "/") path = "/index.html";
            string full;
            try { full = Path.GetFullPath(Path.Combine(_webRoot, Uri.UnescapeDataString(path).TrimStart('/').Replace('/', '\\'))); }
            catch { Send(res, 400, "text/plain; charset=utf-8", "Bad path"); return; }
            // Never outside frontend\ (blocks ..\ and encoded variants).
            if (!full.StartsWith(_webRoot + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            { Send(res, 404, "text/plain; charset=utf-8", "Not found"); return; }
            string type;
            if (!Types.TryGetValue(Path.GetExtension(full), out type)) type = "application/octet-stream";
            byte[] body = File.ReadAllBytes(full);
            res.StatusCode = 200;
            res.ContentType = type;
            res.Headers["Cache-Control"] = "no-store";     // UI files are never cached, so an updated UI shows on reload
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.ContentLength64 = body.Length;
            if (req.HttpMethod != "HEAD") res.OutputStream.Write(body, 0, body.Length);
            res.OutputStream.Close();
        }

        // ------------------------------------------------------------------ responses
        static void SendJson(HttpListenerResponse res, int status, string json) { Send(res, status, "application/json; charset=utf-8", json); }

        static void Send(HttpListenerResponse res, int status, string type, string text)
        {
            byte[] body = Encoding.UTF8.GetBytes(text);
            res.StatusCode = status;
            res.ContentType = type;
            res.Headers["Cache-Control"] = "no-cache";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.ContentLength64 = body.Length;
            res.OutputStream.Write(body, 0, body.Length);
            res.OutputStream.Close();
        }
    }
}
