# Disclosure — Bridge Warden

Event rule 5: all tooling, models, datasets and external services must be disclosed. Must match the "Using Datasets" field on the submission form.

| Type | Name | Source | Used for |
|---|---|---|---|
| Model | LLM provider/model: TBD (see `docs/open-questions.md`) | Event-provided or ours: TBD | Rationale text, conflict summaries, incident reports (never decisions) |
| Dataset | Synthetic scenarios (`backend/scenarios/*.json`, seeded simulation in `host/src/World.cs`) | Ours | All demo data. No real ship data, controlled information or PII. |
| Dataset | Event-provided data, if any | Event | TBD, via `sim/adapters/` |
| Library | None: .NET Framework 4.x (built into Windows) via Windows PowerShell 5.1 `Add-Type`; SQLite via Windows' built-in `winsqlite3.dll` | Microsoft (OS components) | Backend (as built, C# port) |
| Model | Local LLM on an OpenAI-compatible server at `localhost:1234/v1` (first run: `gemma-4-12b-q4_0-it-qat`) | Open-weights model, run locally by the team | **Tactical AI (LLM) agent** that proposes actions; every proposal is governed by Warden and approved by a human |
| Library | None: plain HTML/CSS/JS | — | Frontend (as built) |
| Font | Exo 2, Syne Mono (bundled locally) | Open source (SIL OFL) | UI typography |
| Library | Pester 5, PSScriptAnalyzer (CI runners only) | Open source | Tests and lint |
| Tool | AI coding assistant used to build the first-run prototype; others TBD | Ours | Development |
