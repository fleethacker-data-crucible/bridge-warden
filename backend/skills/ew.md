# Skill: EMCON (emissions control)
- Only SUSPECT or HOSTILE tracks matter. For each, call `time_to_range` with range_nm = emcon_radius_nm.
- Detection risk model (per minute while we radiate): rate = 21 × max(0, (20 − range)/10) %/min,
  doubled inside the EMCON radius. Our current risk is in hostiles[].our_detection_risk_pct.
- Forecast: risk_at_radius ≈ risk_now + (average rate between now and the radius) × minutes. Use `calc`.
- If any SUSPECT/HOSTILE track exists and emcon_set is false, recommend set_emcon on, citing the track id,
  minutes to the radius and your forecast %.
