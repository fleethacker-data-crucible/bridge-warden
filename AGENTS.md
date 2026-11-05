# AGENTS.md

Instructions for any AI agent or model working in this repo. This is the only instruction file. `CLAUDE.md`, `GEMINI.md` and `.github/copilot-instructions.md` just point here. Chat-only models (ChatGPT, Grok in a browser) can't see the repo, so paste this file in at the start of a session.

## Fleet UI is mandatory (do not change)

**This rule applies to every AI agent and every contributor, including people who are not developers.**

- Bridge Warden uses the shared Fleet UI, `challenges/ai-automated-systems/frontend/vendor/fleet/fleet.css`, copied unchanged from the other Fleet tools. Do not edit that file by hand; change it in the Fleet repos and copy it here.
- The Fleet look (Night watch, with the emblem, sidebar, header and status bar) is the DEFAULT when the app opens. `js/theme.js` sets it. Nobody should have to find a setting. Appearance in Settings offers Chart room and Follow the system.
- New screens use the Fleet shell and the `--fl-*` tokens and `.fl-*` components. No new raw colors, fonts or radii in components. `docs/design/style-guide.md` stays the reference for the dense operator-console layout and for notifications and decision handling.
- Status is never colour alone: always colour plus a word, and an icon where it helps. Every control works from the keyboard.
- Any change to a screen needs the UI Lead's UI/508 pass before it merges.
- If a change would break a Fleet UI rule, STOP and ask Anthony (sun-of-anton). Do not work around it. Only he may change or remove this rule.

## Context
Team **Data Crucible** at the **FLEETHACKER** hackathon. Each fact lives in one doc; this file points to it.

| Doc | Source of truth for |
|---|---|
| `docs/event-rules.md` | Official rules, categories, OSW ROIs, definition of victory (official text, don't edit) |
| `docs/judging-criteria.md` | Scoring weights and what each criterion needs from us |
| `docs/submission.md` | Submission form fields and the description template |
| `docs/credential-vault.md` | How to get service keys, and the security rules |
| `docs/open-questions.md` | Unknowns. Never assume an answer to one. |
| `docs/llm-wrapper.md` | How every project calls AI models (settings, modes, fallback, logging) |
| `docs/design/style-guide.md` | All UI: colors, type, layout, components |

## Repo layout
```
docs/                          global event docs (above)
tools/api-console/             local UI for testing any API
tools/creds/pull-creds.ps1     pulls vault keys into the root .env
templates/challenge/           copy this to start every new challenge
challenges/<challenge-name>/   folder name = official challenge name, lowercase, spaces → hyphens
  challenge.md                 official challenge text, verbatim (category recorded inside)
  README.md                    our project name, one-line pitch, how to run it
  docs/                        required project docs (below)
```

## How to build
1. **Every feature must raise a judging criterion** (`docs/judging-criteria.md`). If it raises none, flag it and don't build it.
2. **Keep the main demo path working at all times.** Finish it before adding extras.
3. **Offline-first.** The app must still work with no network; any cloud call needs a fallback.
4. **Config over code.** Never hardcode one ship, rating or provider.
5. **Use any AI model through one wrapper** built to `docs/llm-wrapper.md`. The provider and model come from `.env`.
6. **Keys come only from `.env`.** Never hardcode, print, paste or commit a key.
7. **Unclassified only.** No CUI, PII or nonpublic government data.
8. **Show human authority in AI features.** Approval points, prohibited actions, monitoring, escalation, stop, audit log.
9. **UI follows the style guide exactly.**
10. **Stay in the project folder you were given.** Change `docs/`, `tools/` or root files only when asked.
11. **Steer back.** If the work drifts from these rules, say so.

## Every challenge is structured the same
- **New challenge:** copy `templates/challenge/` to `challenges/<challenge-name>/`, paste the official text into `challenge.md`, then fill in the docs.
- **`docs/plan.md` keeps the template's 16 sections, in order, with the same names.** Write "N/A: <reason>" rather than deleting a section.
- **Required project docs** are the files in `templates/challenge/docs/`. Each project keeps all of them and updates them as work happens, not at the end.
- **Challenge Coverage (plan section 3)** must map every question and capability in `challenge.md`. Re-check it whenever the plan changes.
- **Prior work:** building before the event is allowed but must be disclosed. Log pre-event work in `prior-work.md` as it happens. At kickoff, tag the current commit `event-start` (`git tag event-start`). That tag is the official boundary.
