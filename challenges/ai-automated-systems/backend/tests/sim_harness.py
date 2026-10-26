"""Run the scenario headless on a fake clock (no sleeping). Used by tests and for tuning."""
from __future__ import annotations

import time
from pathlib import Path

from warden.engine import Warden
from warden.ledger import Ledger
from warden.world import Scenario, World

BACKEND = Path(__file__).resolve().parent.parent


class FakeClock:
    def __init__(self):
        self.t = 1_800_000_000.0

    def __call__(self):
        return self.t


def run(seconds: int, tmp: Path, on_tick=None, clock: FakeClock | None = None):
    logs = []
    world = World(time_scale=10)
    ledger = Ledger(tmp / "ledger.db")
    warden = Warden(BACKEND / "config", ledger, world, lambda lv, src, msg: logs.append((clock.t if clock else 0, lv, src, msg)))
    sc = Scenario(BACKEND / "scenarios" / "full_demo.yaml")
    sc.running = True
    for _ in range(seconds):
        if clock:
            clock.t += 1
        for ev in sc.due(1.0):
            if "rogue_agent" in ev:
                warden.agents[ev["rogue_agent"]["id"]].rogue = True
            else:
                world.apply_event(ev)
        world.step()
        warden.step()
        if on_tick:
            on_tick(warden, world)
    return warden, world, ledger, logs


if __name__ == "__main__":
    import sys
    import tempfile

    clock = FakeClock()
    time.time = clock
    secs = int(sys.argv[1]) if len(sys.argv) > 1 else 210

    def show(w, wd):
        s = wd.ship
        if w.tick % 10 == 0:
            cs = " ".join(f"{c.id}:cpa={c.cpa_nm:.2f}/rng={c.range_nm:.2f}/t={c.tcpa_min:.0f}" for c in wd.contacts.values())
            print(f"t={w.tick:3d} hdg={s.heading:5.1f}/{s.cmd_heading:5.1f} spd={s.speed:4.1f}/{s.cmd_speed:4.1f} "
                  f"temp={s.engine_temp:5.1f} y={s.y:5.2f} {cs} | {w.summary()['executed']}ex {w.summary()['rejected']}rej "
                  f"{w.summary()['escalated']}esc {len(w.conflicts)}cf "
                  + " ".join(f"{a}:{r.identity.status[:4]}" for a, r in w.agents.items()))
    with tempfile.TemporaryDirectory() as d:
        w, wd, led, logs = run(secs, Path(d), show, clock)
        print("--- log (last 60) ---")
        for t, lv, src, msg in logs[-60:]:
            print(f"{t - 1_800_000_000:4.0f} {lv:6s} {src:8s} {msg}")
        print("verify:", led.verify())
        led.close()
