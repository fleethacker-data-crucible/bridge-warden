// Outbound HTTP for the LLM wrapper (part of warden/llm.py; the rest is Llm.cs).
// The only file in the host allowed System.Net / WebRequest (host\Build.ps1): it sends exactly what Llm.cs gives it
// to the configured OpenAI-compatible /v1 server and hands the body back line by line. Endpoint policy (loopback,
// https, keys) lives in Llm.cs; this file only moves bytes.
//
// One keep-alive connection pool for every model call (no new connection + TLS handshake per request), like the
// shared httpx.Client in Python. HttpWebRequest is thread-safe per request.
using System;
using System.IO;
using System.Net;
using System.Text;

namespace BridgeWarden
{
    public static class LlmClient
    {
        static LlmClient()
        {
            // httpx.Limits(max_keepalive_connections=8)
            if (ServicePointManager.DefaultConnectionLimit < 8) ServicePointManager.DefaultConnectionLimit = 8;
            ServicePointManager.Expect100Continue = false;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }
            catch (NotSupportedException) { }
        }

        // An open response. Read the body line by line as it arrives (ReadLine) or whole (ReadToEnd). Dispose closes it.
        public sealed class Response : IDisposable
        {
            public readonly int status;
            public readonly string content_type;
            readonly HttpWebResponse _resp;
            readonly StreamReader _reader;

            internal Response(HttpWebResponse r)
            {
                _resp = r;
                status = (int)r.StatusCode;
                content_type = r.ContentType ?? "";
                _reader = new StreamReader(r.GetResponseStream(), new UTF8Encoding(false), false, 4096);
            }

            public string ReadLine() { return _reader.ReadLine(); }
            public string ReadToEnd() { return _reader.ReadToEnd(); }

            public void Dispose()
            {
                try { _reader.Dispose(); } catch (Exception) { }
                try { _resp.Close(); } catch (Exception) { }
            }
        }

        static HttpWebRequest Make(string url, string method, string bearer, double timeoutS, string accept)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.KeepAlive = true;
            req.AllowAutoRedirect = false;          // httpx doesn't follow redirects by default
            int ms = (int)Math.Max(50, Math.Min(int.MaxValue, timeoutS * 1000));
            req.Timeout = ms;                       // connect + headers
            req.ReadWriteTimeout = ms;              // each read of the body
            if (accept != null) req.Accept = accept;
            if (bearer != null) req.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearer;
            if (req.RequestUri.IsLoopback) req.Proxy = null;   // a local model never goes through a proxy
            return req;
        }

        // HTTP errors come back as a Response with status >= 400, not as exceptions; network failures throw.
        static Response Send(HttpWebRequest req)
        {
            try { return new Response((HttpWebResponse)req.GetResponse()); }
            catch (WebException e)
            {
                HttpWebResponse r = e.Response as HttpWebResponse;
                if (r != null) return new Response(r);
                throw;
            }
        }

        // POST <url> with a JSON body (chat completions, stream: true). bearer null = no Authorization header.
        public static Response PostJson(string url, string json, string bearer, double timeoutS, string accept = "text/event-stream")
        {
            HttpWebRequest req = Make(url, "POST", bearer, timeoutS, accept);
            byte[] body = new UTF8Encoding(false).GetBytes(json);
            req.ContentType = "application/json";
            req.ContentLength = body.Length;
            using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            return Send(req);
        }

        // GET <url> (e.g. <base_url>/models). bearer null = no Authorization header.
        public static Response Get(string url, string bearer, double timeoutS)
        {
            return Send(Make(url, "GET", bearer, timeoutS, "application/json"));
        }
    }
}
