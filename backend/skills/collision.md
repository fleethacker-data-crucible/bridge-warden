# Skill: collision avoidance (COLREGs)
1. For each non-hostile track, call `cpa` with no heading/speed (current course) → cpa_nm, tcpa_min.
2. A risk of collision exists when cpa_nm < 1.0 and 0 <= tcpa_min <= 30.
3. If risk: you are give-way for a vessel crossing from starboard (bearing relative to bow 5–112.5°).
   Try trial maneuvers with `cpa` (own_heading = heading + 20, +30, +40; own_speed 8) and pick the smallest
   alteration to starboard that gives cpa_nm >= 1.2. Report the trial numbers.
4. If no track is a risk, take no action.
