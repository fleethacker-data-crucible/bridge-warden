// CDAO Maximus APIs - Deno server engine (engine: "deno" in config.json).
// Launched by server.ps1:  deno run ... server.js <port> <htmlRoot> <secretsPath>
// Same routes as the native PowerShell engine; no imports, so it runs offline.

const [portArg, htmlRoot, secretsPath] = Deno.args;
const port = Number(portArg) || 8080;

const MIME = {
    '.html': 'text/html; charset=utf-8',
    '.css': 'text/css; charset=utf-8',
    '.js': 'application/javascript; charset=utf-8',
    '.json': 'application/json; charset=utf-8',
    '.svg': 'image/svg+xml',
    '.png': 'image/png',
    '.jpg': 'image/jpeg',
    '.jpeg': 'image/jpeg',
    '.gif': 'image/gif',
    '.ico': 'image/x-icon',
    '.woff': 'font/woff',
    '.woff2': 'font/woff2',
    '.txt': 'text/plain; charset=utf-8',
};

const json = (obj, status = 200) =>
    new Response(JSON.stringify(obj), {
        status,
        headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' },
    });

// --- Secrets: API key is DPAPI-encrypted in secrets.json (gitignored) and kept in memory.
// Deno has no DPAPI, so encrypt/decrypt goes through secrets.ps1 over stdin/stdout.
const secretsScript = `${htmlRoot}/../secrets.ps1`;
const b64 = s => btoa(String.fromCharCode(...new TextEncoder().encode(s)));
const unb64 = s => new TextDecoder().decode(Uint8Array.from(atob(s), c => c.charCodeAt(0)));

async function dpapi(action, input) {
    const child = new Deno.Command('powershell.exe', {
        args: ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', secretsScript, '-Action', action],
        stdin: 'piped', stdout: 'piped', stderr: 'piped',
    }).spawn();
    const writer = child.stdin.getWriter();
    await writer.write(new TextEncoder().encode(input));
    await writer.close();
    const { code, stdout, stderr } = await child.output();
    if (code !== 0) throw new Error(`DPAPI ${action} failed: ${new TextDecoder().decode(stderr).trim()}`);
    return new TextDecoder().decode(stdout).trim();
}

let apiKey = null;

async function getApiKey() {
    if (apiKey !== null) return apiKey;
    apiKey = '';
    let secrets = {};
    try { secrets = JSON.parse(await Deno.readTextFile(secretsPath)); } catch { /* no key saved yet */ }
    if (secrets.apiKeyProtected) {
        try { apiKey = unb64(await dpapi('unprotect', secrets.apiKeyProtected)); }
        catch (err) { console.error(`Could not decrypt the saved API key: ${err.message}`); }
    } else if (secrets.apiKey) {
        await setApiKey(secrets.apiKey);   // migrate an old plain-text key
    }
    return apiKey;
}

async function setApiKey(key) {
    apiKey = String(key).trim();
    const data = apiKey ? { apiKeyProtected: await dpapi('protect', b64(apiKey)) } : {};
    await Deno.writeTextFile(secretsPath, JSON.stringify(data, null, 4));
}

// Replace the key (raw and URL-encoded) with *** so it never reaches the browser or log
function hideApiKey(text) {
    if (!text || !apiKey || apiKey.length < 4) return text;
    return text.replaceAll(apiKey, '***').replaceAll(encodeURIComponent(apiKey), '***');
}

async function keyStatus() {
    const key = await getApiKey();
    const keyPreview = key.length > 8 ? `${key.slice(0, 4)}...${key.slice(-4)}` : key ? '****' : '';
    return { hasKey: !!key, keyPreview };
}

// --- Proxy: browser -> server -> external API (avoids CORS, injects {{API_KEY}}) ---
async function proxy(req) {
    const { method = 'GET', url = '', headers = {}, body } = await req.json();
    const key = await getApiKey();
    const started = performance.now();
    const ms = () => Math.round(performance.now() - started);
    try {
        const outHeaders = {};
        for (const [name, value] of Object.entries(headers)) {
            outHeaders[name] = String(value).replaceAll('{{API_KEY}}', key);
        }
        const m = method.toUpperCase();
        let outBody;
        if (body != null && m !== 'GET') {
            outBody = typeof body === 'string' ? body : JSON.stringify(body);
            if (!Object.keys(outHeaders).some(h => h.toLowerCase() === 'content-type')) {
                outHeaders['Content-Type'] = 'application/json';
            }
        }
        const resp = await fetch(url.replaceAll('{{API_KEY}}', encodeURIComponent(key)), {
            method: m,
            headers: outHeaders,
            body: outBody,
            signal: AbortSignal.timeout(60_000),
        });
        const text = await resp.text();
        return json({
            ok: resp.ok,
            status: resp.status,
            statusText: resp.statusText,
            ms: ms(),
            contentType: resp.headers.get('content-type') || '',
            body: hideApiKey(text),
        });
    } catch (err) {
        return json({ ok: false, status: 0, statusText: 'Network error', ms: ms(), error: hideApiKey(err.message), body: '' });
    }
}

// --- Static files from htmlRoot ---
async function serveStatic(pathname) {
    let rel = decodeURIComponent(pathname).replace(/^\/+/, '');
    if (rel.split(/[\\/]/).includes('..')) return new Response('Forbidden', { status: 403 });
    if (!rel) rel = 'index.html';
    let full = `${htmlRoot}/${rel}`;
    try {
        if ((await Deno.stat(full)).isDirectory) full += '/index.html';
        const data = await Deno.readFile(full);
        const ext = full.slice(full.lastIndexOf('.')).toLowerCase();
        return new Response(data, {
            headers: { 'Content-Type': MIME[ext] || 'application/octet-stream', 'Cache-Control': 'no-store' },
        });
    } catch {
        return new Response(`Not found: ${rel}`, { status: 404 });
    }
}

// --- API routes: add hackathon endpoints here ---
async function handler(req) {
    const { pathname } = new URL(req.url);
    console.log(`${req.method} ${pathname}`);
    try {
        switch (pathname.toLowerCase()) {
            case '/api/health':
                return json({ status: 'ok', engine: 'deno', time: new Date().toISOString() });
            case '/api/shutdown':
                setTimeout(() => Deno.exit(0), 100);
                return json({ status: 'stopping' });
            case '/api/config':
                if (req.method === 'POST') {
                    const { apiKey: newKey = '' } = await req.json();
                    await setApiKey(newKey);
                }
                return json(await keyStatus());
            case '/api/proxy':
                return await proxy(req);
        }
        return await serveStatic(pathname);
    } catch (err) {
        console.error(err);
        return new Response(String(err?.message || err), { status: 500 });
    }
}

Deno.serve({
    port,
    hostname: '127.0.0.1',
    onListen: () => { getApiKey(); console.log(`CDAO Maximus server (deno) running at http://127.0.0.1:${port}/ (serving ${htmlRoot})`); },
}, handler);
