// FLEET UI REQUIREMENTS MUST BE APPLIED. Default look is Fleet. Do not change without Anthony's yes. See AGENTS.md.
// Bridge Warden look switch: "fleet" (Fleet Unified UI, the look every Fleet app shares), "kit" (FleetHacker UI
// Kit) or "classic" (the original look). Loaded first in <head> so the right look paints with no flash. Saved per
// browser as localStorage 'bw-skin'. Each look's stylesheet is only loaded while it is on, so "classic" is
// byte-for-byte the old page.
(function () {
    'use strict';
    var KEY = 'bw-skin';
    var ORDER = ['fleet', 'kit', 'classic'];            // the header button steps through them in this order
    var NAMES = { fleet: 'Fleet', kit: 'Kit', classic: 'Classic' };
    var LONG = { fleet: 'Fleet', kit: 'FleetHacker kit', classic: 'classic' };
    var SHEETS = { fleet: 'vendor/fleet/fleet.css', kit: 'vendor/ui-kit/kit.css' };
    var skin = 'fleet';                                 // default: the shared Fleet look
    try { var saved = localStorage.getItem(KEY); if (ORDER.indexOf(saved) >= 0) skin = saved; } catch (e) { }
    if (skin !== 'classic') {
        var root = document.documentElement;
        root.setAttribute('data-skin', skin);
        if (skin === 'fleet') {
            root.setAttribute('data-app', 'bridge-warden');
            root.setAttribute('data-theme', 'dark');    // Night watch only: the COP pictures are drawn for a dark scope
        }
        // Synchronous so it is in place before first paint; before css/styles.css so Bridge Warden's own rules win.
        document.write('<link rel="stylesheet" href="' + SHEETS[skin] + '">');
    }

    function label(btn) {
        btn.textContent = 'Look: ' + NAMES[skin];
        btn.setAttribute('aria-label', 'Switch look, current: ' + LONG[skin]);
    }

    document.addEventListener('DOMContentLoaded', function () {
        var header = document.querySelector('header.header');
        if (!header) return;
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'skin-toggle';
        btn.title = 'Switch between the Fleet, FleetHacker kit and classic Bridge Warden looks';
        label(btn);
        btn.addEventListener('click', function () {
            skin = ORDER[(ORDER.indexOf(skin) + 1) % ORDER.length];
            try { localStorage.setItem(KEY, skin); } catch (e) { }
            location.reload();                          // stylesheets load at parse time; reload applies them cleanly
        });
        header.appendChild(btn);
    });
})();
