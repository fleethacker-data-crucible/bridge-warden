// Notification banners: top center, anti-flood (style guide 7.17), and the typed-acknowledgement modal (7.18).

const Notify = (() => {
    const DURATION = { system: 5000, success: 5000, warn: 6000, error: 8000, decision: 8000 };
    const MAX_VISIBLE = 3, DEDUPE_MS = 10000, RATE_MS = 1000, ARM_MS = 750;
    const visible = [];          // {key, el, timer, remaining, started, data}
    const queue = [];
    let lastShown = 0, root, more, pill, onReview = () => {};
    let scope = null;            // null = show everything; 'nav' | 'systems' | 'dc' = only that COP + global
    const inScope = (d) => !scope || !d || d === 'global' || d === scope;

    function mount() {
        root = document.createElement('div');
        root.className = 'notify-root';
        more = document.createElement('div');
        more.className = 'notify-more';
        pill = document.createElement('button');
        pill.className = 'notify-pill';
        pill.addEventListener('click', () => onReview());
        root.append(more, pill);
        document.body.appendChild(root);
        position();
        window.addEventListener('resize', position);
    }

    // 24 px below the app header (and below the SAFE-STOP strip when it shows).
    function position() {
        const h = document.querySelector('.header');
        if (root && h) root.style.top = `${h.getBoundingClientRect().bottom + 24}px`;
    }

    function show(n) {
        // n: {variant, title, body?, key?, actions?: [{label, cls, fn}], deadline?: epoch ms, safeDefault?}
        if (!root) mount();
        n.key = n.key || `${n.variant}|${n.title}|${n.body || ''}`;
        if (!inScope(n.domain)) return;          // belongs to another COP; it's still in the log
        const dup = visible.find(v => v.key === n.key);
        if (dup) {
            dup.count += 1;
            dup.el.querySelector('.n-count').textContent = `×${dup.count}`;
            restart(dup);
            return;
        }
        if (queue.some(q => q.key === n.key)) return;
        if (n.variant === 'decision') {
            // Decisions jump the queue but never exceed the cap: bump a non-decision banner, or wait first in line.
            const victim = visible.length >= MAX_VISIBLE && visible.find(x => x.data.variant !== 'decision');
            if (victim) { close(victim, true); queue.push(victim.data); }
            if (visible.length < MAX_VISIBLE) render(n, true);
            else queue.unshift(n);
            pump();
            return;
        }
        queue.push(n);
        pump();
    }

    function pump() {
        if (!root) mount();
        position();
        while (queue.length && visible.length < MAX_VISIBLE) {
            const wait = RATE_MS - (Date.now() - lastShown);
            if (wait > 0) { setTimeout(pump, wait); break; }
            render(queue.shift(), false);
        }
        more.textContent = queue.length ? `+${queue.length} more` : '';
        more.style.display = queue.length ? 'block' : 'none';
    }

    function render(n, top) {
        lastShown = Date.now();
        const el = document.createElement('div');
        el.className = `notify n-${n.variant}`;
        el.innerHTML = `<div class="n-main"><div class="n-title">${esc(n.title)} <span class="n-count"></span></div>
            ${n.body ? `<div class="n-body">${esc(n.body.length > 170 ? n.body.slice(0, 167) + '…' : n.body)}</div>` : ''}
            ${n.deadline ? `<div class="n-deadline"></div>` : ''}
            ${n.actions ? `<div class="n-actions">${n.actions.map((a, i) => `<button class="btn ${a.cls}" data-i="${i}" disabled>${esc(a.label)}</button>`).join('')}</div>` : ''}</div>
            <button class="n-close" aria-label="Close">×</button>${n.deadline ? '<div class="n-bar"><div></div></div>' : ''}`;
        const v = { key: n.key, el, count: 1, data: n, remaining: DURATION[n.variant], started: 0, timer: 0 };
        el.querySelector('.n-close').addEventListener('click', () => close(v));
        el.addEventListener('mouseenter', () => pause(v));
        el.addEventListener('mouseleave', () => resume(v));
        (n.actions || []).forEach((a, i) => {
            const b = el.querySelector(`[data-i="${i}"]`);
            b.addEventListener('click', () => { a.fn(); close(v); });
            setTimeout(() => { b.disabled = false; }, ARM_MS);   // arming delay: no accidental clicks
        });
        if (top) root.insertBefore(el, root.firstChild); else root.insertBefore(el, more);
        visible.push(v);
        void el.offsetHeight;          // force layout so the slide-in starts from the offset position
        el.classList.add('in');
        if (n.deadline) tickDeadline(v);
        resume(v);
        // Hard cap, whatever the timing: send the oldest extra banner (non-decision first) back to the queue.
        while (visible.length > MAX_VISIBLE) {
            const extra = visible.find(x => x !== v && x.data.variant !== 'decision') || visible.find(x => x !== v);
            close(extra, true);
            if (extra.data.variant === 'decision') queue.unshift(extra.data); else queue.push(extra.data);
        }
    }

    function tickDeadline(v) {
        const update = () => {
            if (!v.el.isConnected) return;
            const left = Math.max(0, (v.data.deadline - Date.now()) / 1000);
            v.el.querySelector('.n-deadline').textContent = `Safe default in ${left.toFixed(0)} s → ${v.data.safeDefault || ''}`;
            v.el.querySelector('.n-bar > div').style.width = `${Math.min(100, left / (v.data.totalS || 45) * 100)}%`;
            setTimeout(update, 250);
        };
        update();
    }

    function resume(v) { v.started = Date.now(); clearTimeout(v.timer); v.timer = setTimeout(() => close(v), v.remaining); }
    function pause(v) { clearTimeout(v.timer); v.remaining = Math.max(1500, v.remaining - (Date.now() - v.started)); }
    function restart(v) { v.remaining = DURATION[v.data.variant]; resume(v); }

    function close(v, silent) {
        clearTimeout(v.timer);
        const i = visible.indexOf(v);
        if (i >= 0) visible.splice(i, 1);
        v.el.remove();
        if (!silent) pump();
    }

    // Persistent pill: decisions never disappear just because their banner timed out.
    function pending(n, review, elsewhere = 0) {
        if (!root) mount();
        onReview = review || onReview;
        const banners = visible.filter(v => v.data.variant === 'decision').length;
        pill.style.display = (n > 0 && banners === 0) || (n === 0 && elsewhere > 0) ? 'inline-flex' : 'none';
        pill.textContent = n > 0 ? `⚑ ${n} decision${n === 1 ? '' : 's'} pending here${elsewhere ? ` · ${elsewhere} on other COPs` : ''} · Review`
            : `⚑ ${elsewhere} decision${elsewhere === 1 ? '' : 's'} pending on other COPs`;
        position();
    }

    function setScope(d) {
        scope = d;
        visible.filter(v => !inScope(v.data.domain)).forEach(v => close(v, true));
        for (let i = queue.length - 1; i >= 0; i--) if (!inScope(queue[i].domain)) queue.splice(i, 1);
        pump();
    }

    function dismissKey(key) { visible.filter(v => v.key === key).forEach(v => close(v)); }

    return { show, pending, dismissKey, setScope, inScope: (d) => inScope(d) };
})();

// Turns live state changes into banners. Shared by both windows.
const NotifyWatch = (() => {
    let seenAlerts = null, seenEsc = new Map(), wasStopped = null;

    function decide(id, approve) {
        BW.act('POST', `/api/escalations/${id}/${approve ? 'approve' : 'reject'}`, null, approve ? 'Approved' : 'Rejected');
    }

    // decisions: false where the page already shows Approve / Deny cards (the main app's right column); alerts,
    // SAFE-STOP and everything else still notify.
    function update(s, review, decisions = true) {
        if (!s) return;
        // Alerts (critical → error, warn → warn). Skip the backlog on first load.
        if (seenAlerts === null) seenAlerts = new Set(s.alerts.map(a => a.id));
        for (const a of s.alerts.slice().reverse()) {
            if (seenAlerts.has(a.id)) continue;
            seenAlerts.add(a.id);
            if (a.severity === 'critical') Notify.show({ variant: 'error', title: a.message, body: `Source: ${a.source}`, domain: a.domain });
        }
        // Escalations: new open → decision banner; timed out → warn.
        for (const e of s.escalations) {
            const prev = seenEsc.get(e.id);
            // System checks (confirm autonomous ops) never pop up: they sit in every COP's decisions column.
            if (decisions && prev === undefined && e.status === 'open' && e.kind !== 'system_check') {
                const p = e.proposal || {};
                const check = e.kind === 'system_check';
                Notify.show({
                    variant: 'decision', key: `esc|${e.id}`, domain: e.domain,
                    title: check ? 'System check: confirm autonomous operations' : `Decision needed: ${e.title}`,
                    body: check ? e.summary : p.current ? `Current ${p.current} → proposed ${p.value}. ${e.summary}` : e.summary,
                    deadline: Date.now() + e.remaining_s * 1000, totalS: e.timeout_s, safeDefault: e.safe_default,
                    actions: [{ label: e.approve_label, cls: 'success', fn: () => decide(e.id, true) },
                              { label: e.reject_label, cls: 'danger', fn: () => decide(e.id, false) }],
                });
            } else if (prev === 'open' && e.status !== 'open') {
                Notify.dismissKey(`esc|${e.id}`);
                // Timeouts are in the log; the decision card already showed the safe default.
            }
            seenEsc.set(e.id, e.status);
        }
        // SAFE-STOP transitions.
        const stopped = s.summary.safe_stop;
        if (wasStopped !== null && stopped && !wasStopped) {
            Notify.show({ variant: 'error', title: 'SAFE-STOP engaged', body: 'All agents paused · holding course at safe speed' });
        }
        wasStopped = stopped;
        if (!decisions) return;
        const open = s.escalations.filter(e => e.status === 'open' && e.kind !== 'system_check');
        const here = open.filter(e => Notify.inScope(e.domain)).length;
        Notify.pending(here, review, open.length - here);
    }

    return { update, decide };
})();

// Typed-acknowledgement modal for irreversible actions (style guide 7.18).
function confirmDestructive({ title, question, warning, action }) {
    return new Promise((resolve) => {
        const scrim = document.createElement('div');
        scrim.className = 'scrim';
        scrim.innerHTML = `<div class="modal danger" role="dialog" aria-modal="true">
            <div class="m-title">${esc(title)}</div><div class="m-q">${esc(question)}</div>
            <div class="m-warn">WARNING:</div><div class="m-warn-text">${esc(warning)}</div>
            <div class="m-label">Type I Acknowledge to continue</div><input class="m-input" autocomplete="off">
            <div class="m-foot"><button class="btn lg" data-ok disabled>${esc(action)}</button><button class="btn lg" data-cancel>Cancel</button></div></div>`;
        document.body.appendChild(scrim);
        const input = scrim.querySelector('input'), ok = scrim.querySelector('[data-ok]');
        const done = (v) => { scrim.remove(); document.removeEventListener('keydown', onKey); resolve(v); };
        const onKey = (e) => { if (e.key === 'Escape') done(false); };
        input.addEventListener('input', () => {
            const armed = input.value.trim().toLowerCase() === 'i acknowledge';
            ok.disabled = !armed;
            ok.classList.toggle('danger', armed);
        });
        ok.addEventListener('click', () => done(true));
        scrim.querySelector('[data-cancel]').addEventListener('click', () => done(false));
        document.addEventListener('keydown', onKey);
        input.focus();
    });
}
