"""Named LLM tasks for Bridge Warden (spec: docs/llm-wrapper.md).

Each task defines a prompt template, its input fields, and fallback text built
from the inputs without any AI model. The AI only explains; it never decides.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Callable

SYSTEM = ("You explain decisions made by Bridge Warden, a governance layer for shipboard AI agents. "
          "Decisions are already made by deterministic policy; never suggest a different decision. "
          "Write for a watchstander: plain words, one or two short sentences, no preamble.")


@dataclass(frozen=True)
class Task:
    name: str
    fields: tuple[str, ...]
    prompt: str
    fallback: Callable[[dict], str]
    timeout_s: float = 0.0       # longer budget than LLM_TIMEOUT_S, e.g. for local models thinking
    max_tokens: int = 0
    system: str = ""             # task-specific system prompt (defaults to SYSTEM)


def _conflict_fallback(i: dict) -> str:
    if i["kind"] == "oscillation":
        return (f"{i['resource']} is flip-flopping between agents. Warden is damping by holding "
                f"the higher-priority setting.")
    if not i.get("winner_agent"):
        return f"{i['loser_agent']} wants {i['loser_value']} but the resource is held by the operator."
    return (f"{i['winner_agent']} ({i['winner_objective'].lower()}) wants {i['winner_value']}; "
            f"{i['loser_agent']} ({i['loser_objective'].lower()}) wants {i['loser_value']}. "
            f"Priority {i['winner_priority']} beats {i['loser_priority']}.")


def _escalation_fallback(i: dict) -> str:
    return (f"{i['agent']} requests {i['value']} to {i['objective'].lower()}. "
            f"Needs a human because: {i['why']}. Agent rationale: {i['rationale']}")


def _intent_fallback(i: dict) -> str:
    return (f"{i['action']} → {i['value']} from {i['agent']}. Pipeline: {i['stages']}. "
            f"Outcome: {i['status']} ({i['reasons']}). Decided by {i['decided_by']}.")


AGENT_SYSTEM = (
    "You are one of several AI agents aboard a naval surface ship in an UNCLASSIFIED training simulation. "
    "You read the ship's situation and propose at most 2 actions. You do not execute anything: a governance layer "
    "checks every proposal against authority and safety rules and a human officer approves or rejects it. "
    "Only propose actions from the allowed list, with exactly the parameter shapes shown. "
    "Propose nothing if no action is clearly needed. "
    'Reply with ONLY a JSON object: {"actions": [{"action": "...", "params": {...}, "objective": "...", '
    '"rationale": "...", "confidence": 0.0-1.0}]}')

TASKS: dict[str, Task] = {t.name: t for t in [
    Task("summarize_conflict",
         ("kind", "resource", "winner_agent", "winner_objective", "winner_value", "winner_priority",
          "loser_agent", "loser_objective", "loser_value", "loser_priority"),
         "Conflict type {kind} on {resource}. {winner_agent} (objective: {winner_objective}, priority "
         "{winner_priority}) wants {winner_value}. {loser_agent} (objective: {loser_objective}, priority "
         "{loser_priority}) wants {loser_value}. Summarize the clash and why the higher priority won.",
         _conflict_fallback),
    Task("summarize_escalation",
         ("agent", "value", "objective", "why", "rationale"),
         "{agent} requests {value} to {objective}. It needs operator approval because: {why}. "
         "The agent's rationale: {rationale}. Summarize what the operator is being asked to decide "
         "and the trade-off, without recommending an answer.",
         _escalation_fallback),
    Task("explain_intent",
         ("agent", "action", "value", "stages", "status", "reasons", "decided_by"),
         "{agent} asked for {action} → {value}. Pipeline stage results: {stages}. Outcome: {status}. "
         "Reasons: {reasons}. Decided by: {decided_by}. Explain what happened and why.",
         _intent_fallback, timeout_s=30.0),       # operator asked; streamed to the UI as it arrives
    Task("agent_decide",
         ("role", "state", "tools"),
         "Your role: {role}\n\nCurrent situation (JSON):\n{state}\n\nAllowed actions and parameter shapes:\n{tools}\n\n"
         "What, if anything, should be done now? Reply with the JSON object only.",
         lambda i: '{"actions": []}', timeout_s=45.0, max_tokens=600, system=AGENT_SYSTEM),
]}
