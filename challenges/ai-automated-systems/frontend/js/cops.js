// The three operating pictures: Navigation (20 nm), Ship Systems (power plant graph), Damage Control (ship profile).
// Every picture carries the same Decisions panel, so the watch can act without switching screens.

// Each COP shows only its own decisions plus ship-wide ones (system checks).
function decisionsBlock(s, domain) {
    const open = s.escalations.filter(x => x.status === 'open' && (!domain || x.domain === domain || x.domain === 'global'));
    return `<div id="decisions"><div class="label" style="color:${open.length ? 'var(--badge-yellow)' : 'var(--text-muted)'}">Decisions${open.length ? ` needed (${open.length})` : ''}</div>
        ${open.length ? open.map(decisionCard).join('') : '<div class="empty">Nothing waiting on you. Agent recommendations that need approval appear here.</div>'}</div>`;
}

// Every COP: the picture, then a fixed 300 px column on the right that holds only the decisions.
function copStage(main, s, domain) {
    return `<div class="cop-stage"><div class="cop-main">${main}</div><div class="cop-decisions">${decisionsBlock(s, domain)}</div></div>`;
}

// What the watch knows: tracks CIC has classified, never simulation ground truth.
const classified = (sys) => sys.hostiles.filter(h => ['SUSPECT', 'HOSTILE'].includes(h.classification));
// Track colors on the Navigation COP: hostile red, unknown/suspect orange, normal traffic green.
const TRACK_COLOR = { HOSTILE: 'var(--badge-red)', SUSPECT: 'var(--orange)', UNKNOWN: 'var(--orange)',
                      MERCHANT: 'var(--badge-green)', FISHING: 'var(--badge-green)', FRIENDLY: '#4fd1ff' };
const pct = (v) => `${Math.round(v || 0)}%`;
const riskColor = (v) => v >= 75 ? 'var(--badge-red)' : v >= 40 ? 'var(--badge-yellow)' : 'var(--badge-green)';

// ------------------------------------------------------------------ Navigation COP (20 nm)
// Weather radar drawn like a reflectivity display: each isolated storm is nested bands (green -> yellow -> orange
// -> red -> magenta core by intensity), every band a few overlapping ellipses roughened by a noise filter so the
// edges look like real echoes.
// Shapes are seeded per cell so they hold still between frames and drift with the cell.
const WX_BANDS = [[0.0, 1.45, '46,170,70', 0.38], [0.42, 1.0, '235,215,45', 0.55], [0.58, 0.68, '245,140,35', 0.68],
                  [0.72, 0.42, '225,45,45', 0.8], [0.88, 0.2, '230,60,200', 0.85]];
function weatherRadar(cop, P, f) {
    const cells = cop.environment.cells || [];
    if (!cells.length) return '';
    const pts = cells.map(c => { const [x, y] = P(c.x, c.y); return { x, y, r: c.r, i: c.intensity, id: c.id || '' }; });
    const seed = (str, k) => { let h = 2166136261; for (const ch of str + k) h = Math.imul(h ^ ch.charCodeAt(0), 16777619); return ((h >>> 0) % 1000) / 1000; };
    let g = `<defs><filter id="wxnoise" x="-20%" y="-20%" width="140%" height="140%">
        <feTurbulence type="fractalNoise" baseFrequency="0.55" numOctaves="3" seed="7" result="n"/>
        <feDisplacementMap in="SourceGraphic" in2="n" scale="1.6" xChannelSelector="R" yChannelSelector="G" result="d"/>
        <feGaussianBlur in="d" stdDeviation="0.18"/></filter></defs><g filter="url(#wxnoise)" class="wx">`;
    for (const c of pts) {
        for (const [min, scale, rgb, op] of WX_BANDS) {
            if (c.i < min) continue;
            const n = 3, base = c.r * scale;
            for (let k = 0; k < n; k++) {
                const ox = (seed(c.id, 'x' + k + scale) - 0.5) * base * 0.7, oy = (seed(c.id, 'y' + k + scale) - 0.5) * base * 0.7;
                const rx = base * (0.75 + seed(c.id, 'w' + k + scale) * 0.6), ry = rx * (0.45 + seed(c.id, 'h' + k + scale) * 0.3);
                const rot = seed(c.id, 'axis') * 180 + (seed(c.id, 'r' + k + scale) - 0.5) * 50;
                g += `<ellipse cx="${f(c.x + ox)}" cy="${f(c.y + oy)}" rx="${f(rx)}" ry="${f(ry)}" fill="rgba(${rgb},${op})" transform="rotate(${f(rot)} ${f(c.x + ox)} ${f(c.y + oy)})"/>`;
            }
        }
    }
    return g + '</g>';
}
// overlay: controls drawn on top of the plot (positioned against the plot, not the legend under it).
function navCopSvg(cop, overlay = '') {
    const R = 20, own = cop.ownship, sys = cop.systems;
    const P = (x, y) => [(x - own.x), -(y - own.y)];
    const vec = (h, len) => [Math.sin(h * Math.PI / 180) * len, Math.cos(h * Math.PI / 180) * len];
    const f = (n) => n.toFixed(3);
    const RED = '#ff4d4d', BLUE = 'var(--badge-blue)';
    const S = 0.85;   // ONE symbol size for everything on the scope: own ship, tracks, missiles, torpedoes, aircraft, our weapons
    const cs = (c, s) => `${String(Math.round(c)).padStart(3, '0')}° ${Math.round(s)} kn`;   // course · speed label
    // Labels flip to the left near the right edge, so they stay inside the scope.
    const L = (x, dy, size, col, text, w = 700) => `<text x="${f(x > 6 ? x - 0.9 : x + 0.9)}" y="${f(dy)}" text-anchor="${x > 6 ? 'end' : 'start'}" style="font-size:${size}px;fill:${col};font-weight:${w}">${text}</text>`;
    let g = '';
    g += weatherRadar(cop, P, f);
    for (const r of [5, 10, 15, 20]) {
        g += `<circle r="${r}" fill="none" stroke="var(--text-dim)" stroke-opacity=".75" stroke-width="0.05"/>`;
        g += `<text x="0.3" y="${-r + 0.9}" style="font-size:0.7px;fill:var(--text-muted)">${r} nm</text>`;
    }
    // Planned route: the track line (heading 090 along y = 0), dashed, labelled where it enters the picture.
    {
        const rh = cop.route_heading ?? 90, [ux, uy] = vec(rh, 1);
        const [ax, ay] = P(own.x - ux * 40, 0 - uy * 40), [bx, by] = P(own.x + ux * 40, 0 + uy * 40);
        g += `<line x1="${f(ax)}" y1="${f(ay)}" x2="${f(bx)}" y2="${f(by)}" stroke="var(--badge-blue)" stroke-opacity=".7" stroke-width="0.1" stroke-dasharray="0.8 0.5"/>`;
        const [lx, ly] = P(own.x + ux * 12, 0 + uy * 12);
        g += `<text x="${f(lx)}" y="${f(ly - 0.5)}" text-anchor="middle" style="font-size:0.8px;fill:var(--badge-blue);font-weight:700">PLANNED ROUTE ${String(Math.round(rh)).padStart(3, '0')}° · 16 kn</text>`;
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
    g += `<line x1="0" y1="0" x2="${f(hx)}" y2="${f(-hy)}" stroke="var(--blue)" stroke-width="0.14"/>`;
    g += `<polygon points="0,-0.9 0.6,0.65 -0.6,0.65" fill="var(--blue)" stroke="#fff" stroke-width="0.06" transform="rotate(${own.heading}) scale(${S})"/>`;
    g += L(0, 1.6, 0.8, 'var(--blue)', `OWN SHIP ${cs(own.heading, own.speed)}`);
    for (const c of cop.contacts) {
        const [x, y] = P(c.x, c.y);
        const col = TRACK_COLOR[c.classification] || TRACK_COLOR.UNKNOWN;
        const [vx, vy] = vec(c.course, c.speed / 10 * 1.5);
        g += `<line x1="${f(x)}" y1="${f(y)}" x2="${f(x + vx)}" y2="${f(y - vy)}" stroke="${col}" stroke-width="0.1"/>`;
        if (c.domain === 'subsurface') {           // submarine: open bowl (subsurface symbol)
            g += `<path d="M${f(x - 0.7 * S)},${f(y - 0.2 * S)} A${0.7 * S},${0.7 * S} 0 0 0 ${f(x + 0.7 * S)},${f(y - 0.2 * S)}" fill="none" stroke="${col}" stroke-width="${0.22 * S}"/>`;
        } else if (c.kind === 'hostile') {
            g += `<polygon points="${f(x)},${f(y - 0.6 * S)} ${f(x + 0.6 * S)},${f(y)} ${f(x)},${f(y + 0.6 * S)} ${f(x - 0.6 * S)},${f(y)}" fill="${col}"/>`;
        } else {
            g += `<rect x="${f(x - 0.45 * S)}" y="${f(y - 0.45 * S)}" width="${0.9 * S}" height="${0.9 * S}" fill="${col}"/>`;
            // Collision detected (CPA under 1 nm, contact inside 3 nm): red ring plus a dead-reckoning projection of
            // both ships to the moment of closest approach, and the CPA line between them.
            if (c.cpa_nm < 1.0 && c.tcpa_min >= 0 && c.tcpa_min <= 30 && c.range_nm <= 3) {
                const t = c.tcpa_min / 60;
                const [ox, oy] = vec(own.heading, own.speed * t), [cx, cy] = vec(c.course, c.speed * t);
                const ex = x + cx, ey = y - cy;
                g += `<circle cx="${f(x)}" cy="${f(y)}" r="1.1" fill="none" stroke="var(--badge-red)" stroke-width="0.1"/>`;
                g += `<line x1="0" y1="0" x2="${f(ox)}" y2="${f(-oy)}" stroke="var(--blue)" stroke-width="0.1" stroke-dasharray="0.4 0.3"/>`;
                g += `<line x1="${f(x)}" y1="${f(y)}" x2="${f(ex)}" y2="${f(ey)}" stroke="${col}" stroke-width="0.1" stroke-dasharray="0.4 0.3"/>`;
                g += `<circle cx="${f(ox)}" cy="${f(-oy)}" r="0.35" fill="none" stroke="var(--blue)" stroke-width="0.1"/>`;
                g += `<circle cx="${f(ex)}" cy="${f(ey)}" r="0.35" fill="none" stroke="${col}" stroke-width="0.1"/>`;
                g += `<line x1="${f(ox)}" y1="${f(-oy)}" x2="${f(ex)}" y2="${f(ey)}" stroke="var(--badge-red)" stroke-width="0.12"/>`;
                g += L((ox + ex) / 2, (-oy + ey) / 2 + 1.2, 0.75, 'var(--badge-red)', `CPA ${c.cpa_nm.toFixed(2)} nm in ${c.tcpa_min.toFixed(0)} min`);
            }
        }
        const big = c.kind === 'hostile' || c.cpa_nm < 1.0 || c.classification === 'FRIENDLY';
        g += L(x, y - 0.5, 0.8, col, `${esc(c.id)}${c.kind === 'hostile' || c.classification === 'FRIENDLY' ? ' ' + c.classification : ''}`, big ? 700 : 400);
        g += L(x, y + 0.5, 0.7, col, cs(c.course, c.speed), 400);
    }
    // Missiles in the air: red arrowheads pointing at us, with seconds to impact.
    let k = 0;            // missiles fly in pairs on one bearing: stack their labels
    const ciwsOn = (sys.defenses?.CIWS?.active) || sys.defenses?.CIWS === 'active';
    for (const m of sys.inbound) {
        k++;
        const brg = m.bearing ?? 45, [x, y] = vec(brg, m.range);
        const away = m.kind === 'aircraft' && m.leg === 'out';
        const hdg = away ? brg : brg + 180;
        if (m.kind === 'aircraft')    // aircraft: swept-wing chevron
            g += `<path d="M0,-0.8 L0.8,0.5 L0,0.15 L-0.8,0.5 Z" fill="${RED}" stroke="#fff" stroke-width="0.06" transform="translate(${f(x)},${f(-y)}) rotate(${hdg}) scale(${S})"/>`;
        else
            g += `<polygon points="0,-0.7 0.45,0.5 -0.45,0.5" fill="${RED}" stroke="#fff" stroke-width="0.06" transform="translate(${f(x)},${f(-y)}) rotate(${hdg}) scale(${S})"/>`;
        g += L(x, -y + 0.2 + k * 0.95, 0.8, RED, `${esc(m.id)} ${m.kind === 'aircraft' ? 'AIRCRAFT' : (m.eta_s ?? '') + 's'} ${cs(hdg, m.speed_kn || 550)}${m.engaged_by?.length ? ' · engaged' : ''}`);
        // CIWS gunfire: fast blue tracer bursts at anything inside 2 nm
        if (ciwsOn && m.range <= 2)
            g += `<line class="tracer" x1="0" y1="0" x2="${f(x)}" y2="${f(-y)}" stroke="#5ab0ff" stroke-width="0.12" stroke-linecap="round"/>
                  <line class="tracer t2" x1="0" y1="0" x2="${f(x * 1.05)}" y2="${f(-y * 1.05)}" stroke="#9fd2ff" stroke-width="0.07" stroke-linecap="round"/>`;
    }
    // Torpedoes in the water: red dashed run line and a torpedo body.
    for (const t of sys.torpedoes || []) {
        const [x, y] = vec(t.bearing, t.range);
        g += `<line x1="${f(x)}" y1="${f(-y)}" x2="0" y2="0" stroke="${RED}" stroke-width="0.08" stroke-dasharray="0.3 0.3" opacity=".6"/>`;
        g += `<ellipse cx="${f(x)}" cy="${f(-y)}" rx="${0.25 * S}" ry="${0.7 * S}" fill="${RED}" stroke="#fff" stroke-width="0.05" transform="rotate(${t.bearing} ${f(x)} ${f(-y)})"/>`;
        g += L(x, -y + 1.1, 0.8, RED, `TORPEDO ${t.range.toFixed(1)} nm · ${cs(t.bearing + 180, t.speed_kn || 50)} · fuel ${t.run_left.toFixed(1)} nm`);
    }
    // Our weapons: blue arrowheads flying out.
    for (const i of sys.interceptors || []) {
        const brg = i.bearing ?? 45, [x, y] = vec(brg, Math.max(0.4, i.range));
        g += `<polygon points="0,-0.7 0.45,0.5 -0.45,0.5" fill="${BLUE}" stroke="#fff" stroke-width="0.05" transform="translate(${f(x)},${f(-y)}) rotate(${brg}) scale(${S})"/>`;
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
      <span><i style="background:var(--badge-green)"></i>traffic</span><span><i style="background:#4fd1ff"></i>friendly</span><span><i style="background:${BLUE}"></i>our weapons</span><span>◡ submarine</span><span>┄ EMCON ${sys.emcon_radius_nm} nm</span></div>`;
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
// Ship Systems COP: a one-line diagram drawn with right-angle wiring. Every breaker gets its own vertical lane in the gap
// between two columns and its own port on each node, so no two wires share a segment; lanes are reused only where
// their vertical spans don't overlap. Ties (same column, e.g. bus ties) run in the gap to the right of that column.
const SYS_COL = { gen: 0, swbd: 1, lc: 2 };
const SYS_W = 138, LANE = 11, GAP_PAD = 16;

function systemsSvg(sys) {
    const N = Object.fromEntries(sys.nodes.map(n => [n.id, { ...n }]));
    const col = (n) => SYS_COL[n.type] ?? 3;
    const loadsOf = (lc) => sys.breakers.filter(b => b.id.startsWith('F-') && b.a === lc).map(b => b.b);
    // Rows: loads in load-center order; each load center centered on its loads; switchboards on their normal load
    // centers; generators level with their switchboard.
    const LCS = ['LC-1', 'LC-2', 'LC-3', 'LC-4'], ROW = 44;
    let y = 30;
    const loadY = {};
    for (const lc of LCS) { for (const l of loadsOf(lc)) { loadY[l] = y; y += ROW; } y += 14; }
    const H = y + 10;
    for (const [id, yy] of Object.entries(loadY)) N[id].y = yy;
    const mean = (a) => a.reduce((x, v) => x + v, 0) / a.length;
    for (const lc of LCS) N[lc].y = mean(loadsOf(lc).map(l => N[l].y));
    const swFeeds = (sw) => sys.breakers.filter(b => b.a === sw && N[b.b].type === 'lc' && b.normal).map(b => N[b.b].y);
    N['1SB'].y = mean(swFeeds('1SB')); N['2SB'].y = mean(swFeeds('2SB'));
    N['3SB'].y = (N['1SB'].y + N['2SB'].y) / 2;
    if (Math.abs(N['3SB'].y - N['1SB'].y) < 90) N['3SB'].y = N['2SB'].y + (N['2SB'].y - N['1SB'].y) / 2;
    for (const gname of ['SSGTG-1', 'SSGTG-2', 'SSGTG-3']) N[gname].y = N[gname.replace('SSGTG-', '') + 'SB'].y;
    // Edges per gap (gap k = between column k and k+1). Node height grows with its ports.
    const edges = sys.breakers.map(b => {
        const a = N[b.a], z = N[b.b], ca = col(a), cz = col(z);
        return ca <= cz ? { b, from: a, to: z, gap: ca, tie: ca === cz } : { b, from: z, to: a, gap: cz, tie: false };
    });
    const ports = {};   // node id -> side -> edges
    const addPort = (n, side, e) => ((ports[n.id] ||= { l: [], r: [] })[side]).push(e);
    for (const e of edges) { addPort(e.from, 'r', e); addPort(e.to, e.tie ? 'r' : 'l', e); }
    const hOf = (n) => Math.max(30, 8 + 7 * Math.max((ports[n.id]?.l.length || 0), (ports[n.id]?.r.length || 0)));
    const portY = (n, side, e) => {
        const list = ports[n.id][side].slice().sort((p, q) => otherY(p, n) - otherY(q, n));
        const h = hOf(n), k = list.indexOf(e);
        return n.y - h / 2 + 4 + (h - 8) * (k + 0.5) / list.length;
    };
    const otherY = (e, n) => (e.from === n ? e.to : e.from).y;
    // Lanes: interval colouring per gap, longest spans outermost.
    const lanes = [0, 0, 0];
    for (let gap = 0; gap < 3; gap++) {
        const list = edges.filter(e => e.gap === gap).map(e => {
            const y1 = portY(e.from, 'r', e), y2 = portY(e.to, e.tie ? 'r' : 'l', e);
            return Object.assign(e, { y1, y2, lo: Math.min(y1, y2), hi: Math.max(y1, y2) });
        }).sort((p, q) => (q.hi - q.lo) - (p.hi - p.lo));
        const used = [];
        for (const e of list) {
            let k = 0;
            while ((used[k] || []).some(o => !(e.hi + 3 < o.lo || e.lo - 3 > o.hi))) k++;
            (used[k] ||= []).push(e); e.lane = k;
        }
        lanes[gap] = used.length;
    }
    // Column x positions from the lane counts.
    const X = [0];
    for (let c = 0; c < 3; c++) X.push(X[c] + SYS_W + 2 * GAP_PAD + lanes[c] * LANE);
    const W = X[3] + SYS_W + 20;
    for (const n of Object.values(N)) n.x = X[col(n)];
    let g = '';
    const HEAD = ['GENERATION', 'SWITCHBOARDS', 'LOAD CENTERS', 'EQUIPMENT'];
    HEAD.forEach((t, c) => { g += `<text x="${X[c]}" y="10" style="font-size:12px;font-weight:700;letter-spacing:.08em;fill:var(--text-muted)">${t}</text>`; });
    // Wires (open first, so live wiring draws on top), then the breaker symbols.
    const wires = edges.slice().sort((p, q) => (p.b.closed ? 1 : 0) - (q.b.closed ? 1 : 0));
    let marks = '';
    for (const e of wires) {
        const { b, from, to } = e;
        const live = b.closed && from.powered && to.powered && !from.failed && !to.failed;
        const rerouted = b.closed && !b.normal;
        const c = !b.closed ? 'var(--state-off)' : rerouted ? 'var(--blue)' : live ? 'var(--badge-green)' : 'var(--badge-red)';
        const x1 = from.x + SYS_W, lx = x1 + GAP_PAD + e.lane * LANE + LANE / 2;
        const x2 = e.tie ? to.x + SYS_W : to.x;
        g += `<path d="M${x1},${e.y1} H${lx} V${e.y2} H${x2}" fill="none" stroke="${c}" stroke-width="${b.closed ? 2.2 : 1.3}" ${b.closed ? '' : 'stroke-dasharray="4 3"'}/>`;
        // breaker: square on the lane (filled = closed); named unless it is a normal equipment feeder
        const by = (e.y1 + e.y2) / 2;
        marks += `<g><title>${esc(b.id)} ${esc(b.a)} → ${esc(b.b)} · ${b.closed ? 'closed' : 'open'}${b.normal ? '' : ' (alternate path)'}</title>
          <rect x="${lx - 4.5}" y="${by - 4.5}" width="9" height="9" fill="${b.closed ? c : 'var(--bg-inset)'}" stroke="${c}" stroke-width="1.5"/></g>`;
        if (!b.id.startsWith('F-'))
            marks += `<text x="${lx + 6}" y="${by + 3.5}" style="font-size:9.5px;font-family:var(--font-mono);fill:${c}">${esc(b.id)}</text>`;
    }
    g += marks;
    for (const n of Object.values(N)) {
        const h = hOf(n), top = n.y - h / 2;
        const state = n.failed ? 'FAILED' : n.maint ? 'MAINT UNDERWAY' : n.active ? 'ACTIVE' : n.powered ? (n.type === 'gen' ? 'ONLINE' : 'POWERED') : 'NO POWER';
        const c = n.failed ? 'var(--badge-red)' : n.maint ? 'var(--orange)' : n.active ? 'var(--blue)' : n.powered ? 'var(--badge-green)' : 'var(--badge-yellow)';
        const fill = n.failed ? 'rgba(255,107,107,.18)' : n.maint ? 'rgba(240,140,40,.18)' : n.active ? 'rgba(59,130,246,.18)' : !n.powered ? 'rgba(226,179,60,.14)' : 'var(--bg-row)';
        const load = n.type === 'gen' && sys.gen_load[n.id] !== undefined ? `${sys.gen_load[n.id]}% LOAD` : state;
        g += `<g><title>${esc(n.label)} · ${n.comp}${n.fail_reason ? ' · ' + esc(n.fail_reason) : ''}</title>
          <rect x="${n.x}" y="${top}" width="${SYS_W}" height="${h}" rx="5" fill="${fill}" stroke="${c}" stroke-width="${n.failed || n.active || !n.powered ? 2 : 1}"/>
          <rect x="${n.x}" y="${top}" width="4" height="${h}" rx="2" fill="${c}"/>
          <text x="${n.x + 10}" y="${n.y - 2}" style="font-size:13px;font-weight:700;fill:var(--text)">${esc(n.id)}</text>
          <text x="${n.x + SYS_W - 6}" y="${n.y - 2}" text-anchor="end" style="font-size:10px;font-weight:700;fill:${(sys.gen_load[n.id] || 0) > 100 ? 'var(--badge-red)' : c}">${load}</text>
          <text x="${n.x + 10}" y="${n.y + 11}" style="font-size:10px;fill:var(--text-muted)">${esc(n.label.length > 22 ? n.label.slice(0, 21) + '…' : n.label)}</text></g>`;
    }
    g += `<text x="0" y="${H + 4}" style="font-size:11px;fill:var(--text-muted)">■ closed  □ open breaker · green live · blue re-routed (alternate path) · dashed open · yellow box = no power · orange = maint underway (tagged out) · red = failed</text>`;
    return `<svg class="syscop" viewBox="-6 -6 ${W + 12} ${H + 20}">${g}</svg>`;
}

function pageSystems(s) {
    return copStage(`<div class="nav-center sys-center">${systemsSvg(s.cop.systems)}</div>`, s, 'systems');
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
    // Text as large as the compartment allows (mono glyph ~0.62 em wide): the narrow machinery rooms get a bit less.
    const fit = (w, chars, max) => Math.min(max, (w - 6) / (chars * 0.62));
    let over = '';   // casualty labels: drawn last, on top of the compartment text, bulkheads and repair parties
    for (const c of sys.compartments) {
        const x = FX(c.fr[0]), w = FX(c.fr[1]) - x, mid = x + w / 2, top = DECK(c.fr[0]);
        const cas = sys.casualties.find(k => k.comp === c.id && !['out', 'repaired'].includes(k.status));
        const nodes = sys.nodes.filter(n => n.comp === c.id);
        const nfs = fit(w, Math.max(0, ...nodes.map(n => n.id.length + 2)), 14);
        g += `<text x="${mid}" y="${top + 19}" text-anchor="middle" style="font-size:${fit(w, c.id.length, 17)}px;font-weight:700;fill:var(--text)">${esc(c.id)}</text>`;
        g += `<text x="${mid}" y="${top + 35}" text-anchor="middle" style="font-size:${fit(w, c.number.length, 12)}px;font-family:var(--font-mono);fill:var(--text-muted)">${esc(c.number)}</text>`;
        nodes.forEach((n, i) => {
            g += `<text x="${mid}" y="${top + 50 + i * (nfs + 1)}" text-anchor="middle" style="font-size:${nfs}px;font-family:var(--font-mono);fill:${n.failed ? 'var(--badge-red)' : n.powered ? 'var(--text-muted)' : 'var(--badge-yellow)'}">${n.failed ? '✕ ' : n.powered ? '' : '○ '}${esc(n.id)}</text>`;
        });
        if (cas) over += `<text x="${mid}" y="202" text-anchor="middle" style="font-size:${fit(w, 9, 14)}px;font-weight:700;fill:${cas.kind === 'flood' ? 'var(--badge-blue)' : '#ff8a5c'}">${cas.status === 'reported' ? 'REPORTED' : cas.kind.toUpperCase()}</text>`
            // The severity, very large and bold, centered on the compartment over everything else (dark outline keeps it readable).
            + `<text x="${mid}" y="${(top + 210) / 2 + 4}" text-anchor="middle" dominant-baseline="middle" style="font-size:${Math.min(44, w / 2.1)}px;font-weight:800;fill:${cas.kind === 'flood' ? '#7cc0ff' : '#ff8a5c'};stroke:#000;stroke-width:4px;paint-order:stroke">${Math.round(cas.severity * 100)}%</text>`;
    }
    // Bulkheads: a set boundary is a thick yellow line.
    for (const fr of sys.bulkheads.filter(f => f > 0 && f < 505)) {
        const set = sys.casualties.some(k => k.boundaries && k.boundaries.includes(fr) && k.status !== 'repaired');
        g += `<line x1="${FX(fr)}" y1="${DECK(fr)}" x2="${FX(fr)}" y2="210" stroke="${set ? 'var(--badge-yellow)' : 'var(--border-strong)'}" stroke-width="${set ? 5 : 1.2}"/>`;
        g += `<text x="${FX(fr)}" y="256" text-anchor="middle" style="font-size:13px;font-family:var(--font-mono);fill:${set ? 'var(--badge-yellow)' : 'var(--text-dim)'}">FR ${fr}</text>`;
    }
    // Repair parties.
    // Response teams: off watch they are asleep (berthing) or in the galley; at GQ they wake and run to their stations.
    const TEAM_COL = { asleep: '#2f3a6b', galley: '#6b5524', manning: 'var(--orange)', standby: 'var(--green)', 'en route': 'var(--badge-yellow)',
        'on scene': '#1f9d55', returning: 'var(--blue)', unmanned: '#333' };
    const placed = [];
    sys.teams.slice().sort((p, q) => p.pos - q.pos).forEach(t => {
        let x = FX(t.pos);
        while (placed.some(px => Math.abs(px - x) < 30)) x += 30;   // teams in the same space sit side by side
        placed.push(x);
        const y = 226, key = t.status === 'unmanned' ? (t.activity === 'galley' ? 'galley' : 'asleep') : t.status;
        const what = t.status === 'unmanned' ? (t.activity === 'galley' ? 'in the galley' : t.activity === 'asleep' ? 'asleep in berthing' : t.activity || 'off watch')
            : t.status === 'manning' ? (t.activity || 'manning') : t.status === 'standby' ? 'on station, ready' : t.status;
        g += `<g><title>${esc(t.name)}: ${esc(what)} · frame ${Math.round(t.pos)}</title>
          <circle cx="${x}" cy="${y}" r="14" fill="${TEAM_COL[key]}" stroke="${t.status === 'manning' ? '#fff' : '#000'}" stroke-width="1.5" ${t.status === 'manning' ? 'stroke-dasharray="4 3"' : ''}/>
          <text x="${x}" y="${y + 5}" text-anchor="middle" style="font-size:${t.id.length > 3 ? 10.5 : 12.5}px;font-weight:700;fill:#fff">${esc(t.id)}</text></g>`;
    });
    g += over;
    const ship = sys.ship || {};
    g += `<text x="${W - 10}" y="18" text-anchor="end" style="font-size:16px;font-weight:700;fill:var(--text)">${esc(ship.name || '')} (${esc(ship.hull || '')})</text>`;
    g += `<text x="${W - 10}" y="36" text-anchor="end" style="font-size:12px;fill:var(--text-muted)">${esc(ship.class || '')} · profile stylized · frames approx.</text>`;
    g += `<text x="10" y="280" style="font-size:13px;fill:var(--text-muted)">BOW ◄  frames (approx. ft)  ► STERN · teams: green on station · blue-grey asleep · brown galley · orange running to station · yellow en route · ✕ failed · ○ no power</text>`;
    return `<svg class="dccop" viewBox="0 0 ${W} 288">${g}</svg>`;
}

function pageDC(s) {
    const sys = s.cop.systems;
    const comp = Object.fromEntries(sys.compartments.map(c => [c.id, c]));
    const cas = sys.casualties.filter(c => !['out', 'repaired'].includes(c.status)).map(c => `<div class="card"><div class="top">${badge(c.kind.toUpperCase(), c.kind === 'flood' ? 'blue' : c.kind === 'fire' ? 'red' : 'yellow')}
        <span class="ttl">${esc(comp[c.comp].name)}</span><span class="meta">${Math.round(c.severity * 100)}% · ${esc(c.status)} · team ${esc(c.team || 'NONE')}</span></div></div>`).join('');
    // Stacked, not side by side: the ship profile is long and low, so it gets the full width and the decisions sit
    // right under it (in columns), then the open casualties.
    return `<div class="cop-stage stacked"><div class="dc-main">
      <div class="inset-plain" style="margin-bottom:14px">${dcSvg(sys)}</div>
      <div class="cop-decisions dc-decisions">${decisionsBlock(s, 'dc')}</div>
      <div class="label neutral">Open casualties</div>${cas || '<div class="empty">No open casualties</div>'}</div></div>`;
}

