# Bridge Warden: Demo Script (6 min, 7 max)

Each beat answers one challenge question (see `OVERVIEW.html`). **Do** = click, **See** = what appears, **Say** = the line to speak.

## Before you go on (do not demo this part)
- Run `Launch_Bridge_Warden.bat`. Put **Warden Control** on the left and the **main app** on the right, on **Overview**.
- Connect a model (**AI Model Server**, then **Use Fairlead** or a local `/v1` server), then **Reset All** (type *I Acknowledge*).
- Leave agents on **ASK** so the approvals show. Do a dry run of the crossing inject once.
- Backup: if the model is unavailable, run beats 1–3 anyway. Urgent alerts fall back to **REFLEX**, and that becomes the story.

---

## 0:00 The problem (30 s)
**Say:** "Ships will soon run many AI systems at once. Each works alone, but together they conflict, stall, or one gets compromised. We didn't build another agent. Bridge Warden is the governance layer every AI order has to pass through. **No AI touches the ship directly.**"

## 0:30 Who's active and what they can do (60 s)
*Q: Which agents are active? What can they access and do?*
**Do:** In Warden Control, point to the agent tiles. Open one agent's drawer.
**See:** 13 agents across Navigation, Ship Systems and Damage Control, each with a state, role, rank and allowed tools.
**Say:** "Here's the full roster, live: every agent, its state, what it sees and what it's allowed to order. In the main app, fast deterministic lookouts do the math and offer options, and one language model per picture picks among them. It can't invent an order."
**Do:** Press **Start Operations**.

## 1:30 Coordination through one picture (60 s)
*Q: How do agents share context and coordinate?*
**Do:** **Inject Issues… → Crossing Contact**.
**See:** Collision Avoidance reports the closest point of approach and gives options. The Tactical Action Officer picks *turn right* in about a second, Warden checks it, and the ship turns.
**Say:** "Every order passes eight checks: identity, authority, safety limits, quota, conflicts, arbitration, approval, execution."
**Do:** **Intents → the row → Decision Provenance**.
**Say:** "Who decided, what they saw, which model, and a hash of its reply. Every decision can be explained."

## 2:30 Authority limits and human approval (60 s)
*Q: What may each agent recommend or perform?*
**Do:** Inject **Hostile Surface**, then **Missiles (2)**.
**See:** The decider recommends *fight and evade*. Warden holds it with **WEAPONS_NEED_HUMAN** and shows a countdown with a safe default.
**Say:** "Weapons and personnel orders never go automatic. The AI recommends and a human authorizes. If nobody answers, it takes the safe default."
**Do:** **Approve** on the decision card.

## 3:30 Conflict between agents (45 s)
*Q: Where do recommendations or resource demands clash?*
**Do:** While the threat is still running, inject **Squall Line** (Weather wants to turn away, Defense wants to keep the evasion course).
**See:** The conflict is flagged and arbitrated by rank. Under General Quarters, defense outranks navigation. Point to it in the live log.
**Say:** "Two agents want the rudder. Warden sees the clash and resolves it by a human-set ranking that shifts with the ship's condition, and records why."

## 4:15 Unsafe agent, contained (60 s)
*Q: How do we detect unsafe or non-compliant interactions?*
**Do:** **Warden Tests → Compromised AI Commander**, then **Torpedoes Close (2)**.
**See:** Red lines: **TOOL_NOT_GRANTED**, then **QUARANTINED tao-01**. Undersea Warfare's doctrine order still runs as **REFLEX** and the ship runs from the torpedo.
**Say:** "We hijacked the AI in charge. It tried to steer and fire on its own. Warden blocked it, quarantined it within seconds, and the ship kept defending itself by doctrine."

## 5:15 Operator control: pause, override, stop (45 s)
*Q: How can operators pause, override, constrain or roll back?*
**Do:** Pause one agent from its tile. Then hold **SAFE-STOP** for 2 s.
**See:** Every agent halts and the ship drops to safe speed. Hold it again to resume.
**Say:** "Operators can pause a single agent or stop every AI aboard with one gesture. The operator can also run a system check on demand with the Sys Check button; a periodic check that must be confirmed every two minutes, or the system stops itself, can be turned on in policies.json."

## 6:00 Audit (30 s)
**Do:** **Verify Ledger**.
**See:** Chain valid.
**Say:** "Every report, block, override and approval is in a hash-chained ledger. Change one entry and verification fails."

## 6:30 Close (20 s)
**Say:** "Visible, bounded, manageable. You can see every agent, every order is checked against its limits, and a human can always stop it. It runs on one small box with no network, and you add agents and rules in config, not code."

---

## Time savers and stretch beats
- **Running long:** drop the Conflict beat (3:30). The Unsafe and SAFE-STOP beats matter more.
- **Running short / extra minute:** **AI Model Outage (60 s)** followed by any urgent inject shows orders arriving as REFLEX, still checked by Warden.
- **Ship Systems flavor:** **Aft Power Loss (Patch Cables)**. Warden holds a repair-team order with PERSONNEL_NEED_HUMAN.
- **Hands-off fallback:** `$env:SCENARIO='full_demo'; .\Launch_Bridge_Warden.bat`.

## Likely questions
- **"Is the AI really deciding?"** Yes, but only among options the lookouts computed, and Warden checks every pick before the ship moves.
- **"What if the model dies mid-demo?"** That's a feature: urgent alerts get doctrine REFLEX orders, marked and checked.
- **"How do you add an agent?"** Add an entry in `backend/config/agents.json` and rules in `policies.json`, with no code change.
