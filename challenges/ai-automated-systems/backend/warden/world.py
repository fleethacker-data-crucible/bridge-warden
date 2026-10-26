"""Simulated world: own ship, contacts, engine, weather, and the scenario runner.

Coordinates are nautical miles on a local flat plane (x east, y north).
Headings are degrees true (0 = north, clockwise). Speeds are knots (nm/h).
All data is synthetic and seeded. No real ship data.
"""
from __future__ import annotations

import math
from pathlib import Path

import yaml
from pydantic import BaseModel

from .shipsys import ShipSystems


def wrap360(deg: float) -> float:
    return deg % 360.0


def angle_diff(a: float, b: float) -> float:
    """Signed smallest difference a - b in degrees, in (-180, 180]."""
    d = (a - b + 180.0) % 360.0 - 180.0
    return 180.0 if d == -180.0 else d


def velocity(heading: float, speed: float) -> tuple[float, float]:
    r = math.radians(heading)
    return speed * math.sin(r), speed * math.cos(r)


def cpa_tcpa(own_xy, own_v, c_xy, c_v) -> tuple[float, float]:
    """Closest point of approach (nm) and time to it (minutes). TCPA < 0 means opening."""
    rx, ry = c_xy[0] - own_xy[0], c_xy[1] - own_xy[1]
    vx, vy = c_v[0] - own_v[0], c_v[1] - own_v[1]
    v2 = vx * vx + vy * vy
    if v2 < 1e-9:
        return math.hypot(rx, ry), 0.0
    t = -(rx * vx + ry * vy) / v2           # hours
    if t < 0:
        return math.hypot(rx, ry), t * 60
    return math.hypot(rx + vx * t, ry + vy * t), t * 60


class Contact(BaseModel):
    id: str
    x: float
    y: float
    course: float
    speed: float
    cpa_nm: float = 99.0
    tcpa_min: float = 0.0
    range_nm: float = 0.0
    bearing: float = 0.0
    kind: str = "neutral"        # neutral | hostile (ground truth; what the watch knows is "classification")
    classification: str = "UNKNOWN"   # MERCHANT | FISHING | UNKNOWN | SUSPECT | HOSTILE
    domain: str = "surface"      # surface | subsurface
    hidden: bool = False         # a submerged submarine nobody holds on sonar yet: not on the COP


class OwnShip(BaseModel):
    x: float = 0.0
    y: float = 0.0
    heading: float = 90.0
    speed: float = 14.0
    cmd_heading: float = 90.0
    cmd_speed: float = 14.0
    engine_temp: float = 84.0
    fault_heat: float = 0.0
    power_margin_kw: float = 0.0
    fuel_rate: float = 0.0


class Environment(BaseModel):
    wind_kts: float = 12.0
    visibility_nm: float = 8.0
    sea_state: int = 2
    wave_height_m: float = 0.8
    wind_from: float = 200.0
    # Weather-radar rain cells, world coordinates: {id, x, y, r (nm), intensity 0-1, course, speed}
    cells: list[dict] = []


def squall_line(ship, rng_seed: int = 3) -> list[dict]:
    """A line of storm cells 7-13 nm ahead and to starboard, drifting toward the ship's track."""
    import random
    rnd = random.Random(rng_seed)
    out = []
    for i, (brg, r, rad, inten) in enumerate([(15, 9, 2.2, 0.9), (35, 8, 1.8, 0.75), (55, 10, 2.5, 0.6),
                                              (-5, 12, 1.6, 0.5), (75, 12, 2.0, 0.45)]):
        b = math.radians(ship.heading + brg + rnd.uniform(-4, 4))
        out.append({"id": f"WX{i + 1}", "x": ship.x + r * math.sin(b), "y": ship.y + r * math.cos(b),
                    "r": rad, "intensity": inten, "course": 290, "speed": 14})
    return out


TURN_RATE_DPS = 1.0       # degrees per sim second
ACCEL_KN_PS = 0.05        # knots per sim second
ROUTE_HEADING = 90.0      # planned track: due east along y = 0


class World:
    def __init__(self, time_scale: float = 10.0):
        self.time_scale = time_scale
        self.reset()

    def reset(self) -> None:
        self.sim_time = 0.0
        self.ship = OwnShip()
        self.env = Environment()
        self.contacts: dict[str, Contact] = {}
        self.phase = "Standby"
        self.sys = ShipSystems()
        self._tn = 4000
        self._update_derived()

    def next_track(self) -> str:
        self._tn += 1
        return f"TN{self._tn}"

    def normalize(self) -> None:
        """Back to normal conditions without moving the ship: routine traffic, calm weather, all systems restored."""
        self.contacts.clear()
        self.env = Environment()
        self.ship.fault_heat = 0.0
        self.sys = ShipSystems()
        self.phase = "Ops normal"
        self.spawn_traffic()
        self._update_derived()

    # Routine shipping: (bearing relative to bow, range nm, course, speed kn, classification).
    TRAFFIC = [(300, 16, 120, 13, "MERCHANT"), (60, 9, 205, 4, "FISHING"), (20, 18, 255, 16, "MERCHANT"),
               (150, 11, 10, 5, "FISHING"), (230, 14, 60, 9, "UNKNOWN")]

    MAX_TRACKS = 5      # the Navigation COP shows at most 5 targets

    def make_room(self) -> None:
        """Keep the Navigation COP at MAX_TRACKS: drop the farthest routine track (never a threat)."""
        while len(self.contacts) >= self.MAX_TRACKS:
            routine = [c for c in self.contacts.values()
                       if c.kind != "hostile" and c.classification in ("MERCHANT", "FISHING", "UNKNOWN")]
            if not routine:
                break
            del self.contacts[max(routine, key=lambda c: c.range_nm).id]

    def spawn_traffic(self) -> None:
        s = self.ship
        for brg, r, course, spd, cls in self.TRAFFIC[:3]:      # leave room for the presenter's injections
            b = math.radians(brg + s.heading)
            c = Contact(id=self.next_track(), x=s.x + r * math.sin(b), y=s.y + r * math.cos(b),
                        course=course, speed=spd, classification=cls)
            self.contacts[c.id] = c

    # --- physics ---
    def step(self, ticks: float = 1.0, active: set[str] | None = None) -> None:
        """Advance only the COPs on screen: nav = ship motion, traffic, weather, threats; systems = machinery;
        dc = fire, flooding and repair parties. A COP that isn't showing stays frozen."""
        dt = self.time_scale * ticks          # sim seconds
        on = active or {"nav", "systems", "dc"}
        s = self.ship
        if "nav" not in on:
            if "systems" in on:
                target_temp = 62 + s.speed * 1.6 + s.fault_heat
                s.engine_temp += (target_temp - s.engine_temp) * min(1.0, dt / 120)
            self.sim_time += dt
            self.sys.step(dt, self, on)
            self._update_derived()
            return
        # Steering lost: rudder stays where it is. Propulsion lost: the ship coasts down.
        dh = angle_diff(s.cmd_heading, s.heading) if self.sys.steering_ok else 0.0
        s.heading = wrap360(s.heading + max(-TURN_RATE_DPS * dt, min(TURN_RATE_DPS * dt, dh)))
        ds = min(s.cmd_speed, self.sys.max_speed_kn) - s.speed      # one shaft caps speed; none, the ship coasts
        s.speed += max(-ACCEL_KN_PS * dt, min(ACCEL_KN_PS * dt, ds))
        vx, vy = velocity(s.heading, s.speed)
        s.x += vx * dt / 3600
        s.y += vy * dt / 3600
        target_temp = 62 + s.speed * 1.6 + s.fault_heat
        s.engine_temp += (target_temp - s.engine_temp) * min(1.0, dt / 120)
        for c in self.contacts.values():
            cvx, cvy = velocity(c.course, c.speed)
            c.x += cvx * dt / 3600
            c.y += cvy * dt / 3600
        for cell in self.env.cells:
            wvx, wvy = velocity(cell["course"], cell["speed"])
            cell["x"] += wvx * dt / 3600
            cell["y"] += wvy * dt / 3600
        self.sim_time += dt
        self.sys.step(dt, self, on)
        self._update_derived()

    def _update_derived(self) -> None:
        s = self.ship
        s.power_margin_kw = round(520 - s.speed * 22 - s.fault_heat * 2, 1)
        s.fuel_rate = round(0.02 * s.speed ** 2.2, 2)
        own_v = velocity(s.heading, s.speed)
        for c in self.contacts.values():
            c.cpa_nm, c.tcpa_min = cpa_tcpa((s.x, s.y), own_v, (c.x, c.y), velocity(c.course, c.speed))
            dx, dy = c.x - s.x, c.y - s.y
            c.range_nm = math.hypot(dx, dy)
            c.bearing = wrap360(math.degrees(math.atan2(dx, dy)))
        # Drop contacts that are far astern and opening.
        for cid in [k for k, c in self.contacts.items() if c.range_nm > 25 and c.tcpa_min < 0]:
            del self.contacts[cid]

    def predicted_min_cpa(self, heading: float | None = None, speed: float | None = None) -> float:
        """Minimum CPA over approaching contacts if own ship ran at this heading/speed now."""
        s = self.ship
        own_v = velocity(s.heading if heading is None else heading, s.speed if speed is None else speed)
        best = 99.0
        for c in self.contacts.values():
            cpa, tcpa = cpa_tcpa((s.x, s.y), own_v, (c.x, c.y), velocity(c.course, c.speed))
            if 0 <= tcpa <= 45 and c.kind != "hostile" and not c.hidden:
                best = min(best, cpa)
        return best

    def threats(self, min_cpa: float, horizon_min: float = 30) -> list[Contact]:
        return [c for c in self.contacts.values()
                if c.kind != "hostile" and c.cpa_nm < min_cpa and 0 <= c.tcpa_min <= horizon_min]

    # --- scenario events ---
    def apply_event(self, ev: dict) -> str:
        """Apply one scenario event. Returns a log line."""
        if "phase" in ev:
            self.phase = ev["phase"]
            return f"Phase: {self.phase}"
        if "spawn_contact" in ev:
            p = ev["spawn_contact"]
            s = self.ship
            t_h = p.get("collision_in_min", 15) / 60
            # Put the contact on a course that meets own ship's projected position at t_h.
            ox, oy = velocity(s.heading, s.speed)
            mx, my = s.x + ox * t_h, s.y + oy * t_h
            cvx, cvy = velocity(p["course"], p["speed"])
            off = p.get("offset_nm", 0.0)
            self.make_room()
            c = Contact(id=p.get("id") or self.next_track(), x=mx - cvx * t_h + off, y=my - cvy * t_h,
                        course=p["course"], speed=p["speed"], classification=p.get("classification", "MERCHANT"))
            self.contacts[c.id] = c
            self._update_derived()
            return (f"CIC: new track {c.id}, {c.classification.lower()}, bearing {c.bearing:03.0f}, "
                    f"{c.range_nm:.1f} nm, CPA {c.cpa_nm:.2f} nm")
        if "weather" in ev:
            upd = dict(ev["weather"])
            heavy = upd.get("wind_kts", self.env.wind_kts) >= 30
            upd.setdefault("wave_height_m", 3.8 if heavy else 0.8)
            upd["cells"] = squall_line(self.ship) if heavy else []
            self.env = self.env.model_copy(update=upd)
            return (f"OOD: weather radar shows a squall line ahead. Wind {self.env.wind_kts:.0f} kts, "
                    f"waves {self.env.wave_height_m:.1f} m, visibility {self.env.visibility_nm:.1f} nm" if heavy else
                    f"OOD: weather moderating. Wind {self.env.wind_kts:.0f} kts, waves {self.env.wave_height_m:.1f} m")
        if "engine_fault" in ev:
            self.ship.fault_heat = 25 * ev["engine_fault"].get("severity", 1.0)
            return "Engine fault injected: cooling degraded"
        if "clear_fault" in ev:
            self.ship.fault_heat = 0.0
            return "Engine fault cleared"
        if "hostile" in ev:
            p, s = ev["hostile"], self.ship
            brg = math.radians(p.get("bearing", 40) + s.heading)
            r = p.get("range_nm", 17.0)
            self.make_room()
            c = Contact(id=self.next_track(), x=s.x + r * math.sin(brg), y=s.y + r * math.cos(brg), course=0,
                        speed=p.get("speed", 32), kind="hostile", classification="UNKNOWN")
            self.contacts[c.id] = c
            self._update_derived()
            return (f"CIC: new surface track {c.id}, bearing {c.bearing:03.0f}, {c.range_nm:.1f} nm, "
                    f"{c.speed:.0f} kn, unknown, no AIS")
        if "submarine" in ev:
            # Submerged and quiet: not on the COP until the sonar holds it (or it fires).
            p, s = ev["submarine"], self.ship
            brg = math.radians(p.get("bearing", -60) + s.heading)
            r = p.get("range_nm", 9.0)
            self.make_room()
            c = Contact(id=self.next_track(), x=s.x + r * math.sin(brg), y=s.y + r * math.cos(brg), course=0,
                        speed=p.get("speed", 12), kind="hostile", classification="UNKNOWN", domain="subsurface", hidden=True)
            self.contacts[c.id] = c
            self._update_derived()
            return f"Presenter: submarine {c.id} placed {c.range_nm:.1f} nm, bearing {c.bearing:03.0f} (submerged, not yet detected)"
        if "inbound" in ev:
            # Launched from over the horizon, beyond the 20 nm COP: nobody sees it until it crosses 20 nm.
            p = ev["inbound"]
            brg = wrap360(self.ship.heading + p.get("relative_bearing", 40))
            rng = p.get("range_nm", 28.0)
            self.sys.launch("OTH", rng, p.get("count", 2), bearing=brg)
            return (f"Presenter: {p.get('count', 2)} inbound launched from {rng:.0f} nm (beyond the COP), bearing "
                    f"{brg:03.0f}. Not yet detected: enters the COP in ~{(rng - 20) / 500 * 3600:.0f} s")
        if "engine_loss" in ev:
            self.sys.add_casualty("MER2", "equipment", 0.0, nodes=["SHAFT-2"], reason="GTM 2A/2B casualty, low lube oil pressure")
            return "Loss of Shaft 2 (GTM 2A/2B)"
        if "casualty" in ev:
            p = ev["casualty"]
            self.sys.add_casualty(p["comp"], p.get("kind", "fire"), p.get("severity", 0.6))
            return f"Casualty injected: {p.get('kind', 'fire')} in {p['comp']}"
        return ""

    def storm_clearance(self, heading: float, speed: float | None = None, minutes: float = 30,
                        min_intensity: float = 0.6) -> dict:
        """Closest the ship's straight track would come to each heavy rain cell's EDGE over the next `minutes`
        (cells drift too). Negative clearance = the track runs into the cell."""
        s = self.ship
        v = s.speed if speed is None else speed
        out = {}
        for cell in self.env.cells:
            if cell["intensity"] < min_intensity:
                continue
            best = 1e9
            for k in range(0, int(minutes) + 1):
                t = k / 60.0
                ox, oy = velocity(heading, v)
                cx, cy = velocity(cell["course"], cell["speed"])
                d = math.hypot((cell["x"] + cx * t) - (s.x + ox * t), (cell["y"] + cy * t) - (s.y + oy * t)) - cell["r"]
                best = min(best, d)
            out[cell["id"]] = round(best, 2)
        return out

    def weather_ahead(self) -> list[dict]:
        """Rain cells as the bridge sees them: bearing, range to the cell edge, intensity."""
        s, out = self.ship, []
        for cell in self.env.cells:
            dx, dy = cell["x"] - s.x, cell["y"] - s.y
            out.append({"id": cell["id"], "bearing": round(wrap360(math.degrees(math.atan2(dx, dy)))),
                        "range_to_edge_nm": round(max(0.0, math.hypot(dx, dy) - cell["r"]), 1),
                        "intensity": cell["intensity"],
                        "relative_bearing": round(angle_diff(math.degrees(math.atan2(dx, dy)), s.heading))})
        return out

    def cop(self) -> dict:
        return {
            "sim_time": self.sim_time,
            "phase": self.phase,
            "ownship": self.ship.model_dump(),
            "environment": self.env.model_dump() | {"cells_relative": self.weather_ahead()},
            "contacts": [c.model_dump() for c in self.contacts.values() if not c.hidden],
            "route_heading": ROUTE_HEADING,
            "systems": self.sys.view(self),
        }


class Scenario:
    """Timed events from a YAML file. Times are in real (wall-clock) seconds from start."""

    def __init__(self, path: Path):
        data = yaml.safe_load(path.read_text(encoding="utf-8"))
        self.name = data["name"]
        self.events = sorted(data["events"], key=lambda e: e["t"])
        self.elapsed = 0.0
        self._next = 0
        self.running = False

    def reset(self) -> None:
        self.elapsed = 0.0
        self._next = 0
        self.running = False

    def due(self, dt: float) -> list[dict]:
        if not self.running:
            return []
        self.elapsed += dt
        out = []
        while self._next < len(self.events) and self.events[self._next]["t"] <= self.elapsed:
            out.append(self.events[self._next])
            self._next += 1
        return out

    @property
    def finished(self) -> bool:
        return self._next >= len(self.events)
