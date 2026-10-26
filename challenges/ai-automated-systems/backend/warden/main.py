"""Bridge Warden server: tick loop, REST API, WebSocket, and the static operator UI.

Run:  uv run uvicorn warden.main:app --port 8000      (from backend/)
Env:  PROFILE=demo|edge   TIME_SCALE=1   SCENARIO=ops_normal|firefight|full_demo   OPERATOR=OOD
"""
from __future__ import annotations

import asyncio
import random
import json
import os
import time
from collections import deque
from contextlib import asynccontextmanager
from pathlib import Path

from fastapi import FastAPI, HTTPException, WebSocket, WebSocketDisconnect
from fastapi.staticfiles import StaticFiles
from pydantic import BaseModel

from . import llm
from .engine import Warden
from .llm_agent import SCHEDULER
from .ledger import Ledger
from .resources import ResourceMeter
from .world import Scenario, World

BACKEND = Path(__file__).resolve().parent.parent
FRONTEND = BACKEND.parent / "frontend"
PROFILE = os.getenv("PROFILE", "demo")
PROFILES = {
    # tick_s: governance cycle; ui_every: push a UI snapshot every N ticks
    "demo": {"tick_s": 1.0, "ui_every": 1},
    "edge": {"tick_s": 1.0, "ui_every": 2},
}
CFG = PROFILES.get(PROFILE, PROFILES["demo"])
OPERATOR = os.getenv("OPERATOR", "OOD")
BUDGETS = {"cpu_cores": 1.0, "rss_mb": 500, "ui_kbps": 50, "intent_bytes": 1024, "profile": PROFILE}

logs: deque[dict] = deque(maxlen=400)
pending_logs: list[dict] = []


def emit(level: str, source: str, message: str) -> None:
    line = {"ts": time.time(), "level": level, "source": source, "message": message}
    logs.append(line)
    pending_logs.append(line)


world = World(time_scale=float(os.getenv("TIME_SCALE", "1")))      # real time (1x)
ledger = Ledger(BACKEND / "data" / "ledger.db")
warden = Warden(BACKEND / "config", ledger, world, emit)
scenario = Scenario(BACKEND / "scenarios" / f"{os.getenv('SCENARIO', 'ops_normal')}.yaml")
meter = ResourceMeter(BUDGETS)
clients: set[WebSocket] = set()
state = {"started": time.time(), "running": False, "active_cop": "nav", "auto_inject": False, "next_auto": 0.0, "last_auto": ""}
AUTO_POOL = ["contact", "storm", "overheat", "hostile", "inbound", "engine-loss", "fire", "flooding", "rogue"]


def full_state() -> dict:
    return {
        **warden.snapshot(),
        "cop": world.cop(),
        "presenter": {"active_cop": state["active_cop"], "auto_inject": state["auto_inject"], "last_auto": state["last_auto"],
                      "next_auto_s": round(max(0, state["next_auto"] - time.time())) if state["auto_inject"] else None,
                      "time_scale": world.time_scale},
        "scenario": {"name": scenario.name, "running": scenario.running, "elapsed": round(scenario.elapsed, 1),
                     "finished": scenario.finished},
        "resources": meter.sample(),
        "server": {"uptime_s": round(time.time() - state["started"]), "operator": OPERATOR,
                   "profile": PROFILE, "running": state["running"],
                   "llm": {k: v for k, v in llm.settings().items() if k in ("mode", "provider", "model", "local_model", "base_url",
                                                                         "reasoning_effort", "include_thoughts")},
                   "agents_llm": {**SCHEDULER.stats, "queue": SCHEDULER.depth()}},
    }


async def broadcast(msg: dict) -> None:
    if not clients:
        return
    data = json.dumps(msg, default=str)
    for ws in list(clients):
        try:
            await ws.send_text(data)
            meter.count_ws(len(data))
        except Exception:
            clients.discard(ws)


async def tick_loop() -> None:
    n = 0
    while True:
        await asyncio.sleep(CFG["tick_s"])
        if not state["running"]:          # paused: world, agents and model frozen; screens still refresh
            n += 1
            if n % CFG["ui_every"] == 0:
                msg = {"type": "state", "data": full_state(), "logs": list(pending_logs)}
                pending_logs.clear()
                await broadcast(msg)
            continue
        for ev in scenario.due(CFG["tick_s"]):
            if "rogue_agent" in ev:
                aid = ev["rogue_agent"]["id"]
                warden.agents[aid].rogue = True
                emit("error", "scenario", f"Agent {aid} compromised (fault injection)")
                continue
            line = world.apply_event(ev)
            if line:
                emit("milestone" if "phase" in ev else "engine", "scenario", line)
        if state["auto_inject"] and scenario.running and time.time() >= state["next_auto"]:
            pool = [f for f in AUTO_POOL if f != "inbound" or any(c.kind == "hostile" for c in world.contacts.values())]
            fault = random.choice(pool)
            state["last_auto"] = fault
            state["next_auto"] = time.time() + random.uniform(180, 300)     # a realistic pace: one issue every 3-5 min
            emit("system", "auto-inject", f"Auto injection: {fault}")
            inject(fault, "auto-inject")
        active = {state["active_cop"]}
        world.step(active=active)
        t0 = time.perf_counter()
        warden.step(active=active, dt=CFG["tick_s"])
        meter.record_pipeline((time.perf_counter() - t0) * 1000)
        for it in warden.intents.values():
            if it.created_at > time.time() - CFG["tick_s"] * 1.5:
                meter.intent_bytes_max = max(meter.intent_bytes_max, len(it.model_dump_json(
                    include={"agent_id", "kind", "action", "params", "target_resource", "objective", "rationale",
                             "confidence"})))
        n += 1
        if n % CFG["ui_every"] == 0:
            msg = {"type": "state", "data": full_state(), "logs": list(pending_logs)}
            pending_logs.clear()
            await broadcast(msg)


@asynccontextmanager
async def lifespan(app: FastAPI):
    ledger.reset()
    world.normalize()
    emit("milestone", "system", f"WARDEN CORE STARTED · profile {PROFILE} · {len(warden.agents)} agents registered")
    emit("system", "system", "Normal conditions · simulation PAUSED · press Start Operations")
    ledger.append("SYSTEM", "warden", {"event": "start", "profile": PROFILE})
    task = asyncio.create_task(tick_loop())
    yield
    task.cancel()


app = FastAPI(title="Bridge Warden", lifespan=lifespan)


def _agent(aid: str):
    if aid not in warden.agents:
        raise HTTPException(404, f"Unknown agent {aid}")
    return warden.agents[aid]


class AutonomyIn(BaseModel):
    level: int


class OverrideIn(BaseModel):
    action: str
    value: float


class LLMEndpointIn(BaseModel):
    base_url: str
    api_key: str = ""     # blank = keep the current key
    model: str = ""       # blank = first model the server lists at /models
    reasoning_effort: str = ""      # none | low | medium | high; blank = don't send
    include_thoughts: bool = False  # ask Gemini for thought summaries


class RollbackIn(BaseModel):
    agent_id: str | None = None
    count: int = 1


@app.get("/api/state")
def api_state():
    return full_state()


@app.get("/api/summary")
def api_summary():
    return warden.summary()


@app.get("/api/agents")
def api_agents():
    return warden.snapshot()["agents"]


@app.post("/api/agents/{aid}/{action}")
def api_agent_action(aid: str, action: str):
    _agent(aid)
    if action == "pause":
        warden.pause(aid, OPERATOR)
    elif action in ("resume", "release"):
        warden.resume(aid, OPERATOR)
    elif action == "quarantine":
        warden.quarantine(aid, OPERATOR, "operator decision")
    else:
        raise HTTPException(400, f"Unknown action {action}")
    return {"ok": True}


@app.patch("/api/agents/{aid}/autonomy")
def api_autonomy(aid: str, body: AutonomyIn):
    _agent(aid)
    warden.set_autonomy(aid, body.level, OPERATOR)
    return {"ok": True}


@app.post("/api/llm/endpoint")
def api_llm_endpoint(body: LLMEndpointIn):
    """Point the agents at another OpenAI-compatible /v1 server. Saved to backend/.env; the key is never echoed."""
    url = body.base_url.strip().rstrip("/")
    if not url.startswith(("http://", "https://")):
        raise HTTPException(400, "URL must start with http:// or https://")
    if body.reasoning_effort and body.reasoning_effort not in llm.REASONING_EFFORTS:
        raise HTTPException(400, f"reasoning_effort must be one of {', '.join(llm.REASONING_EFFORTS)}")
    llm.set_endpoint(url, body.api_key.strip() or None, body.model.strip(), body.reasoning_effort,
                     body.include_thoughts)
    emit("info", "warden", f"AI model server set to {url} ({body.model.strip() or 'auto model'}) by {OPERATOR}")
    return {"ok": True, "base_url": url, "model": body.model.strip()}


@app.get("/api/cop")
def api_cop():
    return world.cop()


@app.get("/api/intents/{iid}/explain/stream")
def api_explain_stream(iid: str):
    """The same explanation as /explain, sent as Server-Sent Events while the model writes it.
    Events: thinking · thought (text) · content (text) · replace (whole text) · done (source, timings)."""
    from fastapi.responses import StreamingResponse
    from .explain import intent_inputs
    it = warden.intents.get(iid)
    if not it:
        raise HTTPException(404)

    def events():
        started = time.perf_counter()
        flushed_ms = None
        for kind, val in llm.stream("explain_intent", intent_inputs(it)):
            if kind == "done":
                data = {k: getattr(val, k) for k in ("source", "provider", "model", "latency_ms", "prompt_chars",
                                                     "headers_ms", "first_token_ms", "ok", "error")}
                data["first_flush_ms"] = flushed_ms
                emit("info", "llm", f"Explain {iid}: {val.source} · prompt {val.prompt_chars} chars · headers "
                                    f"{val.headers_ms} ms · first token {val.first_token_ms} ms · first sent to "
                                    f"browser {flushed_ms} ms · total {val.latency_ms} ms")
            else:
                data = val
                if kind in ("content", "replace", "thought") and flushed_ms is None:
                    flushed_ms = round((time.perf_counter() - started) * 1000, 1)
            yield f"event: {kind}\ndata: {json.dumps(data)}\n\n"

    # No compression middleware is installed, so nothing buffers the stream; these headers stop proxies doing it.
    return StreamingResponse(events(), media_type="text/event-stream",
                             headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"})


@app.get("/api/intents/{iid}/explain")
def api_explain(iid: str):
    from .explain import intent_story
    it = warden.intents.get(iid)
    if not it:
        raise HTTPException(404)
    r = intent_story(it)
    return {"intent": it.model_dump(), "story": r.text, "source": r.source, "provider": r.provider, "model": r.model}


@app.post("/api/escalations/{eid}/{decision}")
def api_escalation(eid: str, decision: str):
    if eid not in warden.escalations or decision not in ("approve", "reject", "option-b"):
        raise HTTPException(404)
    return warden.decide_escalation(eid, decision == "approve", OPERATOR,
                                    choice="b" if decision == "option-b" else "a").model_dump()


@app.post("/api/control/override")
def api_override(body: OverrideIn):
    return warden.override(OPERATOR, body.action, body.value).model_dump()


@app.post("/api/control/gq/{mode}")
def api_gq(mode: str):
    """General Quarters is an operator order, never an agent's."""
    world.sys.set_gq(mode == "on")
    ledger.append("CONTROL", OPERATOR, {"action": "general_quarters", "on": mode == "on"})
    return {"gq": world.sys.gq}


@app.post("/api/control/auto-act/{mode}")
def api_auto_act(mode: str):
    """on = agents may act without asking (Warden checks still apply); off = every governed action waits."""
    warden.set_auto_act(mode == "on", OPERATOR)
    return {"auto_act": warden.auto_act}


@app.post("/api/control/cease-fire")
def api_cease_fire():
    return {"destructed": warden.cease_fire(OPERATOR), "hold_fire": True}


@app.post("/api/control/hold-fire/lift")
def api_lift_hold():
    warden.lift_hold_fire(OPERATOR)
    return {"hold_fire": False}


@app.post("/api/control/rollback")
def api_rollback(body: RollbackIn):
    return {"undone": warden.rollback(OPERATOR, body.agent_id, body.count)}


@app.post("/api/control/safe-stop")
def api_safe_stop():
    warden.engage_safe_stop(OPERATOR)
    return {"ok": True}


@app.post("/api/control/resume-all")
def api_resume_all():
    warden.resume_all(OPERATOR)
    return {"ok": True}


@app.get("/api/ledger")
def api_ledger(limit: int = 200, type: str | None = None):
    return ledger.rows(limit, type)


@app.get("/api/ledger/verify")
def api_verify():
    result = ledger.verify()
    emit("ok" if result["valid"] else "error", "ledger",
         f"Chain {'valid' if result['valid'] else 'BROKEN at ' + str(result['broken_at'])} ({result['rows']} rows)")
    return result


@app.post("/api/view/{cop}")
def api_view(cop: str):
    """The main window reports which COP is on screen. Only that COP's world and agents run."""
    if cop not in ("nav", "systems", "dc"):
        raise HTTPException(400)
    if state["active_cop"] != cop:
        state["active_cop"] = cop
        emit("system", OPERATOR, f"On screen: {cop.upper()} COP · other COPs paused")
    return {"active_cop": cop}


@app.post("/api/presenter/normal")
def api_normal():
    """Set normal conditions: calm seas, no contacts, all systems up, ops normal, ready for the presenter."""
    global scenario
    scenario = Scenario(BACKEND / "scenarios" / "ops_normal.yaml")
    scenario.running = True
    scenario.due(0)
    world.normalize()
    warden.normalize(OPERATOR)
    if not warden.checks_enabled:
        warden.enable_system_checks()
    return {"ok": True}


@app.post("/api/presenter/firefight")
def api_firefight():
    """Start the scripted firefight from normal conditions."""
    global scenario
    api_normal()
    scenario = Scenario(BACKEND / "scenarios" / "firefight.yaml")
    scenario.running = True
    state["running"] = True
    emit("milestone-red", OPERATOR, "AUTO FIREFIGHT STARTED: two hostiles, two salvos")
    return {"ok": True}


@app.post("/api/presenter/auto-inject/{mode}")
def api_auto_inject(mode: str):
    """auto = random issue every 60-120 s; manual = only the presenter injects."""
    state["auto_inject"] = mode == "auto"
    state["next_auto"] = time.time() + 45
    emit("system", OPERATOR, f"Injection mode: {'AUTO (random issue every 3-5 min)' if state['auto_inject'] else 'MANUAL'}")
    return {"auto_inject": state["auto_inject"]}


@app.post("/api/scenario/{action}")
def api_scenario(action: str):
    if action == "start":
        state["running"] = True
        scenario.running = True
        for ev in scenario.due(0):          # apply t=0 events now, so a quick inject isn't overwritten next tick
            line = world.apply_event(ev)
            if line:
                emit("milestone" if "phase" in ev else "engine", "scenario", line)
        warden.enable_system_checks()
        emit("milestone", OPERATOR, f"OPERATIONS STARTED: {scenario.name} · system check every "
             f"{warden.policies['system_check_interval_s']} s")
    elif action == "pause":
        state["running"] = False
        emit("milestone", OPERATOR, "SIMULATION PAUSED")
    elif action == "reset":
        globals()["scenario"] = Scenario(BACKEND / "scenarios" / "ops_normal.yaml")
        state["auto_inject"] = False
        state["running"] = False
        world.reset()
        world.normalize()
        warden.reset()
        ledger.reset()
        logs.clear()
        emit("milestone", OPERATOR, "SCENARIO RESET")
    else:
        raise HTTPException(400)
    return {"ok": True}


INJECTIONS = {
    "contact": {"spawn_contact": {"course": 0, "speed": 12, "collision_in_min": 14, "classification": "MERCHANT"}},
    "storm": {"weather": {"wind_kts": 38, "visibility_nm": 1.0, "sea_state": 6}},
    "calm": {"weather": {"wind_kts": 10, "visibility_nm": 8.0, "sea_state": 2}},
    "overheat": {"engine_fault": {"severity": 1.0}},
    "clear-fault": {"clear_fault": True},
    "hostile": {"hostile": {"bearing": 40, "range_nm": 19.0, "speed": 32}},
    "inbound": {"inbound": {"count": 2}},
    "engine-loss": {"engine_loss": True},
    "submarine": {"submarine": {"bearing": -60, "range_nm": 8.5}},
    "fire": {"casualty": {"comp": "MER2", "kind": "fire", "severity": 0.6}},
    "flooding": {"casualty": {"comp": "STEER", "kind": "flood", "severity": 0.5}},
}


# Phase label the room sees for each presenter injection.
INJECT_PHASE = {"contact": "Crossing contact", "storm": "Heavy weather", "overheat": "Engine fault",
                "rogue": "Compromised agent", "calm": "Ops normal", "clear-fault": "Ops normal",
                "hostile": "Unknown track closing", "inbound": "Inbound attack", "engine-loss": "Loss of Shaft 2",
                "fire": "Fire in Main Engine Room 2", "submarine": "Submarine threat", "flooding": "Flooding aft (steering)"}


@app.post("/api/inject/{fault}")
def api_inject(fault: str):
    return inject(fault, OPERATOR)


def inject(fault: str, by: str) -> dict:
    if not state["running"]:              # an injected issue into a paused sim would silently do nothing
        state["running"] = True
        scenario.running = True
        if not warden.checks_enabled:
            warden.enable_system_checks()
        emit("milestone", by, "OPERATIONS STARTED (issue injected while paused)")
    if fault == "system-check":
        warden.open_system_check(by)
        return {"ok": True}
    if fault in INJECT_PHASE:
        world.phase = INJECT_PHASE[fault]
        emit("milestone", by, f"Phase: {world.phase}")
    if fault == "rogue":
        warden.agents["nrg-01"].rogue = True
        emit("error", by, "Agent nrg-01 compromised (fault injection)")
        return {"ok": True}
    if fault not in INJECTIONS:
        raise HTTPException(404)
    ev = dict(INJECTIONS[fault])
    if fault == "hostile":
        n = sum(1 for c in world.contacts.values() if c.kind == "hostile")
        ev["hostile"] = {**ev["hostile"], "bearing": 40 - 25 * n}
    emit("engine", by, world.apply_event(ev))
    return {"ok": True}


@app.get("/api/health/resources")
def api_resources():
    return meter.sample()


@app.websocket("/ws")
async def ws_endpoint(ws: WebSocket):
    await ws.accept()
    clients.add(ws)
    try:
        await ws.send_text(json.dumps({"type": "state", "data": full_state(), "logs": list(logs)}, default=str))
        while True:
            await ws.receive_text()
    except WebSocketDisconnect:
        clients.discard(ws)


class NoCacheStatic(StaticFiles):
    """UI files are never cached, so an updated UI shows up on the next reload."""

    async def get_response(self, path, scope):
        resp = await super().get_response(path, scope)
        resp.headers["Cache-Control"] = "no-store"
        return resp


app.mount("/", NoCacheStatic(directory=FRONTEND, html=True), name="ui")
