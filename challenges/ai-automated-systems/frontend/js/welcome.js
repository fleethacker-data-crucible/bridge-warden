// Launch page: a welcome dialog that opens once on first launch (Warden Control and the main app share the flag),
// closes with one click, Enter or Escape, and comes back from the "Welcome" button in the header.
// Plain DOM, no build step. Styled by css/welcome.css with Bridge Warden's own tokens, so every look
// (Fleet, Kit, Classic) paints it. FLEET UI REQUIREMENTS MUST BE APPLIED: see AGENTS.md before changing the look.
(function () {
    'use strict';
    var KEY = 'bw-welcome';                         // 'seen' once the operator has closed it
    var dialog = null, opener = null;

    function seen() { try { return localStorage.getItem(KEY) === 'seen'; } catch (e) { return false; } }
    function remember() { try { localStorage.setItem(KEY, 'seen'); } catch (e) { } }

    var STEPS = [
        ['Connect an AI model', 'In Warden Control, AI Model Server: press <b>Use Fairlead</b> (the Fleet model router) or point it at any OpenAI-compatible server. No model? The agents show NO MODEL and Warden falls back to doctrine.'],
        ['Start Operations', 'In Warden Control, press <b>Start Operations</b>. The ship sails in ops normal and nothing moves until you say so.'],
        ['Inject an issue', 'Use <b>Inject Issues</b> for a contact, a casualty or a compromised agent, and watch the lookouts report and the model decide.'],
        ['Decide, or stop it all', 'Approvals wait in the <b>Decisions</b> column. Hold <b>SAFE-STOP</b> for two seconds to halt every agent.']
    ];

    function build() {
        var onControl = /control\.html$/i.test(location.pathname);
        var el = document.createElement('div');
        el.className = 'welcome-scrim';
        el.innerHTML =
            '<div class="welcome-card" role="dialog" aria-modal="true" aria-labelledby="welcome-title" aria-describedby="welcome-lede">' +
            '<img class="welcome-emblem" src="img/emblem-760.png" width="380" height="380" alt="Bridge Warden emblem: a seabird in a captain\'s cap beside a ship wheel and watch lantern, inside a brass compass ring">' +
            '<h1 id="welcome-title">Every AI order passes the Warden.</h1>' +
            '<p id="welcome-lede">Bridge Warden is a governance layer between a ship\'s AI agents and the ship. Agents recommend, Warden checks every order, and a human stays in charge. Everything here is a synthetic simulation.</p>' +
            '<ol class="welcome-steps">' + STEPS.map(function (s) { return '<li><b>' + s[0] + '</b><span>' + s[1] + '</span></li>'; }).join('') + '</ol>' +
            '<div class="welcome-actions">' +
            '<button type="button" class="btn primary lg" id="welcome-go">Get started</button>' +
            (onControl ? '' : '<a class="btn lg welcome-link" id="welcome-control" href="control.html" target="bw-control">Open Warden Control</a>') +
            '</div>' +
            '<p class="welcome-foot">This page opens once. Bring it back any time with the Welcome button in the header.</p>' +
            '</div>';
        return el;
    }

    function close() {
        if (!dialog) return;
        remember();
        document.removeEventListener('keydown', onKey, true);
        dialog.remove(); dialog = null;
        if (opener && document.contains(opener)) opener.focus();
    }

    function onKey(e) {
        if (e.key === 'Escape') { e.preventDefault(); close(); return; }
        if (e.key !== 'Tab' || !dialog) return;
        var f = dialog.querySelectorAll('button, a[href]');
        if (!f.length) return;
        var first = f[0], last = f[f.length - 1];
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    }

    function open(from) {
        if (dialog) return;
        opener = from || null;
        dialog = build();
        document.body.appendChild(dialog);
        dialog.querySelector('#welcome-go').addEventListener('click', close);
        var link = dialog.querySelector('#welcome-control');
        if (link) link.addEventListener('click', function () { remember(); });
        dialog.addEventListener('mousedown', function (e) { if (e.target === dialog) close(); });
        document.addEventListener('keydown', onKey, true);
        dialog.querySelector('#welcome-go').focus();
    }

    document.addEventListener('DOMContentLoaded', function () {
        var header = document.querySelector('header.header');
        if (header) {
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'skin-toggle welcome-toggle';
            btn.textContent = 'Welcome';
            btn.title = 'Open the welcome page again';
            btn.addEventListener('click', function () { open(btn); });
            header.appendChild(btn);
        }
        if (!seen()) open(null);
    });
})();
