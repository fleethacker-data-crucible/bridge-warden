// The three operating pictures: Navigation (20 nm), Ship Systems (power plant graph), Damage Control (ship profile).
// Every picture carries the same Decisions panel, so the watch can act without switching screens.

// Each COP shows only its own decisions plus ship-wide ones (system checks).
function decisionsBlock(s, domain) {
    const open = s.escalations.filter(x => x.status === 'open' && (!domain || x.domain === domain || x.domain === 'global'));
    return `<div id="decisions">${open.length ? `<div class="label" style="color:var(--badge-yellow)">Decisions Needed (${open.length})</div>
        ${open.map(decisionCard).join('')}<div class="divider"></div>` : ''}</div>`;
}

// What the watch knows: tracks CIC has classified, never simulation ground truth.
const classified = (sys) => sys.hostiles.filter(h => ['SUSPECT', 'HOSTILE'].includes(h.classification));
// Track colors on the Navigation COP: hostile red, unknown/suspect orange, normal traffic green.
const TRACK_COLOR = { HOSTILE: 'var(--badge-red)', SUSPECT: 'var(--orange)', UNKNOWN: 'var(--orange)',
                      MERCHANT: 'var(--badge-green)', FISHING: 'var(--badge-green)' };
const pct = (v) => `${Math.round(v || 0)}%`;
const riskColor = (v) => v >= 75 ? 'var(--badge-red)' : v >= 40 ? 'var(--badge-yellow)' : 'var(--badge-green)';

// ------------------------------------------------------------------ Navigation COP (20 nm)
// overlay: controls drawn on top of the plot (positioned against the plot, not the legend under it).
function navCopSvg(cop, overlay = '') {
    const R = 20, own = cop.ownship, sys = cop.systems;
    const P = (x, y) => [(x - own.x), -(y - own.y)];
    const vec = (h, len) => [Math.sin(h * Math.PI / 180) * len, Math.cos(h * Math.PI / 180) * len];
    const f = (n) => n.toFixed(3);
    const RED = '#ff4d4d', BLUE = 'var(--badge-blue)';
    const S = 0.75;   // ship symbol size (own ship and tracks)
    // Labels flip to the left near the right edge, so they stay inside the scope.
    const L = (x, dy, size, col, text, w = 700) => `<text x="${f(x > 6 ? x - 0.9 : x + 0.9)}" y="${f(dy)}" text-anchor="${x > 6 ? 'end' : 'start'}" style="font-size:${size}px;fill:${col};font-weight:${w}">${text}</text>`;
    let g = '';
    // Weather radar: soft rain cells under everything.
    for (const c of cop.environment.cells || []) {
        const [x, y] = P(c.x, c.y);
        const col = c.intensity >= 0.75 ? '229,72,77' : c.intensity >= 0.55 ? '226,179,60' : '63,174,106';
        g += `<circle cx="${f(x)}" cy="${f(y)}" r="${c.r}" fill="rgba(${col},${(0.12 + c.intensity * 0.2).toFixed(2)})"/>`;
    }
    for (const r of [5, 10, 15, 20]) {
        g += `<circle r="${r}" fill="none" stroke="var(--border)" stroke-width="0.05"/>`;
        g += `<text x="0.3" y="${-r + 0.9}" style="font-size:0.7px;fill:var(--text-dim)">${r} nm</text>`;
    }
    // EMCON radius: hard line. Red when hostiles are around and we're still radiating.
    const hostileNear = sys.hostiles.length > 0;
    const emCol = sys.emcon ? 'var(--badge-green)' : hostileNear ? 'var(--badge-red)' : 'var(--badge-yellow)';
    g += `<circle r="${sys.emcon_radius_nm}" fill="${sys.emcon ? 'none' : hostileNear ? 'rgba(255,107,107,.06)' : 'none'}" stroke="${emCol}" stroke-width="0.14" stroke-dasharray="0.6 0.35"/>`;
    g += `<text x="0" y="${sys.emcon_radius_nm + 1.2}" text-anchor="middle" style="font-size:0.85px;font-weight:700;fill:${emCol}">EMCON ${sys.emcon_radius_nm} nm · ${sys.emcon ? 'SET' : 'NOT SET (RADIATING)'}</text>`;
    // Proposed course from an agent waiting on the operator: dashed yellow.
    for (const e of (BW.state?.escalations || []).filter(x => x.status === 'open' && x.proposal?.heading !== undefined)) {
        const [px, py] = vec(e.proposal.heading, 9);
        g += `<line x1="0" y1="0" x2="${f(px)}" y2="${f(-py)}" stroke="var(--badge-yellow)" stroke-width="0.14" stroke-dasharray="0.5 0.3"/>`;
        g += `<text x="${f(px * 1.08)}" y="${f(-py * 1.08)}" text-anchor="middle" style="font-size:0.9px;fill:var(--badge-yellow);font-weight:700">${esc(e.agent_id)} wants ${esc(e.proposal.value)}</text>`;
    }
    const [hx, hy] = vec(own.heading, Math.max(1.5, own.speed / 10 * 2));
    g += `<line x1="0" y1="0" x2="${f(hx)}" y2="${f(-hy)}" stroke="var(--text)" stroke-width="0.12"/>`;
    g += `<polygon points="0,-0.9 0.55,0.6 -0.55,0.6" fill="var(--blue)" transform="rotate(${own.heading}) scale(${S * 0.7})"/>`;
    for (const c of cop.contacts) {
        const [x, y] = P(c.x, c.y);
        const col = TRACK_COLOR[c.classification] || TRACK_COLOR.UNKNOWN;
        const [vx, vy] = vec(c.course, c.speed / 10 * 1.5);
        g += `<line x1="${f(x)}" y1="${f(y)}" x2="${f(x + vx)}" y2="${f(y - vy)}" stroke="${col}" stroke-width="0.1"/>`;
        if (c.domain === 'subsurface') {           // submarine: open bowl (subsurface symbol)
            g += `<path d="M${f(x - 0.7 * S)},${f(y - 0.2 * S)} A${0.7 * S},${0.7 * S} 0 0 0 ${f(x + 0.7 * S)},${f(y - 0.2 * S)}" fill="none" stroke="${col}" stroke-width="${0.22 * S}"/>`;
        } else if (c.kind === 'hostile') {
            g += `<polygon points="${f(x)},${f(y - 0.65 * S)} ${f(x + 0.65 * S)},${f(y)} ${f(x)},${f(y + 0.65 * S)} ${f(x - 0.65 * S)},${f(y)}" fill="${col}"/>`;
        } else {
            g += `<rect x="${f(x - 0.3 * S)}" y="${f(y - 0.3 * S)}" width="${0.6 * S}" height="${0.6 * S}" fill="${col}"/>`;
            if (c.cpa_nm < 1.0 && c.tcpa_min >= 0 && c.tcpa_min <= 30) g += `<circle cx="${f(x)}" cy="${f(y)}" r="1.1" fill="none" stroke="var(--badge-red)" stroke-width="0.1"/>`;
        }
        const big = c.kind === 'hostile' || c.cpa_nm < 1.0;
        g += L(x, y - 0.5, big ? 0.9 : 0.7, col, `${esc(c.id)}${c.kind === 'hostile' ? ' ' + c.classification : ''}`, big ? 700 : 400);
    }
    // Missiles in the air: red arrowheads pointing at us, with seconds to impact.
    let k = 0;            // missiles fly in pairs on one bearing: stack their labels
    for (const m of sys.inbound) {
        k++;
        const brg = m.bearing ?? 45, [x, y] = vec(brg, m.range);
        g += `<polygon points="0,-0.7 0.45,0.5 -0.45,0.5" fill="${RED}" stroke="#fff" stroke-width="0.06" transform="translate(${f(x)},${f(-y)}) rotate(${brg + 180})"/>`;
        g += L(x, -y + 0.2 + k * 0.95, 0.85, RED, `${esc(m.id)} ${m.eta_s}s${m.engaged_by?.length ? ' · engaged' : ''}`);
    }
    // Torpedoes in the water: red dashed run line and a torpedo body.
    for (const t of sys.torpedoes || []) {
        const [x, y] = vec(t.bearing, t.range);
        g += `<line x1="${f(x)}" y1="${f(-y)}" x2="0" y2="0" stroke="${RED}" stroke-width="0.08" stroke-dasharray="0.3 0.3" opacity=".6"/>`;
        g += `<ellipse cx="${f(x)}" cy="${f(-y)}" rx="0.2" ry="0.6" fill="${RED}" stroke="#fff" stroke-width="0.05" transform="rotate(${t.bearing} ${f(x)} ${f(-y)})"/>`;
        g += L(x, -y + 1.1, 0.85, RED, `TORPEDO ${t.range.toFixed(1)} nm · fuel ${t.run_left.toFixed(1)} nm`);
    }
    // Our weapons: blue arrowheads flying out.
    for (const i of sys.interceptors || []) {
        const brg = i.bearing ?? 45, [x, y] = vec(brg, Math.max(0.4, i.range));
        g += `<line x1="0" y1="0" x2="${f(x)}" y2="${f(-y)}" stroke="${BLUE}" stroke-width="0.05" stroke-dasharray="0.15 0.15" opacity=".6"/>`;
        g += `<polygon points="0,-0.55 0.35,0.4 -0.35,0.4" fill="${BLUE}" stroke="#fff" stroke-width="0.05" transform="translate(${f(x)},${f(-y)}) rotate(${brg})"/>`;
        g += `<text x="${f(x + 0.6)}" y="${f(-y - 0.4)}" style="font-size:0.75px;fill:${BLUE};font-weight:700">${esc(i.weapon || 'SM-2')}</text>`;
    }
    for (const sp of sys.splashes || []) {
        const [x, y] = vec(sp.bearing ?? 45, Math.max(0.6, sp.range));
        const col = ['SPLASH', 'KILL'].includes(sp.label) ? 'var(--badge-green)' : ['SEDUCED', 'OUT OF FUEL', 'ABORTED'].includes(sp.label) ? BLUE : 'var(--badge-yellow)';
        g += `<g opacity="${Math.max(0.15, 1 - sp.age_s / 25).toFixed(2)}"><path d="M${f(x - 0.45)},${f(-y - 0.45)} L${f(x + 0.45)},${f(-y + 0.45)} M${f(x + 0.45)},${f(-y - 0.45)} L${f(x - 0.45)},${f(-y + 0.45)}" stroke="${col}" stroke-width="0.14"/>
          <text x="${f(x + 0.7)}" y="${f(-y - 0.5)}" style="font-size:0.8px;font-weight:700;fill:${col}">${sp.label}</text></g>`;
    }
    return `<div class="cop-frame"><svg class="cop nav-cop" viewBox="${-R} ${-R} ${2 * R} ${2 * R}">${g}</svg>${overlay}</div>
      <div class="legend-row"><span><i style="background:${RED}"></i>missile · torpedo · hostile</span><span><i style="background:var(--orange)"></i>suspect · unknown</span>
      <span><i style="background:var(--badge-green)"></i>traffic</span><span><i style="background:${BLUE}"></i>our weapons</span><span>◡ submarine</span><span>┄ EMCON ${sys.emcon_radius_nm} nm</span></div>`;
}

// One line of plain facts above each picture: what a watch officer glances at.
function pictureLine(s, cop) {
    const o = s.cop.ownship, sys = s.cop.systems, e = s.cop.environment;
    const hostile = s.cop.contacts.filter(c => c.classification === 'HOSTILE').length;
    const torps = (sys.torpedoes || []).length;
    const items = {
        nav: [`Course ${String(Math.round(o.heading)).padStart(3, '0')}° · ${o.speed.toFixed(0)} kn`,
            `${s.cop.contacts.length} tracks${hostile ? `, <b class="red">${hostile} hostile</b>` : ''}`,
            sys.inbound.length ? `<b class="red">${sys.inbound.length} missile${sys.inbound.length > 1 ? 's' : ''} inbound</b>` : null,
            torps ? `<b class="red">${torps} torpedo${torps > 1 ? 'es' : ''}</b>` : null,
            `EMCON ${sys.emcon ? 'set' : 'off'}`, sys.gq ? '<b class="red">GQ</b>' : 'Cond III',
            `wind ${e.wind_kts.toFixed(0)} kts · waves ${e.wave_height_m.toFixed(1)} m`, `SM-2 ${sys.magazine}`,
            sys.hits ? `<b class="red">${sys.hits} hit${sys.hits > 1 ? 's' : ''} taken</b>` : null],
        systems: [`${sys.nodes.filter(n => n.failed).length} failed`,
            `${sys.nodes.filter(n => !n.failed && !n.powered && n.type !== 'gen').length} without power`,
            Object.entries(sys.gen_load).map(([gn, p]) => `${gn} ${p}%`).join(' · ') || 'no generator on line',
            sys.propulsion_ok ? `max ${sys.max_speed_kn} kn` : '<b class="red">no propulsion</b>',
            sys.steering_ok ? 'steering ok' : '<b class="red">steering lost</b>'],
        dc: [sys.gq ? '<b class="red">General Quarters</b>' : 'Condition III',
            `${sys.casualties.filter(c => !['out', 'repaired'].includes(c.status)).length} open casualties`,
            `${sys.teams.filter(t => t.status === 'standby').length}/${sys.teams.length} parties ready`,
            sys.firemain_ok ? 'firemain 150 psi' : '<b class="red">firemain lost</b>'],
    }[cop].filter(Boolean);
    return `<div class="picture-line">${items.join('<span class="sep">·</span>')}</div>`;
}

// ------------------------------------------------------------------ Ship Systems COP
const NODE_W = 150, NODE_H = 34;
function systemsSvg(sys) {
    const N = Object.fromEntries(sys.nodes.map(n => [n.id, n]));
    let g = '';
    for (const [x, t] of [[90, 'GENERATION'], [310, 'SWITCHBOARDS'], [530, 'LOAD CENTERS'], [770, 'LOADS']]) {
        g += `<text x="${x}" y="-6" style="font-size:11px;font-weight:700;letter-spacing:.08em;fill:var(--text-muted)">${t}</text>`;
    }
    for (const b of sys.breakers) {
        const a = N[b.a], z = N[b.b];
        const x1 = a.x + NODE_W, y1 = a.y + NODE_H / 2, x2 = z.x, y2 = z.y + NODE_H / 2;
        const live = b.closed && a.powered && z.powered && !a.failed && !z.failed;
        const rerouted = b.closed && !b.normal;
        const col = !b.closed ? 'var(--state-off)' : rerouted ? 'var(--blue)' : live ? 'var(--badge-green)' : 'var(--border-strong)';
        const mx = (x1 + x2) / 2, my = (y1 + y2) / 2;
        const path = x2 > x1 ? `M${x1},${y1} C${mx},${y1} ${mx},${y2} ${x2},${y2}` : `M${a.x + NODE_W / 2},${a.y + NODE_H} L${z.x + NODE_W / 2},${z.y}`;
        g += `<path d="${path}" fill="none" stroke="${col}" stroke-width="${rerouted ? 3 : b.closed ? 2 : 1.5}" ${b.closed ? '' : 'stroke-dasharray="5 4"'}/>`;
        if (!b.id.startsWith('F-')) {
            g += `<rect x="${mx - 22}" y="${my - 8}" width="44" height="16" rx="3" fill="var(--bg-inset)" stroke="${col}"/>`;
            g += `<text x="${mx}" y="${my + 4}" text-anchor="middle" style="font-size:9.5px;font-family:var(--font-mono);fill:${col}">${esc(b.id)}${b.closed ? '' : ' ○'}</text>`;
        }
    }
    for (const n of sys.nodes) {
        const state = n.failed ? 'FAILED' : n.active ? 'ACTIVE' : n.powered ? (n.type === 'gen' ? 'ONLINE' : 'POWERED') : 'NO POWER';
        const col = n.failed ? 'var(--badge-red)' : n.active ? 'var(--blue)' : n.powered ? 'var(--badge-green)' : 'var(--text-dim)';
        const fill = n.failed ? 'rgba(255,107,107,.18)' : n.active ? 'rgba(59,130,246,.18)' : 'var(--bg-row)';
        g += `<g><title>${esc(n.label)} · ${n.comp}${n.fail_reason ? ' · ' + esc(n.fail_reason) : ''}</title>
          <rect x="${n.x}" y="${n.y}" width="${NODE_W}" height="${NODE_H}" rx="5" fill="${fill}" stroke="${col}" stroke-width="${n.failed || n.active ? 2 : 1}"/>
          <rect x="${n.x}" y="${n.y}" width="4" height="${NODE_H}" rx="2" fill="${col}"/>
          <text x="${n.x + 10}" y="${n.y + 14}" style="font-size:11.5px;font-weight:700;fill:var(--text)">${esc(n.id)}</text>
          <text x="${n.x + NODE_W - 6}" y="${n.y + 14}" text-anchor="end" style="font-size:9px;font-weight:700;fill:${(sys.gen_load[n.id] || 0) > 100 ? 'var(--badge-red)' : col}">${n.type === 'gen' && sys.gen_load[n.id] !== undefined ? `${sys.gen_load[n.id]}% LOAD` : state}</text>
          <text x="${n.x + 10}" y="${n.y + 27}" style="font-size:9.5px;fill:var(--text-muted)">${esc(n.label)} · ${n.comp}</text></g>`;
    }
    return `<svg class="syscop" viewBox="70 -24 870 640">${g}</svg>`;
}

function pageSystems(s) {
    return `<div class="toolbar"><span class="page-title">Ship Systems</span><span class="muted">Electrical plant. Dashed = open breaker · blue = re-routed by an agent.</span></div>
      ${oversightBar(s, 'systems')}
      <div class="overview">
        <div>${pictureLine(s, 'systems')}<div class="inset-plain">${systemsSvg(s.cop.systems)}</div></div>
        <div>${oversightColumn(s, 'systems')}</div>
      </div>`;
}

// ------------------------------------------------------------------ Damage Control COP
// DDG-51 side profile (bow left). Frames ~ feet from the bow, 505 ft LOA. Stylized from public photos.
const FX = (fr) => 30 + fr * 1.86;
const DECK = (fr) => 104 + Math.min(fr, 250) * 0.05;          // sheer: deck rises toward the bow
const HULL = () => {
    const pts = [[FX(0) - 14, 92], [FX(60), DECK(60)], [FX(250), DECK(250)], [FX(505), DECK(250) + 2],
        [FX(505), 196], [FX(470), 206], [FX(60), 210], [FX(18), 206], [FX(4), 190]];
    return 'M' + pts.map(p => p.join(',')).join(' L') + ' Z';
};
function dcSvg(sys) {
    const W = 1000;
    let g = `<defs><clipPath id="hullclip"><path d="${HULL()}"/></clipPath></defs>`;
    // Waterline and sea.
    g += `<rect x="0" y="186" width="${W}" height="40" fill="rgba(59,130,246,.07)"/>`;
    g += `<line x1="0" y1="186" x2="${W}" y2="186" stroke="rgba(90,162,255,.35)" stroke-dasharray="6 5"/>`;
    // Superstructure, stacks, mast, weapons (outline only).
    const ss = 'fill="var(--bg-tile)" stroke="var(--border-strong)" stroke-width="1.5"';
    g += `<path d="M${FX(128)},${DECK(128)} L${FX(140)},62 L${FX(205)},62 L${FX(215)},${DECK(215)} Z" ${ss}/>`;          // fwd deckhouse
    g += `<path d="M${FX(150)},62 L${FX(160)},44 L${FX(196)},44 L${FX(200)},62 Z" ${ss}/>`;                                  // pilot house
    g += `<polygon points="${FX(139)},66 ${FX(148)},62 ${FX(148)},84 ${FX(139)},88" fill="rgba(90,162,255,.25)" stroke="var(--badge-blue)"/>`; // SPY-1D face
    g += `<path d="M${FX(176)},44 L${FX(183)},6 L${FX(190)},44" fill="none" stroke="var(--border-strong)" stroke-width="2"/>`;                // mast
    g += `<line x1="${FX(172)}" y1="20" x2="${FX(194)}" y2="20" stroke="var(--border-strong)" stroke-width="2"/>`;
    g += `<path d="M${FX(238)},${DECK(238)} L${FX(242)},48 L${FX(262)},48 L${FX(266)},${DECK(266)} Z" ${ss}/>`;          // fwd stack
    g += `<path d="M${FX(318)},${DECK(318)} L${FX(322)},48 L${FX(342)},48 L${FX(346)},${DECK(346)} Z" ${ss}/>`;          // aft stack
    g += `<path d="M${FX(352)},${DECK(352)} L${FX(358)},74 L${FX(398)},74 L${FX(404)},${DECK(404)} Z" ${ss}/>`;          // aft deckhouse
    g += `<polygon points="${FX(396)},78 ${FX(404)},82 ${FX(404)},${DECK(404) - 4} ${FX(396)},${DECK(396) - 2}" fill="rgba(90,162,255,.25)" stroke="var(--badge-blue)"/>`; // aft SPY face
    g += `<rect x="${FX(36)}" y="${DECK(36) - 9}" width="${FX(52) - FX(36)}" height="9" rx="3" ${ss}/><line x1="${FX(40)}" y1="${DECK(40) - 5}" x2="${FX(18)}" y2="${DECK(18) - 9}" stroke="var(--border-strong)" stroke-width="2"/>`; // Mk 45 gun
    g += `<rect x="${FX(72)}" y="${DECK(72) - 3}" width="${FX(118) - FX(72)}" height="3" fill="var(--state-off)"/>`;   // fwd VLS hatches
    g += `<rect x="${FX(384)}" y="${DECK(384) - 3}" width="${FX(432) - FX(384)}" height="3" fill="var(--state-off)"/>`; // aft VLS hatches
    g += `<line x1="${FX(445)}" y1="${DECK(445) - 1}" x2="${FX(505)}" y2="${DECK(505) - 1}" stroke="var(--badge-yellow)" stroke-width="1" stroke-dasharray="3 3" opacity=".6"/>`; // flight deck
    // Hull.
    g += `<path d="${HULL()}" fill="var(--bg-inset)" stroke="var(--text-muted)" stroke-width="2"/>`;
    g += `<ellipse cx="${FX(14)}" cy="210" rx="24" ry="8" fill="var(--bg-inset)" stroke="var(--text-muted)" stroke-width="1.5"/>`;  // sonar dome
    // Compartments inside the hull (clipped to the hull shape).
    let inner = '';
    for (const c of sys.compartments) {
        const cas = sys.casualties.find(k => k.comp === c.id && !['out', 'repaired'].includes(k.status));
        const x = FX(c.fr[0]), w = FX(c.fr[1]) - x;
        const fill = cas ? (cas.kind === 'flood' ? `rgba(90,162,255,${0.15 + cas.severity * 0.6})`
            : cas.kind === 'fire' ? `rgba(255,${Math.round(120 - cas.severity * 80)},60,${0.15 + cas.severity * 0.65})` : 'rgba(226,179,60,.25)') : 'transparent';
        inner += `<rect x="${x}" y="90" width="${w}" height="125" fill="${fill}"/>`;
    }
    g += `<g clip-path="url(#hullclip)">${inner}</g>`;
    for (const c of sys.compartments) {
        const x = FX(c.fr[0]), w = FX(c.fr[1]) - x, mid = x + w / 2;
        const cas = sys.casualties.find(k => k.comp === c.id && !['out', 'repaired'].includes(k.status));
        g += `<text x="${mid}" y="${DECK(c.fr[0]) + 16}" text-anchor="middle" style="font-size:10px;font-weight:700;fill:var(--text)">${esc(c.id)}</text>`;
        g += `<text x="${mid}" y="${DECK(c.fr[0]) + 28}" text-anchor="middle" style="font-size:8px;font-family:var(--font-mono);fill:var(--text-muted)">${esc(c.number)}</text>`;
        sys.nodes.filter(n => n.comp === c.id).forEach((n, i) => {
            g += `<text x="${mid}" y="${DECK(c.fr[0]) + 42 + i * 11}" text-anchor="middle" style="font-size:8.5px;font-family:var(--font-mono);fill:${n.failed ? 'var(--badge-red)' : n.powered ? 'var(--text-muted)' : 'var(--badge-yellow)'}">${n.failed ? '✕ ' : n.powered ? '' : '○ '}${esc(n.id)}</text>`;
        });
        if (cas) g += `<text x="${mid}" y="200" text-anchor="middle" style="font-size:9.5px;font-weight:700;fill:${cas.kind === 'flood' ? 'var(--badge-blue)' : '#ff8a5c'}">${cas.status === 'reported' ? 'REPORTED' : cas.kind.toUpperCase()} ${Math.round(cas.severity * 100)}%</text>`;
    }
    // Bulkheads: a set boundary is a thick yellow line.
    for (const fr of sys.bulkheads.filter(f => f > 0 && f < 505)) {
        const set = sys.casualties.some(k => k.boundaries && k.boundaries.includes(fr) && k.status !== 'repaired');
        g += `<line x1="${FX(fr)}" y1="${DECK(fr)}" x2="${FX(fr)}" y2="210" stroke="${set ? 'var(--badge-yellow)' : 'var(--border-strong)'}" stroke-width="${set ? 5 : 1.2}"/>`;
        g += `<text x="${FX(fr)}" y="236" text-anchor="middle" style="font-size:8.5px;font-family:var(--font-mono);fill:${set ? 'var(--badge-yellow)' : 'var(--text-dim)'}">FR ${fr}</text>`;
    }
    // Repair parties.
    const TEAM_COL = { unmanned: '#333', manning: 'var(--orange)', standby: 'var(--state-off)', 'en route': 'var(--badge-yellow)', 'on scene': 'var(--green)', returning: 'var(--blue)' };
    sys.teams.forEach((t, i) => {
        const x = FX(t.pos), y = 160 + (i % 2) * 18;
        g += `<circle cx="${x}" cy="${y}" r="8" fill="${TEAM_COL[t.status]}" stroke="#000" stroke-width="1"/>`;
        g += `<text x="${x}" y="${y + 3.2}" text-anchor="middle" style="font-size:8px;font-weight:700;fill:#fff">${esc(t.id)}</text>`;
    });
    const ship = sys.ship || {};
    g += `<text x="${W - 10}" y="16" text-anchor="end" style="font-size:11px;font-weight:700;fill:var(--text)">${esc(ship.name || '')} (${esc(ship.hull || '')})</text>`;
    g += `<text x="${W - 10}" y="30" text-anchor="end" style="font-size:9px;fill:var(--text-muted)">${esc(ship.class || '')} · profile stylized · frames approx.</text>`;
    g += `<text x="10" y="252" style="font-size:9px;fill:var(--text-muted)">BOW ◄  frames (approx. ft)  ► STERN · yellow bulkhead = boundary set · ● repair party · ✕ failed · ○ no power</text>`;
    return `<svg class="dccop" viewBox="0 0 ${W} 258">${g}</svg>`;
}

function pageDC(s) {
    const sys = s.cop.systems;
    const comp = Object.fromEntries(sys.compartments.map(c => [c.id, c]));
    const cas = sys.casualties.filter(c => !['out', 'repaired'].includes(c.status)).map(c => `<div class="card"><div class="top">${badge(c.kind.toUpperCase(), c.kind === 'flood' ? 'blue' : c.kind === 'fire' ? 'red' : 'yellow')}
        <span class="ttl">${esc(comp[c.comp].name)}</span><span class="meta">${Math.round(c.severity * 100)}% · ${esc(c.status)} · team ${esc(c.team || 'NONE')}</span></div></div>`).join('');
    return `<div class="toolbar"><span class="page-title">Damage Control</span><span class="muted">Ship profile, compartments and repair parties.</span></div>
      ${oversightBar(s, 'dc')}
      <div class="overview">
        <div>${pictureLine(s, 'dc')}<div class="inset-plain" style="margin-bottom:12px">${dcSvg(sys)}</div>${cas}</div>
        <div>${oversightColumn(s, 'dc')}</div>
      </div>`;
}

