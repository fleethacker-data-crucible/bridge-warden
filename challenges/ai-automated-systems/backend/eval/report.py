"""Build eval/REPORT.md from eval/runs/<round>/*.summary.json: what a watch team would do vs what the local LLM did.

    uv run python -m eval.report r1 r2
"""
from __future__ import annotations

import json
import statistics
import sys
from pathlib import Path

EVAL = Path(__file__).resolve().parent
ACT = {"engage_target": "fire", "activate_defense": "weapons free", "set_gq": "GQ", "set_emcon": "EMCON",
       "set_heading": "course", "set_speed": "speed", "dispatch_team": "send team", "close_breaker": "close",
       "open_breaker": "open"}


def load(rnd: str) -> dict:
    return {p.name.split(".")[0]: json.loads(p.read_text(encoding="utf-8"))
            for p in sorted((EVAL / "runs" / rnd).glob("*.summary.json"))}


def stats(runs: dict) -> dict:
    exp = [e for r in runs.values() for e in r["expectations"] if not e["result"].startswith("n/a")]
    on = [e for e in exp if e["result"] == "on time"]
    late = [e for e in exp if e["result"] == "LATE"]
    react = [e["reaction_s"] for e in exp if "reaction_s" in e]
    lat = [r["latency_s"]["mean"] for r in runs.values() if r["latency_s"]["mean"]]
    return {"runs": len(runs), "expectations": len(exp), "on_time": len(on), "late": len(late),
            "missed": len(exp) - len(on) - len(late),
            "reaction_median_s": round(statistics.median(react), 1) if react else None,
            "reaction_mean_s": round(statistics.mean(react), 1) if react else None,
            "model_latency_mean_s": round(statistics.mean(lat), 1) if lat else None,
            "model_latency_max_s": max((r["latency_s"]["max"] or 0) for r in runs.values()) if runs else None,
            "answers": sum(r["model_answers"] for r in runs.values()),
            "overreactions": sum(len(r["overreactions"]) for r in runs.values()),
            "hits": sum(r["outcome"]["hits"] for r in runs.values()),
            "blocked": sum(sum(r["blocked_by_warden"].values()) for r in runs.values()),
            "executed": sum(r["intents"].get("executed", 0) for r in runs.values()),
            "alternatives": sum(r.get("alternatives_offered", 0) for r in runs.values()),
            "risk_filled": sum(r.get("risk_filled", 0) for r in runs.values()),
            "intents": sum(r.get("intents_total", 0) for r in runs.values())}


def did(r: dict) -> str:
    rows, seen = [], set()
    for a in r["actions_timeline"]:
        key = (a["agent"], a["action"], json.dumps(a["params"], sort_keys=True), a["status"])
        if key in seen:
            continue
        seen.add(key)
        p = a["params"]
        val = (p.get("target") or p.get("heading") or p.get("speed_kn") or ",".join(p.get("systems", []))
               or p.get("team") or p.get("breaker") or ("on" if p.get("on") else ""))
        mark = "✓" if a["status"] == "executed" else "✗" if a["status"] == "rejected" else "?"
        why = f" — blocked: {a['why_blocked'][:70]}" if a["status"] == "rejected" and a["why_blocked"] else ""
        rows.append(f"{a['at']:.0f}s {mark} {a['agent']} {ACT.get(a['action'], a['action'])} {val}{why}")
    return "<br>".join(rows[:14]) + ("<br>…" if len(rows) > 14 else "") if rows else "(no actions)"


def main(rounds: list[str]):
    data = {r: load(r) for r in rounds}
    st = {r: stats(d) for r, d in data.items()}
    model = next((x["model"] for d in data.values() for x in d.values() if x.get("model")), "local model")
    L = [f"# Local LLM evaluation: watch team doctrine vs `{model}`", "",
         "Every run: real time (1x), all three COPs live, **all agents set to act** (auto-act: no approval wait; Warden "
         "still checks every action). The simulation engine runs the world; the LLM agents only see the COP and "
         "control the ship through Warden. Numbers are illustrative (unclassified). Full logs: `eval/runs/<round>/*.jsonl`.",
         "", "**Reaction time** = when the correct action was proposed − when the trigger appeared on the COP (e.g. first "
         "missile detected). It includes waiting in the model queue plus the model's own thinking (**model latency**).",
         "", "## Scorecard", "", "| | " + " | ".join(rounds) + " |", "|---|" + "---|" * len(rounds)]
    rows = [("Runs", "runs"), ("Expected actions (scored)", "expectations"), ("On time", "on_time"), ("Late", "late"),
            ("Missed", "missed"), ("Reaction time, median (s)", "reaction_median_s"),
            ("Reaction time, mean (s)", "reaction_mean_s"), ("Model latency per answer, mean (s)", "model_latency_mean_s"),
            ("Model latency, worst (s)", "model_latency_max_s"), ("Model answers", "answers"),
            ("Actions executed", "executed"), ("Actions blocked by Warden", "blocked"),
            ("Overreactions (a good watch would not)", "overreactions"), ("Hits taken (missile + torpedo)", "hits"),
            ("Actions with a risk assessment", "risk_filled"), ("Two-option decisions offered", "alternatives")]
    for label, k in rows:
        L.append(f"| {label} | " + " | ".join(str(st[r][k]) for r in rounds) + " |")
    L += ["", "## Scenario by scenario", ""]
    ids = sorted({i for d in data.values() for i in d})
    for sid in ids:
        first = next(d[sid] for d in data.values() if sid in d)
        L += [f"### {sid}: {first['title']}", "", f"**What I would do:** {first['doctrine']}", ""]
        L.append("| Expected | " + " | ".join(rounds) + " |")
        L.append("|---|" + "---|" * len(rounds))
        names = [e["expect"] for e in first["expectations"]]
        for n in names:
            cells = []
            for r in rounds:
                e = next((x for x in data[r].get(sid, {}).get("expectations", []) if x["expect"] == n), None)
                if not e:
                    cells.append("–")
                elif "reaction_s" in e:
                    cells.append(f"{e['result']} {e['reaction_s']} s (limit {e['deadline_s']:.0f}) · {e['agent']}")
                else:
                    cells.append(e["result"])
            L.append(f"| {n} | " + " | ".join(cells) + " |")
        L.append("| Outcome | " + " | ".join(
            (lambda o, x: f"hits {o['hits']} · overreactions {len(x['overreactions'])} · blocked "
                          f"{sum(x['blocked_by_warden'].values())} · latency {x['latency_s']['mean']} s avg")(
                data[r][sid]["outcome"], data[r][sid]) if sid in data[r] else "–" for r in rounds) + " |")
        L += [""]
        for r in rounds:
            if sid in data[r]:
                L += [f"<details><summary>What the local LLM did ({r})</summary>", "", did(data[r][sid]), "", "</details>", ""]
    lessons = EVAL.parent / "skills" / "lessons"
    L += ["## What the agents learned between rounds", ""]
    for p in sorted(lessons.glob("*.md")):
        if p.name != "README.md":
            L += [f"**{p.stem}**", "", p.read_text(encoding="utf-8").strip(), ""]
    (EVAL / "REPORT.md").write_text("\n".join(L), encoding="utf-8")
    print(json.dumps(st, indent=1))


if __name__ == "__main__":
    main(sys.argv[1:] or ["r1"])
