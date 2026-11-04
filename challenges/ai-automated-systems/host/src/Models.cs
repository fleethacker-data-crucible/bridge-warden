// Typed messages shared by the core, the agents and the UI (port of warden\models.py, agents.py, domains.py).
// Field names stay snake_case on purpose: ToJson() writes them as they are, so the UI gets the JSON the Python
// pydantic models produced (model_dump). C# 5, framework assemblies only.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace BridgeWarden
{
    // model_dump(): every public instance field, in declaration order.
    public abstract class Model : IJson
    {
        public virtual object ToJson() { return Dump(); }

        public Dictionary<string, object> Dump()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            FieldInfo[] fields = GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
            Array.Sort(fields, delegate (FieldInfo a, FieldInfo b) { return a.MetadataToken.CompareTo(b.MetadataToken); });
            foreach (FieldInfo f in fields) d[f.Name] = f.GetValue(this);
            return d;
        }

        // model_dump() | {extra}
        public Dictionary<string, object> DumpWith(params object[] kv)
        {
            Dictionary<string, object> d = Dump();
            foreach (KeyValuePair<string, object> e in Py.D(kv)) d[e.Key] = e.Value;
            return d;
        }
    }

    public static class Stages
    {
        public static readonly string[] All = { "authenticate", "authority", "constraints", "quota", "conflicts", "arbitrate", "approve", "execute" };
        public static int IndexOf(string s) { return Array.IndexOf(All, s); }
        // Autonomy levels: 0 advise - 1 recommend - 2 act-with-approval - 3 act
        public static readonly Dictionary<int, string> AutonomyLabels = new Dictionary<int, string> { { 0, "ADVISE" }, { 1, "RECOMMEND" }, { 2, "ACT W/ APPROVAL" }, { 3, "ACT" } };
    }

    public sealed class AgentIdentity : Model
    {
        public string id;
        public string name;
        public string role;
        public string version = "1.0";
        public string vendor = "";
        public string status = "idle";          // idle active escalated paused quarantined offline
        public int autonomy_level = 1;
        public int priority = 0;
        public List<object> read_scopes = new List<object>();
        public List<object> tools = new List<object>();
        public Dictionary<string, object> action_limits = new Dictionary<string, object>();
        public Dictionary<string, object> resource_quota = new Dictionary<string, object>();
        public double trust_score = 100.0;
        public double heartbeat_at = Py.Now();
        public string description = "";
        public string behavior = "";
        public string cop = "nav";             // nav | systems | dc: the agent only runs while its COP is on screen
        // "decider" (one model per picture: TAO, EOOW, DCO) or "sub" (a lookout, behavior "lookout", that reports alerts to
        // its parent decider and carries out the option the decider picks). NavCell.cs.
        public string tier = "sub";
        public string parent;

        // AgentIdentity(**a) from config\agents.json
        public static AgentIdentity FromDict(Dictionary<string, object> a)
        {
            AgentIdentity i = new AgentIdentity();
            i.id = Py.Str(a["id"]);
            i.name = Py.Str(a["name"]);
            i.role = Py.Str(a["role"]);
            i.version = Py.StrOr(Py.Get(a, "version"), i.version);
            i.vendor = Py.StrOr(Py.Get(a, "vendor"), i.vendor);
            i.status = Py.StrOr(Py.Get(a, "status"), i.status);
            i.autonomy_level = Py.Int(Py.Get(a, "autonomy_level"), i.autonomy_level);
            i.priority = Py.Int(Py.Get(a, "priority"), i.priority);
            i.read_scopes = Py.ListOrEmpty(Py.Get(a, "read_scopes"));
            i.tools = Py.ListOrEmpty(Py.Get(a, "tools"));
            i.action_limits = Py.DictOrEmpty(Py.Get(a, "action_limits"));
            i.resource_quota = Py.DictOrEmpty(Py.Get(a, "resource_quota"));
            i.trust_score = Py.Num(Py.Get(a, "trust_score"), i.trust_score);
            i.description = Py.StrOr(Py.Get(a, "description"), i.description);
            i.behavior = Py.StrOr(Py.Get(a, "behavior"), i.behavior);
            i.cop = Py.StrOr(Py.Get(a, "cop"), i.cop);
            i.tier = Py.StrOr(Py.Get(a, "tier"), i.tier);
            i.parent = Py.Get(a, "parent") as string;
            return i;
        }

        public bool HasTool(string t) { return tools.Contains(t); }
    }

    public sealed class Intent : Model
    {
        public string id = Py.Hex(8);
        public string agent_id;
        public string kind = "act";            // recommend | act
        public string action;
        public Dictionary<string, object> @params = new Dictionary<string, object>();
        public string target_resource;
        public string objective = "";
        public string rationale = "";
        public double confidence = 0.8;
        public double created_at = Py.Now();
        public double sim_time = 0.0;
        public bool reversible = true;
        public Dictionary<string, object> inverse;           // null = None
        public string status = "pending";      // pending approved rejected escalated executed rolled_back expired advisory
        public Dictionary<string, object> pipeline = NewPipeline();
        public List<object> reasons = new List<object>();
        public List<object> policy_hits = new List<object>();
        public List<object> conflict_ids = new List<object>();
        public string decided_by = "";
        public bool needs_approval = false;
        // The model's risk call (ship, crew, fight) and an optional second option: {"action", "params", "rationale"}.
        public Dictionary<string, object> risk = new Dictionary<string, object>();
        public Dictionary<string, object> alternative;       // null = None
        public string preapproved_by = "";     // the operator picked this (option B) from a decision card
        // Proof the decision came from the language model (call id, model, tools, calculations, raw reply, SHA-256).
        public Dictionary<string, object> provenance = new Dictionary<string, object>();

        public Intent() { }
        public Intent(string agentId, string action, Dictionary<string, object> parameters, string target)
        {
            agent_id = agentId;
            this.action = action;
            @params = parameters ?? new Dictionary<string, object>();
            target_resource = target;
        }

        static Dictionary<string, object> NewPipeline()
        {
            Dictionary<string, object> p = new Dictionary<string, object>();
            foreach (string s in Stages.All) p[s] = "pending";
            return p;
        }

        // The JSON key is "params" (a C# keyword, so the field is @params; reflection reports "params").
        public override object ToJson() { return Dump(); }
    }

    public sealed class Conflict : Model
    {
        public string id = "CF-" + Py.Hex(5).ToUpperInvariant();
        public string type;                    // resource objective oscillation cumulative
        public string severity = "medium";     // low medium high critical
        public List<object> intent_ids = new List<object>();
        public List<object> agent_ids = new List<object>();
        public string target_resource = "";
        public string summary = "";
        public string summary_source = "pending";   // live - local - cache - fallback (LLM wrapper)
        public string resolution = "";
        public string status = "open";         // open auto_resolved escalated resolved
        public int count = 1;                  // repeats of the same clash are folded into one record
        public double created_at = Py.Now();
        public double updated_at = Py.Now();
    }

    public sealed class Escalation : Model
    {
        public string id = "ESC-" + Py.Hex(5).ToUpperInvariant();
        public string kind = "intent";         // intent | system_check
        public string intent_id = "";
        public string agent_id;
        public string title;
        public string summary;
        public string summary_source = "pending";
        public string safe_default;
        public double opened_at = Py.Now();
        public double timeout_s = 45.0;
        public string status = "open";         // open approved rejected timed_out
        public string decided_by = "";
        public string domain = "global";       // nav | systems | dc | global (which COP shows it)
        public string approve_label = "Approve";
        public string reject_label = "Reject";
    }

    public sealed class Alert : Model
    {
        public string id = Py.Hex(6);
        public string severity = "warn";       // info warn critical
        public string source;
        public string message;
        public double at = Py.Now();
        public string domain = "global";       // nav | systems | dc | global
    }

    // domains.py: which operating picture a decision or alert belongs to.
    public static class Domains
    {
        static readonly Dictionary<string, string> ActionDomain = new Dictionary<string, string>
        {
            { "set_heading", "nav" }, { "set_speed", "nav" }, { "set_emcon", "nav" }, { "activate_defense", "nav" },
            { "close_breaker", "systems" }, { "open_breaker", "systems" },
            { "dispatch_team", "dc" }, { "set_gq", "global" }, { "engage_target", "nav" }, { "rig_casualty_power", "systems" },
        };
        static readonly Dictionary<string, string> SourceDomain = new Dictionary<string, string>
        {
            { "ew", "nav" }, { "defense", "nav" }, { "asw", "nav" }, { "ship-systems", "systems" }, { "damage-control", "dc" },
        };

        public static string ForAction(string action) { string d; return ActionDomain.TryGetValue(action ?? "", out d) ? d : "global"; }

        public static string ForAlert(string source, string message)
        {
            string d;
            if (SourceDomain.TryGetValue(source ?? "", out d)) return d;
            if (source == "monitor") return (message ?? "").StartsWith("Engine") ? "systems" : "nav";   // CPA, oscillation on helm/throttle
            return "global";             // quarantine, watch alarm, SAFE-STOP: the whole ship needs to know
        }
    }

    // What a model agent may SEE (its read_scopes). The C# lookouts read the whole picture; they report, they do not choose.
    public static class Scopes
    {
        static readonly Dictionary<string, string[]> ScopeKeys = new Dictionary<string, string[]>
        {
            { "cop.ownship", new[] { "ownship" } }, { "cop.contacts", new[] { "contacts" } },
            { "sensor.radar", new[] { "contacts" } }, { "sensor.ais", new[] { "contacts" } },
            { "cop.environment", new[] { "environment" } }, { "cop.route", new[] { "route_heading" } },
            { "sensor.engine", new[] { "ownship" } }, { "sensor.power", new[] { "ownship" } },
            { "ship.systems", new[] { "systems" } }, { "cop.threats", new[] { "systems", "contacts" } },
        };

        public static Dictionary<string, object> ScopedView(Dictionary<string, object> cop, IEnumerable<object> scopes)
        {
            HashSet<string> keys = new HashSet<string>();
            foreach (object s in scopes) { string[] k; if (ScopeKeys.TryGetValue(Py.Str(s), out k)) foreach (string x in k) keys.Add(x); }
            Dictionary<string, object> v = new Dictionary<string, object>();
            foreach (string k in keys) if (cop.ContainsKey(k)) v[k] = cop[k];
            v["sim_time"] = cop["sim_time"];
            return v;
        }
    }
}
