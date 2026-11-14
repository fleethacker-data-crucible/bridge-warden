// DOM-less behaviour test for BRIDGE-WARDEN-03: keyboard focus survives a live re-render.
// Runs the real frontend/js/app.js and frontend/js/watch.js in a node:vm sandbox with a tiny HTML parser.
// No install needed (Node built-ins only).   node --test src/tests/FocusRestore.node.test.js
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const ROOT = path.join(__dirname, '..', '..');
const APP = fs.readFileSync(path.join(ROOT, 'frontend', 'js', 'app.js'), 'utf8');
const WATCH = fs.readFileSync(path.join(ROOT, 'frontend', 'js', 'watch.js'), 'utf8');

function decode(s) {
    return s.replace(/&(#x[\da-fA-F]+|#\d+|[a-z]+);/g, (all, ent) => {
        if (ent[0] === '#') {
            const cp = ent[1] === 'x' || ent[1] === 'X' ? parseInt(ent.slice(2), 16) : parseInt(ent.slice(1), 10);
            return String.fromCodePoint(cp);
        }
        return ({ amp: '&', lt: '<', gt: '>', quot: '"', apos: "'" })[ent] || all;
    });
}

function escalation(id, remaining) {
    return {
        id, status: 'open', remaining_s: remaining, timeout_s: 60, kind: 'recommendation',
        title: 'Alter course', agent_id: 'nav', intent_id: null,
        approve_label: 'Approve', reject_label: 'Reject', safe_default: 'Hold course',
        summary: 'Synthetic recommendation', summary_source: 'fallback',
        proposal: { plain: 'Come right to ' + id, value: '090', current: '000', why_short: 'Traffic' },
    };
}

function stateWith(list) {
    return {
        summary: {
            agents_active: 1, agents_total: 1, intents_per_min: 0, conflicts_total: 0, conflicts_open: 0,
            escalations_open: list.length, submitted: 0, safe_stop: false,
        },
        server: { running: true, llm: { endpoint: '', mode: 'off', provider: '', model: '', local_model: '' } },
        cop: { phase: 'transit', sim_time: 0, systems: { gq: true } },
        presenter: { speed: 1 },
        auto_act: false,
        escalations: list,
        agents: [{ id: 'nav', name: 'Nav', status: 'active' }],
        intents: [],
        conflicts: [],
    };
}

function load() {
    const listeners = {};
    let active = null;
    class El {
        constructor(tag) {
            this.tagName = String(tag || 'div').toUpperCase();
            this.id = '';
            this.children = [];
            this.childNodes = [];
            this.parentElement = null;
            this.attrs = [];
            this._open = false;
            this.style = {};
            this.dataset = {};
            this.classList = { toggle() {}, add() {}, remove() {} };
        }
        get attributes() { return this.attrs; }
        getAttribute(name) {
            const found = this.attrs.find(a => a.name === String(name).toLowerCase());
            return found ? found.value : null;
        }
        setAttribute(name, value) {
            const n = String(name).toLowerCase();
            const found = this.attrs.find(a => a.name === n);
            if (found) found.value = String(value);
            else this.attrs.push({ name: n, value: String(value) });
            if (n === 'id') this.id = String(value);
            if (n === 'open') this._open = true;
        }
        get open() { return this._open; }
        set open(v) { this._open = !!v; }
        get textContent() {
            return this.childNodes.map(n => n.tagName ? n.textContent : n.data).join('');
        }
        set textContent(v) {
            this.childNodes = [{ data: String(v) }];
            this.children = [];
        }
        get innerHTML() { return this._raw || ''; }
        set innerHTML(html) {
            const nodes = parseHtml(html);
            this.childNodes = nodes;
            this.children = nodes.filter(n => n.tagName);
            this.children.forEach(c => { c.parentElement = this; });
            this._raw = html;
        }
        contains(node) {
            let n = node;
            while (n) { if (n === this) return true; n = n.parentElement; }
            return false;
        }
        closest(sel) {
            const ids = String(sel).split(',').map(s => s.trim()).filter(s => s[0] === '#').map(s => s.slice(1));
            let n = this;
            while (n) { if (ids.indexOf(n.id) >= 0) return n; n = n.parentElement; }
            return null;
        }
        querySelector() {
            if (!this._q) { this._q = new El('button'); this._q.parentElement = this; }
            return this._q;
        }
        querySelectorAll(sel) {
            const out = [];
            const walk = (el) => {
                el.children.forEach(c => {
                    if (sel === 'details' && c.tagName === 'DETAILS') out.push(c);
                    walk(c);
                });
            };
            walk(this);
            return out;
        }
        addEventListener() {}
        focus() { active = this; }
    }

    function parseAttrs(el, raw) {
        const re = /([:@A-Za-z0-9_-]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|(\S+)))?/g;
        let m;
        while ((m = re.exec(raw))) el.setAttribute(m[1], decode(m[2] != null ? m[2] : m[3] != null ? m[3] : m[4] != null ? m[4] : ''));
    }
    function parseHtml(html) {
        const root = { el: null, childNodes: [], children: [] };
        const stack = [root];
        const re = /<\/([A-Za-z][\w:-]*)\s*>|<([A-Za-z][\w:-]*)([^<>]*?)(\/?)>|([^<]+)/g;
        let m;
        while ((m = re.exec(html))) {
            const parent = stack[stack.length - 1];
            if (m[1]) {
                const name = m[1].toUpperCase();
                while (stack.length > 1) {
                    const top = stack.pop();
                    if (top.el && top.el.tagName === name) break;
                }
                continue;
            }
            if (m[2]) {
                const el = new El(m[2]);
                parseAttrs(el, m[3] || '');
                el.parentElement = parent.el;
                parent.childNodes.push(el);
                parent.children.push(el);
                const voidEl = /^(area|base|br|col|embed|hr|img|input|link|meta|source|track|wbr)$/i.test(m[2]);
                if (m[4] !== '/' && !voidEl) stack.push({ el, childNodes: el.childNodes, children: el.children });
                continue;
            }
            if (m[5]) parent.childNodes.push({ data: decode(m[5]) });
        }
        return root.childNodes;
    }

    const els = {};
    const document = {
        hidden: true,
        getElementById: (id) => els[id] || (els[id] = Object.assign(new El('div'), { id })),
        querySelector: () => null,
        querySelectorAll: () => [],
        addEventListener: (type, fn) => { (listeners[type] = listeners[type] || []).push(fn); },
        get activeElement() { return active; },
    };
    // getElementById's Object.assign does not keep the id if the constructor reset it. Set it explicitly.
    const origGet = document.getElementById;
    document.getElementById = (id) => {
        const el = origGet(id);
        el.id = id;
        if (!el.getAttribute('id')) el.setAttribute('id', id);
        return el;
    };
    const window = { addEventListener: document.addEventListener };
    const BW = {
        state: null, connected: true, logs: [],
        act: async () => ({}), api: async () => [], toast() {}, connect() {}, onState() {},
    };
    const ctx = {
        document, window, BW, location: { search: '', protocol: 'http:', host: '127.0.0.1' },
        URLSearchParams, Notify: { setScope() {} }, NotifyWatch: { update() {} },
        esc: (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]),
        badge: (text) => String(text), fmtSim: () => '0800:00',
        setTimeout, clearTimeout, setInterval: () => 0, clearInterval() {},
        console,
    };
    ctx.globalThis = ctx;
    vm.createContext(ctx);
    vm.runInContext(APP + '\n' + WATCH + '\n;this.setHtml = setHtml; this.render = render;\n', ctx, { filename: 'app.js' });
    // Picture pages live in cops.js. render() names them even on the Watch screen, so give them stubs.
    if (typeof ctx.pageSystems !== 'function') ctx.pageSystems = () => '';
    if (typeof ctx.pageDC !== 'function') ctx.pageDC = () => '';
    const fire = (type, ev) => { (listeners[type] || []).forEach(fn => fn(ev)); };
    return { ctx, document, BW, fire, content: () => document.getElementById('content') };
}

function walk(el, tag, out) {
    (out = out || []);
    el.children.forEach(c => { if (c.tagName === tag) out.push(c); walk(c, tag, out); });
    return out;
}
function buttonWith(root, needle) {
    return walk(root, 'BUTTON').find(b => (b.getAttribute('onclick') || '').indexOf(needle) >= 0);
}

test('a countdown tick and a new escalation keep focus and the selected record', () => {
    const t = load();
    const s = stateWith([escalation('esc-keep', 12)]);
    t.BW.state = s;
    t.ctx.render(s);
    const approve = buttonWith(t.content(), "NotifyWatch.decide('esc-keep', true)");
    assert.ok(approve, 'the review exposes an Approve control for the open escalation');
    approve.focus();
    s.escalations = [escalation('esc-new', 9), escalation('esc-keep', 3)];
    s.summary.escalations_open = 2;
    t.ctx.render(s);
    const html = t.content().innerHTML;
    const pressed = walk(t.content(), 'BUTTON').filter(b => b.getAttribute('aria-pressed') === 'true');
    assert.equal(pressed.length, 1);
    assert.match(pressed[0].getAttribute('onclick'), /esc-keep/);
    assert.doesNotMatch(pressed[0].getAttribute('onclick'), /esc-new/);
    assert.match(html, /esc-keep', true\)/);
    assert.equal(t.document.activeElement.getAttribute('onclick'), "NotifyWatch.decide('esc-keep', true)");
    assert.match(html, />3 s</);
});

test('Enter or Space holds the live swap until the key is released, then restores that control', async () => {
    for (const key of ['Enter', ' ']) {
        const t = load();
        const s = stateWith([escalation('esc-keep', 12)]);
        t.BW.state = s;
        t.ctx.render(s);
        const approve = buttonWith(t.content(), "NotifyWatch.decide('esc-keep', true)");
        approve.focus();
        t.fire('keydown', { key, repeat: false, target: approve });
        s.escalations = [escalation('esc-keep', 3)];
        t.ctx.render(s);
        assert.equal(t.document.activeElement, approve, 'the focused node is not replaced while the key is down');
        assert.match(t.content().innerHTML, />12 s</);
        t.fire('keyup', { key, target: approve });
        await new Promise(r => setTimeout(r, 0));
        assert.match(t.content().innerHTML, />3 s</);
        assert.equal(t.document.activeElement.getAttribute('onclick'), "NotifyWatch.decide('esc-keep', true)");
        assert.notEqual(t.document.activeElement, approve);
    }
});

test('an open review stays open on its own escalation when the cards are reordered', () => {
    const t = load();
    const box = t.document.getElementById('content');
    const card = (id, note) => `<div class="decision"><details><summary>More detail</summary><p>${note}</p></details>
        <button type="button" onclick="NotifyWatch.decide('${id}', true)">Approve</button></div>`;
    t.ctx.setHtml(box, card('esc-new', 'Other') + card('esc-keep', 'Keep the math'));
    const open = walk(box, 'DETAILS').find(d => d.textContent.indexOf('Keep the math') >= 0);
    open.open = true;
    // The summary text is shared by every card, so focus has to follow the card's own decision button.
    walk(open, 'SUMMARY')[0].focus();
    t.ctx.setHtml(box, card('esc-keep', 'Keep the math') + card('esc-new', 'Other'));
    const details = walk(box, 'DETAILS');
    const kept = details.filter(d => d.open);
    assert.equal(kept.length, 1);
    assert.match(kept[0].textContent, /Keep the math/);
    assert.equal(t.document.activeElement.tagName, 'SUMMARY');
    assert.equal(t.document.activeElement.parentElement, kept[0]);
});

test('identical markup does not replace the focused node, and focus outside the region stays put', () => {
    const t = load();
    const box = t.document.getElementById('content');
    const html = '<button type="button" onclick="NotifyWatch.decide(\'esc-keep\', true)">Approve</button>';
    t.ctx.setHtml(box, html);
    const approve = buttonWith(box, 'esc-keep');
    approve.focus();
    t.ctx.setHtml(box, html);
    assert.equal(t.document.activeElement, approve);
    const gear = t.document.getElementById('gear');
    gear.focus();
    t.ctx.setHtml(box, '<button type="button" onclick="NotifyWatch.decide(\'esc-other\', true)">Approve</button>');
    assert.equal(t.document.activeElement, gear);
});
