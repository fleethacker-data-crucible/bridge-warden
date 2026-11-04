# Disclosure — Bridge Warden

Event rule 5: all tooling, models, datasets and external services must be disclosed. Must match the "Using Datasets" field on the submission form.

| Type | Name | Source | Used for |
|---|---|---|---|
| Model | Optional live LLM provider for explanations: TBD (see `docs/open-questions.md`) | Event-provided or ours: TBD | Rationale text, conflict summaries, incident reports |
| Dataset | Synthetic scenarios (`backend/scenarios/*.json`, seeded simulation in `host/src/World.cs`) | Ours | All demo data. No real ship data, CUI or PII. |
| Dataset | Event-provided data, if any | Event | TBD, via `sim/adapters/` |
| Library | None: .NET Framework 4.x (built into Windows) via Windows PowerShell 5.1 `Add-Type`; SQLite via Windows' built-in `winsqlite3.dll` | Microsoft (OS components) | Backend (as built, C# port) |
| Model | Local LLM on an OpenAI-compatible server at `localhost:1234/v1` (LM Studio; currently `google/gemma-4-26b-a4b-qat`) | Open-weights model, run locally by the team | The three **picture deciders** (TAO, EOOW, DCO): each chooses among options its C# lookouts prepared; every chosen order is checked by Warden, and weapons, personnel and big maneuvers need a human |
| Library | None: plain HTML/CSS/JS | — | Frontend (as built) |
| Font | Exo 2, Syne Mono (bundled locally) | Open source (SIL OFL) | UI typography |
| Library | Pester 5 (installed per user from the PowerShell Gallery) | Open source | Tests |
| Tool | AI coding assistant (Claude Code) used to build the first-run prototype; others TBD | Ours | Development |
