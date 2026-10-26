// Port of warden/llm_tasks.py.
// Named LLM tasks for Bridge Warden (spec: docs/llm-wrapper.md).
//
// Each task defines a prompt template, its input fields, and fallback text built
// from the inputs without any AI model. The AI only explains; it never decides.
using System;
using System.Collections.Generic;
using System.Text;

namespace BridgeWarden
{
    public static class LlmTasks
    {
        static readonly string Arrow = ((char)0x2192).ToString();

        public static readonly string SYSTEM =
            "You explain decisions made by Bridge Warden, a governance layer for shipboard AI agents. " +
            "Decisions are already made by deterministic policy; never suggest a different decision. " +
            "Write for a watchstander: plain words, one or two short sentences, no preamble.";

        // Python's frozen dataclass Task. Nested so it never clashes with System.Threading.Tasks.Task.
        public sealed class Task
        {
            public readonly string name;
            public readonly string[] fields;
            public readonly string prompt;
            public readonly Func<Dictionary<string, object>, string> fallback;
            public readonly double timeout_s;     // longer budget than LLM_TIMEOUT_S, e.g. for local models thinking
            public readonly int max_tokens;
            public readonly string system;        // task-specific system prompt (defaults to SYSTEM)

            public Task(string name, string[] fields, string prompt, Func<Dictionary<string, object>, string> fallback,
                        double timeout_s = 0.0, int max_tokens = 0, string system = "")
            {
                this.name = name; this.fields = fields; this.prompt = prompt; this.fallback = fallback;
                this.timeout_s = timeout_s; this.max_tokens = max_tokens; this.system = system ?? "";
            }
        }

        // str.format(**inputs) for these templates: every {name} found in inputs becomes str(value).
        public static string Fill(string template, IDictionary<string, object> inputs)
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < template.Length)
            {
                char c = template[i];
                if (c == '{')
                {
                    int j = template.IndexOf('}', i + 1);
                    if (j > i)
                    {
                        string k = template.Substring(i + 1, j - i - 1);
                        object v;
                        if (inputs != null && inputs.TryGetValue(k, out v)) { sb.Append(Py.Str(v)); i = j + 1; continue; }
                    }
                }
                sb.Append(c);
                i++;
            }
            return sb.ToString();
        }

        static string S(Dictionary<string, object> i, string k) { return Py.Str(i[k]); }   // i[k] raises like Python

        public static string ConflictFallback(Dictionary<string, object> i)
        {
            if (Py.Str(i["kind"]) == "oscillation")
                return S(i, "resource") + " is flip-flopping between agents. Warden is damping by holding " +
                       "the higher-priority setting.";
            if (!Py.Truthy(Py.Get(i, "winner_agent")))
                return S(i, "loser_agent") + " wants " + S(i, "loser_value") + " but the resource is held by the operator.";
            return S(i, "winner_agent") + " (" + S(i, "winner_objective").ToLowerInvariant() + ") wants " + S(i, "winner_value") + "; " +
                   S(i, "loser_agent") + " (" + S(i, "loser_objective").ToLowerInvariant() + ") wants " + S(i, "loser_value") + ". " +
                   "Priority " + S(i, "winner_priority") + " beats " + S(i, "loser_priority") + ".";
        }

        public static string EscalationFallback(Dictionary<string, object> i)
        {
            return S(i, "agent") + " requests " + S(i, "value") + " to " + S(i, "objective").ToLowerInvariant() + ". " +
                   "Needs a human because: " + S(i, "why") + ". Agent rationale: " + S(i, "rationale");
        }

        public static string IntentFallback(Dictionary<string, object> i)
        {
            return S(i, "action") + " " + Arrow + " " + S(i, "value") + " from " + S(i, "agent") + ". Pipeline: " + S(i, "stages") + ". " +
                   "Outcome: " + S(i, "status") + " (" + S(i, "reasons") + "). Decided by " + S(i, "decided_by") + ".";
        }

        public static readonly string AGENT_SYSTEM =
            "You are one of several AI agents aboard a naval surface ship in an UNCLASSIFIED training simulation. " +
            "You read the ship's situation and propose at most 2 actions. You do not execute anything: a governance layer " +
            "checks every proposal against authority and safety rules and a human officer approves or rejects it. " +
            "Only propose actions from the allowed list, with exactly the parameter shapes shown. " +
            "Propose nothing if no action is clearly needed. " +
            "Reply with ONLY a JSON object: {\"actions\": [{\"action\": \"...\", \"params\": {...}, \"objective\": \"...\", " +
            "\"rationale\": \"...\", \"confidence\": 0.0-1.0}]}";

        public static readonly Dictionary<string, Task> TASKS = Build(
            new Task("summarize_conflict",
                new string[] { "kind", "resource", "winner_agent", "winner_objective", "winner_value", "winner_priority",
                               "loser_agent", "loser_objective", "loser_value", "loser_priority" },
                "Conflict type {kind} on {resource}. {winner_agent} (objective: {winner_objective}, priority " +
                "{winner_priority}) wants {winner_value}. {loser_agent} (objective: {loser_objective}, priority " +
                "{loser_priority}) wants {loser_value}. Summarize the clash and why the higher priority won.",
                ConflictFallback),
            new Task("summarize_escalation",
                new string[] { "agent", "value", "objective", "why", "rationale" },
                "{agent} requests {value} to {objective}. It needs operator approval because: {why}. " +
                "The agent's rationale: {rationale}. Summarize what the operator is being asked to decide " +
                "and the trade-off, without recommending an answer.",
                EscalationFallback),
            new Task("explain_intent",
                new string[] { "agent", "action", "value", "stages", "status", "reasons", "decided_by" },
                "{agent} asked for {action} " + Arrow + " {value}. Pipeline stage results: {stages}. Outcome: {status}. " +
                "Reasons: {reasons}. Decided by: {decided_by}. Explain what happened and why.",
                IntentFallback, 30.0),       // operator asked; streamed to the UI as it arrives
            new Task("agent_decide",
                new string[] { "role", "state", "tools" },
                "Your role: {role}\n\nCurrent situation (JSON):\n{state}\n\nAllowed actions and parameter shapes:\n{tools}\n\n" +
                "What, if anything, should be done now? Reply with the JSON object only.",
                delegate(Dictionary<string, object> i) { return "{\"actions\": []}"; }, 45.0, 600, AGENT_SYSTEM));

        static Dictionary<string, Task> Build(params Task[] tasks)
        {
            Dictionary<string, Task> d = new Dictionary<string, Task>();
            foreach (Task t in tasks) d[t.name] = t;
            return d;
        }
    }
}
