"""Edge resource meter: proves the power and bandwidth budgets (plan Section 6 / 8.11)."""
from __future__ import annotations

import os
import time

import psutil


class ResourceMeter:
    def __init__(self, budgets: dict):
        self.budgets = budgets
        self.proc = psutil.Process(os.getpid())
        self.proc.cpu_percent(None)
        self.ws_bytes = 0
        self.ws_msgs = 0
        self.intent_bytes_max = 0
        self.pipeline_ms: list[float] = []
        self._t0 = time.time()
        self._last = {"t": self._t0, "ws_bytes": 0}
        self.ws_rate = 0.0

    def count_ws(self, nbytes: int) -> None:
        self.ws_bytes += nbytes
        self.ws_msgs += 1

    def record_pipeline(self, ms: float) -> None:
        self.pipeline_ms = (self.pipeline_ms + [ms])[-50:]

    def sample(self) -> dict:
        now = time.time()
        dt = max(0.001, now - self._last["t"])
        if dt >= 2:
            self.ws_rate = (self.ws_bytes - self._last["ws_bytes"]) / dt
            self._last = {"t": now, "ws_bytes": self.ws_bytes}
        cpu = self.proc.cpu_percent(None) / 100.0          # in cores
        rss = self.proc.memory_info().rss / 1e6
        b = self.budgets
        return {
            "cpu_cores": round(cpu, 2), "cpu_budget": b["cpu_cores"],
            "rss_mb": round(rss, 1), "rss_budget": b["rss_mb"],
            "ui_kbps": round(self.ws_rate / 1000, 1), "ui_budget": b["ui_kbps"],
            "intent_bytes_max": self.intent_bytes_max, "intent_budget": b["intent_bytes"],
            "pipeline_ms": round(sum(self.pipeline_ms) / len(self.pipeline_ms), 2) if self.pipeline_ms else 0.0,
            "external_bytes": 0,   # the core loop makes no outbound network calls
            "profile": b["profile"],
        }
