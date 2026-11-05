// DOM-less behaviour test for BRIDGE-WARDEN-02: the SAFE-STOP / RESUME hold button.
// Runs the real frontend/js/control.js in a node:vm sandbox with a tiny fake DOM and fake timers.
// No install needed (Node built-ins only).   node --test host/tests/HoldControl.node.test.js
'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SRC = fs.readFileSync(path.join(__dirname, '..', '..', 'frontend', 'js', 'control.js'), 'utf8');

class El {
    constructor(id) {
        this.id = id; this.listeners = {}; this.style = {}; this.dataset = {}; this.children = [];
        this.textContent = ''; this.innerHTML = ''; this.value = ''; this.checked = false; this.className = '';
        this.classList = { toggle() {}, add() {}, remove() {} };
    }
    addEventListener(type, fn) { (this.listeners[type] = this.listeners[type] || []).push(fn); }
    querySelector() { return this.fill || (this.fill = new El('fill')); }
    insertAdjacentHTML() {}
    // Dispatches a synthetic event; returns it so tests can read defaultPrevented.
    fire(type, props = {}) {
        const ev = Object.assign({ type, key: '', repeat: false, detail: 1, defaultPrevented: false,
            preventDefault() { this.defaultPrevented = true; } }, props);
        (this.listeners[type] || []).forEach(fn => fn(ev));
        return ev;
    }
}

function state(safeStop) {
    return {
        summary: { agents_active: 1, agents_total: 2, intents_per_min: 0, executed: 0, rejected: 0, escalations_open: 0,
            safe_stop: safeStop, next_check_s: null, checks_missed: 0 },
        server: { profile: 'edge', operator: 'op', uptime_s: 1, running: true,
            llm: { mode: 'off', base_url: '', local_model: '', reasoning_effort: '', include_thoughts: false },
            agents_llm: { calls: 0, last_ok: true, queue: 0, model: '', last_error: '' } },
        scenario: { name: 'ops_normal', running: true, finished: false },
        cop: { sim_time: 0, phase: 'transit', systems: { emcon: false, hostiles: [], gq: true } },
        presenter: { time_scale: 1, auto_inject: false, next_auto_s: 0, last_auto: '' },
        escalations: [], agents: [],
        resources: { profile: 'edge', cpu_cores: 1, cpu_budget: 2, rss_mb: 1, rss_budget: 2, ui_kbps: 1, ui_budget: 2,
            intent_bytes_max: 1, intent_budget: 2, pipeline_ms: 1, external_bytes: 0 },
    };
}

// Loads control.js fresh; the first state pushed is "running" (or stopped when startStopped is true).
function load({ startStopped = false, failActions = false } = {}) {
    const els = {}, calls = [], stateCbs = [];
    let now = 0, timers = [], nextId = 1;
    const get = (id) => els[id] || (els[id] = new El(id));
    const BW = {
        state: null, connected: true,
        act: async (method, url) => { calls.push(`${method} ${url}`); return failActions ? undefined : {}; },
        api: async () => ({ models: [] }), toast() {}, connect() {},
        onState: (fn) => stateCbs.push(fn),
    };
    const ctx = {
        document: { getElementById: get, querySelectorAll: () => [] },
        BW, Notify: { show() {} }, NotifyWatch: { update() {} }, window: { open() {} },
        confirmDestructive: async () => false,
        performance: { now: () => now },
        setInterval: (fn, ms) => { const t = { id: nextId++, fn, ms, next: now + ms }; timers.push(t); return t.id; },
        clearInterval: (id) => { timers = timers.filter(t => t.id !== id); },
        setTimeout: () => 0, clearTimeout() {},
        esc: (s) => String(s), fmtDur: () => '0:00:00', fmtSim: () => '0800:00', logLine: () => '',
    };
    ctx.globalThis = ctx;
    vm.createContext(ctx);
    vm.runInContext(SRC, ctx, { filename: 'control.js' });
    const push = (safeStop) => { BW.state = state(safeStop); stateCbs.forEach(fn => fn(BW.state, [])); };
    push(startStopped);
    const advance = async (ms) => {          // fake clock: runs due interval callbacks in order
        const end = now + ms;
        for (;;) {
            const due = timers.filter(t => t.next <= end).sort((a, b) => a.next - b.next)[0];
            if (!due) break;
            now = due.next; due.next += due.ms; due.fn();
        }
        now = end;
        await Promise.resolve(); await Promise.resolve();   // let BW.act().then() run
    };
    return { hold: get('hold'), status: get('hold-status'), hint: get('hold-hint'), calls, advance, push, els };
}

test('pointer hold of 2 s still engages SAFE-STOP (unchanged behaviour)', async () => {
    const t = load();
    t.hold.fire('pointerdown');
    await t.advance(1900);
    assert.deepEqual(t.calls, []);
    await t.advance(200);
    assert.deepEqual(t.calls, ['POST /api/control/safe-stop']);
});

test('pointer release before 2 s cancels', async () => {
    const t = load();
    t.hold.fire('pointerdown'); await t.advance(1500); t.hold.fire('pointerup'); await t.advance(1000);
    assert.deepEqual(t.calls, []);
});

for (const key of ['Enter', ' ']) {
    test(`holding ${JSON.stringify(key)} for 2 s engages SAFE-STOP, and only once`, async () => {
        const t = load();
        const down = t.hold.fire('keydown', { key });
        assert.equal(down.defaultPrevented, true, 'no native click on keydown');
        assert.match(t.status.textContent, /Holding to engage SAFE-STOP/);
        await t.advance(1900);
        t.hold.fire('keydown', { key, repeat: true });          // auto-repeat must not extend or restart
        assert.deepEqual(t.calls, [], 'not before 2 s');
        await t.advance(200);
        assert.deepEqual(t.calls, ['POST /api/control/safe-stop']);
        t.hold.fire('keydown', { key, repeat: true });          // still held after it fired
        await t.advance(5000);
        assert.deepEqual(t.calls, ['POST /api/control/safe-stop'], 'repeats never fire a second action');
        t.hold.fire('keyup', { key });
    });
}

test('holding Enter or Space for 2 s while stopped RESUMES operations', async () => {
    const t = load({ startStopped: true });
    t.hold.fire('keydown', { key: ' ' });
    assert.match(t.status.textContent, /Holding to resume operations/);
    await t.advance(2100);
    assert.deepEqual(t.calls, ['POST /api/control/resume-all']);
});

test('releasing early, Escape and losing focus cancel and announce it', async () => {
    const cancels = [
        (t) => t.hold.fire('keyup', { key: 'Enter' }),
        (t) => t.hold.fire('keydown', { key: 'Escape' }),
        (t) => t.hold.fire('blur'),
    ];
    for (const cancel of cancels) {
        const t = load();
        t.hold.fire('keydown', { key: 'Enter' });
        await t.advance(1500);
        cancel(t);
        assert.match(t.status.textContent, /Cancelled/);
        assert.equal(t.hold.fill.style.width, '0');
        await t.advance(3000);
        assert.deepEqual(t.calls, [], 'a cancelled hold never fires');
    }
});

test('a quick tap or a non-keyboard click does nothing but explain', async () => {
    const t = load();
    t.hold.fire('keydown', { key: 'Enter' }); t.hold.fire('keyup', { key: 'Enter' });
    const click = t.hold.fire('click', { detail: 0 });
    await t.advance(3000);
    assert.deepEqual(t.calls, []);
    assert.equal(click.defaultPrevented, true);
    assert.match(t.status.textContent, /Hold Enter or Space for 2 seconds to engage SAFE-STOP/);
});

test('other keys and modified Enter do not start a hold', async () => {
    const t = load();
    t.hold.fire('keydown', { key: 'a' });
    t.hold.fire('keydown', { key: 'Enter', ctrlKey: true });
    await t.advance(3000);
    assert.deepEqual(t.calls, []);
});

test('state changes are announced, the first state is not', async () => {
    const t = load();
    assert.equal(t.status.textContent, '', 'page load is silent');
    t.push(true);
    assert.equal(t.status.textContent, 'SAFE-STOP engaged. All agents paused.');
    assert.match(t.hint.textContent, /resume operations/);
    t.push(false);
    assert.equal(t.status.textContent, 'Operations resumed.');
    assert.match(t.hint.textContent, /engage SAFE-STOP/);
});

test('a failed request is announced', async () => {
    const t = load({ failActions: true });
    t.hold.fire('keydown', { key: 'Enter' });
    await t.advance(2100);
    assert.match(t.status.textContent, /Could not engage SAFE-STOP/);
});
