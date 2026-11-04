// MORDECAI: Multi-agent Oversight, Resource Deconfliction, Emergent Conflict Arbitration, Intervention.
// C# 5, framework assemblies only.
//
// Bridge Warden is the governance layer above the agents; this file adds the parts of the MORDECAI contract it lacked:
//   - Rank as data: effective rank = human-assigned base rank (identity.priority) + a class modifier for the ship's
//     condition (policies.json "rank".conditions, e.g. general_quarters lifts weapons, lowers navigation) + an
//     operator field promotion while it lasts. Arbitration and resource leases use the live effective rank, so a
//     condition flip or a promotion changes who wins at once. The roster re-sorts with it.
//   - Protocol agents: an agent outside this process registers a manifest (POST /api/protocol/register) and gets a key
//     bound to that manifest's hash. Every later message is signed (HMAC-SHA256 of the body with that key) and honours
//     a four-part contract: heartbeat (whose reply carries the halt hook), intent announcement (through the same
//     Warden pipeline as every agent), action reporting, and halt acknowledgement. Zero restart: the roster grows live.
//   - Declare and verify: an intent for a tool or resource the manifest did not declare, an action report that does not
//     match what Warden approved, or a halt that is not acknowledged = immediate quarantine (policy as data).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace BridgeWarden
{
    // ------------------------------------------------------------------ rank
    public static class Rank
    {
        // Agent class from its role, when the manifest names none.
        static readonly Dictionary<string, string> CLASS_OF_ROLE = new Dictionary<string, string>
        {
            { "nav_tao", "command" }, { "eoow", "command" }, { "dc_officer", "command" },
            { "collision", "navigation" }, { "weather", "navigation" }, { "navigation", "navigation" },
            { "launch", "weapons" }, { "defense", "defense" }, { "asw", "defense" },
            { "power", "engineering" }, { "engine", "engineering" }, { "energy", "logistics" },
            { "damage_control", "damage_control" },
        };

        public static string ClassOf(string role)
        {
            string c;
            return role != null && CLASS_OF_ROLE.TryGetValue(role, out c) ? c : "support";
        }

        // The ship's condition for rank purposes: General Quarters or cruising (Condition III).
        public static string Condition(World w) { return w != null && w.sys != null && w.sys.gq ? "general_quarters" : "cruising"; }

        public static int Modifier(Dictionary<string, object> policies, string condition, string cls)
        {
            Dictionary<string, object> conds = Py.DictOrEmpty(Py.Get(Py.DictOrEmpty(Py.Get(policies, "rank")), "conditions"));
            Dictionary<string, object> mods = Py.DictOrEmpty(Py.Get(conds, condition));
            return Py.Int(Py.Get(mods, cls ?? ""), 0);
        }

        public static bool Promoted(AgentIdentity i) { return i.promotion != 0 && (i.promotion_until <= 0 || Py.Now() < i.promotion_until); }

        // {base, condition, modifier, promotion, effective}
        public static Dictionary<string, object> Parts(AgentIdentity i, Dictionary<string, object> policies, World w)
        {
            string cond = Condition(w);
            int mod = Modifier(policies, cond, i.agent_class);
            int promo = Promoted(i) ? i.promotion : 0;
            return Py.D("base", i.priority, "condition", cond, "class", i.agent_class, "modifier", mod, "promotion", promo,
                        "promoted_by", promo != 0 ? i.promoted_by : "",
                        "promotion_left_s", promo != 0 && i.promotion_until > 0 ? (object)(int)Math.Max(0, i.promotion_until - Py.Now()) : null,
                        "effective", i.priority + mod + promo);
        }

        public static int Effective(AgentIdentity i, Dictionary<string, object> policies, World w)
        {
            return i.priority + Modifier(policies, Condition(w), i.agent_class) + (Promoted(i) ? i.promotion : 0);
        }
    }

    // ------------------------------------------------------------------ protocol errors
    public sealed class ProtocolError : Exception
    {
        public readonly int Status;
        public ProtocolError(int status, string message) : base(message) { Status = status; }
    }

    public partial class Warden
    {
        // ------------------------------------------------------------------ rank and roster
        public int EffectiveRank(string agent_id)
        {
            LLMAgentRuntime rt;
            if (agent_id == null || !agents.TryGetValue(agent_id, out rt)) return OPERATOR_PRIORITY;
            return Rank.Effective(rt.identity, policies, world);
        }

        // The roster in rank order, as the operator sees it: who outranks whom right now, and why.
        public List<object> Roster()
        {
            return agents.Values
                .Select(a => new { a, r = Rank.Parts(a.identity, policies, world) })
                .OrderByDescending(x => Py.Int(x.r["effective"]))
                .ThenBy(x => x.a.identity.id, StringComparer.Ordinal)
                .Select((x, n) => (object)Py.D("position", n + 1, "id", x.a.identity.id, "name", x.a.identity.name,
                                               "status", x.a.identity.status, "protocol", x.a.identity.protocol, "rank", x.r))
                .ToList();
        }

        string _lastCondition;
        readonly Dictionary<string, string> _conflictWinner = new Dictionary<string, string>();   // conflict id -> last winner

        // Called every tick: a condition change (cruising <-> general quarters) re-sorts the roster; the log and the
        // ledger say who moved. Expired promotions end here too.
        void RankTick()
        {
            foreach (LLMAgentRuntime a in agents.Values)
            {
                AgentIdentity i = a.identity;
                if (i.promotion != 0 && i.promotion_until > 0 && Py.Now() >= i.promotion_until)
                {
                    emit("system", "warden", "FIELD PROMOTION ENDED: " + i.name + " back to rank " + Rank.Effective(new AgentIdentity { priority = i.priority, agent_class = i.agent_class }, policies, world));
                    ledger.Append("RANK", "warden", Py.D("event", "promotion_expired", "agent", i.id));
                    i.promotion = 0; i.promotion_until = 0; i.promoted_by = "";
                }
            }
            string cond = Rank.Condition(world);
            if (_lastCondition == null) { _lastCondition = cond; return; }
            if (cond == _lastCondition) return;
            string was = _lastCondition;
            _lastCondition = cond;
            List<object> roster = Roster();
            string top = string.Join(", ", roster.Take(4).Select(r => Py.Str(((Dictionary<string, object>)r)["name"])).ToArray());
            emit("milestone", "warden", "CONDITION " + Label(was) + " -> " + Label(cond) + ": rank roster re-sorted. Top now: " + top);
            ledger.Append("RANK", "warden", Py.D("event", "condition", "from", was, "to", cond,
                "roster", roster.Select(r => (object)(Py.Str(((Dictionary<string, object>)r)["id"]) + ":" +
                    Py.Str(Py.Get(Py.DictOrEmpty(((Dictionary<string, object>)r)["rank"]), "effective")))).ToList()));
        }

        static string Label(string condition) { return condition == "general_quarters" ? "GENERAL QUARTERS" : "CRUISING"; }

        // Operator field promotion: +points for minutes (0 = until revoked). The same move works on every agent.
        public Dictionary<string, object> Promote(string agent_id, int points, double minutes, string op)
        {
            LLMAgentRuntime rt;
            if (!agents.TryGetValue(agent_id, out rt)) throw new KeyNotFoundException("Unknown agent " + agent_id);
            Dictionary<string, object> rk = Py.DictOrEmpty(Py.Get(policies, "rank"));
            if (points == 0) points = Py.Int(Py.Get(rk, "promotion_points"), 25);
            if (minutes < 0) minutes = Py.Num(Py.Get(rk, "promotion_minutes"), 10);
            AgentIdentity i = rt.identity;
            int before = Rank.Effective(i, policies, world);
            i.promotion = points;
            i.promotion_until = minutes > 0 ? Py.Now() + minutes * 60 : 0;
            i.promoted_by = op;
            int after = Rank.Effective(i, policies, world);
            ledger.Append("RANK", op, Py.D("event", "field_promotion", "agent", agent_id, "points", points, "minutes", minutes, "from", before, "to", after));
            emit("milestone", op, "FIELD PROMOTION: " + i.name + " rank " + before + " -> " + after +
                 (minutes > 0 ? " for " + minutes.ToString("0.#", CultureInfo.InvariantCulture) + " min" : " until revoked") + " (by " + op + ")");
            return Rank.Parts(i, policies, world);
        }

        public Dictionary<string, object> Demote(string agent_id, string op)
        {
            LLMAgentRuntime rt;
            if (!agents.TryGetValue(agent_id, out rt)) throw new KeyNotFoundException("Unknown agent " + agent_id);
            AgentIdentity i = rt.identity;
            int before = Rank.Effective(i, policies, world);
            i.promotion = 0; i.promotion_until = 0; i.promoted_by = "";
            ledger.Append("RANK", op, Py.D("event", "promotion_revoked", "agent", agent_id));
            emit("system", op, "FIELD PROMOTION REVOKED: " + i.name + " rank " + before + " -> " + Rank.Effective(i, policies, world));
            return Rank.Parts(i, policies, world);
        }

        // ------------------------------------------------------------------ protocol agents
        sealed class ProtocolState
        {
            public byte[] key;
            public string manifest_sha256;
            public double last_heartbeat;
            public double halt_since;          // when Warden first asked it to halt (0 = not asked)
            public bool halted_ack;            // it said it has stopped
            public int beats;
        }
        readonly Dictionary<string, ProtocolState> _protocol = new Dictionary<string, ProtocolState>();

        Dictionary<string, object> ProtocolPolicy() { return Py.DictOrEmpty(Py.Get(policies, "protocol")); }

        static string Hex(byte[] b) { return string.Concat(b.Select(x => x.ToString("x2"))); }

        public static string Sign(byte[] key, string body)
        {
            using (HMACSHA256 h = new HMACSHA256(key)) return Hex(h.ComputeHash(new UTF8Encoding(false).GetBytes(body ?? "")));
        }

        // Every protocol message after registration: body signed with the agent's key (X-Mordecai-Signature).
        public void VerifySignature(string agent_id, string body, string signature)
        {
            ProtocolState ps;
            if (!_protocol.TryGetValue(agent_id ?? "", out ps)) throw new ProtocolError(404, "Unknown protocol agent " + agent_id + ": register first");
            string want = Sign(ps.key, body);
            if (string.IsNullOrEmpty(signature) || !FixedEquals(want, signature.Trim().ToLowerInvariant()))
                throw new ProtocolError(401, "Bad or missing X-Mordecai-Signature (HMAC-SHA256 of the body with the key from registration)");
        }

        static bool FixedEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }

        static readonly System.Text.RegularExpressions.Regex AGENT_ID = new System.Text.RegularExpressions.Regex("^[a-z][a-z0-9-]{1,30}$");

        // Register (or re-register) a protocol agent from its manifest. Returns the signing key once.
        public Dictionary<string, object> Register(Dictionary<string, object> manifest, string rawManifest)
        {
            string id = Py.StrOr(Py.Get(manifest, "id"), "").Trim();
            if (!AGENT_ID.IsMatch(id)) throw new ProtocolError(422, "manifest.id: lower-case letters, digits and hyphens, starting with a letter");
            LLMAgentRuntime existing;
            if (agents.TryGetValue(id, out existing) && !existing.identity.protocol)
                throw new ProtocolError(409, id + " is a built-in agent; pick another id");
            if (existing == null && agents.Count >= Py.Int(Py.Get(ProtocolPolicy(), "max_agents"), 32))
                throw new ProtocolError(429, "Agent limit reached (policies.json protocol.max_agents)");
            List<object> tools = Py.ListOrEmpty(Py.Get(manifest, "tools"));
            List<object> res = Py.ListOrEmpty(Py.Get(manifest, "resources"));
            if (tools.Count == 0) throw new ProtocolError(422, "manifest.tools: declare at least one tool (what you may ask for)");
            if (res.Count == 0) throw new ProtocolError(422, "manifest.resources: declare the resources you may touch (e.g. main_engine, rudder)");
            foreach (object t in tools)
                if (Array.IndexOf(LlmAgent.TOOL_SHAPE_ORDER, Py.Str(t)) < 0)
                    throw new ProtocolError(422, "manifest.tools: unknown tool " + Py.Str(t) + " (known: " + string.Join(", ", LlmAgent.TOOL_SHAPE_ORDER) + ")");
            // Base rank is the human's to assign: a manifest may ask, but never above the protocol ceiling.
            int ceiling = Py.Int(Py.Get(ProtocolPolicy(), "max_base_rank"), 80);
            int asked = Py.Int(Py.Get(manifest, "base_rank"), 30);
            Dictionary<string, object> a = Py.D("id", id, "name", Py.StrOr(Py.Get(manifest, "name"), id),
                "role", Py.StrOr(Py.Get(manifest, "role"), "external"), "class", Py.StrOr(Py.Get(manifest, "class"), "support"),
                "vendor", Py.StrOr(Py.Get(manifest, "vendor"), "external"), "version", Py.StrOr(Py.Get(manifest, "version"), "1.0"),
                "priority", Math.Max(0, Math.Min(ceiling, asked)), "autonomy_level", Math.Max(0, Math.Min(2, Py.Int(Py.Get(manifest, "autonomy_level"), 1))),
                "tools", tools, "resources", res, "action_limits", Py.DictOrEmpty(Py.Get(manifest, "action_limits")),
                "resource_quota", Py.D("intents_per_min", Math.Min(30, Py.Num(Py.Get(Py.DictOrEmpty(Py.Get(manifest, "resource_quota")), "intents_per_min"), 12))),
                "read_scopes", Py.ListOrEmpty(Py.Get(manifest, "read_scopes")), "cop", Py.StrOr(Py.Get(manifest, "cop"), "nav"),
                "description", Py.Cut(Py.StrOr(Py.Get(manifest, "description"), ""), 300), "heartbeat_s", Math.Max(1, Math.Min(30, Py.Num(Py.Get(manifest, "heartbeat_s"), 5))));
            AgentIdentity ident = AgentIdentity.FromDict(a);
            ident.protocol = true;
            ident.status = "idle";
            LLMAgentRuntime rt = new LLMAgentRuntime(ident, world);
            lock (rt.status) { rt.status["enabled"] = false; rt.status["model"] = "external agent (MORDECAI protocol)"; }
            agents[id] = rt;
            if (!violations.ContainsKey(id)) violations[id] = new Queue<double>();
            if (!quota_log.ContainsKey(id)) quota_log[id] = new Queue<double>();
            quarantine_reason.Remove(id);
            byte[] key = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            string sha;
            using (SHA256 h = SHA256.Create()) sha = Hex(h.ComputeHash(new UTF8Encoding(false).GetBytes(rawManifest ?? "")));
            _protocol[id] = new ProtocolState { key = key, manifest_sha256 = sha, last_heartbeat = Py.Now() };
            last_seen[id] = Py.Now();
            ledger.Append("AGENT_REGISTERED", id, Py.D("name", ident.name, "class", ident.agent_class, "base_rank", ident.priority,
                "rank_asked", asked, "tools", tools, "resources", res, "manifest_sha256", sha, "replaced", existing != null));
            emit("milestone", "warden", "AGENT ONBOARDED LIVE: " + ident.name + " (" + id + "), class " + ident.agent_class + ", base rank " + ident.priority +
                 (asked > ident.priority ? " (asked " + asked + ", capped)" : "") + " - roster now " + agents.Count + " agents, no restart");
            return Py.D("agent_id", id, "key", Hex(key), "manifest_sha256", sha, "base_rank", ident.priority, "heartbeat_s", ident.heartbeat_s,
                        "contract", Py.L("POST /api/protocol/" + id + "/heartbeat", "POST /api/protocol/" + id + "/intent",
                                          "POST /api/protocol/" + id + "/report", "halt: honour halt=true in every heartbeat reply and answer halted=true"),
                        "signature", "X-Mordecai-Signature: hex HMAC-SHA256 of the exact request body with this key");
        }

        // Heartbeat: proves liveness and carries the halt hook both ways. Returns {halt, reason, status, rank}.
        public Dictionary<string, object> Heartbeat(string agent_id, Dictionary<string, object> body)
        {
            ProtocolState ps = _protocol[agent_id];
            AgentIdentity i = agents[agent_id].identity;
            if (Py.StrOr(Py.Get(body, "manifest_sha256"), ps.manifest_sha256) != ps.manifest_sha256)
            {
                Quarantine(agent_id, "warden", "manifest changed without re-registering (declare-and-verify)");
                throw new ProtocolError(409, "Manifest hash does not match the registered manifest: quarantined");
            }
            double now = Py.Now();
            ps.last_heartbeat = now;
            ps.beats++;
            i.heartbeat_at = now;
            last_seen[agent_id] = now;
            if (i.status == "offline")
            {
                i.status = "idle";
                emit("ok", agent_id, "Heartbeat back: " + i.name + " is on line again");
                ledger.Append("CONTRACT", agent_id, Py.D("event", "heartbeat_restored"));
            }
            bool halt = HaltWanted(i);
            if (halt)
            {
                if (ps.halt_since == 0) ps.halt_since = now;
                if (Py.Truthy(Py.Get(body, "halted")) && !ps.halted_ack)
                {
                    ps.halted_ack = true;
                    ledger.Append("CONTRACT", agent_id, Py.D("event", "halt_acknowledged", "after_s", Py.Round(now - ps.halt_since, 1)));
                    emit("system", agent_id, "HALT acknowledged by " + i.name);
                }
            }
            else { ps.halt_since = 0; ps.halted_ack = false; }
            lock (agents[agent_id].status)
            {
                agents[agent_id].status["last_ok"] = true;
                agents[agent_id].status["last_at"] = now;
                agents[agent_id].status["last_actions"] = new List<object> { "heartbeat " + ps.beats + (halt ? " - HALTED" : "") };
            }
            return Py.D("ok", true, "halt", halt, "reason", halt ? HaltReason(i) : "", "status", i.status, "rank", Rank.Parts(i, policies, world),
                        "condition", Rank.Condition(world), "next_heartbeat_s", i.heartbeat_s);
        }

        bool HaltWanted(AgentIdentity i) { return safe_stop || i.status == "paused" || i.status == "quarantined"; }
        string HaltReason(AgentIdentity i)
        {
            if (i.status == "quarantined") { string q; return "quarantined: " + (quarantine_reason.TryGetValue(i.id, out q) ? q : "operator"); }
            if (safe_stop) return "SAFE-STOP engaged: all agents paused";
            return "paused by the operator";
        }

        // Intent announcement: declare-and-verify against the manifest, then the same Warden pipeline as every agent.
        public Dictionary<string, object> Announce(string agent_id, Dictionary<string, object> body, string rawBody)
        {
            Intent it = BuildAnnounced(agent_id, body, rawBody);
            Process(new List<Intent> { it });
            return Py.D("intent_id", it.id, "status", it.status, "reasons", it.reasons, "needs_approval", it.needs_approval,
                        "rank", Rank.Effective(agents[agent_id].identity, policies, world), "conflicts", it.conflict_ids);
        }

        // The checked intent, not yet processed (a drill submits it in one batch with its rival so arbitration sees both).
        public Intent BuildAnnounced(string agent_id, Dictionary<string, object> body, string rawBody)
        {
            LLMAgentRuntime rt = agents[agent_id];
            AgentIdentity i = rt.identity;
            if (HaltWanted(i) || i.status == "offline")
                throw new ProtocolError(423, "Not accepted: agent is " + (i.status == "offline" ? "offline (heartbeat lost)" : "halted - " + HaltReason(i)));
            string action = Py.StrOr(Py.Get(body, "action"), "");
            Dictionary<string, object> prms = Py.DictOrEmpty(Py.Get(body, "params"));
            string target = LlmAgent.Target(action, prms);
            bool mismatchTool = !i.HasTool(action);
            bool mismatchRes = !i.resources.Select(Py.Str).Any(r => r == target || (r.EndsWith("*") && target.StartsWith(r.TrimEnd('*'), StringComparison.Ordinal)));
            if ((mismatchTool || mismatchRes) && Py.Truthy(Py.Get(ProtocolPolicy(), "quarantine_on_manifest_mismatch", true)))
            {
                string why = mismatchTool ? "asked for tool " + action + ", not in its manifest" : "touched resource " + target + ", not declared in its manifest";
                Quarantine(agent_id, "warden", "declare-and-verify: " + why);
                throw new ProtocolError(403, "Manifest mismatch (" + why + "): quarantined");
            }
            Intent it = new Intent();
            it.agent_id = agent_id; it.kind = "act"; it.action = action; it.@params = prms; it.target_resource = target;
            it.objective = Py.Cut(Py.StrOr(Py.Get(body, "objective"), ""), 200);
            it.rationale = Py.Cut(Py.StrOr(Py.Get(body, "rationale"), ""), 400);
            it.confidence = Math.Max(0, Math.Min(1, Py.Num(Py.Get(body, "confidence"), 0.8)));
            it.sim_time = world.sim_time;
            // Provenance: the signed message, registered like a model call so Warden's authenticate stage verifies it.
            string call = "proto-" + Py.Hex(8), digest = LlmAgent.Sha256Hex(rawBody ?? "");
            lock (LlmAgent.CALLS) LlmAgent.CALLS[call] = digest;
            it.provenance = Py.D("call_id", call, "reply_sha256", digest, "model", "external agent (signed MORDECAI message)",
                                 "decided_by", agent_id, "protocol", true, "rounds", 0, "tools", new List<object>(), "calculations", it.rationale,
                                 "raw_reply", Py.Cut(rawBody ?? "", 2000));
            ledger.Append("CONTRACT", agent_id, Py.D("event", "intent_announced", "intent", it.id, "action", action, "target", target));
            return it;
        }

        // Action reporting: what the agent says happened must match what Warden approved for it.
        public Dictionary<string, object> Report(string agent_id, Dictionary<string, object> body)
        {
            string iid = Py.StrOr(Py.Get(body, "intent_id"), "");
            Intent it;
            string why = null;
            if (!intents.TryGetValue(iid, out it) || it.agent_id != agent_id)
                why = "reported an action Warden never received from it (" + Py.Cut(iid, 20) + ")";
            else if (it.status != "executed" && it.status != "approved")
                why = "reported acting on " + iid + ", which Warden " + it.status + (it.reasons.Count > 0 ? " (" + Py.Str(it.reasons[it.reasons.Count - 1]) + ")" : "");
            else
            {
                Dictionary<string, object> did = Py.DictOrEmpty(Py.Get(body, "params"));
                foreach (KeyValuePair<string, object> kv in did)
                    if (it.@params.ContainsKey(kv.Key) && !PyEq(it.@params[kv.Key], kv.Value) &&
                        !(IsNumber(it.@params[kv.Key]) && IsNumber(kv.Value) && Math.Abs(Py.Num(it.@params[kv.Key]) - Py.Num(kv.Value)) < 0.01))
                    { why = "reported " + kv.Key + "=" + Py.Str(kv.Value) + " but Warden approved " + Py.Str(it.@params[kv.Key]); break; }
            }
            if (why != null && Py.Truthy(Py.Get(ProtocolPolicy(), "quarantine_on_manifest_mismatch", true)))
            {
                Quarantine(agent_id, "warden", "declare-and-verify: " + why);
                throw new ProtocolError(409, "Report does not match: " + why + ". Quarantined.");
            }
            ledger.Append("ACTION_REPORT", agent_id, Py.D("intent", iid, "outcome", Py.StrOr(Py.Get(body, "outcome"), "done"), "params", Py.Get(body, "params")));
            return Py.D("ok", true, "verified", true);
        }

        static bool IsNumber(object o) { return o is int || o is long || o is double || o is float || o is decimal; }

        // Contract monitor (every tick): heartbeats and halt acknowledgements.
        void ProtocolTick(double now)
        {
            double timeout = Py.Num(Py.Get(ProtocolPolicy(), "heartbeat_timeout_s"), 15);
            double haltAck = Py.Num(Py.Get(ProtocolPolicy(), "halt_ack_s"), 12);
            foreach (KeyValuePair<string, ProtocolState> kv in _protocol.ToList())
            {
                LLMAgentRuntime rt;
                if (!agents.TryGetValue(kv.Key, out rt)) continue;
                AgentIdentity i = rt.identity;
                ProtocolState ps = kv.Value;
                if (i.status != "offline" && i.status != "quarantined" && now - ps.last_heartbeat > Math.Max(timeout, i.heartbeat_s * 3))
                {
                    i.status = "offline";
                    int freed = 0;
                    foreach (string h in holds.Where(x => Py.Str(x.Value["agent_id"]) == kv.Key).Select(x => x.Key).ToList()) { holds.Remove(h); freed++; }
                    AlertMsg("warn", "warden", "HEARTBEAT LOST: " + i.name + " (" + kv.Key + ") silent " + (int)(now - ps.last_heartbeat) + " s - marked offline" +
                             (freed > 0 ? ", " + freed + " lease(s) released" : ""));
                    ledger.Append("CONTRACT", kv.Key, Py.D("event", "heartbeat_lost", "silent_s", (int)(now - ps.last_heartbeat), "leases_released", freed));
                }
                if (ps.halt_since > 0 && !ps.halted_ack && i.status != "quarantined" && now - ps.halt_since > haltAck)
                {
                    Quarantine(kv.Key, "warden", "did not acknowledge HALT within " + haltAck + " s (halt hook)");
                    ps.halt_since = 0;
                }
            }
        }

        public bool IsProtocol(string agent_id) { return _protocol.ContainsKey(agent_id ?? ""); }
    }
}
