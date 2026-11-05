// Watch screen, Settings screen, status bar and the typed safe-stop dialog for the main window (Fleet shell).
// Loaded after app.js: app.js builds its page map at render time, so these globals are found when a page opens.
// FLEET UI REQUIREMENTS MUST BE APPLIED: see AGENTS.md.
'use strict';
const esc2 = esc;

const BOUNDARY = {
    WEAPONS_NEED_HUMAN: ['Weapons need a human', 'An agent asked to use weapons. Only a person may release them.'],
    PERSONNEL_NEED_HUMAN: ['People need a human', 'An agent asked to send people into a casualty. Only a person may order that.'],
    TOOL_NOT_GRANTED: ['Tool not granted', 'The agent asked for an action its role does not hold.'],
    ALREADY_SET: ['Already in that state', 'The order would change nothing.']
};
const STATE_WORD = { active: 'Active', idle: 'Idle', escalated: 'Escalated', quarantined: 'Quarantined', paused: 'Paused' };
ui.sel = null;                 // the open decision shown in the Watch detail panel

function agentName(s, id) { const a = s.agents.find(x => x.id === id); return a ? a.name : id; }
function openDecisions(s) { return s.escalations.filter(e => e.status === 'open'); }

function intentFor(s, e) { return e.intent_id ? s.intents.find(x => x.id === e.intent_id) : null; }

function pageWatch(s) {
    const open = openDecisions(s);
    const sum = s.summary;
    if (!ui.sel || !open.find(e => e.id === ui.sel)) ui.sel = open.length ? open[0].id : null;
    const oldest = open.length ? Math.min(...open.map(e => e.remaining_s)) : null;
    const blocked = s.intents.filter(i => i.status === 'rejected').length;
    const tiles = `<div class="w-tiles">
      <div class="w-tile"><div class="l">Awaiting you</div><div class="v ${open.length ? 'warn' : 'ok'}">${open.length}</div><div class="s">${open.length ? 'decisions with a countdown' : 'nothing is waiting'}</div></div>
      <div class="w-tile"><div class="l">Open conflicts</div><div class="v ${sum.conflicts_open ? 'warn' : ''}">${sum.conflicts_open}</div><div class="s">${sum.conflicts_total} seen this run</div></div>
      <div class="w-tile"><div class="l">Blocked by Warden</div><div class="v ${blocked ? 'bad' : ''}">${blocked}</div><div class="s">orders refused at a boundary</div></div>
      <div class="w-tile"><div class="l">Shortest timer</div><div class="v ${oldest != null && oldest < 20 ? 'bad' : ''}">${oldest == null ? '–' : oldest.toFixed(0) + ' s'}</div><div class="s">then the safe default</div></div>
    </div>`;
    const agents = s.agents.map(a => {
        const q = a.status === 'quarantined', esc = a.status === 'escalated';
        const cls = q ? 'quar' : esc ? 'esc' : a.status === 'idle' ? 'idle' : a.status === 'paused' ? 'paused' : '';
        return `<button type="button" class="w-agent ${cls}" onclick="selectAgent('${a.id}');go('agents')" title="Open ${esc2(a.name)} on the Agents page"><span class="nm">${esc2(a.name)}</span>
          <span class="st"><span class="dot" aria-hidden="true"></span>${STATE_WORD[a.status] || esc2(a.status)}${a.status === 'quarantined' ? ' ⛔' : ''}</span></button>`;
    }).join('');
    const rows = open.map(e => {
        const it = intentFor(s, e);
        const hit = it && it.policy_hits && it.policy_hits[0];
        const crit = hit === 'WEAPONS_NEED_HUMAN' || hit === 'PERSONNEL_NEED_HUMAN';
        return `<button type="button" class="w-row" aria-pressed="${e.id === ui.sel}" onclick="ui.sel='${e.id}';render(BW.state)"><span class="sev ${crit ? 'crit' : ''}" aria-hidden="true"></span>
          <span><span class="t">${esc2(e.kind === 'system_check' ? 'System check' : agentName(s, e.agent_id))} ${hit ? badge((BOUNDARY[hit] || [hit])[0], 'red') : badge('Needs decision', 'yellow')}</span>
          <span class="s" style="display:block">${esc2(((e.proposal && (e.proposal.plain || e.proposal.value)) || e.title))}</span></span>
          <span class="tm">${e.remaining_s.toFixed(0)} s</span></button>`;
    }).join('');
    const conflicts = s.conflicts.slice(0, 4).map(c => `<div class="w-row" style="cursor:default"><span class="sev ${c.severity === 'critical' ? 'crit' : ''}" aria-hidden="true"></span>
        <span><span class="t">${esc2(c.target_resource)}: ${esc2(c.agent_ids.map(i => agentName(s, i)).join(' vs '))} ${badge(c.type, 'blue')}</span><span class="s" style="display:block">${esc2(c.summary)}</span></span>
        <span class="tm" style="color:var(--fl-muted-foreground)">×${c.count}</span></div>`).join('');
    const led = (ui.ledger || []).filter(r => r.type !== 'LLM_CALL' && r.type !== 'LLM_DECISION').slice(-8).reverse().map(r => `<div class="ln"><span class="t">#${r.seq}</span><span class="lv-${r.type === 'ALERT' ? 'error' : r.type === 'EXECUTED' ? 'ok' : r.type === 'DECISION' ? 'warn' : 'engine'}">${esc2(r.type)}</span><span>${esc2(r.actor)}: ${esc2(JSON.stringify(r.payload)).slice(0, 110)}</span></div>`).join('');
    return `<div class="watch"><div class="watch-main">
      ${tiles}
      <section aria-labelledby="w-ag"><div class="w-h"><h2 id="w-ag">Agents</h2><span class="meta">${sum.agents_active} of ${sum.agents_total} active</span></div><div class="w-agents">${agents}</div></section>
      <section aria-labelledby="w-dec"><div class="w-h"><h2 id="w-dec">Decisions waiting for you</h2><span class="meta">Weapons and people always wait here</span></div>
        <div class="w-rows">${rows || '<div class="w-empty">Nothing is waiting. Inject an issue in Warden Control, or wait for the agents to recommend something.</div>'}</div></section>
      ${conflicts ? `<section aria-labelledby="w-cf"><div class="w-h"><h2 id="w-cf">Conflicts between agents</h2><span class="meta">Repeats are folded</span></div><div class="w-rows">${conflicts}</div></section>` : ''}
      <section aria-labelledby="w-au"><div class="w-h"><h2 id="w-au">Audit record</h2><span class="meta"><a href="#" onclick="go('ledger');return false" style="color:var(--fl-accent)">Open the full ledger</a></span></div>
        <div class="w-ledger" aria-live="off">${led || '<span class="t">The ledger fills as decisions are made.</span>'}</div></section>
    </div>
    <aside class="watch-detail" aria-label="Decision detail">${watchDetail(s)}</aside></div>`;
}

function watchDetail(s) {
    const e = openDecisions(s).find(x => x.id === ui.sel);
    if (!e) return `<h3>No decision selected</h3><p class="w-why">When an agent recommends something that needs a person, it appears here with the reason, the boundary it hit, the options and the safe default.</p>`;
    const p = e.proposal || {}, alt = p.alternative, it = intentFor(s, e);
    const hits = (it && it.policy_hits) || [];
    const pct = Math.max(0, e.remaining_s / e.timeout_s * 100);
    const check = e.kind === 'system_check';
    const boundary = hits.length ? `<div class="violate" role="note"><b><svg class="ic" style="width:16px;height:16px" aria-hidden="true"><use href="#i-alert"/></svg>Boundary: ${hits.map(h => esc2((BOUNDARY[h] || [h])[0])).join(', ')}</b>${hits.map(h => esc2((BOUNDARY[h] || ['', 'Warden policy ' + h])[1])).join(' ')}</div>` : '';
    const recs = check ? '' : `<div class="rec-grid"><div class="rec"><span class="k">${alt ? 'OPTION A' : 'RECOMMENDED'}</span><div class="do">${esc2(p.value || p.plain || e.title)}</div><div class="w-why">Now: ${esc2(p.current || '–')}</div></div>
      ${alt ? `<div class="rec"><span class="k">OPTION B</span><div class="do">${esc2(alt.value)}</div><div class="w-why">${esc2(alt.rationale || '')}</div></div>` : `<div class="rec"><span class="k">SAFE DEFAULT</span><div class="do">${esc2(e.safe_default)}</div><div class="w-why">Applied if nobody decides</div></div>`}</div>`;
    return `<div><h3>${esc2(check ? 'Is the system still fit to run on its own?' : agentName(s, e.agent_id) + ' recommends: ' + (p.plain || p.value || e.title))}</h3><div class="id">${esc2(e.id)}${e.provenance && e.provenance.model ? ' · model ' + esc2(e.provenance.model) : ''}</div></div>
      ${boundary}
      ${p.why_short ? `<div class="w-why"><b>Why:</b> ${esc2(p.why_short)}</div>` : ''}
      ${recs}
      <div><div class="w-timer"><span>Time left</span><span class="mono">${e.remaining_s.toFixed(0)} s</span></div><div class="countdown" role="progressbar" aria-valuemin="0" aria-valuemax="${e.timeout_s}" aria-valuenow="${e.remaining_s.toFixed(0)}"><div style="width:${pct}%"></div></div>
        <div class="w-why">If nobody decides: <b>${esc2(e.safe_default)}</b></div></div>
      <div class="w-actions"><button type="button" class="btn success lg" onclick="NotifyWatch.decide('${e.id}', true)">${alt ? 'A: ' : ''}${esc2(e.approve_label)}</button>
        <button type="button" class="btn danger lg" onclick="NotifyWatch.decide('${e.id}', false)">${esc2(e.reject_label)}</button>
        ${alt ? `<button type="button" class="btn lg wide" onclick="BW.act('POST','/api/escalations/${e.id}/option-b',null,'Option B chosen')">B: ${esc2(alt.value.split('→').pop().trim())}</button>` : ''}</div>
      <div class="w-why">The decision, who made it and when is written to the audit ledger.</div>`;
}

// ---------------------------------------------------------------- settings
function pageSettings(s) {
    const cur = BWTheme.get();
    const swatches = { dark: ['#071a26', '#112d40', '#c9a66b', '#5ec8e0'], light: ['#fbfaf2', '#ffffff', '#8a6b45', '#0b6a80'], system: ['#071a26', '#fbfaf2', '#c9a66b', '#0b6a80'] };
    const opts = BWTheme.options.map(([v, nm, ds]) => `<button type="button" role="radio" class="theme-opt" aria-checked="${cur === v}" onclick="BWTheme.set('${v}');render(BW.state)">
        <span class="nm">${nm}${cur === v ? badge('On', 'green') : ''}</span><span class="ds">${ds}</span><span class="sw">${swatches[v].map(c => `<i style="background:${c}"></i>`).join('')}</span></button>`).join('');
    const ai = s.server.llm;
    return `<div class="settings">
      <section class="set-card" aria-labelledby="st-ap"><h2 id="st-ap">Appearance</h2><p>Applies to every Bridge Warden window. The radar and plant pictures stay dark in the Chart room theme, because they are drawn for a dark scope.</p>
        <div class="theme-opts" role="radiogroup" aria-labelledby="st-ap">${opts}</div></section>
      <section class="set-card" aria-labelledby="st-ai"><h2 id="st-ai">AI model</h2><p>Where the agents get their decisions. Change it in Warden Control, AI Model Server.</p>
        <dl class="kv"><dt>Endpoint</dt><dd class="mono">${esc2(ai.endpoint === 'fairlead' ? 'Fairlead (found automatically)' : (ai.base_url || ''))}</dd><dt>Route or model</dt><dd class="mono">${esc2(ai.local_model || 'auto')}</dd>
        <dt>Fairlead key</dt><dd>${ai.fairlead_key ? 'Installed in Windows Credential Manager (Fairlead\\BridgeWarden)' : 'Not installed'}</dd></dl>
        <p style="margin-top:12px"><button type="button" class="btn" onclick="window.open('/control.html','bw-control')">Open Warden Control</button></p></section>
      <section class="set-card" aria-labelledby="st-sim"><h2 id="st-sim">About this simulation</h2><p>Bridge Warden runs on a synthetic ship. Nothing here is connected to a real vessel, sensor or weapon. The data is unclassified.</p>
        <p style="margin:0"><button type="button" class="btn" onclick="try{localStorage.removeItem('bw-welcome')}catch(e){};location.reload()">Show the welcome page again</button></p></section></div>`;
}

// ---------------------------------------------------------------- status bar and header
function renderStatus(s) {
    const sum = s.summary, ai = s.server.llm;
    const state = sum.safe_stop ? ['stop', 'SAFE-STOP engaged'] : !s.server.running ? ['pause', 'Paused'] : ['run', 'Running'];
    $('sb-dot').className = 'dot ' + state[0]; $('sb-state').textContent = state[1];
    $('sb-addr').textContent = location.host;
    const via = ai.endpoint === 'fairlead' ? `Fairlead (${ai.local_model || 'local route'})` : ai.mode === 'live' ? `${ai.provider}/${ai.model}` : (ai.base_url || ai.mode);
    $('sb-ai').textContent = 'AI: ' + via;
    $('sb-counts').textContent = `${sum.agents_active}/${sum.agents_total} agents · ${sum.escalations_open} waiting · ${sum.submitted} intents`;
    $('sb-ver').textContent = 'Simulation · Bridge Warden';
    const n = $('n-watch'); if (n) n.innerHTML = sum.escalations_open ? badge(sum.escalations_open, 'yellow') : '';
    $('stop-label').textContent = sum.safe_stop ? 'Resume operations' : 'Safe-stop all agents';
    $('stop-open').className = 'fl-btn fl-btn--lg fl-stop ' + (sum.safe_stop ? 'fl-btn--success' : 'fl-btn--danger');
}
const PAGE_TITLES = { watch: 'Watch', conflicts: 'Conflicts', escalations: 'Escalations', ledger: 'Audit ledger', overview: 'Navigation', systems: 'Ship systems', dc: 'Damage control', agents: 'Agents', intents: 'Intents', settings: 'Settings' };

// ---------------------------------------------------------------- typed safe-stop
(function () {
    const scrim = $('stop-scrim'), phrase = $('stop-phrase'), go = $('stop-go');
    let opener = null;
    function close() { scrim.hidden = true; phrase.value = ''; go.disabled = true; document.removeEventListener('keydown', onKey, true); if (opener) opener.focus(); }
    function onKey(e) {
        if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); close(); return; }
        if (e.key !== 'Tab') return;
        const f = [phrase, go.disabled ? null : go, $('stop-cancel')].filter(Boolean); const i = f.indexOf(document.activeElement);
        if (e.shiftKey && i <= 0) { e.preventDefault(); f[f.length - 1].focus(); } else if (!e.shiftKey && i === f.length - 1) { e.preventDefault(); f[0].focus(); }
    }
    $('stop-open').addEventListener('click', () => {
        const stopped = BW.state && BW.state.summary.safe_stop;
        if (stopped) { BW.act('POST', '/api/control/resume-all', null, 'Operations resumed'); return; }   // resuming is not destructive: one click
        opener = $('stop-open'); scrim.hidden = false; phrase.focus(); document.addEventListener('keydown', onKey, true);
    });
    phrase.addEventListener('input', () => { go.disabled = phrase.value.trim().toLowerCase() !== 'stop'; });
    phrase.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !go.disabled) go.click(); });
    go.addEventListener('click', () => { BW.act('POST', '/api/control/safe-stop', null, 'SAFE-STOP engaged'); close(); });
    $('stop-cancel').addEventListener('click', close);
    scrim.addEventListener('mousedown', (e) => { if (e.target === scrim) close(); });
    $('gear').addEventListener('click', () => document.querySelector('.nav-item[data-page="settings"]').click());
})();

// The Watch screen shows the newest ledger rows, so keep them fresh while it is open.
setInterval(() => { if (ui.page === 'watch' && !document.hidden) BW.api('GET', '/api/ledger?limit=40').then((r) => { ui.ledger = r; render(BW.state); }).catch(() => { }); }, 4000);
BW.api('GET', '/api/ledger?limit=40').then((r) => { ui.ledger = r; }).catch(() => { });
