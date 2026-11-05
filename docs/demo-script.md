# Bridge Warden — Demo Script (~7 min, presenter-driven)

**How it works (say it once, early):** every picture (Navigation, Ship Systems, Damage Control) has **C# lookouts** that watch the ship and, when something needs a decision, report it in plain English with ready-made options and each option's predicted result. **One local LLM per picture** (the Tactical Action Officer, the Engineering Officer of the Watch, the Damage Control Officer) picks an option, usually in about a second. **Bridge Warden checks every order before the ship does it.** If the model is slow or down, urgent alerts get the lookout's doctrine option as a **REFLEX**, marked as such, and Warden checks that too.

**How it runs:** real time (1×). The program opens **paused**: nothing moves until **Start Operations**. Model: LM Studio on `127.0.0.1:1234` (set in `backend\.env` or the AI Model Server box). Presenter controls: **OPSNORM**, **Auto Firefight**, **Manual / Auto Injection**, and **Inject Issues…** (one popup, a column per picture plus **Warden Tests**).

**Setup:** run `.\run.ps1`. It opens **Warden Control** (left) and the **main app** (right). Main app on **Overview**. Click **Reset All** (type *I Acknowledge*); this also clears what the deciders learned last run. Leave the agents on **ASK** for the first beats so the human approvals show.

---

## 0:00 — The problem (30 s)
**Say:** "Future ships will run many AI systems at once. Each works alone; together they fight, they're slow, or one gets compromised. Bridge Warden is not another agent. It's the layer every AI order must pass through. **No AI touches the ship directly.**"

## 0:30 — What we're looking at (60 s)
**Main app, left panel, "Chain of command on this picture":** the **Tactical Action Officer** (the LLM) and under it six lookouts: Collision Avoidance, Weather Routing, Navigation, Self-Defense & EMCON, Targeting & Launch, Undersea Warfare. Each lookout shows its current report or *all clear*.
**Say:** "The lookouts are fast, deterministic code: they see everything and do the math. The language model does what it's good at: it reads the reports and chooses. It can only choose among real options. It cannot invent an order. And whatever it chooses, Warden checks first."
**Warden Control:** agent tiles, **SAFE-STOP** (hold 2 s), the live log, the edge budget (one small box, no network).
**Do:** **Start Operations**.

## 1:30 — Crossing contact (60 s)
**Do:** Inject Issues → **Crossing Contact** (or **Close Crossing**).
**See:** a contact on the starboard bow. Collision Avoidance reports: *passes 0.0 nm in N minutes*, with options *turn right 30° → 1.3 nm*, *turn left*, *slow to 8 kn*, *hold*. The TAO answers in about a second: *1a, turn right to give way (Rules of the Road)*.
**Say:** "Lookout reports, model decides, Warden checks the closest point of approach, the rudder limits and the agent's authority, then the ship turns."
**Show:** Intents → the row → **Decision Provenance**: *Decided by tao-01*, the lookout's report, the option chosen, the model's reason, the SHA-256 of the model's reply. "Proof the decision came from the model, and exactly what it saw."

## 2:30 — Threat: hostile and missiles (60 s)
**Do:** **Hostile Surface**, then when it is classified, **Missiles (2)**.
**See:** Self-Defense reports the SUSPECT/HOSTILE track → the TAO picks *go quiet and man battle stations* (EMCON + General Quarters). Inbound missiles → *fight and evade*: SM-2, ready defenses, GQ, hard turn at 30 kn.
**Say:** "Weapons are never automatic. Warden flags every weapons order **WEAPONS_NEED_HUMAN**: the AI recommends, a human authorizes." **Approve** the decision on the COP.

## 3:30 — Torpedoes (30 s)
**Do:** **Torpedoes Close (2)**.
**See:** Undersea Warfare: *TORPEDO in the water, running away means heading N; at 30 kn it ...* → *run at 30 kn, stream Nixie*. The big turn needs the watch's OK.
**Say:** "Run and decoy, in one decision, in about a second."

## 4:00 — Ship Systems: power and patch cables (60 s)
**Do:** switch to **Ship Systems**. Inject **Aft Power Loss (Patch Cables)**.
**See:** the Electrical Plant lookout: *Steering has NO POWER; NO breaker re-route can restore it*. Options: a **patch cable** (casualty power) from a live switchboard, rigged by the nearest repair team, with the generator load it leaves. The EOOW picks it; Warden asks a human (it sends people: **PERSONNEL_NEED_HUMAN**). The team walks there, rigs for 30 s, steering comes back. Then the shafts.
**Say:** "Every option was run through the plant model first: what it restores, and no generator over 100%. The model chooses; it never guesses at a lineup."
**Also good:** **Generator 1 Trip** (bring the standby generator on line), **Switchboard 2 Fault**, **Load Center 3 Loss** (breaker re-routes).

## 5:00 — Warden Tests: a compromised AI commander (45 s)
**Do:** **Warden Tests → Compromised AI Commander**, then inject **Torpedoes Close (2)**.
**See:** the TAO's model is hijacked. It ignores its lookouts and tries to steer, speed up and fire on its own. Red log lines: **TOOL_NOT_GRANTED** (a decider holds no tools), then **QUARANTINED tao-01**. Meanwhile Undersea Warfare's urgent alert still gets its doctrine option by **REFLEX**: the ship runs from the torpedo anyway.
**Say:** "Even the AI in charge can't touch the ship. Warden contained it in seconds, and the ship kept defending itself."
**Also:** **Rogue Lookout Agent** (a lookout hijacked the same way: rudder it may not use, speed past its limits → quarantined).

## 5:45 — Warden Tests: the model goes dark (30 s)
**Do:** **AI Model Outage (60 s)**, then any urgent inject (missiles, torpedoes, close crossing).
**See:** the decider row shows *model error (retrying)*; the urgent order arrives after 6 s marked **REFLEX** in its provenance, and Warden still checks it.
**Say:** "No model, no problem: the ship degrades to doctrine, visibly, and still under governance."

## 6:15 — System check, SAFE-STOP, audit (45 s)
**Do:** **System Check Now** → **Confirm Operations**. Hold **SAFE-STOP** 2 s → every agent stops, safe speed; hold again to resume. **Verify Ledger** → chain valid.
**Say:** "The operator must stay in the loop or it stops itself. One gesture stops every AI aboard. Every report, decision, block and override is in a tamper-evident log."

## 7:00 — Close (15 s)
**Say:** "Fast where it must be fast, smart where judgment matters, and governed everywhere. One small box, no network, configured, not coded."

---

## Injection cheat sheet
| Button | Who reports | What the decider usually picks | Warden's part |
|---|---|---|---|
| Crossing / Close Crossing / Head-on / Fishing | Collision Avoidance | turn right (or slow) | CPA check, rudder limits, authority |
| Overtaking | Collision Avoidance (once inside 30 min) | speed or course change | same |
| Squall Line / Gale | Weather Routing | turn away, or slow to 10 kn | never steer into a heavy cell; Navigation stays off the rudder until clear |
| Dense Fog | Weather Routing | safe speed 10 kn (Rule 19) | warranted only in restricted visibility |
| Hostile Surface / Sub Detected | Self-Defense, Undersea | EMCON + GQ | never fire at SUSPECT |
| Hostile Sub | Undersea | Nixie, GQ, ASROC | weapons need a human |
| Missiles / Salvo / Aircraft | Self-Defense | fight and evade | weapons need a human; ECM breaking EMCON flagged |
| Torpedo Spread / Torpedoes Close | Undersea | run at 30 kn + Nixie | big turn needs a human |
| Engine Overheat | Engine Health | the speed that cools it | warranted only when hot |
| Generator / Switchboard / Load Center faults, Aft Power Loss | Electrical Plant | re-route, standby generator, or patch cable | personnel need a human; no lineup that drops power |
| Fires / Floods | Damage Control | nearest team + GQ | personnel need a human |
| Loss of Shaft / Steering / Radar / CHW / CIWS | (damaged: no re-route possible) | Damage Control repairs it | — |
| Compromised AI Commander | — | (hijacked model orders the ship) | TOOL_NOT_GRANTED → quarantine; lookouts keep the ship safe by reflex |
| Rogue Lookout Agent | — | (hijacked lookout) | authority / limit / quota blocks → quarantine |
| AI Model Outage (60 s) | all lookouts | nothing (model down) | REFLEX orders, marked and checked |

## If something breaks
- **The decider row says "model error":** the model server is down or busy. Urgent alerts still get reflexes. Check LM Studio, or the AI Model Server box in Warden Control.
- **A decision timed out mid-talk:** that's a feature. "No answer → safe default." Inject again.
- **App crash:** close the windows, rerun `run.ps1`, Reset.
- **"Is the AI deciding?"** "Yes, the language model chooses, but only among options the lookouts worked out, and every order it picks is checked by Warden before the ship moves. It can't invent an order, and a hijacked one gets contained."
- **Hands-off run:** start with `$env:SCENARIO='full_demo'` for the scripted storyline.
