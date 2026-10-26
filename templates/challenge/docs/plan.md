# <Project name> — Plan

> **One-liner:** <what it does, for whom, in one sentence>

**Challenge:** [<challenge name>](../challenge.md) · **Category:** <category> · **Team:** Data Crucible

Every challenge plan uses these sections, in this order. Fill each one, or write "N/A: <reason>". Don't delete sections.

## Table of Contents
1. Summary
2. Problem
3. Challenge Coverage
4. Solution Concept & Architecture
5. Human Authority & Safety
6. Fleet Conditions (Edge, Power, Bandwidth, DDIL, Security)
7. Judging & ROI Alignment
8. Technical Design
9. User Interface
10. Build Timeline & Team Split
11. Test Plan
12. Demo Plan
13. Risks & Mitigations
14. Stretch Goals
15. Setup & Pre-Event Checklist
16. Project Docs

---

## 1. Summary
What we're building and why it wins, in 3–5 sentences.

## 2. Problem
| | |
|---|---|
| **User** | Rating / watch station / billet |
| **Operational problem** | |
| **Current process** | Step by step, as done today |
| **Measurable burden** | Time, steps, errors, consoles. Baselines live in `metrics.md`. |

## 3. Challenge Coverage
Every requirement, deliverable question and listed capability from `challenge.md`, mapped to where we answer it.

| Challenge item | Our answer | Section |
|---|---|---|

## 4. Solution Concept & Architecture
Diagram plus a short explanation of the data and control flow.

## 5. Human Authority & Safety
Required by event rule 6 (and the Naval Agentic AI category, when it applies).

| | |
|---|---|
| **May do** | |
| **May never do** | |
| **Needs human approval** | |
| **Monitoring & escalation** | |
| **Logging / audit** | |
| **Stop / shutdown** | |

## 6. Fleet Conditions (Edge, Power, Bandwidth, DDIL, Security)
Required by event rule 9, plus any challenge-specific limits.

| Condition | Design decision | Budget / target | How we prove it |
|---|---|---|---|
| Compute & power | | | |
| Bandwidth | | | |
| Disconnected (DDIL) | | | |
| Cybersecurity | | | |
| Portability | | | |
| Shipboard constraints | | | |
| Sustainment | | | |
| Data classification | Unclassified / synthetic only | | |

## 7. Judging & ROI Alignment
| Criterion (weight) | How we score | Evidence we'll show |
|---|---|---|
| Warfighting & Deckplate Impact (30%) | | |
| Speed to Fleet (20%) | | |
| Fleet Scalability (20%) | | |
| Barrier Removal (15%) | | |
| Judges' Discretion (15%) | | |

**OSW ROIs served:** <list each ROI with its evidence>

## 8. Technical Design
Tech stack, repo structure, data models, components, APIs. Use as many subsections as needed.
If the project uses AI models, include an **LLM tasks** table (task, inputs, output, fallback, where shown) and name the wrapper file. The wrapper itself follows `docs/llm-wrapper.md`.

## 9. User Interface
Screens and layouts. The global `docs/design/style-guide.md` wins on any conflict.

## 10. Build Timeline & Team Split
Roles, an hour-by-hour plan, and the **MVP cut line**.

## 11. Test Plan

## 12. Demo Plan
Setup, a timed script, backup paths, expected judge questions, backup video.

## 13. Risks & Mitigations
| Risk | Mitigation |
|---|---|

## 14. Stretch Goals

## 15. Setup & Pre-Event Checklist
Software to install, first-run commands, offline readiness, hardware, checklist.

## 16. Project Docs
| Doc | Purpose |
|---|---|
| [metrics.md](metrics.md) | Before/after numbers |
| [friction-log.md](friction-log.md) | Barriers hit (event rule 10) |
| [lessons-learned.md](lessons-learned.md) | Skills and tools learned |
| [transition-plan.md](transition-plan.md) | Owners, next experiment, 30/60/90 (event rule 11) |
| [prior-work.md](prior-work.md) | Pre-event vs. event work (event rule 7) |
| [disclosure.md](disclosure.md) | Models, APIs, datasets, services (event rule 5) |
| [submission-description.md](submission-description.md) | Paste-ready text for the submission form |
