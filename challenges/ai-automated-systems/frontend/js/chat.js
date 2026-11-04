// Operator chat with the local model (host/src/Chat.cs). Ask about the ship, the agents and their decisions.
// The model only PROPOSES actions; each shows as a card and runs only when the operator clicks Confirm, through the
// same /api/ route the existing buttons use (so Warden, approvals and the ledger apply unchanged).
// Lives outside #content, so the per-tick re-render never touches it. The conversation stays in this window only.
(function () {
    'use strict';
    const $ = (id) => document.getElementById(id);
    const history = [];          // {role, content} sent to the model (last 8 kept server-side)
    let busy = false;

    function open(show) {
        const panel = $('chat-panel'), btn = $('chat-toggle');
        const on = show === undefined ? panel.hidden : show;
        panel.hidden = !on;
        btn.setAttribute('aria-expanded', String(on));
        if (on) $('chat-input').focus(); else btn.focus();
    }

    // The light markdown local models write: **bold** and "* " / "- " bullets. Escaped first, so it never injects HTML.
    function formatted(text) {
        return esc(text)
            .replace(/\*\*(.+?)\*\*/g, '<b>$1</b>')
            .replace(/^\s*[*-]\s+/gm, '• ');
    }

    function bubble(role, text) {
        const div = document.createElement('div');
        div.className = `chat-msg ${role}`;
        const body = document.createElement('div');
        body.className = 'chat-text';
        body.textContent = text;
        div.appendChild(body);
        $('chat-log').appendChild(div);
        $('chat-log').scrollTop = $('chat-log').scrollHeight;
        return div;
    }

    function actionCard(host, a) {
        const card = document.createElement('div');
        card.className = 'chat-action';
        card.innerHTML = `<div class="chat-action-label">${esc(a.label)}</div>
            <div class="chat-action-row"><button type="button" class="btn primary">Confirm</button>
            <button type="button" class="btn">Dismiss</button></div>`;
        const [ok, no] = card.querySelectorAll('button');
        ok.addEventListener('click', async () => {
            ok.disabled = no.disabled = true;
            const r = await BW.act(a.method, a.url, a.body || undefined);
            card.classList.add(r === undefined ? 'failed' : 'done');
            card.querySelector('.chat-action-row').textContent = r === undefined ? 'Failed (see banner).' : 'Done. Recorded in the ledger.';
        });
        no.addEventListener('click', () => { card.classList.add('dismissed'); card.querySelector('.chat-action-row').textContent = 'Dismissed.'; });
        host.appendChild(card);
    }

    // POST /api/chat/stream and read Server-Sent Events: thinking, content (chunks), done.
    async function ask(question) {
        busy = true;
        $('chat-send').disabled = true;
        history.push({ role: 'user', content: question });
        bubble('user', question);
        const reply = bubble('assistant', '…');
        const text = reply.querySelector('.chat-text');
        $('chat-status').textContent = 'Warden assistant is answering';
        let streamed = '', done = null;
        try {
            const res = await fetch('/api/chat/stream', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ messages: history }),
            });
            if (!res.ok) throw new Error(`${res.status} ${await res.text()}`);
            const reader = res.body.getReader(), dec = new TextDecoder();
            let buf = '';
            for (;;) {
                const { value, done: end } = await reader.read();
                if (end) break;
                buf += dec.decode(value, { stream: true });
                let i;
                while ((i = buf.indexOf('\n\n')) >= 0) {
                    const block = buf.slice(0, i); buf = buf.slice(i + 2);
                    const ev = (block.match(/^event: (.*)$/m) || [, ''])[1];
                    const data = JSON.parse((block.match(/^data: (.*)$/m) || [, 'null'])[1]);
                    if (ev === 'content') { streamed += data; text.textContent = streamed; $('chat-log').scrollTop = $('chat-log').scrollHeight; }
                    else if (ev === 'done') done = data;
                }
            }
            if (!done) throw new Error('the answer was cut off');
            if (!done.ok) {
                text.textContent = `No answer from the model: ${done.error || 'unknown error'}. Check the AI Model Server in Warden Control.`;
                reply.classList.add('error');
                history.pop();
            } else {
                text.innerHTML = formatted(done.text);
                history.push({ role: 'assistant', content: done.text });
                (done.actions || []).forEach(a => actionCard(reply, a));
                const meta = document.createElement('div');
                meta.className = 'chat-meta';
                meta.textContent = `${done.model || 'model'} · ${(done.latency_ms / 1000).toFixed(1)} s`;
                reply.appendChild(meta);
            }
        } catch (e) {
            text.textContent = `Chat failed: ${e.message}`;
            reply.classList.add('error');
            history.pop();
        } finally {
            busy = false;
            $('chat-send').disabled = false;
            $('chat-status').textContent = 'Answer ready';
            $('chat-log').scrollTop = $('chat-log').scrollHeight;
        }
    }

    document.addEventListener('DOMContentLoaded', () => {
        if (!$('chat-panel')) return;
        $('chat-toggle').addEventListener('click', () => open());
        $('chat-close').addEventListener('click', () => open(false));
        $('chat-panel').addEventListener('keydown', (e) => { if (e.key === 'Escape') open(false); });
        $('chat-form').addEventListener('submit', (e) => {
            e.preventDefault();
            const q = $('chat-input').value.trim();
            if (!q || busy) return;
            $('chat-input').value = '';
            ask(q);
        });
        // Enter sends, Shift+Enter is a new line.
        $('chat-input').addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); $('chat-form').requestSubmit(); }
        });
        document.querySelectorAll('.chat-suggest').forEach(b => b.addEventListener('click', () => { if (!busy) ask(b.textContent); }));
    });
})();
