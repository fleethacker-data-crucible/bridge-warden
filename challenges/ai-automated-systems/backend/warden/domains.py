"""Which operating picture a decision or alert belongs to: nav, systems, dc, or global (shown everywhere)."""
from __future__ import annotations

ACTION_DOMAIN = {
    "set_heading": "nav", "set_speed": "nav", "set_emcon": "nav", "activate_defense": "nav",
    "close_breaker": "systems", "open_breaker": "systems",
    "dispatch_team": "dc", "set_gq": "global", "engage_target": "nav",
}
SOURCE_DOMAIN = {"ew": "nav", "defense": "nav", "asw": "nav", "ship-systems": "systems", "damage-control": "dc"}


def for_action(action: str) -> str:
    return ACTION_DOMAIN.get(action, "global")


def for_alert(source: str, message: str) -> str:
    if source in SOURCE_DOMAIN:
        return SOURCE_DOMAIN[source]
    if source == "monitor":
        if message.startswith("Engine"):
            return "systems"
        return "nav"            # CPA, oscillation on helm/throttle
    return "global"             # quarantine, watch alarm, SAFE-STOP: the whole ship needs to know
