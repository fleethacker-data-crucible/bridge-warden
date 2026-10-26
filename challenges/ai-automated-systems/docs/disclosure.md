# Disclosure — Bridge Warden

Event rule 5: all tooling, models, datasets and external services must be disclosed. Must match the "Using Datasets" field on the submission form.

| Type | Name | Source | Used for |
|---|---|---|---|
| Model | LLM provider/model: TBD (see `docs/open-questions.md`) | Event-provided or ours: TBD | Rationale text, conflict summaries, incident reports (never decisions) |
| Dataset | Synthetic scenarios (`sim/generator.py`, seeded) | Ours | All demo data. No real ship data, CUI or PII. |
| Dataset | Event-provided data, if any | Event | TBD, via `sim/adapters/` |
| Library | FastAPI, Uvicorn, websockets, Pydantic, PyYAML, psutil, SQLite (stdlib) | Open source | Backend (as built) |
| Library | LiteLLM (optional, only inside `warden/llm.py`, not installed by default) | Open source | Multi-provider client for `LLM_MODE=live` |
| Model | Local LLM on an OpenAI-compatible server at `localhost:1234/v1` (first run: `gemma-4-12b-q4_0-it-qat`) | Open-weights model, run locally by the team | **Tactical AI (LLM) agent** that proposes actions; every proposal is governed by Warden and approved by a human |
| Library | None: plain HTML/CSS/JS | — | Frontend (as built) |
| Font | Exo 2, Syne Mono (bundled locally) | Open source (SIL OFL) | UI typography |
| Library | pytest, httpx | Open source | Tests |
| Tool | AI coding assistant (Claude Code) used to build the first-run prototype; others TBD | Ours | Development |
