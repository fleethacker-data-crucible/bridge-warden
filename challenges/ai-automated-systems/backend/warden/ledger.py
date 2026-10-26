"""Append-only, hash-chained audit ledger (SQLite).

Each row stores hash = sha256(prev_hash + payload), so editing or deleting any
row breaks every hash after it. verify() walks the chain to prove it's intact.
"""
from __future__ import annotations

import hashlib
import json
import sqlite3
import threading
import time
from pathlib import Path

GENESIS = "0" * 64


class Ledger:
    def __init__(self, path: Path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self._db = sqlite3.connect(path, check_same_thread=False)
        self._lock = threading.Lock()
        self._db.execute("""CREATE TABLE IF NOT EXISTS ledger (
            seq INTEGER PRIMARY KEY, ts REAL, type TEXT, actor TEXT,
            payload TEXT, prev_hash TEXT, hash TEXT)""")
        self._db.commit()

    def close(self) -> None:
        self._db.close()

    def reset(self) -> None:
        with self._lock:
            self._db.execute("DELETE FROM ledger")
            self._db.commit()

    def _last_hash(self) -> str:
        row = self._db.execute("SELECT hash FROM ledger ORDER BY seq DESC LIMIT 1").fetchone()
        return row[0] if row else GENESIS

    @staticmethod
    def _digest(prev_hash: str, ts: float, type_: str, actor: str, payload: str) -> str:
        return hashlib.sha256(f"{prev_hash}|{ts:.6f}|{type_}|{actor}|{payload}".encode()).hexdigest()

    def append(self, type_: str, actor: str, payload: dict) -> dict:
        ts = time.time()
        body = json.dumps(payload, sort_keys=True, default=str)
        with self._lock:
            prev = self._last_hash()
            h = self._digest(prev, ts, type_, actor, body)
            cur = self._db.execute(
                "INSERT INTO ledger (ts, type, actor, payload, prev_hash, hash) VALUES (?,?,?,?,?,?)",
                (ts, type_, actor, body, prev, h))
            self._db.commit()
            seq = cur.lastrowid
        return {"seq": seq, "ts": ts, "type": type_, "actor": actor, "payload": payload, "hash": h}

    def rows(self, limit: int = 200, type_: str | None = None) -> list[dict]:
        q, args = "SELECT seq, ts, type, actor, payload, hash FROM ledger", []
        if type_:
            q += " WHERE type = ?"
            args.append(type_)
        q += " ORDER BY seq DESC LIMIT ?"
        args.append(limit)
        with self._lock:
            rows = self._db.execute(q, args).fetchall()
        return [{"seq": s, "ts": t, "type": ty, "actor": a, "payload": json.loads(p), "hash": h}
                for s, t, ty, a, p, h in reversed(rows)]

    def verify(self) -> dict:
        with self._lock:
            rows = self._db.execute(
                "SELECT seq, ts, type, actor, payload, prev_hash, hash FROM ledger ORDER BY seq").fetchall()
        prev = GENESIS
        for seq, ts, ty, actor, payload, prev_hash, h in rows:
            if prev_hash != prev or self._digest(prev, ts, ty, actor, payload) != h:
                return {"valid": False, "rows": len(rows), "broken_at": seq}
            prev = h
        return {"valid": True, "rows": len(rows), "broken_at": None}
