"""Governance tests: the safety properties Bridge Warden must always hold."""
from __future__ import annotations

import time
from pathlib import Path

import pytest

from warden.engine import Warden
from warden.ledger import Ledger
from warden.models import Intent
from warden.world import World

from .sim_harness import BACKEND, FakeClock, run


@pytest.fixture
def clock(monkeypatch):
    c = FakeClock()
    monkeypatch.setattr(time, "time", c)
    return c


@pytest.fixture
def setup(tmp_path, clock):
    world = World()
    ledger = Ledger(tmp_path / "l.db")
    warden = Warden(BACKEND / "config", ledger, world, lambda *a: None)
    # These tests check other governance rules with hand-built intents; provenance has its own tests.
    warden.policies["require_llm_provenance"] = False
    yield warden, world, ledger
    ledger.close()


def test_no_python_decisions_without_the_model(tmp_path, clock):
    """With the LLM switched off, agents propose nothing: every decision comes from the model."""
    warden, world, ledger, logs = run(120, tmp_path, None, clock)
    try:
        assert warden.stats["submitted"] == 0
        assert ledger.verify()["valid"]
    finally:
        ledger.close()


def test_energy_agent_may_never_touch_rudder(setup):
    warden, world, _ = setup
    it = Intent(agent_id="nrg-01", action="set_heading", params={"heading": 120}, target_resource="rudder")
    warden.process([it])
    assert it.status == "rejected"
    assert "ENERGY_NO_PROPULSION_CONTROL" in it.policy_hits
    assert world.ship.cmd_heading == 90.0


def test_higher_priority_wins_resource(setup):
    warden, world, _ = setup
    world.ship.engine_temp = 100            # warrants Engine Health's speed cut
    nav = Intent(agent_id="nav-01", action="set_speed", params={"speed_kn": 16}, target_resource="main_engine")
    eng = Intent(agent_id="eng-01", action="set_speed", params={"speed_kn": 10}, target_resource="main_engine")
    warden.process([nav, eng])
    assert eng.status == "executed" and nav.status == "rejected"
    assert world.ship.cmd_speed == 10
    assert warden.conflicts and warden.conflicts[0].type == "resource"


def test_weather_course_change_needs_human(setup):
    warden, world, _ = setup
    world.env.wind_kts = 38
    it = Intent(agent_id="wx-01", action="set_heading", params={"heading": 55}, target_resource="rudder")
    warden.process([it])
    assert it.status == "escalated"
    esc = next(iter(warden.escalations.values()))
    warden.decide_escalation(esc.id, True, "OOD")
    assert it.status == "executed" and world.ship.cmd_heading == 55


def test_escalation_times_out_to_safe_default(setup, clock):
    warden, world, _ = setup
    world.env.wind_kts = 38
    it = Intent(agent_id="wx-01", action="set_heading", params={"heading": 55}, target_resource="rudder")
    warden.process([it])
    clock.t += warden.policies["escalation_timeout_s"] - 5
    warden.step()
    assert it.status == "escalated", "still waiting inside the 2-minute window"
    clock.t += 10
    warden.step()
    assert it.status == "expired" and world.ship.cmd_heading == 90.0


def test_rollback_restores_previous_command(setup):
    warden, world, _ = setup
    world.ship.engine_temp = 100
    it = Intent(agent_id="eng-01", action="set_speed", params={"speed_kn": 10}, target_resource="main_engine")
    warden.process([it])
    assert world.ship.cmd_speed == 10
    warden.rollback("OOD")
    assert world.ship.cmd_speed == 14.0 and it.status == "rolled_back"


def test_safe_stop_pauses_everything(setup):
    warden, world, _ = setup
    warden.engage_safe_stop("OOD")
    assert all(a.identity.status == "paused" for a in warden.agents.values())
    assert world.ship.cmd_speed == warden.policies["safe_speed_kn"]
    it = Intent(agent_id="col-01", action="set_speed", params={"speed_kn": 12}, target_resource="main_engine")
    warden.process([it])
    assert it.status == "rejected"
    warden.resume_all("OOD")
    assert all(a.identity.status == "idle" for a in warden.agents.values())


def open_checks(warden):
    return [e for e in warden.escalations.values() if e.kind == "system_check" and e.status == "open"]


def test_system_check_opens_on_schedule_and_confirm_rearms(setup, clock):
    warden, world, _ = setup
    warden.enable_system_checks()
    clock.t += warden.policies["system_check_interval_s"] - 1
    warden.step()
    assert not open_checks(warden), "no check before the interval"
    clock.t += 2
    warden.step()
    (check,) = open_checks(warden)
    assert check.approve_label == "Confirm Operations"
    warden.decide_escalation(check.id, True, "OOD")
    assert check.status == "approved" and warden.checks_missed == 0
    assert not warden.safe_stop
    assert warden.next_check > clock.t


def test_system_check_hold_engages_safe_stop(setup, clock):
    warden, world, _ = setup
    check = warden.open_system_check("OOD")
    warden.decide_escalation(check.id, False, "OOD")
    assert warden.safe_stop
    assert all(a.identity.status == "paused" for a in warden.agents.values())


def test_missed_system_checks_escalate_to_safe_stop(setup, clock):
    warden, world, _ = setup
    p = warden.policies
    warden.enable_system_checks()
    clock.t += p["system_check_interval_s"] + 1
    warden.step()                                     # first check opens
    clock.t += p["system_check_timeout_s"] + 1
    warden.step()                                     # missed → critical alert, retry soon
    assert warden.checks_missed == 1 and not warden.safe_stop
    assert any(a.source == "watch-alarm" and a.severity == "critical" for a in warden.alerts)
    clock.t += p["system_check_retry_s"] + 1
    warden.step()                                     # retry opens
    (retry,) = open_checks(warden)
    assert retry.safe_default == "SAFE-STOP"
    clock.t += p["system_check_timeout_s"] + 1
    warden.step()                                     # second miss → SAFE-STOP
    assert warden.safe_stop and warden.checks_missed == 2


def test_no_system_checks_until_operations_start(setup, clock):
    warden, world, _ = setup
    clock.t += 10_000
    warden.step()
    assert not open_checks(warden)


def test_ledger_detects_tampering(tmp_path):
    led = Ledger(tmp_path / "t.db")
    for i in range(5):
        led.append("TEST", "x", {"i": i})
    assert led.verify()["valid"]
    led._db.execute("UPDATE ledger SET payload = ? WHERE seq = 3", ('{"i": 99}',))
    led._db.commit()
    result = led.verify()
    led.close()
    assert not result["valid"] and result["broken_at"] == 3
