// Live log panel. Use Log.info / Log.ok / Log.warn / Log.error anywhere.
const Log = (() => {
    const MAX_LINES = 1000;
    const body = () => document.getElementById('log');

    function write(level, msg) {
        const el = body();
        if (!el) return;
        const line = document.createElement('div');
        line.className = `log-line ${level}`;
        const t = new Date().toLocaleTimeString([], { hour12: false });
        const text = typeof msg === 'string' ? msg : JSON.stringify(msg);
        line.innerHTML = `<span class="t">${t}</span><span class="lvl">${level.toUpperCase()}</span><span class="m"></span>`;
        line.querySelector('.m').textContent = text;
        el.appendChild(line);
        while (el.childElementCount > MAX_LINES) el.firstChild.remove();
        if (document.getElementById('log-autoscroll')?.checked) el.scrollTop = el.scrollHeight;
    }

    // Catch anything that slips through
    window.addEventListener('error', e => write('error', `${e.message} (${e.filename?.split('/').pop()}:${e.lineno})`));
    window.addEventListener('unhandledrejection', e => write('error', `Unhandled: ${e.reason?.message || e.reason}`));

    return {
        info: m => write('info', m),
        ok: m => write('ok', m),
        warn: m => write('warn', m),
        error: m => write('error', m),
        clear: () => { body().innerHTML = ''; },
    };
})();
