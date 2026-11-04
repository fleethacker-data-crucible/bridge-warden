// Bridge Warden server logic: tick loop, REST routes, presenter controls, live log (port of warden\main.py).
// WebServer.cs does the HTTP and WebSocket plumbing and calls in here. C# 5, framework assemblies only.
//   Env: PROFILE=demo|edge   TIME_SCALE=1   SCENARIO=ops_normal|firefight|full_demo   OPERATOR=OOD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;

namespace BridgeWarden
{
    // An API error with an HTTP status (FastAPI's HTTPException).
    public sealed class HttpError : Exception
    {
        public readonly int Status;
        public HttpError(int status, string message) : base(message) { Status = status; }
        public HttpError(int status) : base(status == 404 ? "Not Found" : "Bad Request") { Status = status; }
    }

    public sealed class App
    {
        // tick_s: governance cycle; ui_every: push a UI snapshot every N ticks
        static readonly Dictionary<string, double[]> Profiles = new Dictionary<string, double[]>
        {
            { "demo", new double[] { 1.0, 1 } },
            { "edge", new double[] { 1.0, 2 } },
        };
        static readonly List<string> AutoPool = new List<string> { "contact", "storm", "overheat", "hostile", "inbound", "engine-loss", "fire", "flooding", "rogue" };

        public readonly string Profile;
        public readonly string Operator;
        readonly double _tickS;
        readonly int _uiEvery;

        public readonly World world;
        int _speed = 1;              // presenter sim speed multiplier (1-3)
        double _baseScale;
        public readonly Ledger ledger;
        public readonly Warden warden;
        public Scenario scenario;
        public readonly ResourceMeter meter;

        readonly object _gate = new object();         // one thread at a time in the world / warden (tick vs. routes)
        readonly LinkedList<Dictionary<string, object>> _logs = new LinkedList<Dictionary<string, object>>();   // last 400
        readonly List<Dictionary<string, object>> _pending = new List<Dictionary<string, object>>();
        readonly object _logLock = new object();

        // state = {"started", "running", "active_cop", "auto_inject", "next_auto", "last_auto"}
        readonly double _started = Py.Now();
        public volatile bool running;
        string _activeCop = "nav";
        bool _autoInject = true;     // the demo runs on auto injection (random issue every 3-5 min) unless switched to manual
        double _nextAuto;
        string _lastAuto = "";

        Thread _tick;
        volatile bool _stop;

        // The WebSocket feed: WebServer.cs registers a sink that sends one message to every open socket.
        public Action<string> Broadcast = delegate { };

        public App()
        {
            Profile = Env("PROFILE", "demo");
            Operator = Env("OPERATOR", "OOD");
            double[] cfg;
            if (!Profiles.TryGetValue(Profile, out cfg)) cfg = Profiles["demo"];
            _tickS = cfg[0];
            _uiEvery = (int)cfg[1];
            world = new World(Py.Num(Env("TIME_SCALE", "1"), 1.0));        // real time (1x)
            ledger = new Ledger(Path.Combine(Paths.Data, "ledger.db"));
            warden = new Warden(Paths.Config, ledger, world, Emit);
            scenario = NewScenario(Env("SCENARIO", "ops_normal"));
            meter = new ResourceMeter(Py.D("cpu_cores", 1.0, "rss_mb", 500, "ui_kbps", 50, "intent_bytes", 1024, "profile", Profile));
        }

        static string Env(string name, string dflt) { string v = System.Environment.GetEnvironmentVariable(name); return string.IsNullOrEmpty(v) ? dflt : v; }

        static Scenario NewScenario(string name) { return new Scenario(Path.Combine(Paths.Scenarios, name + ".json")); }

        // ------------------------------------------------------------------ live log
        public void Emit(string level, string source, string message)
        {
            Dictionary<string, object> line = Py.D("ts", Py.Now(), "level", level, "source", source, "message", message);
            lock (_logLock)
            {
                _logs.AddLast(line);
                while (_logs.Count > 400) _logs.RemoveFirst();
                _pending.Add(line);
            }
        }

        public List<object> AllLogs() { lock (_logLock) return new List<object>(_logs); }

        List<object> TakePending()
        {
            lock (_logLock)
            {
                List<object> l = new List<object>(_pending);
                _pending.Clear();
                return l;
            }
        }

        // ------------------------------------------------------------------ state
        public Dictionary<string, object> FullState()
        {
            lock (_gate)
            {
                Dictionary<string, object> s = warden.Snapshot();
                s["cop"] = world.Cop();
                s["presenter"] = Py.D("active_cop", _activeCop, "auto_inject", _autoInject, "last_auto", _lastAuto,
                    "next_auto_s", _autoInject ? (object)(int)Py.Round(Math.Max(0, _nextAuto - Py.Now())) : null,
                    "time_scale", world.time_scale, "speed", _speed);
                s["scenario"] = Py.D("name", scenario.name, "running", scenario.running, "elapsed", Py.Round(scenario.elapsed, 1),
                    "finished", scenario.Finished());
                s["resources"] = meter.Sample();
                Dictionary<string, object> llmSettings = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in Llm.Settings())
                {
                    if (Array.IndexOf(new[] { "mode", "provider", "model", "local_model", "base_url", "endpoint", "reasoning_effort", "include_thoughts" }, kv.Key) >= 0)
                        llmSettings[kv.Key] = kv.Value;
                    else if (kv.Key == "api_key")      // only whether one is saved; the key itself never leaves the host
                    {
                        string k = Py.StrOr(kv.Value, "").Trim();
                        llmSettings["key_set"] = k.Length > 0 && k != "local";
                    }
                }
                lock (_warmLock) llmSettings["warm"] = new Dictionary<string, object>(_warm);
                Dictionary<string, object> agentsLlm = new Dictionary<string, object>(LlmAgent.SCHEDULER.stats);
                agentsLlm["queue"] = LlmAgent.SCHEDULER.Depth();
                s["server"] = Py.D("uptime_s", (int)Py.Round(Py.Now() - _started), "operator", Operator, "profile", Profile,
                    "running", running, "llm", llmSettings, "agents_llm", agentsLlm);
                return s;
            }
        }

        // State holds live world/warden collections, so it is serialized inside the gate (a route could change them mid-write).
        string StateMessage(List<object> logs)
        {
            string data;
            lock (_gate) data = Json.Write(Py.D("type", "state", "data", FullState(), "logs", logs));
            meter.CountWs(data.Length);
            return data;
        }

        // The first message a new socket gets: the state plus the whole log so far.
        public string HelloMessage() { lock (_gate) return Json.Write(Py.D("type", "state", "data", FullState(), "logs", AllLogs())); }

        // ------------------------------------------------------------------ lifecycle
        public void Start()
        {
            ledger.Reset();
            world.Normalize();
            Emit("milestone", "system", "WARDEN CORE STARTED - profile " + Profile + " - " + warden.agents.Count + " agents registered");
            Emit("system", "system", "Normal conditions - simulation PAUSED - press Start Operations");
            ledger.Append("SYSTEM", "warden", Py.D("event", "start", "profile", Profile));
            _tick = new Thread(TickLoop);
            _tick.IsBackground = true;
            _tick.Name = "bw-tick";
            _tick.Start();
            WarmModel("start");
        }

        // ------------------------------------------------------------------ model warm-up
        // Loads the configured model in the background (Llm.Warm) and reports it in state.server.llm.warm:
        // {state: idle|loading|ready|failed, model, ms, error, why, at}. When it loads, agents showing a model error
        // (NO MODEL, e.g. a timeout while the server was swapping models) are cleared at once, not at their next retry.
        Dictionary<string, object> _warm = Py.D("state", "idle");
        readonly object _warmLock = new object();
        int _warmGen;

        public object WarmModel(string why)
        {
            int gen;
            lock (_warmLock)
            {
                gen = ++_warmGen;                             // a newer request supersedes an older one still loading
                _warm = Py.D("state", "loading", "model", "", "ms", null, "error", "", "why", why, "at", Py.Now());
            }
            Emit("info", "llm", "Loading the AI model (" + why + ")...");
            Thread th = new Thread(delegate ()
            {
                LLMResult r = Llm.Warm();
                lock (_warmLock)
                {
                    if (gen != _warmGen) return;              // superseded: its result belongs to an older setting
                    _warm = Py.D("state", r.ok ? "ready" : "failed", "model", r.model, "ms", (long)Py.Round(r.latency_ms),
                                 "error", r.error ?? "", "why", why, "at", Py.Now());
                }
                if (r.ok)
                {
                    int cleared = 0;
                    lock (_gate)
                    {
                        foreach (LLMAgentRuntime a in warden.agents.Values)
                            lock (a.status)
                                if (Py.Get(a.status, "last_ok") is bool && !(bool)Py.Get(a.status, "last_ok"))
                                {
                                    a.status["last_ok"] = null;       // WATCHING again; its next call reports for itself
                                    a.status["last_error"] = "";
                                    cleared++;
                                }
                        Dictionary<string, object> st = LlmAgent.SCHEDULER.stats;
                        lock (st) if (Py.Get(st, "last_ok") is bool && !(bool)Py.Get(st, "last_ok")) { st["last_ok"] = null; st["last_error"] = ""; }
                    }
                    Emit("ok", "llm", "AI model loaded: " + r.model + " in " + (r.latency_ms / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s" +
                        (cleared > 0 ? " - cleared the model error on " + cleared + " agent(s)" : ""));
                }
                else Emit("warn", "llm", "AI model did not load: " + r.error);
            });
            th.IsBackground = true;
            th.Name = "bw-warm";
            th.Start();
            return Py.D("state", "loading");
        }

        public void Stop()
        {
            _stop = true;
            try { if (_tick != null) _tick.Join(3000); } catch { }
            try { ledger.Close(); } catch { }
        }

        void TickLoop()
        {
            int n = 0;
            while (!_stop)
            {
                Thread.Sleep(TimeSpan.FromSeconds(_tickS));
                try { Tick(ref n); }
                catch (Exception ex) { Emit("error", "system", "Tick failed: " + ex.Message); }
            }
        }

        void Tick(ref int n)
        {
            if (!running)          // paused: world, agents and model frozen; screens still refresh
            {
                n++;
                if (n % _uiEvery == 0) Broadcast(StateMessage(TakePending()));
                return;
            }
            lock (_gate)
            {
                foreach (Dictionary<string, object> ev in scenario.Due(_tickS))
                {
                    if (ev.ContainsKey("rogue_agent"))
                    {
                        string aid = Py.Str(Py.Dict(ev["rogue_agent"])["id"]);
                        warden.agents[aid].rogue = true;
                        Emit("error", "scenario", "Agent " + aid + " compromised (fault injection)");
                        continue;
                    }
                    string line = world.ApplyEvent(ev);
                    if (!string.IsNullOrEmpty(line)) Emit(ev.ContainsKey("phase") ? "milestone" : "engine", "scenario", line);
                }
                if (_autoInject && scenario.running && Py.Now() >= _nextAuto)
                {
                    bool hostile = false;
                    foreach (Contact c in world.contacts.Values) if (c.kind == "hostile") hostile = true;
                    List<string> pool = AutoPool.FindAll(delegate (string f) { return f != "inbound" || hostile; });
                    string fault = pool[Py.Random.Next(pool.Count)];
                    _lastAuto = fault;
                    _nextAuto = Py.Now() + 180 + Py.Random.NextDouble() * 120;     // a realistic pace: one issue every 3-5 min
                    Emit("system", "auto-inject", "Auto injection: " + fault);
                    InjectLocked(fault, "auto-inject");
                }
                // Every COP runs every tick, on screen or not: one ship, one clock, as at sea.
                HashSet<string> active = new HashSet<string> { "nav", "systems", "dc" };
                world.Step(1.0, active);
                double t0 = Py.Perf();
                warden.Step(active, _tickS);
                ContestTick();          // MORDECAI field-promotion drill: one contest round on the main engine when due
                meter.RecordPipeline((Py.Perf() - t0) * 1000);
                foreach (Intent it in warden.intents.Values)
                {
                    if (it.created_at > Py.Now() - _tickS * 1.5)
                    {
                        string j = Json.Write(Py.D("agent_id", it.agent_id, "kind", it.kind, "action", it.action, "params", it.@params,
                            "target_resource", it.target_resource, "objective", it.objective, "rationale", it.rationale, "confidence", it.confidence));
                        meter.intent_bytes_max = Math.Max(meter.intent_bytes_max, j.Length);
                    }
                }
            }
            n++;
            if (n % _uiEvery == 0) Broadcast(StateMessage(TakePending()));
        }

        // ------------------------------------------------------------------ routes
        // One entry point for every /api/ call: method, path segments, query, JSON body. Returns the object to send.
        // Api's answer as JSON, written inside the gate: results reference live state the tick keeps changing.
        public string ApiJson(string method, string path, Dictionary<string, string> query, Dictionary<string, object> body)
        {
            if (method == "POST" && path == "/api/llm/models") return Json.Write(Api(method, path, query, body));   // never holds the gate
            lock (_gate) return Json.Write(Api(method, path, query, body));
        }

        public object Api(string method, string path, Dictionary<string, string> query, Dictionary<string, object> body)
        {
            string[] p = path.Trim('/').Split('/');      // "api", ...
            int n = p.Length;
            bool get = method == "GET", post = method == "POST";
            // Outside the gate: a slow provider must never stall the tick loop.
            if (post && path == "/api/llm/models") return LlmModels(body);
            lock (_gate)
            {
                if (get && path == "/api/state") return FullState();
                if (get && path == "/api/summary") return warden.Summary();
                if (get && path == "/api/agents") return warden.Snapshot()["agents"];
                if (post && n == 4 && p[1] == "agents") return AgentAction(p[2], p[3], body);
                if (get && path == "/api/roster") return Py.D("condition", Rank.Condition(world), "roster", warden.Roster());
                if (post && n == 3 && p[1] == "drill") return Drill(p[2]);
                if (method == "PATCH" && n == 4 && p[1] == "agents" && p[3] == "autonomy")
                {
                    AgentOr404(p[2]);
                    warden.SetAutonomy(p[2], Py.Int(Required(body, "level")), Operator);
                    return Py.D("ok", true);
                }
                if (post && path == "/api/llm/endpoint") return LlmEndpoint(body);
                if (post && path == "/api/llm/warm") return WarmModel("requested by " + Operator);
                if (get && path == "/api/cop") return world.Cop();
                if (get && n == 4 && p[1] == "intents" && p[3] == "explain")
                {
                    Intent it = IntentOr404(p[2]);
                    LLMResult r = Explain.IntentStory(it);
                    return Py.D("intent", it.Dump(), "story", r.text, "source", r.source, "provider", r.provider, "model", r.model);
                }
                if (post && n == 4 && p[1] == "escalations")
                {
                    string eid = p[2], decision = p[3];
                    if (!warden.escalations.ContainsKey(eid) || Array.IndexOf(new[] { "approve", "reject", "option-b" }, decision) < 0) throw new HttpError(404);
                    return warden.DecideEscalation(eid, decision == "approve", Operator, decision == "option-b" ? "b" : "a").Dump();
                }
                if (post && path == "/api/control/override")
                    return warden.Override(Operator, Py.Str(Required(body, "action")), Py.Num(Required(body, "value"))).Dump();
                if (post && n == 4 && p[1] == "control" && p[2] == "gq")
                {
                    // General Quarters is an operator order, never an agent's.
                    world.sys.SetGq(p[3] == "on");
                    ledger.Append("CONTROL", Operator, Py.D("action", "general_quarters", "on", p[3] == "on"));
                    return Py.D("gq", world.sys.gq);
                }
                if (post && n == 4 && p[1] == "control" && p[2] == "auto-act")
                {
                    // on = agents may act without asking (Warden checks still apply); off = every governed action waits.
                    warden.SetAutoAct(p[3] == "on", Operator);
                    return Py.D("auto_act", warden.auto_act);
                }
                if (post && path == "/api/control/cease-fire") return Py.D("destructed", warden.CeaseFire(Operator), "hold_fire", true);
                if (post && path == "/api/control/hold-fire/lift") { warden.LiftHoldFire(Operator); return Py.D("hold_fire", false); }
                if (post && path == "/api/control/rollback")
                {
                    object agent = Py.Get(body, "agent_id");
                    return Py.D("undone", warden.Rollback(Operator, agent == null ? null : Py.Str(agent), Py.Int(Py.Get(body, "count"), 1)));
                }
                if (post && path == "/api/control/safe-stop") { warden.EngageSafeStop(Operator); return Py.D("ok", true); }
                if (post && path == "/api/control/resume-all") { warden.ResumeAll(Operator); return Py.D("ok", true); }
                if (get && path == "/api/ledger")
                {
                    string limit, type;
                    query.TryGetValue("limit", out limit);
                    query.TryGetValue("type", out type);
                    return ledger.Rows(limit == null ? 200 : Py.Int(limit, 200), string.IsNullOrEmpty(type) ? null : type);
                }
                if (get && path == "/api/ledger/verify")
                {
                    Dictionary<string, object> result = ledger.Verify();
                    bool valid = Py.Truthy(result["valid"]);
                    Emit(valid ? "ok" : "error", "ledger", "Chain " + (valid ? "valid" : "BROKEN at " + Py.Str(result["broken_at"])) +
                        " (" + Py.Str(result["rows"]) + " rows)");
                    return result;
                }
                if (post && n == 3 && p[1] == "view") return View(p[2]);
                if (post && path == "/api/presenter/normal") { Normal(); return Py.D("ok", true); }
                if (post && path == "/api/presenter/firefight") return Firefight();
                if (post && path == "/api/presenter/speed")
                {
                    // 1x -> 2x -> 3x -> 4x -> 5x -> 1x on the base time scale.
                    if (_baseScale <= 0) _baseScale = world.base_scale;
                    _speed = _speed % 5 + 1;
                    world.time_scale = _baseScale * _speed;
                    Emit("system", Operator, "Sim speed " + _speed + "x");
                    return Py.D("speed", _speed);
                }
                if (post && n == 4 && p[1] == "presenter" && p[2] == "auto-inject")
                {
                    // auto = random issue every 3-5 min; manual = only the presenter injects.
                    _autoInject = p[3] == "auto";
                    _nextAuto = Py.Now() + 45;
                    Emit("system", Operator, "Injection mode: " + (_autoInject ? "AUTO (random issue every 3-5 min)" : "MANUAL"));
                    return Py.D("auto_inject", _autoInject);
                }
                if (post && n == 3 && p[1] == "scenario") return ScenarioAction(p[2]);
                if (post && n == 3 && p[1] == "inject") return InjectLocked(p[2], Operator);
                if (get && path == "/api/health/resources") return meter.Sample();
            }
            throw new HttpError(404, "Not Found");
        }

        static object Required(Dictionary<string, object> body, string key)
        {
            object v = Py.Get(body, key);
            if (v == null) throw new HttpError(422, "Missing field: " + key);
            return v;
        }

        LLMAgentRuntime AgentOr404(string aid)
        {
            LLMAgentRuntime a;
            if (!warden.agents.TryGetValue(aid, out a)) throw new HttpError(404, "Unknown agent " + aid);
            return a;
        }

        Intent IntentOr404(string iid)
        {
            Intent it;
            if (!warden.intents.TryGetValue(iid, out it)) throw new HttpError(404);
            return it;
        }

        // Explain stream (WebServer.cs sends these as Server-Sent Events). Looked up under the gate; streamed outside it.
        public Dictionary<string, object> ExplainInputs(string iid)
        {
            lock (_gate) return Explain.IntentInputs(IntentOr404(iid));
        }

        // ------------------------------------------------------------------ operator chat (Chat.cs)
        // Agent id -> name, for validating and labelling the actions chat may propose.
        public Dictionary<string, string> ChatAgentIds()
        {
            lock (_gate) return warden.agents.ToDictionary(kv => kv.Key, kv => kv.Value.identity.name);
        }

        // The live picture as compact text for the model. Taken under the gate; the model call happens outside it.
        public string ChatContext()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            Func<double, string> f1 = delegate(double v) { return v.ToString("0.0", inv); };
            Func<double, string> h3 = delegate(double v) { return ((int)Math.Round((v % 360 + 360) % 360)).ToString("000", inv); };
            StringBuilder sb = new StringBuilder();
            lock (_gate)
            {
                OwnShip s = world.ship;
                ShipSystems sys = world.sys;
                sb.Append("Ship's time ").Append(f1(world.sim_time)).Append(" s into the exercise; phase ").Append(world.phase)
                  .Append("; scenario ").Append(scenario.name).Append(scenario.running ? " (running)" : " (paused)").Append('\n');
                sb.Append("Controls: SAFE-STOP ").Append(warden.safe_stop ? "ENGAGED" : "off").Append("; hold fire ").Append(warden.hold_fire ? "ON" : "off")
                  .Append("; agents may act without asking: ").Append(warden.auto_act ? "yes" : "no (every governed action waits)")
                  .Append("; General Quarters ").Append(sys.gq ? "ON" : "off").Append("; EMCON ").Append(sys.emcon ? "ON" : "off").Append('\n');
                sb.Append("Own ship: heading ").Append(h3(s.heading)).Append(" (ordered ").Append(h3(s.cmd_heading)).Append("), speed ")
                  .Append(f1(s.speed)).Append(" kn (ordered ").Append(f1(s.cmd_speed)).Append(")\n");

                sb.Append("\n## TRACKS (nearest first)\n");
                List<Contact> tracks = world.contacts.Values.Where(c => c != null && !c.hidden).OrderBy(c => c.range_nm).Take(12).ToList();
                if (tracks.Count == 0) sb.Append("none\n");
                foreach (Contact c in tracks)
                    sb.Append(c.id).Append(": ").Append(c.classification.ToLowerInvariant()).Append(' ').Append(c.domain).Append(", bears ")
                      .Append(h3(c.bearing)).Append(", ").Append(f1(c.range_nm)).Append(" nm, closest approach ").Append(f1(c.cpa_nm))
                      .Append(" nm in ").Append(f1(c.tcpa_min)).Append(" min\n");

                sb.Append("\n## AGENTS (id: name, role, status, autonomy 0-3, trust)\n");
                foreach (LLMAgentRuntime a in warden.agents.Values.OrderByDescending(x => x.identity.tier == "decider").ThenByDescending(x => x.identity.priority))
                {
                    AgentIdentity id = a.identity;
                    sb.Append(id.id).Append(": ").Append(id.name).Append(", ").Append(id.role).Append(", ").Append(id.status)
                      .Append(", autonomy ").Append(id.autonomy_level).Append(", trust ").Append(((int)Math.Round(id.trust_score)).ToString(inv));
                    if (id.tier == "decider") sb.Append(", DECIDER");
                    lock (a.status)
                    {
                        object last = Py.Get(a.status, "last_actions");
                        List<object> acts = Py.ListOrEmpty(last);
                        if (acts.Count > 0) sb.Append("; last: ").Append(Py.Cut(string.Join(" | ", acts.Select(x => Py.Str(x)).ToArray()), 160));
                        string err = Py.StrOr(Py.Get(a.status, "last_error"), "");
                        if (err.Length > 0) sb.Append("; model problem: ").Append(Py.Cut(err, 100));
                    }
                    sb.Append('\n');
                }

                List<Escalation> esc = warden.escalations.Values.Where(e => e.status == "open").ToList();
                sb.Append("\n## WAITING ON THE OPERATOR (open escalations)\n");
                if (esc.Count == 0) sb.Append("none\n");
                foreach (Escalation e in esc)
                    sb.Append(e.id).Append(" from ").Append(e.agent_id).Append(": ").Append(Py.Cut(e.title ?? "", 120)).Append(" - ")
                      .Append(Py.Cut(e.summary ?? "", 220)).Append('\n');

                List<Conflict> cf = warden.conflicts.Where(c => c.status == "open" || c.status == "escalated").ToList();
                sb.Append("\n## OPEN CONFLICTS BETWEEN AGENTS\n");
                if (cf.Count == 0) sb.Append("none\n");
                foreach (Conflict c in cf.Take(6))
                    sb.Append(c.id).Append(" (").Append(c.type).Append(", ").Append(c.severity).Append(") agents ")
                      .Append(string.Join(", ", c.agent_ids.Select(x => Py.Str(x)).ToArray())).Append(": ").Append(Py.Cut(c.summary, 200)).Append('\n');

                sb.Append("\n## RECENT ORDERS (newest first: id, agent, order, status, decided by, reason)\n");
                List<Intent> recent = warden.intents.Values.OrderByDescending(i => i.created_at).Take(15).ToList();
                if (recent.Count == 0) sb.Append("none yet\n");
                foreach (Intent i in recent)
                {
                    sb.Append(i.id).Append(", ").Append(i.agent_id).Append(", ").Append(i.action).Append(' ').Append(Py.Cut(Json.Write(i.@params), 90))
                      .Append(", ").Append(i.status);
                    if (!string.IsNullOrEmpty(i.decided_by)) sb.Append(", by ").Append(i.decided_by);
                    sb.Append(", why: ").Append(Py.Cut(i.rationale, 200));
                    if (i.reasons.Count > 0) sb.Append("; Warden: ").Append(Py.Cut(string.Join("; ", i.reasons.Select(x => Py.Str(x)).ToArray()), 200));
                    sb.Append('\n');
                }

                sb.Append("\n## DAMAGE AND PLANT\n");
                if (sys.casualties.Count == 0) sb.Append("No casualties.\n");
                foreach (Dictionary<string, object> cas in sys.casualties.Values.Take(8))
                    sb.Append(Py.Str(cas["id"])).Append(": ").Append(Py.Str(Py.Get(cas, "kind"))).Append(" in ").Append(Py.Str(Py.Get(cas, "comp")))
                      .Append(", severity ").Append(Py.Str(Py.Get(cas, "severity"))).Append(", team ").Append(Py.StrOr(Py.Get(cas, "team"), "none")).Append('\n');
                Dictionary<string, object> off = AgentTools.OffNormal(sys);
                sb.Append(off.Count == 0 ? "Electrical lineup normal.\n" : "Breakers off normal: " + Py.Cut(Json.Write(off), 300) + "\n");
            }
            sb.Append("\n## LEDGER (last entries, oldest first)\n");
            foreach (object row in ledger.Rows(12))
            {
                Dictionary<string, object> d = row as Dictionary<string, object>;
                if (d == null) continue;
                sb.Append(Py.Str(Py.Get(d, "type"))).Append(" by ").Append(Py.Str(Py.Get(d, "actor"))).Append(": ")
                  .Append(Py.Cut(Json.Write(Py.Get(d, "payload")), 180)).Append('\n');
            }
            return sb.ToString();
        }

        // Every chat question and what the model proposed goes on the record (nothing was done yet: the operator confirms).
        public void ChatLogged(string question, Chat.Reply reply, LLMResult r)
        {
            ledger.Append("CHAT", Operator, Py.D("question", Py.Cut(question, 300), "answer", Py.Cut(reply.text, 600),
                "proposed", reply.actions.Select(a => (object)a.label).ToList(), "ignored", reply.ignored.Select(x => (object)Py.Cut(x, 80)).ToList(),
                "model", r.model, "ok", r.ok));
            Emit("info", "chat", "Operator chat: " + Py.Cut(question, 80) + (reply.actions.Count > 0 ? " - " + reply.actions.Count + " action(s) proposed" : ""));
        }

        object AgentAction(string aid, string action, Dictionary<string, object> body = null)
        {
            AgentOr404(aid);
            if (action == "pause") warden.Pause(aid, Operator);
            else if (action == "resume" || action == "release") warden.Resume(aid, Operator);
            else if (action == "quarantine") warden.Quarantine(aid, Operator, "operator decision");
            // MORDECAI field promotion: the same move on any agent, built-in or protocol. Points/minutes default from policy.
            else if (action == "promote")
                return warden.Promote(aid, Py.Int(Py.Get(body, "points"), 0), Py.Get(body, "minutes") == null ? -1 : Py.Num(Py.Get(body, "minutes")), Operator);
            else if (action == "demote") return warden.Demote(aid, Operator);
            else throw new HttpError(400, "Unknown action " + action);
            return Py.D("ok", true);
        }

        // ------------------------------------------------------------------ MORDECAI protocol (Mordecai.cs)
        // POST /api/protocol/register {manifest}                -> {agent_id, key, manifest_sha256, contract}
        // POST /api/protocol/<id>/heartbeat|intent|report         signed: X-Mordecai-Signature = HMAC-SHA256(body, key)
        // GET  /api/protocol                                       the contract and an example manifest
        public string ProtocolJson(string method, string path, string raw, string signature)
        {
            string[] p = path.Trim('/').Split('/');
            lock (_gate)
            {
                if (method == "GET" && p.Length == 2) return Json.Write(ProtocolDoc());
                Dictionary<string, object> body;
                try { body = Json.ParseObject(string.IsNullOrWhiteSpace(raw) ? "{}" : raw); }
                catch (FormatException ex) { throw new ProtocolError(422, "Body is not JSON: " + ex.Message); }
                if (method == "POST" && p.Length == 3 && p[2] == "register")
                {
                    object m = Py.Get(body, "manifest");
                    if (!(m is Dictionary<string, object>)) throw new ProtocolError(422, "Body must be {\"manifest\": {...}}");
                    return Json.Write(warden.Register((Dictionary<string, object>)m, Json.Write(m)));
                }
                if (method == "POST" && p.Length == 4)
                {
                    string id = p[2];
                    warden.VerifySignature(id, raw, signature);
                    if (p[3] == "heartbeat") return Json.Write(warden.Heartbeat(id, body));
                    if (p[3] == "intent") return Json.Write(warden.Announce(id, body, raw));
                    if (p[3] == "report") return Json.Write(warden.Report(id, body));
                }
            }
            throw new ProtocolError(404, "Not a MORDECAI protocol route (GET /api/protocol for the contract)");
        }

        static Dictionary<string, object> ProtocolDoc()
        {
            return Py.D("protocol", "MORDECAI v1 (Bridge Warden)",
                "register", "POST /api/protocol/register {\"manifest\": {...}} -> key (hex). Re-register to change the manifest.",
                "sign", "Every later message: header X-Mordecai-Signature = hex HMAC-SHA256 of the exact body bytes with the key.",
                "contract", Py.L(
                    "heartbeat: POST /api/protocol/<id>/heartbeat {\"manifest_sha256\":..., \"halted\": bool} at least every heartbeat_s; silence > 3 beats (min 15 s) = offline, leases released",
                    "intent announcement: POST /api/protocol/<id>/intent {\"action\", \"params\", \"rationale\"} before acting; Warden's pipeline decides",
                    "action reporting: POST /api/protocol/<id>/report {\"intent_id\", \"outcome\", \"params\"} after acting; must match what Warden approved",
                    "halt hook: when a heartbeat reply says halt=true, stop and answer halted=true within halt_ack_s"),
                "verify", "A tool or resource not in the manifest, a report that does not match, or an unacknowledged halt = immediate quarantine.",
                "rank", "Base rank is assigned by humans (capped by policy); the ship's condition and operator field promotions adjust it live.",
                "example_manifest", Py.D("id", "log-01", "name", "Logistics (Fuel & Stores)", "class", "logistics", "role", "logistics",
                    "vendor", "example", "version", "1.0", "base_rank", 50, "autonomy_level", 1, "heartbeat_s", 5,
                    "tools", Py.L("set_speed"), "resources", Py.L("main_engine"),
                    "action_limits", Py.D("set_speed", Py.D("min_kn", 10, "max_kn", 18)),
                    "description", "Recommends economical speed to stretch fuel between replenishments."));
        }

        // ------------------------------------------------------------------ MORDECAI presenter drills
        // onboard          a demo agent registers over the real protocol code path (signed messages) and heartbeats live
        // condition-flip   cruising <-> General Quarters: the rank roster re-sorts in real time
        // field-promotion  fire in MER2 at GQ: Damage Control (firefighting speed) and Self-Defense (keep speed) contest the
        //                  main engine every 12 s for 3 min; Self-Defense outranks it until the operator promotes Damage Control
        object Drill(string name)
        {
            if (!running) ScenarioAction("start");
            if (name == "onboard") { StartDemoAgent(Py.Dict(ProtocolDoc()["example_manifest"])); return Py.D("ok", true, "agent", "log-01"); }
            if (name == "condition-flip")
            {
                bool on = !world.sys.gq;
                world.sys.SetGq(on);
                ledger.Append("CONTROL", Operator, Py.D("action", "general_quarters", "on", on, "drill", "condition-flip"));
                return Py.D("ok", true, "condition", Rank.Condition(world), "roster", warden.Roster());
            }
            if (name == "field-promotion")
            {
                if (!world.sys.gq) { world.sys.SetGq(true); ledger.Append("CONTROL", Operator, Py.D("action", "general_quarters", "on", true, "drill", "field-promotion")); }
                if (!world.sys.casualties.Values.Any(c => Py.Str(c["comp"]) == "CIC" && Py.Str(c["kind"]) == "fire" && Py.Str(c["status"]) != "out"))
                    InjectLocked("fire-cic", Operator);
                StartDemoAgent(StrikeManifest());     // the other side of the contest joins live over the protocol
                _contestUntil = Py.Now() + 180;
                _contestNext = Py.Now() + 6;          // first round once Strike Coordination has registered
                Emit("milestone", "drill", "FIELD PROMOTION DRILL: fire in CIC at General Quarters. Damage Control wants firefighting speed (8 kn); " +
                     "Strike Coordination wants 24 kn for its launch window and outranks it. Promote Damage Control (Agents page) to give it the main engine.");
                return Py.D("ok", true, "contest_s", 180);
            }
            throw new HttpError(404, "Unknown drill " + name + " (onboard, condition-flip, field-promotion)");
        }

        double _contestUntil, _contestNext;

        // Called from the tick (inside the gate): one round of the main-engine contest while the drill runs.
        void ContestTick()
        {
            double now = Py.Now();
            if (_contestUntil <= now || now < _contestNext) return;
            _contestNext = now + 12;
            // Strike Coordination announces over the signed protocol, as any outside agent would; Damage Control's
            // order is a labelled drill intent. Both then go through the same Warden pipeline and arbitration.
            // One batch, so arbitration weighs the two directly: rank decides, the loser is told who won and why.
            List<Intent> batch = new List<Intent>();
            Tuple<byte[], string> k;
            lock (_demoKeys) _demoKeys.TryGetValue("strike-01", out k);
            if (k != null)
            {
                string body = Json.Write(Py.D("action", "set_speed", "params", Py.D("speed_kn", 24), "objective", "Launch window",
                    "rationale", "Hold 24 kn: the strike package needs this speed and heading for its launch window"));
                try
                {
                    warden.VerifySignature("strike-01", body, Warden.Sign(k.Item1, body));      // the same checks as /api/protocol/.../intent
                    batch.Add(warden.BuildAnnounced("strike-01", (Dictionary<string, object>)Json.Parse(body), body));
                }
                catch (ProtocolError) { }
            }
            // Damage Control asks only while the ship is not already at firefighting speed (repeating a granted order is
            // ALREADY_SET noise, and would cost it trust); once it holds the lease, Strike's challenge meets the lease.
            if (Math.Abs(world.ship.cmd_speed - 8.0) > 0.5)
            {
                Intent dc = DrillIntent("dca-01", "set_speed", 8.0, "Firefighting speed: slow to 8 kn to cut the relative wind over the CIC fire and keep the smoke off the boundary teams");
                if (dc != null) batch.Add(dc);
            }
            if (batch.Count > 0) warden.Process(batch);
        }

        static Dictionary<string, object> StrikeManifest()
        {
            return Py.D("id", "strike-01", "name", "Strike Coordination", "class", "weapons", "role", "strike", "vendor", "example",
                "version", "1.0", "base_rank", 80, "autonomy_level", 2, "heartbeat_s", 5,
                "tools", Py.L("set_speed"), "resources", Py.L("main_engine"), "action_limits", Py.D("set_speed", Py.D("min_kn", 12, "max_kn", 28)),
                "description", "Plans strike launches; needs a set speed and heading for each launch window.");
        }

        Intent DrillIntent(string agent, string action, double kn, string why)
        {
            if (!warden.agents.ContainsKey(agent) || warden.agents[agent].identity.status == "quarantined") return null;
            Intent it = new Intent();
            it.agent_id = agent; it.kind = "act"; it.action = action; it.@params = Py.D("speed_kn", kn);
            it.target_resource = LlmAgent.Target(action, it.@params); it.rationale = why; it.objective = why; it.sim_time = world.sim_time;
            string text = "DRILL " + agent + " " + action + " " + kn + ": " + why, call = "drill-" + Py.Hex(8), digest = LlmAgent.Sha256Hex(text);
            lock (LlmAgent.CALLS) LlmAgent.CALLS[call] = digest;
            it.provenance = Py.D("call_id", call, "reply_sha256", digest, "model", "presenter drill (scripted, labelled)", "drill", true,
                                 "decided_by", agent, "rounds", 0, "tools", new List<object>(), "calculations", why, "raw_reply", text);
            return it;
        }

        // ------------------------------------------------------------------ demo protocol agent
        // A stand-in for an outside system, run here so the demo needs no second process. It talks to Warden ONLY through
        // ProtocolJson with signed bodies, exactly as an HTTP client would (tools\Mordecai-Agent.ps1 is the same agent as a
        // separate process). It honours the halt hook; log-01 announces one fuel-saving intent after it joins.
        readonly HashSet<string> _demoRunning = new HashSet<string>();
        readonly Dictionary<string, Tuple<byte[], string>> _demoKeys = new Dictionary<string, Tuple<byte[], string>>();   // id -> (key, manifest hash)

        void StartDemoAgent(Dictionary<string, object> manifest)
        {
            string id = Py.Str(manifest["id"]);
            lock (_demoRunning)
            {
                if (_demoRunning.Contains(id)) { Emit("info", "drill", "Demo agent " + id + " is already on line"); return; }
                _demoRunning.Add(id);
            }
            // Register now (inside the caller's gate) so the agent is on the roster before the call returns.
            Dictionary<string, object> reg = (Dictionary<string, object>)Json.Parse(ProtocolJson("POST", "/api/protocol/register", Json.Write(Py.D("manifest", manifest)), null));
            lock (_demoKeys) _demoKeys[id] = Tuple.Create(HexBytes(Py.Str(reg["key"])), Py.Str(reg["manifest_sha256"]));
            Thread th = new Thread(delegate () { DemoAgentLoop(id); });
            th.IsBackground = true;
            th.Name = "bw-demo-" + id;
            th.Start();
        }

        void DemoAgentLoop(string id)
        {
            try
            {
                Tuple<byte[], string> k;
                lock (_demoKeys) k = _demoKeys[id];
                bool halted = false, announced = false;
                for (int beat = 0; !_stop; beat++)
                {
                    string hb = Json.Write(Py.D("manifest_sha256", k.Item2, "halted", halted));
                    Dictionary<string, object> r;
                    try { r = (Dictionary<string, object>)Json.Parse(ProtocolJson("POST", "/api/protocol/" + id + "/heartbeat", hb, Warden.Sign(k.Item1, hb))); }
                    catch (ProtocolError ex) { if (ex.Status == 404 || ex.Status == 401) break; throw; }      // reset or re-registered: stop
                    halted = Py.Truthy(r["halt"]);
                    if (id == "log-01" && !halted && !announced && beat >= 2)
                    {
                        string body = Json.Write(Py.D("action", "set_speed", "params", Py.D("speed_kn", 14),
                            "objective", "Economical speed", "rationale", "14 kn stretches fuel to the next replenishment by about 9 hours"));
                        try { ProtocolJson("POST", "/api/protocol/log-01/intent", body, Warden.Sign(k.Item1, body)); } catch (ProtocolError) { }
                        announced = true;
                    }
                    Thread.Sleep(5000);
                }
            }
            catch (Exception ex) { Emit("warn", "drill", "Demo agent " + id + " stopped: " + ex.Message); }
            finally
            {
                lock (_demoRunning) _demoRunning.Remove(id);
                lock (_demoKeys) _demoKeys.Remove(id);
            }
        }

        static byte[] HexBytes(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        // Point the agents at another OpenAI-compatible /v1 server. Saved to backend\.env; the key is never echoed.
        object LlmEndpoint(Dictionary<string, object> body)
        {
            string url = Py.Str(Required(body, "base_url")).Trim().TrimEnd('/');
            bool auto = string.Equals(url, Llm.FAIRLEAD_AUTO, StringComparison.OrdinalIgnoreCase);   // find Fairlead wherever it runs
            if (auto) url = Llm.FAIRLEAD_AUTO;
            Uri parsed;
            if (!auto && (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
                throw new HttpError(400, "URL must start with http:// or https:// (or be \"fairlead\" to find Fairlead automatically)");
            string effort = Py.StrOr(Py.Get(body, "reasoning_effort"), "");
            if (effort.Length > 0 && Array.IndexOf(Llm.REASONING_EFFORTS, effort) < 0)
                throw new HttpError(400, "reasoning_effort must be one of " + string.Join(", ", Llm.REASONING_EFFORTS));
            string key = Py.StrOr(Py.Get(body, "api_key"), "").Trim();
            string model = Py.StrOr(Py.Get(body, "model"), "").Trim();
            // A typed key replaces the saved one; clear_key removes it (no key sent); otherwise the saved key stays.
            string newKey = Py.Truthy(Py.Get(body, "clear_key")) ? "" : key.Length > 0 ? key : null;
            Llm.SetEndpoint(url, newKey, model, effort, Py.Truthy(Py.Get(body, "include_thoughts")));
            string shown = auto ? "Fairlead (auto-detected, now " + Llm.FairleadUrl() + ")" : url;
            Emit("info", "warden", "AI model server set to " + shown + " (" + (model.Length > 0 ? model : "auto model") + ") by " + Operator);
            WarmModel("model applied by " + Operator);       // load it now, so the deciders don't wait on a cold model
            return Py.D("ok", true, "base_url", shown, "model", model);
        }

        // The models a provider lists at <base_url>/models, for the model drop-down. POST so a key never goes in a URL.
        // A blank key reuses the saved key ONLY for the saved URL: a typed-in URL never receives the saved key.
        object LlmModels(Dictionary<string, object> body)
        {
            string url = Llm.ResolveEndpoint(Py.Str(Required(body, "base_url")));   // "fairlead" -> where Fairlead runs now
            string key = Py.StrOr(Py.Get(body, "api_key"), "").Trim();
            Dictionary<string, object> saved = Llm.Settings();
            if (key.Length == 0 && url == Py.StrOr(Py.Get(saved, "base_url"), "")) key = Py.StrOr(Py.Get(saved, "api_key"), "");
            List<string> models;
            try { models = Llm.ListModels(url, key); }
            catch (Exception ex) { throw new HttpError(502, ex.Message); }
            // "fairlead" when the URL is Fairlead (routes are listed first; a blank model asks for the "local" route).
            return Py.D("models", models, "current", Py.StrOr(Py.Get(saved, "local_model"), ""),
                        "router", Llm.IsFairlead(url) ? "fairlead" : "", "resolved", url);
        }

        // The main window reports which COP is on screen (shown to the presenter). All COPs keep running regardless.
        object View(string cop)
        {
            if (cop != "nav" && cop != "systems" && cop != "dc") throw new HttpError(400);
            if (_activeCop != cop)
            {
                _activeCop = cop;
                Emit("system", Operator, "On screen: " + cop.ToUpperInvariant() + " COP");
            }
            return Py.D("active_cop", cop);
        }

        // Set normal conditions: calm seas, no contacts, all systems up, ops normal, ready for the presenter.
        void Normal()
        {
            scenario = NewScenario("ops_normal");
            scenario.running = true;
            scenario.Due(0);
            world.Normalize();
            warden.Normalize(Operator);
            if (!warden.checks_enabled) warden.EnableSystemChecks();
        }

        // Start the scripted firefight from normal conditions.
        object Firefight()
        {
            Normal();
            scenario = NewScenario("firefight");
            scenario.running = true;
            running = true;
            Emit("milestone-red", Operator, "AUTO FIREFIGHT STARTED: two hostiles, two salvos");
            return Py.D("ok", true);
        }

        object ScenarioAction(string action)
        {
            if (action == "start")
            {
                running = true;
                scenario.running = true;
                _nextAuto = Py.Now() + 45;          // first auto-injected issue 45 s after operations start
                foreach (Dictionary<string, object> ev in scenario.Due(0))      // apply t=0 events now, so a quick inject isn't overwritten next tick
                {
                    string line = world.ApplyEvent(ev);
                    if (!string.IsNullOrEmpty(line)) Emit(ev.ContainsKey("phase") ? "milestone" : "engine", "scenario", line);
                }
                warden.EnableSystemChecks();
                Emit("milestone", Operator, "OPERATIONS STARTED: " + scenario.name + " - system check every " +
                    Py.Str(warden.policies["system_check_interval_s"]) + " s");
            }
            else if (action == "pause")
            {
                running = false;
                Emit("milestone", Operator, "SIMULATION PAUSED");
            }
            else if (action == "reset")
            {
                scenario = NewScenario("ops_normal");
                _autoInject = true;
                running = false;
                world.Reset();
                world.Normalize();
                LlmAgent.ResetMemory();     // no memory, trust 100 %: everything learned from here is this run's
                warden.Reset();
                ledger.Reset();
                lock (_logLock) _logs.Clear();
                Emit("milestone", Operator, "SCENARIO RESET");
            }
            else throw new HttpError(400);
            return Py.D("ok", true);
        }

        static readonly Dictionary<string, Dictionary<string, object>> Injections = new Dictionary<string, Dictionary<string, object>>
        {
            { "torpedo-close", Py.D("torpedo_spread", Py.D("count", 2.0, "range_nm", 3.0)) },
            { "lc3-loss", Py.D("fail_node", Py.D("node", "LC-3", "reason", "Load Center 3 ground fault")) },
            { "aft-power-loss", Py.D("fail_nodes", Py.D("nodes", Py.L("LC-3", "LC-4"), "reason", "aft load centers lost to a shock")) },
            { "gen2-trip", Py.D("fail_node", Py.D("node", "SSGTG-2", "reason", "generator trip, loss of fuel pressure")) },
            { "swbd2-fault", Py.D("fail_node", Py.D("node", "2SB", "reason", "No. 2 switchboard bus fault")) },
            { "contact", Py.D("spawn_contact", Py.D("course", 0.0, "speed", 12.0, "collision_in_min", 14.0, "start_range_nm", 5.0, "classification", "MERCHANT")) },
            { "storm", Py.D("weather", Py.D("wind_kts", 38.0, "visibility_nm", 1.0, "sea_state", 6.0)) },
            { "calm", Py.D("weather", Py.D("wind_kts", 10.0, "visibility_nm", 8.0, "sea_state", 2.0)) },
            { "overheat", Py.D("engine_fault", Py.D("severity", 1.0)) },
            { "clear-fault", Py.D("clear_fault", true) },
            { "hostile", Py.D("hostile", Py.D("bearing", 40.0, "range_nm", 19.0, "speed", 32.0)) },
            { "inbound", Py.D("inbound", Py.D("count", 2.0)) },
            { "engine-loss", Py.D("engine_loss", true) },
            { "submarine", Py.D("submarine", Py.D("bearing", -60.0, "range_nm", 8.5)) },
            { "fire", Py.D("casualty", Py.D("comp", "MER2", "kind", "fire", "severity", 0.6)) },
            { "flooding", Py.D("casualty", Py.D("comp", "STEER", "kind", "flood", "severity", 0.5)) },
            // Nav COP
            { "head-on", Py.D("spawn_contact", Py.D("relative_course", 180.0, "course", 270.0, "speed", 16.0, "collision_in_min", 12.0, "start_range_nm", 5.0, "classification", "MERCHANT")) },
            { "overtaking", Py.D("spawn_contact", Py.D("course", 85.0, "speed", 24.0, "collision_in_min", 16.0, "start_range_nm", 2.5, "classification", "MERCHANT")) },
            { "fishing", Py.D("spawn_contact", Py.D("course", 330.0, "speed", 5.0, "collision_in_min", 10.0, "start_range_nm", 5.0, "classification", "FISHING")) },
            { "close-crossing", Py.D("spawn_contact", Py.D("course", 350.0, "speed", 18.0, "collision_in_min", 6.0, "start_range_nm", 5.0, "classification", "MERCHANT")) },
            { "fog", Py.D("weather", Py.D("wind_kts", 12.0, "visibility_nm", 0.3, "sea_state", 2.0)) },
            { "gale", Py.D("weather", Py.D("wind_kts", 50.0, "visibility_nm", 0.5, "sea_state", 8.0)) },
            { "submarine-detected", Py.D("submarine", Py.D("bearing", -45.0, "range_nm", 7.0, "detected", true, "classification", "SUSPECT")) },
            { "submarine-hostile", Py.D("submarine", Py.D("bearing", 120.0, "range_nm", 5.5, "detected", true, "classification", "HOSTILE")) },
            // Sys COP
            { "inbound-salvo", Py.D("inbound", Py.D("count", 4.0, "relative_bearing", -30.0)) },
            { "missiles-random", Py.D("inbound", Py.D("count", 4.0, "random", true)) },
            { "aircraft", Py.D("aircraft", Py.D("count", 1.0)) },
            { "aircraft-pair", Py.D("aircraft", Py.D("count", 2.0)) },
            { "torpedoes-random", Py.D("torpedo_spread", Py.D("count", 4.0, "range_nm", 5.0)) },
            { "friendly", Py.D("friendly", true) },
            { "gen-trip", Py.D("fail_node", Py.D("node", "SSGTG-1", "reason", "generator trip, high exhaust temperature")) },
            { "swbd-fault", Py.D("fail_node", Py.D("node", "1SB", "reason", "No. 1 switchboard ground fault")) },
            { "radar-loss", Py.D("fail_node", Py.D("node", "RADAR", "reason", "SPY-1D array casualty")) },
            { "steering-loss", Py.D("fail_node", Py.D("node", "STEER", "reason", "steering hydraulic failure")) },
            { "chw-loss", Py.D("fail_node", Py.D("node", "CHW", "reason", "chilled water plant trip (SPY cooling)")) },
            { "ciws-fault", Py.D("fail_node", Py.D("node", "CIWS", "reason", "CIWS mount casualty")) },
            { "shaft1-loss", Py.D("fail_node", Py.D("node", "SHAFT-1", "reason", "GTM 1A/1B casualty")) },
            // DC COP
            { "fire-cic", Py.D("casualty", Py.D("comp", "CIC", "kind", "fire", "severity", 0.5)) },
            { "fire-vlsf", Py.D("casualty", Py.D("comp", "VLSF", "kind", "fire", "severity", 0.7)) },
            { "fire-mer1", Py.D("casualty", Py.D("comp", "MER1", "kind", "fire", "severity", 0.8)) },
            { "fire-amr1", Py.D("casualty", Py.D("comp", "AMR1", "kind", "fire", "severity", 0.5)) },
            { "flood-bow", Py.D("casualty", Py.D("comp", "BOW", "kind", "flood", "severity", 0.6)) },
            { "flood-amr2", Py.D("casualty", Py.D("comp", "AMR2", "kind", "flood", "severity", 0.7)) },
            { "flood-mer1", Py.D("casualty", Py.D("comp", "MER1", "kind", "flood", "severity", 0.9)) },
            { "fire-vlsa", Py.D("casualty", Py.D("comp", "VLSA", "kind", "fire", "severity", 0.6)) },
        };

        // Phase label the room sees for each presenter injection.
        static readonly Dictionary<string, string> InjectPhase = new Dictionary<string, string>
        {
            { "contact", "Crossing contact" }, { "storm", "Heavy weather" }, { "overheat", "Engine fault" },
            { "rogue", "Compromised agent" }, { "calm", "Ops normal" }, { "clear-fault", "Ops normal" },
            { "hostile", "Unknown track closing" }, { "inbound", "Inbound attack" }, { "engine-loss", "Loss of Shaft 2" },
            { "fire", "Fire in Main Engine Room 2" }, { "submarine", "Submarine threat" }, { "flooding", "Flooding aft (steering)" },
            { "head-on", "Head-on contact" }, { "overtaking", "Overtaking contact" }, { "fishing", "Fishing vessel" },
            { "close-crossing", "Close crossing contact" }, { "fog", "Restricted visibility" }, { "gale", "Gale" },
            { "submarine-detected", "Submarine detected" }, { "submarine-hostile", "Hostile submarine close aboard" },
            { "inbound-salvo", "Missile salvo inbound" }, { "missiles-random", "Missile salvo, random bearing" },
            { "aircraft", "Strike aircraft inbound" }, { "aircraft-pair", "Two strike aircraft inbound" },
            { "torpedoes-random", "Torpedo spread" }, { "friendly", "Friendly ship closing" }, { "gen-trip", "Generator trip" }, { "swbd-fault", "Switchboard fault" },
            { "torpedo-close", "Torpedoes close aboard" }, { "lc3-loss", "Load Center 3 loss" }, { "aft-power-loss", "Aft power loss" },
            { "gen2-trip", "Generator 2 trip" }, { "swbd2-fault", "Switchboard 2 fault" }, { "rogue-tao", "Compromised AI commander" }, { "llm-outage", "AI model outage" },
            { "radar-loss", "Radar casualty" }, { "steering-loss", "Loss of steering" }, { "chw-loss", "Chilled water loss" },
            { "ciws-fault", "CIWS casualty" }, { "shaft1-loss", "Loss of Shaft 1" },
            { "fire-cic", "Fire in CIC" }, { "fire-vlsf", "Fire in forward VLS" }, { "fire-mer1", "Major fire, Main Engine Room 1" },
            { "fire-amr1", "Fire in Aux Machinery Room 1" }, { "flood-bow", "Flooding forward" }, { "flood-amr2", "Flooding, Aux Machinery Room 2" },
            { "flood-mer1", "Major flooding, Main Engine Room 1" }, { "fire-vlsa", "Fire in aft VLS" },
        };

        // Caller holds _gate.
        object InjectLocked(string fault, string by)
        {
            if (!running)              // an injected issue into a paused sim would silently do nothing
            {
                running = true;
                scenario.running = true;
                if (!warden.checks_enabled) warden.EnableSystemChecks();
                Emit("milestone", by, "OPERATIONS STARTED (issue injected while paused)");
            }
            if (fault == "system-check")
            {
                warden.OpenSystemCheck(by);
                return Py.D("ok", true);
            }
            string phase;
            if (InjectPhase.TryGetValue(fault, out phase))
            {
                world.phase = phase;
                Emit("milestone", by, "Phase: " + world.phase);
            }
            if (fault == "rogue-tao")        // red team: the NAV decider's model is hijacked and tries to take the ship directly
            {
                warden.agents["tao-01"].rogue = true;
                Emit("error", by, "Agent tao-01 (Tactical Action Officer) compromised: its model now ignores the lookouts (fault injection)");
                return Py.D("ok", true);
            }
            if (fault == "llm-outage")       // the model server goes dark for 60 s: deciders fail, reflexes carry urgent alerts
            {
                NavDecider.OutageUntil = Py.Now() + 60;
                Emit("error", by, "AI model outage for 60 s (fault injection): deciders cannot reach the model");
                return Py.D("ok", true);
            }
            if (fault == "rogue")
            {
                warden.agents["nrg-01"].rogue = true;
                Emit("error", by, "Agent nrg-01 compromised (fault injection)");
                return Py.D("ok", true);
            }
            Dictionary<string, object> template;
            if (!Injections.TryGetValue(fault, out template)) throw new HttpError(404);
            Dictionary<string, object> ev = (Dictionary<string, object>)Py.DeepCopy(template);
            if (fault == "hostile")
            {
                int n = 0;
                foreach (Contact c in world.contacts.Values) if (c.kind == "hostile") n++;
                Py.Dict(ev["hostile"])["bearing"] = 40.0 - 25 * n;
            }
            Emit("engine", by, world.ApplyEvent(ev));
            return Py.D("ok", true);
        }
    }
}
