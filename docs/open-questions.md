# Open Questions (unknowns about the event)

Agents: never treat an open item as decided. Design so the answer can change without rework, and update this file when an answer arrives.

| # | Question | Status | How we stay ready |
|---|---|---|---|
| 1 | Will the event provide AI models/tools (via the vault), or can we bring our own (OpenAI, Grok, Anthropic, Gemini, local)? | **Unknown** | All LLM calls go through one wrapper. Provider and model come from `.env` (`LLM_PROVIDER`, `LLM_MODEL`). No vendor-specific features in core logic. |
| 2 | Which services and keys will be in the team credential vault? | **Unknown** (token not issued) | Run `pull-creds.ps1 -List` as soon as the token arrives, then update this file. |
| 3 | Must the demo run fully offline (DDIL)? | Implied by the judging criteria (Speed to Fleet) | Keep a cached/offline fallback for every LLM call. |
| 4 | What use cases and datasets are on the submission form's pick lists? | **Unknown** | Record them in `docs/submission.md` when published, and map each project to them. |
| 5 | Must the repo be public? Where can a demo be hosted? | **Unknown** | Keep repos clean and free of secrets. Have a recorded demo video as a fallback. |
| 6 | What is the **Fleet-Ready Definition of Done**? Challenges must meet it. | **Unknown** | Get the official text, add it to `docs/`, and check every project against it. |
| 7 | The portal lists categories (e.g. "Open Innovation") that don't match the rules' 3 categories. How do they map? | **Unknown** | Record each challenge's category exactly as listed in its `challenge.md`. |
| 8 | What are the "curated use cases"? | **Unknown** | Likely the same list as the submission form's use-case picker. |
