# Bridge Warden — Demo Script (~7 min, presenter-driven)

**How it runs:** real time (**1×**). The program opens in **normal conditions, paused**: nothing moves and no AI runs until you press **Start Operations**. **Every agent decision comes from the local LLM** (Gemma 4 E4B on `localhost:1234`); Python only simulates the world and enforces Warden's rules, and humans approve. Presenter controls: **Set Normal Conditions** (clear everything, any time), **Auto Firefight** (two hostiles, two salvos, hands-off), and **Manual / Auto Injection** (auto = a random issue every 60–120 s). Each COP only shows its own banners and decisions; banners appear only for decisions and critical alerts.

**Setup:** run `.\run.ps1`. It opens **Warden Control** (red logo, left) and the **main app** (blue logo, right) as their own windows. Put the main app on **Overview**. Click **Reset All** (type *I Acknowledge*).

---

## 0:00 — The problem (30 s)
**Say:** "Future ships will run many AI systems at once: navigation, collision avoidance, engine health, weather, energy. Each works fine alone. Together they fight, oscillate, or one gets compromised, and today the watchstander has to catch that across five consoles at 0200. Bridge Warden is not another agent. It's the layer every agent must go through. **No agent touches the ship directly.**"

## 0:30 — What we're looking at (60 s)
**Warden Control (left):**
- **Agent tiles:** the five AI systems. Grey idle, green acting, yellow waiting on a human, red quarantined.
- **SAFE-STOP:** hold 2 s, and every agent stops.
- **Next System Check:** countdown to the next operator check-in.
- **Live log** and **Edge Budget:** one small box, ~0.01 CPU cores, ~60 MB RAM, **0 bytes off-ship**.

**Main app (right), Overview = the Common Operating Picture:**
- **Radar:** own ship (blue arrow), planned route (blue dashed), contacts with vectors.
- **Right column:** decisions at the top, then own-ship status, who **holds** the rudder and engine, and alerts.
- **Anything needing a human drops in as a yellow banner at the top of every window, and stays on the COP with Approve / Reject.**

**Do:** **Start Operations**. **Say:** "Ops normal. Navigation holds route at 16 kn; energy would like to slow down; Warden settles it by priority."

## 1:30 — Inject Contact (75 s)
**See:** phase *Crossing contact*. Contact appears on the starboard bow with a red CPA ring. Collision Avoidance turns right and slows. The log shows Navigation **REJECTED: Predicted CPA < 1.3 nm**.
**Say:** "Navigation can't see the contact, by design; it only has the route. It keeps trying to steer back toward the contact. Warden predicts the closest point of approach for every request and blocks the unsafe one, in milliseconds, with the reason logged."
**Show:** Intents → click a red **REJECTED** row → the 8-step pipeline, **failed at Constraints**.

## 2:45 — Inject Overheat (60 s)
**See:** phase *Engine fault*. Engine temp turns yellow; Engine Health cuts to 10 kn; Navigation pushes 16 kn.
**Say:** "Two well-meaning agents in a tug-of-war over the throttle. That's emergent behavior no single vendor would catch. Warden spots the flip-flop and **damps it**, holding the engine for the higher-priority agent."
**Show:** Conflicts → **OSCILLATION** card, "Damped". **Do:** **Clear Fault** when done.

## 3:45 — Inject Storm → human decision (75 s)
**See:** phase *Heavy weather*. The **yellow banner** "Decision needed: Weather Routing…" drops in. On the COP: **rudder now 090° → proposed 055°**, the course drawn in yellow on the radar, and **2 minutes** on the clock.
**Say:** "A 35° course change is above what a machine may do alone, so Warden asks a human and shows exactly what changes. The watch has two minutes to read it. No answer means the **safe default**: hold course. The buttons arm after a moment, so a banner popping up can't be clicked by accident."
**Do:** **Approve** on the COP. Then **Clear Weather**.

## 5:00 — Inject Rogue (45 s)
**See:** phase *Compromised agent*. Red log lines: Energy tries the **rudder** (never allowed), 18 kn (over its limit), then spams (over quota). Within ~3 s a **red banner: QUARANTINED nrg-01**.
**Say:** "Bug or attacker, it doesn't matter. Three hard limits hit and it's contained automatically. **Nothing it asked for reached the ship.**"

## 5:45 — System check (45 s)
**Do:** **System Check Now** (or let the 2-minute timer fire on its own).
**See:** banner "System check: confirm autonomous operations" plus a checklist on the COP: agents, open decisions, conflicts, collision threats, engine, audit ledger.
**Say:** "Like the bridge watch alarm required on ships today, Warden makes the operator confirm the whole system is still fit to run autonomously. Miss one and it raises a critical alarm and asks again; miss two and it **SAFE-STOPs** itself. An unattended bridge never keeps running on AI alone."
**Do:** **Confirm Operations**.

## 6:30 — Authority + audit (30 s)
**Do:** hold **SAFE-STOP** 2 s → red bar, all tiles grey, safe speed. Hold again to **Resume**. Then **Verify Ledger** → "chain valid".
**Say:** "One gesture stops every AI aboard. Every request, decision, check-in and override is in a tamper-evident log."

## 7:00 — Close (15 s)
**Say:** "Visible, bounded, governable. It runs on one low-power box with no network, fields fast, and scales to any ship by config, not code."

---

## Ship-systems & threat beats (add any of these, in any order)
**Inject Hostile** → Navigation COP: a red ◆ at 17 nm closing; the **10 nm EMCON ring turns red** (we're radiating). A decision appears: *EW: set EMCON*, with the prediction "reaches the radius in N min, detection risk X% by then".
**Say:** "Emissions give us away. Warden predicts when the hostile reaches our EMCON radius and how likely it is to detect us if we keep radiating, and asks the watch to go quiet before that." **Approve** → ring turns green, *EMCON SET*.

**Inbound Attack** → red ▲ threats running down the hostile's bearing with an impact countdown. Decision: *Self-Defense: activate CIWS, ADS, counter-drone, ECM*, hit chance 100% → ~15%. If EMCON is set, Warden flags **ECM_BREAKS_EMCON**.
**Say:** "Weapons are never automatic here. The AI recommends; a human authorizes. And Warden shows the trade-off: jamming protects us but makes us visible again."

**Fire MMR2 / Loss of Engine / Flooding Aft** → **Damage Control COP**: the compartment fills red (fire) or blue (flood), and its equipment shows ✕. **Ship Systems COP**: GTG-2, SWBD-2 and PROP turn red; everything downstream loses power.
Decisions: *Damage Control: send Repair 3 to MMR2, boundaries at frames 200/250* and *Electrical Plant: close B-13 / B-14* (re-route power through Switchboard 1).
**Say:** "Two AIs, two decisions: people to the fire, and power around the damage. Approve both and watch the team walk to frame 225, set the yellow boundaries, put the fire out and restore propulsion, while the blue alternate path keeps steering and radar alive. Ignore it and the fire spreads compartment by compartment."

**Tactical AI (LLM)** → a real language model running on this laptop reads the same picture and proposes actions too, shown as *Tactical AI (LLM): …* decisions.
**Say:** "This one isn't scripted. It's a live LLM. Warden treats it exactly like every other agent: scoped data, limited tools, every action approved by a human, and anything malformed or out of scope is dropped." Show Agents → Tactical AI → **Language Model: CONNECTED**, model name, latency, last answer.

## Injection cheat sheet
| Button | What appears | One line to say |
|---|---|---|
| **Inject Contact** | Phase *Crossing contact*; red CPA ring; Collision Avoidance turns and slows; Navigation blocked | "Safety prediction beats schedule, automatically." |
| **Inject Overheat** | Phase *Engine fault*; temp rises; speed capped; oscillation card | "Two good agents fighting, and Warden stops the tug-of-war." |
| **Inject Storm** | Phase *Heavy weather*; yellow banner + COP decision + yellow proposed course; 2-min timer | "Big moves need a human, and the human sees the change first." |
| **Inject Rogue** | Phase *Compromised agent*; red log → red QUARANTINED banner in ~3 s | "Out-of-bounds requests never reach the ship." |
| **System Check Now** | Yellow banner + checklist on COP; Confirm / Hold | "The operator must stay in the loop, or it stops itself." |
| **Inject Hostile** | Red ◆ at 17 nm; red EMCON ring; EW decision with detection prediction | "Go quiet before they hear us." |
| **Inbound Attack** | Red ▲ inbound + countdown; Self-Defense decision; hit chance drops when approved | "AI recommends, a human authorizes." |
| **Fire MMR2 / Loss of Engine / Flooding Aft** | DC COP compartment fills; Ship Systems nodes red; DC + power re-route decisions | "People to the fire, power around the damage." |
| **Clear Fault / Clear Weather** | Phase back to *Ops normal* | "Recovery is automatic once the hazard clears." |
| **Rollback Last** | Last action undone; purple ROLLED BACK in Intents | "Every action carries its own undo." |
| **SAFE-STOP** (hold) | Red bar, all agents paused, safe speed | "One gesture stops every AI aboard." |

## If something breaks
- **A decision timed out mid-talk:** that's a feature. "No answer → safe default." Inject again.
- **App crash:** close the windows, rerun `run.ps1`, Reset. Or play the backup video.
- **"Is the AI deciding?"** "No. AI only writes explanations (tagged *rule text / cache / live*). Every decision is a deterministic policy or a human."
- **Want the old hands-off run?** Start with `$env:SCENARIO='full_demo'` for the scripted 4-beat storyline.
