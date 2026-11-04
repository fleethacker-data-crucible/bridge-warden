// Warden Control window: status, SAFE-STOP, scenario actions, edge budget, live log.

const $ = (id) => document.getElementById(id);
let paused = false;   // log auto-scroll pauses on hover

// Tooltip: why Warden quarantined the agent, else its latest finding (blocked / reprimanded).
function tileWhy(a) {
    if (a.quarantine_reason) return ' – ' + esc(a.quarantine_reason);
    const fs = a.findings || [], f = fs[fs.length - 1];
    return f ? ` – last ${f.kind === 'MISSED' ? 'REPRIMAND' : f.kind}: ${esc(f.reason)} [${esc(f.rule)}]` : '';
}
// Screen-reader announcements (polite live region next to the hold button).
const announce = (text) => { $('hold-status').textContent = text; };
let lastSafeStop;   // undefined until the first state, so the page load itself is not announced

function render(s, newLogs) {
    if (!s) return;
    const sum = s.summary;
    const ai = s.server.llm;
    $('sub').textContent = `${BW.connected ? 'Connected' : 'Disconnected'} · profile ${s.server.profile} · scenario ${s.scenario.name} · AI text: ${ai.mode}${ai.mode === 'live' ? ` (${ai.provider}/${ai.model})` : ''}`;
    $('k-agents').textContent = `${sum.agents_active}/${sum.agents_total}`;
    $('k-ipm').textContent = sum.intents_per_min;
    $('k-exec').textContent = sum.executed;
    $('k-rej').textContent = sum.rejected;
    $('k-esc').textContent = sum.escalations_open;
    $('operator').textContent = s.server.operator;
    $('uptime').textContent = fmtDur(s.server.uptime_s);
    $('simtime').textContent = fmtSim(s.cop.sim_time);
    $('phase').textContent = s.cop.phase;

    const core = $('core');
    core.textContent = !BW.connected ? 'Offline' : sum.safe_stop ? 'Safe-Stop' : !s.server.running ? 'Paused' : 'Online';
    core.className = 'status-word ' + (!BW.connected || sum.safe_stop ? 'sw-red' : !s.server.running ? 'sw-yellow' : 'sw-green');
    const scen = $('scen');
    scen.textContent = s.scenario.running ? (s.scenario.name === 'ops_normal' ? 'Operating' : s.scenario.finished ? 'Complete' : 'Running') : 'Standby';
    scen.className = 'status-word ' + (s.scenario.running ? 'sw-blue' : 'sw-yellow');
    const L = s.server.agents_llm, lw = $('llm');
    lw.textContent = !s.server.running && !L.calls ? 'Standby' : !L.calls ? 'Waiting' : L.last_ok ? 'Online' : 'Offline';
    lw.className = 'status-word ' + (!L.calls ? 'sw-blue' : L.last_ok ? 'sw-green' : 'sw-red');
    lw.title = `${s.server.llm.base_url} · ${L.model || s.server.llm.local_model} · ${L.calls} calls · queue ${L.queue}${L.last_error ? ' · ' + L.last_error : ''}`;
    const sys = s.cop.systems, em = $('emcon');
    em.textContent = sys.emcon ? 'Set' : 'Off';
    em.className = 'status-word ' + (sys.emcon ? 'sw-green' : sys.hostiles.length ? 'sw-red' : 'sw-yellow');
    const gq = s.cop.systems.gq, gb = $('gq');
    gb.textContent = gq ? 'Secure GQ' : 'Sound GQ';
    gb.className = 'btn ' + (gq ? '' : 'danger');
    const pr = s.presenter;
    const sp = pr.speed || 1;
    $('tscale').innerHTML = `<button class="btn ${sp > 1 ? 'warn' : ''}" style="height:24px;min-width:56px" title="Click for ${sp % 5 + 1}x" onclick="BW.act('POST','/api/presenter/speed',null,'Sim speed ${sp % 5 + 1}x')">${sp}x</button>`;
    document.querySelectorAll('#injmode button').forEach(b => b.classList.toggle('on', (b.dataset.mode === 'auto') === pr.auto_inject));
    $('injinfo').textContent = pr.auto_inject
        ? `Auto: a random issue every 60–120 s · next in ${pr.next_auto_s} s${pr.last_auto ? ` · last: ${pr.last_auto}` : ''}`
        : 'Manual: only the presenter injects issues.';
    const openCheck = s.escalations.find(e => e.kind === 'system_check' && e.status === 'open');
    const nc = $('nextcheck');
    nc.textContent = openCheck ? 'WAITING' : sum.next_check_s === null ? 'off' : fmtDur(sum.next_check_s).slice(2);
    nc.style.color = openCheck || sum.checks_missed ? 'var(--badge-yellow)' : '';

    $('tiles').innerHTML = s.agents.map(a =>
        `<div class="agent-tile at-${a.status}" title="${esc(a.name)}: ${a.status.toUpperCase()}${tileWhy(a)}">${esc(a.id)}</div>`).join('');

    $('banner').classList.toggle('show', sum.safe_stop);
    const hold = $('hold');
    hold.classList.toggle('red', !sum.safe_stop);
    hold.classList.toggle('green', sum.safe_stop);
    $('hold-cap').textContent = sum.safe_stop ? 'RESUME OPERATIONS' : 'SAFE-STOP';
    $('hold-hint').textContent = `Keyboard: hold Enter or Space for 2 seconds to ${sum.safe_stop ? 'resume operations' : 'engage SAFE-STOP'}. Release or press Escape to cancel.`;
    if (lastSafeStop !== undefined && lastSafeStop !== sum.safe_stop) announce(sum.safe_stop ? 'SAFE-STOP engaged. All agents paused.' : 'Operations resumed.');
    lastSafeStop = sum.safe_stop;

    const r = s.resources;
    const tile = (label, val, budget, unit) => {
        const ok = budget === null || val <= budget;
        return `<div class="tile"><div class="l">${label}</div><div class="v ${ok ? 'ok' : 'over'}">${val}${unit}</div>
                <div class="s">${budget === null ? '&nbsp;' : `budget ${budget}${unit}`}</div></div>`;
    };
    $('profile').textContent = r.profile.toUpperCase();
    $('edge').innerHTML = tile('CPU', r.cpu_cores, r.cpu_budget, ' cores') + tile('Memory', r.rss_mb, r.rss_budget, ' MB')
        + tile('UI Stream', r.ui_kbps, r.ui_budget, ' KB/s') + tile('Max Intent', r.intent_bytes_max, r.intent_budget, ' B')
        + tile('Pipeline', r.pipeline_ms, null, ' ms') + tile('Off-Ship', r.external_bytes, 0, ' B');

    if (newLogs.length) {
        const log = $('log');
        log.insertAdjacentHTML('beforeend', newLogs.map(logLine).join(''));
        while (log.children.length > 500) log.children[1].remove();
        if (!paused) $('logpane').scrollTop = $('logpane').scrollHeight;
    }
}

// Hold-to-engage button (2 s). Fires SAFE-STOP while running, RESUME while stopped.
// Pointer: press and hold. Keyboard: hold Enter or Space for the same 2 s; releasing, Escape or leaving the button cancels.
// A tap or a key-repeat never fires it, so it is no easier to trigger by accident than the pointer hold.
// A plain interval (not requestAnimationFrame), so it still fires if the window isn't focused.
(function holdButton() {
    const HOLD_MS = 2000;
    const btn = $('hold'), fill = btn.querySelector('.fill');
    let start = 0, timer = 0, via = '';
    const isStopped = () => !!BW.state?.summary.safe_stop;
    const action = () => isStopped() ? 'resume operations' : 'engage SAFE-STOP';
    const stop = () => { clearInterval(timer); timer = 0; via = ''; fill.style.width = '0'; };
    const tick = () => {
        const p = Math.min(1, (performance.now() - start) / HOLD_MS);
        fill.style.width = `${p * 100}%`;
        if (p >= 1) {
            stop();
            const stopped = isStopped();
            BW.act('POST', stopped ? '/api/control/resume-all' : '/api/control/safe-stop', null,
                stopped ? 'Operations resumed' : 'SAFE-STOP engaged').then(r => {
                    if (!r) announce(`Could not ${stopped ? 'resume operations' : 'engage SAFE-STOP'}.`);   // success is announced from the new state
                });
        }
    };
    const begin = (how) => {
        if (timer) return;
        via = how;
        start = performance.now();
        timer = setInterval(tick, 30);
    };
    const cancelKey = () => {
        if (!timer || via !== 'key') return;
        stop();
        announce(`Cancelled. Not engaged.`);
    };
    const isHoldKey = (e) => (e.key === 'Enter' || e.key === ' ' || e.key === 'Spacebar') && !e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey;
    btn.addEventListener('pointerdown', (e) => {
        e.preventDefault();
        begin('pointer');
    });
    ['pointerup', 'pointerleave', 'pointercancel'].forEach(ev => btn.addEventListener(ev, () => { if (via !== 'key') stop(); }));
    btn.addEventListener('keydown', (e) => {
        if (e.key === 'Escape') return cancelKey();
        if (!isHoldKey(e)) return;
        e.preventDefault();                 // no native click: a keypress alone does nothing
        if (e.repeat || timer) return;      // auto-repeat never restarts or re-fires the hold
        begin('key');
        announce(`Holding to ${action()}. Keep holding for 2 seconds, release to cancel.`);
    });
    btn.addEventListener('keyup', (e) => {
        if (!isHoldKey(e)) return;
        e.preventDefault();                 // Space would otherwise click on release
        cancelKey();
    });
    btn.addEventListener('blur', cancelKey);
    // A click that did not come from the pointer (assistive-technology "activate") only explains how to do it.
    btn.addEventListener('click', (e) => {
        e.preventDefault();
        if (e.detail === 0) announce(`Hold Enter or Space for 2 seconds to ${action()}.`);
    });
})();

document.querySelectorAll('[data-act]').forEach(b => b.addEventListener('click', async () => {
    const a = b.dataset.act;
    if (a === 'verify') {
        const r = await BW.act('GET', '/api/ledger/verify');
        if (r) BW.toast(r.valid ? `Ledger chain valid (${r.rows} rows)` : `Ledger BROKEN at row ${r.broken_at}`);
    } else if (a === 'rollback') {
        const r = await BW.act('POST', '/api/control/rollback', { count: 1 });
        if (r) BW.toast(r.undone.length ? 'Rolled back last action' : 'Nothing to roll back');
    } else if (a === 'main') {
        window.open('/index.html', 'bw-main');
    } else if (a === 'reset') {
        // Straight reset, no acknowledgement (presenter's choice, 2026-10-26).
        await BW.act('POST', '/api/scenario/reset', null, 'Reset complete');
        const log = $('log');
        while (log.children.length > 1) log.children[1].remove();
    } else {
        BW.act('POST', `/api/scenario/${a}`, null, a === 'start' ? 'Operations started' : 'Paused');
    }
}));
$('gq').addEventListener('click', () => BW.act('POST', `/api/control/gq/${BW.state?.cop.systems.gq ? 'off' : 'on'}`));
document.querySelectorAll('[data-presenter]').forEach(b => b.addEventListener('click', () => {
    const p = b.dataset.presenter;
    BW.act('POST', `/api/presenter/${p}`, null, p === 'normal' ? 'Normal conditions set' : 'Auto firefight started');
}));
document.querySelectorAll('#injmode button').forEach(b => b.addEventListener('click', () =>
    BW.act('POST', `/api/presenter/auto-inject/${b.dataset.mode}`, null, b.dataset.mode === 'auto' ? 'Auto injection ON' : 'Manual injection')));
document.querySelectorAll('[data-inject]').forEach(b => b.addEventListener('click', () =>
    BW.act('POST', `/api/inject/${b.dataset.inject}`, null, `Injected: ${b.textContent}`)));

// Inject Issues: one popup, a column per COP. Ids match App.Injections on the host.
const INJECT_MENU = [
    ['Nav COP', [['contact', 'Crossing Contact'], ['close-crossing', 'Close Crossing'], ['head-on', 'Head-on Contact'],
        ['overtaking', 'Overtaking'], ['fishing', 'Fishing Vessel'], ['hostile', 'Hostile Surface'],
        ['friendly', 'Friendly Collision'], ['submarine-detected', 'Sub Detected'], ['submarine-hostile', 'Hostile Sub'], ['submarine', 'Hidden Sub'],
        ['storm', 'Squall Line'], ['gale', 'Gale'], ['fog', 'Dense Fog'], ['calm', 'Clear Weather']]],
    ['Sys COP', [['inbound', 'Missiles (2)'], ['inbound-salvo', 'Salvo (4)'], ['missiles-random', 'Missiles Random'],
        ['aircraft', 'Strike Aircraft'], ['aircraft-pair', 'Aircraft Pair'], ['torpedoes-random', 'Torpedo Spread (4)'],
        ['torpedo-close', 'Torpedoes Close (2)'], ['overheat', 'Engine Overheat'],
        ['engine-loss', 'Loss of Shaft 2'], ['shaft1-loss', 'Loss of Shaft 1'], ['steering-loss', 'Loss of Steering'],
        ['gen-trip', 'Generator 1 Trip'], ['gen2-trip', 'Generator 2 Trip'], ['swbd-fault', 'Switchboard 1 Fault'],
        ['swbd2-fault', 'Switchboard 2 Fault'], ['lc3-loss', 'Load Center 3 Loss'], ['aft-power-loss', 'Aft Power Loss (Patch Cables)'],
        ['radar-loss', 'Radar Casualty'], ['chw-loss', 'Chilled Water Loss'], ['ciws-fault', 'CIWS Casualty'], ['clear-fault', 'Clear Fault']]],
    ['DC COP', [['fire', 'Fire MER 2'], ['fire-mer1', 'Fire MER 1'], ['fire-amr1', 'Fire AMR 1'], ['fire-cic', 'Fire in CIC'],
        ['fire-vlsf', 'Fire Fwd VLS'], ['fire-vlsa', 'Fire Aft VLS'], ['flooding', 'Flooding Aft'], ['flood-bow', 'Flooding Fwd'],
        ['flood-amr2', 'Flooding AMR 2'], ['flood-mer1', 'Flood MER 1']]],
    // Red-team and resilience tests of the Warden itself.
    ['Warden Tests', [['rogue-tao', 'Compromised AI Commander'], ['rogue', 'Rogue Lookout Agent'], ['llm-outage', 'AI Model Outage (60 s)'],
        ['system-check', 'System Check Now']]],
];

function openInjectMenu() {
    const scrim = document.createElement('div');
    scrim.className = 'scrim';
    scrim.innerHTML = `<div class="modal inject-modal" role="dialog" aria-modal="true" aria-label="Inject issues">
        <div class="m-title">Inject Issues</div>
        <div class="inject-cols">${INJECT_MENU.map(([cop, items]) => `<div><div class="label orange">${esc(cop)}</div>
            <div class="grid2">${items.map(([id, label]) => `<button class="btn warn" data-inj="${id}" title="${esc(label)}">${esc(label)}</button>`).join('')}</div></div>`).join('')}</div>
        <div class="m-foot"><span></span><button class="btn lg" data-close>Close</button></div></div>`;
    document.body.appendChild(scrim);
    const close = () => { scrim.remove(); document.removeEventListener('keydown', onKey); };
    const onKey = (e) => { if (e.key === 'Escape') close(); };
    scrim.querySelector('[data-close]').addEventListener('click', close);
    scrim.querySelectorAll('[data-inj]').forEach(b => b.addEventListener('click', () => {
        BW.act('POST', `/api/inject/${b.dataset.inj}`, null, `Injected: ${b.textContent}`);   // stays open: Escape or Close
    }));
    document.addEventListener('keydown', onKey);
}
$('inject-open').addEventListener('click', openInjectMenu);

// AI model server: fields fill once from the server, then belong to the operator.
let llmFilled = false;

// Model drop-down: "auto" first, then what the server lists. A chosen model the server doesn't list stays, marked.
function setModelOptions(models, chosen) {
    const sel = $('llm-model');
    const keep = chosen !== undefined ? chosen : sel.value;
    const list = models.slice();
    const opts = [`<option value="">Model: auto</option>`].concat(list.map(m => `<option value="${esc(m)}">${esc(m)}</option>`));
    if (keep && !list.includes(keep)) opts.push(`<option value="${esc(keep)}">${esc(keep)} (not listed)</option>`);
    sel.innerHTML = opts.join('');
    sel.value = keep || '';
}

// Asks the server at the URL box for its models (GET <url>/models via Warden; the key never goes in a URL).
async function loadModels() {
    const info = $('llm-models-info'), btn = $('llm-refresh');
    const url = $('llm-url').value.trim();
    if (!url) return;
    btn.disabled = true;
    info.textContent = 'Loading models...';
    try {
        const r = await BW.api('POST', '/api/llm/models', { base_url: url, api_key: $('llm-key').value });
        setModelOptions(r.models);
        info.textContent = r.router === 'fairlead'
            ? `Fairlead: ${r.models.length} routes and models. Auto = the "local" route (phoenix-lai). Key from Credential Manager.`
            : r.models.length ? `${r.models.length} model${r.models.length === 1 ? '' : 's'} at this server.`
                              : 'The server lists no models (none loaded?).';
    } catch (e) {
        let detail = e.message;
        try { detail = JSON.parse(e.message.replace(/^\d+ /, '')).detail || detail; } catch (_) { }
        info.textContent = `Couldn't load models: ${detail}`;
    } finally {
        btn.disabled = false;
    }
}
$('llm-refresh').addEventListener('click', loadModels);
// Fairlead preset: its default address; model auto (= Fairlead's "local" route). Apply saves it.
$('llm-fairlead').addEventListener('click', () => {
    $('llm-url').value = 'http://127.0.0.1:8776/v1';
    $('llm-key').value = '';
    setModelOptions([], '');
    loadModels();
});
$('llm-url').addEventListener('change', loadModels);
BW.onState((s) => {
    if (llmFilled || !s) return;
    llmFilled = true;
    $('llm-url').value = s.server.llm.base_url;
    setModelOptions([], s.server.llm.local_model);
    loadModels();
    $('llm-effort').value = s.server.llm.reasoning_effort;
    $('llm-thoughts').checked = s.server.llm.include_thoughts;
});
$('llmform').addEventListener('submit', async (e) => {
    e.preventDefault();
    const r = await BW.act('POST', '/api/llm/endpoint',
        { base_url: $('llm-url').value, api_key: $('llm-key').value, model: $('llm-model').value,
          reasoning_effort: $('llm-effort').value, include_thoughts: $('llm-thoughts').checked });
    if (r) { $('llm-key').value = ''; Notify.show({ variant: 'success', title: `AI model server set to ${r.base_url}` }); }
});

$('logpane').addEventListener('mouseenter', () => { paused = true; });
$('logpane').addEventListener('mouseleave', () => { paused = false; });

BW.onState((s, logs) => { render(s, logs); NotifyWatch.update(s, () => window.open('/index.html', 'bw-main')); });
BW.connect();
