// Shared by both windows: live state over WebSocket, REST helper, formatters, badges.

const BW = {
    state: null,
    logs: [],
    listeners: [],
    connected: false,

    connect() {
        const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws`);
        ws.onopen = () => { BW.connected = true; };
        ws.onmessage = (ev) => {
            const msg = JSON.parse(ev.data);
            if (msg.type !== 'state') return;
            BW.state = msg.data;
            BW.logs.push(...(msg.logs || []));
            if (BW.logs.length > 600) BW.logs.splice(0, BW.logs.length - 600);
            BW.listeners.forEach(fn => fn(BW.state, msg.logs || []));
        };
        ws.onclose = () => {
            BW.connected = false;
            BW.listeners.forEach(fn => fn(BW.state, []));
            setTimeout(() => BW.connect(), 1500);
        };
    },

    onState(fn) { BW.listeners.push(fn); },

    async api(method, path, body) {
        const res = await fetch(path, {
            method,
            headers: body ? { 'Content-Type': 'application/json' } : {},
            body: body ? JSON.stringify(body) : undefined,
        });
        if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
        return res.json();
    },

    async act(method, path, body, okText) {
        try {
            const r = await BW.api(method, path, body);
            if (okText) BW.toast(okText);
            return r;
        } catch (e) {
            BW.toast(`Failed: ${e.message}`);
        }
    },

    // Confirmations of operator actions use the shared notification banners (notify.js).
    toast(text, variant) {
        // Only failures get a banner; confirmations of what the operator just did aren't news.
        if (text.startsWith('Failed') || variant === 'error') Notify.show({ variant: 'error', title: text });
    },
};

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const STATUS_BADGE = {
    active: 'green', idle: 'grey', escalated: 'yellow', paused: 'grey', quarantined: 'red', offline: 'red',
    executed: 'green', approved: 'green', rejected: 'red', pending: 'yellow', expired: 'grey',
    rolled_back: 'purple', advisory: 'blue', open: 'yellow', auto_resolved: 'green', resolved: 'green',
    timed_out: 'grey', critical: 'red', high: 'red', medium: 'yellow', low: 'grey',
    resource: 'blue', objective: 'purple', oscillation: 'yellow', cumulative: 'red', warn: 'yellow', info: 'blue',
};
const badge = (text, color) => `<span class="badge b-${color || STATUS_BADGE[text] || 'grey'}">${esc(String(text).replace('_', ' '))}</span>`;
const AUTONOMY = { 0: ['Advise', 'grey'], 1: ['Recommend', 'blue'], 2: ['Act w/ Approval', 'yellow'], 3: ['Act', 'green'] };
const autonomyBadge = (lvl) => badge(AUTONOMY[lvl][0], AUTONOMY[lvl][1]);

const pctClass = (v) => v < 20 ? 'var(--pct-low)' : v < 80 ? 'var(--pct-mid)' : 'var(--pct-high)';
const pad = (n) => String(Math.floor(n)).padStart(2, '0');
const SHIP_CLOCK_START = 8 * 3600;      // the exercise starts at 0800 ship's time
const fmtSim = (s) => { const t = (s + SHIP_CLOCK_START) % 86400; return `${pad(t / 3600)}${pad((t % 3600) / 60)}:${pad(t % 60)}`; };
const fmtDur = (s) => `${Math.floor(s / 3600)}:${pad((s % 3600) / 60)}:${pad(s % 60)}`;
const fmtTs = (t) => {
    const d = new Date(t * 1000);
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
};
const fmtValue = (p) => p.heading !== undefined ? `${String(Math.round(p.heading)).padStart(3, '0')}°`
    : p.speed_kn !== undefined ? `${p.speed_kn} kn` : JSON.stringify(p);

// Shows what produced a piece of AI text (docs/llm-wrapper.md: show the source in the UI).
const SOURCE_COLOR = { live: 'blue', local: 'blue', cache: 'green', fallback: 'grey', pending: 'yellow' };
const sourceTag = (src) => `<span class="badge b-${SOURCE_COLOR[src] || 'grey'}" title="Text source (LLM wrapper)">${esc(src === 'fallback' ? 'rule text' : src)}</span>`;

function logLine(l) {
    const cls = l.level === 'milestone' || l.level === 'milestone-red' ? l.level : '';
    return `<div class="ln ${cls}"><span class="t">${fmtTs(l.ts)}</span><span class="src lv-${esc(l.level)}">${esc(l.source)}</span><span class="lv-${esc(l.level)}">${esc(l.message)}</span></div>`;
}
