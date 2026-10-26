"""Warden core: registry, governance pipeline, conflicts, escalations, monitors, controls.

Every agent action arrives as an Intent and passes 8 stages:
  authenticate → authority → constraints → quota → conflicts → arbitrate → approve → execute
Only Warden writes to the (simulated) actuators.
"""
from __future__ import annotations

import re
import time
from collections import deque
from pathlib import Path
from typing import Callable

import yaml

from . import domains, explain, llm
from .agents import AgentRuntime, scoped_view
from .llm_agent import CALLS as LLM_CALLS
from .llm_agent import _target as _target_of
from .llm_agent import record_feedback
from .llm_agent import LLMAgentRuntime
from .ledger import Ledger
from .models import STAGES, AgentIdentity, Alert, Conflict, Escalation, Intent
from .world import World, angle_diff, wrap360

COP_RANGE_NM = 20.0         # the Navigation COP's display range
# Who each agent reports to, and what the decision buttons say, in watch-station language.
STATION = {"collision": "OOD, Collision Avoidance", "navigation": "OOD, Navigation", "weather": "OOD, Weather Routing",
           "defense": "TAO, Combat Systems", "asw": "TAO, Undersea Warfare", "ew": "TAO, EW", "engine": "EOOW, Main Engine",
           "energy": "EOOW, Fuel Economy", "power": "EOOW, Electrical", "damage_control": "DCA, Repair"}
BUTTONS = {"activate_defense": ("Weapons Free", "Hold Fire"), "set_emcon": ("Set EMCON", "Remain Radiating"),
           "dispatch_team": ("Send Team", "Hold Team"), "close_breaker": ("Close Breaker", "Leave Open"),
           "open_breaker": ("Open Breaker", "Leave Closed"), "set_heading": ("Approve Course", "Deny"),
           "set_gq": ("Sound GQ", "Remain Cond III"), "engage_target": ("Batteries Release", "Hold Fire"),
           "set_speed": ("Approve Speed", "Deny")}
HOLD_S = 20.0               # seconds a resource stays held by the agent that last set it
HOLD_SAFETY_S = 45.0        # longer hold for safety-critical agents (priority >= 80)
OPERATOR_PRIORITY = 1000
STATUS_ACTIVE_S = 8.0


class Warden:
    def __init__(self, config_dir: Path, ledger: Ledger, world: World, emit: Callable[[str, str, str], None]):
        self.config_dir = config_dir
        self.ledger = ledger
        self.world = world
        # Every AI call is audited: task, source, provider, model, latency, success (no prompts, no keys).
        llm.set_logger(lambda r: self.ledger.append("LLM_CALL", "llm", r))
        self.emit = emit                      # emit(level, source, message) → live log
        self.reset()

    # ------------------------------------------------------------------ setup
    def reset(self) -> None:
        self.policies = yaml.safe_load((self.config_dir / "policies.yaml").read_text(encoding="utf-8"))
        agents = yaml.safe_load((self.config_dir / "agents.yaml").read_text(encoding="utf-8"))["agents"]
        self.agents: dict[str, AgentRuntime] = {
            a["id"]: LLMAgentRuntime(AgentIdentity(**a), self.world) for a in agents}   # every agent is an LLM role
        LLMAgentRuntime.on_decision = self._log_decision
        self.intents: dict[str, Intent] = {}
        self.conflicts: list[Conflict] = []
        self.escalations: dict[str, Escalation] = {}
        self.auto_act = bool(self.policies.get("auto_act", False))   # operator lets agents act without asking
        self.hold_fire = False                                         # operator's CEASE FIRE in effect
        self.alerts: deque[Alert] = deque(maxlen=50)
        self.holds: dict[str, dict] = {}
        self.exec_history: dict[str, deque] = {}
        self.executed: list[str] = []
        self.violations: dict[str, deque] = {a: deque() for a in self.agents}
        self.quota_log: dict[str, deque] = {a: deque() for a in self.agents}
        self.last_seen: dict[str, float] = {}
        self.safe_stop = False
        self.paused_by_safe_stop: set[str] = set()
        self.alerted: set[str] = set()
        self.tick = 0
        self.stats = {"submitted": 0, "executed": 0, "rejected": 0, "escalated": 0, "rollbacks": 0}
        self.started_at = time.time()
        # Periodic system check (bridge watch alarm). Armed when operations start.
        self.checks_enabled = False
        self.next_check = 0.0
        self.checks_missed = 0

    def _alert(self, severity: str, source: str, message: str) -> None:
        self.alerts.appendleft(Alert(severity=severity, source=source, message=message,
                                     domain=domains.for_alert(source, message)))
        self.ledger.append("ALERT", source, {"severity": severity, "message": message})
        self.emit("error" if severity == "critical" else "warn", source, message)

    # ------------------------------------------------------------------ tick
    def step(self, active: set[str] | None = None, dt: float = 1.0) -> None:
        """One governance cycle: agents propose, Warden decides, monitors watch. Only the COP on screen runs."""
        self.tick += 1
        now = time.time()
        on = active or {"nav", "systems", "dc"}
        for esc in self.escalations.values():   # decisions on a hidden COP don't time out while hidden
            if esc.status == "open" and esc.domain not in on and esc.domain != "global":
                esc.opened_at += dt
        cop = self.world.cop()
        batch: list[Intent] = []
        for rt in self.agents.values():
            ident = rt.identity
            if ident.status in ("paused", "quarantined", "offline") or ident.cop not in on:
                continue
            ident.heartbeat_at = now
            for it in rt.propose(scoped_view(cop, ident.read_scopes), self.tick, self.policies):
                it.sim_time = self.world.sim_time
                batch.append(it)
        if batch:
            self.process(batch)
        self._check_escalation_timeouts(now)
        self._maybe_system_check(now)
        self._monitors(now)
        self._update_statuses(now)

    # ------------------------------------------------------------------ pipeline
    def process(self, batch: list[Intent]) -> None:
        survivors = []
        for it in batch:
            self.intents[it.id] = it
            self.stats["submitted"] += 1
            self.last_seen[it.agent_id] = time.time()
            self.ledger.append("INTENT_SUBMITTED", it.agent_id, {"intent": it.id, "action": it.action,
                                                                   "params": it.params, "target": it.target_resource})
            if self._stage_authenticate(it) and self._stage_authority(it) and \
               self._stage_constraints(it) and self._stage_quota(it):
                survivors.append(it)
        self._trim_intents()

        # Conflicts and arbitration run on the whole batch, grouped by resource.
        by_resource: dict[str, list[Intent]] = {}
        for it in survivors:
            by_resource.setdefault(it.target_resource, []).append(it)
        for resource, group in by_resource.items():
            for winner in self._stage_conflicts_and_arbitrate(resource, group):
                if self.agents[winner.agent_id].identity.status in ("paused", "quarantined"):
                    self._reject(winner, "approve", "Agent was contained during evaluation")
                    continue
                if any(e.status == "open" and e.agent_id == winner.agent_id and
                       self.intents.get(e.intent_id) and self.intents[e.intent_id].target_resource == winner.target_resource
                       for e in self.escalations.values()):
                    self._reject(winner, "approve", "Already awaiting an operator decision on this action")
                    continue
                self._stage_approve(winner)

    def _pass(self, it: Intent, stage: str) -> None:
        it.pipeline[stage] = "pass"

    def _reject(self, it: Intent, stage: str, reason: str, policy: str | None = None,
                violation: bool = False, severity: str = "high") -> bool:
        it.pipeline[stage] = "fail"
        for s in STAGES[STAGES.index(stage) + 1:]:
            it.pipeline[s] = "skip"
        it.status = "rejected"
        it.reasons.append(reason)
        it.decided_by = it.decided_by or "warden"
        if policy:
            it.policy_hits.append(policy)
        self.stats["rejected"] += 1
        rt = self.agents.get(it.agent_id)
        if rt:
            rt.identity.trust_score = max(0.0, rt.identity.trust_score - (10 if violation else 1))
        self.ledger.append("DECISION", "warden", {"intent": it.id, "outcome": "rejected", "stage": stage,
                                                  "reason": reason, "policy": policy})
        self.emit("error" if violation else "warn", it.agent_id,
                  f"REJECTED {explain.describe(it)} at {stage}: {reason}")
        if violation:
            self._record_violation(it.agent_id, reason, severity)
        return False

    def _log_decision(self, agent_id: str, prov: dict, actions: list[str]) -> None:
        """Every model decision is ledgered, including 'no action', with the hash of what the model actually said."""
        self.ledger.append("LLM_DECISION", agent_id, {
            "call_id": prov["call_id"], "model": prov["model"], "rounds": prov["rounds"],
            "tools": [t["tool"] for t in prov["tools"]], "reply_sha256": prov["reply_sha256"], "actions": actions})

    def _stage_authenticate(self, it: Intent) -> bool:
        rt = self.agents.get(it.agent_id)
        if rt is None:
            return self._reject(it, "authenticate", "Unknown agent", violation=False)
        if self.policies.get("require_llm_provenance", True):
            cid = it.provenance.get("call_id")
            if not cid or LLM_CALLS.get(cid) != it.provenance.get("reply_sha256"):
                return self._reject(it, "authenticate", "No verified LLM provenance: not a model decision",
                                    "NO_PROVENANCE", violation=True, severity="critical")
        if rt.identity.status in ("paused", "quarantined", "offline"):
            return self._reject(it, "authenticate", f"Agent is {rt.identity.status}")
        self._pass(it, "authenticate")
        return True

    def _stage_authority(self, it: Intent) -> bool:
        ident = self.agents[it.agent_id].identity
        for rule in self.policies.get("deny", []):
            if rule["role"] == ident.role and it.target_resource in rule["targets"]:
                return self._reject(it, "authority", rule["reason"], rule["id"], violation=True, severity="critical")
        if it.action not in ident.tools:
            return self._reject(it, "authority", f"Tool '{it.action}' not granted", "TOOL_NOT_GRANTED",
                                violation=True, severity="critical")
        lim = ident.action_limits.get(it.action, {})
        if "speed_kn" in it.params:
            v = it.params["speed_kn"]
            if not lim.get("min_kn", 0) <= v <= lim.get("max_kn", 99):
                return self._reject(it, "authority", f"Speed {v} kn outside limits "
                                    f"{lim.get('min_kn', 0)}–{lim.get('max_kn', 99)} kn", "ACTION_LIMIT", violation=True)
        if "heading" in it.params and "max_delta_deg" in lim:
            delta = abs(angle_diff(it.params["heading"], self.world.ship.cmd_heading))
            if delta > lim["max_delta_deg"]:
                return self._reject(it, "authority", f"Turn {delta:.0f}° exceeds limit {lim['max_delta_deg']}°",
                                    "ACTION_LIMIT", violation=True)
        if it.kind == "act" and ident.autonomy_level <= 1:
            it.kind = "recommend"
        self._pass(it, "authority")
        return True

    def _unwarranted(self, it: Intent) -> str | None:
        """Why nothing on the COP justifies this action, or None. A governance check: it never chooses an
        action, it only stops ones the situation doesn't support, so the operator never sees an unfounded request."""
        w, sysm, s = self.world, self.world.sys, self.world.ship
        # Only what the watch can see counts: tracks on the 20 nm COP, judged by their DISPLAYED classification,
        # never by simulation ground truth.
        on_cop = {c.id: c for c in w.contacts.values() if c.range_nm <= COP_RANGE_NM and not c.hidden}
        role = self.agents[it.agent_id].identity.role if it.agent_id in self.agents else ""
        cited = set(re.findall(r"TN\s?(\d{4})", f"{it.rationale} {it.objective} {it.params}"))
        ghosts = sorted(f"TN{n}" for n in cited if f"TN{n}" not in on_cop)
        if ghosts:
            return f"Cites {', '.join(ghosts)}, which is not on the COP"
        if it.action == "activate_defense":
            wanted = [d for d in (it.params.get("systems") or []) if d in sysm.nodes or d == "NIXIE"]
            if not wanted or all(sysm.is_active(d) for d in wanted):
                return "Every requested defense is already active (nothing to decide)"
            if "NIXIE" in wanted and not (sysm.torpedoes or any(c.domain == "subsurface" for c in on_cop.values())):
                return "Nixie is for torpedoes: no torpedo or submarine on the COP"
            if wanted != ["NIXIE"] and not sysm.threats_on_cop() and not any(c.classification == "HOSTILE" and c.range_nm <= 10 for c in on_cop.values()):
                return "No inbound threats and no track classified HOSTILE inside 10 nm on the COP"
        elif it.action == "engage_target":
            if self.hold_fire:
                return "HOLD FIRE is in effect (operator ordered cease fire)"
        elif it.action == "set_emcon":
            if it.params.get("on") and not any(c.classification in ("SUSPECT", "HOSTILE") for c in on_cop.values()):
                return "No SUSPECT or HOSTILE track on the COP"
        elif it.action == "close_breaker":
            dead = [n for n, v in sysm.nodes.items()
                    if not v["powered"] and not v["failed"] and v["type"] not in ("gen", "swbd", "lc")]
            if not dead and not sysm.overloaded():
                return "No unpowered equipment and no overloaded generator on the Ship Systems COP"
        elif it.action == "open_breaker":
            if not any(n["failed"] for n in sysm.nodes.values()):
                return "No failed equipment to isolate"
        elif it.action == "set_gq":
            casualty = any(c["status"] not in ("out", "repaired") for c in sysm.casualties.values())
            threat = sysm.threats_on_cop() or any(c.classification in ("SUSPECT", "HOSTILE") for c in on_cop.values())
            if sysm.gq:
                return "General Quarters is already set"
            if not (casualty or threat):
                return "No casualty, SUSPECT/HOSTILE track or inbound threat on any COP"
        elif it.action == "dispatch_team":
            cas = sysm.casualties.get(it.params.get("casualty", ""))
            if not cas or cas["status"] in ("out", "repaired"):
                return "No active casualty on the COP"
        elif it.action in ("set_heading", "set_speed"):
            if role in ("defense", "asw") and not sysm.threats_on_cop():
                return "Evasive maneuvering needs an inbound missile or torpedo on the COP"
            if role == "collision" and not [c for c in w.threats(self.policies["min_cpa_nm"]) if c.id in on_cop]:
                return "No contact with CPA under 1 nm on the COP"
            if role == "engine" and s.engine_temp <= self.policies["engine_temp_warn_c"]:
                return f"Engine temperature {s.engine_temp:.0f} °C is normal"
            heavy_ahead = any(c["intensity"] >= 0.6 and c["range_to_edge_nm"] <= 6 and abs(c["relative_bearing"]) <= 60
                              for c in w.weather_ahead())
            if role == "weather" and w.env.wind_kts < 30 and not heavy_ahead:
                return f"No heavy weather on the radar ahead and wind {w.env.wind_kts:.0f} kts"
        return None

    def _stage_constraints(self, it: Intent) -> bool:
        p, s = self.policies, self.world.ship
        sysm = self.world.sys
        why = self._unwarranted(it)
        if why:
            return self._reject(it, "constraints", why, "NOT_WARRANTED")
        if it.action in sysm.SYSTEM_ACTIONS:
            why = sysm.validate(it.action, it.params)
            if why:
                return self._reject(it, "constraints", why, "SYSTEM_STATE")
            if it.action in ("activate_defense", "engage_target"):
                it.policy_hits.append("WEAPONS_NEED_HUMAN")
                it.needs_approval = True
                if "ECM" in it.params.get("systems", []) and sysm.emcon:
                    it.policy_hits.append("ECM_BREAKS_EMCON")
            if it.action == "dispatch_team":
                it.policy_hits.append("PERSONNEL_NEED_HUMAN")
                it.needs_approval = True
            self._pass(it, "constraints")
            return True
        if it.action == "set_speed" and not sysm.propulsion_ok:
            return self._reject(it, "constraints", "Main propulsion unavailable", "PROPULSION_LOST")
        if it.action == "set_heading" and not sysm.steering_ok:
            return self._reject(it, "constraints", "Steering gear unavailable", "STEERING_LOST")
        heading = it.params.get("heading", s.cmd_heading)
        speed = it.params.get("speed_kn", s.cmd_speed)
        before = self.world.predicted_min_cpa(s.cmd_heading, s.cmd_speed)
        after = self.world.predicted_min_cpa(heading, speed)
        # Block actions that make the closest approach worse while below the safe distance.
        if after < p["min_cpa_nm"] + p["cpa_buffer_nm"] and after < before - 0.05:
            return self._reject(it, "constraints",
                                f"Predicted CPA {after:.2f} nm < {p['min_cpa_nm'] + p['cpa_buffer_nm']:.1f} nm "
                                f"(now {before:.2f})",
                                "MIN_CPA", severity="critical")
        # Weather: never steer INTO a heavy cell (unless avoiding a collision, which a human then decides).
        if "heading" in it.params and self.world.env.cells:
            new = self.world.storm_clearance(heading)
            now = self.world.storm_clearance(s.cmd_heading)
            worse = {c: d for c, d in new.items() if d < 0 and d < now.get(c, 0) - 0.1}
            if worse:
                cell, d = min(worse.items(), key=lambda kv: kv[1])
                role = self.agents[it.agent_id].identity.role if it.agent_id in self.agents else ""
                why = f"Course {heading:03.0f}° runs into heavy weather cell {cell} ({abs(d):.1f} nm inside its edge)"
                if role != "collision":
                    return self._reject(it, "constraints", why, "INTO_HEAVY_WEATHER")
                it.policy_hits.append("INTO_HEAVY_WEATHER")
                it.needs_approval = True
                it.reasons.append(why + "; collision avoidance outranks weather, human decides")
        if "heading" in it.params and abs(angle_diff(heading, s.cmd_heading)) > p["big_turn_deg"]:
            it.needs_approval = True
            it.policy_hits.append("BIG_TURN_NEEDS_HUMAN")
        if not it.reversible:
            it.needs_approval = True
            it.policy_hits.append("IRREVERSIBLE_NEEDS_HUMAN")
        self._pass(it, "constraints")
        return True

    def _stage_quota(self, it: Intent) -> bool:
        ident = self.agents[it.agent_id].identity
        limit = ident.resource_quota.get("intents_per_min")
        log, now = self.quota_log[it.agent_id], time.time()
        while log and now - log[0] > 60:
            log.popleft()
        log.append(now)
        if limit and len(log) > limit:
            return self._reject(it, "quota", f"Rate {len(log)}/min exceeds quota {limit}/min",
                                "RATE_QUOTA", violation=True)
        self._pass(it, "quota")
        return True

    def _stage_conflicts_and_arbitrate(self, resource: str, group: list[Intent]) -> list[Intent]:
        """Returns the intents that win their resource and may proceed to approval."""
        prio = lambda it: self.agents[it.agent_id].identity.priority  # noqa: E731
        group.sort(key=prio, reverse=True)
        winners: list[Intent] = []
        # Same-batch clashes: the highest priority wins; equal priority with different values escalates.
        top = group[0]
        for it in group:
            it.pipeline["conflicts"] = "pass"
        for other in group[1:]:
            if other.agent_id == top.agent_id:
                self._reject(other, "arbitrate", "Superseded by the same agent's newer intent")
                continue
            if other.params == top.params:
                other.pipeline["arbitrate"] = "pass"
                self._reject(other, "arbitrate", f"Duplicate of {top.agent_id}'s intent")
                continue
            c = self._open_conflict("resource", resource, top, other, prio(top), prio(other))
            if prio(other) == prio(top):
                top.needs_approval = True
                other.pipeline["conflicts"] = "fail"
                self._reject(other, "arbitrate", f"Tie with {top.agent_id}; escalated to operator", None)
                c.status = "escalated"
            else:
                other.pipeline["conflicts"] = "fail"
                self._reject(other, "arbitrate", f"Lost arbitration to {top.agent_id} (priority {prio(top)})")
                c.status = "auto_resolved"
                c.resolution = f"{top.agent_id} wins on priority"
        # Clash with whoever currently holds the resource.
        hold = self.holds.get(resource)
        if hold and hold["until"] > time.time() and hold["agent_id"] != top.agent_id \
                and abs(hold["value"] - list(top.params.values())[0]) > 0.5:
            holder_prio = hold["priority"]
            holder_intent = self.intents.get(hold["intent_id"])
            if prio(top) < holder_prio:
                c = self._open_conflict("resource", resource, holder_intent, top, holder_prio, prio(top))
                c.status, c.resolution = "auto_resolved", f"{hold['agent_id']} keeps control (higher priority)"
                top.pipeline["conflicts"] = "fail"
                self._reject(top, "arbitrate", f"{resource} held by {hold['agent_id']} (priority {holder_prio})")
                return winners
            if holder_intent:
                c = self._open_conflict("resource", resource, top, holder_intent, prio(top), holder_prio)
                c.status, c.resolution = "auto_resolved", f"{top.agent_id} overrides {hold['agent_id']} (higher priority)"
        top.pipeline["arbitrate"] = "pass"
        winners.append(top)
        return winners

    def _open_conflict(self, kind: str, resource: str, winner: Intent | None, loser: Intent,
                       wp: int | None, lp: int) -> Conflict:
        agents = sorted({a for a in [winner.agent_id if winner else "operator", loser.agent_id]})
        now = time.time()
        for c in self.conflicts:  # fold repeats of the same clash into one record
            if c.type == kind and c.target_resource == resource and c.agent_ids == agents and now - c.updated_at < 90:
                c.count += 1
                c.updated_at = now
                c.intent_ids = (c.intent_ids + [loser.id])[-10:]
                for it in (winner, loser):
                    if it and c.id not in it.conflict_ids:
                        it.conflict_ids.append(c.id)
                return c
        c = Conflict(type=kind, target_resource=resource, agent_ids=agents,
                     intent_ids=[i.id for i in (winner, loser) if i],
                     severity="high" if resource == "rudder" else "medium")
        explain.summarize_conflict(c, winner, loser, wp, lp)
        for it in (winner, loser):
            if it:
                it.conflict_ids.append(c.id)
        self.conflicts.insert(0, c)
        del self.conflicts[40:]
        self.ledger.append("CONFLICT", "warden", {"conflict": c.id, "type": kind, "resource": resource, "agents": agents})
        self.emit("warn", "warden", f"CONFLICT {c.id} {kind} on {resource}: {' vs '.join(agents)}")
        return c

    def _stage_approve(self, it: Intent) -> None:
        ident = self.agents[it.agent_id].identity
        why = []
        if ident.autonomy_level == 2:
            why.append(f"{ident.name} is ACT W/ APPROVAL")
        if "BIG_TURN_NEEDS_HUMAN" in it.policy_hits:
            why.append(f"turn above {self.policies['big_turn_deg']}°")
        if "IRREVERSIBLE_NEEDS_HUMAN" in it.policy_hits:
            why.append("irreversible")
        if it.needs_approval and not why:
            why.append("equal-priority conflict")
        if it.kind == "recommend":
            it.pipeline["approve"] = "skip"
            it.pipeline["execute"] = "skip"
            it.status = "advisory"
            return
        if it.preapproved_by:                    # the operator chose this option on a decision card
            it.pipeline["approve"] = "pass"
            self._execute(it, it.preapproved_by)
            return
        if why and self.auto_act:
            # The operator set "agents may act": no approval wait. Every Warden check above still ran; the operator
            # watches, and can Cease Fire, roll back, pause or quarantine at any time.
            it.pipeline["approve"] = "pass"
            it.reasons.append("AUTO-ACT: operator granted permission to act (" + "; ".join(why) + ")")
            self.ledger.append("AUTO_ACT", "warden", {"intent": it.id, "why": why})
            self._execute(it, "auto-act")
            return
        if why:
            it.status = "escalated"
            it.pipeline["approve"] = "pending"
            ok_label, no_label = BUTTONS.get(it.action, ("Approve", "Deny"))
            esc = Escalation(intent_id=it.id, agent_id=it.agent_id, domain=domains.for_action(it.action),
                             title=f"{STATION.get(ident.role, ident.name)}: recommend {explain.describe(it)}",
                             approve_label=ok_label, reject_label=no_label,
                             summary="", safe_default=self.world.sys.SAFE_DEFAULTS.get(
                                 it.action, "Reject: hold current course and speed"),
                             timeout_s=self.policies["escalation_timeout_s"])
            explain.summarize_escalation(esc, it, why)
            self.escalations[esc.id] = esc
            self.stats["escalated"] += 1
            self.ledger.append("ESCALATION", "warden", {"escalation": esc.id, "intent": it.id, "why": why})
            self.emit("warn", "warden", f"ESCALATED {esc.id}: {esc.title} ({'; '.join(why)})")
            return
        it.pipeline["approve"] = "pass"
        self._execute(it, "warden")

    def _execute(self, it: Intent, decided_by: str) -> None:
        s = self.world.ship
        value = None
        if it.action in self.world.sys.SYSTEM_ACTIONS:
            it.inverse = self.world.sys.apply(it.action, it.params)
        elif "heading" in it.params:
            prev, s.cmd_heading = s.cmd_heading, wrap360(it.params["heading"])
            it.inverse = {"heading": prev}
            value = s.cmd_heading
        else:
            prev, s.cmd_speed = s.cmd_speed, it.params["speed_kn"]
            it.inverse = {"speed_kn": prev}
            value = s.cmd_speed
        it.pipeline["execute"] = "pass"
        it.status = "executed"
        it.decided_by = it.decided_by or decided_by
        self.stats["executed"] += 1
        self.executed.append(it.id)
        rt = self.agents.get(it.agent_id)
        prio = rt.identity.priority if rt else OPERATOR_PRIORITY
        if rt:
            rt.identity.trust_score = min(100.0, rt.identity.trust_score + 0.5)
        if value is not None:   # helm and throttle are held; ship-system actions are one-shot
            self.holds[it.target_resource] = {"agent_id": it.agent_id, "priority": prio, "value": value,
                                              "intent_id": it.id,
                                              "until": time.time() + (HOLD_SAFETY_S if prio >= 80 else HOLD_S)}
        self.ledger.append("EXECUTED", decided_by, {"intent": it.id, "agent": it.agent_id,
                                                    "params": it.params, "inverse": it.inverse})
        self.emit("ok", it.agent_id, f"EXECUTED {explain.describe(it)} ({it.objective})")
        if value is not None:
            self._check_oscillation(it, value)

    def _check_oscillation(self, it: Intent, value: float) -> None:
        hist = self.exec_history.setdefault(it.target_resource, deque(maxlen=12))
        now = time.time()
        hist.append((now, it.agent_id, value))
        window = [h for h in hist if now - h[0] <= self.policies["violation_window_s"]]
        flips = sum(1 for a, b in zip(window, window[1:]) if a[1] != b[1])
        if flips >= self.policies["oscillation_flips"]:
            agents = sorted({h[1] for h in window})
            loser = min(agents, key=lambda a: self.agents[a].identity.priority if a in self.agents else OPERATOR_PRIORITY)
            loser_it = next((self.intents[i] for i in reversed(self.executed) if self.intents[i].agent_id == loser), it)
            c = self._open_conflict("oscillation", it.target_resource, None if loser == it.agent_id else it, loser_it,
                                    None, 0)
            c.severity = "high"
            c.resolution = f"Damped: {it.target_resource} hold extended 60 s for the higher-priority agent"
            c.status = "auto_resolved"
            winner = max(agents, key=lambda a: self.agents[a].identity.priority if a in self.agents else OPERATOR_PRIORITY)
            h = self.holds.get(it.target_resource)
            if h and h["agent_id"] == winner:
                h["until"] = now + 60
            hist.clear()
            self._alert("warn", "monitor", f"Oscillation on {it.target_resource} between {', '.join(agents)}; damping applied")

    # ------------------------------------------------------------------ escalations
    def decide_escalation(self, esc_id: str, approve: bool, operator: str, choice: str = "a") -> Escalation:
        esc = self.escalations[esc_id]
        if esc.status != "open":
            return esc
        if esc.kind == "system_check":
            return self._decide_system_check(esc, approve, operator)
        it = self.intents[esc.intent_id]
        role = self.agents[it.agent_id].identity.role if it.agent_id in self.agents else ""
        if choice == "b" and it.alternative:
            # Option B: the agent's second course of action. It still goes through every Warden check.
            alt = Intent(agent_id=it.agent_id, action=it.alternative["action"], params=dict(it.alternative["params"]),
                         target_resource=_target_of(it.alternative["action"], it.alternative["params"]),
                         objective=it.objective, rationale=it.alternative.get("rationale", ""), confidence=it.confidence,
                         provenance=it.provenance, preapproved_by=operator)
            esc.status, esc.decided_by, it.decided_by = "rejected", operator, operator
            it.pipeline["approve"], it.pipeline["execute"], it.status = "fail", "skip", "rejected"
            it.reasons.append(f"{operator} chose option B: {explain.describe(alt)}")
            self.ledger.append("CONTROL", operator, {"escalation": esc.id, "decision": "option_b", "intent": alt.id})
            self.emit("ok", operator, f"OPTION B {esc.id}: {explain.describe(alt)}")
            record_feedback(role, "operator chose option B", f"preferred {explain.describe(alt)} over {explain.describe(it)}",
                            it.rationale)
            self.process([alt])
            return esc
        if not approve and it.agent_id in self.agents and operator != "timeout-default":
            record_feedback(role, "operator REJECTED", explain.describe(it), it.rationale)
        esc.status = "approved" if approve else "rejected"
        esc.decided_by = operator
        it.decided_by = operator
        self.ledger.append("CONTROL", operator, {"escalation": esc.id, "decision": esc.status})
        if approve:
            it.pipeline["approve"] = "pass"
            self.emit("ok", operator, f"APPROVED {esc.id}: {esc.title}")
            self._execute(it, operator)
        else:
            it.pipeline["approve"] = "fail"
            it.pipeline["execute"] = "skip"
            it.status = "rejected"
            it.reasons.append(f"Rejected by {operator}")
            self.stats["rejected"] += 1
            self.emit("warn", operator, f"REJECTED {esc.id}: {esc.title}")
        return esc

    def _check_escalation_timeouts(self, now: float) -> None:
        for esc in self.escalations.values():
            if esc.status == "open" and now - esc.opened_at > esc.timeout_s:
                esc.status = "timed_out"
                esc.decided_by = "timeout-default"
                if esc.kind == "system_check":
                    self._missed_system_check(esc, now)
                    continue
                it = self.intents.get(esc.intent_id)
                if it:
                    it.pipeline["approve"] = "fail"
                    it.pipeline["execute"] = "skip"
                    it.status = "expired"
                    it.decided_by = "timeout-default"
                    it.reasons.append(f"No operator decision in {esc.timeout_s:.0f} s; safe default applied")
                self.ledger.append("DECISION", "timeout-default", {"escalation": esc.id, "default": esc.safe_default})
                self.emit("warn", "warden", f"TIMEOUT {esc.id}: safe default applied ({esc.safe_default})")

    # ------------------------------------------------------------------ periodic system check
    def enable_system_checks(self) -> None:
        self.checks_enabled = True
        self.checks_missed = 0
        self.next_check = time.time() + self.policies["system_check_interval_s"]

    def _maybe_system_check(self, now: float) -> None:
        if not self.checks_enabled or self.safe_stop or now < self.next_check:
            return
        if any(e.kind == "system_check" and e.status == "open" for e in self.escalations.values()):
            return
        self.open_system_check()

    def open_system_check(self, operator: str = "warden") -> Escalation:
        """Ask the operator to confirm the whole system is still fit for autonomous operation."""
        by_status: dict[str, int] = {}
        for a in self.agents.values():
            by_status[a.identity.status] = by_status.get(a.identity.status, 0) + 1
        s = self.world.ship
        threats = self.world.threats(self.policies["min_cpa_nm"])
        chain = self.ledger.verify()
        facts = [
            "Agents: " + ", ".join(f"{n} {st}" for st, n in sorted(by_status.items())),
            f"Decisions waiting: {sum(1 for e in self.escalations.values() if e.status == 'open' and e.kind == 'intent')}",
            f"Conflicts: {len(self.conflicts)}",
            f"Collision threats: {len(threats)}" + (f" (closest CPA {min(c.cpa_nm for c in threats):.2f} nm)" if threats else ""),
            f"Engine {s.engine_temp:.0f} °C, {s.speed:.1f} kn, heading {s.heading:03.0f}°",
            f"Audit ledger {'valid' if chain['valid'] else 'BROKEN'} ({chain['rows']} rows)",
        ]
        last_chance = self.checks_missed + 1 >= self.policies["system_check_max_missed"]
        esc = Escalation(kind="system_check", agent_id="warden", title="Periodic system check",
                         summary=" · ".join(facts) + (f" · Missed checks: {self.checks_missed}" if self.checks_missed else ""),
                         summary_source="fallback",
                         safe_default="SAFE-STOP" if last_chance
                         else f"critical alert, ask again in {self.policies['system_check_retry_s']} s",
                         timeout_s=self.policies["system_check_timeout_s"],
                         approve_label="Confirm Operations", reject_label="Hold (SAFE-STOP)")
        self.escalations[esc.id] = esc
        self.ledger.append("SYSTEM_CHECK", operator, {"escalation": esc.id, "facts": facts, "missed": self.checks_missed})
        self.emit("warn", "warden", f"SYSTEM CHECK {esc.id}: operator confirmation required")
        return esc

    def _decide_system_check(self, esc: Escalation, approve: bool, operator: str) -> Escalation:
        esc.status = "approved" if approve else "rejected"
        esc.decided_by = operator
        self.ledger.append("CONTROL", operator, {"system_check": esc.id, "decision": esc.status})
        if approve:
            self.checks_missed = 0
            self.next_check = time.time() + self.policies["system_check_interval_s"]
            self.emit("ok", operator, f"SYSTEM CHECK {esc.id} confirmed: autonomous operations continue")
        else:
            self.emit("warn", operator, f"SYSTEM CHECK {esc.id}: operator chose to hold operations")
            self.engage_safe_stop(operator)
        return esc

    def _missed_system_check(self, esc: Escalation, now: float) -> None:
        self.checks_missed += 1
        self.ledger.append("DECISION", "timeout-default", {"system_check": esc.id, "missed": self.checks_missed})
        if self.checks_missed >= self.policies["system_check_max_missed"]:
            self._alert("critical", "watch-alarm", f"{self.checks_missed} system checks missed: no operator response")
            self.engage_safe_stop("watch-alarm")
        else:
            self._alert("critical", "watch-alarm",
                        f"System check {esc.id} missed; asking again in {self.policies['system_check_retry_s']} s")
            self.next_check = now + self.policies["system_check_retry_s"]

    # ------------------------------------------------------------------ monitors
    def _record_violation(self, agent_id: str, reason: str, severity: str) -> None:
        log, now = self.violations[agent_id], time.time()
        log.append(now)
        while log and now - log[0] > self.policies["violation_window_s"]:
            log.popleft()
        rt = self.agents[agent_id]
        if len(log) >= self.policies["auto_quarantine_violations"] and rt.identity.status != "quarantined":
            self.quarantine(agent_id, "auto-containment", f"{len(log)} violations in {self.policies['violation_window_s']} s")

    def _monitors(self, now: float) -> None:
        p, s = self.policies, self.world.ship
        while self.world.sys.events:
            sev, src, msg = self.world.sys.events.popleft()
            if sev == "info":
                self.emit("engine", src, msg)
                self.ledger.append("SHIP_EVENT", src, {"message": msg})
            else:
                self._alert("critical" if sev == "critical" else "warn", src, msg)
        for c in self.world.contacts.values():
            key = f"cpa:{c.id}"
            if c.range_nm < p["critical_cpa_nm"] * 2 and c.cpa_nm < p["critical_cpa_nm"] and key not in self.alerted:
                self.alerted.add(key)
                self._alert("critical", "monitor", f"OOD, CIC: {c.id} CPA {c.cpa_nm:.2f} nm, range {c.range_nm:.2f} nm, closing")
        if s.engine_temp > p["engine_temp_crit_c"] and "temp" not in self.alerted:
            self.alerted.add("temp")
            self._alert("critical", "monitor", f"Engine: main engine temperature {s.engine_temp:.0f} °C, above {p['engine_temp_crit_c']} °C")
        elif s.engine_temp < p["engine_temp_warn_c"] - 5:
            self.alerted.discard("temp")

    def _update_statuses(self, now: float) -> None:
        open_by_agent = {e.agent_id for e in self.escalations.values() if e.status == "open"}
        for aid, rt in self.agents.items():
            ident = rt.identity
            if ident.status in ("paused", "quarantined", "offline"):
                continue
            if aid in open_by_agent:
                ident.status = "escalated"
            elif now - self.last_seen.get(aid, 0) < STATUS_ACTIVE_S:
                ident.status = "active"
            else:
                ident.status = "idle"

    # ------------------------------------------------------------------ operator controls
    def _control(self, operator: str, action: str, **payload) -> None:
        self.ledger.append("CONTROL", operator, {"action": action, **payload})

    def pause(self, agent_id: str, operator: str) -> None:
        self.agents[agent_id].identity.status = "paused"
        self._control(operator, "pause", agent=agent_id)
        self.emit("system", operator, f"PAUSED {agent_id}")

    def resume(self, agent_id: str, operator: str) -> None:
        ident = self.agents[agent_id].identity
        if ident.status in ("paused", "quarantined"):
            ident.status = "idle"
        self.violations[agent_id].clear()
        self._control(operator, "resume", agent=agent_id)
        self.emit("system", operator, f"RESUMED {agent_id}")

    def quarantine(self, agent_id: str, operator: str, reason: str = "") -> None:
        ident = self.agents[agent_id].identity
        ident.status = "quarantined"
        for esc in self.escalations.values():
            if esc.agent_id == agent_id and esc.status == "open":
                self.decide_escalation(esc.id, False, operator)
        self._control(operator, "quarantine", agent=agent_id, reason=reason)
        self._alert("critical", operator, f"QUARANTINED {agent_id}{': ' + reason if reason else ''}")

    def set_autonomy(self, agent_id: str, level: int, operator: str) -> None:
        ident = self.agents[agent_id].identity
        old, ident.autonomy_level = ident.autonomy_level, max(0, min(3, level))
        self._control(operator, "autonomy", agent=agent_id, old=old, new=ident.autonomy_level)
        self.emit("system", operator, f"AUTONOMY {agent_id}: {old} → {ident.autonomy_level}")

    def override(self, operator: str, action: str, value: float) -> Intent:
        target = "rudder" if action == "set_heading" else "main_engine"
        key = "heading" if action == "set_heading" else "speed_kn"
        it = Intent(agent_id="operator", action=action, params={key: value}, target_resource=target,
                    objective="Operator override", rationale=f"Manual override by {operator}",
                    confidence=1.0, sim_time=self.world.sim_time)
        it.pipeline = {s: "skip" for s in STAGES}
        self.intents[it.id] = it
        self._control(operator, "override", action=action, value=value)
        self._execute(it, operator)
        self.holds[target]["priority"] = OPERATOR_PRIORITY
        self.holds[target]["until"] = time.time() + 120
        return it

    def rollback(self, operator: str, agent_id: str | None = None, count: int = 1) -> list[str]:
        undone = []
        for iid in reversed(list(self.executed)):
            if len(undone) >= count:
                break
            it = self.intents.get(iid)
            if not it or it.status != "executed" or (agent_id and it.agent_id != agent_id):
                continue
            s = self.world.ship
            if it.action in self.world.sys.SYSTEM_ACTIONS:
                self.world.sys.undo(it.action, it.inverse or {})
            elif "heading" in (it.inverse or {}):
                s.cmd_heading = it.inverse["heading"]
            elif "speed_kn" in (it.inverse or {}):
                s.cmd_speed = it.inverse["speed_kn"]
            it.status = "rolled_back"
            self.holds.pop(it.target_resource, None)
            self.stats["rollbacks"] += 1
            undone.append(it.id)
            self.emit("system", operator, f"ROLLED BACK {explain.describe(it)} from {it.agent_id}")
        self._control(operator, "rollback", agent=agent_id, intents=undone)
        return undone

    def engage_safe_stop(self, operator: str) -> None:
        if self.safe_stop:
            return
        self.safe_stop = True
        s = self.world.ship
        self.paused_by_safe_stop = set()
        for aid, rt in self.agents.items():
            if rt.identity.status not in ("paused", "quarantined"):
                rt.identity.status = "paused"
                self.paused_by_safe_stop.add(aid)
        for esc in list(self.escalations.values()):
            if esc.status == "open":
                self.decide_escalation(esc.id, False, operator)
        s.cmd_heading = s.heading
        s.cmd_speed = self.policies["safe_speed_kn"]
        for target in ("rudder", "main_engine"):
            self.holds[target] = {"agent_id": "operator", "priority": OPERATOR_PRIORITY,
                                  "value": s.cmd_heading if target == "rudder" else s.cmd_speed,
                                  "intent_id": "", "until": float("inf")}
        self._control(operator, "safe_stop")
        self.emit("milestone-red", operator, "SAFE-STOP ENGAGED: all agents paused, holding course, safe speed")

    def normalize(self, operator: str) -> None:
        """Clear every injected problem: un-compromise agents, release containment, close open decisions."""
        for esc in list(self.escalations.values()):
            if esc.status == "open":
                esc.status, esc.decided_by = "rejected", "normal-conditions"
                it = self.intents.get(esc.intent_id)
                if it:
                    it.status, it.decided_by = "rejected", "normal-conditions"
                    it.pipeline["approve"], it.pipeline["execute"] = "fail", "skip"
        for aid, rt in self.agents.items():
            rt.rogue = False
            if rt.identity.status in ("paused", "quarantined"):
                rt.identity.status = "idle"
            self.violations[aid].clear()
        self.holds.clear()
        self.alerted.clear()
        if self.safe_stop:
            self.resume_all(operator)
        self._control(operator, "normal_conditions")
        self.emit("milestone", operator, "NORMAL CONDITIONS SET: contacts cleared, systems restored, agents released")

    def resume_all(self, operator: str) -> None:
        self.safe_stop = False
        for aid in self.paused_by_safe_stop:
            if self.agents[aid].identity.status == "paused":
                self.agents[aid].identity.status = "idle"
        self.paused_by_safe_stop = set()
        self.holds.clear()
        self.checks_missed = 0
        self.next_check = time.time() + self.policies["system_check_interval_s"]
        self._control(operator, "resume_all")
        self.emit("milestone", operator, "OPERATIONS RESUMED")

    # ------------------------------------------------------------------ views
    def _trim_intents(self, keep: int = 400) -> None:
        if len(self.intents) > keep:
            for k in list(self.intents)[: len(self.intents) - keep]:
                if self.intents[k].status != "escalated":
                    del self.intents[k]
            self.executed = [i for i in self.executed if i in self.intents]

    def _prov_summary(self, esc: Escalation) -> dict:
        p = (self.intents.get(esc.intent_id) or Intent(agent_id="", action="", target_resource="")).provenance
        return {"model": p.get("model"), "tools": len(p.get("tools", [])), "calculations": p.get("calculations", "")} if p else {}

    @staticmethod
    def _prov_compact(p: dict) -> dict:
        """Keeps the live state small: full tool trail, trimmed raw reply."""
        return p | {"raw_reply": p["raw_reply"][:600]} if p.get("raw_reply") else p

    def _proposal(self, esc: Escalation) -> dict:
        it = self.intents.get(esc.intent_id)
        if not it:
            return {}
        s = self.world.ship
        if it.action in self.world.sys.SYSTEM_ACTIONS:
            return {"action": it.action, "target": it.target_resource, "value": explain.value(it),
                    "current": self.world.sys.current(it.action, it.params), **self._extras(it)}
        if "heading" in it.params:
            return {"action": it.action, "target": it.target_resource, "heading": it.params["heading"],
                    "value": f"{it.params['heading']:03.0f}°", "current": f"{s.cmd_heading:03.0f}°", **self._extras(it)}
        return {"action": it.action, "target": it.target_resource, "speed_kn": it.params.get("speed_kn"),
                "value": f"{it.params.get('speed_kn')} kn", "current": f"{s.cmd_speed:.0f} kn", **self._extras(it)}

    @staticmethod
    def _extras(it: Intent) -> dict:
        out = {"risk": it.risk}
        if it.alternative:
            alt = Intent(agent_id=it.agent_id, action=it.alternative["action"], params=it.alternative["params"],
                         target_resource="")
            out["alternative"] = {"value": explain.describe(alt), "rationale": it.alternative.get("rationale", "")}
        return out

    def summary(self) -> dict:
        now = time.time()
        recent = [i for i in self.intents.values() if now - i.created_at <= 60]
        return {
            "agents_total": len(self.agents),
            "agents_active": sum(1 for a in self.agents.values() if a.identity.status in ("active", "escalated")),
            "intents_per_min": len(recent),
            "conflicts_open": sum(1 for c in self.conflicts if c.status in ("open", "escalated")),
            "conflicts_total": len(self.conflicts),
            "escalations_open": sum(1 for e in self.escalations.values() if e.status == "open"),
            "safe_stop": self.safe_stop,
            "next_check_s": round(max(0.0, self.next_check - now)) if self.checks_enabled and not self.safe_stop else None,
            "checks_missed": self.checks_missed,
            **self.stats,
        }

    def set_auto_act(self, on: bool, operator: str) -> None:
        self.auto_act = on
        self._control(operator, "auto_act", on=on)
        self.emit("milestone-red" if on else "milestone", operator,
                  "AGENTS MAY ACT: approvals granted in advance (Warden checks still apply)" if on
                  else "AGENTS ASK FIRST: every governed action waits for the watch")

    def cease_fire(self, operator: str) -> int:
        """One button: destruct every weapon in flight, refuse every pending launch, and hold fire until lifted."""
        self.hold_fire = True
        n = self.world.sys.abort_weapons()
        for esc in list(self.escalations.values()):
            it = self.intents.get(esc.intent_id)
            if esc.status == "open" and it and it.action == "engage_target":
                self.decide_escalation(esc.id, False, operator)
        self._control(operator, "cease_fire", destructed=n)
        self._alert("critical", operator, f"CEASE FIRE, CEASE FIRE: {n} weapon(s) destructed, HOLD FIRE in effect")
        return n

    def lift_hold_fire(self, operator: str) -> None:
        self.hold_fire = False
        self._control(operator, "lift_hold_fire")
        self.emit("milestone", operator, "HOLD FIRE lifted: agents may again request weapons release")

    def snapshot(self) -> dict:
        now = time.time()
        intents = sorted(self.intents.values(), key=lambda i: i.created_at, reverse=True)[:40]
        escs = sorted(self.escalations.values(), key=lambda e: e.opened_at, reverse=True)[:20]
        holds = {k: {**v, "until": None if v["until"] == float("inf") else round(v["until"] - now, 1)}
                 for k, v in self.holds.items() if v["until"] > now}
        return {
            "summary": self.summary(),
            "agents": [a.identity.model_dump() | {"violations": len(self.violations[a.identity.id]),
                                                  "llm": getattr(a, "status", None)}
                       for a in self.agents.values()],
            "intents": [i.model_dump() | {"provenance": self._prov_compact(i.provenance)} for i in intents],
            "conflicts": [c.model_dump() for c in self.conflicts[:20]],
            "escalations": [e.model_dump() | {"remaining_s": max(0.0, e.timeout_s - (now - e.opened_at)),
                                              "proposal": self._proposal(e), "provenance": self._prov_summary(e)}
                            for e in escs],
            "alerts": [a.model_dump() for a in list(self.alerts)[:20]],
            "holds": holds,
            "auto_act": self.auto_act,
            "hold_fire": self.hold_fire,
        }
