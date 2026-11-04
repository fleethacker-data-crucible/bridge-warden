// Bridge Warden look switch: "kit" (FleetHacker UI Kit, the org suite standard) or "classic" (the original look).
// Loaded first in <head> so the right look paints with no flash. Saved per browser as localStorage 'bw-skin'.
// The kit stylesheet is only loaded while the skin is on, so "classic" is byte-for-byte the old page.
(function () {
    'use strict';
    var KEY = 'bw-skin';
    var skin = 'kit';                                   // hackathon default: the org look
    try { var saved = localStorage.getItem(KEY); if (saved === 'kit' || saved === 'classic') skin = saved; } catch (e) { }
    if (skin === 'kit') {
        document.documentElement.setAttribute('data-skin', 'kit');
        // Synchronous so it is in place before first paint; before css/styles.css so Bridge Warden's own rules win.
        document.write('<link rel="stylesheet" href="vendor/ui-kit/kit.css">');
    }

    function label(btn) {
        btn.textContent = skin === 'kit' ? 'Look: Kit' : 'Look: Classic';
        btn.setAttribute('aria-label', 'Switch look, current: ' + (skin === 'kit' ? 'FleetHacker kit' : 'classic'));
    }

    document.addEventListener('DOMContentLoaded', function () {
        var header = document.querySelector('header.header');
        if (!header) return;
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'skin-toggle';
        btn.title = 'Switch between the FleetHacker kit look and the classic Bridge Warden look';
        label(btn);
        btn.addEventListener('click', function () {
            skin = skin === 'kit' ? 'classic' : 'kit';
            try { localStorage.setItem(KEY, skin); } catch (e) { }
            location.reload();                          // the kit stylesheet is loaded at parse time; reload applies it cleanly
        });
        header.appendChild(btn);
    });
})();
