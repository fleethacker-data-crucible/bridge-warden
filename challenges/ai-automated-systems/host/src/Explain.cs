// Port of warden/explain.py.
// Plain-language explanations for operators, produced through the LLM wrapper (Llm.cs).
//
// All decisions are made by deterministic policy or a human, never by a language model.
// These helpers only gather the facts for a named task and store the text plus its source
// (live - local - cache - fallback) on the record, so the UI can show what produced it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace BridgeWarden
{
    public static class Explain
    {
        static readonly string Arrow = ((char)0x2192).ToString();
        static readonly string Degree = ((char)0x00B0).ToString();

        // f"{x:.0f}" (round half to even, like Python)
        static string F0(double x) { return Py.Round(x).ToString("F0", CultureInfo.InvariantCulture); }

        // f"{x:03.0f}"
        static string F03(double x)
        {
            string s = F0(x);
            if (s.StartsWith("-")) return "-" + s.Substring(1).PadLeft(2, '0');
            return s.PadLeft(3, '0');
        }

        public static string Value(Intent intent)
        {
            if (intent == null) return "";
            Dictionary<string, object> p = intent.@params ?? new Dictionary<string, object>();
            if (p.ContainsKey("heading"))
                return "heading " + F03(Py.Num(p["heading"])) + Degree;
            if (p.ContainsKey("speed_kn"))
                return "speed " + F0(Py.Num(p["speed_kn"])) + " kn";
            if (intent.action == "engage_target")
                return "fire " + Py.Str(Py.Get(p, "rounds", 2)) + " round(s) at " + Py.Str(Py.Get(p, "target"));
            if (intent.action == "set_gq")
                return "sound General Quarters";
            if (intent.action == "set_emcon")
                return Py.Truthy(Py.Get(p, "on")) ? "set EMCON (silence emitters)" : "lift EMCON";
            if (intent.action == "activate_defense")
                return "weapons free: " + Py.Join(", ", Py.ListOrEmpty(Py.Get(p, "systems", new List<object>())));
            if (intent.action == "close_breaker" || intent.action == "open_breaker")
                return (intent.action == "close_breaker" ? "close" : "open") + " " + Py.Str(Py.Get(p, "breaker"));
            if (intent.action == "dispatch_team")
            {
                object bo = Py.Get(p, "boundaries");
                List<object> b = Py.Truthy(bo) ? Py.List(bo) : Py.L("?", "?");
                return "send " + Py.Str(Py.Get(p, "team")) + " to " + Py.Str(Py.Get(p, "compartment")) +
                       ", boundaries at frames " + Py.Str(b[0]) + " and " + Py.Str(b[1]);
            }
            List<string> kv = new List<string>();
            foreach (KeyValuePair<string, object> e in p) kv.Add(e.Key + "=" + Py.Str(e.Value));
            return string.Join(", ", kv.ToArray());
        }

        public static string Describe(Intent intent)
        {
            return intent.action + " " + Arrow + " " + Value(intent);
        }

        // record.summary = result.text; record.summary_source = result.source (Conflict or Escalation)
        static void Apply(Model record, LLMResult result)
        {
            Type t = record.GetType();
            FieldInfo summary = t.GetField("summary"), source = t.GetField("summary_source");
            if (summary != null) summary.SetValue(record, result.text);
            if (source != null) source.SetValue(record, result.source);
        }

        // wp null = None
        public static void SummarizeConflict(Conflict c, Intent winner, Intent loser, int? wp, int lp)
        {
            Dictionary<string, object> inputs = Py.D(
                "kind", c.type, "resource", c.target_resource,
                "winner_agent", winner != null ? winner.agent_id : "", "winner_objective", winner != null ? winner.objective : "",
                "winner_value", Value(winner), "winner_priority", wp.HasValue ? (object)wp.Value : "",
                "loser_agent", loser.agent_id, "loser_objective", loser.objective, "loser_value", Value(loser),
                "loser_priority", lp);
            Llm.RunBackground("summarize_conflict", inputs, delegate(LLMResult r) { Apply(c, r); });
        }

        public static void SummarizeEscalation(Escalation esc, Intent intent, IEnumerable<object> why)
        {
            Dictionary<string, object> inputs = Py.D("agent", intent.agent_id, "value", Value(intent), "objective", intent.objective,
                "why", Py.Join("; ", why), "rationale", intent.rationale);
            Llm.RunBackground("summarize_escalation", inputs, delegate(LLMResult r) { Apply(esc, r); });
        }

        public static Dictionary<string, object> IntentInputs(Intent intent)
        {
            List<string> stages = new List<string>();
            foreach (KeyValuePair<string, object> e in intent.pipeline)
                if (Py.Str(e.Value) != "pending") stages.Add(e.Key + " " + Py.Str(e.Value));
            string reasons = Py.Join("; ", intent.reasons);
            return Py.D(
                "agent", intent.agent_id, "action", intent.action, "value", Value(intent),
                "stages", string.Join(", ", stages.ToArray()),
                "status", intent.status, "reasons", reasons.Length > 0 ? reasons : "all checks passed",
                "decided_by", string.IsNullOrEmpty(intent.decided_by) ? "pending" : intent.decided_by);
        }

        public static LLMResult IntentStory(Intent intent)
        {
            return Llm.Run("explain_intent", IntentInputs(intent));
        }
    }
}
