// Bridge Warden main app: Overview (COP), Agents, Intents, Conflicts, Escalations, Ledger.

const $ = (id) => document.getElementById(id);
// Which notifications each COP shows. Other pages show everything.
const PAGE_DOMAIN = { overview: 'nav', systems: 'systems', dc: 'dc' };
const ui = { page: 'overview', agent: null, intent: null, ledger: null, explain: {} };
// Streams the explanation (Server-Sent Events) and shows each chunk as it arrives.
async function explainIntent(id) {
    const x = ui.explain[id] = { story: '', thoughts: '', thinking: true, open: false };
    render(BW.state);
    try {
        const res = await fetch(`/api/intents/${id}/explain/stream`);
        if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
        const reader = res.body.getReader(), dec = new TextDecoder();
        let buf = '';
        for (;;) {
            const { value, done } = await reader.read();
            if (done) break;
            buf += dec.decode(value, { stream: true });
            let cut;
            while ((cut = buf.indexOf('\n\n')) >= 0) {
                const block = buf.slice(0, cut); buf = buf.slice(cut + 2);
                const ev = (block.match(/^event: (.*)$/m) || [])[1];
                const data = JSON.parse((block.match(/^data: (.*)$/m) || [, 'null'])[1]);
                if (ev === 'thought') x.thoughts += data;
                else if (ev === 'content') { x.thinking = false; x.story += data; }
                else if (ev === 'replace') { x.thinking = false; x.story = data; }
                else if (ev === 'done') Object.assign(x, data, { thinking: false });
            }
            render(BW.state);
        }
    } catch (e) {
        x.thinking = false;
        BW.toast(`Failed: ${e.message}`);
    }
    render(BW.state);
}
// Re-rendered on every chunk and tick, so the Thinking section remembers whether the operator opened it.
function explainBody(id) {
    const x = ui.explain[id];
    if (!x) return '<span class="muted">Ask the AI model (or rule text) to explain this decision.</span>';
    const thoughts = x.thoughts ? `<details class="thoughts"${x.open ? ' open' : ''} ontoggle="ui.explain['${id}'].open = this.open"><summary>Thinking</summary><div>${esc(x.thoughts)}</div></details>` : '';
    const timing = x.source ? ` <span class="muted" style="font-size:13px">first token ${x.first_token_ms || '–'} ms · total ${x.latency_ms} ms · prompt ${x.prompt_chars} chars</span>` : '';
    return `${thoughts}${x.thinking ? '<span class="muted thinking">Thinking…</span>' : ''}${esc(x.story)}${x.source ? ' ' + sourceTag(x.source) : ''}${timing}`;
}
const STAGE_SHORT = { authenticate: 'Auth', authority: 'Authz', constraints: 'Cnstr', quota: 'Quota',
    conflicts: 'Confl', arbitrate: 'Arbit', approve: 'Apprv', execute: 'Exec' };

// ---------------------------------------------------------------- shell
function renderShell(s) {
    const sum = s.summary;
    const ai = s.server.llm;
    // Through Fairlead the model may be in the cloud or local, so name Fairlead and the route instead of "local".
    const viaFairlead = ai.endpoint === 'fairlead' ? ` via Fairlead (${ai.local_model || 'local route'})` : '';
    $('sub').textContent = `Multi-agent governance · ${BW.connected ? 'live' : 'DISCONNECTED'} · phase: ${s.cop.phase} · AI text: ${ai.mode}${ai.mode === 'live' ? ` (${ai.provider}/${ai.model})` : ''}${viaFairlead}`;
    $('chat-model').textContent = `${viaFairlead ? 'AI model' + viaFairlead : 'local model'} · proposes, never acts`;
    $('k-sim').textContent = fmtSim(s.cop.sim_time);
    const sp = (s.presenter && s.presenter.speed) || 1;
    $('k-speed').innerHTML = `<button class="btn ${sp > 1 ? 'warn' : ''}" style="height:24px;min-width:56px" title="Sim speed: click for ${sp % 5 + 1}x" onclick="BW.act('POST','/api/presenter/speed',null,'Sim speed ${sp % 5 + 1}x')">${sp}x</button>`;
    const gq = s.cop.systems.gq;
    $('k-gq').innerHTML = `<button class="btn ${gq ? '' : 'danger'}" style="height:24px" onclick="BW.act('POST','/api/control/gq/${gq ? 'off' : 'on'}')">${gq ? 'GQ · Secure' : 'Sound GQ'}</button>`;
    // One toggle; the label is the mode in force. Click switches to the other.
    // One toggle for every agent: ASK (gold) = agents ask the watch first; ACT (green) = agents may act. Click flips it.
    const act = s.auto_act;
    $('k-auth').innerHTML = `<button class="authsw ${act ? 'is-act' : 'is-ask'}" title="All agents: click to switch to ${act ? 'ASK first' : 'ACT on their own'}"
        onclick="BW.act('POST','/api/control/auto-act/${act ? 'off' : 'on'}',null,'All agents: ${act ? 'ASK first' : 'ACT'}')"><span class="ask">ASK</span><span class="act">ACT</span></button>`;
    $('k-agents').textContent = `${sum.agents_active}/${sum.agents_total}`;
    $('k-ipm').textContent = sum.intents_per_min;
    $('k-cf').textContent = sum.conflicts_total;
    $('k-esc').textContent = sum.escalations_open;
    $('n-agents').textContent = sum.agents_total;
    $('n-intents').textContent = sum.submitted;
    $('n-conflicts').textContent = sum.conflicts_total || '';
    $('n-esc').innerHTML = sum.escalations_open ? badge(sum.escalations_open, 'yellow') : '';
    $('banner').classList.toggle('show', sum.safe_stop);
    $('paused').classList.toggle('show', !s.server.running);
}

document.querySelectorAll('.nav-item').forEach(n => n.addEventListener('click', () => {
    document.querySelectorAll('.nav-item').forEach(x => {
        x.classList.toggle('active', x === n);
        if (x === n) x.setAttribute('aria-current', 'page'); else x.removeAttribute('aria-current');
    });
    // Keyboard focus stays on the button; the live region tells screen readers which page opened.
    $('page-status').textContent = `${[...n.childNodes].filter(c => c.nodeType === 3).map(c => c.textContent).join('')} page`;
    ui.page = n.dataset.page;
    Notify.setScope(PAGE_DOMAIN[ui.page] || null);
    if (PAGE_DOMAIN[ui.page]) BW.api('POST', `/api/view/${PAGE_DOMAIN[ui.page]}`).catch(() => {});   // tells the server which COP is on screen (all COPs keep running)
    ui.agent = ui.intent = null;
    render(BW.state);
    if (ui.page === 'ledger') loadLedger();
}));

// Only touch the DOM when the markup changed, and never mid-click, so live updates can't eat an operator's click.
let pointerDown = false;
document.addEventListener('pointerdown', () => { pointerDown = true; });
document.addEventListener('pointerup', () => { pointerDown = false; setTimeout(() => render(BW.state), 0); });
function setHtml(el, html) {
    if (el._html !== html) { el.innerHTML = html; el._html = html; }
}

function render(s) {
    if (!s || pointerDown || ui.dragging) return;   // never rebuild the page under a drag (Agents roster re-rank)
    renderShell(s);
    const pages = { overview: pageOverview, systems: pageSystems, dc: pageDC, agents: pageAgents, intents: pageIntents,
        conflicts: pageConflicts, escalations: pageEscalations, ledger: pageLedger };
    $('content').classList.toggle('nav-full', ['overview', 'systems', 'dc'].includes(ui.page));
    renderSide(s);
    setHtml($('content'), pages[ui.page](s));
    renderDrawer(s);
    fitNavCop();
    applyNavView();
}

// The nav COP is square: size it to the smaller of its column's width and the height left below it in the window,
// so the whole scope is always on screen.
// The scope fills its whole area; the SVG (viewBox "meet") keeps the 20 nm picture square and centered in it.
function fitNavCop() {
    const svg = document.querySelector('.nav-cop');
    if (svg) svg.style.width = svg.style.height = '';
}
window.addEventListener('resize', fitNavCop);

// Nav COP zoom (mouse wheel, toward the cursor) and pan (drag). Double-click resets to the full 20 nm picture.
// The view is kept here and re-applied after every render, so live updates don't snap it back.
const NAV_R = 20;
ui.navView = { zoom: 1, cx: 0, cy: 0 };
function applyNavView() {
    const svg = document.querySelector('.nav-cop');
    if (!svg) return;
    const v = ui.navView, half = NAV_R / v.zoom;
    svg.setAttribute('viewBox', `${v.cx - half} ${v.cy - half} ${2 * half} ${2 * half}`);
    svg.classList.toggle('zoomed', v.zoom > 1.001 || v.cx !== 0 || v.cy !== 0);
}
// The view never leaves the 20 nm picture: the outer range ring can't be dragged off, and fully zoomed out the
// rings sit centered again.
function clampNavView() {
    const v = ui.navView;
    v.zoom = Math.min(8, Math.max(1, v.zoom));
    const lim = NAV_R - NAV_R / v.zoom;
    v.cx = Math.min(lim, Math.max(-lim, v.cx));
    v.cy = Math.min(lim, Math.max(-lim, v.cy));
}
// Screen px per scope nm, and where the (square, centered) picture sits inside the SVG box.
function navFrame(svg) {
    const r = svg.getBoundingClientRect(), side = Math.min(r.width, r.height);
    return { r, side, left: r.left + (r.width - side) / 2, top: r.top + (r.height - side) / 2 };
}
function navPoint(svg, ev) {                       // client px -> scope nm (SVG user units)
    const fr = navFrame(svg), v = ui.navView, half = NAV_R / v.zoom;
    return [v.cx - half + (ev.clientX - fr.left) / fr.side * 2 * half, v.cy - half + (ev.clientY - fr.top) / fr.side * 2 * half];
}
document.addEventListener('wheel', (ev) => {
    const svg = ev.target.closest && ev.target.closest('.nav-cop');
    if (!svg) return;
    ev.preventDefault();
    const [px, py] = navPoint(svg, ev), v = ui.navView, before = v.zoom;
    v.zoom *= ev.deltaY < 0 ? 1.2 : 1 / 1.2;
    clampNavView();
    const k = before / v.zoom;                      // keep the point under the cursor fixed
    v.cx = px - (px - v.cx) * k; v.cy = py - (py - v.cy) * k;
    clampNavView();
    applyNavView();
}, { passive: false });
let navDrag = null;
document.addEventListener('pointerdown', (ev) => {
    const svg = ev.target.closest && ev.target.closest('.nav-cop');
    if (!svg || ev.button !== 0 || ev.target.closest('button')) return;
    navDrag = { x: ev.clientX, y: ev.clientY, cx: ui.navView.cx, cy: ui.navView.cy, w: navFrame(svg).side };
    svg.classList.add('dragging');
});
document.addEventListener('pointermove', (ev) => {
    if (!navDrag) return;
    const scale = 2 * NAV_R / ui.navView.zoom / navDrag.w;
    ui.navView.cx = navDrag.cx - (ev.clientX - navDrag.x) * scale;
    ui.navView.cy = navDrag.cy - (ev.clientY - navDrag.y) * scale;
    clampNavView();
    applyNavView();
});
document.addEventListener('pointerup', () => {
    if (!navDrag) return;
    navDrag = null;
    const svg = document.querySelector('.nav-cop');
    if (svg) svg.classList.remove('dragging');
});
document.addEventListener('dblclick', (ev) => {
    if (!(ev.target.closest && ev.target.closest('.nav-cop'))) return;
    ui.navView = { zoom: 1, cx: 0, cy: 0 };
    applyNavView();
});

// ---------------------------------------------------------------- overview
function copSvg(cop) {
    const R = 6, own = cop.ownship;
    const P = (x, y) => [(x - own.x).toFixed(3), (-(y - own.y)).toFixed(3)];
    const vec = (h, len) => [Math.sin(h * Math.PI / 180) * len, Math.cos(h * Math.PI / 180) * len];
    let g = '';
    for (const r of [2, 4, 6]) {
        g += `<circle r="${r}" fill="none" stroke="var(--border-strong)" stroke-width="0.02"/>`;
        g += `<text x="0.08" y="${-r + 0.3}" style="font-size:0.28px">${r} nm</text>`;
    }
    g += `<line x1="${-R}" y1="${own.y}" x2="${R}" y2="${own.y}" stroke="var(--blue)" stroke-width="0.03" stroke-dasharray="0.15 0.12" opacity="0.6"/>`;
    g += `<text x="${-R + 0.15}" y="${own.y - 0.12}" style="font-size:0.26px;fill:var(--blue)">PLANNED ROUTE 090°</text>`;
    const [hx, hy] = vec(own.heading, own.speed / 10 * 1.0);
    g += `<line x1="0" y1="0" x2="${hx}" y2="${-hy}" stroke="var(--text)" stroke-width="0.04"/>`;
    const [cx, cy] = vec(own.cmd_heading, 1.6);
    g += `<line x1="0" y1="0" x2="${cx}" y2="${-cy}" stroke="var(--badge-green)" stroke-width="0.03" stroke-dasharray="0.1 0.08"/>`;
    for (const e of (BW.state?.escalations || []).filter(x => x.status === 'open' && x.proposal?.heading !== undefined)) {
        const [px, py] = vec(e.proposal.heading, 3.2);
        g += `<line x1="0" y1="0" x2="${px}" y2="${-py}" stroke="var(--badge-yellow)" stroke-width="0.05" stroke-dasharray="0.18 0.1"/>`;
        g += `<text x="${px * 1.08}" y="${-py * 1.08 - 0.15}" text-anchor="middle" style="font-size:0.3px;fill:var(--badge-yellow);font-weight:700">PROPOSED ${esc(e.proposal.value)} · ${esc(e.agent_id)}</text>`;
    }
    g += `<polygon points="0,-0.32 0.18,0.22 -0.18,0.22" fill="var(--blue)" transform="rotate(${own.heading})"/>`;
    for (const c of cop.contacts) {
        const [x, y] = P(c.x, c.y);
        const threat = c.cpa_nm < 1.0 && c.tcpa_min >= 0 && c.tcpa_min <= 30;
        const col = threat ? 'var(--badge-red)' : 'var(--badge-yellow)';
        const [vx, vy] = vec(c.course, c.speed / 10 * 1.0);
        g += `<line x1="${x}" y1="${y}" x2="${+x + vx}" y2="${+y - vy}" stroke="${col}" stroke-width="0.04"/>`;
        g += `<rect x="${x - 0.13}" y="${y - 0.13}" width="0.26" height="0.26" fill="${col}"/>`;
        if (threat) g += `<circle cx="${x}" cy="${y}" r="0.45" fill="none" stroke="${col}" stroke-width="0.03"/>`;
        g += `<text x="${+x + 0.3}" y="${+y - 0.2}" style="font-size:0.3px;fill:${col}">${esc(c.id)} · CPA ${c.cpa_nm.toFixed(2)} nm · ${c.tcpa_min.toFixed(0)} min</text>`;
    }
    g += `<text x="${-R + 0.15}" y="${R - 0.2}" style="font-size:0.26px">— heading  ┄ commanded · 6-min vectors</text>`;
    return `<svg class="cop" viewBox="${-R} ${-R} ${2 * R} ${2 * R}">${g}</svg>`;
}

// The picture facts and oversight bar go in the left panel (renderSide), so the scope gets the full height.
function pageOverview(s) {
    return copStage(`<div class="nav-center">${navCopSvg(s.cop, ceaseFireButton(s))}</div>`, s, 'nav');
}

// Left panel under the page buttons: on a COP its facts, oversight bar, its agents and what they did; on every page
// Warden's training log.
const SIDE = { overview: ['Navigation', '20 nm picture · what the agents see', 'nav'],
    systems: ['Ship Systems', 'Electrical plant · re-routing paths', 'systems'],
    dc: ['Damage Control', 'Compartments and response teams', 'dc'] };
function renderSide(s) {
    const p = SIDE[ui.page];
    setHtml($('side-extra'), (p ? `<div><div class="page-title">${p[0]}</div><div class="muted">${p[1]}</div></div>
           <div>${agentPanel(s, p[2])}</div>` : '') +
        `<div>${trainingPanel(s)}</div>` + (p ? `<div>${activityFeed(s, p[2])}</div>` : ''));
}

function decisionCard(e) {
    const p = e.proposal || {};
    const pct = Math.max(0, e.remaining_s / e.timeout_s * 100);
    const check = e.kind === 'system_check';
    const alt = p.alternative;
    const who = (BW.state?.agents || []).find(a => a.id === e.agent_id);
    const headline = check ? 'Is the system still fit to run on its own?'
        : `${who ? who.name : e.agent_id} recommends: ${p.plain || p.value || e.title}`;
    const plainWhy = check ? '' : (p.why_short || '');
    const detail = check
        ? `<div class="checklist">${e.summary.split(' · ').map(f => `<div>${esc(f)}</div>`).join('')}</div>`
        : `<div class="plain-why">${plainWhy ? `<b>Why:</b> ${esc(plainWhy)}` : ''}${p.current ? `<div class="muted">Now: ${esc(p.current)}</div>` : ''}</div>
           ${alt ? `<div class="plain-why"><b>Or (B):</b> ${esc(alt.value)}</div>` : ''}
           <details class="more"><summary class="muted">More detail</summary>
           <div class="chg"><span class="k">${esc(p.target || '')} now</span><span class="v">${esc(p.current || '–')}</span>
             <span class="k">${alt ? 'Option A' : 'Proposed'}</span><span class="v new">${esc(p.value || '–')}</span>
             ${alt ? `<span class="k">Option B</span><span class="v new">${esc(alt.value)}</span>` : ''}</div>
           ${riskLine(p.risk)}
           ${alt && alt.rationale ? `<div class="why"><b>Why B:</b> ${esc(alt.rationale)}</div>` : ''}
           ${e.provenance && e.provenance.model ? `<details><summary class="muted" style="cursor:pointer;font-size:14px">${badge('LLM ✓ ' + e.provenance.model, 'blue')} ${e.provenance.tools} tool calls · show the math</summary>
           <div class="calc">${esc(e.provenance.calculations || '(no calculations given)')}</div></details>` : ''}
           <div class="why">${esc(e.summary)} ${sourceTag(e.summary_source)}</div></details>`;
    return `<div class="decision"><div class="plain-head">${esc(headline)}</div>
      ${detail}
      <div class="countdown"><div style="width:${pct}%"></div></div>
      <div class="muted" style="margin-bottom:10px;font-size:13px">${e.remaining_s.toFixed(0)} s left, then: ${esc(e.safe_default)}</div>
      <div class="btn-row"><button class="btn success lg" onclick="NotifyWatch.decide('${e.id}', true)">${alt ? 'A: ' : ''}${esc(e.approve_label)}</button>
        ${alt ? `<button class="btn primary lg" onclick="BW.act('POST','/api/escalations/${e.id}/option-b',null,'Option B chosen')">B: ${esc(alt.value.split('→').pop().trim())}</button>` : ''}
        <button class="btn danger lg" onclick="NotifyWatch.decide('${e.id}', false)">${esc(e.reject_label)}</button></div></div>`;
}

function go(page) { document.querySelector(`.nav-item[data-page="${page}"]`).click(); }

// ---------------------------------------------------------------- agents
const AGENT_COLS = '.3fr 2.2fr 1fr 1.2fr 1fr .7fr .6fr';
// MORDECAI rank: effective = base (human-assigned) + condition modifier (by class) + field promotion. Mordecai.cs.
const CONDITION_LABEL = { cruising: 'CRUISING', general_quarters: 'GENERAL QUARTERS' };
const signed = (n) => n > 0 ? `+${n}` : n < 0 ? `${n}` : '±0';
function rankCell(r) {
    if (!r) return '–';
    const parts = `${r.base} ${signed(r.modifier)}${r.promotion ? ` <b class="promo">+${r.promotion}</b>` : ''}`;
    return `<span class="rank-eff">${r.effective}</span><span class="rank-parts">${parts}</span>`;
}

function pageAgents(s) {
    // The roster in rank order: who outranks whom right now. Re-sorts live on a condition flip or a promotion.
    const agents = s.agents.slice().sort((a, b) => (b.rank ? b.rank.effective : 0) - (a.rank ? a.rank.effective : 0) || a.id.localeCompare(b.id));
    const rows = agents.map((a, n) => `<div class="trow agent-row ${ui.agent === a.id ? 'selected' : ''}" style="grid-template-columns:${AGENT_COLS}"
        draggable="true" data-agent="${esc(a.id)}" title="Drag onto another agent to rank it above (top half) or below (bottom half)" onclick="selectAgent('${a.id}')">
        <div class="right mono muted"><span class="grip" aria-hidden="true">⠿</span>${n + 1}</div>
        <div><div class="primary">${esc(a.name)}${a.protocol ? ' ' + badge('PROTOCOL', 'purple') : ''}${a.rank && a.rank.promotion ? ' ' + badge('PROMOTED', 'yellow') : ''}</div>
          <div class="id">${esc(a.id)} · ${esc(a.agent_class || a.role)} · ${esc(a.vendor)}</div></div>
        <div>${badge(a.status)}</div><div>${autonomyBadge(a.autonomy_level)}</div>
        <div class="right">${rankCell(a.rank)}</div>
        <div class="right" style="font-weight:700;color:${pctClass(a.trust_score)}">${a.trust_score.toFixed(0)}%</div>
        <div class="right mono ${a.violations ? '' : 'muted'}" style="${a.violations ? 'color:var(--badge-red)' : ''}">${a.violations}</div></div>`).join('');
    const cond = s.condition || 'cruising';
    return `<div class="toolbar"><span class="page-title">Agents</span><span class="muted">Every AI system aboard in rank order: what it may access, what it may do, and who outranks whom.</span></div>
      <div class="condition-bar ${cond}"><b>CONDITION: ${CONDITION_LABEL[cond] || esc(cond)}</b>
        <span>Rank = base (human-assigned) + condition modifier for the agent's class + field promotion. Arbitration and leases use it live.
          Drag an agent onto another to rank it above or below (or use Move Up / Move Down in its panel).</span></div>
      <div class="table"><div class="thead" style="grid-template-columns:${AGENT_COLS}"><span class="right">#</span><span>Agent</span><span>Status</span>
      <span>Autonomy</span><span class="right">Rank</span><span class="right">Trust</span><span class="right">Violations</span></div>${rows}</div>`;
}
function selectAgent(id) { ui.agent = ui.agent === id ? null : id; ui.intent = null; render(BW.state); }

// ---- Roster re-rank: drag an agent row onto another (top half = above it, bottom half = below it).
// Sets the dragged agent's human-assigned base rank (POST /api/agents/<id>/rank); the page doesn't re-render mid-drag.
function rankRelative(id, place, other) {
    if (!id || !other || id === other) return;
    BW.act('POST', `/api/agents/${id}/rank`, { [place]: other }, `${id} ranked ${place} ${other}`);
}
function dropPlace(row, e) { const r = row.getBoundingClientRect(); return e.clientY < r.top + r.height / 2 ? 'above' : 'below'; }
function clearDropMarks() { document.querySelectorAll('.agent-row.drop-above, .agent-row.drop-below, .agent-row.dragging').forEach(r => r.classList.remove('drop-above', 'drop-below', 'dragging')); }
document.addEventListener('dragstart', (e) => {
    const row = e.target.closest && e.target.closest('.agent-row');
    if (!row) return;
    ui.dragging = row.dataset.agent;
    row.classList.add('dragging');
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', row.dataset.agent);
});
document.addEventListener('dragover', (e) => {
    const row = ui.dragging && e.target.closest && e.target.closest('.agent-row');
    if (!row || row.dataset.agent === ui.dragging) return;
    e.preventDefault();
    e.dataTransfer.dropEffect = 'move';
    const place = dropPlace(row, e);
    if (!row.classList.contains('drop-' + place)) {
        document.querySelectorAll('.agent-row.drop-above, .agent-row.drop-below').forEach(r => r.classList.remove('drop-above', 'drop-below'));
        row.classList.add('drop-' + place);
    }
});
document.addEventListener('drop', (e) => {
    const row = ui.dragging && e.target.closest && e.target.closest('.agent-row');
    if (!row) return;
    e.preventDefault();
    rankRelative(ui.dragging, dropPlace(row, e), row.dataset.agent);
});
document.addEventListener('dragend', () => { ui.dragging = null; clearDropMarks(); render(BW.state); });

// Keyboard equivalent: move one place up or down the current roster.
function rankStep(id, dir) {
    const s = BW.state;
    if (!s) return;
    const order = s.agents.slice().sort((a, b) => (b.rank ? b.rank.effective : 0) - (a.rank ? a.rank.effective : 0) || a.id.localeCompare(b.id)).map(a => a.id);
    const i = order.indexOf(id), j = i + dir;
    if (i < 0 || j < 0 || j >= order.length) return;
    rankRelative(id, dir < 0 ? 'above' : 'below', order[j]);
}

function drawerAgent(s, a) {
    const mine = s.intents.filter(i => i.agent_id === a.id);
    const n = (st) => mine.filter(i => i.status === st).length;
    const limits = Object.entries(a.action_limits).map(([k, v]) =>
        `<div class="item">${esc(k)}: ${esc(Object.entries(v).map(([a, b]) => `${a} ${b}`).join(', '))}</div>`).join('') || '–';
    const contained = a.status === 'quarantined' || a.status === 'paused';
    const activity = mine.slice(0, 12).map(i => `<div class="ln"><span class="t">${fmtTs(i.created_at).slice(11)}</span>
        <span class="lv-${i.status === 'executed' ? 'ok' : i.status === 'rejected' ? 'error' : 'warn'}">${esc(i.status)}</span>
        <span>${esc(i.action)} → ${esc(fmtValue(i.params))}${i.reasons.length ? ' · ' + esc(i.reasons[0]) : ''}</span></div>`).join('')
        || '<div class="muted">No intents in the recent window</div>';
    const KIND = { BLOCKED: 'lv-error', MISSED: 'lv-warn', QUARANTINED: 'lv-error' };
    const findings = (a.findings || []).slice().reverse().map(f => `<div class="ln"><span class="t">${fmtTs(f.at).slice(11)}</span>
        <span class="${KIND[f.kind] || ''}">${f.kind === 'MISSED' ? 'REPRIMANDED' : esc(f.kind)}</span>
        <span>${esc(f.what)}<br><b>Why:</b> ${esc(f.reason)} <span class="rule">[${esc(f.rule)}]</span></span></div>`).join('')
        || '<div class="muted">Warden has not blocked, reprimanded or quarantined this agent</div>';
    return `<div class="drawer-head">${badge(a.status)}<span class="crumb">AGENTS / ${esc(a.id)}</span><button class="close" onclick="closeDrawer()">×</button></div>
    <div class="drawer-body">
      <h3>${esc(a.name)}</h3><div class="ids"><span>${esc(a.id)}</span><span>v${esc(a.version)}</span><span>${esc(a.vendor)}</span></div>
      ${a.quarantine_reason ? `<div class="card" style="border-color:var(--red);margin:10px 0"><b style="color:var(--badge-red)">QUARANTINED</b> · ${esc(a.quarantine_reason)}</div>` : ''}
      <div class="kv">
        <div class="k">Role</div><div class="v">${esc(a.role)}</div><div class="k">Autonomy</div><div class="v">${autonomyBadge(a.autonomy_level)}</div>
        <div class="k">Rank</div><div class="v">${rankCell(a.rank)}${a.rank && a.rank.promotion ? `<div class="muted" style="font-size:12px">promoted by ${esc(a.rank.promoted_by)}${a.rank.promotion_left_s != null ? `, ${Math.ceil(a.rank.promotion_left_s / 60)} min left` : ''}</div>` : ''}</div>
        <div class="k">Class</div><div class="v">${esc(a.agent_class || '–')}${a.protocol ? ' ' + badge('PROTOCOL', 'purple') : ''}</div>
        <div class="k">Trust</div><div class="v" style="color:${pctClass(a.trust_score)}">${a.trust_score.toFixed(0)}%</div>
        <div class="k">Read scopes</div><div class="v">${a.read_scopes.map(x => `<div class="item mono">${esc(x)}</div>`).join('')}</div>
        <div class="k">Tools</div><div class="v">${a.tools.map(x => `<div class="item mono">${esc(x)}</div>`).join('')}</div>
        <div class="k">Limits</div><div class="v">${limits}</div>
        <div class="k">Quota</div><div class="v">${esc(Object.entries(a.resource_quota).map(([k, v]) => `${k.replace(/_/g, ' ')} ${v}`).join(', '))}</div>
      </div>
      <div class="divider"></div>
      <div class="desc-title">Description</div><div class="desc">${esc(a.description)}</div>
      ${a.llm ? llmStatus(a.llm) : ''}
      <div class="divider"></div>
      <div class="tiles"><div class="tile"><div class="l">Recent</div><div class="v">${mine.length}</div></div>
        <div class="tile"><div class="l">Executed</div><div class="v">${n('executed')}</div></div>
        <div class="tile"><div class="l">Rejected</div><div class="v">${n('rejected')}</div></div>
        <div class="tile"><div class="l">Escalated</div><div class="v">${n('escalated')}</div></div>
        <div class="tile"><div class="l">Violations</div><div class="v">${a.violations}</div><div class="s">last 60 s</div></div></div>
      <div class="divider"></div>
      <div class="btn-row" style="margin-bottom:8px">
        <button type="button" class="btn" onclick="rankStep('${a.id}', -1)" title="Rank this agent one place higher (sets its base rank)">Move Up</button>
        <button type="button" class="btn" onclick="rankStep('${a.id}', 1)" title="Rank this agent one place lower (sets its base rank)">Move Down</button>
        ${a.rank && a.rank.promotion
            ? `<button type="button" class="btn" onclick="BW.act('POST','/api/agents/${a.id}/demote',{},'${a.id} promotion revoked')">Revoke Field Promotion</button>`
            : `<button type="button" class="btn warn" onclick="BW.act('POST','/api/agents/${a.id}/promote',{},'${a.id} promoted')" title="Field promotion: +rank for a few minutes (policies.json rank.promotion_points / promotion_minutes)">Field Promote</button>`}
      </div>
      <div class="btn-row">
        <button class="btn" onclick="setAutonomy('${a.id}', ${a.autonomy_level - 1})" ${a.autonomy_level === 0 ? 'disabled' : ''}>Downgrade Autonomy</button>
        <button class="btn" onclick="setAutonomy('${a.id}', ${a.autonomy_level + 1})" ${a.autonomy_level === 3 ? 'disabled' : ''}>Upgrade Autonomy</button>
        <button class="btn" onclick="BW.act('POST','/api/control/rollback',{agent_id:'${a.id}',count:1},'Rolled back last ${a.id} action')">Rollback Last</button>
        ${contained ? `<button class="btn success" onclick="BW.act('POST','/api/agents/${a.id}/resume',null,'${a.id} released')">${a.status === 'quarantined' ? 'Release' : 'Resume'}</button>`
            : `<button class="btn" onclick="BW.act('POST','/api/agents/${a.id}/pause',null,'${a.id} paused')">Pause</button>`}
        <button class="btn danger" onclick="BW.act('POST','/api/agents/${a.id}/quarantine',null,'${a.id} quarantined')" ${a.status === 'quarantined' ? 'disabled' : ''}>Quarantine</button>
      </div>
      <div class="divider"></div>
      <div class="label neutral">Warden Findings · why it acted</div><div class="inset log findings">${findings}</div>
      <div class="divider"></div>
      <div class="label neutral">Activity</div><div class="inset log">${activity}</div>
    </div>`;
}
function llmStatus(l) {
    const state = !l.enabled ? ['OFF', 'grey'] : l.thinking ? ['THINKING', 'yellow'] : l.last_ok === null ? ['WAITING', 'grey']
        : l.last_ok ? ['CONNECTED', 'green'] : ['UNREACHABLE', 'red'];
    if ((l.model || "").startsWith("C# lookout")) return `<div class="divider"></div><div class="desc-title">Lookout ${badge("C#, no model call", "blue")}</div>
      <div class="kv" style="margin-top:8px"><div class="k">Scan time</div><div class="v">${l.last_latency_ms === null ? "–" : l.last_latency_ms + " ms"}</div>
        <div class="k">Report</div><div class="v mono" style="grid-column:span 3">${esc((l.last_actions || []).join(" · ") || "–")}</div></div>`;
    return `<div class="divider"></div><div class="desc-title">Language Model ${badge(state[0], state[1])}</div>
      <div class="kv" style="margin-top:8px">
        <div class="k">Endpoint</div><div class="v mono">${esc(l.endpoint)}</div><div class="k">Model</div><div class="v mono">${esc(l.model || '–')}</div>
        <div class="k">Calls</div><div class="v">${l.calls}</div><div class="k">Last latency</div><div class="v">${l.last_latency_ms === null ? '–' : (l.last_latency_ms / 1000).toFixed(1) + ' s'}</div>
        <div class="k">Last answer</div><div class="v mono" style="grid-column:span 3">${esc((l.last_actions || []).join(' · ') || '–')}</div>
        ${l.last_error ? `<div class="k">Problem</div><div class="v" style="grid-column:span 3;color:var(--badge-red)">${esc(l.last_error)}</div>` : ''}
      </div>`;
}

function setAutonomy(id, level) { BW.act('PATCH', `/api/agents/${id}/autonomy`, { level }, `Autonomy set to ${AUTONOMY[level][0]}`); }

// ---------------------------------------------------------------- intents
const INTENT_COLS = '90px 1.1fr 1fr .8fr .9fr 1fr 2fr';
function pageIntents(s) {
    const rows = s.intents.map(i => `<div class="trow ${ui.intent === i.id ? 'selected' : ''}" style="grid-template-columns:${INTENT_COLS}" onclick="selectIntent('${i.id}')">
        <div class="mono sec">${fmtSim(i.sim_time)}</div><div class="primary">${esc(i.agent_id)}</div>
        <div class="mono">${esc(i.action)}</div><div class="mono">${esc(fmtValue(i.params))}</div>
        <div class="sec">${esc(i.target_resource)}</div><div>${badge(i.status)}</div>
        <div class="sec" style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis">${esc(i.reasons[0] || i.objective)}</div></div>`).join('')
        || '<div class="empty">No intents yet. Start the scenario from Warden Control.</div>';
    return `<div class="toolbar"><span class="page-title">Intents</span><span class="muted">Every action an agent asked for, and what Warden decided.</span></div>
      <div class="table"><div class="thead" style="grid-template-columns:${INTENT_COLS}"><span>Sim Time</span><span>Agent</span><span>Action</span>
      <span>Value</span><span>Target</span><span>Status</span><span>Reason / Objective</span></div>${rows}</div>`;
}
// Proof the decision came from the language model, and how it calculated.
function provenancePanel(p) {
    if (!p || !p.call_id) return `<div class="divider"></div><div class="desc-title">Decision Provenance ${badge('none', 'red')}</div>
        <div class="desc muted">No LLM provenance. Warden rejects agent actions like this.</div>`;
    const tools = (p.tools || []).map((t, n) => `<div class="ln" style="grid-template-columns:22px 1fr"><span class="muted">${n + 1}</span>
        <span><b>${esc(t.tool)}</b> <span class="muted">${esc(JSON.stringify(t.args))}</span><br>→ <span class="lv-ok">${esc(JSON.stringify(t.result))}</span></span></div>`).join('');
    // A decider's choice (NavCell.cs): the lookout's report and the option the model picked, or a reflex when it was late.
    const head = p.reflex ? badge('REFLEX: no model answer in time', 'yellow') : badge('LLM ✓ verified', 'green');
    const chosen = p.cue ? `<div class="label neutral">Lookout report</div><div class="calc">${esc(p.cue)}</div>
      <div class="label neutral" style="margin-top:10px">${p.reflex ? 'Doctrine option applied' : 'Option the model chose'}</div><div class="calc">${esc(p.option || '–')}</div>` : '';
    return `<div class="divider"></div><div class="desc-title">Decision Provenance ${head}</div>
      <div class="kv" style="margin:8px 0">
        <div class="k">Decided by</div><div class="v mono">${esc(p.decided_by || '–')}</div><div class="k">Model</div><div class="v mono">${esc(p.model)}</div>
        <div class="k">Call ID</div><div class="v mono">${esc(p.call_id)}</div><div class="k">Latency</div><div class="v">${((p.latency_ms || 0) / 1000).toFixed(1)} s</div>
        <div class="k">Reply SHA-256</div><div class="v mono" style="grid-column:span 3;font-size:12px">${esc(p.reply_sha256)}</div>
      </div>
      ${chosen}
      ${tools ? `<div class="label neutral" style="margin-top:10px">Calculator calls (chosen by the model)</div><div class="inset log">${tools}</div>` : ''}
      <div class="label neutral" style="margin-top:10px">${p.cue ? "Model's reason" : "Model's calculations"}</div><div class="calc">${esc(p.calculations || '–')}</div>
      <details style="margin-top:8px"><summary class="muted" style="cursor:pointer">Raw model reply</summary><pre class="inset" style="white-space:pre-wrap;font-size:13px">${esc(p.raw_reply)}</pre></details>`;
}

function selectIntent(id) { ui.intent = ui.intent === id ? null : id; ui.agent = null; render(BW.state); }

function stepper(pipeline) {
    const keys = Object.keys(pipeline);
    return `<div class="stepper">${keys.map((k, i) => `<div class="step"><span class="st s-${pipeline[k]}" style="border:0">${pipeline[k]}</span>
        <span class="ring s-${pipeline[k]}">${i + 1}</span><span class="nm">${STAGE_SHORT[k]}</span></div>${i < keys.length - 1 ? '<div class="step-conn"></div>' : ''}`).join('')}</div>`;
}

function drawerIntent(s, i) {
    const cfs = s.conflicts.filter(c => i.conflict_ids.includes(c.id));
    return `<div class="drawer-head">${badge(i.status)}<span class="crumb">INTENTS / ${esc(i.id)}</span><button class="close" onclick="closeDrawer()">×</button></div>
    <div class="drawer-body">
      <h3>${esc(i.action)} → ${esc(fmtValue(i.params))}</h3><div class="ids"><span>${esc(i.id)}</span><span>${esc(i.agent_id)}</span><span>sim ${fmtSim(i.sim_time)}</span></div>
      <div class="label neutral">Governance Pipeline</div>${stepper(i.pipeline)}
      <div class="divider"></div>
      <div class="kv">
        <div class="k">Objective</div><div class="v">${esc(i.objective)}</div><div class="k">Target</div><div class="v mono">${esc(i.target_resource)}</div>
        <div class="k">Confidence</div><div class="v">${Math.round(i.confidence * 100)}%</div><div class="k">Kind</div><div class="v">${esc(i.kind)}</div>
        <div class="k">Reversible</div><div class="v">${i.reversible ? 'Yes' : 'No'}</div><div class="k">Inverse</div><div class="v mono">${i.inverse ? esc(fmtValue(i.inverse)) : '–'}</div>
        <div class="k">Decided by</div><div class="v">${esc(i.decided_by || 'pending')}</div><div class="k">Policy hits</div><div class="v">${i.policy_hits.map(p => badge(p, 'purple')).join(' ') || '–'}</div>
      </div>
      <div class="divider"></div>
      <div class="desc-title">Agent Rationale</div><div class="desc">${esc(i.rationale)}</div>
      ${i.reasons.length ? `<div class="divider"></div><div class="desc-title">Warden Decision</div><div class="desc">${i.reasons.map(esc).join('<br>')}</div>` : ''}
      ${cfs.length ? `<div class="divider"></div><div class="label neutral">Linked Conflicts</div>${cfs.map(conflictCard).join('')}` : ''}
      ${provenancePanel(i.provenance)}
      <div class="divider"></div>
      <div class="desc-title">Explanation <button class="btn" style="margin-left:8px" onclick="explainIntent('${i.id}')">Explain</button></div>
      <div class="desc">${explainBody(i.id)}</div>
    </div>`;
}

// ---------------------------------------------------------------- conflicts & escalations
function conflictCard(c) {
    return `<div class="card"><div class="top">${badge(c.type)}${badge(c.severity)}<span class="ttl">${esc(c.target_resource)}: ${esc(c.agent_ids.join(' vs '))}</span>
      <span class="meta">${esc(c.id)} · ×${c.count} · ${fmtTs(c.updated_at).slice(11)}</span></div>
      <div class="body">${esc(c.summary)} ${sourceTag(c.summary_source)}</div>${c.resolution ? `<div class="res">✓ ${esc(c.resolution)}</div>` : ''}</div>`;
}
function pageConflicts(s) {
    // Leases: who holds each contested actuator, at what rank, until when. A higher rank takes the lease.
    const name = (id) => { const a = s.agents.find(x => x.id === id); return a ? a.name : id; };
    const leases = Object.entries(s.holds || {}).map(([res, h]) => `<div class="trow" style="grid-template-columns:${LEASE_COLS}">
        <div class="mono">${esc(res)}</div><div><div class="primary">${esc(name(h.agent_id))}</div><div class="id">${esc(h.agent_id)}</div></div>
        <div class="mono">${esc(fmtValue(res === 'rudder' ? { heading: h.value } : { speed_kn: h.value }))}</div>
        <div class="right mono">${esc(String(h.priority))}</div><div class="right mono">${h.until == null ? '–' : h.until.toFixed(0) + ' s'}</div></div>`).join('');
    return `<div class="toolbar"><span class="page-title">Conflicts</span><span class="muted">Where agents' recommendations, objectives or resource demands clash. Repeats are folded (×count).</span></div>
      <div class="label neutral">Resource leases · contested actuators</div>
      <div class="table" style="margin-bottom:18px"><div class="thead" style="grid-template-columns:${LEASE_COLS}"><span>Resource</span><span>Held by</span><span>Value</span>
        <span class="right">Rank</span><span class="right">Lease left</span></div>${leases || '<div class="empty">No actuator is leased right now</div>'}</div>
      ${s.conflicts.map(conflictCard).join('') || '<div class="empty">No conflicts detected</div>'}`;
}
const LEASE_COLS = '1fr 2fr 1fr .7fr .8fr';

function pageEscalations(s) {
    const cards = s.escalations.map(e => {
        const open = e.status === 'open';
        const pct = Math.max(0, e.remaining_s / e.timeout_s * 100);
        return `<div class="card ${open ? 'attn' : ''}"><div class="top">${badge(e.status)}<span class="ttl">${esc(e.title)}</span>
          <span class="meta">${esc(e.id)}${open ? ` · ${e.remaining_s.toFixed(0)} s left` : ` · ${esc(e.decided_by)}`}</span></div>
          <div class="body">${esc(e.summary)} ${sourceTag(e.summary_source)}</div>
          ${open ? `<div class="countdown"><div style="width:${pct}%"></div></div>
          <div class="muted" style="margin-bottom:10px">Safe default in ${e.remaining_s.toFixed(0)} s → ${esc(e.safe_default)}</div>
          <div class="btn-row"><button class="btn success lg" onclick="decide('${e.id}','approve')">${esc(e.approve_label)}</button>
          <button class="btn danger lg" onclick="decide('${e.id}','reject')">${esc(e.reject_label)}</button></div>` : ''}</div>`;
    }).join('');
    return `<div class="toolbar"><span class="page-title">Escalations</span><span class="muted">Decisions only a human may make. No decision before the timer runs out → the safe default.</span></div>
      ${cards || '<div class="empty">No escalations</div>'}`;
}
function decide(id, d) { NotifyWatch.decide(id, d === 'approve'); }

// ---------------------------------------------------------------- ledger
async function loadLedger() {
    ui.ledger = await BW.api('GET', '/api/ledger?limit=300');
    render(BW.state);
}
async function verifyLedger() {
    const r = await BW.act('GET', '/api/ledger/verify');
    if (r) BW.toast(r.valid ? `Chain valid · ${r.rows} rows · tamper-evident` : `Chain BROKEN at row ${r.broken_at}`);
}
function pageLedger() {
    const rows = (ui.ledger || []).slice().reverse().map(r => {
        const lv = r.type === 'ALERT' ? 'error' : r.type === 'EXECUTED' ? 'ok' : r.type === 'CONTROL' ? 'system' : r.type === 'DECISION' ? 'warn' : 'engine';
        return `<div class="ln" style="grid-template-columns:60px 150px 140px 110px 1fr"><span class="muted">#${r.seq}</span><span class="t">${fmtTs(r.ts)}</span>
          <span class="lv-${lv}">${esc(r.type)}</span><span>${esc(r.actor)}</span><span class="muted">${esc(JSON.stringify(r.payload))}</span></div>`;
    }).join('');
    return `<div class="toolbar"><span class="page-title">Audit Ledger</span><span class="muted">Append-only, hash-chained. Editing any row breaks the chain.</span>
      <span class="spacer"></span><button class="btn" onclick="loadLedger()">Refresh</button><button class="btn primary" onclick="verifyLedger()">Verify Chain</button></div>
      <div class="log"><div class="hdr" style="grid-template-columns:60px 150px 140px 110px 1fr"><span>Seq</span><span>Timestamp</span><span>Type</span><span>Actor</span><span>Payload</span></div>
      ${rows || '<div class="empty">Loading…</div>'}</div>`;
}

// ---------------------------------------------------------------- drawer
function renderDrawer(s) {
    const d = $('drawer');
    const a = ui.agent && s.agents.find(x => x.id === ui.agent);
    const i = ui.intent && s.intents.find(x => x.id === ui.intent);
    const html = a ? drawerAgent(s, a) : i ? drawerIntent(s, i) : '';
    const top = d.scrollTop;
    setHtml(d, html);
    d.scrollTop = top;
    d.classList.toggle('open', !!html);
}
function closeDrawer() { ui.agent = ui.intent = null; render(BW.state); }
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') closeDrawer(); });

function reviewDecisions() {
    if (!PAGE_DOMAIN[ui.page]) go('escalations');        // off a COP: the full inbox
    setTimeout(() => document.getElementById('decisions')?.scrollIntoView({ behavior: 'smooth', block: 'center' }), 50);
}
Notify.setScope(PAGE_DOMAIN[ui.page] || null);
BW.api('POST', `/api/view/${PAGE_DOMAIN[ui.page] || 'nav'}`).catch(() => {});
BW.onState((s) => { render(s); NotifyWatch.update(s, reviewDecisions, false); });   // decisions live in the right column
BW.connect();
// Deep link: index.html?page=systems opens that page (used for screenshots and second monitors).
{ const p = new URLSearchParams(location.search).get('page'); if (p && document.querySelector(`.nav-item[data-page="${p}"]`)) go(p); }
