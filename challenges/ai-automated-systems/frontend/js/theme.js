// Appearance (Fleet Unified UI section 3): Night watch (dark, the default), Chart room (light) or follow the system.
// The choice is saved under bridgewarden-theme in localStorage (wrapped in try/catch; the page works without storage)
// and applies to both windows. Loaded first in <head> so the right theme paints with no flash.
// FLEET UI REQUIREMENTS MUST BE APPLIED. The default is Night watch. See AGENTS.md.
(function () {
    'use strict';
    var KEY = 'bridgewarden-theme';
    function read() { try { var v = localStorage.getItem(KEY); return v === 'light' || v === 'dark' || v === 'system' ? v : 'dark'; } catch (e) { return 'dark'; } }
    function apply(v) {
        var root = document.documentElement;
        if (v === 'system') root.removeAttribute('data-theme'); else root.setAttribute('data-theme', v);
    }
    apply(read());
    window.BWTheme = {
        get: read,
        set: function (v) { try { localStorage.setItem(KEY, v); } catch (e) { } apply(v); window.dispatchEvent(new Event('bw-theme')); },
        options: [['dark', 'Night watch', 'Dark. The Fleet default.'], ['light', 'Chart room', 'Light. For a bright space.'], ['system', 'Follow the system', 'Night watch or Chart room, as the operating system says.']]
    };
    window.addEventListener('storage', function (e) { if (e.key === KEY) apply(read()); });   // the other window changed it
})();
