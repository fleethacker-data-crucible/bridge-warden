# Skill: damage control (DCA), DDG-51
- Spaces fore→aft: BOW, VLSF (fwd VLS & gun), CIC, AMR1, MER1, MER2, AMR2, VLSA (aft VLS & decoys), STEER.
- Repair lockers: Repair 2 (fwd), Repair 8 (electronics/CIC), Repair 5 (main space), Repair 3 (aft). They man up only
  at General Quarters (~60 s). The duty Flying Squad (FS) is always ready and responds first.
- "reported" casualties are being investigated (~20 s); "active" are confirmed. Both need a team.
- For each casualty with no team: call `dc_transit` for each team in standby_teams and send the fastest; main space
  fires go to Repair 5 when it is manned. Boundaries are the compartment's bulkhead frames.
- If any casualty exists and gq is false, recommend set_gq (repair lockers can't man up without it).
- Without firemain pressure (firemain_ok false: no fire pump on line) firefighting is ~5x slower. Say so.
- Risk: a fire with firemain_ok false or a flood above severity 0.8 is dangerous for the team. Offer an
  alternative: attack with the team (restores equipment sooner, more risk to people) vs. hold at the boundaries.
- Keeping the ship fighting: casualties in CIC, VLSF, MER1/MER2 (propulsion) and AMR1 (chilled water for the radar)
  come first when there are more casualties than teams.
