"""Run evaluation scenarios against the local LLM, in real time, with every agent set to act (auto-act).

    uv run python -m eval.run_eval --round r1                 # all scenarios
    uv run python -m eval.run_eval --round r1 --only S01 S05  # some

One process per run (clean model state). Everything is logged to eval/runs/<round>/<scenario>.jsonl:
every event the watch saw, every model answer (latency, tool calls, calculations, raw reply), every intent with
Warden's verdict. A summary per run goes to <scenario>.summary.json; eval/report.py builds the report.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import time
from pathlib import Path

BACKEND = Path(__file__).resolve().parent.parent
RUNS = BACKEND / "eval" / "runs"
ALL = {"nav", "systems", "dc"}


def run_one(sid: str, rnd: str) -> dict:
    os.environ.setdefault("WARDEN_DATA", tempfile.mkdtemp(prefix="warden-eval-"))
    sys.path.insert(0, str(BACKEND))
    from eval.scenarios import SCENARIOS
    from warden import llm_agent
    from warden.engine import Warden
    from warden.ledger import Ledger
    from warden.world import World

    sc = next(s for s in SCENARIOS if s.id == sid)
    out_dir = RUNS / rnd
    out_dir.mkdir(parents=True, exist_ok=True)
    log = (out_dir / f"{sid}.jsonl").open("w", encoding="utf-8")
    t_start = time.time()

    def rec(kind, **kw):
        log.write(json.dumps({"t": round(time.time() - t_start, 2), "type": kind, **kw}, default=str) + "\n")
        log.flush()

    world = World(time_scale=1.0)
    world.normalize()
    ledger = Ledger(Path(os.environ["WARDEN_DATA"]) / f"{sid}.db")
    warden = Warden(BACKEND / "config", ledger, world, lambda lv, src, msg: rec("event", level=lv, source=src, msg=msg))
    warden.auto_act = True                     # all permissions set to act
    orig = llm_agent.LLMAgentRuntime.on_decision

    def on_decision(agent_id, prov, actions):
        rec("model_answer", agent=agent_id, latency_ms=prov.get("latency_ms"), rounds=prov.get("rounds"),
            tools=prov.get("tools"), calculations=prov.get("calculations"), actions=actions, raw=prov.get("raw_reply"))
        orig(agent_id, prov, actions)
    llm_agent.LLMAgentRuntime.on_decision = on_decision
    sc.setup(world, warden)
    rec("start", scenario=sid, title=sc.title, doctrine=sc.doctrine, model=os.getenv("LLM_LOCAL_MODEL", ""))

    events = sorted(sc.events, key=lambda e: e[0])
    ops = list(sc.operator)
    trig_t = {e.name: None for e in sc.expects}
    tick = 0
    while True:
        el = time.time() - t_start
        while events and events[0][0] <= el:
            _, ev = events.pop(0)
            rec("inject", event=ev, line=world.apply_event(ev))
        if ops and ops[0][0](world):
            _, fn = ops.pop(0)
            fn(world, warden)
            rec("operator", what=fn.__code__.co_names[-1] if fn.__code__.co_names else "operator action")
        world.step(active=ALL)
        warden.step(active=ALL, dt=1.0)
        for e in sc.expects:
            if trig_t[e.name] is None and e.trigger(world):
                trig_t[e.name] = time.time() - t_start
                rec("trigger", expect=e.name)
        tick += 1
        if tick % 5 == 0:
            s = world.sys
            rec("state", inbound=[(m["id"], round(m["range"], 1)) for m in s.inbound_on_cop()],
                torpedoes=[(t["id"], round(t["range"], 2), round(t["run_left"], 2)) for t in s.torpedoes],
                weapons=[(i["id"], i["target"]) for i in s.interceptors], heading=round(world.ship.heading),
                speed=round(world.ship.speed, 1), gq=s.gq, hits=s.hits, magazine=s.magazine, emcon=s.emcon,
                hold_fire=warden.hold_fire, queue=llm_agent.SCHEDULER.depth())
        if el >= sc.cap_s or (el > 20 and sc.done(world)):
            break
        time.sleep(max(0.0, (t_start + tick) - time.time()))

    # ------------------------------------------------------------ score
    intents = sorted(warden.intents.values(), key=lambda i: i.created_at)
    for it in intents:
        rec("intent", id=it.id, agent=it.agent_id, action=it.action, params=it.params, status=it.status,
            decided_by=it.decided_by, reasons=it.reasons, policy_hits=it.policy_hits, risk=it.risk,
            alternative=it.alternative, at=round(it.created_at - t_start, 2),
            latency_ms=it.provenance.get("latency_ms"), rationale=it.rationale)
    results = []
    for e in sc.expects:
        t0 = trig_t[e.name]
        hit = next((i for i in intents if i.status == "executed" and i.action == e.action
                    and (not e.agents or i.agent_id in e.agents) and _safe(e.pred, i.params, world)), None)
        if t0 is None:
            results.append({"expect": e.name, "result": "n/a (trigger never happened)", "deadline_s": e.deadline_s})
            continue
        if hit is None:
            results.append({"expect": e.name, "result": "MISSED", "deadline_s": e.deadline_s})
            continue
        rt = max(0.0, hit.created_at - t_start - t0)
        results.append({"expect": e.name, "result": "on time" if rt <= e.deadline_s else "LATE", "reaction_s": round(rt, 1),
                        "deadline_s": e.deadline_s, "agent": hit.agent_id, "params": hit.params,
                        "model_latency_s": round((hit.provenance.get("latency_ms") or 0) / 1000, 1)})
    over = [{"agent": i.agent_id, "action": i.action, "params": i.params} for i in intents
            if i.status == "executed" and _safe(sc.overreact, i, world)]
    answers = [json.loads(x) for x in (out_dir / f"{sid}.jsonl").read_text(encoding="utf-8").splitlines()]
    lat = [a["latency_ms"] for a in answers if a["type"] == "model_answer" and a.get("latency_ms")]
    by_status = {}
    for i in intents:
        by_status[i.status] = by_status.get(i.status, 0) + 1
    blocked = {}
    for i in intents:
        if i.status == "rejected":
            k = (i.policy_hits[-1] if i.policy_hits else "OTHER")
            blocked[k] = blocked.get(k, 0) + 1
    s = world.sys
    summary = {
        "scenario": sid, "title": sc.title, "round": rnd, "doctrine": sc.doctrine,
        "model": os.getenv("LLM_LOCAL_MODEL", ""), "duration_s": round(time.time() - t_start, 1),
        "expectations": results, "overreactions": over,
        "model_answers": len(lat), "latency_s": {"mean": _r(sum(lat) / len(lat) / 1000) if lat else None,
                                                  "max": _r(max(lat) / 1000) if lat else None,
                                                  "min": _r(min(lat) / 1000) if lat else None},
        "failed_calls": sum(a.status.get("calls", 0) for a in []),
        "intents": by_status, "blocked_by_warden": blocked,
        "alternatives_offered": sum(1 for i in intents if i.alternative),
        "risk_filled": sum(1 for i in intents if i.risk), "intents_total": len(intents),
        "outcome": {"hits": s.hits, "torpedo_hits": s.torp_hits, "sm2_used": 24 - s.magazine if s.magazine <= 24 else 0,
                    "casualties_open": sum(1 for c in s.casualties.values() if c["status"] not in ("out", "repaired")),
                    "hostiles_left": sum(1 for c in world.contacts.values() if c.kind == "hostile"),
                    "gq": s.gq, "unpowered": s.unpowered_vital()},
        "llm_errors": [a.status["last_error"] for a in warden.agents.values()
                       if getattr(a, "status", None) and a.status.get("last_ok") is False],
        "actions_timeline": [{"at": round(i.created_at - t_start, 1), "agent": i.agent_id, "action": i.action,
                              "params": {k: v for k, v in i.params.items() if k != "boundaries"}, "status": i.status,
                              "why_blocked": i.reasons[0] if i.status == "rejected" and i.reasons else ""}
                             for i in intents],
    }
    (out_dir / f"{sid}.summary.json").write_text(json.dumps(summary, indent=1, default=str), encoding="utf-8")
    rec("end", summary={k: summary[k] for k in ("expectations", "outcome", "latency_s")})
    log.close()
    return summary


def _r(x):
    return round(x, 1)


def _safe(fn, *a):
    try:
        return bool(fn(*a))
    except Exception:
        return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--round", required=True)
    ap.add_argument("--only", nargs="*")
    ap.add_argument("--child")
    a = ap.parse_args()
    if a.child:
        s = run_one(a.child, a.round)
        print(json.dumps({"id": a.child, "expect": [(e["expect"], e["result"], e.get("reaction_s")) for e in s["expectations"]],
                          "over": len(s["overreactions"]), "outcome": s["outcome"], "lat": s["latency_s"]}), flush=True)
        os._exit(0)          # the model worker thread is a daemon; leave without waiting for it
    sys.path.insert(0, str(BACKEND))
    from eval.scenarios import SCENARIOS
    ids = a.only or [s.id for s in SCENARIOS]
    for sid in ids:
        print(f"== {a.round} {sid}", flush=True)
        subprocess.run([sys.executable, "-m", "eval.run_eval", "--round", a.round, "--child", sid], cwd=BACKEND)


if __name__ == "__main__":
    main()
