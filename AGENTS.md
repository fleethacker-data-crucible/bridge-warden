# AGENTS.md

Instructions for any AI agent or model working in this repo. This is the only instruction file. `CLAUDE.md`, `GEMINI.md` and `.github/copilot-instructions.md` just point here. Chat-only models (ChatGPT, Grok in a browser) can't see the repo, so paste this file in at the start of a session.

## Context
Team **Data Crucible** at the **FLEETHACKER** hackathon. Each fact lives in one doc; this file points to it.

| Doc | Source of truth for |
|---|---|
| `docs/event-rules.md` | Official rules, categories, OSW ROIs, definition of victory (official text, don't edit) |
| `docs/judging-criteria.md` | Scoring weights and what each criterion needs from us |
| `docs/submission.md` | Submission form fields and the description template |
| `docs/credential-vault.md` | How to get service keys, and the security rules |
| `docs/open-questions.md` | Unknowns. Never assume an answer to one. |
| `docs/design/style-guide.md` | All UI: colors, type, layout, components |

## Repo layout
```
docs/                          global event docs (above)
tools/api-console/             local UI for testing any API
tools/creds/pull-creds.ps1     pulls vault keys into the root .env
challenges/<category>/<project>/
  README.md                    what it is, how to run it
  docs/                        required project docs (below)
```
Categories: `naval-agentic-ai`, `bring-your-own-idea`, `digital-challenge`.

## How to build
1. **Every feature must raise a judging criterion** (`docs/judging-criteria.md`). If it raises none, flag it and don't build it.
2. **Keep the main demo path working at all times.** Finish it before adding extras.
3. **Offline-first.** The app must still work with no network; any cloud call needs a fallback.
4. **Config over code.** Never hardcode one ship, rating or provider.
5. **Use any AI model through one wrapper.** Pick the provider and model with `.env` (`LLM_PROVIDER`, `LLM_MODEL`).
6. **Keys come only from `.env`.** Never hardcode, print, paste or commit a key.
7. **Unclassified only.** No CUI, PII or nonpublic government data.
8. **Show human authority in AI features.** Approval points, prohibited actions, monitoring, escalation, stop, audit log.
9. **UI follows the style guide exactly.**
10. **Stay in the project folder you were given.** Change `docs/`, `tools/` or root files only when asked.
11. **Steer back.** If the work drifts from these rules, say so.

## Required project docs
Each project's `docs/` folder must contain these files. Keep them updated as you work, not at the end.

| File | Contents |
|---|---|
| `plan.md` | User, problem, current process, solution, OSW ROI(s) it serves |
| `metrics.md` | Before/after numbers for the burden reduced |
| `friction-log.md` | Each barrier: type, responsible org, recommended action |
| `lessons-learned.md` | Skills practiced, tools learned, collaboration |
| `transition-plan.md` | Mission owner, technical owner, next experiment, resources, 30/60/90 |
| `prior-work.md` | What existed before the event vs. what we built during it |
| `disclosure.md` | Every model, API, dataset and service used, and where it came from |
| `submission-description.md` | Paste-ready text for the submission form (template in `docs/submission.md`) |
