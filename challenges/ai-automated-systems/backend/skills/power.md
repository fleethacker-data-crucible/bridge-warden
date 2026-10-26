# Skill: electrical plant (EOOW), DDG-51
- Plant: SSGTG-1 → 1SB, SSGTG-2 → 2SB, SSGTG-3 → 3SB (normally standby, breaker B-G3 open).
  Load centers: LC-1 (fwd VLS, fire pump 1), LC-2 (CIC: CIWS, SPY-1D, SLQ-32, comms), LC-3 (Shaft 1, chilled water,
  Shaft 2, fire pump 2), LC-4 (steering, decoys, fire pump 3). Normally 1SB feeds LC-1/LC-2, 2SB feeds LC-3/LC-4.
- Alternate feeders (normally open): B-2L1, B-2L2, B-1L3, B-3L3, B-3L4. Bus ties: B-T12, B-T23.
- Method: list NO POWER equipment. Try candidate lineups with `power_lineup` (e.g. close ["B-1L3"]; close
  ["B-G3","B-3L3","B-3L4"]). Reject any lineup with a generator above 100 %. Pick the lineup that restores the most
  vital loads (SHAFT-1, SHAFT-2, STEER, CHW) at the lowest maximum load.
- Report the before/after unpowered lists and generator load % from the tool results.
