// Port of warden/llm.py: the only module that talks to AI models. Implements docs/llm-wrapper.md.
//
//     LLMResult result = Llm.Run("summarize_conflict", inputs);
//
// Modes (LLM_MODE): live -> provider, cache, fallback - cached -> cache, fallback (default)
//                   local -> local model, cache, fallback - off -> fallback only
// Every step that errors or exceeds LLM_TIMEOUT_S drops to the next one, so callers
// never wait longer than the timeout and always get text back.
//
// Local calls always stream (stream: true) over one shared keep-alive client (LlmClient.cs, the only file allowed
// network code), so every call logs prompt size, time to response headers and time to first token.
// Llm.Stream() also hands the chunks to the caller as they arrive.
//
// Team standard for AI endpoints (applied here, on top of the Python behavior):
//   - Endpoint: LLM_URL, else BRIDGEWARDEN_AI_ENDPOINT_URL, else LLM_BASE_URL, else http://127.0.0.1:1234/v1
//     (local phoenix-lai, OpenAI-compatible). set_endpoint still writes LLM_BASE_URL, so it only takes effect when
//     neither override variable is set.
//   - Model: LLM_LOCAL_MODEL when set (as before). When it is blank and the endpoint is loopback, send "phoenixlai"
//     instead of asking /models; a non-loopback endpoint still discovers the first model at /models.
//   - Auth: on a loopback endpoint no Authorization header is sent when the key is blank or "local".
//   - A non-loopback endpoint must use https and have a real key (not blank, not "local"); otherwise the call is not
//     made and the step fails with a clear error, so Run() drops to cache / fallback.
//   - live mode: Python used LiteLLM (provider/model). The host has no multi-provider client, so live sends the same
//     OpenAI-compatible chat request to the configured endpoint with model LLM_MODEL (LLM_PROVIDER is recorded as
//     the provider). Both must be set, as in Python, or the step fails and Run() drops to the cache.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BridgeWarden
{
    // Python's LLMResult dataclass; Dump() = asdict().
    public sealed class LLMResult : Model
    {
        public string task;
        public string text;
        public string source;            // live - local - cache - fallback
        public string provider = "";
        public string model = "";
        public double latency_ms = 0.0;
        public bool ok = true;
        public string error = "";
        public int prompt_chars = 0;     // what we sent (system + messages); ~4 chars per token
        public double headers_ms = 0.0;  // request sent -> provider response headers
        public double first_token_ms = 0.0;  // request sent -> first content (or thought) chunk
        public string thoughts = "";     // model's thought summary, when the provider streams one

        public LLMResult() { }
        public LLMResult(string task, string text, string source, string provider = "", string model = "", double latency_ms = 0.0)
        {
            this.task = task; this.text = text; this.source = source;
            this.provider = provider ?? ""; this.model = model ?? ""; this.latency_ms = latency_ms;
        }
    }

    public static class Llm
    {
        public static string BACKEND { get { return Paths.Backend; } }
        public static string CACHE_PATH { get { return Path.Combine(Paths.Fixtures, "llm_cache.json"); } }

        // Model sent to a loopback endpoint when LLM_LOCAL_MODEL is blank (team standard: local phoenix-lai).
        public const string DEFAULT_LOCAL_MODEL = "phoenixlai";
        public const string DEFAULT_BASE_URL = "http://127.0.0.1:1234/v1";

        static Llm()
        {
            LoadDotenv();
        }

        // ---------------------------------------------------------------- environment
        // Windows can't hold an empty environment variable, so values set to "" (from .env or set_endpoint) live here,
        // keeping Python's os.environ semantics (KEY= in .env gives "", not the default).
        static readonly Dictionary<string, string> _blank = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly object _envLock = new object();

        public static string Getenv(string key, string dflt = null)
        {
            string v = System.Environment.GetEnvironmentVariable(key);
            if (v != null) return v;
            lock (_envLock) { if (_blank.ContainsKey(key)) return ""; }
            return dflt;
        }

        static void SetEnv(string key, string value)
        {
            lock (_envLock)
            {
                if (string.IsNullOrEmpty(value)) { System.Environment.SetEnvironmentVariable(key, null); _blank[key] = ""; }
                else { _blank.Remove(key); System.Environment.SetEnvironmentVariable(key, value); }
            }
        }

        static void SetDefault(string key, string value)
        {
            if (Getenv(key) == null) SetEnv(key, value);
        }

        // Read KEY=value lines from backend/.env and the repo-root .env without overriding the real environment.
        public static void LoadDotenv()
        {
            string[] paths = { Paths.EnvFile, Path.GetFullPath(Path.Combine(Paths.Backend, "..", "..", "..", ".env")) };
            foreach (string path in paths)
            {
                if (!File.Exists(path)) continue;
                foreach (string raw in SplitLines(Paths.ReadText(path)))
                {
                    string line = raw.Trim();
                    if (line.Length > 0 && !line.StartsWith("#") && line.IndexOf('=') >= 0)
                    {
                        int eq = line.IndexOf('=');
                        string k = line.Substring(0, eq), v = line.Substring(eq + 1).Trim();
                        if (!v.StartsWith("\""))
                        {
                            int c = v.IndexOf(" #");
                            if (c >= 0) v = v.Substring(0, c);
                            c = v.IndexOf("\t#");
                            if (c >= 0) v = v.Substring(0, c);
                            v = v.Trim();   // inline comment, as in .env.example
                        }
                        SetDefault(k.Trim(), v.Trim('"'));
                    }
                }
            }
        }

        static List<string> SplitLines(string text)
        {
            // str.splitlines() for the line breaks a .env can hold
            List<string> lines = new List<string>(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        public static readonly string[] REASONING_EFFORTS = { "none", "low", "medium", "high" };

        // Read on every call, so switching provider or mode is an .env change, never a code change.
        public static Dictionary<string, object> Settings()
        {
            string effort = (Getenv("LLM_REASONING_EFFORT", "") ?? "").Trim().ToLowerInvariant();
            // Any OpenAI-compatible /v1 server (phoenix-lai, LM Studio, llama.cpp, vLLM, Ollama's /v1, ...).
            // LLM_URL > BRIDGEWARDEN_AI_ENDPOINT_URL > LLM_BASE_URL > local phoenix-lai (team standard).
            string baseUrl = Getenv("LLM_URL");
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = Getenv("BRIDGEWARDEN_AI_ENDPOINT_URL");
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = Getenv("LLM_BASE_URL", DEFAULT_BASE_URL);
            return Py.D(
                "mode", Getenv("LLM_MODE", "cached"),
                "provider", Getenv("LLM_PROVIDER", ""),
                "model", Getenv("LLM_MODEL", ""),
                "local_model", Getenv("LLM_LOCAL_MODEL", ""),
                "base_url", baseUrl.TrimEnd('/'),
                "api_key", Getenv("LLM_API_KEY", "local"),
                "timeout_s", Py.Num(Getenv("LLM_TIMEOUT_S", "3")),
                "max_tokens", (int)Py.Num(Getenv("LLM_MAX_TOKENS", "400")),
                "record", Getenv("LLM_RECORD", "") == "1",
                // Thinking models (Gemini 3.x, o-series...): blank = don't send. "minimal" is rejected by Gemini, so not allowed.
                "reasoning_effort", Array.IndexOf(REASONING_EFFORTS, effort) >= 0 ? effort : "",
                "include_thoughts", Getenv("LLM_INCLUDE_THOUGHTS", "") == "1");
        }

        static string S(Dictionary<string, object> s, string k) { return Py.StrOr(Py.Get(s, k), ""); }
        static double N(Dictionary<string, object> s, string k) { return Py.Num(s[k]); }

        // ---------------------------------------------------------------- endpoint policy (team standard)
        public static bool IsLoopback(string url)
        {
            Uri u;
            return Uri.TryCreate(url, UriKind.Absolute, out u) && u.IsLoopback;
        }

        static bool KeyBlank(string key) { return key == null || key.Trim().Length == 0 || key.Trim() == "local"; }

        // Throws (so the step fails and Run() falls back) when the endpoint may not be called.
        public static void CheckEndpoint(Dictionary<string, object> s)
        {
            string url = S(s, "base_url");
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                throw new InvalidOperationException("invalid LLM endpoint URL: " + url);
            if (u.IsLoopback) return;
            if (u.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("refusing non-loopback LLM endpoint " + url + ": it must use https");
            if (KeyBlank(S(s, "api_key")))
                throw new InvalidOperationException("refusing non-loopback LLM endpoint " + url + ": LLM_API_KEY is not set");
        }

        // Bearer token to send, or null for none (loopback with a blank / "local" key).
        static string Bearer(Dictionary<string, object> s)
        {
            string key = S(s, "api_key");
            if (IsLoopback(S(s, "base_url")) && KeyBlank(key)) return null;
            return key;
        }

        // ---------------------------------------------------------------- providers
        static string CallLive(object prompt, Dictionary<string, object> s)
        {
            if (S(s, "provider").Length == 0 || S(s, "model").Length == 0)
                throw new InvalidOperationException("LLM_PROVIDER and LLM_MODEL must be set for live mode");
            return StreamChat(Py.L(Py.D("role", "system", "content", LlmTasks.SYSTEM), Py.D("role", "user", "content", prompt)),
                              s, double.NaN, S(s, "model"));
        }

        static readonly Dictionary<string, string> _discovered_model = new Dictionary<string, string>();

        // Switch the OpenAI-compatible server at runtime and save it to backend/.env (gitignored).
        // api_key null keeps the current key.
        public static void SetEndpoint(string base_url, string api_key, string model, string reasoning_effort = "",
                                       bool include_thoughts = false)
        {
            List<KeyValuePair<string, string>> updates = new List<KeyValuePair<string, string>>();
            updates.Add(new KeyValuePair<string, string>("LLM_BASE_URL", base_url ?? ""));
            updates.Add(new KeyValuePair<string, string>("LLM_LOCAL_MODEL", model ?? ""));
            updates.Add(new KeyValuePair<string, string>("LLM_REASONING_EFFORT",
                Array.IndexOf(REASONING_EFFORTS, reasoning_effort ?? "") >= 0 ? reasoning_effort : ""));
            updates.Add(new KeyValuePair<string, string>("LLM_INCLUDE_THOUGHTS", include_thoughts ? "1" : "0"));
            if (api_key != null) updates.Add(new KeyValuePair<string, string>("LLM_API_KEY", api_key));
            foreach (KeyValuePair<string, string> kv in updates) SetEnv(kv.Key, kv.Value);
            lock (_discovered_model) _discovered_model.Clear();
            string path = Paths.EnvFile;
            List<string> lines = File.Exists(path) ? SplitLines(Paths.ReadText(path)) : new List<string>();
            foreach (KeyValuePair<string, string> kv in updates)
            {
                int i = -1;
                for (int n = 0; n < lines.Count; n++)
                {
                    int eq = lines[n].IndexOf('=');
                    string k = (eq >= 0 ? lines[n].Substring(0, eq) : lines[n]).Trim();
                    if (k == kv.Key) { i = n; break; }
                }
                if (i < 0) lines.Add(kv.Key + "=" + kv.Value);
                else lines[i] = kv.Key + "=" + kv.Value;
            }
            // Python's write_text in text mode writes os.linesep on Windows.
            File.WriteAllText(path, string.Join("\r\n", lines.ToArray()) + "\r\n", new UTF8Encoding(false));
        }

        // LLM_LOCAL_MODEL; else "phoenixlai" on a loopback endpoint (team standard);
        // else the first model the OpenAI-compatible server reports at /models.
        public static string LocalModel(Dictionary<string, object> s)
        {
            if (S(s, "local_model").Length > 0) return S(s, "local_model");
            string baseUrl = S(s, "base_url");
            if (IsLoopback(baseUrl)) return DEFAULT_LOCAL_MODEL;
            lock (_discovered_model)
            {
                string m;
                if (_discovered_model.TryGetValue(baseUrl, out m)) return m;
            }
            CheckEndpoint(s);
            string body;
            int status;
            using (LlmClient.Response r = LlmClient.Get(baseUrl + "/models", Bearer(s), Math.Min(3.0, N(s, "timeout_s"))))
            {
                status = r.status;
                body = r.ReadToEnd();
            }
            if (status >= 400) throw new InvalidOperationException("HTTP " + status + " from " + baseUrl + "/models");
            List<object> models = Py.ListOrEmpty(Py.Get(Py.DictOrEmpty(Json.Parse(body)), "data", new List<object>()));
            if (models.Count == 0) throw new InvalidOperationException("No models loaded at " + baseUrl);
            string id = Py.Str(Py.Get(Py.DictOrEmpty(models[0]), "id"));
            lock (_discovered_model) _discovered_model[baseUrl] = id;
            return id;
        }

        // GET <base_url>/models (OpenAI-compatible): every model id the server lists, in its order. For the model
        // drop-down in Warden Control. Same endpoint rules as a model call: loopback, or https with a key.
        public static List<string> ListModels(string baseUrl, string apiKey)
        {
            Dictionary<string, object> s = Settings();
            s["base_url"] = (baseUrl ?? "").Trim().TrimEnd('/');
            s["api_key"] = apiKey ?? "";
            CheckEndpoint(s);
            string body;
            int status;
            using (LlmClient.Response r = LlmClient.Get(S(s, "base_url") + "/models", Bearer(s), 5.0))
            {
                status = r.status;
                body = r.ReadToEnd();
            }
            if (status >= 400) throw new InvalidOperationException("HTTP " + status + " from " + S(s, "base_url") + "/models");
            List<string> ids = new List<string>();
            foreach (object m in Py.ListOrEmpty(Py.Get(Py.DictOrEmpty(Json.Parse(body)), "data")))
            {
                string id = Py.StrOr(Py.Get(Py.DictOrEmpty(m), "id"), "");
                if (id.Length > 0 && !ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }

        // Thought text in a streamed delta: reasoning_content (LM Studio, DeepSeek, vLLM), reasoning (OpenRouter),
        // or Gemini's include_thoughts parts, which arrive as content flagged extra_content.google.thought.
        public static string ThoughtOf(Dictionary<string, object> delta)
        {
            if (Py.Truthy(Py.Get(delta, "reasoning_content")) || Py.Truthy(Py.Get(delta, "reasoning")))
                return Py.Str(Py.Truthy(Py.Get(delta, "reasoning_content")) ? Py.Get(delta, "reasoning_content") : Py.Get(delta, "reasoning"));
            object extra = Py.Get(delta, "extra_content");
            Dictionary<string, object> google = Py.DictOrEmpty(Py.Get(Py.DictOrEmpty(Py.Truthy(extra) ? extra : null), "google", new Dictionary<string, object>()));
            if (Py.Truthy(Py.Get(google, "thought")))
                return Py.Truthy(Py.Get(delta, "content")) ? Py.Str(Py.Get(delta, "content")) : "";
            return "";
        }

        static Dictionary<string, object> Timing(Dictionary<string, object> s)
        {
            lock (s)
            {
                Dictionary<string, object> t = Py.Dict(Py.Get(s, "timing"));
                if (t == null) { t = new Dictionary<string, object>(); s["timing"] = t; }
                return t;
            }
        }

        // OpenAI chat completions with stream: true. Reads the SSE body line by line as it arrives (never the whole
        // body at once), stops at data: [DONE]. Timings go in s["timing"]; chunks go to s["on_chunk"](kind, text) if set,
        // kind being "thought" or "content". temperature NaN = not sent; model null = LocalModel(s).
        static string StreamChat(List<object> messages, Dictionary<string, object> s, double temperature, string model = null)
        {
            CheckEndpoint(s);
            Dictionary<string, object> body = Py.D("model", model ?? LocalModel(s));
            if (!double.IsNaN(temperature)) body["temperature"] = temperature;
            body["max_tokens"] = (int)N(s, "max_tokens");
            body["stream"] = true;
            body["messages"] = messages;
            if (S(s, "reasoning_effort").Length > 0) body["reasoning_effort"] = S(s, "reasoning_effort");
            if (Py.Truthy(Py.Get(s, "include_thoughts")))       // Gemini's OpenAI-compatibility extension; other servers ignore it
                body["extra_body"] = Py.D("google", Py.D("thinking_config", Py.D("include_thoughts", true)));
            Dictionary<string, object> timing = Timing(s);
            int chars = 0;
            foreach (object m in messages) chars += Py.Str(Py.Get(Py.DictOrEmpty(m), "content", "")).Length;
            timing["prompt_chars"] = chars;
            Action<string, string> on_chunk = Py.Get(s, "on_chunk") as Action<string, string> ?? delegate(string kind, string text) { };
            double sent = Py.Perf();
            Func<double> ms = delegate { return Py.Round((Py.Perf() - sent) * 1000, 1); };
            StringBuilder parts = new StringBuilder(), thoughts = new StringBuilder();
            using (LlmClient.Response r = LlmClient.PostJson(S(s, "base_url") + "/chat/completions", Json.Write(body), Bearer(s), N(s, "timeout_s")))
            {
                timing["headers_ms"] = ms();
                if (r.status >= 400)
                    throw new InvalidOperationException("HTTP " + r.status + ": " + Py.Cut(r.ReadToEnd(), 200));
                if (!r.content_type.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase))
                {
                    // Server ignored stream: true and sent one JSON body; take it whole.
                    Dictionary<string, object> whole = Json.ParseObject(r.ReadToEnd());
                    Dictionary<string, object> msg = Py.Dict(Py.Dict(Py.List(whole["choices"])[0])["message"]);
                    timing["first_token_ms"] = ms();
                    string content = Py.Truthy(Py.Get(msg, "content")) ? Py.Str(msg["content"]) : "";
                    on_chunk("content", content);
                    return content.Trim();
                }
                string line;
                while ((line = r.ReadLine()) != null)
                {
                    if (!line.StartsWith("data:")) continue;
                    string data = line.Substring(5).Trim();
                    if (data == "[DONE]") break;
                    Dictionary<string, object> evt;
                    try { evt = Json.Parse(data) as Dictionary<string, object>; }
                    catch (FormatException) { continue; }
                    if (evt == null) continue;
                    List<object> choices = Py.ListOrEmpty(Py.Get(evt, "choices"));
                    Dictionary<string, object> delta = choices.Count > 0 ? Py.DictOrEmpty(Py.Get(Py.DictOrEmpty(choices[0]), "delta")) : new Dictionary<string, object>();
                    string thought = ThoughtOf(delta);
                    object c = Py.Get(delta, "content");
                    string text = thought.Length > 0 && c is string && (string)c == thought ? "" : (Py.Truthy(c) ? Py.Str(c) : "");
                    if ((thought.Length > 0 || text.Length > 0) && !timing.ContainsKey("first_token_ms"))
                        timing["first_token_ms"] = ms();
                    if (thought.Length > 0)
                    {
                        thoughts.Append(thought);
                        on_chunk("thought", thought);
                    }
                    if (text.Length > 0)
                    {
                        parts.Append(text);
                        on_chunk("content", text);
                    }
                }
            }
            s["thoughts"] = thoughts.ToString();
            return parts.ToString().Trim();
        }

        // Standard OpenAI chat completions against the endpoint (default http://127.0.0.1:1234/v1).
        static string CallLocal(object prompt, Dictionary<string, object> s)
        {
            return StreamChat(Py.L(Py.D("role", "system", "content", Py.Get(s, "system", LlmTasks.SYSTEM)),
                                   Py.D("role", "user", "content", prompt)), s, 0.2);
        }

        // Multi-turn (agent tool loop): OpenAI chat completions with a full message list (system, user, assistant, tool results).
        static string CallLocalChat(object messages, Dictionary<string, object> s)
        {
            return StreamChat(Py.List(messages), s, 0.1);
        }

        public static readonly Dictionary<string, Func<object, Dictionary<string, object>, string>> PROVIDERS =
            new Dictionary<string, Func<object, Dictionary<string, object>, string>>
            {
                { "live", CallLive }, { "local", CallLocal }, { "local_chat", CallLocalChat }
            };

        // ---------------------------------------------------------------- cache
        static Dictionary<string, object> _cache;
        static readonly object _cache_lock = new object();

        // sha256(f"{task}|{json.dumps(inputs, sort_keys=True, default=str)}")[:32], same keys as the Python backend.
        public static string Key(string task, IDictionary<string, object> inputs)
        {
            string s = task + "|" + PyJson.Dumps(inputs, true);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] d = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in d) sb.Append(b.ToString("x2"));
                return sb.ToString().Substring(0, 32);
            }
        }

        // Caller holds _cache_lock.
        static Dictionary<string, object> CacheLoad()
        {
            if (_cache == null)
                _cache = File.Exists(CACHE_PATH) ? Json.ParseObject(Paths.ReadText(CACHE_PATH)) : new Dictionary<string, object>();
            return _cache;
        }

        static string CacheGet(string key)
        {
            object hit;
            lock (_cache_lock) hit = Py.Get(CacheLoad(), key);
            return Py.Truthy(hit) ? Py.Str(Py.Dict(hit)["text"]) : null;
        }

        static void CachePut(string key, string task, string text, string provider, string model)
        {
            lock (_cache_lock)
            {
                CacheLoad()[key] = Py.D("task", task, "text", text, "provider", provider, "model", model);
                Directory.CreateDirectory(Path.GetDirectoryName(CACHE_PATH));
                File.WriteAllText(CACHE_PATH, PyJson.Dumps(_cache, true, 1).Replace("\n", "\r\n"), new UTF8Encoding(false));
            }
        }

        public static void ReloadCache()
        {
            lock (_cache_lock) _cache = null;
        }

        // ---------------------------------------------------------------- logging
        static volatile Action<Dictionary<string, object>> _logger;

        // fn receives task, source, provider, model, latency_ms, ok, error. Never prompts or keys.
        public static void SetLogger(Action<Dictionary<string, object>> fn)
        {
            _logger = fn;
        }

        // ---------------------------------------------------------------- run
        // ThreadPoolExecutor(max_workers=2): at most two model calls at once; queue time counts against the timeout.
        static readonly Semaphore _pool = new Semaphore(2, 2);

        sealed class FutureTimeout : Exception { }

        // _pool.submit(fn, ...).result(timeout): the call keeps running in the background after a timeout, as in Python.
        static string Submit(Func<string> fn, double timeoutS)
        {
            string result = null;
            Exception err = null;
            ManualResetEvent done = new ManualResetEvent(false);
            Thread th = new Thread(delegate()
            {
                _pool.WaitOne();
                try { result = fn(); }
                catch (Exception e) { err = e; }
                finally { _pool.Release(); done.Set(); }
            });
            th.IsBackground = true;
            th.Name = "llm";
            th.Start();
            if (!done.WaitOne(TimeSpan.FromSeconds(Math.Max(0.0, timeoutS)))) throw new FutureTimeout();
            done.Close();
            if (err != null) throw err;
            return result;
        }

        // Copy the streaming timings onto the result and write the log row (never the prompt, text or key).
        static LLMResult Finish(LLMResult result, Dictionary<string, object> s)
        {
            Dictionary<string, object> timing = Py.DictOrEmpty(Py.Get(s, "timing"));
            result.prompt_chars = Py.Int(Py.Get(timing, "prompt_chars", 0));
            result.headers_ms = Py.Num(Py.Get(timing, "headers_ms", 0.0));
            result.first_token_ms = Py.Num(Py.Get(timing, "first_token_ms", 0.0));
            result.thoughts = Py.StrOr(Py.Get(s, "thoughts"), "");
            Action<Dictionary<string, object>> log = _logger;
            if (log != null)
            {
                Dictionary<string, object> row = result.Dump();
                row.Remove("text");
                row.Remove("thoughts");
                log(row);
            }
            return result;
        }

        // mode overrides LLM_MODE for this call (the LLM agent always asks the local model).
        // on_chunk(kind, text) receives "thought" and "content" chunks as a local model streams them.
        public static LLMResult Run(string task, IDictionary<string, object> inputs, string mode = null,
                                    Action<string, string> on_chunk = null)
        {
            LlmTasks.Task t = LlmTasks.TASKS[task];
            Dictionary<string, object> s = Settings();
            s["on_chunk"] = on_chunk;
            if (!string.IsNullOrEmpty(mode)) s["mode"] = mode;
            if (t.timeout_s != 0) s["timeout_s"] = Math.Max(N(s, "timeout_s"), t.timeout_s);
            if (t.max_tokens != 0) s["max_tokens"] = Math.Max((int)N(s, "max_tokens"), t.max_tokens);
            if (t.system.Length > 0) s["system"] = t.system;
            Dictionary<string, object> ins = new Dictionary<string, object>();
            foreach (string k in t.fields) ins[k] = inputs != null && inputs.ContainsKey(k) ? inputs[k] : "";
            string key = Key(task, ins);
            double started = Py.Perf();
            Func<double> ms = delegate { return Py.Round((Py.Perf() - started) * 1000, 1); };
            List<string> errors = new List<string>();

            string m = S(s, "mode");
            string[] order = m == "live" ? new string[] { "live", "cache" } : m == "local" ? new string[] { "local", "cache" }
                           : m == "off" ? new string[0] : new string[] { "cache" };
            LLMResult result = null;
            foreach (string step in order)
            {
                try
                {
                    if (step == "cache")
                    {
                        string hit = CacheGet(key);
                        if (hit != null)
                        {
                            result = new LLMResult(task, hit, "cache", "", "", ms());
                            break;
                        }
                        continue;
                    }
                    double remaining = N(s, "timeout_s") - (Py.Perf() - started);
                    string prompt = LlmTasks.Fill(t.prompt, ins);
                    Func<object, Dictionary<string, object>, string> fn = PROVIDERS[step];
                    string text = Submit(delegate { return fn(prompt, s); }, Math.Max(0.05, remaining));
                    if (string.IsNullOrEmpty(text)) throw new InvalidOperationException("empty answer");
                    string provider = step == "live" ? S(s, "provider") : S(s, "base_url");
                    string model = step == "live" ? S(s, "model") : LocalModel(s);
                    result = new LLMResult(task, text, step, provider, model, ms());
                    if (Py.Truthy(s["record"])) CachePut(key, task, text, provider, model);
                    break;
                }
                catch (FutureTimeout)
                {
                    errors.Add(step + ": timeout after " + Py.Str(s["timeout_s"]) + " s");
                }
                catch (Exception e)    // any provider failure drops to the next step
                {
                    errors.Add(step + ": " + e.Message);
                }
            }
            if (result == null)
            {
                try { result = new LLMResult(task, t.fallback(ins), "fallback", "", "", ms()); }
                catch (Exception e)
                {
                    result = new LLMResult(task, "", "fallback", "", "", ms());
                    result.ok = false;
                    result.error = e.Message;
                }
            }
            if (errors.Count > 0 && string.IsNullOrEmpty(result.error))
                result.error = string.Join("; ", errors.ToArray());
            return Finish(result, s);
        }

        // Run() as events, for showing text as it arrives: ("thinking", null) at once, then ("thought", text) and
        // ("content", text) chunks, then ("done", LLMResult). Cache and fallback text arrive as one content chunk
        // ("replace").
        public static IEnumerable<KeyValuePair<string, object>> Stream(string task, IDictionary<string, object> inputs)
        {
            BlockingCollection<KeyValuePair<string, object>> q = new BlockingCollection<KeyValuePair<string, object>>();
            List<string> streamed = new List<string>();
            Action<string, string> chunk = delegate(string kind, string text)
            {
                lock (streamed) streamed.Add(kind);
                q.Add(new KeyValuePair<string, object>(kind, text));
            };
            yield return new KeyValuePair<string, object>("thinking", null);
            Thread th = new Thread(delegate()
            {
                try { q.Add(new KeyValuePair<string, object>("done", Run(task, inputs, null, chunk))); }
                catch (Exception e)     // Run() never throws, but the stream must always end
                {
                    LLMResult r = new LLMResult(task, "", "fallback");
                    r.ok = false;
                    r.error = e.Message;
                    q.Add(new KeyValuePair<string, object>("done", r));
                }
            });
            th.IsBackground = true;
            th.Start();
            while (true)
            {
                KeyValuePair<string, object> ev = q.Take();
                if (ev.Key == "done")
                {
                    LLMResult val = (LLMResult)ev.Value;
                    bool sawContent;
                    lock (streamed) sawContent = streamed.Contains("content");
                    // A provider that streamed then failed, or a cache/fallback answer: send the final text whole.
                    if ((val.source != "local" && val.source != "live") || !sawContent)
                        yield return new KeyValuePair<string, object>("replace", val.text);
                    yield return ev;
                    yield break;
                }
                yield return ev;
            }
        }

        // Instant modes run inline. Network/model modes run off the governance loop so decisions never wait on AI.
        public static void RunBackground(string task, IDictionary<string, object> inputs, Action<LLMResult> on_done)
        {
            string mode = S(Settings(), "mode");
            if (mode == "cached" || mode == "off") on_done(Run(task, inputs));
            else
            {
                Thread th = new Thread(delegate() { on_done(Run(task, inputs)); });
                th.IsBackground = true;
                th.Start();
            }
        }

        // One turn of a conversation with the local model. No cache and no fallback text: an agent turn is either a
        // real model answer or a failure, never something we made up.
        // on_chunk(kind, text), when given, receives "thought" and "content" chunks as the model streams (operator chat).
        public static LLMResult Chat(List<object> messages, string task = "agent_turn", double timeout_s = 45.0, int max_tokens = 700,
                                     Action<string, string> on_chunk = null)
        {
            Dictionary<string, object> s = Settings();
            if (on_chunk != null) s["on_chunk"] = on_chunk;
            s["timeout_s"] = Math.Max(N(s, "timeout_s"), timeout_s);
            s["max_tokens"] = Math.Max((int)N(s, "max_tokens"), max_tokens);
            double started = Py.Perf();
            LLMResult result;
            try
            {
                Func<object, Dictionary<string, object>, string> fn = PROVIDERS["local_chat"];
                string text = Submit(delegate { return fn(messages, s); }, N(s, "timeout_s"));
                result = new LLMResult(task, text, "local", S(s, "base_url"), LocalModel(s), Py.Round((Py.Perf() - started) * 1000, 1));
            }
            catch (FutureTimeout)
            {
                result = new LLMResult(task, "", "failed", S(s, "base_url"), S(s, "local_model"), Py.Round((Py.Perf() - started) * 1000, 1));
                result.ok = false;
                result.error = "timeout after " + Py.Str(s["timeout_s"]) + " s";
            }
            catch (Exception e)
            {
                result = new LLMResult(task, "", "failed", S(s, "base_url"), S(s, "local_model"), Py.Round((Py.Perf() - started) * 1000, 1));
                result.ok = false;
                result.error = Py.Cut(e.Message, 200);
            }
            return Finish(result, s);
        }
    }
}
