# LLM Wrapper Spec (all projects)

Every project that uses an AI model calls it through **one wrapper module**. No other file imports a provider SDK or calls a provider API. This keeps us provider-neutral (see `docs/open-questions.md` #1), offline-capable and auditable.

Each project's plan (section 8) names its wrapper file and lists its tasks. Everything else is defined here.

## 1. Settings (`.env`)
| Variable | Values | Default | Purpose |
|---|---|---|---|
| `LLM_MODE` | `live` · `cached` · `local` · `off` | `cached` | How answers are produced (section 3) |
| `LLM_PROVIDER` | e.g. `openai`, `xai`, `anthropic`, `gemini`, `bedrock`, `ollama` | none | Provider for `live` |
| `LLM_MODEL` | provider's model name | none | Model for `live` |
| `LLM_BASE_URL` | any **OpenAI-compatible `/v1`** server (LM Studio, llama.cpp, vLLM, Ollama `/v1`) | `http://localhost:1234/v1` | Endpoint for `local` (runs on the box, no network) |
| `LLM_LOCAL_MODEL` | model id on that server | first model at `/v1/models` | Model for `local` |
| `LLM_API_KEY` | sent as `Bearer` token | `local` | Most local servers ignore it |
| `LLM_TIMEOUT_S` | number | `3` | Per-call timeout |
| `LLM_MAX_TOKENS` | number | `400` | Caps cost and latency |
| `<PROVIDER>_API_KEY` | from the vault (`tools/creds/pull-creds.ps1`) | none | Never hardcoded or logged |

Switching providers = editing `.env`. No code changes.

## 2. Interface
The rest of the app calls **named tasks**, never raw prompts:

```python
result = llm.run(task="summarize_conflict", inputs={...})
```

| Field returned | Meaning |
|---|---|
| `text` / `data` | The answer (plain text, or JSON validated against the task's schema) |
| `source` | `live` · `local` · `cache` · `fallback` |
| `provider`, `model` | What actually answered |
| `latency_ms` | Time taken |
| `ok` | `false` only if even the fallback failed (should never happen) |

**Each task defines:** a prompt template, the input fields, an output schema (if structured), and **fallback text** built from the inputs without an AI model (e.g. "Conflict: nav-01 wants 16 kn, col-01 wants 8 kn"). Tasks live in one file, e.g. `llm_tasks.py` / `llm_tasks.yaml`.

## 3. Modes and fallback order
| Mode | Order tried | Use when |
|---|---|---|
| `live` | provider → cache → fallback | Network is solid and the provider is allowed |
| `cached` | cache → fallback | **Demos** and offline use. Deterministic. |
| `local` | local model → cache → fallback | Edge box with no network |
| `off` | fallback only | Lowest power, or AI not permitted |

- A timeout or error on any step drops to the next one. **The UI never waits more than `LLM_TIMEOUT_S`.**
- Structured output that fails schema validation counts as an error.

## 4. Cache
- Key = hash of (task, normalized inputs). Stored as a JSON file in the project (e.g. `fixtures/llm_cache.json`), committed so demos work on any machine.
- **Fill it before demos:** run the demo scenario once with `LLM_MODE=live` and `LLM_RECORD=1`, then review the answers and commit the cache.

## 5. Rules
- **AI never makes decisions.** It explains, summarizes and drafts. Decisions come from deterministic logic or a human (event rule 6).
- **Unclassified inputs only.** Send only synthetic or approved data; never secrets, CUI or PII.
- **Every call is logged** (the project's audit log/ledger): task, source, provider, model, latency, success. No keys, and the full prompt only if the data is safe to store.
- **Show the source in the UI** when it matters (e.g. a small "cached" / "fallback" tag), so judges and operators know what produced the text.
- Libraries: a multi-provider client (e.g. LiteLLM) is allowed **inside** the wrapper only. List whatever is used in `disclosure.md`.

## 6. Required tests
- Every task returns its fallback when `LLM_MODE=off`.
- A forced timeout falls through to cache, then fallback, within `LLM_TIMEOUT_S`.
- `cached` mode returns identical output across runs.
- Changing `LLM_PROVIDER`/`LLM_MODEL` needs no code change (one test per provider we have keys for).
