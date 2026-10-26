# Submission Description (paste-ready)

Copy everything below the line into the form's Project Description field. Template: `docs/submission.md`.

---
## Problem
Future ships will run many AI systems at once. Each may behave well alone, but together they can conflict, oscillate or act unsafely, and today the watchstander has to merge separate consoles in their head.

## Solution
**Bridge Warden** is a governance and orchestration layer between every onboard AI agent and the ship. No agent touches an actuator directly. Each action becomes a typed Intent that Warden authenticates, checks against authority and safety limits, compares with other agents, resolves or escalates to a human, executes and logs. Operators can pause, constrain, override, roll back or SAFE-STOP any agent.

## Impact (Warfighting & Deckplate)
TBD: before/after numbers from metrics.md.

## Speed to Fleet
Runs fully offline on a single low-power box. No cloud, no infrastructure.

## Scalability
Agents, policies and data sources are configuration plus adapters, so it applies to any ship class or domain.

## Barriers Removed
TBD: from friction-log.md.

## Human Authority & Safety
Approval points, hard denies, runtime monitors, auto-quarantine, a hash-chained audit ledger and a system-wide SAFE-STOP.

## OSW ROI Alignment
Learning and Adaptation During War; Accelerating Platform Adoption; Clarifying Friction Points; Digital Workforce.

## Next Steps (30/60/90)
TBD: from transition-plan.md.

## Disclosure
TBD: from disclosure.md. Pre-event vs. event work: see prior-work.md (boundary = `event-start` git tag).
