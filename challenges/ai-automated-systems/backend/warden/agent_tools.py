"""Calculator tools the LLM agents may call while deciding.

These are pure math over facts already on the COP. They never recommend, rank or choose anything: the model decides
WHICH calculation to run, reads the numbers, and makes the decision. Every call and result is recorded in the
decision's provenance.
"""
from __future__ import annotations

import ast
import copy
import math
import operator

from .shipsys import COMP, DEFENSES, TEAM_FRAMES_PER_S
from .world import cpa_tcpa, velocity

_OPS = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul, ast.Div: operator.truediv,
        ast.Pow: operator.pow, ast.USub: operator.neg, ast.UAdd: operator.pos, ast.Mod: operator.mod}
_FUNCS = {k: getattr(math, k) for k in ("sqrt", "sin", "cos", "tan", "atan2", "radians", "degrees", "exp", "log",
                                        "hypot", "floor", "ceil")} | {"abs": abs, "min": min, "max": max, "round": round}


def _safe_eval(node):
    if isinstance(node, ast.Expression):
        return _safe_eval(node.body)
    if isinstance(node, ast.Constant) and isinstance(node.value, (int, float)):
        return node.value
    if isinstance(node, ast.BinOp) and type(node.op) in _OPS:
        return _OPS[type(node.op)](_safe_eval(node.left), _safe_eval(node.right))
    if isinstance(node, ast.UnaryOp) and type(node.op) in _OPS:
        return _OPS[type(node.op)](_safe_eval(node.operand))
    if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id in _FUNCS:
        return _FUNCS[node.func.id](*[_safe_eval(a) for a in node.args])
    if isinstance(node, ast.Name) and node.id == "pi":
        return math.pi
    raise ValueError("only numbers, + - * / ** %, and math functions are allowed")


def _track(world, tid):
    c = world.contacts.get(str(tid).replace(" ", ""))
    if not c or c.hidden:
        raise ValueError(f"no track {tid} on the COP")
    return c


def tool_calc(world, args):
    expr = str(args.get("expression", ""))[:300]
    return {"expression": expr, "result": round(float(_safe_eval(ast.parse(expr, mode="eval"))), 6)}


def tool_cpa(world, args):
    """Closest point of approach to a track for own ship on its current, or a trial, heading/speed."""
    c, s = _track(world, args.get("track")), world.ship
    h = float(args.get("own_heading", s.heading))
    v = float(args.get("own_speed", s.speed))
    cpa, tcpa = cpa_tcpa((s.x, s.y), velocity(h, v), (c.x, c.y), velocity(c.course, c.speed))
    return {"track": c.id, "own_heading": h, "own_speed": v, "cpa_nm": round(cpa, 3), "tcpa_min": round(tcpa, 2)}


def tool_time_to_range(world, args):
    """Minutes until a track (closing at its current geometry) reaches a given range from own ship."""
    c, s = _track(world, args.get("track")), world.ship
    target = float(args.get("range_nm"))
    rel_v = [a - b for a, b in zip(velocity(c.course, c.speed), velocity(s.heading, s.speed))]
    rx, ry = c.x - s.x, c.y - s.y
    rng = math.hypot(rx, ry)
    closing = -(rx * rel_v[0] + ry * rel_v[1]) / rng if rng else 0.0      # kn, positive = closing
    minutes = (rng - target) / closing * 60 if closing > 0 and rng > target else None
    return {"track": c.id, "range_now_nm": round(rng, 2), "closing_speed_kn": round(closing, 1),
            "target_range_nm": target, "minutes": None if minutes is None else round(minutes, 2)}


def tool_hit_probability(world, args):
    """P(one inbound gets through) = product of (1 - pk) over the listed systems, using the published pk table."""
    systems = [str(x) for x in args.get("systems", [])]
    unknown = [x for x in systems if x not in DEFENSES]
    if unknown:
        raise ValueError(f"unknown systems {unknown}; known: {sorted(DEFENSES)}")
    p = 1.0
    for d in systems:
        p *= 1 - DEFENSES[d]
    n = int(args.get("inbound_count", 1))
    return {"systems": systems, "pk_table": {d: DEFENSES[d] for d in systems}, "p_one_gets_through": round(p, 4),
            "inbound_count": n, "p_at_least_one_hit": round(1 - (1 - p) ** n, 4)}


def tool_power_lineup(world, args):
    """What would be energized, and generator load, if these breakers were closed/opened. A what-if on a copy."""
    sim = copy.deepcopy(world.sys)
    for b in args.get("close", []):
        if b not in sim.breakers:
            raise ValueError(f"unknown breaker {b}")
        sim.breakers[b]["closed"] = True
    for b in args.get("open", []):
        if b not in sim.breakers:
            raise ValueError(f"unknown breaker {b}")
        sim.breakers[b]["closed"] = False
    sim.flow()
    return {"close": args.get("close", []), "open": args.get("open", []),
            "unpowered": [n for n, v in sim.nodes.items() if not v["powered"] and not v["failed"]],
            "generator_load_pct": sim.gen_load}


def tool_dc_transit(world, args):
    """Frames and seconds for a repair party to reach a compartment."""
    t = world.sys.teams.get(args.get("team"))
    comp = COMP.get(args.get("compartment"))
    if not t or not comp:
        raise ValueError("unknown team or compartment")
    mid = sum(comp["fr"]) / 2
    frames = abs(t["pos"] - mid)
    return {"team": t["id"], "from_frame": round(t["pos"]), "to_frame": mid, "frames": round(frames),
            "seconds": round(frames / TEAM_FRAMES_PER_S), "team_status": t["status"]}


def tool_weather_on_heading(world, args):
    """Clearance (nm) from each heavy rain cell's edge along a trial heading over the next 30 min. Negative = into it."""
    h = float(args.get("heading"))
    clear = world.storm_clearance(h % 360, args.get("speed"))
    return {"heading": h % 360, "clearance_nm_by_cell": clear,
            "min_clearance_nm": min(clear.values()) if clear else None, "runs_into_cell": any(v < 0 for v in clear.values())}

def tool_torpedo_evasion(world, args):
    """For a trial heading/speed: closing speed of a torpedo, time to reach us, and whether it runs out of fuel first."""
    from .shipsys import TORP_KN
    t = next((x for x in world.sys.torpedoes if x["id"] == str(args.get("torpedo"))), None)
    if not t:
        raise ValueError(f"no torpedo {args.get('torpedo')} in the water")
    h = float(args.get("own_heading", world.ship.heading)) % 360
    v = float(args.get("own_speed", world.ship.speed))
    closing = TORP_KN + v * math.cos(math.radians(h - t["bearing"]))
    t_reach = t["range"] / closing * 60 if closing > 0 else None
    t_fuel = t["run_left"] / TORP_KN * 60
    return {"torpedo": t["id"], "own_heading": h, "own_speed": v, "torpedo_bearing": t["bearing"],
            "closing_speed_kn": round(closing, 1), "minutes_to_reach_us": None if t_reach is None else round(t_reach, 2),
            "minutes_of_fuel_left": round(t_fuel, 2), "outruns_torpedo": t_reach is None or t_fuel < t_reach}


def tool_evasion_headings(world, args):
    """Both headings that put a threat bearing `offset` degrees off the bow, the turn each needs from cmd_heading,
    and the heavy-weather clearance on each."""
    from .world import angle_diff
    brg = float(args.get("threat_bearing")) % 360
    off = float(args.get("offset_deg", 45))
    cmd = world.ship.cmd_heading
    out = []
    for h in ((brg + off) % 360, (brg - off) % 360):
        clear = world.storm_clearance(h) if world.env.cells else {}
        out.append({"heading": round(h), "turn_from_cmd_deg": round(angle_diff(h, cmd)),
                    "min_weather_clearance_nm": min(clear.values()) if clear else None})
    return {"threat_bearing": brg, "cmd_heading": round(cmd), "options": out}


TOOLS = {
    "calc": (tool_calc, '{"expression": "1-(1-0.45)*(1-0.35)"}', "arithmetic and math functions"),
    "cpa": (tool_cpa, '{"track": "TN4002", "own_heading": 120, "own_speed": 8}',
            "CPA (nm) and TCPA (min) to a track; heading/speed optional for a trial maneuver"),
    "time_to_range": (tool_time_to_range, '{"track": "TN4006", "range_nm": 10}',
                      "closing speed and minutes until a track reaches a range"),
    "hit_probability": (tool_hit_probability, '{"systems": ["CIWS", "ADS"], "inbound_count": 2}',
                        "probability inbound get through a set of defenses (pk table)"),
    "power_lineup": (tool_power_lineup, '{"close": ["B-13"], "open": []}',
                     "what-if: equipment unpowered and generator load for a breaker lineup"),
    "weather_on_heading": (tool_weather_on_heading, '{"heading": 55}',
                           "clearance from each heavy rain cell along a trial heading (negative = runs into it)"),
    "torpedo_evasion": (tool_torpedo_evasion, '{"torpedo": "TN4003-T1", "own_heading": 270, "own_speed": 31}',
                        "torpedo closing speed and whether it runs out of fuel before reaching us on a trial heading/speed"),
    "evasion_headings": (tool_evasion_headings, '{"threat_bearing": 130, "offset_deg": 45}',
                         "the two headings putting a threat offset_deg off the bow, the turn each needs, weather clearance"),
    "dc_transit": (tool_dc_transit, '{"team": "R3", "compartment": "MMR2"}',
                   "frames and seconds for a repair party to reach a compartment"),
}
# Which calculators each role may use.
ROLE_TOOLS = {
    "collision": ["cpa", "weather_on_heading", "calc"], "navigation": ["weather_on_heading", "calc"],
    "weather": ["weather_on_heading", "calc"], "energy": ["calc"],
    "engine": ["calc"], "ew": ["time_to_range", "calc"],
    "defense": ["evasion_headings", "hit_probability", "calc", "time_to_range"],
    "asw": ["torpedo_evasion", "calc"],
    "power": ["power_lineup", "calc"], "damage_control": ["dc_transit", "calc"],
}


def run_tool(world, name: str, args: dict) -> dict:
    if name not in TOOLS:
        return {"error": f"unknown tool {name}"}
    try:
        return TOOLS[name][0](world, args if isinstance(args, dict) else {})
    except Exception as e:
        return {"error": str(e)[:200]}
