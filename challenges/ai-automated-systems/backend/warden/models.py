"""Typed messages shared by the core, the agents and the UI."""
from __future__ import annotations

import time
import uuid
from typing import Literal

from pydantic import BaseModel, Field

AgentStatus = Literal["idle", "active", "escalated", "paused", "quarantined", "offline"]
IntentStatus = Literal["pending", "approved", "rejected", "escalated", "executed",
                       "rolled_back", "expired", "advisory"]
StageResult = Literal["pass", "fail", "skip", "pending"]

STAGES = ["authenticate", "authority", "constraints", "quota",
          "conflicts", "arbitrate", "approve", "execute"]

# Autonomy levels: 0 advise · 1 recommend · 2 act-with-approval · 3 act
AUTONOMY_LABELS = {0: "ADVISE", 1: "RECOMMEND", 2: "ACT W/ APPROVAL", 3: "ACT"}


class AgentIdentity(BaseModel):
    id: str
    name: str
    role: str
    version: str = "1.0"
    vendor: str = ""
    status: AgentStatus = "idle"
    autonomy_level: int = 1
    priority: int = 0
    read_scopes: list[str] = []
    tools: list[str] = []
    action_limits: dict = {}
    resource_quota: dict = {}
    trust_score: float = 100.0
    heartbeat_at: float = Field(default_factory=lambda: time.time())
    description: str = ""
    behavior: str = ""
    cop: str = "nav"            # nav | systems | dc: the agent only runs while its COP is on screen          # simulator only: which built-in behavior drives this agent


class Intent(BaseModel):
    id: str = Field(default_factory=lambda: uuid.uuid4().hex[:8])
    agent_id: str
    kind: Literal["recommend", "act"] = "act"
    action: str
    params: dict = {}
    target_resource: str
    objective: str = ""
    rationale: str = ""
    confidence: float = 0.8
    created_at: float = Field(default_factory=lambda: time.time())
    sim_time: float = 0.0
    reversible: bool = True
    inverse: dict | None = None
    status: IntentStatus = "pending"
    pipeline: dict[str, StageResult] = Field(default_factory=lambda: {s: "pending" for s in STAGES})
    reasons: list[str] = []
    policy_hits: list[str] = []
    conflict_ids: list[str] = []
    decided_by: str = ""
    needs_approval: bool = False
    # The model's risk call (risk to ship, risk to crew, effect on the ship's ability to fight), and an optional
    # second option it offers the operator: {"action", "params", "rationale"}.
    risk: dict = {}
    alternative: dict | None = None
    preapproved_by: str = ""      # the operator picked this (option B) from a decision card
    # Proof the decision came from the language model: call id, model, tool calls + results, calculations,
    # raw reply and its SHA-256. Agent intents without a registered call id are rejected.
    provenance: dict = {}


class Conflict(BaseModel):
    id: str = Field(default_factory=lambda: "CF-" + uuid.uuid4().hex[:5].upper())
    type: Literal["resource", "objective", "oscillation", "cumulative"]
    severity: Literal["low", "medium", "high", "critical"] = "medium"
    intent_ids: list[str] = []
    agent_ids: list[str] = []
    target_resource: str = ""
    summary: str = ""
    summary_source: str = "pending"     # live · local · cache · fallback (LLM wrapper)
    resolution: str = ""
    status: Literal["open", "auto_resolved", "escalated", "resolved"] = "open"
    count: int = 1                  # repeats of the same clash are folded into one record
    created_at: float = Field(default_factory=lambda: time.time())
    updated_at: float = Field(default_factory=lambda: time.time())


class Escalation(BaseModel):
    id: str = Field(default_factory=lambda: "ESC-" + uuid.uuid4().hex[:5].upper())
    kind: Literal["intent", "system_check"] = "intent"
    intent_id: str = ""
    agent_id: str
    title: str
    summary: str
    summary_source: str = "pending"
    safe_default: str
    opened_at: float = Field(default_factory=lambda: time.time())
    timeout_s: float = 45.0
    status: Literal["open", "approved", "rejected", "timed_out"] = "open"
    decided_by: str = ""
    domain: str = "global"            # nav | systems | dc | global (which COP shows it)
    approve_label: str = "Approve"
    reject_label: str = "Reject"


class Alert(BaseModel):
    id: str = Field(default_factory=lambda: uuid.uuid4().hex[:6])
    severity: Literal["info", "warn", "critical"] = "warn"
    source: str
    message: str
    at: float = Field(default_factory=lambda: time.time())
    domain: str = "global"            # nav | systems | dc | global
