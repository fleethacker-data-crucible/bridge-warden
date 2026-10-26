"""Shipboard systems simulation modeled on USS Arleigh Burke (DDG-51), Flight I.

Layout from PUBLIC, UNCLASSIFIED sources only: three ship-service gas turbine generators (SSGTG) on three
switchboards, two shafts each driven by two LM2500 gas turbines in Main Engine Rooms 1 and 2, auxiliary machinery
rooms, CIC forward, Mk 41 VLS fore and aft, Phalanx CIWS, SLQ-32 EW, repair lockers 2/3/5/8. Positions are
approximate (frames ~ feet from the bow, 505 ft LOA). ALL performance numbers (defense effectiveness, load units,
detection model) are ILLUSTRATIVE, not real. It exists so Warden has realistic decisions to govern.

Distances: nautical miles. Time: sim seconds.
"""
from __future__ import annotations

import math
import random
from collections import deque

SHIP = {"name": "USS Arleigh Burke", "hull": "DDG-51", "class": "Arleigh Burke-class destroyer (Flight I)",
        "loa_ft": 505, "max_speed_kn": 31, "one_shaft_speed_kn": 20}

# --------------------------------------------------------------------------- layout (fore → aft)
# Compartments: id, name, compartment number (deck-frame-position-use, illustrative), frames (approx. ft from bow).
COMPARTMENTS = [
    {"id": "BOW", "name": "Bow, Fwd Stores & Windlass", "number": "2-10-0-A", "fr": (0, 60)},
    {"id": "VLSF", "name": "Fwd Mk 41 VLS & 5-in Gun", "number": "1-60-0-M", "fr": (60, 130)},
    {"id": "CIC", "name": "CIC & Combat Systems", "number": "1-130-0-C", "fr": (130, 200)},
    {"id": "AMR1", "name": "Aux Machinery Room 1", "number": "4-200-0-E", "fr": (200, 232)},
    {"id": "MER1", "name": "Main Engine Room 1", "number": "4-232-0-E", "fr": (232, 290)},
    {"id": "MER2", "name": "Main Engine Room 2", "number": "4-290-0-E", "fr": (290, 348)},
    {"id": "AMR2", "name": "Aux Machinery Room 2", "number": "4-348-0-E", "fr": (348, 380)},
    {"id": "VLSA", "name": "Aft Mk 41 VLS & Decoys", "number": "1-380-0-M", "fr": (380, 440)},
    {"id": "STEER", "name": "Steering Gear & Aft Stores", "number": "4-440-0-E", "fr": (440, 505)},
]
COMP = {c["id"]: c for c in COMPARTMENTS}
BULKHEADS = sorted({c["fr"][0] for c in COMPARTMENTS} | {c["fr"][1] for c in COMPARTMENTS})

# Electrical plant: generation → switchboards → load centers → loads. x/y place nodes on the Ship Systems COP.
NODES = [
    ("SSGTG-1", "Ship Service GTG 1", "gen", "AMR1", 90, 120), ("SSGTG-2", "Ship Service GTG 2", "gen", "MER2", 90, 300),
    ("SSGTG-3", "Ship Service GTG 3", "gen", "AMR2", 90, 480),
    ("1SB", "No. 1 Switchboard", "swbd", "AMR1", 310, 130), ("2SB", "No. 2 Switchboard", "swbd", "MER2", 310, 300),
    ("3SB", "No. 3 Switchboard", "swbd", "AMR2", 310, 470),
    ("LC-1", "Load Center 1 (Fwd)", "lc", "VLSF", 530, 50), ("LC-2", "Load Center 2 (CIC)", "lc", "CIC", 530, 200),
    ("LC-3", "Load Center 3 (Eng)", "lc", "MER1", 530, 350), ("LC-4", "Load Center 4 (Aft)", "lc", "STEER", 530, 500),
    ("ADS", "Mk 41 VLS Fwd (SM-2)", "defense", "VLSF", 770, 10), ("FP-1", "Fire Pump 1", "load", "VLSF", 770, 56),
    ("CIWS", "Mk 15 Phalanx CIWS", "defense", "CIC", 770, 102), ("RADAR", "AN/SPY-1D Radar", "sensor", "CIC", 770, 148),
    ("ECM", "AN/SLQ-32 EW", "defense", "CIC", 770, 194), ("COMMS", "Comms Suite", "sensor", "CIC", 770, 240),
    ("SHAFT-1", "Shaft 1 (GTM 1A/1B)", "vital", "MER1", 770, 286), ("CHW", "Chilled Water (SPY cooling)", "load", "AMR1", 770, 332),
    ("SHAFT-2", "Shaft 2 (GTM 2A/2B)", "vital", "MER2", 770, 378), ("FP-2", "Fire Pump 2", "load", "MER2", 770, 424),
    ("STEER", "Steering Gear", "vital", "STEER", 770, 470), ("CMD", "Decoy / Counter-drone Launcher", "defense", "VLSA", 770, 516),
    ("FP-3", "Fire Pump 3", "load", "STEER", 770, 562),
]
# Breakers: id, a, b, normally closed? Normal lineup: two SSGTGs on line, SSGTG-3 in standby, bus ties open.
BREAKERS = [
    ("B-G1", "SSGTG-1", "1SB", True), ("B-G2", "SSGTG-2", "2SB", True), ("B-G3", "SSGTG-3", "3SB", False),
    ("B-T12", "1SB", "2SB", False), ("B-T23", "2SB", "3SB", False),
    ("B-1L1", "1SB", "LC-1", True), ("B-1L2", "1SB", "LC-2", True), ("B-2L3", "2SB", "LC-3", True), ("B-2L4", "2SB", "LC-4", True),
    ("B-2L1", "2SB", "LC-1", False), ("B-2L2", "2SB", "LC-2", False), ("B-1L3", "1SB", "LC-3", False),
    ("B-3L3", "3SB", "LC-3", False), ("B-3L4", "3SB", "LC-4", False),
] + [(f"F-{load}", lc, load, True) for lc, loads in {
    "LC-1": ["ADS", "FP-1"], "LC-2": ["CIWS", "RADAR", "ECM", "COMMS"], "LC-3": ["SHAFT-1", "CHW", "SHAFT-2", "FP-2"],
    "LC-4": ["STEER", "CMD", "FP-3"]}.items() for load in loads]
FIRE_PUMPS = ["FP-1", "FP-2", "FP-3"]

# Repair lockers man up only at General Quarters. The duty-section Flying Squad is always ready.
DC_TEAMS = [
    {"id": "R2", "name": "Repair 2 (Fwd)", "home": 110, "members": 14, "always": False},
    {"id": "R8", "name": "Repair 8 (Electronics)", "home": 165, "members": 8, "always": False},
    {"id": "R5", "name": "Repair 5 (Main Space)", "home": 300, "members": 12, "always": False},
    {"id": "R3", "name": "Repair 3 (Aft)", "home": 420, "members": 14, "always": False},
    {"id": "FS", "name": "Flying Squad (Duty)", "home": 260, "members": 6, "always": True},
]
MANNING_S = 60          # time for a repair locker to man up after GQ is sounded
INVESTIGATE_S = 20      # time for an investigator to confirm a reported casualty
GEN_CAPACITY = {"SSGTG-1": 7, "SSGTG-2": 7, "SSGTG-3": 7}     # loads each SSGTG carries (illustrative units)

# Illustrative defense effectiveness (probability one system defeats one inbound). NOT real numbers.
DEFENSES = {"CIWS": 0.45, "ADS": 0.35, "CMD": 0.30, "ECM": 0.40}

EMCON_RADIUS_NM = 10.0
DETECT_FROM_NM = 20.0
LAUNCH_RANGE_NM = 12.0     # stand-off launch range (illustrative)
INBOUND_KN = 500.0         # subsonic sea-skimmer (illustrative)
TEAM_FRAMES_PER_S = 2.0    # ~2 ft/s through passageways with gear
HIT_TARGETS = ["MER2", "AMR2", "CIC"]
COP_RANGE_NM = 20.0        # radar picture: an inbound beyond this is not detected yet
# Engagement model (ILLUSTRATIVE numbers, not real performance):
SM2_KN = 900.0             # interceptor fly-out speed
SM2_PK = 0.60              # chance one interceptor kills its target
SM2_MAX_NM, SM2_MIN_NM = 15.0, 1.5
SM2_MAGAZINE = 24
CIWS_NM, ECM_NM, DECOY_NM = 1.0, 8.0, 4.0
EVASIVE_MISS = 0.25        # chance an inbound misses a ship turning hard at speed
# Return fire and undersea warfare (ILLUSTRATIVE numbers, not real performance):
WEAPONS = {   # weapon: fly-out kn, pk per round, min/max range nm, magazine
    "HARPOON": {"kn": 500.0, "pk": 0.70, "min": 1.0, "max": 60.0, "mag": 8, "vs": "surface"},
    "ASROC": {"kn": 600.0, "pk": 0.55, "min": 1.0, "max": 10.0, "mag": 8, "vs": "subsurface"},
}
TORP_KN = 45.0             # heavyweight torpedo speed
TORP_RUN_NM = 9.0          # fuel: total run before it sinks
NIXIE_NM, NIXIE_PK = 2.0, 0.50     # AN/SLQ-25 towed decoy: one seduction chance as the torpedo passes 2 nm
SUB_DETECT_NM = 6.0        # hull sonar holds a quiet submarine inside this range
SUB_ATTACK_NM = 6.5
FLOOD_TARGETS = ["MER1", "AMR2", "STEER"]


class ShipSystems:
    def __init__(self, seed: int = 7):
        self.rng = random.Random(seed)
        self.nodes = {n[0]: {"id": n[0], "label": n[1], "type": n[2], "comp": n[3], "x": n[4], "y": n[5],
                             "failed": False, "powered": True, "active": False, "fail_reason": ""} for n in NODES}
        self.breakers = {b[0]: {"id": b[0], "a": b[1], "b": b[2], "normal": b[3], "closed": b[3]} for b in BREAKERS}
        self.teams = {t["id"]: {**t, "pos": float(t["home"]), "status": "standby" if t["always"] else "unmanned",
                                "target": None, "casualty": None, "boundaries": None, "man_s": 0.0} for t in DC_TEAMS}
        self.gq = False
        self.gen_load: dict[str, int] = {}
        self._overload_alerted: set[str] = set()
        self._firemain_alerted = False
        self.casualties: dict[str, dict] = {}
        self.inbound: list[dict] = []
        self.interceptors: list[dict] = []     # SM-2 in flight: {id, target, range, bearing}
        self.splashes: list[dict] = []         # recent kills/misses for the COP: {range, bearing, label, age_s}
        self.magazine = SM2_MAGAZINE
        self.held: dict[str, int] = {}         # engagements released but waiting for the target to enter the envelope
        self.mags = {w: v["mag"] for w, v in WEAPONS.items()}
        self.torpedoes: list[dict] = []        # in the water: {id, from, range, bearing, run_left}
        self.nixie = False                     # SLQ-25 streamed
        self.torp_hits = 0
        self._sm = 0
        self._turn_age_s = 999.0               # seconds since own ship last turned hard
        self._last_heading = None
        self._world = None
        self.emcon = False
        self.hostile_state: dict[str, dict] = {}    # per hostile: detection %, attack %, detected, launched
        self.events: deque = deque()                  # (severity, source, message) drained by Warden
        self.hits = 0
        self._cid = 0
        self._next_target = 0
        self._defense_idle_s = 0.0
        self.flow()

    # ------------------------------------------------------------------ electrical plant
    def flow(self) -> None:
        """Energize everything reachable from a running generator through closed breakers and working nodes."""
        for n in self.nodes.values():
            n["powered"] = False
        frontier = [nid for nid, n in self.nodes.items() if n["type"] == "gen" and not n["failed"]
                    and any(b["closed"] for b in self.breakers.values() if nid in (b["a"], b["b"]))]
        seen = set(frontier)
        while frontier:
            cur = frontier.pop()
            self.nodes[cur]["powered"] = True
            for b in self.breakers.values():
                if not b["closed"] or cur not in (b["a"], b["b"]):
                    continue
                nxt = b["b"] if b["a"] == cur else b["a"]
                if nxt not in seen and not self.nodes[nxt]["failed"]:
                    seen.add(nxt)
                    frontier.append(nxt)
        for n in self.nodes.values():      # a standby generator is available, not dead
            if n["type"] == "gen" and not n["failed"]:
                n["powered"] = True
        for d in DEFENSES:
            if self.nodes[d]["active"] and not self.ok(d):
                self.nodes[d]["active"] = False
        self._compute_load()

    def _island(self, start: str) -> set[str]:
        seen, frontier = {start}, [start]
        while frontier:
            cur = frontier.pop()
            for b in self.breakers.values():
                if b["closed"] and cur in (b["a"], b["b"]):
                    nxt = b["b"] if b["a"] == cur else b["a"]
                    if nxt not in seen and not self.nodes[nxt]["failed"]:
                        seen.add(nxt)
                        frontier.append(nxt)
        return seen

    def _compute_load(self) -> None:
        """Percent load on each on-line generator: powered loads on its electrical island / island capacity."""
        self.gen_load = {}
        done = set()
        for g in GEN_CAPACITY:
            n = self.nodes[g]
            if n["failed"] or g in done:
                continue
            isl = self._island(g)
            if len(isl) == 1:
                continue          # standby: not connected to anything
            gens = [x for x in isl if x in GEN_CAPACITY]
            done.update(gens)
            loads = sum(1 for x in isl if self.nodes[x]["type"] not in ("gen", "swbd", "lc"))
            pct = round(100 * loads / sum(GEN_CAPACITY[x] for x in gens))
            for x in gens:
                self.gen_load[x] = pct

    def overloaded(self) -> list[str]:
        return [g for g, p in self.gen_load.items() if p > 100]

    @property
    def firemain_ok(self) -> bool:
        return any(self.ok(p) for p in FIRE_PUMPS)

    def set_gq(self, on: bool) -> None:
        self.gq = on
        if on:
            for t in self.teams.values():
                if t["status"] == "unmanned":
                    t.update(status="manning", man_s=0.0)
            self.events.append(("critical", "bridge", "GENERAL QUARTERS, GENERAL QUARTERS. All hands man your battle stations."))
        else:
            for t in self.teams.values():
                if not t["always"] and t["status"] in ("standby", "manning"):
                    t.update(status="unmanned")
            self.events.append(("info", "bridge", "Secure from General Quarters"))

    def ok(self, nid: str) -> bool:
        n = self.nodes[nid]
        if nid == "RADAR" and not (self.nodes["CHW"]["powered"] and not self.nodes["CHW"]["failed"]):
            return False        # SPY-1D can't radiate without chilled water
        return n["powered"] and not n["failed"]

    @property
    def shafts_ok(self) -> int:
        return sum(self.ok(x) for x in ("SHAFT-1", "SHAFT-2"))

    @property
    def max_speed_kn(self) -> float:
        return {2: SHIP["max_speed_kn"], 1: SHIP["one_shaft_speed_kn"], 0: 0.0}[self.shafts_ok]

    @property
    def propulsion_ok(self) -> bool:
        return self.shafts_ok > 0

    @property
    def steering_ok(self) -> bool:
        return self.ok("STEER")

    def restoring_breaker(self, nid: str) -> str | None:
        """An open breaker whose closing would re-energize this node, or None."""
        for bid, b in self.breakers.items():
            if b["closed"]:
                continue
            b["closed"] = True
            self.flow()
            fixed = self.ok(nid)
            b["closed"] = False
            self.flow()
            if fixed:
                return bid
        return None

    def set_breaker(self, bid: str, closed: bool) -> bool:
        prev = self.breakers[bid]["closed"]
        self.breakers[bid]["closed"] = closed
        self.flow()
        return prev

    def fail(self, nid: str, reason: str) -> None:
        n = self.nodes[nid]
        if not n["failed"]:
            n["failed"], n["fail_reason"] = True, reason
            self.events.append(("critical", "ship-systems", f"EOOW: {n['label']} ({nid}) lost: {reason}"))
            self.flow()

    def unpowered_vital(self) -> list[str]:
        return [nid for nid, n in self.nodes.items()
                if n["type"] in ("vital", "defense", "sensor", "load") and not n["failed"] and not n["powered"]]

    # ------------------------------------------------------------------ casualties & DC
    def add_casualty(self, comp: str, kind: str, severity: float, nodes: list[str] | None = None,
                     reason: str = "", confirmed: bool = False) -> dict:
        """Fires and floods start as a report; an investigator confirms them INVESTIGATE_S later.
        Obvious casualties (weapon hits, spreading, machinery trips) are confirmed at once."""
        self._cid += 1
        reported = kind in ("fire", "flood") and not confirmed
        cas = {"id": f"CAS-{self._cid}", "comp": comp, "kind": kind, "severity": severity, "team": None,
               "boundaries": None, "status": "reported" if reported else "active", "investigate_s": 0.0,
               "repair_s": 0.0, "spread": False, "nodes": list(nodes or []), "reason": reason}
        self.casualties[cas["id"]] = cas
        for nid in cas["nodes"]:
            self.fail(nid, reason or kind)
        c = COMP[comp]
        where = f"compartment {c['number']}, {c['name']}"
        if reported:
            first = "Smoke" if kind == "fire" else "Water"
            msg = f"DCA: {first} reported in {where}. Investigator away."
        else:
            msg = {"fire": f"FIRE, FIRE, FIRE in {where}",
                   "flood": f"FLOODING, FLOODING, FLOODING in {where}",
                   "equipment": f"EOOW: {reason or 'equipment casualty'} in {where}"}[kind]
        self.events.append(("critical", "damage-control", msg))
        return cas

    def dispatch(self, team_id: str, cas_id: str) -> dict:
        t, cas = self.teams[team_id], self.casualties[cas_id]
        lo, hi = COMP[cas["comp"]]["fr"]
        prev = {"team": team_id, "status": t["status"], "pos": t["pos"], "casualty": t["casualty"]}
        t.update(status="en route", target=(lo + hi) / 2, casualty=cas_id, boundaries=[lo, hi])
        cas["team"] = team_id
        return prev

    def nearest_idle_team(self, comp: str) -> str | None:
        lo, hi = COMP[comp]["fr"]
        idle = [t for t in self.teams.values() if t["status"] == "standby"]  # manned and ready
        if not idle:
            return None
        return min(idle, key=lambda t: abs(t["pos"] - (lo + hi) / 2))["id"]

    def _neighbor(self, comp: str) -> str | None:
        ids = [c["id"] for c in COMPARTMENTS]
        i = ids.index(comp)
        options = [ids[j] for j in (i - 1, i + 1) if 0 <= j < len(ids) and
                   not any(c["comp"] == ids[j] and c["status"] == "active" for c in self.casualties.values())]
        return options[0] if options else None

    def _step_dc(self, dt: float) -> None:
        for t in self.teams.values():
            if t["status"] == "manning":
                t["man_s"] += dt
                if t["man_s"] >= MANNING_S:
                    t["status"] = "standby"
                    self.events.append(("info", "damage-control", f"DCA: {t['name']} manned and ready"))
        if not self.firemain_ok and not self._firemain_alerted and any(
                c["kind"] == "fire" and c["status"] not in ("out", "repaired") for c in self.casualties.values()):
            self._firemain_alerted = True
            self.events.append(("critical", "damage-control", "DCA: firemain pressure LOST, no fire pump on line"))
        if self.firemain_ok:
            self._firemain_alerted = False
        for t in self.teams.values():
            if t["status"] in ("en route", "returning") and t["target"] is not None:
                d = t["target"] - t["pos"]
                step = TEAM_FRAMES_PER_S * dt
                if abs(d) <= step:
                    t["pos"] = t["target"]
                    if t["status"] == "returning":
                        t.update(status="standby" if (t["always"] or self.gq) else "unmanned", target=None, boundaries=None)
                    else:
                        t["status"] = "on scene"
                        cas = self.casualties.get(t["casualty"])
                        if cas:
                            cas["boundaries"] = t["boundaries"]
                            self.events.append(("info", "damage-control",
                                                f"{t['name']} on scene at {cas['comp']}: boundaries set at frames "
                                                f"{t['boundaries'][0]} and {t['boundaries'][1]}"))
                else:
                    t["pos"] += math.copysign(step, d)

        for cas in list(self.casualties.values()):   # spreading adds casualties mid-loop
            if cas["status"] in ("out", "repaired"):
                continue
            team = self.teams.get(cas["team"]) if cas["team"] else None
            on_scene = team is not None and team["status"] == "on scene"
            if cas["status"] == "reported":
                cas["investigate_s"] += dt
                if cas["investigate_s"] >= INVESTIGATE_S:
                    cas["status"] = "active"
                    c = COMP[cas["comp"]]
                    what = "FIRE, FIRE, FIRE" if cas["kind"] == "fire" else "FLOODING, FLOODING, FLOODING"
                    self.events.append(("critical", "damage-control",
                                        f"{what} in compartment {c['number']}, {c['name']} "
                                        f"(investigator: {'class B, spreading' if cas['severity'] > 0.5 else 'class A, contained'})"))
            if cas["kind"] in ("fire", "flood"):
                if on_scene:
                    # Firefighting needs firemain pressure; without a fire pump, extinguishers only.
                    rate = 0.02 if (cas["kind"] == "flood" or self.firemain_ok) else 0.004
                    cas["severity"] = max(0.0, cas["severity"] - rate * dt)
                else:
                    cas["severity"] = min(1.0, cas["severity"] + 0.0008 * dt)   # ~1 real minute before it spreads
                if cas["severity"] >= 0.5:
                    for nid, n in self.nodes.items():
                        if n["comp"] == cas["comp"] and not n["failed"]:
                            cas["nodes"].append(nid)
                            self.fail(nid, f"{cas['kind']} damage")
                if cas["severity"] >= 0.98 and not cas["boundaries"] and not cas["spread"]:
                    nb = self._neighbor(cas["comp"])
                    cas["spread"] = True
                    if nb:
                        self.events.append(("critical", "damage-control",
                                            f"{cas['kind'].upper()} SPREADING from {cas['comp']} to {nb}: no boundaries set"))
                        self.add_casualty(nb, cas["kind"], 0.4, confirmed=True)
                if cas["severity"] <= 0 and cas["status"] == "active":
                    cas["status"] = "repairing"
                    self.events.append(("info", "damage-control", f"{cas['kind'].upper()} OUT in {cas['comp']}; repairs started"))
            elif on_scene and cas["status"] == "active":
                cas["status"] = "repairing"
            if cas["status"] == "repairing" and on_scene:
                cas["repair_s"] += dt
                if cas["repair_s"] >= 45:
                    for nid in cas["nodes"]:
                        self.nodes[nid].update(failed=False, fail_reason="")
                    cas["status"] = "repaired"
                    team.update(status="returning", target=float(team["home"]), casualty=None, boundaries=None)
                    self.flow()
                    self.events.append(("info", "damage-control",
                                        f"{cas['comp']} repaired: {', '.join(cas['nodes']) or 'no equipment lost'} restored"))

    # ------------------------------------------------------------------ EMCON, detection, attack, defense
    def _step_threats(self, dt: float, world) -> None:
        s = world.ship
        radiating = (not self.emcon and self.ok("RADAR")) or self.nodes["ECM"]["active"]
        for c in world.contacts.values():
            if c.kind != "hostile":
                continue
            if c.domain == "subsurface":
                self._step_sub(c, s, dt)
                continue
            # Close to launch range, then hold off and orbit.
            brg_to_own = (math.degrees(math.atan2(s.x - c.x, s.y - c.y))) % 360
            c.course = brg_to_own if c.range_nm > LAUNCH_RANGE_NM else (brg_to_own + 90) % 360
            st = self.hostile_state.setdefault(c.id, {"detect": 0.0, "attack": 0.0, "detected": False,
                                                      "launched": 0, "in_radius_alerted": False, "tracked_s": 0.0})
            r = c.range_nm
            # Classification: UNKNOWN → SUSPECT (closing fast, no AIS) → HOSTILE (fire-control radar intercept).
            st["tracked_s"] = st.get("tracked_s", 0.0) + dt
            if c.classification == "UNKNOWN" and (st["tracked_s"] >= 30 or r <= 15):
                c.classification = "SUSPECT"
                self.events.append(("critical", "ew", f"CIC: {c.id} classified SUSPECT, bearing {c.bearing:03.0f}, "
                                                      f"{r:.1f} nm, closing {c.speed:.0f} kn, no AIS"))
            if c.classification == "SUSPECT" and (r <= 13 or st["attack"] >= 30 or st["detected"]):
                c.classification = "HOSTILE"
                self.events.append(("critical", "ew", f"TAO, EW: fire-control radar intercept bearing {c.bearing:03.0f}. "
                                                      f"{c.id} classified HOSTILE"))
            if radiating:
                rate = 0.35 * max(0.0, (DETECT_FROM_NM - r) / 10) * (2 if r <= EMCON_RADIUS_NM else 1)
                st["detect"] = min(100.0, st["detect"] + rate * dt)
            else:
                st["detect"] = max(0.0, st["detect"] - 0.3 * dt)
            if st["detect"] >= 100 and not st["detected"]:
                st["detected"] = True
                self.events.append(("critical", "ew", f"TAO, EW: own ship is being tracked by {c.id} (our emissions)"))
            if r <= EMCON_RADIUS_NM and not self.emcon and not st["in_radius_alerted"]:
                st["in_radius_alerted"] = True
                self.events.append(("critical", "ew", f"TAO: {c.id} inside EMCON radius ({r:.1f} nm), EMCON NOT SET"))
            if st["detected"] and r <= LAUNCH_RANGE_NM + 2:
                st["attack"] = min(100.0, st["attack"] + 0.4 * dt * (2 if r <= LAUNCH_RANGE_NM else 1))
            elif r <= LAUNCH_RANGE_NM:
                st["attack"] = min(100.0, st["attack"] + 0.05 * dt)
            if st["attack"] >= 100 and st["launched"] < 2:
                self.launch(c.id, r, 2)
                st["attack"] = 40.0

        self._world = world
        if self._last_heading is not None and abs(((s.heading - self._last_heading + 180) % 360) - 180) >= 0.8 * dt:
            self._turn_age_s = 0.0
        else:
            self._turn_age_s += dt
        self._last_heading = s.heading
        for m in list(self.inbound):
            prev = m["range"]
            m["range"] -= INBOUND_KN * dt / 3600
            if m["detected"]:
                self._soft_kill(m, prev)
            if m in self.inbound and m["range"] <= CIWS_NM and m["detected"] and not m.get("ciws"):
                m["ciws"] = True
                if self.nodes["CIWS"]["active"] and self.ok("CIWS"):
                    if self.rng.random() < DEFENSES["CIWS"]:
                        self._splash(m, "CIWS")
                        continue
                    self.events.append(("critical", "defense", f"TAO: CIWS engaged {m['id']}, LEAKER"))
            if m in self.inbound and m["range"] <= 0.2:
                self.inbound.remove(m)
                self._resolve(m)
        self._step_torpedoes(dt, world)
        for tid, rounds in list(self.held.items()):
            m = next((x for x in self.inbound if x["id"] == tid), None)
            if m is None:
                del self.held[tid]
            elif m["range"] <= SM2_MAX_NM:
                del self.held[tid]
                if self.ok("ADS") and self.magazine >= rounds:
                    self.engage(tid, rounds)
        for it in list(self.interceptors):
            if it.get("weapon", "SM-2") != "SM-2":
                self._step_strike(it, dt, world)
                continue
            tgt = next((m for m in self.inbound if m["id"] == it["target"]), None)
            if tgt is None:
                self.interceptors.remove(it)
                self.events.append(("info", "defense", f"TAO: {it['id']} lost its target (already killed), self-destructed"))
                continue
            it["range"] += SM2_KN * dt / 3600
            if it["range"] >= tgt["range"]:
                self.interceptors.remove(it)
                if self.rng.random() < SM2_PK:
                    self._splash(tgt, it["id"])
                else:
                    self.events.append(("critical", "defense", f"TAO: {it['id']} MISSED {tgt['id']}"))
        for sp in list(self.splashes):
            sp["age_s"] += dt
            if sp["age_s"] > 25:
                self.splashes.remove(sp)
        self._detect_inbound()

        if self.inbound or self.torpedoes:
            self._defense_idle_s = 0.0
        else:
            self._defense_idle_s += dt
            if self._defense_idle_s > 60 and any(self.nodes[d]["active"] for d in DEFENSES):
                for d in DEFENSES:
                    self.nodes[d]["active"] = False
                self.events.append(("info", "defense", "No inbound threats for 60 s: defenses back to standby"))

    def launch(self, hostile_id: str, range_nm: float, count: int, bearing: float | None = None) -> None:
        """Inbounds are only DETECTED (and reported, shown, given to agents) once inside the 20 nm COP."""
        st = self.hostile_state.setdefault(hostile_id, {"detect": 100.0, "attack": 0.0, "detected": True,
                                                        "launched": 0, "in_radius_alerted": True, "tracked_s": 99.0})
        for i in range(count):
            st["launched"] += 1
            self.inbound.append({"id": f"{hostile_id}-V{st['launched']}", "from": hostile_id, "detected": False,
                                 "range": range_nm + i * 0.4, "bearing": bearing})
        self._detect_inbound()

    def _detect_inbound(self) -> None:
        new = [m for m in self.inbound if not m["detected"] and m["range"] <= COP_RANGE_NM]
        for m in new:
            m["detected"] = True
        if new:
            first = min(new, key=lambda m: m["range"])
            eta = first["range"] / INBOUND_KN * 3600
            brg = f", bearing {first['bearing']:03.0f}" if first.get("bearing") is not None else ""
            self.events.append(("critical", "defense",
                                f"VAMPIRE, VAMPIRE, VAMPIRE: {len(new)} inbound{brg}, {first['range']:.1f} nm, "
                                f"impact in ~{eta:.0f} s"))

    def inbound_on_cop(self) -> list[dict]:
        return [m for m in self.inbound if m["detected"]]

    def threats_on_cop(self) -> list[dict]:
        """Every weapon coming at us that the watch can see: missiles on the COP and torpedoes in the water."""
        return self.inbound_on_cop() + self.torpedoes

    # ---- undersea
    def _step_sub(self, c, s, dt: float) -> None:
        st = self.hostile_state.setdefault(c.id, {"detect": 0.0, "attack": 0.0, "detected": False, "launched": 0,
                                                  "in_radius_alerted": True, "tracked_s": 0.0})
        brg_to_own = (math.degrees(math.atan2(s.x - c.x, s.y - c.y))) % 360
        c.course = brg_to_own if c.range_nm > SUB_ATTACK_NM - 1 else (brg_to_own + 100) % 360
        if c.hidden and c.range_nm <= SUB_DETECT_NM:
            c.hidden, c.classification = False, "SUSPECT"
            self.events.append(("critical", "asw", f"SONAR: submerged contact {c.id}, bearing {c.bearing:03.0f}, "
                                                   f"{c.range_nm:.1f} nm, classified SUSPECT SUBMARINE"))
        if not c.hidden:
            st["tracked_s"] += dt
        if c.range_nm <= SUB_ATTACK_NM:
            st["attack"] = min(100.0, st["attack"] + 1.2 * dt)
        if st["attack"] >= 100 and st["launched"] < 2:
            st["attack"] = 0.0
            for _ in range(2):
                st["launched"] += 1
                self.torpedoes.append({"id": f"{c.id}-T{st['launched']}", "from": c.id, "range": c.range_nm,
                                       "bearing": c.bearing, "run_left": TORP_RUN_NM, "nixie_tried": False})
            c.hidden, c.classification = False, "HOSTILE"
            self.events.append(("critical", "asw", f"TORPEDO, TORPEDO, TORPEDO: 2 torpedoes in the water, bearing "
                                                   f"{c.bearing:03.0f}, {c.range_nm:.1f} nm, from {c.id} (HOSTILE)"))

    def _step_torpedoes(self, dt: float, world) -> None:
        s = world.ship
        for t in list(self.torpedoes):
            # Closing speed: torpedo speed plus own ship's speed TOWARD the torpedo (negative when running away).
            toward = s.speed * math.cos(math.radians(s.heading - t["bearing"]))
            prev = t["range"]
            t["range"] -= (TORP_KN + toward) * dt / 3600
            t["run_left"] -= TORP_KN * dt / 3600
            if prev > NIXIE_NM >= t["range"] and not t["nixie_tried"]:
                t["nixie_tried"] = True
                if self.nixie and self.ok("CMD") and self.rng.random() < NIXIE_PK:
                    self.torpedoes.remove(t)
                    self.splashes.append({"range": t["range"], "bearing": t["bearing"], "label": "SEDUCED", "age_s": 0})
                    self.events.append(("info", "asw", f"TAO: {t['id']} seduced by Nixie, passing astern"))
                    continue
            if t["run_left"] <= 0:
                self.torpedoes.remove(t)
                self.splashes.append({"range": t["range"], "bearing": t["bearing"], "label": "OUT OF FUEL", "age_s": 0})
                self.events.append(("info", "asw", f"TAO: {t['id']} ran out of fuel {t['range']:.1f} nm short, sinking"))
                continue
            if t["range"] <= 0.1:
                self.torpedoes.remove(t)
                self.torp_hits += 1
                self.hits += 1
                comp = FLOOD_TARGETS[self.torp_hits % len(FLOOD_TARGETS)]
                self.events.append(("critical", "asw", f"TORPEDO HIT: {t['id']} under {COMP[comp]['name']}"))
                self.add_casualty(comp, "flood", 0.8, reason="torpedo hit", confirmed=True)

    def _step_strike(self, it: dict, dt: float, world) -> None:
        """Harpoon / ASROC flying at a contact. Hits when it reaches the contact's range."""
        c = world.contacts.get(it["target"])
        if c is None:
            self.interceptors.remove(it)
            return
        it["range"] += WEAPONS[it["weapon"]]["kn"] * dt / 3600
        it["bearing"] = c.bearing
        if it["range"] >= c.range_nm:
            self.interceptors.remove(it)
            if self.rng.random() < WEAPONS[it["weapon"]]["pk"]:
                world.contacts.pop(c.id, None)
                self.splashes.append({"range": c.range_nm, "bearing": c.bearing, "label": "KILL", "age_s": 0})
                self.events.append(("info", "defense", f"TAO: {it['id']} ({it['weapon']}) KILLED {c.id}"))
            else:
                self.events.append(("critical", "defense", f"TAO: {it['id']} ({it['weapon']}) MISSED {c.id}"))

    def weapon_for(self, target: str):
        """(weapon, thing) for a target id: an inbound missile → SM-2, a contact → Harpoon / ASROC."""
        m = next((x for x in self.inbound_on_cop() if x["id"] == target), None)
        if m:
            return "SM-2", m
        c = self._world.contacts.get(target) if self._world else None
        if c and not c.hidden:
            return ("ASROC" if c.domain == "subsurface" else "HARPOON"), c
        return None, None

    def abort_weapons(self) -> int:
        """CEASE FIRE: every own weapon in flight is ordered to self-destruct."""
        n = len(self.interceptors) + len(self.held)
        self.held.clear()
        for it in self.interceptors:
            self.splashes.append({"range": it["range"], "bearing": it.get("bearing"), "label": "ABORTED", "age_s": 0})
            self.events.append(("critical", "defense", f"CEASE FIRE: {it['id']} ({it.get('weapon', 'SM-2')}) destruct"))
        self.interceptors.clear()
        return n

    def is_active(self, d: str) -> bool:
        return self.nixie if d == "NIXIE" else self.nodes[d]["active"]

    def p_hit(self) -> float:
        p = 1.0
        for d, pk in DEFENSES.items():
            if self.nodes[d]["active"] and self.ok(d):
                p *= 1 - pk
        return p

    def _soft_kill(self, m: dict, prev_range: float) -> None:
        """Jamming (ECM) and decoys get one chance each to seduce an inbound as it crosses their range."""
        for name, rng_nm in (("ECM", ECM_NM), ("CMD", DECOY_NM)):
            if prev_range > rng_nm >= m["range"] and self.nodes[name]["active"] and self.ok(name) and m in self.inbound:
                if self.rng.random() < DEFENSES[name]:
                    self.inbound.remove(m)
                    what = "SLQ-32 jamming" if name == "ECM" else "decoy"
                    self.splashes.append({"range": m["range"], "bearing": m.get("bearing"), "label": "SEDUCED", "age_s": 0})
                    self.events.append(("info", "defense", f"TAO: {m['id']} seduced by {what}, missing astern"))

    def _splash(self, m: dict, by: str) -> None:
        if m in self.inbound:
            self.inbound.remove(m)
        self.splashes.append({"range": m["range"], "bearing": m.get("bearing"), "label": "SPLASH", "age_s": 0})
        self.events.append(("info", "defense", f"TAO: SPLASH {m['id']}, killed by {by} at {m['range']:.1f} nm"))

    def evasive(self) -> bool:
        s = self._world.ship if self._world else None
        return bool(s and s.speed >= 20 and self._turn_age_s <= 60)

    def engage(self, target: str, rounds: int) -> dict:
        weapon, thing = self.weapon_for(target)
        if weapon in WEAPONS:
            fired = []
            for _ in range(rounds):
                self._sm += 1
                self.mags[weapon] -= 1
                w = {"id": f"{weapon[:3]}-{self._sm}", "weapon": weapon, "target": target, "range": 0.0,
                     "bearing": thing.bearing}
                self.interceptors.append(w)
                fired.append(w["id"])
            self.events.append(("critical", "defense", f"{'WEAPON AWAY' if weapon == 'ASROC' else 'BIRDS AWAY'}: "
                                                       f"{rounds}x {weapon} at {target}, {thing.range_nm:.1f} nm"))
            return {"fired": fired}
        m = thing
        fired = []
        for _ in range(rounds):
            self._sm += 1
            self.magazine -= 1
            sm = {"id": f"SM2-{self._sm}", "weapon": "SM-2", "target": target, "range": 0.0, "bearing": m.get("bearing")}
            self.interceptors.append(sm)
            fired.append(sm["id"])
        tti = m["range"] / (SM2_KN + INBOUND_KN) * 3600
        self.events.append(("critical", "defense", f"BIRDS AWAY: {rounds}x SM-2 ({', '.join(fired)}) at {target}, "
                                                   f"{m['range']:.1f} nm, intercept in ~{tti:.0f} s"))
        return {"fired": fired}

    def _resolve(self, m: dict) -> None:
        p = 1.0 - (EVASIVE_MISS if self.evasive() else 0.0)
        if self.rng.random() < p:
            self.hits += 1
            comp = HIT_TARGETS[self._next_target % len(HIT_TARGETS)]
            self._next_target += 1
            c = COMP[comp]
            self.events.append(("critical", "defense", f"WE'VE BEEN HIT: {m['id']} impact frame "
                                                       f"{sum(c['fr']) // 2}, {c['name']} (hit chance was {p:.0%})"))
            self.add_casualty(comp, "fire", 0.85, reason="weapon impact", confirmed=True)
        else:
            self.splashes.append({"range": 0.3, "bearing": m.get("bearing"), "label": "MISS", "age_s": 0})
            self.events.append(("info", "defense", f"TAO: {m['id']} MISSED the ship (evasive maneuvering)"))

    def predict_emcon(self, world) -> dict | None:
        """Nearest hostile outside the EMCON radius: minutes until it's inside, and detection risk by then if we keep radiating."""
        best = None
        for c in world.contacts.values():
            if c.classification not in ("SUSPECT", "HOSTILE"):     # only what CIC has classified
                continue
            st = self.hostile_state.get(c.id, {"detect": 0.0})
            closing = 32.0 + 8.0
            mins = max(0.0, (c.range_nm - EMCON_RADIUS_NM) / closing * 60)
            r, risk, t = c.range_nm, st["detect"], 0.0
            while r > EMCON_RADIUS_NM and t < 3600:
                risk = min(100.0, risk + 0.35 * max(0.0, (DETECT_FROM_NM - r) / 10) * 10)
                r -= closing * 10 / 3600
                t += 10
            item = {"hostile": c.id, "range": round(c.range_nm, 1), "minutes_to_radius": round(mins, 1),
                    "risk_now": round(st["detect"]), "risk_at_radius": round(risk)}
            if best is None or item["minutes_to_radius"] < best["minutes_to_radius"]:
                best = item
        return best

    # ------------------------------------------------------------------ governed actions
    SYSTEM_ACTIONS = ("set_emcon", "activate_defense", "close_breaker", "open_breaker", "dispatch_team", "set_gq",
                      "engage_target")
    SAFE_DEFAULTS = {
        "set_emcon": "Keep current emissions posture",
        "activate_defense": "Do not activate (defenses stay in standby)",
        "close_breaker": "Leave breaker open",
        "open_breaker": "Leave breaker closed",
        "dispatch_team": "Team stays at its station",
        "set_gq": "Remain in Condition III",
        "engage_target": "Hold fire (no launch)",
    }

    def current(self, action: str, params: dict) -> str:
        if action == "engage_target":
            weapon, thing = self.weapon_for(params.get("target"))
            if weapon == "SM-2":
                return f"{params.get('target')} at {thing['range']:.1f} nm, SM-2 magazine {self.magazine}"
            if weapon:
                return f"{params.get('target')} at {thing.range_nm:.1f} nm, {weapon} magazine {self.mags[weapon]}"
            return "target gone"
        if action == "set_gq":
            return "General Quarters" if self.gq else "Condition III (normal watch)"
        if action == "set_emcon":
            return "EMCON SET" if self.emcon else "EMCON OFF (radiating)"
        if action == "activate_defense":
            return ", ".join(f"{d} {'active' if self.is_active(d) else 'standby'}" for d in params.get("systems", []))
        if action in ("close_breaker", "open_breaker"):
            b = self.breakers.get(params.get("breaker"), {})
            return f"{params.get('breaker')} {'closed' if b.get('closed') else 'open'}"
        if action == "dispatch_team":
            t = self.teams.get(params.get("team"), {})
            return f"{t.get('name')} {t.get('status')} at frame {t.get('pos', 0):.0f}"
        return ""

    def validate(self, action: str, params: dict) -> str | None:
        """Why this action can't be done right now, or None."""
        if action in ("close_breaker", "open_breaker") and params.get("breaker") not in self.breakers:
            return f"Unknown breaker {params.get('breaker')}"
        if action == "set_emcon" and not isinstance(params.get("on"), bool):
            return "set_emcon needs on: true/false"
        if action == "activate_defense":
            systems = params.get("systems") or []
            bad = [d for d in systems if d not in DEFENSES and d != "NIXIE"]
            if bad or not systems:
                return f"Unknown defense {bad or systems}"
            dead = [d for d in systems if not self.ok("CMD" if d == "NIXIE" else d)]
            if dead:
                return f"{', '.join(dead)} not available (no power or damaged)"
        if action == "engage_target":
            rounds = int(params.get("rounds", 2) or 2)
            weapon, c = self.weapon_for(params.get("target"))
            if weapon in WEAPONS:
                w = WEAPONS[weapon]
                if c.classification != "HOSTILE":
                    return f"{c.id} is {c.classification}, not HOSTILE: rules of engagement forbid firing"
                if not w["min"] <= c.range_nm <= w["max"]:
                    return f"{c.id} at {c.range_nm:.1f} nm is outside {weapon} range {w['min']}-{w['max']} nm"
                if not self.ok("ADS"):
                    return "Mk 41 VLS not available (no power or damaged)"
                if any(i["target"] == c.id for i in self.interceptors):
                    return f"{c.id} already has a {weapon} in flight"
                if not 1 <= rounds <= 2 or rounds > self.mags[weapon]:
                    return f"Rounds must be 1-2 and within {weapon} magazine ({self.mags[weapon]})"
                return None
            m = next((x for x in self.inbound_on_cop() if x["id"] == params.get("target")), None)
            if not m:
                return f"No inbound or HOSTILE track {params.get('target')} on the COP"
            if not self.ok("ADS"):
                return "Mk 41 VLS / SM-2 not available (no power or damaged)"
            if m["range"] < SM2_MIN_NM:
                return f"{m['id']} at {m['range']:.1f} nm is inside SM-2 minimum range {SM2_MIN_NM} nm (CIWS only)"
            if m["id"] in self.held:
                return f"{m['id']} is already released, held for the SM-2 envelope"
            if any(i["target"] == m["id"] for i in self.interceptors):
                return f"{m['id']} already has SM-2 in flight"
            if not 1 <= rounds <= 2 or rounds > self.magazine:
                return f"Rounds must be 1-2 and within magazine ({self.magazine})"
        if action == "dispatch_team":
            t = self.teams.get(params.get("team"))
            if not t or params.get("casualty") not in self.casualties:
                return "Unknown team or casualty"
            if t["status"] == "unmanned":
                return f"{t['name']} is not manned (General Quarters not set)"
            if t["status"] == "manning":
                return f"{t['name']} is still manning up"
            if t["status"] not in ("standby", "returning"):
                return f"{t['name']} is busy ({t['status']})"
            cas = self.casualties[params["casualty"]]
            if cas["team"] and cas["team"] != t["id"]:
                return f"{cas['id']} already has {self.teams[cas['team']]['name']} assigned"
            if cas["status"] in ("out", "repaired"):
                return f"{cas['id']} is already {cas['status']}"
        return None

    def apply(self, action: str, params: dict) -> dict:
        """Carry out an approved action. Returns its inverse, for rollback."""
        if action == "set_emcon":
            prev, self.emcon = self.emcon, bool(params["on"])
            self.events.append(("info", "ew", "EMCON SET: radiating emitters silenced" if self.emcon else "EMCON lifted"))
            return {"on": prev}
        if action == "activate_defense":
            newly = [d for d in params["systems"] if not self.is_active(d)]
            for d in newly:
                if d == "NIXIE":
                    self.nixie = True
                    self.events.append(("info", "asw", "TAO: Nixie streamed"))
                else:
                    self.nodes[d]["active"] = True
            return {"deactivate": newly}
        if action in ("close_breaker", "open_breaker"):
            prev = self.set_breaker(params["breaker"], action == "close_breaker")
            return {"breaker": params["breaker"], "closed": prev}
        if action == "dispatch_team":
            return self.dispatch(params["team"], params["casualty"])
        if action == "engage_target":
            weapon, m = self.weapon_for(params["target"])
            if weapon == "SM-2" and m["range"] > SM2_MAX_NM:
                self.held[m["id"]] = int(params.get("rounds", 2))
                self.events.append(("critical", "defense", f"BATTERIES RELEASED on {m['id']} ({m['range']:.1f} nm): "
                                                           f"fire control will launch at {SM2_MAX_NM:.0f} nm"))
                return {"held": m["id"]}
            return self.engage(params["target"], int(params.get("rounds", 2)))
        if action == "set_gq":
            prev = self.gq
            self.set_gq(True)
            return {"gq": prev}
        return {}

    def undo(self, action: str, inverse: dict) -> None:
        if action == "set_emcon":
            self.emcon = inverse["on"]
        elif action == "activate_defense":
            for d in inverse.get("deactivate", []):
                if d == "NIXIE":
                    self.nixie = False
                else:
                    self.nodes[d]["active"] = False
        elif action in ("close_breaker", "open_breaker"):
            self.set_breaker(inverse["breaker"], inverse["closed"])
        elif action == "set_gq":
            self.set_gq(inverse.get("gq", False))
        elif action == "dispatch_team":
            t = self.teams[inverse["team"]]
            cas = self.casualties.get(t["casualty"] or "")
            if cas:
                cas["team"] = None
            t.update(status="returning", target=float(t["home"]), casualty=None, boundaries=None)

    # ------------------------------------------------------------------ step & view
    def step(self, dt: float, world, active: set[str] | None = None) -> None:
        on = active or {"nav", "systems", "dc"}
        if "dc" in on:
            self._step_dc(dt)
        if "nav" in on:
            self._step_threats(dt, world)
        self.flow()
        for g, pct in self.gen_load.items():
            if pct > 100 and g not in self._overload_alerted:
                self._overload_alerted.add(g)
                self.events.append(("critical", "ship-systems", f"EOOW: {g} overloaded, {pct}% load"))
            elif pct <= 100:
                self._overload_alerted.discard(g)

    def view(self, world) -> dict:
        hostiles = [{"id": c.id, "range": round(c.range_nm, 1), "bearing": round(c.bearing), "classification": c.classification,
                     **{k: (round(v) if isinstance(v, float) else v) for k, v in self.hostile_state.get(c.id, {}).items()}}
                    for c in world.contacts.values() if c.kind == "hostile"]
        return {
            "nodes": list(self.nodes.values()),
            "breakers": list(self.breakers.values()),
            "compartments": COMPARTMENTS,
            "bulkheads": BULKHEADS,
            "casualties": list(self.casualties.values()),
            "teams": list(self.teams.values()),
            "emcon": self.emcon,
            "radiating": (not self.emcon and self.ok("RADAR")) or self.nodes["ECM"]["active"],
            "emcon_radius_nm": EMCON_RADIUS_NM,
            "hostiles": hostiles,
            "interceptors": [{"id": i["id"], "weapon": i.get("weapon", "SM-2"), "target": i["target"],
                              "range": round(i["range"], 2), "bearing": i["bearing"]} for i in self.interceptors],
            "torpedoes": [{"id": t["id"], "from": t["from"], "range": round(t["range"], 2), "bearing": round(t["bearing"]),
                           "run_left": round(t["run_left"], 2), "speed_kn": TORP_KN} for t in self.torpedoes],
            "mags": {"SM-2": self.magazine, **self.mags},
            "nixie": self.nixie,
            "splashes": [{**sp, "range": round(sp["range"], 2)} for sp in self.splashes],
            "magazine": self.magazine,
            "evasive": self.evasive(),
            "inbound": [{"id": m["id"], "from": m["from"], "range": round(m["range"], 2), "bearing": m.get("bearing"),
                         "engaged_by": [i["id"] for i in self.interceptors if i["target"] == m["id"]] +
                                       (["released"] if m["id"] in self.held else []),
                         "eta_s": round(m["range"] / INBOUND_KN * 3600)} for m in self.inbound_on_cop()],
            "defenses": {d: {"active": self.nodes[d]["active"], "ready": self.ok(d), "pk": pk} for d, pk in DEFENSES.items()},
            # Chance an inbound gets through: 0 when nothing is inbound. "if_attacked" is the defensive posture.
            "p_hit": round(self.p_hit(), 2) if self.inbound_on_cop() else 0.0,
            "p_hit_if_attacked": round(self.p_hit(), 2),
            "hits": self.hits,
            "propulsion_ok": self.propulsion_ok,
            "shafts_ok": self.shafts_ok,
            "max_speed_kn": self.max_speed_kn,
            "ship": SHIP,
            "steering_ok": self.steering_ok,
            "emcon_prediction": self.predict_emcon(world),
            "gq": self.gq,
            "firemain_ok": self.firemain_ok,
            "gen_load": self.gen_load,
            "_restore": {nid: self.restoring_breaker(nid) for nid in self.unpowered_vital()},
        }
