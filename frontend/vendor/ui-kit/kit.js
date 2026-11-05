/*! FleetHacker UI Kit v0.2.2 | vanilla, no deps, classic script (works from file://). Global: Kit.
    API mirrors data-noodler's UI/Menus/Theme helpers so adoption is a rename:
      Kit.toast(msg, kind='', ms=5000)            kind: '' info | 'ok' | 'warn' | 'err' ('error' accepted)
      await Kit.confirm(msg, {danger, ok, cancel}) -> true/false
      await Kit.dialog(title, body, buttons)       body: string or Node; buttons [{label, value, kind}] -> value (Esc -> null)
      Kit.menu(button, menu, onPick(act, ev))      .dd-wrap > button + .dd-menu > [data-act]
      Kit.theme.apply('light'|'dark') / .toggle() / .current   (sets data-theme and the .light class, phoenix-lai style)
    Declarative: [data-kit-theme-toggle], [data-kit-dialog-open=id], [data-kit-dialog-close], [data-kit-toast][data-kit-toast-kind],
      [role=tablist], button.disclosure[aria-controls], table.grid th[aria-sort] > button (sortable). */
(function () {
  'use strict';
  var Kit = window.Kit = { version: '0.2.2' };
  var $$ = function (s, r) { return Array.prototype.slice.call((r || document).querySelectorAll(s)); };
  var root = document.documentElement;

  /* ---- theme ---- */
  var KEY = root.getAttribute('data-kit-theme-key') || 'kit-theme';
  function store(t) { try { localStorage.setItem(KEY, t); } catch (e) {} }
  function read() { try { return localStorage.getItem(KEY); } catch (e) { return null; } }
  Kit.theme = {
    apply: function (t) {
      t = t === 'light' ? 'light' : 'dark';
      root.setAttribute('data-theme', t); root.classList.toggle('light', t === 'light'); store(t);
      $$('[data-kit-theme-toggle]').forEach(function (b) {
        b.setAttribute('aria-pressed', String(t === 'light'));
        var l = b.querySelector('.label') || b; l.textContent = 'Light mode: ' + (t === 'light' ? 'on' : 'off');
      });
      return t;
    },
    toggle: function () { return Kit.theme.apply(Kit.theme.current === 'light' ? 'dark' : 'light'); },
    get current() { return root.getAttribute('data-theme') || (root.classList.contains('light') ? 'light' : 'dark'); }
  };
  var saved = read(); if (saved) { root.setAttribute('data-theme', saved); root.classList.toggle('light', saved === 'light'); }

  /* ---- toasts: polite live region; errors use role=alert ---- */
  var box;
  function region() {
    if (box && box.isConnected) return box;
    box = document.createElement('div'); box.className = 'toasts';
    box.setAttribute('role', 'status'); box.setAttribute('aria-live', 'polite');
    document.body.appendChild(box); return box;
  }
  Kit.toast = function (msg, kind, ms) {
    kind = kind === 'error' ? 'err' : (kind || ''); ms = ms == null ? 5000 : ms;
    var t = document.createElement('div'); t.className = 'toast' + (kind ? ' ' + kind : '');
    if (kind === 'err') t.setAttribute('role', 'alert');
    var s = document.createElement('span'); s.textContent = String(msg); t.appendChild(s);
    var x = document.createElement('button'); x.type = 'button'; x.className = 'x'; x.setAttribute('aria-label', 'Dismiss notification'); x.textContent = '\u00d7';
    x.addEventListener('click', function () { t.remove(); }); t.appendChild(x);
    region().prepend(t);
    if (ms) { var h = setTimeout(function () { t.remove(); }, ms);
      ['mouseenter', 'focusin'].forEach(function (ev) { t.addEventListener(ev, function () { clearTimeout(h); }); }); }
    return t;
  };

  /* ---- dialogs: native <dialog>, focus moves in and returns to the opener ---- */
  var uid = 0;
  Kit.dialog = function (title, body, buttons) {
    return new Promise(function (resolve) {
      var opener = document.activeElement, d = document.createElement('dialog'), id = 'kit-dlg-' + (++uid);
      d.setAttribute('aria-labelledby', id + '-t');
      var hd = document.createElement('header'), h = document.createElement('h2'); h.id = id + '-t'; h.textContent = title; hd.appendChild(h);
      var bd = document.createElement('div'); bd.className = 'body'; bd.id = id + '-b';
      if (body instanceof Node) bd.appendChild(body); else { var p = document.createElement('div'); p.textContent = body == null ? '' : String(body); bd.appendChild(p); }
      d.setAttribute('aria-describedby', bd.id);
      var ft = document.createElement('footer'), result = null;
      (buttons || [{ label: 'OK', value: true }]).forEach(function (b) {
        var el = document.createElement('button'); el.type = 'button'; el.textContent = b.label; if (b.kind) el.className = b.kind;
        el.addEventListener('click', function () { result = b.value; d.close(); }); ft.appendChild(el);
      });
      d.append(hd, bd, ft); document.body.appendChild(d);
      d.addEventListener('close', function () { d.remove(); if (opener && opener.focus) opener.focus(); resolve(result); });
      d.showModal();
      var safe = ft.querySelector('.danger') ? ft.firstElementChild : ft.lastElementChild; if (safe) safe.focus();   // destructive dialogs focus Cancel
    });
  };
  Kit.confirm = function (msg, o) {
    o = o || {};
    return Kit.dialog(o.title || 'Confirm', msg, [{ label: o.cancel || 'Cancel', value: false, kind: 'ghost' }, { label: o.ok || 'OK', value: true, kind: o.danger ? 'danger' : '' }])
      .then(function (v) { return v === true; });
  };
  Kit.alert = function (msg, title) { return Kit.dialog(title || 'Notice', msg, [{ label: 'OK', value: true }]); };
  function initDialogs() {
    $$('[data-kit-dialog-open]').forEach(function (b) {
      b.setAttribute('aria-haspopup', 'dialog');
      b.addEventListener('click', function () { var d = document.getElementById(b.getAttribute('data-kit-dialog-open')); if (d) { d._opener = b; d.showModal(); } });
    });
    $$('dialog').forEach(function (d) {
      d.addEventListener('close', function () { if (d._opener) d._opener.focus(); });
      $$('[data-kit-dialog-close]', d).forEach(function (c) { c.addEventListener('click', function () { d.close(c.value || ''); }); });
    });
  }

  /* ---- menus: APG menu button ---- */
  Kit.menu = function (btn, menu, onPick) {
    if (!menu.id) menu.id = 'kit-menu-' + (++uid);
    btn.setAttribute('aria-haspopup', 'menu'); btn.setAttribute('aria-expanded', 'false'); btn.setAttribute('aria-controls', menu.id);
    menu.setAttribute('role', 'menu'); if (!menu.hasAttribute('aria-label')) menu.setAttribute('aria-label', btn.textContent.trim());
    var items = function () { return $$('[data-act]', menu); };
    items().forEach(function (i) { i.setAttribute('role', 'menuitem'); i.tabIndex = -1; if (i.classList.contains('off')) i.setAttribute('aria-disabled', 'true'); });
    function open(focusLast) { menu.classList.add('open'); btn.setAttribute('aria-expanded', 'true'); var it = items(); if (it.length) it[focusLast ? it.length - 1 : 0].focus(); }
    function close(refocus) { menu.classList.remove('open'); btn.setAttribute('aria-expanded', 'false'); if (refocus) btn.focus(); }
    function pick(i, ev) { if (i.getAttribute('aria-disabled') === 'true') return; close(true); if (onPick) onPick(i.getAttribute('data-act'), ev); }
    btn.addEventListener('click', function () { menu.classList.contains('open') ? close(false) : open(false); });
    btn.addEventListener('keydown', function (e) { if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); open(e.key === 'ArrowUp'); } });
    menu.addEventListener('click', function (e) { var i = e.target.closest('[data-act]'); if (i) pick(i, e); });
    menu.addEventListener('keydown', function (e) {
      var it = items(), i = it.indexOf(document.activeElement), n = null;
      if (e.key === 'ArrowDown') n = it[(i + 1) % it.length]; else if (e.key === 'ArrowUp') n = it[(i - 1 + it.length) % it.length];
      else if (e.key === 'Home') n = it[0]; else if (e.key === 'End') n = it[it.length - 1];
      else if (e.key === 'Escape') { e.preventDefault(); close(true); return; }
      else if (e.key === 'Tab') { close(false); return; }
      else if ((e.key === 'Enter' || e.key === ' ') && i >= 0) { e.preventDefault(); pick(it[i], e); return; }
      if (n) { e.preventDefault(); n.focus(); }
    });
    document.addEventListener('click', function (e) { if (!menu.contains(e.target) && e.target !== btn && !btn.contains(e.target)) close(false); });
    return { open: open, close: close };
  };

  /* ---- tabs: APG, automatic activation, roving tabindex ---- */
  function initTabs() {
    $$('[role=tablist]').forEach(function (list) {
      var tabs = $$('[role=tab]', list);
      function sel(t, focus) {
        tabs.forEach(function (x) { var on = x === t; x.setAttribute('aria-selected', String(on)); x.tabIndex = on ? 0 : -1;
          var p = document.getElementById(x.getAttribute('aria-controls')); if (p) p.hidden = !on; });
        if (focus) t.focus();
      }
      tabs.forEach(function (t, i) {
        t.addEventListener('click', function () { sel(t); });
        t.addEventListener('keydown', function (e) {
          var k = { ArrowRight: i + 1, ArrowLeft: i - 1, Home: 0, End: tabs.length - 1 }[e.key];
          if (k === undefined) return; e.preventDefault(); sel(tabs[(k + tabs.length) % tabs.length], true);
        });
      });
      sel(tabs.filter(function (t) { return t.getAttribute('aria-selected') === 'true'; })[0] || tabs[0]);
    });
  }
  /* ---- disclosure ---- */
  function initDisclosure() {
    $$('button.disclosure[aria-controls]').forEach(function (b) {
      var p = document.getElementById(b.getAttribute('aria-controls')); if (!p) return;
      p.hidden = b.getAttribute('aria-expanded') !== 'true';
      b.addEventListener('click', function () { var o = b.getAttribute('aria-expanded') === 'true'; b.setAttribute('aria-expanded', String(!o)); p.hidden = o; });
    });
  }
  /* ---- sortable tables: th[aria-sort] > button ---- */
  function initSort() {
    $$('table.grid').forEach(function (tbl) {
      var ths = $$('th[aria-sort]', tbl);
      ths.forEach(function (th) {
        var b = th.querySelector('button'); if (!b) return;
        b.addEventListener('click', function () {
          var col = Array.prototype.indexOf.call(th.parentNode.children, th), dir = th.getAttribute('aria-sort') === 'ascending' ? 'descending' : 'ascending';
          ths.forEach(function (o) { o.setAttribute('aria-sort', 'none'); }); th.setAttribute('aria-sort', dir);
          var body = tbl.tBodies[0], rows = Array.prototype.slice.call(body.rows), num = th.classList.contains('num');
          rows.sort(function (a, c) { var x = a.cells[col].textContent.trim(), y = c.cells[col].textContent.trim();
            var r = num ? parseFloat(x) - parseFloat(y) : x.localeCompare(y, undefined, { numeric: true }); return dir === 'ascending' ? r : -r; });
          rows.forEach(function (r) { body.appendChild(r); });
        });
      });
    });
  }

  Kit.init = function () {
    $$('[data-kit-theme-toggle]').forEach(function (b) { b.addEventListener('click', function () { Kit.theme.toggle(); }); });
    Kit.theme.apply(Kit.theme.current);
    initDialogs(); initTabs(); initDisclosure(); initSort();
    $$('.dd-wrap').forEach(function (w) { var b = w.querySelector('button'), m = w.querySelector('.dd-menu'); if (b && m && !b.hasAttribute('aria-haspopup')) Kit.menu(b, m, function (act) { w.dispatchEvent(new CustomEvent('kit-pick', { detail: act, bubbles: true })); }); });
    $$('[data-kit-toast]').forEach(function (b) { b.addEventListener('click', function () { Kit.toast(b.getAttribute('data-kit-toast'), b.getAttribute('data-kit-toast-kind') || ''); }); });
  };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', Kit.init); else Kit.init();
})();
