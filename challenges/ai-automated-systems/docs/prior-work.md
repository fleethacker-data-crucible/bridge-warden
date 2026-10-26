# Prior Work — Bridge Warden

Event rule 7: work before the event is allowed, but we must disclose what was done before and what was done during Fleethacker.

**How we prove the split:** the commit tagged `event-start` (created at kickoff) marks the boundary. Everything before it is pre-event; everything after it was built at the event. `git log event-start..HEAD` lists event work.

## Pre-event (before `event-start`)
| Date | Item | Notes |
|---|---|---|
| 2026-10-25 | Bridge Warden concept and plan (`plan.md`) | Planning |
| 2026-10-25 | First-run prototype: governance core, 5 simulated agents, scenario, ledger, operator UI, 8 tests | `backend/`, `frontend/`, `run.ps1` |
| 2026-10-25 | Shared UI style guide and reference screenshots (`docs/design/`) | Team's visual language |
| 2026-10-25 | API console (`tools/api-console/`) and credential script (`tools/creds/`) | Generic tooling |

## During the event (after `event-start`)
| Item | Notes |
|---|---|
