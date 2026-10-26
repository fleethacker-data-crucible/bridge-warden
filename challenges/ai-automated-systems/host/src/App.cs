// Bridge Warden server logic: tick loop, REST routes, presenter controls, live log (port of warden\main.py).
// WebServer.cs does the HTTP and WebSocket plumbing and calls in here. C# 5, framework assemblies only.
//   Env: PROFILE=demo|edge   TIME_SCALE=1   SCENARIO=ops_normal|firefight|full_demo   OPERATOR=OOD
using System;
using System.Collections.Generic;
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
        bool _autoInject;
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
                    "time_scale", world.time_scale);
                s["scenario"] = Py.D("name", scenario.name, "running", scenario.running, "elapsed", Py.Round(scenario.elapsed, 1),
                    "finished", scenario.Finished());
                s["resources"] = meter.Sample();
                Dictionary<string, object> llmSettings = new Dictionary<string, object>();
                foreach (KeyValuePair<string, object> kv in Llm.Settings())
                    if (Array.IndexOf(new[] { "mode", "provider", "model", "local_model", "base_url", "reasoning_effort", "include_thoughts" }, kv.Key) >= 0)
                        llmSettings[kv.Key] = kv.Value;
                Dictionary<string, object> agentsLlm = new Dictionary<string, object>(LlmAgent.SCHEDULER.stats);
                agentsLlm["queue"] = LlmAgent.SCHEDULER.Depth();
                s["server"] = Py.D("uptime_s", (int)Py.Round(Py.Now() - _started), "operator", Operator, "profile", Profile,
                    "running", running, "llm", llmSettings, "agents_llm", agentsLlm);
                return s;
            }
        }

        string StateMessage(List<object> logs)
        {
            string data = Json.Write(Py.D("type", "state", "data", FullState(), "logs", logs));
            meter.CountWs(data.Length);
            return data;
        }

        // The first message a new socket gets: the state plus the whole log so far.
        public string HelloMessage() { return Json.Write(Py.D("type", "state", "data", FullState(), "logs", AllLogs())); }

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
                HashSet<string> active = new HashSet<string> { _activeCop };
                world.Step(1.0, active);
                double t0 = Py.Perf();
                warden.Step(active, _tickS);
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
        public object Api(string method, string path, Dictionary<string, string> query, Dictionary<string, object> body)
        {
            string[] p = path.Trim('/').Split('/');      // "api", ...
            int n = p.Length;
            bool get = method == "GET", post = method == "POST";
            lock (_gate)
            {
                if (get && path == "/api/state") return FullState();
                if (get && path == "/api/summary") return warden.Summary();
                if (get && path == "/api/agents") return warden.Snapshot()["agents"];
                if (post && n == 4 && p[1] == "agents") return AgentAction(p[2], p[3]);
                if (method == "PATCH" && n == 4 && p[1] == "agents" && p[3] == "autonomy")
                {
                    AgentOr404(p[2]);
                    warden.SetAutonomy(p[2], Py.Int(Required(body, "level")), Operator);
                    return Py.D("ok", true);
                }
                if (post && path == "/api/llm/endpoint") return LlmEndpoint(body);
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

        object AgentAction(string aid, string action)
        {
            AgentOr404(aid);
            if (action == "pause") warden.Pause(aid, Operator);
            else if (action == "resume" || action == "release") warden.Resume(aid, Operator);
            else if (action == "quarantine") warden.Quarantine(aid, Operator, "operator decision");
            else throw new HttpError(400, "Unknown action " + action);
            return Py.D("ok", true);
        }

        // Point the agents at another OpenAI-compatible /v1 server. Saved to backend\.env; the key is never echoed.
        object LlmEndpoint(Dictionary<string, object> body)
        {
            string url = Py.Str(Required(body, "base_url")).Trim().TrimEnd('/');
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                throw new HttpError(400, "URL must start with http:// or https://");
            string effort = Py.StrOr(Py.Get(body, "reasoning_effort"), "");
            if (effort.Length > 0 && Array.IndexOf(Llm.REASONING_EFFORTS, effort) < 0)
                throw new HttpError(400, "reasoning_effort must be one of " + string.Join(", ", Llm.REASONING_EFFORTS));
            string key = Py.StrOr(Py.Get(body, "api_key"), "").Trim();
            string model = Py.StrOr(Py.Get(body, "model"), "").Trim();
            Llm.SetEndpoint(url, key.Length > 0 ? key : null, model, effort, Py.Truthy(Py.Get(body, "include_thoughts")));
            Emit("info", "warden", "AI model server set to " + url + " (" + (model.Length > 0 ? model : "auto model") + ") by " + Operator);
            return Py.D("ok", true, "base_url", url, "model", model);
        }

        // The main window reports which COP is on screen. Only that COP's world and agents run.
        object View(string cop)
        {
            if (cop != "nav" && cop != "systems" && cop != "dc") throw new HttpError(400);
            if (_activeCop != cop)
            {
                _activeCop = cop;
                Emit("system", Operator, "On screen: " + cop.ToUpperInvariant() + " COP - other COPs paused");
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
                _autoInject = false;
                running = false;
                world.Reset();
                world.Normalize();
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
            { "contact", Py.D("spawn_contact", Py.D("course", 0.0, "speed", 12.0, "collision_in_min", 14.0, "classification", "MERCHANT")) },
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
        };

        // Phase label the room sees for each presenter injection.
        static readonly Dictionary<string, string> InjectPhase = new Dictionary<string, string>
        {
            { "contact", "Crossing contact" }, { "storm", "Heavy weather" }, { "overheat", "Engine fault" },
            { "rogue", "Compromised agent" }, { "calm", "Ops normal" }, { "clear-fault", "Ops normal" },
            { "hostile", "Unknown track closing" }, { "inbound", "Inbound attack" }, { "engine-loss", "Loss of Shaft 2" },
            { "fire", "Fire in Main Engine Room 2" }, { "submarine", "Submarine threat" }, { "flooding", "Flooding aft (steering)" },
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
