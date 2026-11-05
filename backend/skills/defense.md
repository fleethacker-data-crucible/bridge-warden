# Skill: ship self-defense / anti-air & surface warfare (TAO), DDG-51
Systems (illustrative numbers, NOT real):
- ADS = Mk 41 VLS / SM-2 vs inbound missiles: 900 kn, pk 0.6 per round, envelope 1.5-15 nm.
- HARPOON vs a HOSTILE surface track: 500 kn, pk 0.7, 1-60 nm (return fire; stops further launches).
- CIWS (automatic at ~1 nm once active), ECM = SLQ-32 jamming (seduction chance at 8 nm, breaks EMCON),
  CMD = decoys (seduction chance at 4 nm). Evasive: hard turn at >= 20 kn makes a leaker miss ~25%.
Rules of engagement: fire only at inbound missiles and tracks classified HOSTILE. Never at SUSPECT/UNKNOWN.
If the operator ordered CEASE FIRE, Warden blocks launches; do not request weapons until it is lifted.

Method when inbound_threats is non-empty (FIRST answer, at most one tool turn):
  1. LAUNCH NOW: engage_target rounds 2 for every inbound with empty sm2_in_flight, even beyond 15 nm: fire control
     holds the release and launches the moment it is inside 15 nm. Waiting for 15 nm yourself wastes 20+ s.
  2. POSTURE: one activate_defense listing ONLY the defenses whose state is "ready" (never "unavailable", never
     "active", never NIXIE: torpedoes belong to the ASW agent).
  3. If gq is false: set_gq.
  4. EVASIVE: call `evasion_headings` with the closest inbound's bearing; pick the option with the smaller
     |turn_from_cmd_deg| and weather clearance not negative; set_heading to it. set_speed = ship_max_speed_kn if
     ownship.cmd_speed is lower.
  NEVER repeat an action that is already done (gq true, a defense already "active", a target that already has
  sm2_in_flight or "released", cmd_speed already max, cmd_heading already the evasion heading). Repeats waste your
  4 action slots while missiles fly. Max 4 actions: launches first.
Return fire: if a track is HOSTILE and surface (domain "surface") and inside 60 nm and no own weapon is in flight
at it, recommend engage_target at the TRACK id with rounds 1 (Harpoon). Removing the shooter prevents the next salvo.
Use `hit_probability` only when there is time (time to impact > 90 s).
If a SUSPECT or HOSTILE surface track is on the COP and gq is false, recommend set_gq.
When there are no inbound threats and no SUSPECT/HOSTILE track, do nothing.
