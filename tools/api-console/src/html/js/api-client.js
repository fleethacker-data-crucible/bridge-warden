// Talks to server.ps1. External calls go through /api/proxy so the API key
// stays on the server and CORS is not a problem. Use {{API_KEY}} in a URL or
// header value and the server substitutes the saved key.
const ApiClient = (() => {
    async function getKeyStatus() {
        const res = await fetch('/api/config');
        return res.json();
    }

    async function saveKey(apiKey) {
        const res = await fetch('/api/config', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ apiKey }),
        });
        return res.json();
    }

    async function health() {
        const res = await fetch('/api/health');
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        return res.json();
    }

    // { method, url, headers, body } -> { ok, status, statusText, ms, contentType, body, data?, error? }
    async function request({ method = 'GET', url, headers = {}, body }) {
        const res = await fetch('/api/proxy', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ method, url, headers, body }),
        });
        const result = await res.json();
        try { result.data = JSON.parse(result.body); } catch { result.data = null; }
        return result;
    }

    return { getKeyStatus, saveKey, health, request };
})();
