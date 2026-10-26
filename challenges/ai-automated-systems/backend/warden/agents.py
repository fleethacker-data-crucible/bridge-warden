"""Agent scoping. Agents decide nothing in Python: every decision comes from the
language model (see llm_agent.py). This module only controls what each agent may SEE.
"""
from __future__ import annotations

from .models import AgentIdentity

# Which COP keys each read scope unlocks.
SCOPE_KEYS = {
    "cop.ownship": ["ownship"],
    "cop.contacts": ["contacts"],
    "sensor.radar": ["contacts"],
    "sensor.ais": ["contacts"],
    "cop.environment": ["environment"],
    "cop.route": ["route_heading"],
    "sensor.engine": ["ownship"],
    "sensor.power": ["ownship"],
    "ship.systems": ["systems"],
    "cop.threats": ["systems", "contacts"],
}


def scoped_view(cop: dict, scopes: list[str]) -> dict:
    keys = {k for s in scopes for k in SCOPE_KEYS.get(s, [])}
    return {k: cop[k] for k in keys if k in cop} | {"sim_time": cop["sim_time"]}


class AgentRuntime:
    """Base runtime: identity plus the compromised flag a fault injection can set."""

    def __init__(self, identity: AgentIdentity):
        self.identity = identity
        self.rogue = False

    def propose(self, view: dict, tick: int, policies: dict) -> list:
        return []
