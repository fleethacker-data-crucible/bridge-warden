// Operator chat with the local model (phoenix-lai by default; Llm.cs picks the endpoint). C# 5, framework assemblies only.
//
// The operator asks about the ship, the agents and their decisions in plain English. Each turn the model gets a
// compact snapshot of the live picture (App.ChatContext: agents, open escalations and conflicts, recent orders and
// why, the ledger tail, ship and plant state) and answers from it.
//
// Chat never acts on its own. The model may PROPOSE operator actions from a short allow-list (ACTION lines, parsed
// here); the browser shows each as a card, and only the operator's click calls the same /api/ route the existing
// buttons use. So every chat action goes through Warden and the ledger exactly like a button press, and chat is
// never a way around governance. Each question and its proposals are also written to the ledger (type CHAT).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace BridgeWarden
{
    public static class Chat
    {
        public const double TIMEOUT_S = 60.0;
        public const int MAX_TOKENS = 700;
        public const int MAX_TURNS = 8;          // history sent to the model (user + assistant messages)
        public const int MAX_CHARS = 2000;       // per message
        public const int MAX_ACTIONS = 3;        // proposals per answer

        public const string SYSTEM =
            "You are the Bridge Warden assistant aboard a naval surface ship in an UNCLASSIFIED training simulation. " +
            "Bridge Warden governs the ship's AI agents: they propose orders, Warden checks every order against the rules, " +
            "and the human operator approves anything risky. You help the operator understand what is happening and why.\n\n" +
            "Rules:\n" +
            "- Answer ONLY from the SHIP SNAPSHOT below. If it does not say, answer that you cannot tell from the current picture. Never invent tracks, agents, numbers or events.\n" +
            "- Be brief and plain: a few short sentences or bullets. Name agents, tracks and orders exactly as the snapshot does.\n" +
            "- To explain a decision, use the RECENT ORDERS and LEDGER: who decided, the order, its status, and the reason given.\n" +
            "- You cannot change anything yourself, but you CAN propose operator actions. When the operator asks for one, propose it;\n" +
            "  the operator confirms it with a button. Write each proposal on its own line, exactly like:\n" +
            "  ACTION: pause_agent dco-01\n" +
            "  Every agent can be paused, resumed or quarantined in ANY status (idle, active, escalated); never refuse that. Use the agent id (the part before the colon in AGENTS),\n" +
            "  and pick the agent whose NAME matches what the operator said. The allowed actions, one per line:\n" +
            "  ACTION: pause_agent <agent_id>        stop the agent proposing orders until resumed\n" +
            "  ACTION: resume_agent <agent_id>       let a paused agent run again\n" +
            "  ACTION: quarantine_agent <agent_id>   isolate a misbehaving agent (stronger than pause)\n" +
            "  ACTION: set_autonomy <agent_id> <0-3> 0 advise, 1 recommend, 2 act with approval, 3 act\n" +
            "  ACTION: promote_agent <agent_id>      field promotion: raise its rank for a few minutes so it wins contested actuators\n" +
            "  ACTION: demote_agent <agent_id>       revoke a field promotion\n" +
            "  ACTION: rollback_last [agent_id]      undo the last executed order (of that agent)\n" +
            "  ACTION: safe_stop                     pause all agents, hold course at safe speed\n" +
            "  ACTION: resume_all                    resume all agents after a safe stop\n" +
            "  ACTION: cease_fire                    stop every weapon in flight and hold fire\n" +
            "  ACTION: general_quarters on|off\n" +
            "  ACTION: auto_act on|off               on = agents act without asking (Warden still checks)\n" +
            "  In the text, say in one sentence what the action will do. At most 3 ACTION lines. Weapons are never fired from chat.";

        // ------------------------------------------------------------------ history from the browser
        // Keeps the last MAX_TURNS user/assistant messages, each cut to MAX_CHARS; must end with a user message.
        public static List<object> History(object raw)
        {
            List<object> outl = new List<object>();
            foreach (object m in Py.ListOrEmpty(raw))
            {
                Dictionary<string, object> d = m as Dictionary<string, object>;
                if (d == null) continue;
                string role = Py.StrOr(Py.Get(d, "role"), "");
                string content = Py.StrOr(Py.Get(d, "content"), "").Trim();
                if ((role != "user" && role != "assistant") || content.Length == 0) continue;
                outl.Add(Py.D("role", role, "content", Py.Cut(content, MAX_CHARS)));
            }
            if (outl.Count > MAX_TURNS) outl = outl.Skip(outl.Count - MAX_TURNS).ToList();
            while (outl.Count > 0 && Py.Str(((Dictionary<string, object>)outl[0])["role"]) != "user") outl.RemoveAt(0);
            if (outl.Count == 0 || Py.Str(((Dictionary<string, object>)outl[outl.Count - 1])["role"]) != "user") return new List<object>();
            return outl;
        }

        public static List<object> Messages(string context, List<object> history, IDictionary<string, string> agents)
        {
            List<object> msgs = new List<object>();
            msgs.Add(Py.D("role", "system", "content", SYSTEM + "\n\n# SHIP SNAPSHOT (now)\n" + context));
            msgs.AddRange(history);
            return msgs;
        }

        // ------------------------------------------------------------------ proposed actions
        public sealed class Proposal
        {
            public string verb, label, method, url;
            public Dictionary<string, object> body;
            public Dictionary<string, object> Dump()
            {
                return Py.D("verb", verb, "label", label, "method", method, "url", url, "body", body);
            }
        }

        public sealed class Reply
        {
            public string text = "";
            public List<Proposal> actions = new List<Proposal>();
            public List<string> ignored = new List<string>();     // ACTION lines that were not on the allow-list
            public List<object> Proposals() { return actions.Select(a => (object)a.Dump()).ToList(); }
        }

        static readonly Regex ACTION_LINE = new Regex(@"^\s*[-*>`]*\s*ACTION\s*:\s*(.+?)\s*`*\s*$", RegexOptions.IgnoreCase);

        // Splits the model's answer into the text to show and the allowed, validated proposals.
        public static Reply Parse(string text, IDictionary<string, string> agents)
        {
            Reply r = new Reply();
            StringBuilder kept = new StringBuilder();
            foreach (string line in (text ?? "").Replace("\r", "").Split('\n'))
            {
                Match m = ACTION_LINE.Match(line);
                if (!m.Success) { kept.Append(line).Append('\n'); continue; }
                string spec = m.Groups[1].Value.Trim();
                Proposal p = Build(spec, agents);
                if (p == null) { r.ignored.Add(spec); continue; }
                if (r.actions.Count < MAX_ACTIONS && !r.actions.Any(a => a.url == p.url && a.method == p.method)) r.actions.Add(p);
            }
            r.text = kept.ToString().Trim();
            if (r.text.Length == 0 && r.actions.Count > 0) r.text = "Proposed:";
            return r;
        }

        // A direct operator command ("pause the Damage Control Officer", "resume wx-01") read without the model: the
        // verb plus the agent whose id or LONGEST name appears in the message ("Damage Control Officer" beats "Damage
        // Control"). Local models mix up similar agent names; this keeps the card on the agent the operator named.
        // Commands only (the message starts with the verb), so "why did it resume?" never makes a card.
        static readonly Regex DIRECT = new Regex(@"^\s*(?:(?:please|ok|okay|now)[\s,]+)?(?:(?:can|could|would|will) you\s+)?(?:go ahead and\s+)?(pause|resume|release|quarantine|isolate|promote)\b",
                                                 RegexOptions.IgnoreCase);

        public static Proposal Direct(string question, IDictionary<string, string> agents)
        {
            string q = (question ?? "").ToLowerInvariant();
            Match m = DIRECT.Match(q);
            if (!m.Success) return null;
            string id = null;
            int best = 0;
            foreach (KeyValuePair<string, string> kv in agents)
            {
                string idl = kv.Key.ToLowerInvariant(), name = (kv.Value ?? "").ToLowerInvariant();
                if (Regex.IsMatch(q, @"(^|[^a-z0-9-])" + Regex.Escape(idl) + @"($|[^a-z0-9-])") && idl.Length + 1000 > best) { id = kv.Key; best = idl.Length + 1000; }
                else if (name.Length > 0 && q.Contains(name) && name.Length > best) { id = kv.Key; best = name.Length; }
            }
            if (id == null) return null;
            string w = m.Groups[1].Value.ToLowerInvariant();
            string verb = w == "pause" ? "pause_agent" : w == "quarantine" || w == "isolate" ? "quarantine_agent" : w == "promote" ? "promote_agent" : "resume_agent";
            return Build(verb + " " + id, agents);
        }

        // A direct command the model got right keeps the model's answer. One it missed (wrong agent, or a refusal the
        // rules never make) means the model misread the request: its text and proposals are replaced by the command
        // itself, stated plainly, so the words never contradict the card.
        public static void ApplyDirect(Reply r, string question, IDictionary<string, string> agents)
        {
            Proposal d = Direct(question, agents);
            if (d == null) return;
            if (r.actions.Any(a => a.url == d.url && a.method == d.method))
            {
                r.actions.RemoveAll(a => a.verb == d.verb && a.url != d.url);
                return;
            }
            r.actions.Clear();
            r.actions.Add(d);
            r.text = "Ready: " + d.label + ". Confirm below to apply it; Warden records it in the ledger.";
        }

        static string AgentLabel(string id, IDictionary<string, string> agents)
        {
            string name;
            return agents.TryGetValue(id, out name) && !string.IsNullOrEmpty(name) ? name + " (" + id + ")" : id;
        }

        // One ACTION spec -> a proposal for an existing operator route, or null when it is not allowed or malformed.
        public static Proposal Build(string spec, IDictionary<string, string> agents)
        {
            string[] t = spec.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) return null;
            string verb = t[0].ToLowerInvariant();
            string a1 = t.Length > 1 ? t[1].Trim('"', '\'', '`', '.') : null;
            string a2 = t.Length > 2 ? t[2].Trim('"', '\'', '`', '.') : null;
            Func<string, bool> isAgent = delegate(string id) { return id != null && agents.ContainsKey(id); };
            Func<string, string, string, Dictionary<string, object>, Proposal> P = delegate(string label, string method, string url, Dictionary<string, object> body)
            {
                Proposal p = new Proposal();
                p.verb = verb; p.label = label; p.method = method; p.url = url; p.body = body;
                return p;
            };
            switch (verb)
            {
                case "pause_agent":
                    return isAgent(a1) ? P("Pause " + AgentLabel(a1, agents), "POST", "/api/agents/" + a1 + "/pause", null) : null;
                case "resume_agent":
                    return isAgent(a1) ? P("Resume " + AgentLabel(a1, agents), "POST", "/api/agents/" + a1 + "/resume", null) : null;
                case "quarantine_agent":
                    return isAgent(a1) ? P("Quarantine " + AgentLabel(a1, agents), "POST", "/api/agents/" + a1 + "/quarantine", null) : null;
                case "promote_agent":
                    return isAgent(a1) ? P("Field-promote " + AgentLabel(a1, agents) + " (higher rank for a few minutes)", "POST", "/api/agents/" + a1 + "/promote", Py.D()) : null;
                case "demote_agent":
                    return isAgent(a1) ? P("Revoke the field promotion of " + AgentLabel(a1, agents), "POST", "/api/agents/" + a1 + "/demote", Py.D()) : null;
                case "set_autonomy":
                {
                    int lvl;
                    if (!isAgent(a1) || a2 == null || !int.TryParse(a2, NumberStyles.Integer, CultureInfo.InvariantCulture, out lvl) || lvl < 0 || lvl > 3) return null;
                    string[] names = { "Advise", "Recommend", "Act w/ Approval", "Act" };
                    return P("Set " + AgentLabel(a1, agents) + " autonomy to " + lvl + " (" + names[lvl] + ")", "PATCH",
                        "/api/agents/" + a1 + "/autonomy", Py.D("level", lvl));
                }
                case "rollback_last":
                    if (a1 != null && !isAgent(a1)) return null;
                    return P(a1 == null ? "Roll back the last executed order" : "Roll back the last order by " + AgentLabel(a1, agents),
                        "POST", "/api/control/rollback", a1 == null ? Py.D("count", 1) : Py.D("agent_id", a1, "count", 1));
                case "safe_stop":
                    return P("SAFE-STOP: pause all agents and hold course at safe speed", "POST", "/api/control/safe-stop", null);
                case "resume_all":
                    return P("Resume all agents", "POST", "/api/control/resume-all", null);
                case "cease_fire":
                    return P("CEASE FIRE: stop every weapon in flight and hold fire", "POST", "/api/control/cease-fire", null);
                case "general_quarters":
                    if (a1 != "on" && a1 != "off") return null;
                    return P(a1 == "on" ? "Sound General Quarters" : "Secure from General Quarters", "POST", "/api/control/gq/" + a1, null);
                case "auto_act":
                    if (a1 != "on" && a1 != "off") return null;
                    return P(a1 == "on" ? "Let agents act without asking (Warden checks still apply)" : "Make every governed action wait for approval",
                        "POST", "/api/control/auto-act/" + a1, null);
            }
            return null;
        }
    }
}
