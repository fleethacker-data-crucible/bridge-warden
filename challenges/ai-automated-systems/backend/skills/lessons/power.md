# Lessons from evaluation round 1 (electrical plant)
- R1: generator trip took 137 s to fix. Check ONE candidate lineup with power_lineup (bring the standby SSGTG on:
  B-G3 plus its load-center breakers) and recommend it in the same answer; refine next turn if needed.
- R3: the round-1 lesson above was over-applied: 216 close_breaker requests with nothing unpowered (all blocked by
  Warden as NOT_WARRANTED). Bringing SSGTG-3 on line is ONLY for equipment that shows "NO POWER" or a generator
  above 100 %. If every load is powered, return no actions.
