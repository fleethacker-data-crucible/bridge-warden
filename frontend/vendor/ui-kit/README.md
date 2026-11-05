# FleetHacker UI Kit v0.2.2

The kit is one CSS file and one JS file. It needs no npm, no build step and no CDN, and it works offline from `file://` in Chrome and Edge. Its tokens, components and patterns come from the two most settled UIs in the org, **data-noodler** and **phoenix-lai**. The kit adds a few changes on top of them so that it meets WCAG 2.1 AA and Section 508; see "Deviations" below.

## Files
| File | Purpose |
|---|---|
| `kit.css` | Tokens for dark (default) and light themes, base styles, layout, side nav, buttons, inputs, tables, badges, notices, tabs, disclosure, menus, dialogs, toasts, progress and log. Everything is inside `@layer kit`. |
| `kit.js` | A classic script (no modules) that exposes `window.Kit`: theme, toast, dialog, confirm, alert, menu, tabs, disclosure and sortable tables. |
| `fonts/` | Exo 2 (variable font, weights 400–700) and Syne Mono, both subset to Latin-1 as woff2 (about 38 KB). Licensed under SIL OFL 1.1; the license texts are included. |
| `assets/` | `coin-64.png` (used as `img.logo`) and `favicon-32.png`, both taken from `dumping-grounds`. |
| `demo.html` | Shows every component. Open it directly in a browser. |
| `check-contrast.py` | Checks every token pair and badge colour against AA. It exits non-zero if any pair fails. |
| `VERSION` | `0.2.2`. The same version is stamped at the top of `kit.css` and `kit.js`. |

## Adopting it
1. Copy the `ui-kit/` folder into the tool, for example as `vendor/ui-kit/`, and record the version.
2. Load it before the tool's own CSS: `<link rel="stylesheet" href="vendor/ui-kit/kit.css">` and `<script src="vendor/ui-kit/kit.js" defer></script>`.
3. Because the kit is in `@layer kit`, **the tool's existing unlayered CSS always wins**. Adopting the kit therefore changes nothing until you delete the duplicated rules from the tool.
4. **data-noodler.** The class names (`.topbar .brand .logo .sub .spacer .main .box .row .stack .grid2 .split .stats .stat`, buttons, `label.field`, `.table-wrap table.grid`, `.badge.<colour>`, `dialog header/.body/footer`, `.toasts .toast`, `.dd-wrap .dd-menu`, `.progress`, `.log`), the token names (`--bg --panel --panel-2 --sel --border --text --text-2 --muted --ok --warn --err --radius --mono`, plus the aliases `--blue --blue-h --save`) and the `data-theme` attribute are the same as in `ui/css/theme.css`. When you adopt the kit, delete the `*:focus, *:focus-visible { outline: none; }` rule there, because it would hide the kit's focus ring. Replace `UI.toast/confirm/alert`, `Menus.bind` and `Theme.apply` with `Kit.toast/confirm/alert`, `Kit.menu` and `Kit.theme.apply`; the signatures match. If you want to keep the existing saved preference, set `<html data-kit-theme-key="dn-theme">`.
5. **phoenix-lai.** The aliases `--line --line-strong --soft --dim --r --panel-solid` resolve to kit tokens, and the `:root.light` class is honoured. The `.window` markup in `chat.html` maps to `dialog header/.body/footer`, and `#side .snav` maps to `.snav`. phoenix-lai's accent is gold, so keep `--accent: var(--brand); --on-accent: var(--on-brand)` in its own CSS.

## Derived from
| Kit token / pattern | Value (dark / light) | Reference source |
|---|---|---|
| `--bg --panel --panel-2 --sel --border` | `#1a1a19 #242422 #2c2c2a #44433f #3a3936` / `#fcfcfb #f4f4f2 #ecebe7 #dcdbd5 #dddcd6` | data-noodler `ui/css/theme.css` `:root` and `[data-theme=light]` |
| `--text --text-2` | `#ffffff #c3c2b7` / `#0b0b0b #52514e` | data-noodler |
| `--muted` | `#9a988f` / `#6b6962` | data-noodler `--muted` `#8d8b82` / `#85837c` (**deviation**) |
| `--border-strong` (form controls) | `#7a7872` | **new**; plays the role of phoenix-lai's `--line-strong` |
| `--accent` (button fill) | `#2f74c8` / `#2a6fc4` | data-noodler `--blue` `#3987e5` / `#2a78d6` (**deviation**) |
| `--link`, `--focus` | `#5a9cec` / `#1f66bd` | data-noodler `--blue-h` |
| `--brand`, `--on-brand` | `#e8c15a`, `#1a1310` / `#7d6308`, `#fff` | phoenix-lai `--accent` (gold) and `button.primary` text; the light value darkens it, as phoenix-lai's `--you` mix does |
| `--ok --warn` (text) | `#4cc27f #d9b343` / `#0a7a3d #7d6308` | data-noodler (light `--warn` **deviation**, was `#a8850f`) |
| `--err` (text) | `#ff9090` / `#c42b2b` | data-noodler `--lerr` (dark) / `--err` |
| `--ok-bg --warn-bg --err-bg` (fills) | `#287a4c #8a6d0c #c93b3b` / `#086832 #7d6308 #c42b2b` | data-noodler `--save`, `--warn`, `--err` (**deviation** in dark) |
| `--sans`, `--mono` | Exo 2, Syne Mono | both repos (bundled fonts) |
| `--fs` 14px, h1 20/600, h2 13px uppercase `.08em` | | data-noodler headings; phoenix-lai `server.css` body 14px |
| `--radius` 4px, `--ctl-h` 25px, gaps 5/10/14/20px | | data-noodler (buttons are 25px with 4px corners, 5px apart) |
| Buttons: default blue, `.ghost`, `.danger`, `.go` (save), `.small`, hover `brightness(1.15)` / `(.9)` in light | | data-noodler; `.primary` = phoenix-lai gold |
| `.badge` `--bc` colour model | | data-noodler (the mix is **40%**, not 50%, and white is 25%; light text is 55%, not 65%) |
| `dialog header/.body/footer`, backdrop `#0008`, `dialog.blur` | | data-noodler dialog; phoenix-lai `.window` and its blur backdrop |
| `.toasts`/`.toast` top-centre drop, 5 s or ×, ok/warn/err | | data-noodler `UI.toast` |
| `.dd-wrap/.dd-menu`, `.off`, `.danger` | | data-noodler `menus.js` (the kit adds APG menu-button keyboard support and ARIA) |
| `.snav`, `.snav-group`, tabs with the `.on`-style tint | | phoenix-lai `#side .snav`, settings segmented buttons |
| `.notice` | | phoenix-lai `.approve` bar |
| Theme via `data-theme` and the `.light` class, saved in localStorage | | data-noodler `theme.js`, phoenix-lai `settings.light` |
| `[data-reduced-motion=true]` | | phoenix-lai / synapse convention |

### Deviations (AA only)
| What | Reference | Kit | Why |
|---|---|---|---|
| Focus ring | data-noodler removes all outlines | 2px `--focus` ring on `:focus-visible` | WCAG 2.4.7 |
| Dark `--muted` | `#8d8b82` (4.10:1 on panel-2) | `#9a988f` | 1.4.3 |
| Light `--muted` | `#85837c` (3.70:1) | `#6b6962` | 1.4.3 |
| Button fill `--accent` | `#3987e5` / `#2a78d6` with white text (3.64 / 4.42:1) | `#2f74c8` / `#2a6fc4` | 1.4.3 |
| Light `--warn` text | `#a8850f` (3.39:1) | `#7d6308` | 1.4.3 |
| Dark `--err` text | `#e24c4c` (4.46:1) | `#ff9090` (data-noodler's `--lerr`) | 1.4.3 |
| Toast and button fills (ok/warn/err) | `#4cc27f`, `#d9822b`, `#e24c4c` with white text (2.3–3.9:1) | `#287a4c`, `#8a6d0c`, `#c93b3b` | 1.4.3 |
| Control borders | `#3a3936` (1.5:1) | `--border-strong` `#7a7872` (≥3:1) | 1.4.11 |
| Badge background mix | 50% (yellow 4.03:1, white 2.66:1) | 40%; white 25%; light text 55% | 1.4.3 |
| Destructive confirm | focus on OK | focus on Cancel | Safer default |

## Accessibility rules
- Set `lang`, give each page a unique `<title>`, include a `.skip` link to `#main`, and use landmarks (`header`, `nav[aria-label]`, `main`).
- Every field needs a label (`label.field`). Use `aria-describedby` for hints and errors and `aria-invalid` for invalid fields. Never rely on colour alone.
- Use `<button>` for actions and `<a href>` for navigation. Keep `:focus-visible`.
- Tables need a `<caption>` and `th[scope]`. A sortable column uses `th[aria-sort] > button`, and a scroll wrapper is `role=region tabindex=0` with a label.
- Dialogs use native `<dialog aria-labelledby>`. Esc closes the dialog and focus returns to its opener.
- Tabs and menus follow the WAI-ARIA APG patterns: arrow keys, Home/End and Esc. Toasts are in a polite live region, and errors use `role=alert`.
- After any token change, run `python3 check-contrast.py`, then run axe-core in headless Chrome from a temporary folder outside the repo.

## Changelog
- **0.2.2**: forced-colors fixes. The active sort header button is underlined (`all:unset` was hiding the underline on the `th`). Badges inside selected or open rows use a transparent background with HighlightText text and border.
- **0.2.1**: forced-colors (Windows High Contrast) fixes. The progress track gets a CanvasText border and the fill is drawn in Highlight. Menu items show a Highlight outline on focus and an outline on hover, and disabled items use GrayText. Selected and open table rows, and `aria-pressed` buttons, use Highlight/HighlightText. The active sort column is underlined. Disabled buttons use GrayText. Warn and err toasts get a thicker border. The current side-nav item is underlined. The selected tab is forced to CanvasText on Canvas (previously its text vanished). Focus rings use 2px Highlight with an offset.
- **0.2.0**: rebased on data-noodler and phoenix-lai.
