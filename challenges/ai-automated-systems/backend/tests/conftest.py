"""Tests never call a real model: the LLM agent is switched off unless a test opts in."""
import os
import tempfile

os.environ.setdefault("LLM_AGENT", "off")
os.environ.setdefault("WARDEN_DATA", tempfile.mkdtemp(prefix="warden-test-"))   # operator feedback stays out of data/
