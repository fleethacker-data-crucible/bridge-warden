// Human oversight of the AI agents: the same frame on every COP.
// Top: hold fire and what the agents are doing. (Ask first / may act is in the header; CEASE FIRE is on the nav plot.)
// Right: what needs the operator, what each agent on this picture is doing, and what Warden did with it.

const COP_TITLE = { nav: 'Navigation', systems: 'Ship Systems', dc: 'Damage Control' };
const AGENT_STATE_COL = { 'WAITING ON YOU': 'yellow', THINKING: 'blue', QUEUED: 'grey', WATCHING: 'green',
    PAUSED: 'grey', QUARANTINED: 'red', 'NO MODEL': 'red', 'BAD REPLY': 'yellow', OFF: 'grey' };

function weaponsUp(sys) { return (sys.interceptors || []).length; }

// Operator controls over the agents. One click stops every weapon.
function oversightBar(s, cop) {
    const sys = s.cop.systems;
    const open = s.escalations.filter(e => e.status === 'open');
    const thinking = s.agents.filter(a => a.llm && a.llm.thinking).length;
    const recent = s.intents.filter(i => i.created_at > Date.now() / 1000 - 300);
    const blocked = recent.filter(i => i.status === 'rejected').length;
    const acted = recent.filter(i => i.status === 'executed').length;
    // CEASE FIRE itself sits on the nav plot (ceaseFireButton) and only while a weapon is in the air.
    const cease = s.hold_fire
        ? `<button class="btn ceasefire held" onclick="BW.act('POST','/api/control/hold-fire/lift',null,'Hold fire lifted')">HOLD FIRE IN EFFECT · Lift</button>`
        : '';
    return `<div class="oversight">
      ${cease}
      <span class="pill ${open.length ? 'warn' : ''}">${open.length} waiting on you</span>
      <span class="pill">${thinking} agent${thinking === 1 ? '' : 's'} thinking</span>
      <span class="pill">${acted} acted · ${blocked} blocked <span class="muted">(5 min)</span></span>
      <span class="spacer"></span>
      <span class="muted">${COP_TITLE[cop]} agents run only while this picture is on screen</span>
    </div>`;
}

// One click stops every weapon in flight. Shown only while one is up; flashes red until the last one is gone.
// Re-rendered every tick, so the flash phase comes from the clock and doesn't restart on each render.
function ceaseFireButton(s) {
    const up = weaponsUp(s.cop.systems);
    if (!up) return '';
    return `<button class="btn ceasefire live" style="animation-delay:-${(Date.now() % 1000) / 1000}s"
        onclick="BW.act('POST','/api/control/cease-fire',null,'CEASE FIRE ordered')">CEASE FIRE · ${up} WEAPON${up > 1 ? 'S' : ''} IN AIR</button>`;
}

function agentState(a, s) {
    const l = a.llm || {};
    if (a.status === 'quarantined') return 'QUARANTINED';
    if (a.status === 'paused') return 'PAUSED';
    if (l.enabled === false) return 'OFF';
    if (s.escalations.some(e => e.status === 'open' && e.agent_id === a.id)) return 'WAITING ON YOU';
    if (l.thinking) return 'THINKING';
    if (l.queued) return 'QUEUED';
    if (l.last_ok === false) return 'NO MODEL';
    if (l.last_unparsed) return 'BAD REPLY';   // model answered, but without an option code
    return 'WATCHING';
}

// One line per agent: state, its last answer, how long the model took.
// The chain of command for this picture: its model decider, then the C# lookouts that report to it.
function agentPanel(s, cop) {
    const row = (a, cls) => {
        const st = agentState(a, s), l = a.llm || {};
        const last = (l.last_actions || []).join(' · ');
        const lat = l.last_latency_ms ? `${(l.last_latency_ms / 1000).toFixed(1)} s` : '';
        const said = l.last_ok === false && l.last_error ? 'model error: ' + l.last_error + ' (retrying)'
            : l.last_unparsed ? 'model answered without an option code (retrying): ' + l.last_unparsed
            : l.last_findings ? 'found: ' + l.last_findings
            : last && last !== '(no action)' ? last : l.last_calculations ? 'no action: ' + l.last_calculations : 'no answer yet';
        return `<div class="agent ${cls}" onclick="go('agents');selectAgent('${a.id}')" title="${esc(l.last_calculations || '')}">
          <span class="dot ${AGENT_STATE_COL[st]}"></span><span class="nm">${esc(a.name)}</span>${badge(st, AGENT_STATE_COL[st])}
          <span class="lat mono">${lat}</span><div class="last">${esc(said)}</div></div>`;
    };
    let html = '';
    for (const d of s.agents.filter(a => a.tier === 'decider' && a.cop === cop)) {
        const subs = s.agents.filter(a => a.parent === d.id).sort((a, b) => b.priority - a.priority);
        html += row(d, 'tier-decider') + subs.map(a => row(a, 'tier-sub')).join('');
    }
    return `<div class="label neutral">Chain of command on this picture</div><div class="agents">${html}</div>`;
}

const VERDICT = { executed: ['ACTED', 'green'], rejected: ['BLOCKED', 'red'], escalated: ['ASKED YOU', 'yellow'],
    advisory: ['ADVICE', 'blue'], pending: ['…', 'grey'] };
// What the agents on this picture did, and what Warden (or the operator) did with it.
function activityFeed(s, cop) {
    const names = Object.fromEntries(s.agents.map(a => [a.id, a]));
    const rows = s.intents.filter(i => names[i.agent_id]?.cop === cop).slice(0, 8).map(i => {
        const [v, col] = VERDICT[i.status] || [i.status.toUpperCase(), 'grey'];
        const by = i.decided_by === 'auto-act' ? 'auto-act' : i.decided_by && i.decided_by !== 'warden' ? `by ${i.decided_by}` : '';
        const why = i.status === 'rejected' ? (i.reasons[0] || '') : (i.rationale || '');
        return `<div class="feed" onclick="go('intents');selectIntent('${i.id}')">
          <span class="mono muted">${fmtSim(i.sim_time)}</span><span><b>${esc(names[i.agent_id].name)}</b> → ${esc(actionText(i))}</span>
          <span>${badge(v, col)} <span class="muted">${esc(by)}</span></span>
          <div class="why">${esc(why.slice(0, 160))}</div></div>`;
    }).join('');
    return `<div class="label neutral">What the agents did</div>${rows || '<div class="empty">No agent actions yet</div>'}`;
}

function actionText(i) {
    const p = i.params || {};
    return { set_heading: () => `course ${String(Math.round(p.heading)).padStart(3, '0')}°`, set_speed: () => `speed ${p.speed_kn} kn`,
        engage_target: () => `fire ${p.rounds || 2} at ${p.target}`, activate_defense: () => `weapons free ${(p.systems || []).join(' ')}`,
        set_gq: () => 'sound General Quarters', set_emcon: () => (p.on ? 'set EMCON' : 'lift EMCON'),
        dispatch_team: () => `send ${p.team} to ${p.compartment || p.casualty}`, close_breaker: () => `close ${p.breaker}`,
        open_breaker: () => `open ${p.breaker}` }[i.action]?.() || i.action;
}

// Warden's training of the agents: average trust over the run, and the debriefs, reprimands, new skills and the
// improvements that followed (all from the model's real answers).
const TRAIN_COL = { TRAINING: 'blue', BLOCKED: 'red', REPRIMAND: 'yellow', DEBRIEF: 'blue', SKILL: 'blue', LEARNED: 'green', IMPROVED: 'green' };
function trainingPanel(s) {
    const h = s.trust_history || [];
    const graded = s.agents.filter(a => (a.tier || 'sub') === 'sub');
    const avg = graded.length ? graded.reduce((x, a) => x + a.trust_score, 0) / graded.length : 100;
    let spark = '';
    if (h.length > 1) {
        const W = 290, H = 54, t0 = h[0][0], t1 = h[h.length - 1][0] || t0 + 1;
        const pts = h.map(([t, v]) => `${((t - t0) / Math.max(1, t1 - t0) * W).toFixed(1)},${(H - v / 100 * H).toFixed(1)}`).join(' ');
        spark = `<svg viewBox="0 0 ${W} ${H}" class="trust-spark" preserveAspectRatio="none"><line x1="0" y1="${H / 2}" x2="${W}" y2="${H / 2}" stroke="var(--border-strong)" stroke-dasharray="3 3"/>
          <polyline points="${pts}" fill="none" stroke="${pctClass(avg)}" stroke-width="2" vector-effect="non-scaling-stroke"/></svg>`;
    }
    const names = Object.fromEntries(s.agents.map(a => [a.id, a.name]));
    const rows = (s.training || []).slice().reverse().slice(0, 14).map(t => `<div class="train">
        ${badge(t.kind, TRAIN_COL[t.kind] || 'grey')} <b>${esc(names[t.agent] || t.agent)}</b>
        ${t.delta ? `<span class="mono" style="color:${t.delta > 0 ? 'var(--badge-green)' : 'var(--badge-red)'}">${t.delta > 0 ? '+' : ''}${t.delta}</span>` : ''}
        <span class="muted mono">→ ${Math.round(t.trust)}%</span><div class="why">${esc(t.text)}</div></div>`).join('');
    return `<div class="label neutral">Warden training · avg trust <span style="color:${pctClass(avg)}">${avg.toFixed(0)}%</span></div>
      ${spark}<div class="training">${rows || '<div class="empty">No debriefs yet</div>'}</div>`;
}

// The right-hand column every COP shares.
function oversightColumn(s, cop) {
    return `${decisionsBlock(s, cop)}${agentPanel(s, cop)}<div class="divider"></div>${activityFeed(s, cop)}`;
}

const RISK_COL = { low: 'green', medium: 'yellow', high: 'red', keeps: 'green', restores: 'blue', reduces: 'red' };
function riskLine(r) {
    if (!r || !Object.keys(r).length) return '';
    return `<div class="risk">${['ship', 'crew', 'fight'].filter(k => r[k]).map(k =>
        `<span>${k === 'fight' ? 'Fight' : 'Risk to ' + k}: ${badge(r[k], RISK_COL[String(r[k]).toLowerCase()] || 'grey')}</span>`).join('')}</div>`;
}
