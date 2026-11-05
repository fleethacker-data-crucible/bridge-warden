# Skill: undersea warfare (ASW), DDG-51
- A submarine is a track with domain "subsurface". A torpedo in the water runs at 45 kn and has limited fuel:
  if we open the range fast enough, it runs out of fuel before reaching us (that is the main defense).
- Closing speed = 45 + own_speed * cos(own_heading - torpedo_bearing). Heading AWAY = torpedo bearing + 180.
- NIXIE (AN/SLQ-25 towed decoy): seduction chance as the torpedo passes 2 nm. Stream it (activate_defense
  ["NIXIE"]) as soon as a submarine or torpedo is on the COP.
- ASROC vs a HOSTILE submarine: 1-10 nm, pk 0.55. Fire only at HOSTILE (a sub that fired is HOSTILE).

Method when torpedoes_in_water is non-empty (FIRST answer, one tool turn):
  1. `calc` escape heading = (torpedo bearing + 180) mod 360.
  2. `torpedo_evasion` for that heading at ship_max_speed_kn, and for cmd_heading at current speed.
  3. Recommend set_heading = escape heading in ONE order (any size turn is allowed for evasion) and
     set_speed = ship_max_speed_kn.
  4. activate_defense ["NIXIE"] if nixie_streamed is false. set_gq if gq is false.
  5. Return fire: engage_target at the HOSTILE submarine with rounds 1 if it is 1-10 nm and nothing is in flight at it.
When a SUSPECT submarine is on the COP but no torpedo: stream NIXIE and set_gq; do not fire (not HOSTILE).
Otherwise do nothing.
NEVER repeat an action that is already done (nixie_streamed true, gq true, cmd_heading already the escape heading,
cmd_speed already max, a weapon already in flight at the submarine). Repeats waste your action slots.
Once torpedoes_in_water is empty again, do nothing more.
