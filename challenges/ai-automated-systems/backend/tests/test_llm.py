"""Required LLM wrapper tests (docs/llm-wrapper.md, section 6)."""
from __future__ import annotations

import json
import time

import pytest

from warden import llm
from warden.llm_tasks import TASKS

SAMPLE = {
    "summarize_conflict": {"kind": "resource", "resource": "main_engine", "winner_agent": "col-01",
                           "winner_objective": "Keep CPA", "winner_value": "speed 8 kn", "winner_priority": 90,
                           "loser_agent": "nav-01", "loser_objective": "Keep ETA", "loser_value": "speed 16 kn",
                           "loser_priority": 40},
    "summarize_escalation": {"agent": "wx-01", "value": "heading 055°", "objective": "Avoid heavy weather",
                             "why": "turn above 30°", "rationale": "Wind 38 kts on the beam."},
    "explain_intent": {"agent": "nav-01", "action": "set_speed", "value": "speed 16 kn", "stages": "constraints fail",
                       "status": "rejected", "reasons": "Predicted CPA 0.4 nm", "decided_by": "warden"},
    "agent_decide": {"role": "Navigation", "state": '{"ownship": {"speed": 16}}', "tools": "set_speed: {\"speed_kn\": number}"},
}


@pytest.fixture(autouse=True)
def isolated(tmp_path, monkeypatch):
    """Fresh cache file, no logger, default settings, real providers restored after each test."""
    monkeypatch.setattr(llm, "CACHE_PATH", tmp_path / "llm_cache.json")
    llm.reload_cache()
    for var in ("LLM_MODE", "LLM_PROVIDER", "LLM_MODEL", "LLM_LOCAL_MODEL", "LLM_TIMEOUT_S", "LLM_RECORD",
                "LLM_REASONING_EFFORT", "LLM_INCLUDE_THOUGHTS", "LLM_BASE_URL"):
        monkeypatch.delenv(var, raising=False)
    monkeypatch.setattr(llm, "PROVIDERS", dict(llm.PROVIDERS))
    llm.set_logger(None)
    yield
    llm.reload_cache()


def seed_cache(task: str, text: str) -> None:
    key = llm._key(task, {k: SAMPLE[task].get(k, "") for k in TASKS[task].fields})
    llm.CACHE_PATH.write_text(json.dumps({key: {"task": task, "text": text}}), encoding="utf-8")
    llm.reload_cache()


def test_every_task_returns_fallback_when_off(monkeypatch):
    monkeypatch.setenv("LLM_MODE", "off")
    for name in TASKS:
        r = llm.run(name, SAMPLE[name])
        assert r.ok and r.source == "fallback" and r.text


def test_timeout_falls_through_to_cache_then_fallback_within_limit(monkeypatch):
    monkeypatch.setenv("LLM_MODE", "live")
    monkeypatch.setenv("LLM_TIMEOUT_S", "0.3")
    monkeypatch.setitem(llm.PROVIDERS, "live", lambda prompt, s: time.sleep(2) or "too late")

    seed_cache("summarize_conflict", "cached answer")
    t0 = time.perf_counter()
    r = llm.run("summarize_conflict", SAMPLE["summarize_conflict"])
    assert r.source == "cache" and r.text == "cached answer"
    assert time.perf_counter() - t0 < 0.6
    assert "timeout" in r.error

    t0 = time.perf_counter()
    r = llm.run("summarize_escalation", SAMPLE["summarize_escalation"])   # nothing cached for this task
    assert r.source == "fallback" and r.ok
    assert time.perf_counter() - t0 < 0.6


def test_cached_mode_is_deterministic(monkeypatch):
    monkeypatch.setenv("LLM_MODE", "cached")
    seed_cache("summarize_conflict", "same every time")
    a = llm.run("summarize_conflict", SAMPLE["summarize_conflict"])
    b = llm.run("summarize_conflict", SAMPLE["summarize_conflict"])
    assert a.text == b.text == "same every time" and a.source == b.source == "cache"
    # A cache miss is deterministic too: same rule text both times.
    c = llm.run("explain_intent", SAMPLE["explain_intent"])
    d = llm.run("explain_intent", SAMPLE["explain_intent"])
    assert c.text == d.text and c.source == "fallback"


@pytest.mark.parametrize("provider,model", [("openai", "gpt-x"), ("xai", "grok-x"), ("anthropic", "claude-x")])
def test_switching_provider_needs_no_code_change(monkeypatch, provider, model):
    monkeypatch.setenv("LLM_MODE", "live")
    monkeypatch.setenv("LLM_PROVIDER", provider)
    monkeypatch.setenv("LLM_MODEL", model)
    monkeypatch.setitem(llm.PROVIDERS, "live", lambda prompt, s: f"answer from {s['provider']}/{s['model']}")
    r = llm.run("summarize_conflict", SAMPLE["summarize_conflict"])
    assert r.source == "live" and r.provider == provider and r.model == model
    assert r.text == f"answer from {provider}/{model}"


def test_every_call_is_logged_without_text_or_keys(monkeypatch):
    monkeypatch.setenv("LLM_MODE", "off")
    seen = []
    llm.set_logger(seen.append)
    llm.run("summarize_conflict", SAMPLE["summarize_conflict"])
    assert len(seen) == 1
    assert {"task", "source", "provider", "model", "latency_ms", "ok"} <= seen[0].keys()
    assert "text" not in seen[0]


def test_record_mode_fills_the_cache(monkeypatch):
    monkeypatch.setenv("LLM_MODE", "live")
    monkeypatch.setenv("LLM_PROVIDER", "openai")
    monkeypatch.setenv("LLM_MODEL", "gpt-x")
    monkeypatch.setenv("LLM_RECORD", "1")
    monkeypatch.setitem(llm.PROVIDERS, "live", lambda prompt, s: "recorded answer")
    llm.run("summarize_conflict", SAMPLE["summarize_conflict"])          # live answer is written to the cache
    monkeypatch.setenv("LLM_MODE", "cached")
    llm.reload_cache()
    r = llm.run("summarize_conflict", SAMPLE["summarize_conflict"])
    assert r.source == "cache" and r.text == "recorded answer"


# ---------------------------------------------------------------- streaming (docs/things-to-check/streaming-latency-checklist.md)
def fake_provider(monkeypatch, chunks: list[dict], seen: list):
    """Swap the shared client for one whose /chat/completions streams `chunks` as SSE."""
    import httpx

    def handler(req):
        if req.url.path.endswith("/models"):
            return httpx.Response(200, json={"data": [{"id": "fake-model"}]})
        seen.append(json.loads(req.content))
        sse = "".join(f"data: {json.dumps({'choices': [{'delta': c}]})}\n\n" for c in chunks) + "data: [DONE]\n\n"
        return httpx.Response(200, text=sse, headers={"content-type": "text/event-stream"})
    monkeypatch.setattr(llm, "_client", httpx.Client(transport=httpx.MockTransport(handler)))
    llm._discovered_model.clear()


def test_local_call_streams_thoughts_and_content_with_timings(monkeypatch):
    seen = []
    fake_provider(monkeypatch, [{"reasoning_content": "weigh CPA. "}, {"content": "Rejected: "},
                                {"content": "CPA too close."}], seen)
    monkeypatch.setenv("LLM_MODE", "local")
    monkeypatch.setenv("LLM_REASONING_EFFORT", "low")
    chunks = []
    r = llm.run("explain_intent", SAMPLE["explain_intent"], on_chunk=lambda k, t: chunks.append((k, t)))
    assert r.source == "local" and r.text == "Rejected: CPA too close." and r.thoughts == "weigh CPA. "
    assert chunks == [("thought", "weigh CPA. "), ("content", "Rejected: "), ("content", "CPA too close.")]
    assert seen[0]["stream"] is True and seen[0]["reasoning_effort"] == "low" and "extra_body" not in seen[0]
    assert r.prompt_chars > 0 and r.first_token_ms >= r.headers_ms >= 0


def test_invalid_reasoning_effort_is_not_sent(monkeypatch):
    seen = []
    fake_provider(monkeypatch, [{"content": "ok"}], seen)
    monkeypatch.setenv("LLM_MODE", "local")
    monkeypatch.setenv("LLM_REASONING_EFFORT", "minimal")      # Gemini answers 400 to this
    monkeypatch.setenv("LLM_INCLUDE_THOUGHTS", "1")
    llm.run("explain_intent", SAMPLE["explain_intent"])
    assert "reasoning_effort" not in seen[0]
    assert seen[0]["extra_body"]["google"]["thinking_config"]["include_thoughts"] is True


def test_gemini_thought_parts_are_kept_out_of_the_answer(monkeypatch):
    fake_provider(monkeypatch, [{"content": "plan it", "extra_content": {"google": {"thought": True}}},
                                {"content": "Answer."}], [])
    monkeypatch.setenv("LLM_MODE", "local")
    r = llm.run("explain_intent", SAMPLE["explain_intent"])
    assert r.text == "Answer." and r.thoughts == "plan it"


def test_stream_events_end_with_done_and_fallback_arrives_whole(monkeypatch):
    monkeypatch.setenv("LLM_MODE", "off")
    events = list(llm.stream("explain_intent", SAMPLE["explain_intent"]))
    kinds = [k for k, _ in events]
    assert kinds[0] == "thinking" and kinds[-2:] == ["replace", "done"]
    assert events[-1][1].source == "fallback" and events[-2][1] == events[-1][1].text


def test_log_row_has_timings_but_no_text_or_thoughts(monkeypatch):
    fake_provider(monkeypatch, [{"reasoning": "secret-ish"}, {"content": "hi"}], [])
    monkeypatch.setenv("LLM_MODE", "local")
    rows = []
    llm.set_logger(rows.append)
    llm.run("explain_intent", SAMPLE["explain_intent"])
    assert "text" not in rows[0] and "thoughts" not in rows[0] and rows[0]["prompt_chars"] > 0


def test_dotenv_strips_inline_comments(tmp_path, monkeypatch):
    """backend/.env copied from .env.example keeps its inline comments; values must not include them."""
    (tmp_path / "backend").mkdir()
    (tmp_path / "backend" / ".env").write_text('BW_T_NUM=45   # seconds\nBW_T_URL=http://x/v1\t# tab\nBW_T_Q="a # b"\n',
                                               encoding="utf-8")
    monkeypatch.setattr(llm, "BACKEND", tmp_path / "backend")
    for k in ("BW_T_NUM", "BW_T_URL", "BW_T_Q"):
        monkeypatch.delenv(k, raising=False)
    llm._load_dotenv()
    import os
    assert (os.environ["BW_T_NUM"], os.environ["BW_T_URL"], os.environ["BW_T_Q"]) == ("45", "http://x/v1", "a # b")
    for k in ("BW_T_NUM", "BW_T_URL", "BW_T_Q"):
        monkeypatch.delenv(k)
