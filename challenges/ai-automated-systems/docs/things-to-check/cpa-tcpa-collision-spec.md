# CPA / TCPA Collision Risk Spec (for Bridge Warden Collision Agent)

## Purpose
Determine whether a target vessel poses a collision risk to own ship by computing the
Closest Point of Approach (CPA) and Time to CPA (TCPA), then classifying the risk.

## 1. Inputs
| Field | Own ship | Target | Units |
|---|---|---|---|
| Position | lat/lon | lat/lon | degrees |
| Course over ground (COG) | `C_o` | `C_t` | degrees true, 0 = north, clockwise |
| Speed over ground (SOG) | `S_o` | `S_t` | knots |
| Timestamp | `t_o` | `t_t` | UTC |

Use COG/SOG (not heading / speed through water) — they reflect actual motion including current and leeway.

## 2. Convert positions to a local flat frame (nautical miles)
Own ship at the origin. For targets within roughly 20–30 NM, an equirectangular projection is accurate enough:

```
x = (lon_t - lon_o) * 60 * cos(lat_o in radians)   # east, NM
y = (lat_t - lat_o) * 60                           # north, NM
```
(1 minute of latitude ≈ 1 NM.) For longer ranges, use a proper geodesic/ENU conversion.

## 3. Convert course/speed to velocity vectors (knots = NM per hour)
Navigation bearings are measured from north, clockwise, so sine goes with x (east):
```
vx = S * sin(C)
vy = S * cos(C)
```
Compute `(vox, voy)` for own ship and `(vtx, vty)` for the target.

## 4. Relative position and velocity (target relative to own ship)
```
rx = x_t - x_o          ry = y_t - y_o
vx = vtx - vox          vy = vty - voy
```

## 5. TCPA and CPA
```
v2   = vx^2 + vy^2
TCPA = -(rx*vx + ry*vy) / v2            # hours  (multiply by 60 for minutes)
CPA  = sqrt( (rx + vx*TCPA)^2 + (ry + vy*TCPA)^2 )   # NM
```
Also useful:
```
range   = sqrt(rx^2 + ry^2)
bearing = atan2(rx, ry) converted to 0–360 degrees true
```

### Edge cases
- **v2 ≈ 0** (same course and speed): relative motion is zero; range never changes. Set `TCPA = 0` (or undefined) and `CPA = current range`. Use a small epsilon, e.g. `v2 < 1e-6`.
- **TCPA < 0**: closest approach is in the past; vessels are opening. Not a threat (CPA = current range going forward).
- **Stale data**: before computing, dead-reckon each vessel forward to a common timestamp: `x += vx * dt`, `y += vy * dt`.

## 6. Risk classification (configurable thresholds)
Example defaults — tune for vessel type, speed and sea room (open sea vs. harbour/strait):

| Level | Condition |
|---|---|
| **DANGER** | `0 <= TCPA <= 6 min` AND `CPA < 0.5 NM`, OR current range < 0.5 NM |
| **WARNING** | `0 <= TCPA <= 12 min` AND `CPA < 1.0 NM` |
| **MONITOR** | `0 <= TCPA <= 30 min` AND `CPA < 2.0 NM` |
| **SAFE** | otherwise, or `TCPA < 0` |

Keep thresholds in YAML policy config, not hard-coded. Add hysteresis (e.g. require 2–3 consecutive ticks to downgrade a level) to avoid flickering alerts.

## 7. Optional: COLREG encounter type (for the "Why?" panel)
Using relative bearing of the target from own heading and the course difference:
- **Head-on** (Rule 14): target roughly dead ahead (within ~±6°) and courses nearly reciprocal → both alter to starboard.
- **Crossing** (Rule 15): target on own starboard side (relative bearing ~005°–112.5°) → own ship is give-way. On port side → own ship is stand-on (Rule 17).
- **Overtaking** (Rule 13): one vessel approaching from more than 22.5° abaft the other's beam (relative bearing ~112.5°–247.5°) → overtaking vessel keeps clear.
Treat this as decision support only, not a final legal determination.

## 8. Limitations (state these in the demo)
1. **Straight-line assumption**: assumes both vessels hold constant course and speed. Any turn or speed change invalidates the prediction; recompute every tick.
2. **Point targets**: vessels are treated as points. Real ships have length and beam; add a safety margin (e.g. include half of each vessel's length in the CPA threshold).
3. **Flat-earth approximation**: fine at short range; errors grow with distance and at high latitudes.
4. **Sensor quality**: AIS can be delayed, missing, spoofed or wrong; radar has noise and may miss small targets. Results are only as good as inputs. Track data age and confidence.
5. **No intent prediction**: doesn't know if the target is about to turn, follow a channel, or is restricted in ability to manoeuvre.
6. **No environment**: ignores shallow water, traffic separation schemes, land, and other vessels limiting escape options.
7. **Multi-target**: compute for every target, but avoidance for one may create risk with another — the governance layer's conflict detection should check this.
8. **Not certified**: prototype logic for a hackathon demo, not a certified navigation or collision-avoidance system.

## 9. Test cases (own ship at origin, course 000°, 12 kn)
| # | Target position (x, y NM) | Target course / speed | Expected TCPA | Expected CPA | Expected level |
|---|---|---|---|---|---|
| 1 | (0, 6) | 180° / 10 kn (head-on) | ≈ 16.4 min | 0.0 NM | MONITOR (escalates as TCPA drops) |
| 2 | (1, 6) | 180° / 10 kn (passing 1 NM off) | ≈ 16.4 min | 1.0 NM | MONITOR |
| 3 | (5, 5) | 270° / 12 kn (crossing from starboard) | 25.0 min | 0.0 NM | MONITOR; own ship is give-way |
| 4 | (0, -2) | 000° / 12 kn (same course & speed) | n/a (v2 = 0) | 2.0 NM (constant) | SAFE |
| 5 | any target moving away | — | negative | — | SAFE |

## 10. Implementation notes
- Pure function: `compute_cpa(own, target) -> {tcpa_min, cpa_nm, range_nm, bearing_deg, level}`.
- Unit test all cases above with pytest, including the v2 ≈ 0 and TCPA < 0 edge cases.
- Recompute every simulation tick for every target; publish results to the Common Operating Picture.
- The collision agent emits an **Intent** (e.g. "alter course 20° to starboard") that Warden checks for conflicts with other agents before anything is executed.
