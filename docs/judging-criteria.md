# FLEETHACKER Judging Criteria

Each criterion is scored 0–100 and all five are required. The final score is the weighted sum.

| # | Criterion | Weight | Judges' question |
|---|---|---|---|
| 1 | **Warfighting & Deckplate Impact** | **30%** | How much does this take cognitive load off watchstanders or give back deckplate hours? |
| 2 | **Speed to Fleet** | **20%** | How quickly can this be fielded to run at the edge in disconnected afloat (DDIL) conditions? |
| 3 | **Fleet Scalability** | **20%** | How widely can it scale across ship classes, ratings or operational domains? |
| 4 | **Barrier Removal** | **15%** | How well does it remove systemic bottlenecks, manual data entry and legacy friction? |
| 5 | **Judges' Discretion** | **15%** | How strong are the technical execution, the Sailor's experience using it (UX) and the innovation? |

## What each criterion needs from us (scoring playbook)

### 1. Warfighting & Deckplate Impact — 30%
- Name the user, such as a specific rating or watch station.
- Describe the current process step by step and its measured burden.
- Show a before/after number: minutes saved per watch, steps removed or errors avoided.
- Build for the Sailor standing watch at 0200, not for the demo.

### 2. Speed to Fleet — 20%
- It runs offline on a laptop with no cloud dependency, or degrades gracefully without one.
- Installs with one click or one command. Prefer tools that are already authorized or open source.
- Spell out the steps to field it and estimate how long each takes.

### 3. Fleet Scalability — 20%
- Keep it configuration-driven, not hardcoded to one ship.
- Explicitly name the other ship classes, ratings and domains it serves.
- Use a portable stack and standard data formats.

### 4. Barrier Removal — 15%
- Eliminate manual re-entry and swivel-chair work between systems.
- Keep a friction log of the policy, data, authority, infrastructure and acquisition barriers we hit (the event rules require it).

### 5. Judges' Discretion — 15%
- Polished UI that follows `docs/design/style-guide.md` exactly.
- A working live demo. Slides alone don't score.
- Clear human approval points, a stop/shutdown path and an audit log.
