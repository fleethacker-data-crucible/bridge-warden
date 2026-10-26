"""Plain-language explanations for operators, produced through the LLM wrapper (warden/llm.py).

All decisions are made by deterministic policy or a human, never by a language model.
These helpers only gather the facts for a named task and store the text plus its source
(live · local · cache · fallback) on the record, so the UI can show what produced it.
"""
from __future__ import annotations

from . import llm
from .models import Conflict, Escalation, Intent


def value(intent: Intent | None) -> str:
    if intent is None:
        return ""
    p = intent.params
    if "heading" in p:
        return f"heading {p['heading']:03.0f}°"
    if "speed_kn" in p:
        return f"speed {p['speed_kn']:.0f} kn"
    if intent.action == "engage_target":
        return f"fire {p.get('rounds', 2)} round(s) at {p.get('target')}"
    if intent.action == "set_gq":
        return "sound General Quarters"
    if intent.action == "set_emcon":
        return "set EMCON (silence emitters)" if p.get("on") else "lift EMCON"
    if intent.action == "activate_defense":
        return "weapons free: " + ", ".join(p.get("systems", []))
    if intent.action in ("close_breaker", "open_breaker"):
        return f"{'close' if intent.action == 'close_breaker' else 'open'} {p.get('breaker')}"
    if intent.action == "dispatch_team":
        b = p.get("boundaries") or ["?", "?"]
        return f"send {p.get('team')} to {p.get('compartment')}, boundaries at frames {b[0]} and {b[1]}"
    return ", ".join(f"{k}={v}" for k, v in p.items())


def describe(intent: Intent) -> str:
    return f"{intent.action} → {value(intent)}"


def _apply(record, result: llm.LLMResult) -> None:
    record.summary = result.text
    record.summary_source = result.source


def summarize_conflict(c: Conflict, winner: Intent | None, loser: Intent, wp: int | None, lp: int) -> None:
    inputs = {
        "kind": c.type, "resource": c.target_resource,
        "winner_agent": winner.agent_id if winner else "", "winner_objective": winner.objective if winner else "",
        "winner_value": value(winner), "winner_priority": wp if wp is not None else "",
        "loser_agent": loser.agent_id, "loser_objective": loser.objective, "loser_value": value(loser),
        "loser_priority": lp,
    }
    llm.run_background("summarize_conflict", inputs, lambda r: _apply(c, r))


def summarize_escalation(esc: Escalation, intent: Intent, why: list[str]) -> None:
    inputs = {"agent": intent.agent_id, "value": value(intent), "objective": intent.objective,
              "why": "; ".join(why), "rationale": intent.rationale}
    llm.run_background("summarize_escalation", inputs, lambda r: _apply(esc, r))


def intent_inputs(intent: Intent) -> dict:
    return {
        "agent": intent.agent_id, "action": intent.action, "value": value(intent),
        "stages": ", ".join(f"{k} {v}" for k, v in intent.pipeline.items() if v != "pending"),
        "status": intent.status, "reasons": "; ".join(intent.reasons) or "all checks passed",
        "decided_by": intent.decided_by or "pending",
    }


def intent_story(intent: Intent) -> llm.LLMResult:
    return llm.run("explain_intent", intent_inputs(intent))
