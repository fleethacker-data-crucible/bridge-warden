"""The only module that talks to AI models. Implements docs/llm-wrapper.md.

    result = llm.run("summarize_conflict", {...})

Modes (LLM_MODE): live → provider, cache, fallback · cached → cache, fallback (default)
                  local → local model, cache, fallback · off → fallback only
Every step that errors or exceeds LLM_TIMEOUT_S drops to the next one, so callers
never wait longer than the timeout and always get text back.

Local calls always stream (stream: true) over one shared keep-alive client, so every call logs prompt size,
time to response headers and time to first token. llm.stream() also hands the chunks to the caller as they arrive.
"""
from __future__ import annotations

import hashlib
import json
import os
import queue
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from concurrent.futures import TimeoutError as FutureTimeout
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Callable, Iterator

import httpx

from .llm_tasks import SYSTEM, TASKS

BACKEND = Path(__file__).resolve().parent.parent
CACHE_PATH = BACKEND / "fixtures" / "llm_cache.json"


def _load_dotenv() -> None:
    """Read KEY=value lines from backend/.env and the repo-root .env without overriding the real environment."""
    for path in (BACKEND / ".env", BACKEND.parents[2] / ".env"):
        if not path.exists():
            continue
        for line in path.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                os.environ.setdefault(k.strip(), v.strip().strip('"'))


_load_dotenv()


def settings() -> dict:
    """Read on every call, so switching provider or mode is an .env change, never a code change."""
    return {
        "mode": os.getenv("LLM_MODE", "cached"),
        "provider": os.getenv("LLM_PROVIDER", ""),
        "model": os.getenv("LLM_MODEL", ""),
        "local_model": os.getenv("LLM_LOCAL_MODEL", ""),
        # Any OpenAI-compatible /v1 server (LM Studio, llama.cpp, vLLM, Ollama's /v1, ...).
        "base_url": os.getenv("LLM_BASE_URL", "http://localhost:1234/v1").rstrip("/"),
        "api_key": os.getenv("LLM_API_KEY", "local"),
        "timeout_s": float(os.getenv("LLM_TIMEOUT_S", "3")),
        "max_tokens": int(os.getenv("LLM_MAX_TOKENS", "400")),
        "record": os.getenv("LLM_RECORD", "") == "1",
        # Thinking models (Gemini 3.x, o-series...): blank = don't send. "minimal" is rejected by Gemini, so not allowed.
        "reasoning_effort": os.getenv("LLM_REASONING_EFFORT", "").strip().lower()
        if os.getenv("LLM_REASONING_EFFORT", "").strip().lower() in REASONING_EFFORTS else "",
        "include_thoughts": os.getenv("LLM_INCLUDE_THOUGHTS", "") == "1",
    }


REASONING_EFFORTS = ("none", "low", "medium", "high")


@dataclass
class LLMResult:
    task: str
    text: str
    source: str          # live · local · cache · fallback
    provider: str = ""
    model: str = ""
    latency_ms: float = 0.0
    ok: bool = True
    error: str = ""
    prompt_chars: int = 0        # what we sent (system + messages); ~4 chars per token
    headers_ms: float = 0.0      # request sent → provider response headers
    first_token_ms: float = 0.0  # request sent → first content (or thought) chunk
    thoughts: str = ""           # model's thought summary, when the provider streams one


# ---------------------------------------------------------------- providers
def _call_live(prompt: str, s: dict) -> str:
    # A multi-provider client is allowed inside the wrapper only. Optional: the edge build doesn't need it.
    try:
        import litellm
    except ImportError as e:
        raise RuntimeError("LiteLLM not installed (uv add litellm) — live mode unavailable") from e
    if not s["provider"] or not s["model"]:
        raise RuntimeError("LLM_PROVIDER and LLM_MODEL must be set for live mode")
    resp = litellm.completion(
        model=f"{s['provider']}/{s['model']}",
        messages=[{"role": "system", "content": SYSTEM}, {"role": "user", "content": prompt}],
        max_tokens=s["max_tokens"], timeout=s["timeout_s"])
    return resp.choices[0].message.content.strip()


_discovered_model: dict[str, str] = {}


def set_endpoint(base_url: str, api_key: str | None, model: str, reasoning_effort: str = "",
                 include_thoughts: bool = False) -> None:
    """Switch the OpenAI-compatible server at runtime and save it to backend/.env (gitignored).
    api_key None keeps the current key."""
    updates = {"LLM_BASE_URL": base_url, "LLM_LOCAL_MODEL": model,
               "LLM_REASONING_EFFORT": reasoning_effort if reasoning_effort in REASONING_EFFORTS else "",
               "LLM_INCLUDE_THOUGHTS": "1" if include_thoughts else "0"}
    if api_key is not None:
        updates["LLM_API_KEY"] = api_key
    os.environ.update(updates)
    _discovered_model.clear()
    path = BACKEND / ".env"
    lines = path.read_text(encoding="utf-8").splitlines() if path.exists() else []
    for k, v in updates.items():
        i = next((n for n, ln in enumerate(lines) if ln.split("=", 1)[0].strip() == k), None)
        if i is None:
            lines.append(f"{k}={v}")
        else:
            lines[i] = f"{k}={v}"
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def local_model(s: dict) -> str:
    """LLM_LOCAL_MODEL, or the first model the OpenAI-compatible server reports at /models."""
    if s["local_model"]:
        return s["local_model"]
    if s["base_url"] not in _discovered_model:
        r = _client.get(f"{s['base_url']}/models", headers={"Authorization": f"Bearer {s['api_key']}"},
                        timeout=min(3.0, s["timeout_s"]))
        r.raise_for_status()
        models = r.json().get("data", [])
        if not models:
            raise RuntimeError(f"No models loaded at {s['base_url']}")
        _discovered_model[s["base_url"]] = models[0]["id"]
    return _discovered_model[s["base_url"]]


# One keep-alive client for every model call (no new connection + TLS handshake per request). Thread-safe.
_client = httpx.Client(timeout=30.0, limits=httpx.Limits(max_keepalive_connections=8))


def _thought_of(delta: dict) -> str:
    """Thought text in a streamed delta: reasoning_content (LM Studio, DeepSeek, vLLM), reasoning (OpenRouter),
    or Gemini's include_thoughts parts, which arrive as content flagged extra_content.google.thought."""
    if delta.get("reasoning_content") or delta.get("reasoning"):
        return str(delta.get("reasoning_content") or delta.get("reasoning"))
    if (delta.get("extra_content") or {}).get("google", {}).get("thought"):
        return str(delta.get("content") or "")
    return ""


def _stream_chat(messages: list[dict], s: dict, temperature: float) -> str:
    """OpenAI chat completions with stream: true. Reads the SSE body line by line as it arrives (never the whole
    body at once), stops at data: [DONE]. Timings go in s["timing"]; chunks go to s["on_chunk"](kind, text) if set,
    kind being "thought" or "content"."""
    body = {"model": local_model(s), "temperature": temperature, "max_tokens": s["max_tokens"], "stream": True,
            "messages": messages}
    if s["reasoning_effort"]:
        body["reasoning_effort"] = s["reasoning_effort"]
    if s["include_thoughts"]:       # Gemini's OpenAI-compatibility extension; other servers ignore it
        body["extra_body"] = {"google": {"thinking_config": {"include_thoughts": True}}}
    timing = s.setdefault("timing", {})
    timing["prompt_chars"] = sum(len(str(m.get("content", ""))) for m in messages)
    on_chunk = s.get("on_chunk") or (lambda kind, text: None)
    sent = time.perf_counter()
    ms = lambda: round((time.perf_counter() - sent) * 1000, 1)  # noqa: E731
    parts, thoughts = [], []
    with _client.stream("POST", f"{s['base_url']}/chat/completions", json=body, timeout=s["timeout_s"],
                        headers={"Authorization": f"Bearer {s['api_key']}", "Accept": "text/event-stream"}) as r:
        timing["headers_ms"] = ms()
        if r.status_code >= 400:
            raise RuntimeError(f"HTTP {r.status_code}: {r.read().decode(errors='replace')[:200]}")
        if not r.headers.get("content-type", "").startswith("text/event-stream"):
            # Server ignored stream: true and sent one JSON body; take it whole.
            r.read()
            msg = r.json()["choices"][0]["message"]
            timing["first_token_ms"] = ms()
            on_chunk("content", msg.get("content") or "")
            return (msg.get("content") or "").strip()
        for line in r.iter_lines():
            if not line.startswith("data:"):
                continue
            data = line[5:].strip()
            if data == "[DONE]":
                break
            try:
                choices = json.loads(data).get("choices") or []
            except json.JSONDecodeError:
                continue
            delta = (choices[0].get("delta") or {}) if choices else {}
            thought = _thought_of(delta)
            text = "" if thought and delta.get("content") == thought else (delta.get("content") or "")
            if (thought or text) and "first_token_ms" not in timing:
                timing["first_token_ms"] = ms()
            if thought:
                thoughts.append(thought)
                on_chunk("thought", thought)
            if text:
                parts.append(text)
                on_chunk("content", text)
    s["thoughts"] = "".join(thoughts)
    return "".join(parts).strip()


def _call_local(prompt: str, s: dict) -> str:
    """Standard OpenAI chat completions against LLM_BASE_URL (default http://localhost:1234/v1)."""
    return _stream_chat([{"role": "system", "content": s.get("system", SYSTEM)},
                         {"role": "user", "content": prompt}], s, 0.2)


PROVIDERS: dict[str, Callable[[str, dict], str]] = {"live": _call_live, "local": _call_local}

# ---------------------------------------------------------------- cache
_cache: dict | None = None
_cache_lock = threading.Lock()


def _key(task: str, inputs: dict) -> str:
    return hashlib.sha256(f"{task}|{json.dumps(inputs, sort_keys=True, default=str)}".encode()).hexdigest()[:32]


def _cache_load() -> dict:
    """Caller holds _cache_lock."""
    global _cache
    if _cache is None:
        _cache = json.loads(CACHE_PATH.read_text(encoding="utf-8")) if CACHE_PATH.exists() else {}
    return _cache


def _cache_get(key: str) -> str | None:
    with _cache_lock:
        hit = _cache_load().get(key)
    return hit["text"] if hit else None


def _cache_put(key: str, task: str, text: str, provider: str, model: str) -> None:
    with _cache_lock:
        _cache_load()[key] = {"task": task, "text": text, "provider": provider, "model": model}
        CACHE_PATH.parent.mkdir(parents=True, exist_ok=True)
        CACHE_PATH.write_text(json.dumps(_cache, indent=1, sort_keys=True), encoding="utf-8")


def reload_cache() -> None:
    global _cache
    with _cache_lock:
        _cache = None


# ---------------------------------------------------------------- logging
_logger: Callable[[dict], None] | None = None


def set_logger(fn: Callable[[dict], None] | None) -> None:
    """fn receives task, source, provider, model, latency_ms, ok, error. Never prompts or keys."""
    global _logger
    _logger = fn


# ---------------------------------------------------------------- run
_pool = ThreadPoolExecutor(max_workers=2, thread_name_prefix="llm")


def _finish(result: LLMResult, s: dict) -> LLMResult:
    """Copy the streaming timings onto the result and write the log row (never the prompt, text or key)."""
    timing = s.get("timing", {})
    result.prompt_chars = timing.get("prompt_chars", 0)
    result.headers_ms = timing.get("headers_ms", 0.0)
    result.first_token_ms = timing.get("first_token_ms", 0.0)
    result.thoughts = s.get("thoughts", "")
    if _logger:
        _logger({k: v for k, v in asdict(result).items() if k not in ("text", "thoughts")})
    return result


def run(task: str, inputs: dict, mode: str | None = None,
        on_chunk: Callable[[str, str], None] | None = None) -> LLMResult:
    """mode overrides LLM_MODE for this call (the LLM agent always asks the local model).
    on_chunk(kind, text) receives "thought" and "content" chunks as a local model streams them."""
    t = TASKS[task]
    s = settings()
    s["on_chunk"] = on_chunk
    if mode:
        s["mode"] = mode
    if t.timeout_s:
        s["timeout_s"] = max(s["timeout_s"], t.timeout_s)
    if t.max_tokens:
        s["max_tokens"] = max(s["max_tokens"], t.max_tokens)
    if t.system:
        s["system"] = t.system
    inputs = {k: inputs.get(k, "") for k in t.fields}
    key = _key(task, inputs)
    started = time.perf_counter()
    ms = lambda: round((time.perf_counter() - started) * 1000, 1)  # noqa: E731
    errors = []

    order = {"live": ["live", "cache"], "local": ["local", "cache"], "cached": ["cache"], "off": []}.get(s["mode"], ["cache"])
    result = None
    for step in order:
        try:
            if step == "cache":
                text = _cache_get(key)
                if text is not None:
                    result = LLMResult(task, text, "cache", latency_ms=ms())
                    break
                continue
            remaining = s["timeout_s"] - (time.perf_counter() - started)
            fut = _pool.submit(PROVIDERS[step], t.prompt.format(**inputs), s)
            text = fut.result(timeout=max(0.05, remaining))
            if not text:
                raise RuntimeError("empty answer")
            provider, model = (s["provider"], s["model"]) if step == "live" else (s["base_url"], local_model(s))
            result = LLMResult(task, text, step, provider, model, ms())
            if s["record"]:
                _cache_put(key, task, text, provider, model)
            break
        except FutureTimeout:
            errors.append(f"{step}: timeout after {s['timeout_s']} s")
        except Exception as e:  # any provider failure drops to the next step
            errors.append(f"{step}: {e}")
    if result is None:
        try:
            result = LLMResult(task, t.fallback(inputs), "fallback", latency_ms=ms())
        except Exception as e:
            result = LLMResult(task, "", "fallback", latency_ms=ms(), ok=False, error=str(e))
    if errors and not result.error:
        result.error = "; ".join(errors)
    return _finish(result, s)


def stream(task: str, inputs: dict) -> Iterator[tuple[str, object]]:
    """run() as events, for showing text as it arrives: ("thinking", None) at once, then ("thought", text) and
    ("content", text) chunks, then ("done", result). Cache and fallback text arrive as one content chunk."""
    q: queue.Queue = queue.Queue()
    streamed = []

    def chunk(kind: str, text: str) -> None:
        streamed.append(kind)
        q.put((kind, text))

    def work() -> None:
        try:
            q.put(("done", run(task, inputs, on_chunk=chunk)))
        except Exception as e:  # run() never raises, but the stream must always end
            q.put(("done", LLMResult(task, "", "fallback", ok=False, error=str(e))))

    yield "thinking", None
    threading.Thread(target=work, daemon=True).start()
    while True:
        kind, val = q.get()
        if kind == "done":
            # A provider that streamed then failed, or a cache/fallback answer: send the final text whole.
            if val.source not in ("local", "live") or "content" not in streamed:
                yield "replace", val.text
            yield "done", val
            return
        yield kind, val


def run_background(task: str, inputs: dict, on_done: Callable[[LLMResult], None]) -> None:
    """Instant modes run inline. Network/model modes run off the governance loop so decisions never wait on AI."""
    if settings()["mode"] in ("cached", "off"):
        on_done(run(task, inputs))
    else:
        threading.Thread(target=lambda: on_done(run(task, inputs)), daemon=True).start()


# ---------------------------------------------------------------- multi-turn (agent tool loop)
def _call_local_chat(messages: list[dict], s: dict) -> str:
    """OpenAI chat completions with a full message list (system, user, assistant, tool results)."""
    return _stream_chat(messages, s, 0.1)


PROVIDERS["local_chat"] = _call_local_chat


def chat(messages: list[dict], task: str = "agent_turn", timeout_s: float = 45.0, max_tokens: int = 700) -> LLMResult:
    """One turn of a conversation with the local model. No cache and no fallback text: an agent turn is either a
    real model answer or a failure, never something we made up."""
    s = settings()
    s["timeout_s"] = max(s["timeout_s"], timeout_s)
    s["max_tokens"] = max(s["max_tokens"], max_tokens)
    started = time.perf_counter()
    try:
        fut = _pool.submit(PROVIDERS["local_chat"], messages, s)
        text = fut.result(timeout=s["timeout_s"])
        result = LLMResult(task, text, "local", s["base_url"], local_model(s), round((time.perf_counter() - started) * 1000, 1))
    except FutureTimeout:
        result = LLMResult(task, "", "failed", s["base_url"], s["local_model"], round((time.perf_counter() - started) * 1000, 1),
                           ok=False, error=f"timeout after {s['timeout_s']} s")
    except Exception as e:
        result = LLMResult(task, "", "failed", s["base_url"], s["local_model"], round((time.perf_counter() - started) * 1000, 1),
                           ok=False, error=str(e)[:200])
    return _finish(result, s)
