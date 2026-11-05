# LLM Role & Decision Boundary — Bridge Warden Design Principle

> **Principle:** The LLM suggests. The code checks. Humans or deterministic rules decide.
> Warden governs its own AI the same way it governs the ship's agents.

## 1. Why
- The challenge is to build a system that **governs AI** and keeps **human oversight**. If Warden's own LLM makes unchecked decisions, judges can ask: "What governs the governor?"
- Small local models (e.g. Gemma E4B) can be inconsistent between runs; deterministic code is predictable, testable and explainable.
- Safety-critical logic must be reproducible for replay, audit and testing.

## 2. Action required (review recent changes)
A recent change moved more decision-making onto the LLM. **Audit the codebase** and, for every place the LLM output directly causes an action, approval, rejection, escalation, authority change or actuator command:
- [ ] Convert the LLM output into a **proposal** (typed, structured object), not a final decision.
- [ ] Route the proposal through the **same policy engine, authority checks, constraint checks and conflict detection** used for agent intents.
- [ ] Ensure the code (or a human) makes the final decision.
- [ ] Log both the LLM proposal and the final decision in the audit ledger.

## 3. What the LLM MAY do
| Role | Example | Who decides |
|---|---|---|
| Explain | "Why?" panel, conflict summaries, incident reports | n/a (text only) |
| Summarise | Bundle escalation summaries (Oversight Capacity Governor) | n/a (text only) |
| Translate | Turn an agent's free-text recommendation into a typed Intent | Policy engine validates the Intent |
| Interpret operator language | "Restrict energy agent to advise-only" → proposed policy change | Operator confirms, then policy engine applies |
| Hypothesise | Suggest likely cause of an integrity anomaly (Disagreement-as-Signal) | Deterministic cross-checks confirm or reject |
| Recommend | Suggest a resolution for a conflict | Arbiter rules / operator decide |
| Propose policy tweaks | "CPA warning threshold may be too tight in harbour" | Human approves; can be tested via What-If replay first |

## 4. What the LLM must NEVER do
- Directly command actuators (rudder, engine, etc.).
- Approve or reject intents on its own.
- Change any agent's authority or autonomy level without confirmation.
- Override safety constraints, CPA thresholds or safe-stop.
- Calculate safety-critical numbers (CPA/TCPA, oversight capacity index, trust scores) — these are code.

## 5. Pipeline
```
LLM output
   │
   ▼
Parse into typed proposal (Pydantic schema)  ──► invalid? → reject, log, fall back to rule-based default
   │
   ▼
Policy engine: authority + constraints + conflicts + oversight capacity band
   │
   ├─► passes, within autonomy → execute (logged)
   ├─► needs approval          → escalate to operator (logged)
   └─► fails                   → reject (logged, with reason)
```

## 6. Implementation rules
- **Structured output only**: the LLM must return JSON matching a schema; free text is only for explanation fields.
- **Validate everything**: schema validation, value ranges, allowed action types, agent IDs that exist.
- **Fallback**: if the LLM is slow, fails, or returns invalid output, the system continues using rule-based logic. The LLM is never on the critical path for safety.
- **Timeouts**: hard timeout on LLM calls (e.g. 3–5 s); on timeout, use cached or rule-based output.
- **Treat the LLM as an agent**: register it in the agent registry with its own identity, read scopes and authority level ("advise-only"). Its proposals show in the UI and ledger like any other agent's.
- **Record outputs**: store LLM outputs in the ledger so Time-Travel Replay is deterministic (never re-call the LLM during replay).

## 7. Tests
- [ ] No code path lets LLM output reach an actuator without passing the policy engine.
- [ ] Invalid / malformed LLM output is rejected and the system falls back safely.
- [ ] LLM timeout does not block safety functions.
- [ ] An LLM proposal that violates authority or constraints is rejected with a logged reason.
- [ ] The LLM appears in the agent registry as advise-only.

## 8. Demo talking point
"Warden doesn't trust AI blindly, including its own. Its language model is registered as an advise-only agent,
and every suggestion it makes goes through the same checks and human sign-off as every other agent on the ship."
