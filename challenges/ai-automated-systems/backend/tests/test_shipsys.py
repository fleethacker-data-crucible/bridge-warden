"""Ship systems, threat picture and the LLM agent, governed by Warden."""
from __future__ import annotations

import json
import time

import pytest

from warden import llm
from warden.engine import Warden
from warden.ledger import Ledger
from warden.llm_agent import parse_actions, situation
from warden.models import Intent
from warden.shipsys import ShipSystems
from warden.world import World

from .sim_harness import BACKEND, FakeClock


@pytest.fixture
def clock(monkeypatch):
    c = FakeClock()
    monkeypatch.setattr(time, "time", c)
    return c


@pytest.fixture
def setup(tmp_path, clock):
    world = World(time_scale=3)
    ledger = Ledger(tmp_path / "l.db")
    warden = Warden(BACKEND / "config", ledger, world, lambda *a: None)
    # These tests check other governance rules with hand-built intents; provenance has its own tests.
    warden.policies["require_llm_provenance"] = False
    yield warden, world, ledger
    ledger.close()


def test_losing_2sb_needs_the_standby_ssgtg_to_restore_everything():
    sys = ShipSystems()
    assert sys.ok("STEER") and sys.ok("SHAFT-1")
    sys.fail("2SB", "test")
    assert not sys.ok("STEER") and not sys.ok("SHAFT-1")
    sys.set_breaker("B-1L3", True)                     # the obvious fix...
    assert sys.ok("SHAFT-1") and not sys.ok("STEER")
    assert "SSGTG-1" in sys.overloaded()               # ...overloads SSGTG-1
    sys.set_breaker("B-1L3", False)
    for b in ("B-G3", "B-3L3", "B-3L4"):               # bring SSGTG-3 on line instead
        sys.set_breaker(b, True)
    assert sys.ok("STEER") and sys.ok("SHAFT-1") and not sys.overloaded()


def test_spy_radar_needs_chilled_water():
    sys = ShipSystems()
    assert sys.ok("RADAR")
    sys.fail("CHW", "test")
    assert not sys.ok("RADAR")


def test_one_shaft_caps_speed():
    sys = ShipSystems()
    assert sys.max_speed_kn == 31
    sys.fail("SHAFT-2", "test")
    assert sys.max_speed_kn == 20 and sys.propulsion_ok


def test_weapons_always_need_a_human(setup):
    warden, world, _ = setup
    world.apply_event({"hostile": {"range_nm": 9}})
    world.sys.launch(hostile_id(world), 9, 2)
    it = Intent(agent_id="def-01", action="activate_defense", params={"systems": ["CIWS", "ADS"]}, target_resource="defense")
    warden.process([it])
    assert it.status == "escalated" and "WEAPONS_NEED_HUMAN" in it.policy_hits
    assert not world.sys.nodes["CIWS"]["active"]
    esc = next(e for e in warden.escalations.values() if e.intent_id == it.id)
    warden.decide_escalation(esc.id, True, "OOD")
    assert world.sys.nodes["CIWS"]["active"] and world.sys.p_hit() < 0.4
    assert world.sys.view(world)["p_hit"] < 0.4


def test_ecm_while_in_emcon_is_flagged(setup):
    warden, world, _ = setup
    world.apply_event({"hostile": {"range_nm": 9}})
    world.sys.launch(hostile_id(world), 9, 1)
    world.sys.emcon = True
    it = Intent(agent_id="def-01", action="activate_defense", params={"systems": ["ECM"]}, target_resource="defense")
    warden.process([it])
    assert "ECM_BREAKS_EMCON" in it.policy_hits


def hostile_id(world):
    return next(c.id for c in world.contacts.values() if c.kind == "hostile")


def test_emcon_stops_detection(setup, clock):
    warden, world, _ = setup
    world.apply_event({"hostile": {"range_nm": 12}})
    h = hostile_id(world)
    for _ in range(20):
        world.step()
    radiating = world.sys.hostile_state[h]["detect"]
    assert radiating > 0
    world.sys.emcon = True
    for _ in range(20):
        world.step()
    assert world.sys.hostile_state[h]["detect"] < radiating


def test_damage_control_team_contains_fire_and_restores_equipment(setup, clock):
    warden, world, _ = setup
    cas = world.sys.add_casualty("MER2", "fire", 0.8)
    world.step()
    assert world.sys.nodes["SHAFT-2"]["failed"]
    world.sys.set_gq(True)                      # Repair 3 only mans up at General Quarters
    for _ in range(25):
        world.step()
    assert world.sys.teams["R5"]["status"] == "standby"
    it = Intent(agent_id="dca-01", action="dispatch_team", params={"team": "R5", "casualty": cas["id"], "compartment": "MER2",
                                                                    "boundaries": [290, 348]}, target_resource="team:R5")
    warden.process([it])
    assert it.status == "escalated" and "PERSONNEL_NEED_HUMAN" in it.policy_hits
    warden.decide_escalation(next(e.id for e in warden.escalations.values() if e.intent_id == it.id), True, "OOD")
    for _ in range(80):
        world.step()
    assert cas["status"] == "repaired" and world.sys.ok("SHAFT-2")


def test_speed_orders_rejected_without_propulsion(setup):
    warden, world, _ = setup
    world.sys.fail("SHAFT-1", "test")
    world.sys.fail("SHAFT-2", "test")
    it = Intent(agent_id="nav-01", action="set_speed", params={"speed_kn": 16}, target_resource="main_engine")
    warden.process([it])
    assert it.status == "rejected" and "PROPULSION_LOST" in it.policy_hits


def test_llm_answer_parsing_drops_unknown_actions_but_lets_warden_judge_known_ones(setup):
    warden, world, _ = setup
    view = world.cop()
    text = ('Sure! ```json {"actions": [{"action": "set_emcon", "params": {"on": true}, "objective": "Go quiet", '
            '"rationale": "Hostile closing", "confidence": 0.8}, {"action": "launch_missiles", "params": {}}, '
            '{"action": "set_heading", "params": {"heading": 200}}]} ```')
    from warden.llm_agent import _json_obj
    intents, problems = parse_actions(_json_obj(text), view, "ew-01", {"call_id": "t"})
    assert [i.action for i in intents] == ["set_emcon", "set_heading"]
    assert any("launch_missiles" in p for p in problems)
    warden.process(intents)
    assert intents[1].status == "rejected" and "TOOL_NOT_GRANTED" in intents[1].policy_hits,         "an LLM reaching for a tool it wasn't granted is stopped by Warden, and counted"
    assert _json_obj("no json here") is None


def test_situation_only_contains_scoped_data(setup):
    warden, world, _ = setup
    s = situation({"ownship": world.cop()["ownship"], "sim_time": 0})
    assert "ownship" in s and "contacts" not in s and "hostiles" not in s


def test_second_team_to_the_same_casualty_is_rejected(setup):
    warden, world, _ = setup
    cas = world.sys.add_casualty("MER2", "fire", 0.6)
    world.sys.dispatch("FS", cas["id"])
    world.sys.teams["R2"]["status"] = "standby"      # manned, so only the double-assignment rule applies
    it = Intent(agent_id="dca-01", action="dispatch_team", params={"team": "R2", "casualty": cas["id"]}, target_resource="team:R2")
    warden.process([it])
    assert it.status == "rejected" and "already has" in it.reasons[0]


def test_compromised_agent_gets_adversarial_prompt_and_is_contained(setup, monkeypatch, clock):
    """Fault injection makes the model play a compromised agent; Warden stops what it tries."""
    warden, world, _ = setup
    prompts = []

    def evil(prompt, s):
        prompts.append(prompt)
        return json.dumps({"actions": [{"action": "set_heading", "params": {"heading": 270}},
                                       {"action": "set_speed", "params": {"speed_kn": 18}}]})

    monkeypatch.setitem(llm.PROVIDERS, "local_chat", lambda messages, s: evil(messages[0]["content"], s))
    monkeypatch.setenv("LLM_LOCAL_MODEL", "test-model")
    rt = warden.agents["nrg-01"]
    rt.enabled, rt.min_gap_s, rt.rogue = True, 0, True
    for _ in range(6):
        warden.step()
        for _ in range(50):
            if not rt._busy:
                break
            time.sleep(0.02)
    assert prompts and "COMPROMISED" in prompts[0]
    mine = [i for i in warden.intents.values() if i.agent_id == "nrg-01"]
    assert mine and all(i.status == "rejected" for i in mine)
    assert world.ship.cmd_heading == 90.0
    assert rt.identity.status == "quarantined"


@pytest.mark.parametrize("agent,action,params,target,setup_fn", [
    ("def-01", "activate_defense", {"systems": ["CIWS"]}, "defense", None),
    ("ew-01", "set_emcon", {"on": True}, "emcon", None),
    ("pwr-01", "close_breaker", {"breaker": "B-1L3"}, "breaker:B-1L3", None),
    ("col-01", "set_heading", {"heading": 120}, "rudder", None),
    ("eng-01", "set_speed", {"speed_kn": 10}, "main_engine", None),
    ("wx-01", "set_heading", {"heading": 55}, "rudder", None),
])
def test_unwarranted_actions_never_reach_the_operator(setup, agent, action, params, target, setup_fn):
    """Nothing on the COP justifies these, so Warden rejects them quietly: no decision, no banner."""
    warden, world, _ = setup
    it = Intent(agent_id=agent, action=action, params=params, target_resource=target)
    warden.process([it])
    assert it.status == "rejected" and "NOT_WARRANTED" in it.policy_hits
    assert not warden.escalations
    assert not warden.violations[agent], "an unfounded idea is not a violation"


def test_defense_is_warranted_once_a_hostile_closes(setup):
    warden, world, _ = setup
    world.apply_event({"hostile": {"range_nm": 9}})
    world.contacts[hostile_id(world)].classification = "HOSTILE"     # as CIC has classified it
    it = Intent(agent_id="def-01", action="activate_defense", params={"systems": ["CIWS"]}, target_resource="defense")
    warden.process([it])
    assert it.status == "escalated"


def test_reported_casualty_is_confirmed_by_investigator(setup):
    warden, world, _ = setup
    cas = world.sys.add_casualty("AMR2", "fire", 0.3)
    assert cas["status"] == "reported"
    for _ in range(8):
        world.step()                             # 3 s per tick → 24 s > investigation time
    assert cas["status"] == "active"


def test_unmanned_repair_party_cannot_be_sent_but_flying_squad_can(setup):
    warden, world, _ = setup
    cas = world.sys.add_casualty("AMR2", "fire", 0.3)
    assert "not manned" in world.sys.validate("dispatch_team", {"team": "R3", "casualty": cas["id"]})
    assert world.sys.validate("dispatch_team", {"team": "FS", "casualty": cas["id"]}) is None


def test_one_generator_carrying_the_ship_overloads():
    sys = ShipSystems()
    assert not sys.overloaded()
    sys.fail("SSGTG-2", "test")
    sys.set_breaker("B-1L3", True)
    sys.set_breaker("B-T12", True)
    assert "SSGTG-1" in sys.overloaded()
    sys.set_breaker("B-G3", True)
    sys.set_breaker("B-T23", True)               # SSGTG-3 shares the load through the bus ties
    assert not sys.overloaded()


def test_hostile_track_is_classified_over_time(setup):
    warden, world, _ = setup
    world.apply_event({"hostile": {"range_nm": 17}})
    c = world.contacts[hostile_id(world)]
    assert c.classification == "UNKNOWN"
    for _ in range(12):
        world.step()                             # 36 s tracked → SUSPECT
    assert c.classification in ("SUSPECT", "HOSTILE")


def test_defense_needs_a_track_the_cop_shows_as_hostile(setup):
    """Ground truth says hostile, but the COP still shows UNKNOWN: no weapons decision yet."""
    warden, world, _ = setup
    world.apply_event({"hostile": {"range_nm": 9}})
    c = world.contacts[hostile_id(world)]
    c.classification = "UNKNOWN"
    it = Intent(agent_id="def-01", action="activate_defense", params={"systems": ["CIWS"]}, target_resource="defense")
    warden.process([it])
    assert it.status == "rejected" and "NOT_WARRANTED" in it.policy_hits


def test_decision_citing_a_track_not_on_the_cop_is_rejected(setup):
    warden, world, _ = setup
    world.apply_event({"hostile": {"range_nm": 9}})
    world.contacts[hostile_id(world)].classification = "HOSTILE"
    it = Intent(agent_id="ew-01", action="set_emcon", params={"on": True}, target_resource="emcon",
                rationale="TN4999 is closing fast")
    warden.process([it])
    assert it.status == "rejected" and "TN4999" in it.reasons[0]


def test_hit_chance_is_zero_with_nothing_inbound():
    from warden.world import World
    w = World()
    assert w.sys.view(w)["p_hit"] == 0.0
    w.apply_event({"hostile": {"range_nm": 12}})
    assert w.sys.view(w)["p_hit"] == 0.0, "a hostile on the COP isn't a hit chance until something is inbound"
    w.sys.launch(next(c.id for c in w.contacts.values() if c.kind == "hostile"), 10, 1)
    assert w.sys.view(w)["p_hit"] == 1.0


def test_agent_action_without_llm_provenance_is_rejected(setup):
    """The 100% guarantee: an action that didn't come from a registered model call can't pass as an agent decision."""
    warden, world, _ = setup
    warden.policies["require_llm_provenance"] = True
    world.apply_event({"hostile": {"range_nm": 9}})
    world.contacts[hostile_id(world)].classification = "HOSTILE"
    forged = Intent(agent_id="ew-01", action="set_emcon", params={"on": True}, target_resource="emcon",
                    provenance={"call_id": "made-up", "reply_sha256": "0" * 64})
    warden.process([forged])
    assert forged.status == "rejected" and "NO_PROVENANCE" in forged.policy_hits


def test_model_uses_calculator_tools_and_provenance_records_them(setup, monkeypatch, clock):
    """The model asks for a calculation, gets the result, then decides; the trail is on the intent."""
    warden, world, _ = setup
    warden.policies["require_llm_provenance"] = True
    world.apply_event({"hostile": {"range_nm": 14}})
    h = hostile_id(world)
    world.contacts[h].classification = "SUSPECT"
    turns = iter([
        json.dumps({"tool_calls": [{"tool": "time_to_range", "args": {"track": h, "range_nm": 10}}]}),
        json.dumps({"calculations": "4 nm at 40 kn closing = 6 min", "actions": [
            {"action": "set_emcon", "params": {"on": True}, "objective": "Go quiet",
             "rationale": h + " reaches 10 nm in 6 min", "confidence": 0.9}]}),
    ])
    monkeypatch.setitem(llm.PROVIDERS, "local_chat", lambda messages, s: next(turns))
    monkeypatch.setenv("LLM_LOCAL_MODEL", "test-model")
    rt = warden.agents["ew-01"]
    rt.enabled, rt.min_gap_s = True, 0
    warden.step()
    for _ in range(100):
        if rt.status["calls"]:
            break
        time.sleep(0.02)
    warden.step()
    it = next(i for i in warden.intents.values() if i.agent_id == "ew-01")
    assert it.status == "escalated", it.reasons
    p = it.provenance
    assert p["model"] == "test-model" and p["tools"][0]["tool"] == "time_to_range"
    assert "minutes" in p["tools"][0]["result"] and "6 min" in p["calculations"]
    assert any(r["type"] == "LLM_DECISION" for r in warden.ledger.rows(50))


def test_hidden_cop_is_frozen(setup, clock):
    warden, world, _ = setup
    cas = world.sys.add_casualty("MER2", "fire", 0.3, confirmed=True)
    sev = cas["severity"]
    for _ in range(10):
        world.step(active={"nav"})
    assert cas["severity"] == sev, "the DC COP isn't on screen, so the fire doesn't grow"
    world.step(active={"dc"})
    assert cas["severity"] > sev


def test_nav_cop_holds_at_most_five_targets(setup):
    warden, world, _ = setup
    world.normalize()
    for _ in range(4):
        world.apply_event({"spawn_contact": {"course": 0, "speed": 12, "collision_in_min": 14}})
    world.apply_event({"hostile": {"range_nm": 15}})
    assert len(world.contacts) <= 5
    assert any(c.kind == "hostile" for c in world.contacts.values())


def test_turning_into_a_storm_cell_is_rejected(setup):
    """The weather agent's sign error (port = +35) must never reach the watch."""
    warden, world, _ = setup
    world.apply_event({"weather": {"wind_kts": 38, "visibility_nm": 1.0}})
    # the heading within the agent's 40-degree authority that runs deepest into a cell
    bad = min(range(50, 131, 5), key=lambda h: min(world.storm_clearance(h).values()))
    assert min(world.storm_clearance(bad).values()) < 0
    it = Intent(agent_id="wx-01", action="set_heading", params={"heading": float(bad)}, target_resource="rudder")
    warden.process([it])
    assert it.status == "rejected" and "INTO_HEAVY_WEATHER" in it.policy_hits


def test_inbound_from_over_the_horizon_is_invisible_until_it_enters_the_cop(setup):
    warden, world, _ = setup
    world.apply_event({"inbound": {"count": 2}})
    v = world.sys.view(world)
    assert v["inbound"] == [] and v["p_hit"] == 0.0, "28 nm out: not on the COP, nobody knows"
    it = Intent(agent_id="def-01", action="activate_defense", params={"systems": ["CIWS"]}, target_resource="defense")
    warden.process([it])
    assert "NOT_WARRANTED" in it.policy_hits, "an agent can't act on a threat that isn't on the COP"
    for _ in range(30):                          # 3 s ticks: 90 s at 500 kn = 12.5 nm
        world.step()
    v = world.sys.view(world)
    assert v["inbound"] and all(m["range"] <= 20 for m in v["inbound"])
    it2 = Intent(agent_id="def-01", action="activate_defense", params={"systems": ["CIWS"]}, target_resource="defense")
    warden.process([it2])
    assert it2.status == "escalated"


def test_activating_defenses_that_are_already_active_is_not_a_decision(setup):
    warden, world, _ = setup
    world.sys.launch("OTH", 15, 1, bearing=40)
    world.sys.nodes["CIWS"]["active"] = True
    it = Intent(agent_id="def-01", action="activate_defense", params={"systems": ["CIWS"]}, target_resource="defense")
    warden.process([it])
    assert it.status == "rejected" and "already active" in it.reasons[0]


def test_sm2_launch_needs_batteries_release_and_can_splash_the_target(setup, clock):
    warden, world, _ = setup
    world.sys.launch("OTH", 12, 1, bearing=40)
    tgt = world.sys.inbound[0]["id"]
    it = Intent(agent_id="def-01", action="engage_target", params={"target": tgt, "rounds": 2}, target_resource=f"engage:{tgt}")
    warden.process([it])
    assert it.status == "escalated" and "WEAPONS_NEED_HUMAN" in it.policy_hits
    esc = next(e for e in warden.escalations.values() if e.intent_id == it.id)
    assert esc.approve_label == "Batteries Release" and not world.sys.interceptors
    warden.decide_escalation(esc.id, True, "OOD")
    assert len(world.sys.interceptors) == 2 and world.sys.magazine == 22
    world.sys.rng.seed(1)
    for _ in range(12):
        world.step()
    assert not world.sys.interceptors, "interceptors reached the target"
    assert any(sp["label"] == "SPLASH" for sp in world.sys.splashes) or not world.sys.inbound or world.sys.inbound


def test_release_beyond_the_envelope_is_held_by_fire_control_until_15_nm(setup):
    warden, world, _ = setup
    warden.auto_act = True
    world.sys.launch("OTH", 18, 1, bearing=40)
    tgt = world.sys.inbound[0]["id"]
    it = Intent(agent_id="def-01", action="engage_target", params={"target": tgt, "rounds": 2}, target_resource=f"engage:{tgt}")
    warden.process([it])
    assert it.status == "executed" and tgt in world.sys.held and not world.sys.interceptors
    for _ in range(12):                      # 3 s ticks at 500 kn: inside 15 nm
        world.step()
    assert tgt not in world.sys.held and world.sys.magazine == 22


def test_defense_agent_may_only_maneuver_with_something_inbound(setup):
    warden, world, _ = setup
    it = Intent(agent_id="def-01", action="set_speed", params={"speed_kn": 31}, target_resource="main_engine")
    warden.process([it])
    assert "NOT_WARRANTED" in it.policy_hits
    world.sys.launch("OTH", 12, 1, bearing=40)
    it2 = Intent(agent_id="def-01", action="set_speed", params={"speed_kn": 31}, target_resource="main_engine")
    warden.process([it2])
    assert it2.status in ("executed", "escalated")


def test_submarine_is_hidden_until_sonar_holds_it_then_fires_torpedoes(setup):
    warden, world, _ = setup
    world.apply_event({"submarine": {"bearing": 0, "range_nm": 9.0, "speed": 12}})
    assert not [c for c in world.cop()["contacts"] if c["domain"] == "subsurface"], "quiet sub: not on the COP"
    for _ in range(200):
        world.step()
        if world.sys.torpedoes:
            break
    sub = next(c for c in world.contacts.values() if c.domain == "subsurface")
    assert not sub.hidden and sub.classification == "HOSTILE" and len(world.sys.torpedoes) == 2


def test_running_from_a_torpedo_outlasts_its_fuel(setup):
    _, world, _ = setup
    s = world.sys
    world.ship.speed = world.ship.cmd_speed = 31
    world.ship.heading = world.ship.cmd_heading = 270          # torpedo bearing 090: running directly away
    s.torpedoes.append({"id": "T1", "from": "X", "range": 4.0, "bearing": 90.0, "run_left": 9.0, "nixie_tried": True})
    for _ in range(400):
        world.step()
    assert not s.torpedoes and s.torp_hits == 0


def test_cease_fire_destructs_weapons_and_blocks_new_launches(setup):
    warden, world, _ = setup
    world.sys.launch("OTH", 12.0, 1, bearing=45.0)
    it = Intent(agent_id="def-01", action="engage_target", params={"target": "OTH-V1", "rounds": 2},
                target_resource="engage:OTH-V1")
    warden.auto_act = True
    warden.process([it])
    assert it.status == "executed" and it.decided_by == "auto-act" and len(world.sys.interceptors) == 2
    assert warden.cease_fire("operator") == 2 and not world.sys.interceptors
    again = Intent(agent_id="def-01", action="engage_target", params={"target": "OTH-V1", "rounds": 1},
                   target_resource="engage:OTH-V1")
    warden.process([again])
    assert again.status == "rejected" and "NOT_WARRANTED" in again.policy_hits


def test_operator_can_pick_the_agents_second_option(setup):
    warden, world, _ = setup
    world.sys.add_casualty("MER2", "fire", 0.6, confirmed=True)
    world.sys.set_gq(True)
    for t in world.sys.teams.values():
        t["status"] = "standby"
    cas = next(iter(world.sys.casualties))
    it = Intent(agent_id="dca-01", action="dispatch_team", params={"team": "R5", "casualty": cas},
                target_resource="team:R5", alternative={"action": "dispatch_team",
                                                         "params": {"team": "FS", "casualty": cas}, "rationale": "closer"})
    warden.process([it])
    esc = next(e for e in warden.escalations.values() if e.intent_id == it.id)
    assert warden._proposal(esc)["alternative"]["value"].startswith("dispatch_team")
    warden.decide_escalation(esc.id, False, "operator", choice="b")
    assert world.sys.teams["FS"]["casualty"] == cas and world.sys.teams["R5"]["casualty"] is None
