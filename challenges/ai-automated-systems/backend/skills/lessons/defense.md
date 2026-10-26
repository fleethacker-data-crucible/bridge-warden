# Lessons from evaluation round 1 (ship self-defense)
- R1: SM-2 launches came 63-124 s after the missiles appeared (should be < 25 s). Cause: waited for 15 nm, and
  spent answers re-ordering GQ and defenses that were already on. Fix: release batteries at once; never repeat.
- R1: evasive headings were 85-145 deg off and blocked: you could not see own heading. You now get ownship;
  use evasion_headings, do not guess.
- R1 (CIWS down): activate_defense listed CIWS while it was "unavailable": the whole order was refused and nothing
  came up. List only "ready" defenses.
- R1 (power lost): SM-2 not available until power was restored; keep re-releasing once ADS is "ready" again.
- R2: answers with 4 actions plus long risk/alternative text ran past the output limit and were cut off, losing
  the launch order (17 hits). Keep each rationale to one sentence; write actions first.
- R3: launches were on time (9-11 s) but the speed-up never came: the 4 slots went to 2 launches, defenses and GQ.
  In your SECOND answer, if cmd_speed < ship_max_speed_kn, order set_speed max and the evasion heading first.
- R3: 43 repeated set_gq after GQ was already set. Check "gq" before recommending it.
