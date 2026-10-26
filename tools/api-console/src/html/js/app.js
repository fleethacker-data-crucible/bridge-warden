// App shell: sidebar navigation, top-bar status, settings page, generic API page.
const $ = sel => document.querySelector(sel);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const state = { currentApi: null, apiStatus: {} };

// ---------- Top bar status ----------
function setStat(name, text, level) {
    $(`#stat-${name}`).textContent = text;
    $(`#dot-${name}`).className = `dot ${level || ''}`;
}

async function refreshServer(quiet) {
    try {
        const h = await ApiClient.health();
        setStat('server', `online · ${h.engine}`, 'ok');
        $('#meta-server').textContent = `🖥 ${location.host} · ${h.engine}`;
        return true;
    } catch {
        setStat('server', 'offline', 'err');
        $('#meta-server').textContent = '🖥 server offline';
        if (!quiet) Log.error('Local server is not responding');
        return false;
    }
}

async function refreshKey() {
    try {
        const k = await ApiClient.getKeyStatus();
        setStat('key', k.hasKey ? k.keyPreview : 'not set', k.hasKey ? 'ok' : 'warn');
        $('#meta-key').textContent = k.hasKey ? `🔑 key ${k.keyPreview}` : '🔑 no API key';
        return k;
    } catch {
        setStat('key', 'unknown', 'err');
        return { hasKey: false };
    }
}

// ---------- Sidebar ----------
function renderNav() {
    $('#api-list').innerHTML = APIS.map(api => `
        <button class="nav-item" data-api="${esc(api.id)}">
            <span class="nav-icon">${esc(api.icon || '◇')}</span>
            <span class="nav-label">${esc(api.name)}</span>
            <span class="dot" id="nav-dot-${esc(api.id)}"></span>
        </button>`).join('');
    $('#api-list').addEventListener('click', e => {
        const btn = e.target.closest('.nav-item');
        if (btn) openApi(btn.dataset.api);
    });
}

function setActiveNav(id) {
    document.querySelectorAll('.nav-item').forEach(b => b.classList.toggle('active', b.dataset.api === id));
}

function setApiStatus(apiId, result) {
    const level = result.ok ? 'ok' : 'err';
    state.apiStatus[apiId] = { level, text: result.status ? `${result.status} · ${result.ms}ms` : 'network error' };
    const dot = $(`#nav-dot-${apiId}`);
    if (dot) dot.className = `dot ${level}`;
    if (state.currentApi === apiId) setStat('api', state.apiStatus[apiId].text, level);
}

// ---------- Settings page ----------
async function openSettings() {
    state.currentApi = null;
    setActiveNav(null);
    $('#page-title').textContent = 'Settings';
    setStat('api', 'idle');
    const k = await refreshKey();
    $('#view').innerHTML = `
        <div class="card">
            <h2>API key</h2>
            <p class="desc">Paste the event API key once. It is stored by the local server in
            <code>src/config.json</code> (gitignored) and injected into every API call — it never sits in the browser.</p>
            <div class="key-row">
                <input id="key-input" type="password" placeholder="${k.hasKey ? 'Saved: ' + esc(k.keyPreview) + ' — paste to replace' : 'Paste API key'}" autocomplete="off">
                <button id="key-show" class="btn-ghost">show</button>
                <button id="key-save" class="btn">Save</button>
            </div>
        </div>`;
    $('#key-show').onclick = () => {
        const i = $('#key-input');
        i.type = i.type === 'password' ? 'text' : 'password';
        $('#key-show').textContent = i.type === 'password' ? 'show' : 'hide';
    };
    $('#key-save').onclick = async () => {
        const v = $('#key-input').value.trim();
        if (!v) { Log.warn('Key input is empty — nothing saved'); return; }
        await ApiClient.saveKey(v);
        $('#key-input').value = '';
        const s = await refreshKey();
        Log.ok(`API key saved (${s.keyPreview})`);
    };
}

// ---------- Generic API page ----------
function openApi(id) {
    const api = APIS.find(a => a.id === id);
    if (!api) return;
    state.currentApi = id;
    setActiveNav(id);
    $('#page-title').textContent = api.name;
    const st = state.apiStatus[id];
    setStat('api', st ? st.text : 'idle', st?.level);
    Log.info(`Opened ${api.name}`);

    const view = $('#view');
    view.innerHTML = '';
    const ctx = { api, call: (ep, values) => callEndpoint(api, ep, values), log: Log };
    if (typeof api.render === 'function') { api.render(view, ctx); return; }

    view.innerHTML = `
        <div class="card">
            <h2>${esc(api.name)}</h2>
            <p class="desc">${esc(api.description || '')}</p>
            <div class="endpoint-tabs">
                ${(api.endpoints || []).map((ep, i) => `
                    <button class="tab ${i === 0 ? 'active' : ''}" data-ep="${esc(ep.id)}">
                        <span class="method ${esc(ep.method)}">${esc(ep.method)}</span>${esc(ep.name)}
                    </button>`).join('')}
            </div>
            <div id="ep-form"></div>
        </div>
        <div class="card" id="resp-card" hidden>
            <h3>Response</h3>
            <div class="resp-meta" id="resp-meta"></div>
            <pre class="resp" id="resp-body"></pre>
        </div>`;

    view.querySelector('.endpoint-tabs').addEventListener('click', e => {
        const tab = e.target.closest('.tab');
        if (!tab) return;
        view.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t === tab));
        renderEndpointForm(api, api.endpoints.find(ep => ep.id === tab.dataset.ep));
    });
    if (api.endpoints?.length) renderEndpointForm(api, api.endpoints[0]);
}

function renderEndpointForm(api, ep) {
    const params = ep.params || [];
    const hasBody = ['POST', 'PUT', 'PATCH'].includes(ep.method);
    $('#ep-form').innerHTML = `
        <p class="desc">${esc(ep.description || '')}</p>
        <div class="url-line"><span class="method ${esc(ep.method)}">${esc(ep.method)}</span>${esc(api.baseUrl + ep.path)}</div>
        <div class="form-grid">
            ${params.map(p => `
                <label for="p-${esc(p.name)}">${esc(p.label || p.name)} <small>(${esc(p.in)})</small></label>
                <input id="p-${esc(p.name)}" data-param="${esc(p.name)}" value="${esc(p.default)}">
                ${p.hint ? `<div class="hint">${esc(p.hint)}</div>` : ''}`).join('')}
            ${hasBody ? `
                <label for="p-body">JSON body</label>
                <textarea id="p-body" rows="8">${esc(JSON.stringify(ep.body ?? {}, null, 2))}</textarea>` : ''}
        </div>
        <button class="btn" id="btn-send">Send request</button>`;

    $('#btn-send').onclick = async () => {
        const values = {};
        document.querySelectorAll('[data-param]').forEach(i => { values[i.dataset.param] = i.value; });
        if (hasBody) {
            try { values.__body = JSON.parse($('#p-body').value || '{}'); }
            catch (err) { Log.error(`Invalid JSON body: ${err.message}`); return; }
        }
        $('#btn-send').disabled = true;
        try { showResponse(await callEndpoint(api, ep, values)); }
        finally { $('#btn-send').disabled = false; }
    };
}

// Builds the URL/headers from the registry entry and sends it through the proxy.
async function callEndpoint(api, ep, values = {}) {
    let path = ep.path;
    const query = new URLSearchParams();
    for (const p of ep.params || []) {
        const v = values[p.name] ?? p.default ?? '';
        if (p.in === 'path') path = path.replace(`{${p.name}}`, encodeURIComponent(v));
        else if (v !== '') query.append(p.name, v);
    }
    const headers = { Accept: 'application/json' };
    if (api.auth?.header) headers[api.auth.header] = api.auth.value;
    let qs = query.toString();
    if (api.auth?.query) qs += (qs ? '&' : '') + `${encodeURIComponent(api.auth.query)}={{API_KEY}}`;
    const url = api.baseUrl + path + (qs ? `?${qs}` : '');

    const body = values.__body ?? ep.body;
    const shownUrl = url.replace(/\{\{API_KEY\}\}/g, '***');
    Log.info(`→ ${ep.method} ${shownUrl}`);
    if (state.currentApi === api.id) setStat('api', 'calling…', 'busy');

    let result;
    try {
        result = await ApiClient.request({ method: ep.method, url, headers, body: ['GET', 'DELETE'].includes(ep.method) ? undefined : body });
    } catch (err) {
        result = { ok: false, status: 0, ms: 0, error: err.message, body: '' };
    }

    if (result.ok) Log.ok(`← ${result.status} ${result.statusText} · ${result.ms}ms · ${api.name} / ${ep.name}`);
    else if (result.status) Log.error(`← ${result.status} ${result.statusText} · ${result.ms}ms · ${api.name} / ${ep.name}: ${(result.body || '').slice(0, 300)}`);
    else Log.error(`✕ ${api.name} / ${ep.name}: ${result.error}`);
    setApiStatus(api.id, result);
    return result;
}

function showResponse(r) {
    $('#resp-card').hidden = false;
    const cls = r.ok ? 'ok' : 'err';
    $('#resp-meta').innerHTML = `
        <span class="code ${cls}">${r.status ? esc(r.status + ' ' + (r.statusText || '')) : 'Network error'}</span>
        <span>${esc(r.ms)} ms</span>
        <span>${esc(r.contentType || '')}</span>`;
    $('#resp-body').textContent = r.data ? JSON.stringify(r.data, null, 2) : (r.body || r.error || '(empty)');
}

// ---------- Boot ----------
async function boot() {
    renderNav();
    $('#btn-settings').onclick = openSettings;
    $('#log-clear').onclick = Log.clear;
    $('#view').innerHTML = `<div class="empty"><div class="big">CDAO Maximus</div>Pick an API on the left, or set your key with ⚙.</div>`;

    Log.info('CDAO Maximus UI loaded');
    if (await refreshServer()) Log.ok(`Connected to local server ${location.host}`);
    const k = await refreshKey();
    if (!k.hasKey) Log.warn('No API key saved — click ⚙ to add one');
    Log.info(`${APIS.length} API(s) registered`);

    let wasUp = true;
    setInterval(async () => {
        const up = await refreshServer(true);
        if (up !== wasUp) (up ? Log.ok('Local server back online') : Log.error('Lost connection to local server'));
        wasUp = up;
    }, 5000);
}

boot();
