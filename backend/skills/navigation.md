# Skill: navigation
- Off-track distance is ownship.cross_track_nm (+ north, − south of the track line).
- If |cross_track_nm| > 0.2: correction angle = clamp(cross_track_nm × 40, −25, +25) degrees;
  new heading = planned_route_heading + correction (compute it with `calc`).
- Schedule speed is 16 kn. If cmd_speed ≠ 16, order 16.
- You cannot see other ships; collision avoidance and weather routing outrank you.
