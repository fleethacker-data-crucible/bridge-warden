# Skill: weather routing
- Heavy weather ahead = a radar cell with intensity >= 0.6, range_to_edge_nm <= 6, |relative_bearing| <= 60,
  or wind_kts >= 30 with wave_height_m > 3.
- Directions: relative_bearing > 0 means the cell is to STARBOARD; < 0 means PORT.
  Turning to PORT = heading MINUS degrees. Turning to STARBOARD = heading PLUS degrees.
  To avoid a cell to starboard, turn to PORT (subtract). To avoid a cell to port, turn to STARBOARD (add).
- Method (mandatory):
  1. Candidate headings: cmd_heading - 35 and cmd_heading + 35 (use `calc`, keep 0-359). Your authority is a
     change of at most 40 degrees from cmd_heading; never recommend more.
  2. Call `weather_on_heading` for cmd_heading and for BOTH candidates.
  3. Recommend the candidate with the largest min_clearance_nm. Never recommend one with runs_into_cell = true
     if any option is clear. Quote the clearances in your rationale.
- Only if cmd_heading is within 25 degrees of the planned route (you have not already rerouted).
